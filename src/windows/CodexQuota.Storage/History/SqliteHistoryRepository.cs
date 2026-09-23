using System.Globalization;
using CodexQuota.Core.Quota;
using CodexQuota.Storage.Database;
using Microsoft.Data.Sqlite;

namespace CodexQuota.Storage.History;

/// <summary>
/// SQLite implementation of <see cref="IHistoryRepository"/>.
/// </summary>
/// <remarks>
/// Timestamps are stored as fixed-width UTC text so that lexical comparison on the column is
/// also chronological comparison.
/// </remarks>
public sealed class SqliteHistoryRepository : IHistoryRepository
{
    /// <summary>Fixed-width UTC format. Every value is 27 characters, always ending in <c>Z</c>.</summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";

    private readonly BridgeDatabase _database;

    public SqliteHistoryRepository(BridgeDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _database.EnsureCreatedAsync(cancellationToken);

        await using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quota_samples (recorded_at_utc, short_window_remaining_percent, weekly_window_remaining_percent)
            VALUES ($recordedAt, $shortRemaining, $weeklyRemaining);
            """;
        command.Parameters.AddWithValue("$recordedAt", Format(update.Current.GeneratedAt));
        command.Parameters.AddWithValue("$shortRemaining", update.Current.ShortWindow.RemainingPercent);
        command.Parameters.AddWithValue("$weeklyRemaining", update.Current.Weekly.RemainingPercent);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotaEvent);

        await _database.EnsureCreatedAsync(cancellationToken);

        await using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quota_events (occurred_at_utc, event_type, detail)
            VALUES ($occurredAt, $eventType, $detail);
            """;
        command.Parameters.AddWithValue("$occurredAt", Format(quotaEvent.OccurredAt));
        command.Parameters.AddWithValue("$eventType", quotaEvent.Type.ToString());
        command.Parameters.AddWithValue("$detail", (object?)quotaEvent.Detail ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await _database.EnsureCreatedAsync(cancellationToken);

        await using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT recorded_at_utc, short_window_remaining_percent, weekly_window_remaining_percent
            FROM quota_samples
            WHERE recorded_at_utc >= $from AND recorded_at_utc <= $to
            ORDER BY recorded_at_utc ASC;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));

        var samples = new List<HistoryPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryReadTimestamp(reader.GetString(0), out var recordedAt))
            {
                continue;
            }

            samples.Add(new HistoryPoint(recordedAt, reader.GetDouble(1), reader.GetDouble(2)));
        }

        return samples;
    }

    public async Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        await _database.EnsureCreatedAsync(cancellationToken);

        await using var command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT occurred_at_utc, event_type, detail
            FROM quota_events
            WHERE occurred_at_utc >= $from AND occurred_at_utc <= $to
            ORDER BY occurred_at_utc ASC;
            """;
        command.Parameters.AddWithValue("$from", Format(from));
        command.Parameters.AddWithValue("$to", Format(to));

        var events = new List<QuotaEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!TryReadTimestamp(reader.GetString(0), out var occurredAt)
                || !Enum.TryParse<QuotaEventType>(reader.GetString(1), out var type))
            {
                continue;
            }

            var detail = reader.IsDBNull(2) ? null : reader.GetString(2);
            events.Add(new QuotaEvent(type, occurredAt, detail));
        }

        return events;
    }

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await _database.EnsureCreatedAsync(cancellationToken);

        var formatted = Format(cutoff);

        await using var samples = _database.Connection.CreateCommand();
        samples.CommandText = "DELETE FROM quota_samples WHERE recorded_at_utc < $cutoff;";
        samples.Parameters.AddWithValue("$cutoff", formatted);
        await samples.ExecuteNonQueryAsync(cancellationToken);

        await using var events = _database.Connection.CreateCommand();
        events.CommandText = "DELETE FROM quota_events WHERE occurred_at_utc < $cutoff;";
        events.Parameters.AddWithValue("$cutoff", formatted);
        await events.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    private static bool TryReadTimestamp(string text, out DateTimeOffset value)
        => DateTimeOffset.TryParseExact(
            text,
            TimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);
}
