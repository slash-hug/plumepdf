using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PlumePdf.Objects.Signing;

/// <summary>
/// The <see cref="X509Certificate2"/>-backed <see cref="IPdfSigner"/> — the "convenience door":
/// <c>PdfSignOptions</c>'s certificate overload wraps this
/// internally so the common case (a certificate with a locally-usable private key, e.g. loaded
/// from a PFX) never needs a caller to implement <see cref="IPdfSigner"/> themselves. Mirrors
/// <c>StandardSecurityHandler</c>'s status: an internal seam implementation behind a public
/// interface.
/// </summary>
internal sealed class CertificateSigner : IPdfSigner
{
    /// <inheritdoc/>
    public X509Certificate2 Certificate { get; }

    /// <inheritdoc/>
    public IReadOnlyList<X509Certificate2> AdditionalCertificates { get; }

    /// <inheritdoc/>
    public HashAlgorithmName DigestAlgorithm { get; }

    /// <summary>
    /// Wraps <paramref name="certificate"/> — which must carry a usable private key — as an
    /// <see cref="IPdfSigner"/>.
    /// </summary>
    /// <param name="certificate">An RSA- or ECDSA-keyed certificate with an attached private key (<see cref="X509Certificate2.HasPrivateKey"/>).</param>
    /// <param name="digestAlgorithm">The hash algorithm to sign over. Defaults to SHA-256 (PAdES/CAdES baseline).</param>
    /// <param name="additionalCertificates">Intermediate/root certificates to embed alongside <paramref name="certificate"/>. Defaults to none.</param>
    /// <exception cref="PlumePdfException">
    /// <paramref name="certificate"/> has no private key, or its key algorithm is neither RSA nor ECDSA (<c>PLUME4007</c>).
    /// </exception>
    public CertificateSigner(X509Certificate2 certificate, HashAlgorithmName? digestAlgorithm = null, IReadOnlyList<X509Certificate2>? additionalCertificates = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (!certificate.HasPrivateKey)
        {
            throw new PlumePdfException("PLUME4007", $"Certificate '{certificate.Subject}' has no private key — CertificateSigner requires one to sign locally. For a remote/HSM-backed key, implement IPdfSigner directly instead.");
        }

        using (var rsaProbe = certificate.GetRSAPrivateKey())
        using (var ecdsaProbe = certificate.GetECDsaPrivateKey())
        {
            if (rsaProbe is null && ecdsaProbe is null)
            {
                throw new PlumePdfException("PLUME4007", $"Certificate '{certificate.Subject}' carries a private key of an unsupported algorithm — only RSA and ECDSA are supported.");
            }
        }

        Certificate = certificate;
        DigestAlgorithm = digestAlgorithm ?? HashAlgorithmName.SHA256;
        AdditionalCertificates = additionalCertificates ?? [];
    }

    /// <inheritdoc/>
    /// <exception cref="PlumePdfException">The certificate's private key algorithm is neither RSA nor ECDSA (<c>PLUME4007</c>) — guarded against at construction, so this should be unreachable in practice.</exception>
    public Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digest);
        cancellationToken.ThrowIfCancellationRequested();

        using (var rsa = Certificate.GetRSAPrivateKey())
        {
            if (rsa is not null)
            {
                // PKCS#1 v1.5 padding (RSA-PSS is a later-phase extension, refused under
                // Deterministic signing until it exists at all).
                return Task.FromResult(rsa.SignHash(digest, DigestAlgorithm, RSASignaturePadding.Pkcs1));
            }
        }

        using (var ecdsa = Certificate.GetECDsaPrivateKey())
        {
            if (ecdsa is not null)
            {
                // DER SEQUENCE{r, s} — the format CMS's ECDSA-Sig-Value requires and the exact
                // shape SignerInfo.signature must carry (verified empirically against
                // System.Security.Cryptography.Pkcs's own CmsSigner output).
                return Task.FromResult(ecdsa.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence));
            }
        }

        throw new PlumePdfException("PLUME4007", $"Certificate '{Certificate.Subject}' carries a private key of an unsupported algorithm — only RSA and ECDSA are supported.");
    }
}
