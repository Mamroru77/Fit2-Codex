namespace CodexQuota.Networking.Auth;

/// <summary>
/// The authenticated identity behind a request, once a device credential has been accepted.
/// </summary>
/// <param name="DeviceId">Non-secret identifier of the paired device.</param>
/// <param name="DisplayName">User-visible name of the device.</param>
public sealed record DevicePrincipal(string DeviceId, string DisplayName);
