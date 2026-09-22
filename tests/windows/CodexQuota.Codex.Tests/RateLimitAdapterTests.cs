using CodexQuota.Codex.Quota;
using CodexQuota.Core.Quota;
using Xunit;

namespace CodexQuota.Codex.Tests;

public class RateLimitAdapterTests
{
    [Theory]
    [InlineData(25, 75)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void MapsShortWindowFromDuration(double used, double expectedRemaining)
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("short", used, 300, 1_800_000_000),
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

        var result = new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch);

        var success = Assert.IsType<RateLimitAdaptResult.Success>(result);
        Assert.Equal(expectedRemaining, success.Snapshot.ShortWindow.RemainingPercent);
    }

    [Fact]
    public void RejectsDuplicateShortWindowBucketsAsAmbiguous()
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("short-a", 10, 300, 1_800_000_000),
            RateLimitTestData.Bucket("short-b", 20, 300, 1_800_000_001),
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

        var result = new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch);

        Assert.IsType<RateLimitAdaptResult.Unsupported>(result);
    }

    [Fact]
    public void RejectsMissingWeeklyWindow()
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("short", 10, 300, 1_800_000_000));

        Assert.IsType<RateLimitAdaptResult.Unsupported>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RejectsPercentOutsideZeroToHundred()
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("short", 101, 300, 1_800_000_000),
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

        Assert.IsType<RateLimitAdaptResult.Unsupported>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RejectsMissingShortWindow()
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

        Assert.IsType<RateLimitAdaptResult.Unsupported>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RejectsDuplicateWeeklyWindowBucketsAsAmbiguous()
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("weekly-a", 40, 10080, 1_800_100_000),
            RateLimitTestData.Bucket("weekly-b", 41, 10080, 1_800_100_001),
            RateLimitTestData.Bucket("short", 25, 300, 1_800_000_000));

        Assert.IsType<RateLimitAdaptResult.Unsupported>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RejectsBucketWithMissingUsedPercent()
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("short", null, 300, 1_800_000_000),
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

        Assert.IsType<RateLimitAdaptResult.Unsupported>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void RejectsBucketWithMissingResetsAt()
    {
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("short", 25, 300, null),
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

        Assert.IsType<RateLimitAdaptResult.Unsupported>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void MapsWindowsPlacedOnlyInRateLimitsByLimitId()
    {
        var source = RateLimitTestData.ReadResultByLimitId(
            RateLimitTestData.Bucket("short", 25, 300, 1_800_000_000),
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000));

        var success = Assert.IsType<RateLimitAdaptResult.Success>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));

        Assert.Equal(75d, success.Snapshot.ShortWindow.RemainingPercent);
        Assert.Equal(60d, success.Snapshot.Weekly.RemainingPercent);
    }

    [Fact]
    public void MapsWindowsRegardlessOfPrimaryAndSecondaryPosition()
    {
        // Weekly in primary, short in secondary: mapping must use window metadata, not field order.
        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000),
            RateLimitTestData.Bucket("short", 25, 300, 1_800_000_000));

        var success = Assert.IsType<RateLimitAdaptResult.Success>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));

        Assert.Equal(300, success.Snapshot.ShortWindow.WindowMinutes);
        Assert.Equal(75d, success.Snapshot.ShortWindow.RemainingPercent);
        Assert.Equal(10080, success.Snapshot.Weekly.WindowMinutes);
        Assert.Equal(60d, success.Snapshot.Weekly.RemainingPercent);
    }

    [Fact]
    public void TreatsEqualBucketAppearingTwiceAsOneCandidate()
    {
        var source = RateLimitTestData.ReadResultWithByLimitId(
            primary: RateLimitTestData.Bucket("short", 25, 300, 1_800_000_000),
            secondary: null,
            byLimitId: new[]
            {
                RateLimitTestData.Bucket("short", 25, 300, 1_800_000_000),
                RateLimitTestData.Bucket("weekly", 40, 10080, 1_800_100_000),
            });

        Assert.IsType<RateLimitAdaptResult.Success>(
            new RateLimitAdapter().TryAdapt(source, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void MapsSnapshotMetadataAndUtcResetTimes()
    {
        var receivedAt = new DateTimeOffset(2026, 9, 22, 13, 30, 0, TimeSpan.Zero);

        var source = RateLimitTestData.ReadResult(
            RateLimitTestData.Bucket("short", 28, 300, 1_800_000_000),
            RateLimitTestData.Bucket("weekly", 46, 10080, 1_800_100_000));

        var success = Assert.IsType<RateLimitAdaptResult.Success>(
            new RateLimitAdapter().TryAdapt(source, receivedAt));

        var snapshot = success.Snapshot;

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal("codex_app_server", snapshot.Source);
        Assert.Equal(QuotaSourceStatus.Online, snapshot.Status);
        Assert.Equal(receivedAt, snapshot.GeneratedAt);
        Assert.Equal(receivedAt, snapshot.LastSuccessfulSyncAt);

        Assert.Equal(300, snapshot.ShortWindow.WindowMinutes);
        Assert.Equal(10080, snapshot.Weekly.WindowMinutes);
        Assert.Equal(72d, snapshot.ShortWindow.RemainingPercent);
        Assert.Equal(54d, snapshot.Weekly.RemainingPercent);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), snapshot.ShortWindow.ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_100_000), snapshot.Weekly.ResetsAt);
        Assert.Equal(TimeSpan.Zero, snapshot.ShortWindow.ResetsAt.Offset);
        Assert.Equal(TimeSpan.Zero, snapshot.Weekly.ResetsAt.Offset);
    }
}
