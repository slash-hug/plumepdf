using System.Security.Cryptography.X509Certificates;

namespace PlumePdf;

/// <summary>The cryptographic verdict a <see cref="SignatureVerificationResult"/> reaches over a signature's CMS bytes.</summary>
public enum SignatureCryptographicStatus
{
    /// <summary>The message digest matches the document's actual bytes and the signature over it verifies against the embedded certificate.</summary>
    Valid,

    /// <summary>The signed message-digest attribute does not match the document's actual <c>/ByteRange</c>-covered bytes — the signed content has been altered since signing.</summary>
    DigestMismatch,

    /// <summary>The digest matched, but the cryptographic signature does not verify against the embedded certificate.</summary>
    SignatureInvalid,

    /// <summary>The CMS bytes could not be decoded, or lack a structure PlumePDF requires (single <c>SignerInfo</c>, embedded signing certificate, message-digest attribute).</summary>
    Malformed,

    /// <summary>The signature declares an algorithm PlumePDF does not implement (only RSA PKCS#1 v1.5 and ECDSA — no RSASSA-PSS this phase).</summary>
    UnsupportedAlgorithm,

    /// <summary>An ESS signing-certificate-v2 attribute is present but names a different certificate than the one embedded — a substitution attempt.</summary>
    SigningCertificateMismatch,
}

/// <summary>The certificate-chain verdict a <see cref="SignatureVerificationResult"/> reaches, when a trust anchor set was supplied to <see cref="PdfSignature.Verify"/>.</summary>
public enum SignatureChainStatus
{
    /// <summary>No trust anchors were supplied — chain trust was not evaluated.</summary>
    NotEvaluated,

    /// <summary>The chain builds to one of the supplied trusted roots with no status flags.</summary>
    Trusted,

    /// <summary>The chain builds, but its root is not among the supplied trust anchors.</summary>
    UntrustedRoot,

    /// <summary>At least one certificate in the chain is expired or not yet valid at the signing time.</summary>
    NotTimeValid,

    /// <summary>At least one certificate in the chain is revoked, per embedded <c>/DSS</c> revocation material.</summary>
    Revoked,

    /// <summary>No path to any root could be built — an intermediate or root is missing.</summary>
    PartialChain,

    /// <summary>The chain failed to build for a reason not covered by the other statuses.</summary>
    Error,
}

/// <summary>
/// The result of <see cref="PdfSignature.Verify"/>: a signature's cryptographic verdict, its
/// <c>/ByteRange</c> coverage verdict, and (when trust anchors were supplied) its certificate
/// chain verdict — every property is populated on every call, since ByteRange coverage is a
/// first-class, un-hideable property of verification (the shadow-attack literature),
/// never a buried diagnostic a caller could accidentally skip reading.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("signed.pdf");
/// var result = document.Signatures[0].Verify();
/// if (result.IsValid)
/// {
///     Console.WriteLine($"Signed by {result.SigningCertificate?.Subject} at {result.SigningTime}");
/// }
/// else
/// {
///     Console.WriteLine($"{result.CryptographicStatus}: {result.Detail}");
/// }
/// </code>
/// </example>
public sealed class SignatureVerificationResult
{
    internal SignatureVerificationResult(
        SignatureCryptographicStatus cryptographicStatus,
        bool coversWholeDocument,
        SignatureChainStatus chainStatus,
        X509Certificate2? signingCertificate,
        IReadOnlyList<X509Certificate2> certificates,
        DateTimeOffset? signingTime,
        bool hasTimestamp,
        DateTimeOffset? timestampTime,
        bool isTimestampTrusted,
        string? detail)
    {
        CryptographicStatus = cryptographicStatus;
        CoversWholeDocument = coversWholeDocument;
        ChainStatus = chainStatus;
        SigningCertificate = signingCertificate;
        Certificates = certificates;
        SigningTime = signingTime;
        HasTimestamp = hasTimestamp;
        TimestampTime = timestampTime;
        IsTimestampTrusted = isTimestampTrusted;
        Detail = detail;
    }

    /// <summary>The cryptographic verdict over the signature's CMS bytes.</summary>
    public SignatureCryptographicStatus CryptographicStatus { get; }

    /// <summary>
    /// Whether this signature's <c>/ByteRange</c> covers the entire file (only the
    /// <c>/Contents</c> hex string itself excluded) — a structural, un-hideable property
    /// checked independently of <see cref="CryptographicStatus"/>: a signature can be
    /// cryptographically valid over the bytes it covers while still leaving part of the file
    /// (e.g. appended pages) outside its protection. Always populated, never buried behind a
    /// diagnostic a caller could skip.
    /// </summary>
    public bool CoversWholeDocument { get; }

    /// <summary>
    /// The certificate-chain trust verdict, or <see cref="SignatureChainStatus.NotEvaluated"/>
    /// when <see cref="PdfSignature.Verify"/> was called with no trust anchors — PlumePDF never
    /// silently falls back to the OS trust store (the offline-by-default posture applied to
    /// verification too; a caller who wants OS-trust-store validation passes it explicitly).
    /// </summary>
    public SignatureChainStatus ChainStatus { get; }

    /// <summary>The certificate the CMS names as its signer, when one could be identified.</summary>
    public X509Certificate2? SigningCertificate { get; }

    /// <summary>Every certificate embedded in the CMS (<see cref="SigningCertificate"/> included).</summary>
    public IReadOnlyList<X509Certificate2> Certificates { get; }

    /// <summary>The claimed signing time — the signature dictionary's <c>/M</c> entry — or <see langword="null"/> when absent/unparseable.</summary>
    public DateTimeOffset? SigningTime { get; }

    /// <summary>Whether an RFC 3161 signature timestamp (PAdES B-T or later) is present.</summary>
    public bool HasTimestamp { get; }

    /// <summary>
    /// The RFC 3161 timestamp's claimed time, when <see cref="HasTimestamp"/> is
    /// <see langword="true"/> and the token decoded successfully. This value comes from a CMS
    /// unsigned attribute (RFC 5652 §11) — outside the signature's own protection — and is
    /// <em>not</em> proven merely by being present; check <see cref="IsTimestampTrusted"/>
    /// before relying on it. PlumePDF never uses an untrusted <see cref="TimestampTime"/> to
    /// decide <see cref="ChainStatus"/>.
    /// </summary>
    public DateTimeOffset? TimestampTime { get; }

    /// <summary>
    /// Whether <see cref="TimestampTime"/> has actually been proven: the timestamp token's own
    /// signature verifies against the CMS's <c>SignerInfo.signature</c> bytes it covers, and its
    /// TSA certificate chain-builds to one of the trust anchors supplied to
    /// <see cref="PdfSignature.Verify"/>. Always <see langword="false"/> when no trust anchors
    /// were supplied (chain trust, like <see cref="ChainStatus"/>, is opt-in) or when
    /// <see cref="HasTimestamp"/> is <see langword="false"/>.
    /// </summary>
    public bool IsTimestampTrusted { get; }

    /// <summary>A human-readable explanation, populated for any non-<see cref="SignatureCryptographicStatus.Valid"/> outcome or a non-<see cref="SignatureChainStatus.Trusted"/>/<see cref="SignatureChainStatus.NotEvaluated"/> chain status.</summary>
    public string? Detail { get; }

    /// <summary>
    /// Shorthand for the common case: the signature is cryptographically valid, covers the
    /// whole document, and — when trust anchors were supplied — its certificate chain came back
    /// <see cref="SignatureChainStatus.Trusted"/>. Chain trust stays opt-in: with no trust
    /// anchors supplied (<see cref="SignatureChainStatus.NotEvaluated"/>) this property does not
    /// judge trust at all; but once the caller <em>did</em> ask for chain evaluation, a
    /// <see cref="SignatureChainStatus.Revoked"/>/<see cref="SignatureChainStatus.UntrustedRoot"/>/
    /// <see cref="SignatureChainStatus.NotTimeValid"/> chain makes this <see langword="false"/>
    /// rather than silently passing. Check <see cref="ChainStatus"/> for the specific verdict.
    /// </summary>
    public bool IsValid => CryptographicStatus == SignatureCryptographicStatus.Valid
        && CoversWholeDocument
        && ChainStatus is SignatureChainStatus.NotEvaluated or SignatureChainStatus.Trusted;
}
