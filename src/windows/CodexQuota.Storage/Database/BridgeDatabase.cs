using Microsoft.Data.Sqlite;

namespace CodexQuota.Storage.Database;

/// <summary>
/// Owns the single SQLite connection for the Bridge database, creates its schema on first use, and
/// serializes every operation that touches the connection.
/// </summary>
/// <remarks>
/// <para>
/// The connection is opened once and kept open for the lifetime of this object. That is required
/// for <c>Data Source=:memory:</c> databases, which disappear as soon as their connection closes,
/// so the connection lifetime must not be traded away for thread safety.
/// </para>
/// <para>
/// Because there is exactly one connection there must be exactly one user of it at a time: SQLite
/// forbids using one connection from several threads at once, and the managed command bookkeeping
/// around it is not thread-safe either. Every statement therefore runs inside
/// <see cref="ExecuteAsync{T}"/>, which holds a single permit for the whole operation — including
/// across awaits — and the raw connection is deliberately not reachable from outside this assembly.
/// </para>
/// </remarks>
public sealed class BridgeDatabase : IAsyncDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS quota_samples (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            recorded_at_utc TEXT NOT NULL,
            short_window_remaining_percent REAL NOT NULL,
            weekly_window_remaining_percent REAL NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_quota_samples_recorded_at_utc
            ON quota_samples (recorded_at_utc);

        CREATE TABLE IF NOT EXISTS quota_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at_utc TEXT NOT NULL,
            event_type TEXT NOT NULL,
            detail TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_quota_events_occurred_at_utc
            ON quota_events (occurred_at_utc);

        CREATE TABLE IF NOT EXISTS paired_devices (
            device_id TEXT NOT NULL PRIMARY KEY,
            display_name TEXT NULL,
            paired_at_utc TEXT NOT NULL,
            revoked_at_utc TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS bridge_metadata (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NULL
        );
        """;

    /// <summary>
    /// The one permit that makes the one connection safe to share. It is intentionally never
    /// disposed: <see cref="SemaphoreSlim"/> allocates no unmanaged resource unless
    /// <c>AvailableWaitHandle</c> is touched, and disposing it while an operation still holds it
    /// would turn an orderly shutdown into a spurious <see cref="ObjectDisposedException"/>.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly SqliteConnection _connection;
    private bool _schemaCreated;
    private bool _disposed;

    public BridgeDatabase(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <summary>
    /// Runs <paramref name="operation"/> against the shared connection while holding its exclusive
    /// gate, so two operations can never interleave on it.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await OpenAndCreateSchemaAsync(cancellationToken).ConfigureAwait(false);
            return await operation(_connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs an operation that produces no value, under the same gate.</summary>
    public Task ExecuteAsync(
        Func<SqliteConnection, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return ExecuteAsync(
            async (connection, token) =>
            {
                await operation(connection, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>Opens the connection and creates the schema the first time it is called.</summary>
    public Task EnsureCreatedAsync(CancellationToken cancellationToken)
        => ExecuteAsync(static (_, _) => Task.CompletedTask, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        return _connection.DisposeAsync();
    }

    /// <summary>
    /// Opens the connection and creates the schema. The caller already holds the gate, which is what
    /// keeps two first-time callers from racing each other into <c>OpenAsync</c>.
    /// </summary>
    private async Task OpenAndCreateSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaCreated)
        {
            return;
        }

        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = _connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _schemaCreated = true;
    }
}
