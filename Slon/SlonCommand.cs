using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Slon.Pg;
using Slon.Protocol;
using Slon.Protocol.Pg;
using Slon.Shared;

namespace Slon;

static class CommandBehaviorExtensions
{
    public static ExecutionFlags ToExecutionFlags(this CommandBehavior commandBehavior)
    {
        // Remove any irrelevant flags and mask the rest of the range for ExecutionFlags so users can't leak any other flags through.
        const int allFlags = (int)CommandBehavior.CloseConnection * 2 - 1; // 2^6 - 1.
        return (ExecutionFlags)(commandBehavior & ~((CommandBehavior)int.MaxValue - allFlags | CommandBehavior.CloseConnection | CommandBehavior.SingleResult));
    }
}

readonly struct CommandCache
{
    public ParameterCache ParameterCache { get; init; }
    public StatementCache StatementCache { get; init; }
}

readonly struct StatementCache
{
    public SizedString SizedString { get; init; }
    public CacheableStatement Statement { get; init; }
}

// Implementation
public sealed partial class SlonCommand
{
    object? _dataSourceOrConnection;
    CommandType _commandType = CommandType.Text;
    TimeSpan _commandTimeout = SlonDataSourceOptions.DefaultCommandTimeout;
    readonly SlonTransaction? _transaction;
    string _userCommandText;
    bool _disposed;
    SlonParameterCollection? _parameterCollection;
    bool _preparationRequested;
    CommandCache _cache;
    object SyncObj => this; // DbCommand base also locks on 'this'.

    SlonCommand(string? commandText, SlonConnection? conn, SlonTransaction? transaction, SlonDataSource? dataSource = null)
    {
        GC.SuppressFinalize(this);
        _userCommandText = commandText ?? string.Empty;
        _transaction = transaction;
        if (conn is not null)
        {
            _dataSourceOrConnection = conn;
            _commandTimeout = conn.DefaultCommandTimeout;
        }
        else if (dataSource is not null)
        {
            _dataSourceOrConnection = dataSource;
            _commandTimeout = dataSource.DefaultCommandTimeout;
        }
    }

    void SetCommandText(string? value)
    {
        if (!ReferenceEquals(value, _userCommandText))
        {
            _preparationRequested = false;
            ResetCache();
            _userCommandText = value ?? string.Empty;
        }
    }

    CommandCache ReadCache()
    {
        lock (SyncObj)
            return _cache;
    }

    /// SetCache will not dispose any individual fields as they may be aliased/reused in the new value.
    void SetCache(in CommandCache value)
    {
        lock (SyncObj)
            _cache = value;
    }

    /// ResetCache will dispose any individual fields.
    void ResetCache()
    {
        lock (SyncObj)
        {
            if (!_cache.ParameterCache.IsDefault)
                _cache.ParameterCache.Dispose();
            _cache = default;
        }
    }

    // Captures any per call state and merges it with the remaining, less volatile, SlonCommand state during GetValues.
    // This allows SlonCommand to be concurrency safe (an execution is entirely isolated but command mutations are not thread safe), store an instance on a static and go!
    // TODO we may want to lock values when _preparationRequested.
    readonly struct Command: IPgCommand
    {
        static readonly IPgCommand.BeginExecutionDelegate BeginExecutionDelegate = BeginExecutionCore;

        readonly SlonCommand _instance;
        readonly SlonParameterCollection? _parameters;
        readonly ExecutionFlags _additionalFlags;

        public Command(SlonCommand instance, SlonParameterCollection? parameters, ExecutionFlags additionalFlags)
        {
            _instance = instance;
            _parameters = parameters;
            _additionalFlags = additionalFlags;
        }

        (Statement?, StatementCache?, ExecutionFlags) GetStatement(StatementCache cache, SlonDataSource dataSource, string statementText, PgTypeIdView parameterTypes)
        {
            var cachedStatement = cache.Statement;
            StatementCache? updatedCache = null;
            if (cachedStatement.IsDefault || !cachedStatement.TryGetValue(statementText, parameterTypes, out var statement))
            {
                if (!cachedStatement.IsDefault)
                    updatedCache = default;

                statement = _instance._preparationRequested
                    ? dataSource.CreateCommandStatement(parameterTypes)
                    : dataSource.GetStatement(statementText, parameterTypes);
            }

            var flags = statement switch
            {
                { IsComplete: true } => ExecutionFlags.Prepared,
                { } => ExecutionFlags.Preparing,
                _ => ExecutionFlags.Unprepared
            };

            return (statement, updatedCache, flags);
        }

        // TODO rewrite if necessary (should have happened already, to allow for batching).
        string GetStatementText()
        {
            return _instance._userCommandText;
        }

        // statementText is expected to be null when we have a prepared statement.
        (ParameterContext, ParameterCache?) BuildParameterContext(SlonDataSource dataSource, string? statementText, SlonParameterCollection? parameters, ParameterCache cache)
        {
            if (parameters is null || parameters.Count == 0)
                // We return null (no change) for the cache here as we rely on command text changes to clear any caches.
                return (ParameterContext.Empty, null);

            return dataSource.GetSlonParameterContextFactory(statementText).Create(parameters, cache, createCache: true);
        }

        public IPgCommand.Values GetValues()
        {
            var cache = _instance.ReadCache();
            var dataSource = _instance.TryGetDataSource(out var s) ? s : _instance.GetConnection().DbDataSource;
            var statementText = GetStatementText();
            var (parameterContext, parameterCache) = BuildParameterContext(dataSource, cache.StatementCache.Statement.IsDefault ? statementText : null, _parameters, cache.ParameterCache);
            var (statement, statementCache, executionFlags) = GetStatement(cache.StatementCache, dataSource, statementText, new(parameterContext));

            if (parameterCache is not null || statementCache is not null)
            {
                // If we got an update we should cleanup the current cache.
                if (parameterCache is not null && !cache.ParameterCache.IsDefault)
                    cache.ParameterCache.Dispose();
                _instance.SetCache(new CommandCache { ParameterCache = parameterCache ?? cache.ParameterCache, StatementCache = statementCache ?? cache.StatementCache });
            }

            return new()
            {
                StatementText = statementText,
                ExecutionFlags = executionFlags | _additionalFlags,
                Statement = statement,
                ExecutionTimeout = _instance._commandTimeout,
                Additional = new()
                {
                    ParameterContext = parameterContext,
                    Flags = CommandFlags.ErrorBarrier,
                    RowRepresentation = RowRepresentation.CreateForAll(DataRepresentation.Binary),
                    State = dataSource,
                }
            };
        }

        public IPgCommand.BeginExecutionDelegate BeginExecutionMethod => BeginExecutionDelegate;
        public CommandExecution BeginExecution(in IPgCommand.Values values) => BeginExecutionCore(values);

        // This is a static function to assure CreateExecution only has dependencies on clearly passed in state.
        // Any unexpected _instance dependencies would undoubtedly cause fun races.
        static CommandExecution BeginExecutionCore(in IPgCommand.Values values)
        {
            var executionFlags = values.ExecutionFlags;
            var statement = values.Statement;
            DebugShim.Assert(values.Additional.State is SlonDataSource);
            DebugShim.Assert(executionFlags.HasUnprepared() || statement is not null);
            // We only allocate to facilitate preparation or output params, both are fairly uncommon operations.
            SlonCommandSession? session = null;
            if (executionFlags.HasPreparing() || values.Additional.ParameterContext.HasOutputSessions())
                session = new SlonCommandSession((SlonDataSource)values.Additional.State, values);

            var commandExecution = executionFlags switch
            {
                _ when executionFlags.HasPrepared() => CommandExecution.Create(executionFlags, values.Additional.Flags, statement!),
                _ when session is not null => CommandExecution.Create(executionFlags, values.Additional.Flags, session),
                _ => CommandExecution.Create(executionFlags, values.Additional.Flags)
            };

            return commandExecution;
        }
    }

    void ThrowIfDisposed()
    {
        if (_disposed)
            ThrowObjectDisposed();

        static void ThrowObjectDisposed() => throw new ObjectDisposedException(nameof(SlonCommand));
    }

    bool TryGetConnection([NotNullWhen(true)]out SlonConnection? connection)
    {
        connection = _dataSourceOrConnection as SlonConnection;
        return connection is not null;
    }
    SlonConnection GetConnection() => TryGetConnection(out var connection) ? connection : throw new NullReferenceException("Connection is null.");
    SlonConnection.CommandWriter GetCommandWriter() => GetConnection().GetCommandWriter();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryGetDataSource([NotNullWhen(true)]out SlonDataSource? connection)
    {
        connection = _dataSourceOrConnection as SlonDataSource;
        return connection is not null;
    }

    // Only for DbConnection commands, throws for DbDataSource commands (alternatively we can choose to ignore it).
    bool HasCloseConnection(CommandBehavior behavior) => (behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection;
    void ThrowIfHasCloseConnection(CommandBehavior behavior)
    {
        if (HasCloseConnection(behavior))
            ThrowHasCloseConnection();

        void ThrowHasCloseConnection() => throw new ArgumentException($"Cannot pass {nameof(CommandBehavior.CloseConnection)} to a DbDataSource command, this is only valid when a command has a connection.");
    }

    SlonDataReader ExecuteDataReader(CommandBehavior behavior)
    {
        ThrowIfDisposed();
        if (TryGetDataSource(out var dataSource))
        {
            ThrowIfHasCloseConnection(behavior);
            // Pick a connection and do the write ourselves, connectionless command execution for sync paths :)
            var slot = dataSource.GetSlot(exclusiveUse: false, dataSource.ConnectionTimeout);
            var command = dataSource.WriteCommand(slot, CreateCommand(null, behavior));
            return SlonDataReader.Create(async: false, new ValueTask<CommandContextBatch<CommandExecution>>(command)).GetAwaiter().GetResult();
        }
        else
        {
            var command = GetCommandWriter().WriteCommand(allowPipelining: false, CreateCommand(null, behavior), HasCloseConnection(behavior));
            return SlonDataReader.Create(async: false, command).GetAwaiter().GetResult();
        }
    }

    ValueTask<SlonDataReader> ExecuteDataReaderAsync(SlonParameterCollection? parameters, CommandBehavior behavior, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (TryGetDataSource(out var dataSource))
        {
            ThrowIfHasCloseConnection(behavior);
            var command = dataSource.WriteMultiplexingCommand(CreateCommand(parameters, behavior), cancellationToken);
            return SlonDataReader.Create(async: true, command);
        }
        else
        {
            var command = GetCommandWriter().WriteCommand(allowPipelining: true, CreateCommand(parameters, behavior), HasCloseConnection(behavior), cancellationToken);
            return SlonDataReader.Create(async: true, command);
        }
    }

    Command CreateCommand(SlonParameterCollection? parameters, CommandBehavior behavior)
        => new(this, parameters ?? _parameterCollection, behavior.ToExecutionFlags());

    async ValueTask DisposeCore(bool async)
    {
        if (_disposed)
            return;
        _disposed = true;

        // TODO, unprepare etc.
        await new ValueTask().ConfigureAwait(false);

        ResetCache();
        base.Dispose(true);
    }
}

// Public surface & ADO.NET
public sealed partial class SlonCommand: DbCommand
{
    public SlonCommand() : this(null, null, null) {}
    public SlonCommand(string? commandText) : this(commandText, null, null) {}
    public SlonCommand(string? commandText, SlonConnection? conn) : this(commandText, conn, null) {}
    public SlonCommand(string? commandText, SlonConnection? conn, SlonTransaction? transaction)
        : this(commandText, conn, transaction, null) {} // Points to the private constructor.
    internal SlonCommand(string? commandText, SlonDataSource dataSource)
        : this(commandText, null, null, dataSource: dataSource) {} // Points to the private constructor.

    public override void Prepare()
    {
        ThrowIfDisposed();
        _preparationRequested = true;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _userCommandText;
        set => SetCommandText(value);
    }

    public override int CommandTimeout
    {
        get => (int)_commandTimeout.TotalSeconds;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Cannot be zero or negative.");
            _commandTimeout = TimeSpan.FromSeconds(value);
        }
    }

    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (!EnumShim.IsDefined(value))
                throw new ArgumentOutOfRangeException();
            _commandType = value;
        }
    }

    /// <summary>
    /// Setting this property is ignored by Slon as its values are not respected.
    /// Gets or sets how command results are applied to the DataRow when used by the
    /// DbDataAdapter.Update(DataSet) method.
    /// </summary>
    /// <value>One of the <see cref="System.Data.UpdateRowSource"/> values.</value>
    public override UpdateRowSource UpdatedRowSource
    {
        get => UpdateRowSource.None;
        set { }
    }

    public new SlonParameterCollection Parameters => _parameterCollection ??= new();

    /// <summary>
    /// Setting this property is ignored by Slon. PostgreSQL only supports a single transaction at a given time on
    /// a given connection, and all commands implicitly run inside the current transaction started via
    /// <see cref="SlonConnection.BeginTransaction()"/>
    /// </summary>
    public new SlonTransaction? Transaction => _transaction;

    public override bool DesignTimeVisible { get; set; }

    public override void Cancel()
    {
        // We can't throw in connectionless scenarios as dapper etc expect this method to work.
        // TODO We might be able to support it on connectionless commands by creating protocol level support for it, not today :)
        if (!TryGetConnection(out var connection) || !connection.ConnectionOpInProgress)
            return;

        connection.PerformUserCancellation();
    }

    public override int ExecuteNonQuery()
    {
        throw new NotImplementedException();
    }

    public override object? ExecuteScalar()
    {
        throw new NotImplementedException();
    }

    public new SlonDataReader ExecuteReader()
        => ExecuteDataReader(CommandBehavior.Default);
    public new SlonDataReader ExecuteReader(CommandBehavior behavior)
        => ExecuteDataReader(behavior);

    public new Task<SlonDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(null, CommandBehavior.Default, cancellationToken).AsTask();
    public new Task<SlonDataReader> ExecuteReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(null, behavior, cancellationToken).AsTask();

    public ValueTask<SlonDataReader> ExecuteReaderAsync(SlonParameterCollection? parameters, CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(parameters, CommandBehavior.Default, cancellationToken);

    public ValueTask<SlonDataReader> ExecuteReaderAsync(SlonParameterCollection? parameters, CommandBehavior behavior, CancellationToken cancellationToken = default)
        => ExecuteDataReaderAsync(parameters, behavior, cancellationToken);

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteDataReader(behavior);
    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        => await ExecuteDataReaderAsync(null, behavior, cancellationToken);

    protected override DbParameter CreateDbParameter() => SlonDbParameter.Create();
    protected override DbConnection? DbConnection
    {
        get => _dataSourceOrConnection as SlonConnection;
        set
        {
            ThrowIfDisposed();
            if (value is not SlonConnection conn)
                throw new ArgumentException($"Value is not an instance of {nameof(SlonConnection)}.", nameof(value));

            if (TryGetConnection(out var current))
            {
                if (!ReferenceEquals(current.DbDataSource.DataSourceOwner, conn.DbDataSource.DataSourceOwner))
                    ResetCache();
            }
            else
                throw new InvalidOperationException("This is a DbDataSource command and cannot be assigned to connections.");

            _dataSourceOrConnection = conn;
        }
    }

    protected override DbParameterCollection DbParameterCollection => Parameters;
    protected override DbTransaction? DbTransaction { get => Transaction; set {} }

#if !NETSTANDARD2_0
    public override ValueTask DisposeAsync()
#else
    public ValueTask DisposeAsync()
#endif
        => DisposeCore(async: true);

    protected override void Dispose(bool disposing)
        => DisposeCore(false).GetAwaiter().GetResult();
}
