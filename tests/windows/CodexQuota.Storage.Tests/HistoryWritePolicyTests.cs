using CodexQuota.Core.Quota;
using CodexQuota.Storage.History;
using Xunit;

namespace CodexQuota.Storage.Tests;

public class HistoryWritePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WritesTheFirstSampleWhenNothingHasBeenPersisted()
    {
        var update = HistoryFixtures.Update(previous: null, current: HistoryFixtures.Snapshot(80, 60));

        Assert.True(new HistoryWritePolicy().ShouldWrite(update, lastPersisted: null, Now));
    }

    [Fact]
    public void SkipsAnIdenticalSampleInsideFiveMinutes()
    {
        var lastPersisted = new HistoryPoint(Now.AddMinutes(-1), 80, 60);
        var snapshot = HistoryFixtures.Snapshot(80, 60);
        var update = HistoryFixtures.Update(previous: snapshot, current: snapshot);

        Assert.False(new HistoryWritePolicy().ShouldWrite(update, lastPersisted, Now));
    }

    [Fact]
    public void WritesWhenTheShortWindowQuotaChanges()
    {
        var lastPersisted = new HistoryPoint(Now.AddMinutes(-1), 80, 60);
        var update = HistoryFixtures.Update(
            previous: HistoryFixtures.Snapshot(80, 60),
            current: HistoryFixtures.Snapshot(70, 60));

        Assert.True(new HistoryWritePolicy().ShouldWrite(update, lastPersisted, Now));
    }

    [Fact]
    public void WritesWhenTheWeeklyWindowQuotaChanges()
    {
        var lastPersisted = new HistoryPoint(Now.AddMinutes(-1), 80, 60);
        var update = HistoryFixtures.Update(
            previous: HistoryFixtures.Snapshot(80, 60),
            current: HistoryFixtures.Snapshot(80, 55));

        Assert.True(new HistoryWritePolicy().ShouldWrite(update, lastPersisted, Now));
    }

    [Fact]
    public void WritesWhenAWindowResetsEvenIfPercentagesAreUnchanged()
    {
        var lastPersisted = new HistoryPoint(Now.AddMinutes(-1), 80, 60);
        var before = HistoryFixtures.Snapshot(80, 60, resetsAt: new DateTimeOffset(2026, 9, 22, 18, 0, 0, TimeSpan.Zero));
        var after = HistoryFixtures.Snapshot(80, 60, resetsAt: new DateTimeOffset(2026, 9, 22, 23, 0, 0, TimeSpan.Zero));

        Assert.True(
            new HistoryWritePolicy().ShouldWrite(
                HistoryFixtures.Update(previous: before, current: after),
                lastPersisted,
                Now));
    }

    [Fact]
    public void WritesWhenFiveMinutesHaveElapsedSinceTheLastPersistedSample()
    {
        var lastPersisted = new HistoryPoint(Now.AddMinutes(-5), 80, 60);
        var snapshot = HistoryFixtures.Snapshot(80, 60);
        var update = HistoryFixtures.Update(previous: snapshot, current: snapshot);

        Assert.True(new HistoryWritePolicy().ShouldWrite(update, lastPersisted, Now));
    }

    [Fact]
    public void DoesNotWriteJustBeforeFiveMinutesHaveElapsed()
    {
        var lastPersisted = new HistoryPoint(Now.AddSeconds(-299), 80, 60);
        var snapshot = HistoryFixtures.Snapshot(80, 60);
        var update = HistoryFixtures.Update(previous: snapshot, current: snapshot);

        Assert.False(new HistoryWritePolicy().ShouldWrite(update, lastPersisted, Now));
    }
}

/// <summary>Shared builders for the storage tests. Percentages always sum to 100.</summary>
internal static class HistoryFixtures
{
    internal const long ShortResetsAtUnix = 1_800_000_000;

    internal static QuotaSnapshot Snapshot(
        double shortRemaining,
        double weeklyRemaining,
        DateTimeOffset? resetsAt = null,
        DateTimeOffset? generatedAt = null)
    {
        var shortResetsAt = resetsAt ?? DateTimeOffset.FromUnixTimeSeconds(ShortResetsAtUnix);
        var producedAt = generatedAt ?? new DateTimeOffset(2026, 9, 22, 13, 0, 0, TimeSpan.Zero);

        return new QuotaSnapshot(
            SchemaVersion: 1,
            GeneratedAt: producedAt,
            Source: "codex_app_server",
            Status: QuotaSourceStatus.Online,
            LastSuccessfulSyncAt: producedAt,
            ShortWindow: new QuotaWindow(
                UsedPercent: 100d - shortRemaining,
                RemainingPercent: shortRemaining,
                WindowMinutes: 300,
                ResetsAt: shortResetsAt),
            Weekly: new QuotaWindow(
                UsedPercent: 100d - weeklyRemaining,
                RemainingPercent: weeklyRemaining,
                WindowMinutes: 10080,
                ResetsAt: shortResetsAt.AddDays(6)));
    }

    internal static QuotaStateUpdate Update(QuotaSnapshot? previous, QuotaSnapshot current, long sequence = 1)
        => new(sequence, previous, current);
}
