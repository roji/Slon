using System;
using System.Collections;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql.Pipelines.Protocol;
using Npgsql.Pipelines.Protocol.PgV3;

namespace Npgsql.Pipelines;

enum ReaderState
{
    Uninitialized = 0,
    Active,
    Completed,
    Exhausted,
    Closed,
}

// Implementation
public sealed partial class NpgsqlDataReader
{
    static ObjectPool<NpgsqlDataReader>? _sharedPool;
    static ObjectPool<NpgsqlDataReader> SharedPool =>
        _sharedPool ??= new(pool =>
        {
            var returnAction = pool.Return;
            return () => new NpgsqlDataReader(returnAction);
        });

    readonly Action<NpgsqlDataReader>? _returnAction;

    // Will be set during initialization.
    CommandReader _commandReader = null!;
    CommandContextBatch.Enumerator _commandEnumerator;

    ReaderState _state;
    ulong? _recordsAffected;

    CommandContext GetCurrent() => _commandEnumerator.Current;
    Protocol.Protocol GetProtocol() => GetCurrent().GetOperation().Result.Protocol;

    internal static async ValueTask<NpgsqlDataReader> Create(bool async, ValueTask<CommandContextBatch> batch)
    {
        // This is an inlined version of NextResultAsyncCore to save on async overhead.
        // Either commandReader is null and initException is not null or its always a valid reference.
        CommandReader commandReader = null!;
        ExceptionDispatchInfo? initException = null;
        CommandContextBatch.Enumerator enumerator = default;
        try
        {
            enumerator = (await batch.ConfigureAwait(false)).GetEnumerator();
            enumerator.MoveNext();
            var op = await enumerator.Current.GetOperation().ConfigureAwait(false);
            commandReader = op.Protocol.GetCommandReader();
            // Immediately initialize the first command, we're supposed to be positioned there at the start.
            await commandReader.InitializeAsync(enumerator.Current).ConfigureAwait(false);
        }
        catch(Exception ex)
        {
            initException = ExceptionDispatchInfo.Capture(ex);
        }

        if (initException is not null)
            return await HandleUncommon(commandReader, initException, enumerator);

        return SharedPool.Rent().Initialize(commandReader, enumerator);

        static async ValueTask<NpgsqlDataReader> HandleUncommon(CommandReader commandReader, ExceptionDispatchInfo initException, CommandContextBatch.Enumerator enumerator)
        {
            try
            {
                await ConsumeBatch(enumerator, commandReader).ConfigureAwait(false);
            }
            catch
            {
                // We swallow any remaining exceptions (maybe we want to aggregate though).
            }
            // Does not return.
            initException.Throw();
            return null!;
        }
    }

    static ValueTask ConsumeBatch(CommandContextBatch.Enumerator enumerator, CommandReader? activeReader = null)
    {
        enumerator.Current.GetOperation().Result.Dispose();
        return default;
    }

    NpgsqlDataReader Initialize(CommandReader reader, CommandContextBatch.Enumerator enumerator)
    {
        _state = ReaderState.Active;
        _commandReader = reader;
        _commandEnumerator = enumerator;
        SyncStates();
        return this;
    }

    void SyncStates()
    {
        DebugShim.Assert(_commandReader is not null);
        switch (_commandReader.State)
        {
            case CommandReaderState.Initialized:
                _state = ReaderState.Active;
                break;
            case CommandReaderState.Completed:
                HandleCompleted();
                break;
            case CommandReaderState.UnrecoverablyCompleted:
                if (_state is not ReaderState.Uninitialized or ReaderState.Closed)
                    _state = ReaderState.Closed;
                break;
        }

        void HandleCompleted()
        {
            // Store this before we move on.
            if (_state is ReaderState.Active)
            {
                _state = ReaderState.Completed;
                if (!_recordsAffected.HasValue)
                    _recordsAffected = 0;
                _recordsAffected += _commandReader.RowsAffected;
            }
        }
    }

    // If this changes make sure to expand any inlined checks in Read/ReadAsync etc.
    [MemberNotNull(nameof(_commandReader))]
    Exception? ThrowIfClosedOrDisposed(ReaderState? readerState = null, bool returnException = false)
    {
        DebugShim.Assert(_commandReader is not null);
        var exception = (readerState ?? _state) switch
        {
            ReaderState.Uninitialized => new ObjectDisposedException(nameof(NpgsqlDataReader)),
            ReaderState.Closed => new InvalidOperationException("Reader is closed."),
            _ => null
        };

        if (exception is null)
            return null;

        return returnException ? exception : throw exception;
    }

    // Any changes to this method should be reflected in Create.
    async Task<bool> NextResultAsyncCore(CancellationToken cancellationToken = default)
    {
        if (_state is ReaderState.Exhausted || !_commandEnumerator.MoveNext())
        {
            _state = ReaderState.Exhausted;
            return false;
        }

        try
        {
            await _commandReader.InitializeAsync(_commandEnumerator.Current, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            SyncStates();
        }
    }

    async ValueTask CloseCore(bool async, ReaderState? readerState = null)
    {
        // TODO consume.
    }

#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    async ValueTask DisposeCore(bool async)
    {
        // var readerState = InterlockedShim.Exchange(ref _state, ReaderState.Uninitialized);
        if (_state is ReaderState.Uninitialized)
            return;
        _state = ReaderState.Uninitialized;

        ExceptionDispatchInfo? edi = null;
        var commandContext = GetCurrent();
        try
        {
            await CloseCore(async, _state).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            edi = ExceptionDispatchInfo.Capture(e);
        }
        finally
        {
            _commandReader.Reset();
            _commandEnumerator.Dispose();
            _commandEnumerator = default;
            _commandReader = null!;
            _returnAction?.Invoke(this);
        }
        // Do last, sometimes this is followed by a sync continuation of the next op.
        commandContext.GetOperation().Result.Complete(edi?.SourceException);
        edi?.Throw();
    }
}

// Public surface & ADO.NET
public sealed partial class NpgsqlDataReader: DbDataReader
{
    internal NpgsqlDataReader(Action<NpgsqlDataReader>? returnAction = null) => _returnAction = returnAction;

    public override int Depth => 0;
    public override int FieldCount
    {
        get
        {
            ThrowIfClosedOrDisposed();
            return _commandReader.FieldCount;
        }
    }
    public override object this[int ordinal] => throw new NotImplementedException();
    public override object this[string name] => throw new NotImplementedException();

    public override int RecordsAffected
    {
        get
        {
            ThrowIfClosedOrDisposed();
            return !_recordsAffected.HasValue
                ? -1
                : _recordsAffected > int.MaxValue
                    ? throw new OverflowException(
                        $"The number of records affected exceeds int.MaxValue. Use {nameof(Rows)}.")
                    : (int)_recordsAffected;
        }
    }

    public ulong Rows
    {
        get
        {
            ThrowIfClosedOrDisposed();
            return _recordsAffected ?? 0;
        }
    }

    public override bool HasRows
    {
        get
        {
            ThrowIfClosedOrDisposed();
            return _commandReader.HasRows;
        }
    }
    public override bool IsClosed => _state is ReaderState.Closed or ReaderState.Uninitialized;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (_state is ReaderState.Closed or ReaderState.Uninitialized && ThrowIfClosedOrDisposed(returnException: true) is { } ex)
            Task.FromException(ex);
        return _commandReader.ReadAsync(cancellationToken);
    }

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        if (_state is ReaderState.Closed or ReaderState.Uninitialized && ThrowIfClosedOrDisposed(returnException: true) is { } ex)
            Task.FromException(ex);
        return NextResultAsyncCore(cancellationToken);
    }

    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public override bool GetBoolean(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override byte GetByte(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        throw new NotImplementedException();
    }

    public override char GetChar(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        throw new NotImplementedException();
    }

    public override string GetDataTypeName(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override DateTime GetDateTime(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override decimal GetDecimal(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override double GetDouble(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override Type GetFieldType(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override float GetFloat(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override Guid GetGuid(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override short GetInt16(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override int GetInt32(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override long GetInt64(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override string GetName(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override int GetOrdinal(string name)
    {
        throw new NotImplementedException();
    }

    public override string GetString(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override object GetValue(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override int GetValues(object[] values)
    {
        throw new NotImplementedException();
    }

    public override bool IsDBNull(int ordinal)
    {
        throw new NotImplementedException();
    }

    public override bool NextResult()
    {
        throw new NotImplementedException();
    }

    public override bool Read()
    {
        throw new NotImplementedException();
    }

    public override IEnumerator GetEnumerator()
    {
        throw new NotImplementedException();
    }

    public override void Close() => CloseCore(false).GetAwaiter().GetResult();
    protected override void Dispose(bool disposing) => DisposeCore(false).GetAwaiter().GetResult();

#if NETSTANDARD2_0
    public Task CloseAsync()
#else
    public override Task CloseAsync()
#endif
        => CloseCore(true).AsTask();

#if NETSTANDARD2_0
    public ValueTask DisposeAsync()
#else
    public override ValueTask DisposeAsync()
#endif
        => DisposeCore(true);
}
