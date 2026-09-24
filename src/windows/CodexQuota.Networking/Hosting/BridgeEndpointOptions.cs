using System.Net;

namespace CodexQuota.Networking.Hosting;

/// <summary>
/// Where the Bridge API listens and what it reports about itself.
/// </summary>
/// <param name="Address">
/// The single address to bind. It is deliberately one explicit address rather than "any": the
/// Bridge must never end up listening on a public, VPN or virtual adapter by accident.
/// </param>
/// <param name="Port">TCP port. <c>0</c> asks the OS for an ephemeral port, which tests use.</param>
/// <param name="BridgeVersion">Application version reported by <c>/info</c> and the WebSocket hello.</param>
public sealed record BridgeEndpointOptions(IPAddress Address, int Port, string BridgeVersion)
{
    /// <summary>The API path version. It is independent of the application and schema versions.</summary>
    public const string ApiVersion = "v1";

    /// <summary>The default port the Bridge tries first.</summary>
    public const int DefaultPort = 47821;

    /// <summary>Options for the loopback address, used by tests and by the local health check.</summary>
    public static BridgeEndpointOptions Loopback(string bridgeVersion)
        => new(IPAddress.Loopback, 0, bridgeVersion);
}
