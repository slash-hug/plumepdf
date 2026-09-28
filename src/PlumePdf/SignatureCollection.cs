using System.Collections;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.Documents.Signing;

namespace PlumePdf;

/// <summary>
/// The <c>doc.Signatures</c> hub (mirroring <see cref="PdfForm"/>/<c>doc.Form</c>):
/// every existing signature reachable from this document's <c>/AcroForm</c> field tree, plus the
/// doors to create a new one (<see cref="Add"/>/<see cref="SignAsync"/>) and to maintain an
/// existing one toward B-LT/B-LTA (<see cref="AddLtvAsync"/>/<see cref="AddDocumentTimestampAsync"/>).
/// </summary>
/// <remarks>
/// Every write here (<see cref="Add"/>, <see cref="SignAsync"/>, <see cref="AddLtvAsync"/>,
/// <see cref="AddDocumentTimestampAsync"/>) both mutates <see cref="PdfDocument.Objects"/> and
/// writes the finished result to an output path in one call — unlike <see cref="PdfForm.Fill"/>
/// (mutate now, save later, any number of times), a signature's two-pass placeholder/hash/patch
/// technique (<c>Objects.SigningWriteSession</c>) only makes sense as one atomic
/// mutate-and-write operation. Treat the owning document (obtained via
/// <see cref="PdfDocument.Signatures"/>) as done after any of these calls — reopen the written
/// output to continue working with the signed result.
/// </remarks>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("input.pdf");
/// document.Signatures.Add("signed.pdf", new PdfSignOptions { Certificate = myCertificate });
///
/// using var signed = PdfDocument.Open("signed.pdf");
/// foreach (var signature in signed.Signatures)
/// {
///     Console.WriteLine(signature.Verify().IsValid);
/// }
/// </code>
/// </example>
public sealed class SignatureCollection : IReadOnlyList<PdfSignature>
{
    private readonly PdfDocument _document;
    private readonly Lazy<IReadOnlyList<PdfSignature>> _signatures;

    internal SignatureCollection(PdfDocument document)
    {
        _document = document;
        _signatures = new Lazy<IReadOnlyList<PdfSignature>>(() =>
            SignatureDictionaryReader.ReadAll(_document).Select(info => new PdfSignature(_document, info)).ToList());
    }

    /// <summary>The number of signatures (and document timestamps) currently in this document.</summary>
    public int Count => _signatures.Value.Count;

    /// <summary>The signature at <paramref name="index"/>, in field-tree (document) order.</summary>
    public PdfSignature this[int index] => _signatures.Value[index];

    /// <inheritdoc/>
    public IEnumerator<PdfSignature> GetEnumerator() => _signatures.Value.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Signs the document synchronously and writes the result to <paramref name="outputPath"/>
    /// — valid only for a request that never touches the network
    /// (<see cref="PdfSignatureLevel.B"/>, no timestamp authority).
    /// </summary>
    /// <param name="outputPath">Where to write the signed document. May equal the path this document was opened from.</param>
    /// <param name="signOptions">Who signs, and the signature dictionary's descriptive fields.</param>
    /// <param name="options">Options controlling the write. Defaults to the options this document was opened with.</param>
    /// <exception cref="PlumePdfException">
    /// <paramref name="signOptions"/> requires a network round trip (<c>PLUME6049</c> — use
    /// <see cref="SignAsync"/> instead); neither <see cref="PdfSignOptions.Signer"/> nor
    /// <see cref="PdfSignOptions.Certificate"/> is set (<c>PLUME6048</c>); this document's
    /// source is encrypted (<c>PLUME6054</c>) or has no backing byte source
    /// (<c>PLUME5003</c>) or no reliable prior <c>startxref</c> (<c>PLUME5005</c>); or
    /// <paramref name="signOptions"/>'s field name collides with an existing field
    /// (<c>PLUME6047</c>).
    /// </exception>
    /// <example>
    /// <code>
    /// document.Signatures.Add("signed.pdf", new PdfSignOptions { Certificate = myCertificate, Reason = "Approved" });
    /// </code>
    /// </example>
    public void Add(string outputPath, PdfSignOptions signOptions, PdfOptions? options = null) =>
        SigningOrchestrator.Sign(_document, outputPath, signOptions, options);

    /// <summary>
    /// Asynchronous equivalent of <see cref="Add"/>, supporting every <see cref="PdfSignatureLevel"/>
    /// including one requiring a network round trip (an RFC 3161 timestamp fetch) — real awaits
    /// down to <c>HttpClient</c>, never <c>Task.Run</c> (this phase's first
    /// true-async member).
    /// </summary>
    /// <example>
    /// <code>
    /// await document.Signatures.SignAsync("signed.pdf", new PdfSignOptions
    /// {
    ///     Certificate = myCertificate,
    ///     Level = PdfSignatureLevel.T,
    ///     TimestampAuthorityUrl = new Uri("https://freetsa.org/tsr"),
    /// });
    /// </code>
    /// </example>
    public Task SignAsync(string outputPath, PdfSignOptions signOptions, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        SigningOrchestrator.SignAsync(_document, outputPath, signOptions, options, cancellationToken);

    /// <summary>
    /// Adds LTV (Long-Term Validation) material — every embedded certificate across every
    /// existing signature, plus OCSP/CRL revocation material fetched through
    /// <paramref name="revocationFetcher"/> — as a <c>/DSS</c> incremental revision (PAdES B-LT).
    /// </summary>
    /// <param name="outputPath">Where to write the result.</param>
    /// <param name="revocationFetcher">The seam OCSP/CRL requests go through — PlumePDF never opens a socket unless one is supplied.</param>
    /// <param name="trustedRoots">Trust anchors for chain building — the roots revocation material is collected up to.</param>
    /// <param name="options">Options controlling the write. Defaults to the options this document was opened with.</param>
    /// <param name="cancellationToken">Propagated to every revocation fetch.</param>
    /// <exception cref="PlumePdfException">This document carries no signature to build LTV material for (<c>PLUME6050</c>).</exception>
    public Task AddLtvAsync(string outputPath, IRevocationFetcher revocationFetcher, IReadOnlyList<X509Certificate2>? trustedRoots = null, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        DssWriter.AddLtvAsync(_document, outputPath, revocationFetcher, trustedRoots, options, cancellationToken);

    /// <summary>
    /// Adds a standalone <c>/DocTimeStamp</c> revision covering the document's current state —
    /// the PAdES B-LTA renewal door: call this again before an earlier
    /// timestamp's own certificate expires to keep the chain of trust continuously provable.
    /// </summary>
    /// <param name="outputPath">Where to write the result.</param>
    /// <param name="timestampAuthority">The RFC 3161 timestamp authority to use.</param>
    /// <param name="digestAlgorithm">The digest algorithm to timestamp with. Defaults to SHA-256.</param>
    /// <param name="reservationBytes">Overrides the <c>/Contents</c> reservation. Defaults to 24 KB — generous for a TSA token including its own certificate chain.</param>
    /// <param name="options">Options controlling the write. Defaults to the options this document was opened with.</param>
    /// <param name="cancellationToken">Propagated to the timestamp request.</param>
    public Task AddDocumentTimestampAsync(string outputPath, ITimestampAuthority timestampAuthority, HashAlgorithmName? digestAlgorithm = null, int? reservationBytes = null, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        DocumentTimestamper.AddDocumentTimestampAsync(_document, outputPath, timestampAuthority, digestAlgorithm ?? HashAlgorithmName.SHA256, reservationBytes, options, cancellationToken);
}
