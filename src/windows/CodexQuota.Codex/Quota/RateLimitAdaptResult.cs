using CodexQuota.Core.Quota;

namespace CodexQuota.Codex.Quota;

/// <summary>
/// Outcome of adapting a Codex App Server rate-limit payload into a canonical snapshot.
/// Callers must keep the previously trusted snapshot when the result is
/// <see cref="Unsupported"/>.
/// </summary>
public abstract record RateLimitAdaptResult
{
    /// <summary>A payload whose required windows could be identified unambiguously.</summary>
    public sealed record Success(QuotaSnapshot Snapshot) : RateLimitAdaptResult;

    /// <summary>A payload that must not be guessed at, with a non-secret explanation.</summary>
    public sealed record Unsupported(string Reason) : RateLimitAdaptResult;
}
