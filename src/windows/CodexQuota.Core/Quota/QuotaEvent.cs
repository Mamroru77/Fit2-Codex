namespace CodexQuota.Core.Quota;

/// <summary>
/// A user-meaningful quota or Bridge event. Technical ping, storage cleanup and
/// routine transport successes are deliberately not part of this model.
/// </summary>
/// <param name="Type">The kind of event.</param>
/// <param name="OccurredAt">UTC instant at which the event occurred.</param>
/// <param name="Detail">Optional non-secret context for display.</param>
public record QuotaEvent(
    QuotaEventType Type,
    DateTimeOffset OccurredAt,
    string? Detail = null);

/// <summary>
/// Event types that are meaningful to the user.
/// </summary>
public enum QuotaEventType
{
    QuotaChanged,
    WindowReset,
    BridgeStarted,
    BridgeStopped,
    CodexConnected,
    CodexDisconnected,
    AuthRequired,
    SourceError,
}
