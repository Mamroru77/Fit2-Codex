using CodexQuota.Codex.Protocol;

namespace CodexQuota.Codex.Tests;

/// <summary>
/// Test-only builder for Codex App Server rate-limit payloads. Buckets can be placed in
/// <c>primary</c>, <c>secondary</c> or <c>rateLimitsByLimitId</c> so that mapping tests
/// cannot accidentally depend on a field position.
/// </summary>
internal static class RateLimitTestData
{
    internal const int ShortWindowMinutes = 300;
    internal const int WeeklyWindowMinutes = 10080;
    internal const long ShortResetsAt = 1_800_000_000;
    internal const long WeeklyResetsAt = 1_800_100_000;

    internal static SourceRateLimitBucket Bucket(
        string? limitId,
        double? usedPercent,
        int? windowDurationMins,
        long? resetsAt)
        => new(limitId, usedPercent, windowDurationMins, resetsAt);

    /// <summary>Fills <c>primary</c>, then <c>secondary</c>, then <c>rateLimitsByLimitId</c> in order.</summary>
    internal static RateLimitsReadResult ReadResult(params SourceRateLimitBucket[] buckets)
    {
        var primary = buckets.Length > 0 ? buckets[0] : null;
        var secondary = buckets.Length > 1 ? buckets[1] : null;
        var byLimitId = buckets.Length > 2 ? ToByLimitId(buckets.Skip(2)) : null;

        return new RateLimitsReadResult(primary, secondary, byLimitId);
    }

    /// <summary>Places every bucket in <c>rateLimitsByLimitId</c> only.</summary>
    internal static RateLimitsReadResult ReadResultByLimitId(params SourceRateLimitBucket[] buckets)
        => new(null, null, ToByLimitId(buckets));

    /// <summary>Places buckets explicitly so the same logical bucket can appear in two fields.</summary>
    internal static RateLimitsReadResult ReadResultWithByLimitId(
        SourceRateLimitBucket? primary,
        SourceRateLimitBucket? secondary,
        params SourceRateLimitBucket[] byLimitId)
        => new(primary, secondary, byLimitId.Length > 0 ? ToByLimitId(byLimitId) : null);

    private static IReadOnlyDictionary<string, SourceRateLimitBucket> ToByLimitId(
        IEnumerable<SourceRateLimitBucket> buckets)
        => buckets
            .Select((bucket, index) => new { Key = bucket.LimitId ?? $"limit-{index}", Bucket = bucket })
            .ToDictionary(entry => entry.Key, entry => entry.Bucket);
}
