using System.Security.Cryptography.X509Certificates;

namespace CodexQuota.Networking.Security;

/// <summary>
/// The stable trust anchor of one Bridge installation.
/// </summary>
/// <remarks>
/// <para>
/// The identity is what Android pins. It is created once, on first Bridge start, and then reused
/// forever: a new DHCP address, a new network interface or a new leaf certificate must never change
/// it, because changing it is exactly what makes a client refuse to connect.
/// </para>
/// <para>
/// <see cref="Certificate"/> may carry the private key and is therefore only for local use, such as
/// signing a leaf. <see cref="PublicCertificate"/> is the material that may be persisted, shown or
/// transmitted.
/// </para>
/// </remarks>
public sealed class BridgeIdentity : IDisposable
{
    private readonly X509Certificate2 _publicCertificate;

    public BridgeIdentity(string bridgeId, X509Certificate2 certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgeId);
        ArgumentNullException.ThrowIfNull(certificate);

        BridgeId = bridgeId;
        Certificate = certificate;
        _publicCertificate = new X509Certificate2(certificate.Export(X509ContentType.Cert));
        SpkiSha256 = CertificateFingerprint.ComputeSpkiSha256(certificate);
        VerificationCode = CertificateFingerprint.ToHumanVerificationCode(SpkiSha256);
    }

    /// <summary>Non-personal, stable identifier of this Bridge installation.</summary>
    public string BridgeId { get; }

    /// <summary>The identity certificate, with its private key when this process created it.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The identity certificate without any private key. Safe to persist and to show.</summary>
    public X509Certificate2 PublicCertificate => _publicCertificate;

    /// <summary>The pinned fingerprint: SHA-256 over the identity's public key.</summary>
    public string SpkiSha256 { get; }

    /// <summary>The human comparison code a person reads off both screens.</summary>
    public string VerificationCode { get; }

    /// <summary>
    /// The stable DNS name this Bridge advertises. It is derived from the Bridge id, so it follows
    /// the identity rather than the address.
    /// </summary>
    public string DnsName => $"codexquota-{ShortBridgeId}.local";

    /// <summary>A short, stable, non-personal form of the Bridge id for use in names.</summary>
    public string ShortBridgeId => BridgeId.Length <= 8 ? BridgeId : BridgeId[..8];

    public void Dispose()
    {
        _publicCertificate.Dispose();
        Certificate.Dispose();
    }
}
