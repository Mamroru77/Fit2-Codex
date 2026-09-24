using System.Text.Json;
using CodexQuota.Networking.Contracts.V1;
using CodexQuota.Networking.Hosting;

namespace CodexQuota.Networking.Discovery;

/// <summary>
/// The payload a pairing QR code carries.
/// </summary>
/// <param name="V">API path version, so the phone can refuse a Bridge it cannot talk to.</param>
/// <param name="BridgeId">Non-personal Bridge identifier.</param>
/// <param name="Host">Address the phone should connect to.</param>
/// <param name="Port">TCP port.</param>
/// <param name="PairingId">The one-time pairing session this QR code was generated for.</param>
/// <param name="IdentityFingerprint">
/// The Bridge identity pin. It is public by design — it is what the phone compares against — and it
/// is the reason a QR code can bootstrap trust without carrying any secret.
/// </param>
/// <remarks>
/// A QR code is displayed on a screen, photographed by phones and often left in a photo library, so
/// it is treated as public. It therefore carries no device credential, no token, and nothing that
/// would let whoever reads it authenticate as a paired device. The credential is only ever issued
/// after the user approves the session on Windows.
/// </remarks>
public sealed record PairingQrPayload(
    string V,
    string BridgeId,
    string Host,
    int Port,
    string PairingId,
    string IdentityFingerprint)
{
    /// <summary>The only keys a v1 pairing QR payload may contain.</summary>
    public static readonly IReadOnlyList<string> Keys =
        ["v", "bridgeId", "host", "port", "pairingId", "identityFingerprint"];

    /// <summary>Serialises the payload for the QR renderer.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, V1Json.Options);

    /// <summary>Creates a payload from a pairing session and the endpoint the phone should use.</summary>
    public static PairingQrPayload Create(
        string host,
        int port,
        string pairingId,
        string bridgeId,
        string identityFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityFingerprint);

        return new PairingQrPayload(
            BridgeEndpointOptions.ApiVersion,
            bridgeId,
            host,
            port,
            pairingId,
            identityFingerprint);
    }
}
