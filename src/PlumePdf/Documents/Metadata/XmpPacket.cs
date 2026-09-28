namespace PlumePdf.Documents.Metadata;

/// <summary>
/// A typed model of a document's XMP metadata packet (ISO 16684-1; ISO 32000-1 §14.3.2) —
/// the write-side counterpart to <c>PdfDocument.GetXmpMetadataBytes</c>/
/// <c>GetXmpMetadataText</c>, consumed by <c>PdfDocument.SetXmpMetadata</c> and serialized by
/// <see cref="XmpWriter"/>. Covers exactly the fields PlumePDF's own writer surface and PDF/A
/// conformance need: Dublin Core
/// descriptive fields, the XMP Basic schema's create/modify dates, and the pdfaid/pdfuaid
/// identification schemas — not a general-purpose RDF/XML object model. Phase 1
/// deliberately drew the line at a raw-bytes escape hatch for <em>reading</em> arbitrary
/// XMP; this type stays narrower, covering only what PlumePDF itself writes. Immutable — use
/// object initializers or <c>with</c>.
/// </summary>
/// <example>
/// <code>
/// var packet = new XmpPacket
/// {
///     Title = "Q3 Report",
///     Creator = "Acme Corp",
///     CreateDate = DateTimeOffset.UtcNow,
///     ModifyDate = DateTimeOffset.UtcNow,
/// };
/// document.SetXmpMetadata(packet);
/// </code>
/// </example>
public sealed record XmpPacket
{
    /// <summary>Dublin Core <c>dc:title</c> — the document's title. Written as a single <c>x-default</c> <c>rdf:Alt</c> entry, the schema's required container shape.</summary>
    public string? Title { get; init; }

    /// <summary>Dublin Core <c>dc:creator</c> — the document's author. Written as a single-entry <c>rdf:Seq</c>, the schema's required container shape even for one name.</summary>
    public string? Creator { get; init; }

    /// <summary>Dublin Core <c>dc:description</c> — the document's subject. Written as a single <c>x-default</c> <c>rdf:Alt</c> entry.</summary>
    public string? Subject { get; init; }

    /// <summary>PDF schema <c>pdf:Keywords</c>.</summary>
    public string? Keywords { get; init; }

    /// <summary>PDF schema <c>pdf:Producer</c> — the application/library that produced the file.</summary>
    public string? Producer { get; init; }

    /// <summary>
    /// XMP Basic <c>xmp:CreateDate</c>. PDF/A requires this be present and agree with the
    /// DocInfo dictionary's <c>/CreationDate</c> — <c>PdfDocument.SetInfo</c>/
    /// <c>SetXmpMetadata</c> enforce that agreement at write time.
    /// </summary>
    public DateTimeOffset? CreateDate { get; init; }

    /// <summary>
    /// XMP Basic <c>xmp:ModifyDate</c>. PDF/A requires this be present and agree with the
    /// DocInfo dictionary's <c>/ModDate</c>, enforced the same way as <see cref="CreateDate"/>.
    /// </summary>
    public DateTimeOffset? ModifyDate { get; init; }

    /// <summary>
    /// The PDF/A identification schema (<c>pdfaid:part</c>/<c>pdfaid:conformance</c>) to
    /// declare, or <see cref="PlumePdf.PdfAConformance.None"/> to omit the schema entirely.
    /// Under <c>PdfOptions.Deterministic</c>, a non-<see cref="PlumePdf.PdfAConformance.None"/>
    /// value requires both <see cref="CreateDate"/> and <see cref="ModifyDate"/> to be
    /// supplied — <see cref="XmpWriter"/> refuses otherwise: a fixed or omitted
    /// timestamp would silently violate PDF/A's mandatory-XMP-dates requirement.
    /// </summary>
    public PdfAConformance Conformance { get; init; } = PdfAConformance.None;

    /// <summary>
    /// When <see langword="true"/>, the PDF/UA identification schema (<c>pdfuaid:part = 1</c>)
    /// is emitted alongside whatever <see cref="Conformance"/> declares — the two schemas
    /// coexist in the same packet for a document conforming to both PDF/A and PDF/UA.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool DeclarePdfUa { get; init; }
}
