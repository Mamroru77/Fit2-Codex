namespace CodexQuota.Codex.Protocol;

/// <summary>
/// Source shape of the App Server <c>account/rateLimits/read</c> result. Every field is
/// nullable on purpose: missing or unexpected data must be rejected, never defaulted to
/// 0% or 100%.
/// </summary>
/// <param name="Primary">The primary bucket as reported by the source, if present.</param>
/// <param name="Secondary">The secondary bucket as reported by the source, if present.</param>
/// <param name="RateLimitsByLimitId">Optional multi-limit view keyed by limit identifier.</param>
public sealed record RateLimitsReadResult(
    SourceRateLimitBucket? Primary,
    SourceRateLimitBucket? Secondary,
    IReadOnlyDictionary<string, SourceRateLimitBucket>? RateLimitsByLimitId);

/// <summary>
/// One rate-limit bucket as reported by the App Server. The bucket's field position carries
/// no meaning; only <see cref="WindowDurationMins"/> identifies which window it is.
/// </summary>
/// <param name="LimitId">Source limit identifier, when reported.</param>
/// <param name="UsedPercent">Percentage consumed, when reported.</param>
/// <param name="WindowDurationMins">Length of the window in minutes, when reported.</param>
/// <param name="ResetsAt">Unix epoch seconds at which the window resets, when reported.</param>
public sealed record SourceRateLimitBucket(
    string? LimitId,
    double? UsedPercent,
    int? WindowDurationMins,
    long? ResetsAt);
