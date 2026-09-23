using CodexQuota.Core.Quota;
using CodexQuota.Storage.Database;
using CodexQuota.Storage.History;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CodexQuota.Storage.Tests;

public class SqliteHistoryRepositoryTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AppendedSampleIsReturnedByReadHistory()
    {
        await using var database = new BridgeDatabase(new SqliteConnection("Data Source=:memory:"));
        var repository = new SqliteHistoryRepository(database);

        await repository.AppendSnapshotAsync(
            HistoryFixtures.Update(previous: null, current: HistoryFixtures.Snapshot(80, 60)),
            CancellationToken.None);

        var history = await repository.ReadHistoryAsync(From, To, CancellationToken.None);

        var sample = Assert.Single(history);
        Assert.Equal(80d, sample.ShortWindowRemainingPercent);
        Assert.Equal(60d, sample.WeeklyRemainingPercent);
        Assert.Equal(TimeSpan.Zero, sample.Timestamp.Offset);
    }

    [Fact]
    public async Task ReadHistoryIsEmptyOutsideTheRequestedRange()
    {
        await using var database = new BridgeDatabase(new SqliteConnection("Data Source=:memory:"));
        var repository = new SqliteHistoryRepository(database);

        await repository.AppendSnapshotAsync(
            HistoryFixtures.Update(previous: null, current: HistoryFixtures.Snapshot(80, 60)),
            CancellationToken.None);

        var later = await repository.ReadHistoryAsync(
            new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Empty(later);
    }

    [Fact]
    public async Task AppendedEventIsReturnedByReadEvents()
    {
        await using var database = new BridgeDatabase(new SqliteConnection("Data Source=:memory:"));
        var repository = new SqliteHistoryRepository(database);

        await repository.AppendEventAsync(
            new QuotaEvent(QuotaEventType.WindowReset, new DateTimeOffset(2026, 9, 22, 15, 0, 0, TimeSpan.Zero), "short"),
            CancellationToken.None);

        var events = await repository.ReadEventsAsync(From, To, CancellationToken.None);

        var recorded = Assert.Single(events);
        Assert.Equal(QuotaEventType.WindowReset, recorded.Type);
        Assert.Equal("short", recorded.Detail);
        Assert.Equal(TimeSpan.Zero, recorded.OccurredAt.Offset);
    }

    [Fact]
    public async Task CleanupDeletesSamplesAndEventsOlderThanTheCutoff()
    {
        await using var database = new BridgeDatabase(new SqliteConnection("Data Source=:memory:"));
        var repository = new SqliteHistoryRepository(database);

        // One old sample and one old event, both well before the 25-hour cutoff.
        await repository.AppendSnapshotAsync(
            HistoryFixtures.Update(
                previous: null,
                current: HistoryFixtures.Snapshot(
                    80,
                    60,
                    generatedAt: new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero))),
            CancellationToken.None);
        await repository.AppendEventAsync(
            new QuotaEvent(QuotaEventType.WindowReset, new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero)),
            CancellationToken.None);
        await repository.DeleteOlderThanAsync(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);

        Assert.Empty(await repository.ReadHistoryAsync(From, To, CancellationToken.None));
        Assert.Empty(await repository.ReadEventsAsync(From, To, CancellationToken.None));
    }
}
