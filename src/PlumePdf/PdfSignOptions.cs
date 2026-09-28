using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.IO.Http;
using PlumePdf.Objects.Signing;

namespace PlumePdf;

/// <summary>
/// The PAdES conformance level a <see cref="SignatureCollection.Add"/>/<see cref="SignatureCollection.SignAsync"/>
/// call targets. B-LT (LTV material) and B-LTA (a maintained document
/// timestamp) are not requested here — they are separate, later operations
/// (<see cref="SignatureCollection.AddLtvAsync"/>/<see cref="SignatureCollection.AddDocumentTimestampAsync"/>)
/// performed against an already-signed document, mirroring how a real B-LTA signature is built
/// incrementally over time, not produced in one call.
/// </summary>
public enum PdfSignatureLevel
{
    /// <summary>PAdES B-B (baseline): no signature timestamp. Never touches the network.</summary>
    B,

    /// <summary>PAdES B-T (baseline + timestamp): an RFC 3161 signature-timestamp is embedded. Requires <see cref="PdfSignOptions.TimestampAuthority"/> or <see cref="PdfSignOptions.TimestampAuthorityUrl"/>.</summary>
    T,
}

/// <summary>
/// Options controlling a <see cref="SignatureCollection.Add"/>/<see cref="SignatureCollection.SignAsync"/>
/// call: who signs, what conformance level, and the signature dictionary's descriptive fields
/// (ISO 32000-1 §12.8.1).
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("input.pdf");
/// document.Signatures.Add("signed.pdf", new PdfSignOptions
/// {
///     Certificate = myCertificateWithPrivateKey,
///     Reason = "I approve this document",
/// });
/// </code>
/// </example>
public sealed class PdfSignOptions
{
    /// <summary>
    /// The seam that performs the signing operation. Takes priority over
    /// <see cref="Certificate"/> when both are set. Exactly one of <see cref="Signer"/>/
    /// <see cref="Certificate"/> must be supplied.
    /// </summary>
    public IPdfSigner? Signer { get; init; }

    /// <summary>
    /// The convenience door: a certificate with a locally-usable private
    /// key, wrapped internally as an <c>Objects.Signing.CertificateSigner</c> when
    /// <see cref="Signer"/> is not set.
    /// </summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>
    /// Intermediate/root certificates to embed in the CMS alongside the signing certificate,
    /// so a verifier can build the chain without a separate fetch. Only consulted when
    /// <see cref="Certificate"/> (not <see cref="Signer"/>) is used — an <see cref="IPdfSigner"/>
    /// implementation supplies its own via <see cref="IPdfSigner.AdditionalCertificates"/>.
    /// </summary>
    public IReadOnlyList<X509Certificate2>? AdditionalCertificates { get; init; }

    /// <summary>The PAdES conformance level to sign at. Defaults to <see cref="PdfSignatureLevel.B"/> (baseline, offline).</summary>
    public PdfSignatureLevel Level { get; init; } = PdfSignatureLevel.B;

    /// <summary>The digest algorithm to sign over. Defaults to SHA-256 (PAdES/CAdES baseline).</summary>
    public HashAlgorithmName DigestAlgorithm { get; init; } = HashAlgorithmName.SHA256;

    /// <summary>
    /// The RFC 3161 timestamp authority to use for <see cref="PdfSignatureLevel.T"/>. Takes
    /// priority over <see cref="TimestampAuthorityUrl"/> when both are set. Required (with
    /// either property) when <see cref="Level"/> is <see cref="PdfSignatureLevel.T"/>; ignored
    /// for <see cref="PdfSignatureLevel.B"/> (offline by default).
    /// </summary>
    public ITimestampAuthority? TimestampAuthority { get; init; }

    /// <summary>
    /// Convenience: a TSA HTTP(S) endpoint. When set (and <see cref="TimestampAuthority"/> is
    /// not), an <c>IO.Http.HttpTimestampAuthority</c> is constructed internally for the
    /// duration of the call — still one call for the caller, per the offline-by-default
    /// ergonomics carve-out.
    /// </summary>
    public Uri? TimestampAuthorityUrl { get; init; }

    /// <summary>
    /// The claimed signing time, written to the signature dictionary's <c>/M</c> entry.
    /// Defaults to <see cref="DateTimeOffset.UtcNow"/> at the moment of signing. Required
    /// (caller-supplied, not wall-clock) for a <see cref="PdfOptions.Deterministic"/> signing
    /// call — two runs must agree on the value, which the wall clock cannot
    /// guarantee.
    /// </summary>
    public DateTimeOffset? SigningTime { get; init; }

    /// <summary>The signer-supplied reason for signing, written to <c>/Reason</c>. Optional.</summary>
    public string? Reason { get; init; }

    /// <summary>The signer-supplied signing location, written to <c>/Location</c>. Optional.</summary>
    public string? Location { get; init; }

    /// <summary>The signer-supplied contact information, written to <c>/ContactInfo</c>. Optional.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// The new signature field's name. Defaults to <c>"Signature" + (1 + the number of
    /// existing signature fields)</c> — the same generated-name convention as most PDF
    /// producers.
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// Overrides the reservation-sizing estimate: the number of raw bytes to
    /// reserve for the CMS signature's <c>/Contents</c>. When unset, PlumePDF estimates it from
    /// the signer's certificate/chain byte length plus a key-size-derived signature-size
    /// estimate plus fixed CMS/ASN.1 framing overhead — not a literal dry-run CMS build — then
    /// adds 50% headroom, floored at 12 KB (<see cref="PdfSignatureLevel.B"/>) or 24 KB
    /// (<see cref="PdfSignatureLevel.T"/> and above). Because this is an estimate rather than an
    /// exact build, a signer whose actual CMS output is unusually large for its algorithm/key
    /// size (e.g. an <see cref="IPdfSigner"/> embedding extra attributes) can still overflow the
    /// reservation and hit <c>PLUME5011</c> — set this property explicitly when that happens.
    /// </summary>
    public int? ContentsReservationBytes { get; init; }

    /// <summary>
    /// When <see langword="true"/>, this signature is written as a DocMDP certification
    /// signature (ISO 32000-1 §12.8.2.2) at permission level 1 ("no changes allowed"), via a
    /// <c>/Reference</c> entry naming the <c>/DocMDP</c> transform method and a catalog
    /// <c>/Perms/DocMDP</c> entry pointing back at this signature. Only valid as the
    /// document's first signature (<c>PLUME6051</c> otherwise) — DocMDP certification must be
    /// applied before any other signature exists (ISO 32000-1 §12.8.2.2).
    /// </summary>
    public bool CertifyNoChanges { get; init; }

    /// <summary>
    /// Resolves the effective <see cref="IPdfSigner"/> for this call: <see cref="Signer"/>
    /// directly, or <see cref="Certificate"/> wrapped as an <c>Objects.Signing.CertificateSigner</c>.
    /// </summary>
    /// <exception cref="PlumePdfException">Neither <see cref="Signer"/> nor <see cref="Certificate"/> is set (<c>PLUME6048</c>).</exception>
    internal IPdfSigner ResolveSigner()
    {
        if (Signer is not null)
        {
            return Signer;
        }

        if (Certificate is not null)
        {
            return new CertificateSigner(Certificate, DigestAlgorithm, AdditionalCertificates);
        }

        throw new PlumePdfException("PLUME6048", "PdfSignOptions must set either Signer (an IPdfSigner) or Certificate (an X509Certificate2 with a private key) — neither was supplied.");
    }

    /// <summary>
    /// Resolves the effective timestamp authority, and whether this call owns (and must
    /// dispose) the instance it constructed.
    /// </summary>
    internal (ITimestampAuthority? Authority, bool Owned) ResolveTimestampAuthority(PdfOptions options)
    {
        if (TimestampAuthority is not null)
        {
            return (TimestampAuthority, false);
        }

        if (TimestampAuthorityUrl is not null)
        {
            return (new HttpTimestampAuthority(TimestampAuthorityUrl, options.TimestampTimeout, options.MaxTimestampResponseBytes), true);
        }

        return (null, false);
    }

    /// <summary>Maps <see cref="Level"/> onto the crypto core's own level enum (Objects.Signing is internal plumbing this public type never exposes directly).</summary>
    internal CmsSignatureLevel ResolveCmsLevel() => Level switch
    {
        PdfSignatureLevel.B => CmsSignatureLevel.B,
        PdfSignatureLevel.T => CmsSignatureLevel.T,
        _ => CmsSignatureLevel.B,
    };
}
