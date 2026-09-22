using CodexQuota.Codex.Protocol;
using CodexQuota.Core.Quota;

namespace CodexQuota.Codex.Quota;

/// <summary>
/// The only layer that knows the Codex rate-limit schema. Required windows are identified from
/// explicit window metadata, never from field position, and a payload that cannot be identified
/// unambiguously is rejected instead of guessed at.
/// </summary>
public sealed class RateLimitAdapter
{
    private const int SchemaVersion = 1;
    private const string Source = "codex_app_server";
    private const int ShortWindowMinutes = 300;
    private const int WeeklyWindowMinutes = 10080;

    public RateLimitAdaptResult TryAdapt(RateLimitsReadResult source, DateTimeOffset receivedAt)
    {
        var candidates = Flatten(source);

        var shortCandidates = candidates
            .Where(candidate => candidate.WindowDurationMins == ShortWindowMinutes)
            .ToArray();

        if (shortCandidates.Length != 1)
        {
            return new RateLimitAdaptResult.Unsupported(
                $"Expected exactly one {ShortWindowMinutes}-minute window but found {shortCandidates.Length}.");
        }

        var weeklyCandidates = candidates
            .Where(candidate => candidate.WindowDurationMins == WeeklyWindowMinutes)
            .ToArray();

        if (weeklyCandidates.Length != 1)
        {
            return new RateLimitAdaptResult.Unsupported(
                $"Expected exactly one {WeeklyWindowMinutes}-minute window but found {weeklyCandidates.Length}.");
        }

        var (shortWindow, shortReason) = BuildWindow(shortCandidates[0], ShortWindowMinutes);
        if (shortWindow is null)
        {
            return new RateLimitAdaptResult.Unsupported(shortReason);
        }

        var (weeklyWindow, weeklyReason) = BuildWindow(weeklyCandidates[0], WeeklyWindowMinutes);
        if (weeklyWindow is null)
        {
            return new RateLimitAdaptResult.Unsupported(weeklyReason);
        }

        var snapshot = new QuotaSnapshot(
            SchemaVersion,
            receivedAt,
            Source,
            QuotaSourceStatus.Online,
            receivedAt,
            shortWindow,
            weeklyWindow);

        return new RateLimitAdaptResult.Success(snapshot);
    }

    /// <summary>
    /// Flattens every reported bucket into candidates. The same logical bucket can be reported
    /// through more than one field; <see cref="SourceRateLimitBucket"/> is a record, so value
    /// equality collapses those repeats into a single candidate.
    /// </summary>
    private static List<SourceRateLimitBucket> Flatten(RateLimitsReadResult source)
    {
        var buckets = new List<SourceRateLimitBucket>();

        if (source.Primary is not null)
        {
            buckets.Add(source.Primary);
        }

        if (source.Secondary is not null)
        {
            buckets.Add(source.Secondary);
        }

        if (source.RateLimitsByLimitId is not null)
        {
            foreach (var bucket in source.RateLimitsByLimitId.Values)
            {
                if (bucket is not null)
                {
                    buckets.Add(bucket);
                }
            }
        }

        return buckets.Distinct().ToList();
    }

    private static (QuotaWindow? Window, string Reason) BuildWindow(
        SourceRateLimitBucket bucket,
        int windowMinutes)
    {
        if (bucket.UsedPercent is not { } usedPercent)
        {
            return (null, $"The {windowMinutes}-minute window is missing usedPercent.");
        }

        // Written as a negated range test so that NaN also fails validation.
        if (!(usedPercent >= 0d && usedPercent <= 100d))
        {
            return (null, $"The {windowMinutes}-minute window reports usedPercent {usedPercent}, outside 0-100.");
        }

        if (bucket.ResetsAt is not { } resetsAt)
        {
            return (null, $"The {windowMinutes}-minute window is missing resetsAt.");
        }

        var window = new QuotaWindow(
            usedPercent,
            100d - usedPercent,
            windowMinutes,
            DateTimeOffset.FromUnixTimeSeconds(resetsAt));

        return (window, string.Empty);
    }
}
