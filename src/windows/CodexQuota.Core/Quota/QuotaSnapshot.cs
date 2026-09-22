namespace CodexQuota.Core.Quota;

/// <summary>
/// The complete normalized quota state published by the Bridge. Timestamps are UTC.
/// </summary>
/// <param name="SchemaVersion">Version of the published JSON schema.</param>
/// <param name="GeneratedAt">UTC instant at which this snapshot was produced.</param>
/// <param name="Source">Identifier of the quota source.</param>
/// <param name="Status">Availability of the quota source.</param>
/// <param name="LastSuccessfulSyncAt">UTC instant of the last successful source read, if any.</param>
/// <param name="ShortWindow">The short (300-minute) window.</param>
/// <param name="Weekly">The weekly (10080-minute) window.</param>
public record QuotaSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string Source,
    QuotaSourceStatus Status,
    DateTimeOffset? LastSuccessfulSyncAt,
    QuotaWindow ShortWindow,
    QuotaWindow Weekly);
