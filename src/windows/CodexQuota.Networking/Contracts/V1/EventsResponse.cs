namespace CodexQuota.Networking.Contracts.V1;

/// <summary>
/// One user-meaningful event. Technical ping, cleanup and routine HTTP-success events never appear
/// here.
/// </summary>
public sealed record QuotaEventResponse(string Type, DateTimeOffset OccurredAt, string? Detail);

/// <summary><c>GET /api/v1/events?hours=24</c>.</summary>
public sealed record EventsResponse(int Hours, IReadOnlyList<QuotaEventResponse> Events);
