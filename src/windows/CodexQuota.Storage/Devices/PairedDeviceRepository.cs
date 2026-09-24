using CodexQuota.Storage.Database;

namespace CodexQuota.Storage.Devices;

/// <summary>
/// One paired device.
/// </summary>
/// <param name="DeviceId">Non-secret identifier of the device.</param>
/// <param name="DisplayName">User-visible name shown in the Windows tray.</param>
/// <param name="PairedAt">UTC instant at which pairing completed.</param>
/// <param name="RevokedAt">UTC instant at which the device was revoked, if it has been.</param>
/// <param name="TokenHash">
/// Hash of the device credential. The credential itself is never persisted, here or anywhere else.
/// </param>
public sealed record PairedDevice(
    string DeviceId,
    string DisplayName,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt,
    string TokenHash);

/// <summary>Storage for paired devices. The schema is multi-device even though V1's UI is not.</summary>
public interface IPairedDeviceRepository
{
    /// <summary>Records a newly paired device.</summary>
    Task AppendAsync(PairedDevice device, CancellationToken cancellationToken);

    /// <summary>Looks up a device by its identifier, revoked or not.</summary>
    Task<PairedDevice?> FindByDeviceIdAsync(string deviceId, CancellationToken cancellationToken);

    /// <summary>Lists every device, revoked or not.</summary>
    Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Marks a device revoked. Returns <c>false</c> when no such device exists.</summary>
    Task<bool> RevokeAsync(string deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken);
}

/// <summary>
/// SQLite implementation of <see cref="IPairedDeviceRepository"/>.
/// </summary>
/// <remarks>
/// Timestamps use the same fixed-width UTC text form as the history tables, so lexical comparison
/// on the column is chronological comparison.
/// </remarks>
public sealed class PairedDeviceRepository : IPairedDeviceRepository
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";

    private readonly BridgeDatabase _database;

    public PairedDeviceRepository(BridgeDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task AppendAsync(PairedDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        return _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO paired_devices (device_id, display_name, paired_at_utc, revoked_at_utc, token_hash)
                    VALUES ($deviceId, $displayName, $pairedAt, $revokedAt, $tokenHash);
                    """;
                command.Parameters.AddWithValue("$deviceId", device.DeviceId);
                command.Parameters.AddWithValue("$displayName", device.DisplayName);
                command.Parameters.AddWithValue("$pairedAt", Format(device.PairedAt));
                command.Parameters.AddWithValue(
                    "$revokedAt",
                    device.RevokedAt is { } revokedAt ? Format(revokedAt) : (object)DBNull.Value);
                command.Parameters.AddWithValue("$tokenHash", device.TokenHash);

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<PairedDevice?> FindByDeviceIdAsync(string deviceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return _database.ExecuteAsync<PairedDevice?>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT device_id, display_name, paired_at_utc, revoked_at_utc, token_hash
                    FROM paired_devices
                    WHERE device_id = $deviceId
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$deviceId", deviceId);

                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                return await reader.ReadAsync(token).ConfigureAwait(false) ? Read(reader) : null;
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<PairedDevice>> ListAsync(CancellationToken cancellationToken)
        => _database.ExecuteAsync<IReadOnlyList<PairedDevice>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT device_id, display_name, paired_at_utc, revoked_at_utc, token_hash
                    FROM paired_devices
                    ORDER BY paired_at_utc ASC;
                    """;

                var devices = new List<PairedDevice>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    devices.Add(Read(reader));
                }

                return devices;
            },
            cancellationToken);

    public Task<bool> RevokeAsync(string deviceId, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE paired_devices
                    SET revoked_at_utc = $revokedAt
                    WHERE device_id = $deviceId AND revoked_at_utc IS NULL;
                    """;
                command.Parameters.AddWithValue("$deviceId", deviceId);
                command.Parameters.AddWithValue("$revokedAt", Format(revokedAt));

                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
            },
            cancellationToken);
    }

    private static PairedDevice Read(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            DateTimeOffset.Parse(
                reader.GetString(2),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal),
            reader.IsDBNull(3)
                ? null
                : DateTimeOffset.Parse(
                    reader.GetString(3),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal),
            reader.GetString(4));

    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
}
