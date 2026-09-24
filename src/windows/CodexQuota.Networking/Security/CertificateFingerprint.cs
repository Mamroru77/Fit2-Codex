using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodexQuota.Networking.Security;

/// <summary>
/// Formats certificate fingerprints for pinning and for human comparison.
/// </summary>
/// <remarks>
/// The pinned value is the SHA-256 of the certificate's <c>SubjectPublicKeyInfo</c>, not of the
/// certificate itself. That is deliberate: the SPKI is stable across re-issuance of the same key,
/// so a renewed leaf or a renewed identity certificate with the same key keeps the same pin.
/// </remarks>
public static class CertificateFingerprint
{
    /// <summary>Number of hex characters that make up the human comparison code.</summary>
    public const int VerificationCodeLength = 16;

    /// <summary>
    /// SHA-256 over the certificate's <c>SubjectPublicKeyInfo</c>, as uppercase hex.
    /// </summary>
    public static string ComputeSpkiSha256(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var subjectPublicKeyInfo = certificate.PublicKey.ExportSubjectPublicKeyInfo();

        return Convert.ToHexString(SHA256.HashData(subjectPublicKeyInfo));
    }

    /// <summary>
    /// Turns the fingerprint prefix into the <c>A1B2-C3D4-E5F6-7890</c> form a person reads aloud.
    /// </summary>
    public static string ToHumanVerificationCode(string spkiSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spkiSha256);

        if (spkiSha256.Length < VerificationCodeLength)
        {
            throw new ArgumentException(
                $"A fingerprint needs at least {VerificationCodeLength} hex characters.",
                nameof(spkiSha256));
        }

        var prefix = spkiSha256[..VerificationCodeLength].ToUpperInvariant();

        return string.Join(
            '-',
            Enumerable.Range(0, VerificationCodeLength / 4).Select(group => prefix.Substring(group * 4, 4)));
    }
}
