using System.Collections.Concurrent;
using CodexQuota.Core.Quota;
using Xunit;

namespace CodexQuota.Core.Tests;

public class InMemoryQuotaStateStoreTests
{
    [Fact]
    public void Current_IsNullBeforeAnyReplace()
    {
        var store = new InMemoryQuotaStateStore();

        Assert.Null(store.Current);
    }

    [Fact]
    public void Sequence_IsZeroBeforeAnyReplace()
    {
        var store = new InMemoryQuotaStateStore();

        Assert.Equal(0L, store.Sequence);
    }

    [Fact]
    public void Replace_IncrementsSequenceAndReturnsPreviousSnapshot()
    {
        var store = new InMemoryQuotaStateStore();
        var first = Fixtures.Snapshot(shortRemaining: 80, weeklyRemaining: 60);
        var second = Fixtures.Snapshot(shortRemaining: 70, weeklyRemaining: 55);

        var update1 = store.Replace(first);
        var update2 = store.Replace(second);

        Assert.Equal(1, update1.Sequence);
        Assert.Null(update1.Previous);

        Assert.Equal(2, update2.Sequence);
        Assert.Equal(first, update2.Previous);

        Assert.Equal(second, store.Current);
    }

    [Fact]
    public void Replace_IncrementsSequenceExactlyOncePerCall()
    {
        var store = new InMemoryQuotaStateStore();
        QuotaSnapshot? previousSnapshot = null;

        for (var call = 1L; call <= 5; call++)
        {
            var snapshot = Fixtures.Snapshot(shortRemaining: 100 - call, weeklyRemaining: 50);

            var update = store.Replace(snapshot);

            Assert.Equal(call, update.Sequence);
            Assert.Equal(previousSnapshot, update.Previous);
            Assert.Equal(snapshot, update.Current);
            Assert.Equal(snapshot, store.Current);
            Assert.Equal(call, store.Sequence);

            previousSnapshot = snapshot;
        }
    }

    [Fact]
    public void Replace_RejectsNullSnapshot()
    {
        var store = new InMemoryQuotaStateStore();

        Assert.Throws<ArgumentNullException>(() => store.Replace(null!));
    }

    [Fact]
    public async Task Replace_KeepsSequenceAndCurrentConsistentUnderConcurrency()
    {
        const int writers = 4;
        const int replacementsPerWriter = 250;
        const int totalReplacements = writers * replacementsPerWriter;

        var store = new InMemoryQuotaStateStore();
        var updates = new ConcurrentBag<QuotaStateUpdate>();
        using var startGate = new ManualResetEventSlim(false);

        var writerTasks = Enumerable.Range(0, writers)
            .Select(writer => Task.Run(() =>
            {
                startGate.Wait();

                for (var index = 0; index < replacementsPerWriter; index++)
                {
                    // Every snapshot is unique so sequence, previous and current can be cross-checked.
                    var ordinal = (writer * replacementsPerWriter) + index + 1;
                    var snapshot = Fixtures.Snapshot(
                        shortRemaining: 100d * ordinal / (totalReplacements + 1),
                        weeklyRemaining: 50);

                    updates.Add(store.Replace(snapshot));
                }
            }))
            .ToArray();

        startGate.Set();
        await Task.WhenAll(writerTasks);

        Assert.Equal(totalReplacements, updates.Count);

        // Each successful replacement consumes exactly one sequence value: no gaps, no duplicates.
        Assert.Equal(
            Enumerable.Range(1, totalReplacements).Select(sequence => (long)sequence),
            updates.Select(update => update.Sequence).OrderBy(sequence => sequence));

        var snapshotBySequence = updates.ToDictionary(update => update.Sequence, update => update.Current);

        foreach (var update in updates)
        {
            Assert.Equal(snapshotBySequence[update.Sequence], update.Current);

            var expectedPrevious = update.Sequence == 1
                ? null
                : snapshotBySequence[update.Sequence - 1];

            Assert.Equal(expectedPrevious, update.Previous);
        }

        // The store's own view agrees with the highest sequence it handed out.
        Assert.Equal(totalReplacements, store.Sequence);
        Assert.Equal(snapshotBySequence[totalReplacements], store.Current);
    }
}

/// <summary>
/// Builds canonical, valid quota snapshots for tests. Timestamps are always UTC and
/// every produced window is checked so used + remaining equals 100 within
/// <see cref="PercentTolerance"/>.
/// </summary>
internal static class Fixtures
{
    internal const double PercentTolerance = 0.0001;
    internal const int SchemaVersion = 1;
    internal const string Source = "codex_app_server";
    internal const int ShortWindowMinutes = 300;
    internal const int WeeklyWindowMinutes = 10080;

    internal static QuotaSnapshot Snapshot(
        double shortRemaining,
        double weeklyRemaining,
        DateTimeOffset? generatedAt = null,
        QuotaSourceStatus status = QuotaSourceStatus.Online,
        int shortWindowMinutes = ShortWindowMinutes,
        int weeklyWindowMinutes = WeeklyWindowMinutes)
    {
        var generated = AsUtc(generatedAt ?? DateTimeOffset.UtcNow);

        return new QuotaSnapshot(
            SchemaVersion,
            generated,
            Source,
            status,
            generated,
            Window(shortRemaining, shortWindowMinutes, generated.AddMinutes(shortWindowMinutes)),
            Window(weeklyRemaining, weeklyWindowMinutes, generated.AddMinutes(weeklyWindowMinutes)));
    }

    /// <summary>Builds a window from a remaining percentage, deriving the used percentage.</summary>
    internal static QuotaWindow Window(double remainingPercent, int windowMinutes, DateTimeOffset resetsAt)
        => BuildWindow(100d - remainingPercent, remainingPercent, windowMinutes, resetsAt);

    /// <summary>Builds a window from explicit percentages and enforces the fixture invariant.</summary>
    internal static QuotaWindow BuildWindow(
        double usedPercent,
        double remainingPercent,
        int windowMinutes,
        DateTimeOffset resetsAt)
    {
        EnsurePercentagesAreConsistent(usedPercent, remainingPercent);

        return new QuotaWindow(usedPercent, remainingPercent, windowMinutes, AsUtc(resetsAt));
    }

    internal static void EnsurePercentagesAreConsistent(double usedPercent, double remainingPercent)
    {
        if (Math.Abs(usedPercent + remainingPercent - 100d) > PercentTolerance)
        {
            throw new InvalidOperationException(
                $"Quota window percentages are inconsistent: used={usedPercent}, remaining={remainingPercent}.");
        }
    }

    private static DateTimeOffset AsUtc(DateTimeOffset value) => value.ToUniversalTime();
}

public class FixturesTests
{
    [Fact]
    public void Snapshot_ProducesUtcTimestampsAndConsistentPercentages()
    {
        var snapshot = Fixtures.Snapshot(shortRemaining: 80, weeklyRemaining: 60);

        Assert.Equal(TimeSpan.Zero, snapshot.GeneratedAt.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.LastSuccessfulSyncAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.ShortWindow.ResetsAt.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.Weekly.ResetsAt.Offset);

        Assert.Equal(QuotaSourceStatus.Online, snapshot.Status);
        Assert.Equal(20d, snapshot.ShortWindow.UsedPercent);
        Assert.Equal(40d, snapshot.Weekly.UsedPercent);
        Assert.Equal(300, snapshot.ShortWindow.WindowMinutes);
        Assert.Equal(10080, snapshot.Weekly.WindowMinutes);

        Fixtures.EnsurePercentagesAreConsistent(
            snapshot.ShortWindow.UsedPercent,
            snapshot.ShortWindow.RemainingPercent);
        Fixtures.EnsurePercentagesAreConsistent(
            snapshot.Weekly.UsedPercent,
            snapshot.Weekly.RemainingPercent);
    }

    [Fact]
    public void Snapshot_AcceptsPercentageDriftWithinTolerance()
    {
        // A drift strictly inside the tolerance is accepted. The exact boundary value is not
        // asserted: 0.0001 is not representable in binary floating point, so the sum of the
        // pair rounds to just outside the tolerance and the comparison would be ill-conditioned.
        Fixtures.EnsurePercentagesAreConsistent(25d, 100d - 25d + (Fixtures.PercentTolerance / 2d));
    }

    [Fact]
    public void Snapshot_RejectsPercentagesThatDoNotSumToOneHundred()
    {
        Assert.Throws<InvalidOperationException>(
            () => Fixtures.BuildWindow(
                usedPercent: 25,
                remainingPercent: 80,
                windowMinutes: Fixtures.ShortWindowMinutes,
                resetsAt: DateTimeOffset.UnixEpoch));
    }
}
