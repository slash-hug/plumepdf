using System.Security.Cryptography.X509Certificates;
using PlumePdf.Documents.Signing;

namespace PlumePdf;

/// <summary>
/// One signature (or B-LTA document timestamp) already present in a document, discovered via
/// <see cref="SignatureCollection"/> (<c>doc.Signatures</c>). Read-only — creating a new
/// signature goes through <see cref="SignatureCollection.Add"/>/<see cref="SignatureCollection.SignAsync"/>.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("signed.pdf");
/// var signature = document.Signatures[0];
/// Console.WriteLine($"{signature.FieldName} ({signature.SubFilter}): signed {signature.SigningTime}, reason: {signature.Reason}");
/// var result = signature.Verify();
/// Console.WriteLine(result.IsValid ? "valid" : $"invalid: {result.CryptographicStatus}");
/// </code>
/// </example>
public sealed class PdfSignature
{
    private readonly PdfDocument _document;
    private readonly SignatureDictionaryInfo _info;

    internal PdfSignature(PdfDocument document, SignatureDictionaryInfo info)
    {
        _document = document;
        _info = info;
    }

    /// <summary>The owning <c>/FT /Sig</c> AcroForm field's fully-qualified name.</summary>
    public string FieldName => _info.FieldName;

    /// <summary>
    /// Whether this is a standalone document timestamp (<c>/Type /DocTimeStamp</c>, B-LTA
    /// maintenance) rather than an ordinary signature.
    /// </summary>
    public bool IsDocumentTimestamp => _info.IsDocTimeStamp;

    /// <summary>The <c>/SubFilter</c> value naming this signature's encoding, or <see langword="null"/> when absent.</summary>
    public string? SubFilter => _info.SubFilter;

    /// <summary>The claimed signing time (<c>/M</c>), or <see langword="null"/> when absent/unparseable. See <see cref="SignatureVerificationResult.TimestampTime"/> for the RFC 3161-proven time instead.</summary>
    public DateTimeOffset? SigningTime => _info.SigningTime;

    /// <summary>The signer-supplied reason for signing (<c>/Reason</c>), or <see langword="null"/>.</summary>
    public string? Reason => _info.Reason;

    /// <summary>The signer-supplied signing location (<c>/Location</c>), or <see langword="null"/>.</summary>
    public string? Location => _info.Location;

    /// <summary>The signer-supplied contact information (<c>/ContactInfo</c>), or <see langword="null"/>.</summary>
    public string? ContactInfo => _info.ContactInfo;

    /// <summary>
    /// Verifies this signature: recomputes the content digest over the document's actual
    /// <c>/ByteRange</c>-covered bytes, decodes and verifies the CMS, and evaluates ByteRange
    /// coverage structurally (always populated — see <see cref="SignatureVerificationResult.CoversWholeDocument"/>).
    /// </summary>
    /// <param name="trustedRoots">
    /// Trust anchors for certificate-chain building. When omitted (or empty), the chain is not
    /// evaluated at all — <see cref="SignatureVerificationResult.ChainStatus"/> comes back
    /// <see cref="SignatureChainStatus.NotEvaluated"/> — PlumePDF never silently falls back to
    /// the OS trust store (the offline-by-default posture applied to verification too).
    /// </param>
    /// <example>
    /// <code>
    /// var result = document.Signatures[0].Verify(trustedRoots: [myRootCertificate]);
    /// </code>
    /// </example>
    public SignatureVerificationResult Verify(IReadOnlyList<X509Certificate2>? trustedRoots = null) =>
        SignatureVerifier.Verify(_document, _info, trustedRoots);

    /// <summary>Internal accessor letting <see cref="SignatureCollection"/> reach the underlying read model without widening this type's own public surface.</summary>
    internal SignatureDictionaryInfo Info => _info;
}
