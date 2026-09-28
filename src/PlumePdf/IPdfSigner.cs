using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PlumePdf;

/// <summary>
/// The seam a signing operation goes through: a digest-in,
/// <c>SignerInfo.signature</c>-out primitive that never requires the private key to leave
/// wherever it actually lives — an HSM, a cloud KMS (Azure Key Vault, AWS KMS), a PKCS#11
/// token, or, for the common case, a local <see cref="X509Certificate2"/> with an attached
/// private key (see <c>Objects.Signing.CertificateSigner</c>, the implementation
/// <c>PdfSignOptions</c>'s <see cref="X509Certificate2"/> convenience overload wraps
/// internally).
/// </summary>
/// <remarks>
/// <para>
/// This is the phase's one-way door: retrofitting an external-signer seam after the
/// public signing API ships would be a breaking change, so it is designed now even though the
/// certificate overload is the only door most callers use in Phase 5. Enterprise PAdES signing
/// is overwhelmingly HSM- or KMS-backed, so a caller who needs that can implement this
/// interface directly — <c>Objects.Signing.CertificateSigner</c> is the reference
/// implementation to model an adapter after.
/// </para>
/// <para>
/// PlumePDF never buffers a whole signed document in memory (an architectural promise that
/// predates this phase), so the digest handed to <see cref="SignAsync"/> is computed by
/// streaming through the document's <c>/ByteRange</c>-covered bytes rather than by handing
/// this seam the document itself — this interface only ever sees a small, fixed-size hash.
/// </para>
/// </remarks>
public interface IPdfSigner
{
    /// <summary>
    /// The signer's end-entity (leaf) certificate. Embedded in the CMS as the signing
    /// certificate and referenced by the ESS signing-certificate-v2 signed attribute
    /// (PAdES requirement) via its digest.
    /// </summary>
    X509Certificate2 Certificate { get; }

    /// <summary>
    /// Intermediate (and, optionally, root) certificates to embed in the CMS alongside
    /// <see cref="Certificate"/>, so a verifier can build the chain without a separate
    /// fetch. May be empty — an empty list is common when the verifier is expected to
    /// already hold the intermediates.
    /// </summary>
    IReadOnlyList<X509Certificate2> AdditionalCertificates { get; }

    /// <summary>
    /// The hash algorithm this signer signs over. <c>Objects.Signing.CmsSignatureBuilder</c>
    /// hashes the CMS signed-attributes set with this algorithm before calling
    /// <see cref="SignAsync"/> — implementations never see unhashed data.
    /// </summary>
    HashAlgorithmName DigestAlgorithm { get; }

    /// <summary>
    /// Signs <paramref name="digest"/> — already hashed with <see cref="DigestAlgorithm"/> —
    /// and returns the raw signature bytes exactly as they belong in a CMS
    /// <c>SignerInfo.signature</c> field.
    /// </summary>
    /// <param name="digest">
    /// The message digest to sign: the hash of the DER-encoded CMS signed-attributes set,
    /// computed with <see cref="DigestAlgorithm"/>. Never the document itself.
    /// </param>
    /// <param name="cancellationToken">Propagated to any I/O the signer performs (an HSM or KMS call is typically network I/O).</param>
    /// <returns>
    /// PKCS#1 v1.5 signature octets for an RSA <see cref="Certificate"/> (the length of the RSA
    /// modulus in bytes), or the DER-encoded <c>ECDSA-Sig-Value ::= SEQUENCE { r, s }</c> for an
    /// ECDSA <see cref="Certificate"/> — the format CMS's own <c>ECDSA-Sig-Value</c> requires
    /// (RFC 3279 §2.2.3) and the exact shape <c>SignerInfo.signature</c> must carry, i.e.
    /// <c>ECDsa.SignHash(digest, DSASignatureFormat.Rfc3279DerSequence)</c> for a local key —
    /// <em>not</em> the raw IEEE P1363 (r‖s) concatenation
    /// <c>ECDsa.SignHash(digest)</c> returns by default. Getting this wrong for an ECDSA signer
    /// produces a signature no verifier, including PlumePDF's own, will accept, with no error at
    /// signing time. <c>Objects.Signing.CertificateSigner</c> is the reference implementation.
    /// </returns>
    /// <example>
    /// <code>
    /// // A minimal adapter over an HSM/KMS client that exposes only "sign this hash":
    /// public sealed class KeyVaultPdfSigner : IPdfSigner
    /// {
    ///     public X509Certificate2 Certificate { get; }
    ///     public IReadOnlyList&lt;X509Certificate2&gt; AdditionalCertificates { get; } = [];
    ///     public HashAlgorithmName DigestAlgorithm => HashAlgorithmName.SHA256;
    ///
    ///     public KeyVaultPdfSigner(X509Certificate2 publicCertificate) =&gt; Certificate = publicCertificate;
    ///
    ///     public async Task&lt;byte[]&gt; SignAsync(byte[] digest, CancellationToken cancellationToken = default)
    ///     {
    ///         // Delegates the actual RSA/ECDSA operation to the remote key; the private key
    ///         // material never enters this process.
    ///         var result = await _keyClient.SignAsync(SignatureAlgorithm.RS256, digest, cancellationToken);
    ///         return result.Signature;
    ///     }
    /// }
    /// </code>
    /// </example>
    Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default);
}
