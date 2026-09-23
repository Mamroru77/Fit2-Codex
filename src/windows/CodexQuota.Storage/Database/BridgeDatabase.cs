using Microsoft.Data.Sqlite;

namespace CodexQuota.Storage.Database;

/// <summary>
/// Owns the SQLite connection for the Bridge database and creates its schema on first use.
/// </summary>
/// <remarks>
/// The connection is opened once and kept open for the lifetime of this object. That is
/// required for <c>Data Source=:memory:</c> databases, which disappear as soon as their
/// connection closes.
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

    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private readonly SqliteConnection _connection;
    private bool _schemaCreated;
    private bool _disposed;

    public BridgeDatabase(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <summary>The underlying connection. It is opened by <see cref="EnsureCreatedAsync"/>.</summary>
    public SqliteConnection Connection
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _connection;
        }
    }

    /// <summary>Opens the connection and creates the schema the first time it is called.</summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_schemaCreated)
        {
            return;
        }

        await _schemaGate.WaitAsync(cancellationToken);
        try
        {
            if (_schemaCreated)
            {
                return;
            }

            if (_connection.State != System.Data.ConnectionState.Open)
            {
                await _connection.OpenAsync(cancellationToken);
            }

            await using var command = _connection.CreateCommand();
            command.CommandText = SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);

            _schemaCreated = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connection.DisposeAsync();
        _schemaGate.Dispose();
    }
}
