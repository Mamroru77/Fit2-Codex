namespace CodexQuota.Core.Quota;

/// <summary>
/// High-level availability of the quota source. Bridge reachability and source
/// availability are distinct concerns; an online Bridge can still report
/// <see cref="AuthRequired"/>.
/// </summary>
public enum QuotaSourceStatus
{
    /// <summary>Source data is fresh and trusted.</summary>
    Online,

    /// <summary>Last trusted data is being shown but is no longer fresh.</summary>
    Stale,

    /// <summary>No trusted data is available.</summary>
    Unavailable,

    /// <summary>Codex authentication is required.</summary>
    AuthRequired,

    /// <summary>The source reported an error.</summary>
    SourceError,

    /// <summary>The source schema could not be interpreted safely.</summary>
    SourceSchemaUnsupported,
}
