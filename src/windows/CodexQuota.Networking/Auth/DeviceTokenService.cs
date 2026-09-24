using System.Security.Cryptography;
using CodexQuota.Networking.Pairing;
using CodexQuota.Storage.Devices;

namespace CodexQuota.Networking.Auth;

/// <summary>
/// Issues and validates device credentials.
/// </summary>
/// <remarks>
/// <para>
/// A credential is <c>base64url(deviceId || secret)</c>: 16 random bytes identifying the device,
/// followed by 32 random bytes of secret, so the whole thing is 256 bits of entropy. Carrying the
/// device id in the clear is what lets validation find the right record with one indexed lookup and
/// then compare the secret in constant time; the device id itself is not a secret.
/// </para>
/// <para>
/// Only the SHA-256 of the secret is stored. The plaintext credential exists exactly once, in the
/// response to the pairing completion that created it.
/// </para>
/// </remarks>
public sealed class DeviceTokenService
{
    private const int DeviceIdBytes = 16;
    private const int SecretBytes = 32;

    /// <summary>Total length of a well-formed credential.</summary>
    private const int CredentialBytes = DeviceIdBytes + SecretBytes;

    private readonly IPairedDeviceRepository _repository;

    public DeviceTokenService(IPairedDeviceRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <summary>Creates a device credential and records the device, storing only the hash.</summary>
    public async Task<DeviceCredential> IssueAsync(
        string displayName,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var deviceIdBytes = RandomNumberGenerator.GetBytes(DeviceIdBytes);
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);

        var deviceId = Base64Url.Encode(deviceIdBytes);
        var token = Base64Url.Encode([.. deviceIdBytes, .. secret]);

        await _repository
            .AppendAsync(new PairedDevice(deviceId, displayName, now, RevokedAt: null, Hash(secret)), cancellationToken)
            .ConfigureAwait(false);

        return new DeviceCredential(deviceId, token);
    }

    /// <summary>
    /// Resolves a bearer credential to its device, or returns <c>null</c> for anything that is
    /// malformed, unknown, revoked or simply wrong. Every failure looks the same from outside.
    /// </summary>
    public async Task<DevicePrincipal?> ValidateAsync(string bearerToken, CancellationToken cancellationToken)
    {
        if (!TrySplit(bearerToken, out var deviceId, out var secret))
        {
            return null;
        }

        var device = await _repository.FindByDeviceIdAsync(deviceId, cancellationToken).ConfigureAwait(false);

        if (device is null || device.RevokedAt is not null)
        {
            return null;
        }

        // Constant-time comparison: a wrong secret must not be distinguishable from a nearly-wrong
        // secret by how long the check took.
        if (!Matches(device.TokenHash, Hash(secret)))
        {
            return null;
        }

        return new DevicePrincipal(device.DeviceId, device.DisplayName);
    }

    /// <summary>Revokes a device. Its credential stops working immediately.</summary>
    public Task<bool> RevokeAsync(string deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        return _repository.RevokeAsync(deviceId, now, cancellationToken);
    }

    private static bool TrySplit(string? bearerToken, out string deviceId, out byte[] secret)
    {
        deviceId = string.Empty;
        secret = [];

        if (!Base64Url.TryDecode(bearerToken, out var decoded) || decoded.Length != CredentialBytes)
        {
            return false;
        }

        deviceId = Base64Url.Encode(decoded.AsSpan(0, DeviceIdBytes));
        secret = decoded[DeviceIdBytes..];
        return true;
    }

    private static string Hash(byte[] secret) => Convert.ToHexString(SHA256.HashData(secret));

    private static bool Matches(string storedHash, string presentedHash)
    {
        if (storedHash.Length != presentedHash.Length || storedHash.Length == 0)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(storedHash),
                Convert.FromHexString(presentedHash));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
