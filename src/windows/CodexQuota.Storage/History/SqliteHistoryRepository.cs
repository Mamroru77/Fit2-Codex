using System.Globalization;
using CodexQuota.Core.Quota;
using CodexQuota.Storage.Database;
using Microsoft.Data.Sqlite;

namespace CodexQuota.Storage.History;

/// <summary>
/// SQLite implementation of <see cref="IHistoryRepository"/>.
/// </summary>
/// <remarks>
/// <para>
/// Timestamps are stored as fixed-width UTC text so that lexical comparison on the column is
/// also chronological comparison.
/// </para>
/// <para>
/// Every statement runs through <see cref="BridgeDatabase.ExecuteAsync{T}"/>: the database owns one
/// connection, and that gate is the only thing that makes it safe for concurrent readers and
/// writers to share it.
/// </para>
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

    public Task AppendSnapshotAsync(QuotaStateUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        return _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO quota_samples (recorded_at_utc, short_window_remaining_percent, weekly_window_remaining_percent)
                    VALUES ($recordedAt, $shortRemaining, $weeklyRemaining);
                    """;
                command.Parameters.AddWithValue("$recordedAt", Format(update.Current.GeneratedAt));
                command.Parameters.AddWithValue("$shortRemaining", update.Current.ShortWindow.RemainingPercent);
                command.Parameters.AddWithValue("$weeklyRemaining", update.Current.Weekly.RemainingPercent);

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task AppendEventAsync(QuotaEvent quotaEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(quotaEvent);

        return _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO quota_events (occurred_at_utc, event_type, detail)
                    VALUES ($occurredAt, $eventType, $detail);
                    """;
                command.Parameters.AddWithValue("$occurredAt", Format(quotaEvent.OccurredAt));
                command.Parameters.AddWithValue("$eventType", quotaEvent.Type.ToString());
                command.Parameters.AddWithValue("$detail", (object?)quotaEvent.Detail ?? DBNull.Value);

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<HistoryPoint>> ReadHistoryAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => _database.ExecuteAsync<IReadOnlyList<HistoryPoint>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT recorded_at_utc, short_window_remaining_percent, weekly_window_remaining_percent
                    FROM quota_samples
                    WHERE recorded_at_utc >= $from AND recorded_at_utc <= $to
                    ORDER BY recorded_at_utc ASC;
                    """;
                command.Parameters.AddWithValue("$from", Format(from));
                command.Parameters.AddWithValue("$to", Format(to));

                var samples = new List<HistoryPoint>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!TryReadTimestamp(reader.GetString(0), out var recordedAt))
                    {
                        continue;
                    }

                    samples.Add(new HistoryPoint(recordedAt, reader.GetDouble(1), reader.GetDouble(2)));
                }

                return samples;
            },
            cancellationToken);

    public Task<IReadOnlyList<QuotaEvent>> ReadEventsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
        => _database.ExecuteAsync<IReadOnlyList<QuotaEvent>>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT occurred_at_utc, event_type, detail
                    FROM quota_events
                    WHERE occurred_at_utc >= $from AND occurred_at_utc <= $to
                    ORDER BY occurred_at_utc ASC;
                    """;
                command.Parameters.AddWithValue("$from", Format(from));
                command.Parameters.AddWithValue("$to", Format(to));

                var events = new List<QuotaEvent>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
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
            },
            cancellationToken);

    public Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var formatted = Format(cutoff);

        // Both deletes run under one permit, so a reader can never observe the samples table
        // already pruned while the events table is not.
        return _database.ExecuteAsync(
            async (connection, token) =>
            {
                await using var samples = connection.CreateCommand();
                samples.CommandText = "DELETE FROM quota_samples WHERE recorded_at_utc < $cutoff;";
                samples.Parameters.AddWithValue("$cutoff", formatted);
                await samples.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await using var events = connection.CreateCommand();
                events.CommandText = "DELETE FROM quota_events WHERE occurred_at_utc < $cutoff;";
                events.Parameters.AddWithValue("$cutoff", formatted);
                await events.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
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
