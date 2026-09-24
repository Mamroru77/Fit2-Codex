namespace CodexQuota.Networking.Contracts.V1;

/// <summary>The WebSocket message type discriminators.</summary>
public static class WsMessageTypes
{
    /// <summary>First frame the server sends, so a client learns the protocol version.</summary>
    public const string Hello = "hello";

    /// <summary>A complete quota snapshot plus its sequence number.</summary>
    public const string QuotaUpdated = "quota.updated";

    /// <summary>Sent before an orderly Bridge shutdown, when the connection still allows it.</summary>
    public const string BridgeShutdown = "bridge.shutdown";
}

/// <summary>
/// The first server frame. It carries version information only: the application version, the API
/// path version and the JSON schema version stay independent.
/// </summary>
public sealed record WsHelloMessage(string ApiVersion, string BridgeVersion)
{
    public string Type => WsMessageTypes.Hello;
}

/// <summary>
/// A quota state update. The payload is the complete normalised snapshot, and
/// <see cref="Sequence"/> increases monotonically for the lifetime of one Bridge runtime so a
/// client can detect a gap and reconcile over REST instead of replaying.
/// </summary>
public sealed record WsQuotaUpdatedMessage(long Sequence, QuotaResponse Payload)
{
    public string Type => WsMessageTypes.QuotaUpdated;
}

/// <summary>Sent before an orderly shutdown so clients can show "Bridge stopped" rather than a drop.</summary>
public sealed record WsBridgeShutdownMessage
{
    public string Type => WsMessageTypes.BridgeShutdown;
}
