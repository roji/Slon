using System;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Npgsql.Pipelines.Protocol;
using Npgsql.Pipelines.Protocol.PgV3;

namespace Npgsql.Pipelines;

// Implementation
public sealed partial class NpgsqlConnection
{
    NpgsqlDataSource? _dataSource;
    OperationSlot _operationSlot = null!;
    ConnectionState _state;
    Exception? _breakException;
    bool _disposed;
    string? _connectionString;

    // Slots are thread safe up to the granularity of the slot, anything more is the responsibility of the caller.
    volatile SemaphoreSlim? _pipeliningWriteLock;
    volatile ConnectionOperationSource? _pipelineTail;
    ConnectionOperationSource? _operationSingleton;
    TaskCompletionSource<bool>? _closingTcs;

    NpgsqlDataSource DbDataSource
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (_dataSource is not null)
                return _dataSource;

            return LookupDataSource();

            NpgsqlDataSource LookupDataSource()
            {
                throw new NotImplementedException();
            }
        }
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(NpgsqlConnection));
    }

    OperationSlot GetSlotUnsynchronized()
    {
        ThrowIfDisposed();
        if (_state is ConnectionState.Broken)
            throw new InvalidOperationException("Connection is in a broken state.", _breakException);

        if (_state is not (ConnectionState.Open or ConnectionState.Executing or ConnectionState.Fetching))
            throw new InvalidOperationException("Connection is not open or ready.");

        return _operationSlot;
    }

    void MoveToConnecting()
    {
        ThrowIfDisposed();
        if (_state is not (ConnectionState.Closed or ConnectionState.Broken))
            throw new InvalidOperationException("Connection is already open or being opened.");

        _state = ConnectionState.Connecting;
    }

    void MoveToExecuting()
    {
        Debug.Assert(_state is not (ConnectionState.Closed or ConnectionState.Broken), "Already Closed or Broken.");
        Debug.Assert(_state is ConnectionState.Open or ConnectionState.Executing or ConnectionState.Fetching, "Called on an unopened/not fetching/not executing connection.");
        // We allow pipelining so we can be fetching and executing, leave fetching in place.
        if (_state != ConnectionState.Fetching)
            _state = ConnectionState.Executing;
    }

    void MoveToFetching()
    {
        Debug.Assert(_state is not (ConnectionState.Closed or ConnectionState.Broken), "Already Closed or Broken.");
        Debug.Assert(_state is ConnectionState.Open or ConnectionState.Executing or ConnectionState.Fetching, "Called on an unopened/not fetching/not executing connection.");
        _state = ConnectionState.Fetching;
    }

    void EndSlot(OperationSlot slot)
    {
        Debug.Assert(slot.Task.IsCompletedSuccessfully);
        slot.Task.Result.Complete(_breakException);
    }

    void MoveToBroken(Exception? exception = null, ConnectionOperationSource? pendingHead = null)
    {
        OperationSlot slot;
        lock (SyncObj)
        {
            slot = _operationSlot;
            // We'll just keep the first exception.
            if (_state is ConnectionState.Broken)
                return;

            _state = ConnectionState.Broken;
            _breakException = exception;
        }

        var next = pendingHead;
        while (next is not null)
        {
            next.TryComplete(exception);
            next = next.Next;
        }

        EndSlot(slot);
    }

    async ValueTask CloseCore(bool async)
    {
        // The only time SyncObj (_operationSlot) is null, before the first successful open.
        if (ReferenceEquals(SyncObj, null))
            return;

        OperationSlot slot;
        Task drainingTask;
        lock (SyncObj)
        {
            slot = _operationSlot;
            // Only throw if we're already closed
            if (_state is ConnectionState.Closed)
            {
                ThrowIfDisposed();
                return;
            }

            if (_pipelineTail is null || _pipelineTail.IsCompleted)
                drainingTask = Task.CompletedTask;
            else
            {
                _closingTcs = new TaskCompletionSource<bool>();
                drainingTask = _closingTcs.Task;
            }

            _state = ConnectionState.Closed;
        }

        // TODO, if somebody pipelines without reading and then Closes we'll wait forever for the commands to finish as their readers won't get disposed.
        // Probably want a timeout and then force complete them like in broken.
        if (async)
            await drainingTask;
        else
            // TODO we may want a latch to prevent sync and async capabilities (pipelining) mixing like this.
            // Best we can do, this will only happen if somebody closes synchronously while having executed commands asynchronously.
            drainingTask.Wait();
        EndSlot(slot);
        Reset();
    }

    void MoveToIdle()
    {
        lock (SyncObj)
        {
            // No debug assert as completion can often happen in finally blocks, just check here.
            if (_state is not (ConnectionState.Closed or ConnectionState.Broken))
                _state = ConnectionState.Open;

            _closingTcs?.SetResult(true);
        }
    }

    void Reset()
    {
        _operationSingleton = null;
        _pipelineTail = null;
        _state = default;
        _breakException = null;
    }

    object SyncObj => _operationSlot;

    internal void PerformUserCancellation()
    {
        var start = TickCount64Shim.Get();
        PgProtocol? protocol;
        SemaphoreSlim? writeLock;
        lock (SyncObj)
        {
            var connectionSlot = GetSlotUnsynchronized();
            protocol = connectionSlot.Protocol;
            if (protocol is null)
                return;
            if (_pipeliningWriteLock is null)
                writeLock = _pipeliningWriteLock = new SemaphoreSlim(0); // init with empty count.
            else if (_pipeliningWriteLock.Wait(DbDataSource.DefaultCancellationTimeout))
                writeLock = _pipeliningWriteLock;
            else
                writeLock = null;
        }

        // We timed out before getting the lock, tear down the connection (highly undesirable).
        if (writeLock is null)
            // TODO or something like this.
            Task.Run(() => protocol.CompleteAsync(new Exception("Connection was prematurely completed as a result of a user cancellation fallback tearing down the socket.")));

        try
        {
            var elapsed = TimeSpan.FromMilliseconds(TickCount64Shim.Get() - start);
            DbDataSource.PerformUserCancellation(protocol, DbDataSource.DefaultCancellationTimeout - elapsed);
        }
        finally
        {
            _pipeliningWriteLock.Release();
        }
    }

    internal readonly struct CommandWriter
    {
        readonly NpgsqlConnection _instance;

        public CommandWriter(NpgsqlConnection instance)
        {
            _instance = instance;
        }

        public Command WriteCommand(bool allowPipelining, NpgsqlCommand command, CommandBehavior behavior, CancellationToken cancellationToken = default)
        {
            OperationSlot slot;
            ConnectionOperationSource subSlot;
            lock (_instance.SyncObj)
            {
                slot = _instance.GetSlotUnsynchronized();
                // Assumption is that we can already begin reading, otherwise we'd need to tie the tasks together.
                Debug.Assert(slot.Task.IsCompletedSuccessfully);
                // First enqueue, only then call WriteCommandCore.
                subSlot = EnqueueReadUnsynchronized(slot, allowPipelining);
            }

            return Command.Create(command, new IOCompletionPair(WriteCommandCore(slot, command, behavior, cancellationToken), subSlot.Task));
        }

        async ValueTask<WriteResult> WriteCommandCore(OperationSlot connectionSlot, NpgsqlCommand commandInfo, CommandBehavior behavior, CancellationToken cancellationToken = default)
        {
            await BeginWrite(cancellationToken);
            try
            {
                _instance.MoveToExecuting();
                var command = _instance.DbDataSource.WriteCommandAsync(connectionSlot, commandInfo, behavior, cancellationToken);
                return await command.WriteTask;
            }
            finally
            {
                EndWrite();
            }
        }

        ConnectionOperationSource EnqueueReadUnsynchronized(OperationSlot connectionSlot, bool allowPipelining)
        {
            var current = _instance._pipelineTail;
            if (!allowPipelining && !(current is null || current.IsCompleted))
                ThrowCommandInProgress();

            var source = _instance._pipelineTail = _instance.CreateSlotUnsynchronized(connectionSlot);
            // An immediately active read means head == tail, move to fetching immediately.
            if (source.Task.IsCompletedSuccessfully)
                _instance.MoveToFetching();

            return source;

            void ThrowCommandInProgress() => throw new InvalidOperationException("A command is already in progress.");
        }

        Task BeginWrite(CancellationToken cancellationToken = default)
        {
            var writeLock = _instance._pipeliningWriteLock;
            if (writeLock is null)
            {
                var value = new SemaphoreSlim(1);
#pragma warning disable CS0197
                if (Interlocked.CompareExchange(ref _instance._pipeliningWriteLock, value, null) is null)
#pragma warning restore CS0197
                    writeLock = value;
            }

            if (!writeLock!.Wait(0))
                return writeLock.WaitAsync(cancellationToken);

            return Task.CompletedTask;
        }

        void EndWrite()
        {
            var writeLock = _instance._pipeliningWriteLock;
            if (writeLock?.CurrentCount == 0)
                writeLock.Release();
            else
                throw new InvalidOperationException("No write to end.");
        }
    }

    internal CommandWriter GetCommandWriter() => new(this);
    internal TimeSpan DefaultCommandTimeout => DbDataSource.DefaultCommandTimeout;

    ConnectionOperationSource CreateSlotUnsynchronized(OperationSlot connectionSlot)
    {
        ConnectionOperationSource source;
        var current = _pipelineTail;
        if (current is null || current.IsCompleted)
        {
            var singleton = _operationSingleton;
            if (singleton is not null)
            {
                singleton.Reset();
                source = singleton;
            }
            else
                source = _operationSingleton = new ConnectionOperationSource(this, connectionSlot.Protocol, pooled: true);
        }
        else
        {
            source = new ConnectionOperationSource(this, connectionSlot.Protocol);
            current.Next = source;
        }

        return source;
    }


    void CompleteOperation(ConnectionOperationSource? next, Exception? exception)
    {
        if (exception is not null)
            MoveToBroken(exception, next);
        else if (next is null)
            MoveToIdle();
        else
            next.Activate();
    }

    sealed class ConnectionOperationSource: OperationSource
    {
        readonly NpgsqlConnection _connection;
        ConnectionOperationSource? _next;

        // Pipelining on the same connection is expected to be done on the same thread.
        public ConnectionOperationSource(NpgsqlConnection connection, PgProtocol? protocol, bool pooled = false) :
            base(protocol, asyncContinuations: false, pooled)
        {
            _connection = connection;
        }

        public void Activate() => ActivateCore();

        public ConnectionOperationSource? Next
        {
            get => _next;
            set
            {
                if (Interlocked.CompareExchange(ref _next, value, null) == null)
                    return;

                throw new InvalidOperationException("Next was already set.");
            }
        }

        protected override void CompleteCore(PgProtocol protocol, Exception? exception)
            => _connection.CompleteOperation(_next, exception);

        protected override void ResetCore()
        {
            _next = null;
        }
    }

    async ValueTask DisposeCore(bool async)
    {
        if (_disposed)
            return;
        _disposed = true;
        await CloseCore(async);
    }

    NpgsqlTransaction BeginTransactionCore(IsolationLevel isolationLevel)
    {
        // TODO
        throw new NotImplementedException();
    }

    void OpenCore()
    {
        MoveToConnecting();
        try
        {
            _operationSlot = DbDataSource.Open(exclusiveUse: true, DbDataSource.DefaultConnectionTimeout);
            Debug.Assert(_operationSlot.Task.IsCompleted);
            _operationSlot.Task.GetAwaiter().GetResult();
            MoveToIdle();
        }
        catch
        {
            CloseCore(async: false).GetAwaiter().GetResult();
            throw;
        }
    }

    async Task OpenAsyncCore(CancellationToken cancellationToken)
    {
        MoveToConnecting();
        try
        {
            // First we get a slot (could be a connection open but usually this is synchronous)
            var slot = await DbDataSource.OpenAsync(exclusiveUse: true, DbDataSource.DefaultConnectionTimeout, cancellationToken).ConfigureAwait(false);
            _operationSlot = slot;
            // Then we await until the connection is fully ready for us (both tasks are covered by the same cancellationToken).
            // In non exclusive cases we already start writing our message as well but we choose not to do so here.
            // One of the reasons would be to be sure the connection is healthy once we transition to Open.
            // If we're still stuck in a pipeline we won't know for sure.
            await slot.Task;
            MoveToIdle();
        }
        catch
        {
            await CloseCore(async: true);
            throw;
        }
    }

    public ValueTask ChangeDatabaseCore(bool async, string? connectionString, bool open = false)
    {
        if (connectionString is null)
            throw new ArgumentNullException(nameof(connectionString));

        // TODO change the datasource etc.
        _connectionString = _dataSource?.ConnectionString;
        throw new NotImplementedException();
    }
}

// Public surface & ADO.NET
public sealed partial class NpgsqlConnection : DbConnection, ICloneable, IComponent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NpgsqlConnection"/> class.
    /// </summary>
    public NpgsqlConnection()
        => GC.SuppressFinalize(this);

    /// <summary>
    /// Initializes a new instance of <see cref="NpgsqlConnection"/> with the given connection string.
    /// </summary>
    /// <param name="connectionString">The connection used to open the PostgreSQL database.</param>
    public NpgsqlConnection(string? connectionString) : this()
        => ConnectionString = connectionString;

    internal NpgsqlConnection(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString ?? DbDataSource.ConnectionString;
        set => ChangeDatabaseCore(async: false, value).GetAwaiter().GetResult();
    }
    public override string Database => DbDataSource.Database;
    public override string DataSource => DbDataSource.EndPointRepresentation;
    public override int ConnectionTimeout => (int)DbDataSource.DefaultConnectionTimeout.TotalSeconds;
    public override string ServerVersion => DbDataSource.ServerVersion;
    public override ConnectionState State => _state;

    public override void Open() => OpenCore();
    public override Task OpenAsync(CancellationToken cancellationToken) => OpenAsyncCore(cancellationToken);

    public override void ChangeDatabase(string databaseName)
    {
        // Such a shitty concept to put in an api...
        throw new NotImplementedException();
    }

#if !NETSTANDARD2_0
    public override Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
#else
    public Task ChangeDatabaseAsync(string databaseName, CancellationToken cancellationToken = default)
#endif
    {
        // Such a shitty concept to put in an api...
        throw new NotImplementedException();
    }

    /// <summary>
    /// Begins a database transaction.
    /// </summary>
    /// <returns>A <see cref="NpgsqlTransaction"/> object representing the new transaction.</returns>
    /// <remarks>
    /// Nested transactions are not supported.
    /// Transactions created by this method will have the <see cref="IsolationLevel.ReadCommitted"/> isolation level.
    /// </remarks>
    public new NpgsqlTransaction BeginTransaction()
        => BeginTransaction(IsolationLevel.Unspecified);

    /// <summary>
    /// Begins a database transaction with the specified isolation level.
    /// </summary>
    /// <param name="level">The isolation level under which the transaction should run.</param>
    /// <returns>A <see cref="NpgsqlTransaction"/> object representing the new transaction.</returns>
    /// <remarks>Nested transactions are not supported.</remarks>
    public new NpgsqlTransaction BeginTransaction(IsolationLevel level)
        => BeginTransactionCore(level);

    /// <summary>
    /// Begins a database transaction.
    /// </summary>
    /// <returns>A <see cref="NpgsqlTransaction"/> object representing the new transaction.</returns>
    /// <remarks>
    /// Nested transactions are not supported.
    /// Transactions created by this method will have the <see cref="IsolationLevel.ReadCommitted"/> isolation level.
    /// </remarks>
#if !NETSTANDARD2_0
    public new ValueTask<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
#else
    public ValueTask<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
#endif
        => new(BeginTransactionCore(IsolationLevel.Unspecified));

    /// <summary>
    /// Begins a database transaction with the specified isolation level.
    /// </summary>
    /// <param name="level">The isolation level under which the transaction should run.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>A <see cref="NpgsqlTransaction"/> object representing the new transaction.</returns>
    /// <remarks>Nested transactions are not supported.</remarks>
#if !NETSTANDARD2_0
    public new ValueTask<NpgsqlTransaction> BeginTransactionAsync(IsolationLevel level, CancellationToken cancellationToken = default)
#else
    public ValueTask<NpgsqlTransaction> BeginTransactionAsync(IsolationLevel level, CancellationToken cancellationToken = default)
#endif
        => new(BeginTransactionCore(IsolationLevel.Unspecified));

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => BeginTransactionCore(isolationLevel);

    protected override DbCommand CreateDbCommand() => new NpgsqlCommand(null, this);

    public override void Close() => CloseCore(async: false).GetAwaiter().GetResult();

#if !NETSTANDARD2_0
    public override Task CloseAsync()
#else
    public Task CloseAsync()
#endif
        => CloseCore(async: true).AsTask();

#if !NETSTANDARD2_0
    public override ValueTask DisposeAsync()
#else
    public ValueTask DisposeAsync()
#endif
        => DisposeCore(async: true);

    protected override void Dispose(bool disposing)
        => DisposeCore(async: false).GetAwaiter().GetResult();

    object ICloneable.Clone()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// This event is unsupported by Npgsql. Use <see cref="DbConnection.StateChange"/> instead.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public new event EventHandler? Disposed
    {
        add => throw new NotSupportedException("The Disposed event isn't supported by Npgsql. Use DbConnection.StateChange instead.");
        remove => throw new NotSupportedException("The Disposed event isn't supported by Npgsql. Use DbConnection.StateChange instead.");
    }

    event EventHandler? IComponent.Disposed
    {
        add => Disposed += value;
        remove => Disposed -= value;
    }

    /// <summary>
    /// Returns the schema collection specified by the collection name.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <returns>The collection specified.</returns>
    public override DataTable GetSchema(string? collectionName) => GetSchema(collectionName, null);

    /// <summary>
    /// Returns the schema collection specified by the collection name filtered by the restrictions.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="restrictions">
    /// The restriction values to filter the results.  A description of the restrictions is contained
    /// in the Restrictions collection.
    /// </param>
    /// <returns>The collection specified.</returns>
    public override DataTable GetSchema(string? collectionName, string?[]? restrictions)
        => throw new NotImplementedException();

    /// <summary>
    /// Asynchronously returns the supported collections.
    /// </summary>
    /// <param name="cancellationToken">
    /// An optional token to cancel the asynchronous operation. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>The collection specified.</returns>
#if !NETSTANDARD2_0
    public override Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default)
#else
    public Task<DataTable> GetSchemaAsync(CancellationToken cancellationToken = default)
#endif
        => GetSchemaAsync("MetaDataCollections", null, cancellationToken);

    /// <summary>
    /// Asynchronously returns the schema collection specified by the collection name.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="cancellationToken">
    /// An optional token to cancel the asynchronous operation. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>The collection specified.</returns>
#if !NETSTANDARD2_0
    public override Task<DataTable> GetSchemaAsync(string collectionName, CancellationToken cancellationToken = default)
#else
    public Task<DataTable> GetSchemaAsync(string collectionName, CancellationToken cancellationToken = default)
#endif
        => GetSchemaAsync(collectionName, null, cancellationToken);

    /// <summary>
    /// Asynchronously returns the schema collection specified by the collection name filtered by the restrictions.
    /// </summary>
    /// <param name="collectionName">The collection name.</param>
    /// <param name="restrictions">
    /// The restriction values to filter the results.  A description of the restrictions is contained
    /// in the Restrictions collection.
    /// </param>
    /// <param name="cancellationToken">
    /// An optional token to cancel the asynchronous operation. The default value is <see cref="CancellationToken.None"/>.
    /// </param>
    /// <returns>The collection specified.</returns>
#if !NETSTANDARD2_0
    public override Task<DataTable> GetSchemaAsync(string collectionName, string?[]? restrictions, CancellationToken cancellationToken = default)
#else
    public Task<DataTable> GetSchemaAsync(string collectionName, string?[]? restrictions, CancellationToken cancellationToken = default)
#endif
        => throw new NotImplementedException();

    /// <summary>
    /// DB provider factory.
    /// </summary>
    protected override DbProviderFactory DbProviderFactory => throw new NotImplementedException();
}
