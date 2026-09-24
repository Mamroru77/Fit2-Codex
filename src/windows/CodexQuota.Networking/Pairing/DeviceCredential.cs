namespace CodexQuota.Networking.Pairing;

/// <summary>
/// The credential a paired device receives exactly once.
/// </summary>
/// <param name="DeviceId">Non-secret identifier of the device.</param>
/// <param name="Token">
/// The bearer credential. It is returned to the device once and never stored in plaintext anywhere,
/// so it exists in memory only for as long as it takes to hand it over.
/// </param>
public sealed record DeviceCredential(string DeviceId, string Token);

/// <summary>
/// Outcome of completing a pairing session.
/// </summary>
/// <param name="Succeeded">Whether a credential was issued.</param>
/// <param name="Credential">The credential, present only on success.</param>
/// <param name="ErrorCode">A stable v1 error code; empty on success.</param>
public sealed record PairingCompleteResult(bool Succeeded, DeviceCredential? Credential, string ErrorCode)
{
    /// <summary>A completed pairing.</summary>
    public static PairingCompleteResult Success(DeviceCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new PairingCompleteResult(true, credential, string.Empty);
    }

    /// <summary>A refused completion, with the stable code the client branches on.</summary>
    public static PairingCompleteResult Failure(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new PairingCompleteResult(false, null, errorCode);
    }
}
