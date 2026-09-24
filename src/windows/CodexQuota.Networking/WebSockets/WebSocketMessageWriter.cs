using System.Text;
using System.Text.Json;
using CodexQuota.Core.Quota;
using CodexQuota.Networking.Contracts.V1;
using CodexQuota.Networking.Hosting;

namespace CodexQuota.Networking.WebSockets;

/// <summary>
/// Serialises the v1 WebSocket frames.
/// </summary>
/// <remarks>
/// One place builds every frame, so the wire shape of <c>hello</c>, <c>quota.updated</c> and
/// <c>bridge.shutdown</c> cannot drift between the initial send and a broadcast.
/// </remarks>
internal static class WebSocketMessageWriter
{
    /// <summary>
    /// The first frame the server sends. It carries versions only, so a client learns the protocol
    /// before it has to interpret any state.
    /// </summary>
    internal static string Hello(BridgeEndpointOptions options)
        => JsonSerializer.Serialize(
            new WsHelloMessage(BridgeEndpointOptions.ApiVersion, options.BridgeVersion),
            V1Json.Options);

    /// <summary>
    /// A quota update: the complete normalised snapshot plus its sequence number. Never a delta,
    /// because a client that missed a frame must be able to resynchronise from any single one.
    /// </summary>
    internal static string QuotaUpdated(long sequence, QuotaSnapshot snapshot)
        => JsonSerializer.Serialize(
            new WsQuotaUpdatedMessage(sequence, V1ContractMapper.ToQuotaResponse(snapshot)),
            V1Json.Options);

    /// <summary>Sent before an orderly shutdown, so a drop is not mistaken for a network fault.</summary>
    internal static string Shutdown() => JsonSerializer.Serialize(new WsBridgeShutdownMessage(), V1Json.Options);
}
