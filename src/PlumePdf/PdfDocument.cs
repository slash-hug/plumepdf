using PlumePdf.Documents;
using PlumePdf.Documents.Metadata;
using PlumePdf.Documents.PageRemoval;
using PlumePdf.Documents.Redaction;
using PlumePdf.Documents.Signing;
using PlumePdf.IO;
using PlumePdf.Layout;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// The hub of the public API — always a real PDF, opened or produced.
/// <see cref="Open(string,PdfOptions?)"/> and its overloads parse only the header, trailer,
/// and cross-reference data; indirect objects resolve lazily through <see cref="Objects"/>
/// on first access. <see cref="Pages"/> is the mutation door for reorder/removal;
/// <see cref="Save"/> (full rewrite: garbage-collects and renumbers) and
/// <see cref="SaveIncremental(string,PdfOptions?)"/> (the default save path: appends changed
/// objects and a new cross-reference section, preserving prior revisions) are the two save
/// paths (docs/architecture.md "Writing pipeline"). Thread-safe for concurrent reads once
/// open (docs/architecture.md "Threading &amp; mutation"); mutation through <see cref="Pages"/>
/// is single-threaded by contract.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("input.pdf");
/// Console.WriteLine($"{document.Pages.Count} pages, {document.Diagnostics.Count} diagnostics");
/// document.Pages.RemoveAt(0);
/// document.SaveIncremental("input.pdf");
/// </code>
/// </example>
public sealed class PdfDocument : IDisposable
{
    private readonly ByteSource? _source;
    private readonly string? _openPath;
    private readonly PdfOptions _options;
    private readonly long? _startXrefOffset;
    private readonly int? _rawPermissions;
    private readonly StandardSecurityHandler? _securityHandler;
    private bool _pagesTreeDirty;
    private bool _redactionDirty;
    private bool _linearizedSave;
    private bool _disposed;
    private XmpPacket? _lastXmpMetadata;
    private DocInfoMetadata? _lastDocInfoMetadata;

    private PdfDocument(
        ByteSource? source,
        string? openPath,
        IObjectSource objectSource,
        PdfOptions options,
        DiagnosticCollection diagnostics,
        bool hasEncryptedSource,
        long? startXrefOffset,
        int nextObjectNumber,
        IReadOnlyCollection<int>? freeObjectNumbers = null,
        int? rawPermissions = null,
        StandardSecurityHandler? securityHandler = null)
    {
        _source = source;
        _openPath = openPath;
        _options = options;
        _startXrefOffset = startXrefOffset;
        _rawPermissions = rawPermissions;
        _securityHandler = securityHandler;
        Diagnostics = diagnostics;
        Objects = new ObjectRegistry(objectSource, nextObjectNumber, freeObjectNumbers);
        HasEncryptedSource = hasEncryptedSource;

        Catalog = DocumentCatalog.Resolve(Objects, Objects.Trailer, options, diagnostics);
        var pageReferences = Catalog is not null
            ? PageTreeReader.CollectPages(Objects, Catalog.Dictionary, options, diagnostics, _openTimePageTree)
            : [];

        var pages = new List<PdfPage>(pageReferences.Count);
        foreach (var (reference, dictionary) in pageReferences)
        {
            pages.Add(new PdfPage(reference, dictionary, this));
        }

        _openTimePages = [.. pageReferences.Select(static p => p.Reference)];

        // R-n: a page reorder/removal is a mutation of this document exactly like
        // Objects.MarkDirty/RegisterNew/AllocateNumber, so it participates in the same
        // single-writer guard those go through (ObjectRegistry's indexer asserts against it) —
        // one contract covering every way this document can be mutated, not just the newest one.
        Pages = new PageCollection(pages, () =>
        {
            Objects.EnterExternalMutation();
            try
            {
                _pagesTreeDirty = true;
            }
            finally
            {
                Objects.ExitExternalMutation();
            }
        });
    }

    /// <summary>The deviations tolerated while reading this document — never thrown under default options, never silently dropped.</summary>
    public DiagnosticCollection Diagnostics { get; }

    /// <summary>The low-level escape hatch to this document's raw object graph.</summary>
    public ObjectRegistry Objects { get; }

    /// <summary>This document's pages, in document order — reorder and remove through this collection.</summary>
    public PageCollection Pages { get; }

    /// <summary>The resolved document catalog, or <see langword="null"/> when the trailer's <c>/Root</c> could not be resolved (recorded to <see cref="Diagnostics"/>).</summary>
    internal DocumentCatalog? Catalog { get; }

    // Every page-tree node, page and indirect /Kids array the open-time walk resolved. A full
    // rewrite always replaces the tree with a fresh flat /Pages node, so any of these that is
    // not a page being saved must never reach the output — whatever still references it.
    private readonly HashSet<int> _openTimePageTree = [];
    private readonly IReadOnlyList<IndirectReference> _openTimePages;

    /// <summary>The object numbers of every page-tree node and page this document was opened with (see <c>PageTreeReader.CollectPages</c>).</summary>
    internal IReadOnlySet<int> OpenTimePageTree => _openTimePageTree;

    /// <summary>
    /// Whether this document's trailer declares an <c>/Encrypt</c> dictionary. Gates
    /// <see cref="Save"/> unconditionally and
    /// <see cref="SaveIncremental(string,PdfOptions?)"/> only when re-encrypting the appended
    /// revision on write is not possible for some reason — the common case succeeds instead.
    /// Public so a caller can pre-check before choosing a save path rather than discovering the
    /// refusal only via a caught <see cref="PlumePdfException"/>.
    /// </summary>
    public bool HasEncryptedSource { get; }

    /// <summary>Whether <see cref="Pages"/> has been reordered or had a page removed since this document was opened/created.</summary>
    internal bool PagesTreeDirty => _pagesTreeDirty;

    /// <summary>
    /// Whether this document has been marked as carrying a redaction. Once
    /// set, it is never cleared for the remainder of this <see cref="PdfDocument"/> instance's
    /// lifetime — even after a successful <see cref="Save"/> — because
    /// <see cref="SaveIncremental(string,PdfOptions?)"/> always incrementally updates onto
    /// this instance's <em>original</em> backing source, not onto whatever a prior
    /// <see cref="Save"/> call wrote elsewhere; those original bytes are exactly the
    /// unredacted content redaction exists to make unrecoverable, so permitting an
    /// incremental update onto them after redaction would defeat the guarantee regardless of
    /// what <see cref="Save"/> already produced. Set by
    /// <c>Documents.Redaction.RedactionEngine</c> (Phase 6); exposed here
    /// purely as the seam and the guard below, ahead of that work landing.
    /// </summary>
    internal bool IsRedactionDirty => _redactionDirty;

    /// <summary>Marks this document redaction-dirty (see <see cref="IsRedactionDirty"/>) — called once a redaction actually removed something, never for a zero-match redaction call.</summary>
    internal void MarkRedactionDirty() => _redactionDirty = true;

    /// <summary>The options this document was opened (or composed) with.</summary>
    internal PdfOptions Options => _options;

    /// <summary>
    /// This document's interactive AcroForm facade (ISO 32000-1 §12.7) — field
    /// enumeration (<see cref="PdfForm.Fields"/>), filling (<see cref="PdfForm.Fill"/>), and
    /// flattening (<see cref="PdfForm.Flatten"/>). Equivalent to <see cref="PdfForm.For"/>;
    /// reading the field tree is deferred until first use.
    /// </summary>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("f1040.pdf");
    /// document.Form.Fields["c1_01"].Value = "1";
    /// document.SaveIncremental("f1040-filled.pdf");
    /// </code>
    /// </example>
    private PdfForm? _form;

    /// <summary>Cached: repeated reads return one instance, so the underlying field-tree read
    /// (and its first-occurrence diagnostics) runs once per document, not once
    /// per property access (an uncached read previously appended a duplicate diagnostic on every access).
    /// Thread-safe for concurrent reads via lazy publication.</summary>
    public PdfForm Form => LazyInitializer.EnsureInitialized(ref _form, () => PdfForm.For(this));

    // Backing field for Signatures below — see that property's own <summary>/<example> for the
    // documented contract; this field is plain cache storage, never resolved through directly.
    private SignatureCollection? _signatures;

    /// <summary>
    /// This document's digital-signature hub: every existing
    /// signature/document-timestamp reachable from this document's <c>/AcroForm</c> field tree
    /// (<see cref="SignatureCollection.Count"/>, indexer), plus the doors to create new ones
    /// (<see cref="SignatureCollection.Add"/>/<see cref="SignatureCollection.SignAsync"/>) and
    /// maintain existing ones toward B-LT/B-LTA
    /// (<see cref="SignatureCollection.AddLtvAsync"/>/<see cref="SignatureCollection.AddDocumentTimestampAsync"/>).
    /// Equivalent to <see cref="Form"/>'s hub shape one layer over — cached the same way, so
    /// the underlying field-tree read runs once per document.
    /// </summary>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("signed.pdf");
    /// foreach (var signature in document.Signatures)
    /// {
    ///     Console.WriteLine($"{signature.FieldName}: {signature.Verify().IsValid}");
    /// }
    /// </code>
    /// </example>
    public SignatureCollection Signatures => LazyInitializer.EnsureInitialized(ref _signatures, () => new SignatureCollection(this));

    /// <summary>Whether <see cref="Dispose"/> has already run — <see cref="PdfPage"/> checks this before an extraction call touches this document's object graph.</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// This document's permission flags (<c>/Encrypt</c>'s <c>/P</c> entry, ISO 32000-1
    /// §7.6.3.2), or <see cref="PdfPermissions.All"/> for an unencrypted document. Advisory
    /// only — PlumePDF does not refuse any operation based on this; see
    /// <see cref="PdfPermissions"/>'s remarks for why.
    /// </summary>
    public PdfPermissions Permissions
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return MetadataReader.ReadPermissions(_rawPermissions);
        }
    }

    /// <summary>
    /// Reads this document's Document Information Dictionary (<c>/Info</c>, ISO 32000-1
    /// §14.3.3) into its typed fields.
    /// </summary>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// Console.WriteLine(document.GetInfo().Title);
    /// </code>
    /// </example>
    public PdfDocumentInfo GetInfo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return MetadataReader.ReadDocumentInfo(Objects, Objects.Trailer);
    }

    /// <summary>
    /// Reads the catalog's <c>/Metadata</c> XMP packet (ISO 32000-1 §14.3.2) as raw,
    /// filter-decoded bytes, or <see langword="null"/> when the document has none. No XMP/RDF
    /// parsing occurs — this is the escape hatch a caller feeds to their own
    /// XML/RDF tooling.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// The decoded packet exceeds <see cref="PdfOptions.MaxXmpPacketReadBytes"/>
    /// (<c>PLUME6080</c>) — a resource-limit guard against a hostile packet that is tiny
    /// compressed but enormous decoded; raise the cap only for a trusted source.
    /// </exception>
    public byte[]? GetXmpMetadataBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return MetadataReader.ReadXmpBytes(Objects, Catalog?.Dictionary, _options);
    }

    /// <summary>
    /// Equivalent to <see cref="GetXmpMetadataBytes"/>, decoded as UTF-8 text (XMP packets are
    /// UTF-8-, UTF-16-, or UTF-16BE-encoded XML per the XMP specification; PlumePDF decodes as
    /// UTF-8, the overwhelmingly common case for PDF producers — a packet in another encoding
    /// round-trips incorrectly through this overload, in which case use
    /// <see cref="GetXmpMetadataBytes"/> directly).
    /// </summary>
    public string? GetXmpMetadataText()
    {
        var bytes = GetXmpMetadataBytes();
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Sets this document's Document Information Dictionary (<c>/Info</c>, ISO 32000-1
    /// §14.3.3) from a typed model — the write-side counterpart to <see cref="GetInfo"/>.
    /// Registers a fresh <c>/Info</c> object and repoints the trailer's <c>/Info</c> entry at
    /// it (any previously-registered <c>/Info</c> object simply becomes unreferenced and is
    /// garbage-collected away by the next <see cref="Save"/>) — resolvable immediately
    /// through <see cref="GetInfo"/>, even before either save path runs, via
    /// <see cref="ObjectRegistry"/>'s overlay.
    /// </summary>
    /// <param name="info">The Document Information Dictionary fields to write.</param>
    /// <exception cref="PlumePdfException">
    /// <paramref name="info"/>'s <c>Title</c>/<c>CreationDate</c>/<c>ModDate</c> disagree with
    /// an already-supplied <see cref="SetXmpMetadata"/> packet's <c>Title</c>/<c>CreateDate</c>/
    /// <c>ModifyDate</c> (the XMP/DocInfo agreement rule, <c>PLUME6057</c>) — call
    /// whichever of the two you call second with values that agree, or set only one.
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Compose(page => page.Content().Text("Hi"));
    /// document.SetInfo(new DocInfoMetadata { Title = "Q3 Report", Producer = "PlumePDF" });
    /// document.Save("report.pdf");
    /// </code>
    /// </example>
    public void SetInfo(DocInfoMetadata info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateMetadataAgreement(info, _lastXmpMetadata);

        var dictionary = DocInfoWriter.BuildDictionary(info);
        var reference = Objects.AllocateNumber();
        Objects.RegisterNew(reference, dictionary);
        Objects.Trailer.Set(PdfName.Info, new PdfReference(reference));

        _lastDocInfoMetadata = info;
    }

    /// <summary>
    /// Sets this document's XMP metadata packet (<c>/Metadata</c>, ISO 32000-1 §14.3.2) from
    /// a typed model — the write-side counterpart to <see cref="GetXmpMetadataBytes"/>/
    /// <see cref="GetXmpMetadataText"/>. Serializes <paramref name="packet"/> via
    /// <see cref="XmpWriter"/>, registers it as a fresh <c>/Metadata</c> stream, and repoints
    /// the catalog's <c>/Metadata</c> entry at it — resolvable immediately through
    /// <see cref="GetXmpMetadataText"/>, even before either save path runs.
    /// </summary>
    /// <param name="packet">The XMP metadata to write.</param>
    /// <exception cref="PlumePdfException">
    /// This document has no resolvable catalog (<c>PLUME6059</c>); <paramref name="packet"/>'s
    /// <c>Title</c>/<c>CreateDate</c>/<c>ModifyDate</c> disagree with an already-supplied
    /// <see cref="SetInfo"/> model (<c>PLUME6057</c>); the serialized packet
    /// exceeds <see cref="PdfOptions.MaxXmpPacketWriteBytes"/> (<c>PLUME6056</c>); or
    /// <paramref name="packet"/> declares a <see cref="XmpPacket.Conformance"/> under
    /// <see cref="PdfOptions.Deterministic"/> without both dates supplied
    /// (<c>PLUME6058</c>).
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Compose(page => page.Content().Text("Hi"));
    /// document.SetXmpMetadata(new XmpPacket
    /// {
    ///     Title = "Q3 Report",
    ///     CreateDate = DateTimeOffset.UtcNow,
    ///     ModifyDate = DateTimeOffset.UtcNow,
    /// });
    /// document.Save("report.pdf");
    /// </code>
    /// </example>
    public void SetXmpMetadata(XmpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateMetadataAgreement(_lastDocInfoMetadata, packet);

        if (Catalog is null)
        {
            throw new PlumePdfException("PLUME6059", "Cannot set XMP metadata: this document has no resolvable catalog (its trailer's /Root entry is missing or did not resolve to a dictionary).");
        }

        var bytes = XmpWriter.ToBytes(packet, _options);
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, PdfName.Metadata);
        dictionary.Set(PdfName.Subtype, XmlSubtypeName);
        dictionary.Set(PdfName.Length, PdfNumber.Get(bytes.Length));
        var stream = new PdfStream(dictionary, bytes);

        var reference = Objects.AllocateNumber();
        Objects.RegisterNew(reference, stream);
        Catalog.Dictionary.Set(PdfName.Metadata, new PdfReference(reference));
        Objects.MarkDirty(Catalog.Reference);

        _lastXmpMetadata = packet;
    }

    // DocInfo and XMP describe the same document and must not silently disagree
    // once both are supplied — checked against whichever of SetInfo/SetXmpMetadata was called
    // first (either argument may be null when only one has been set so far, in which case
    // there is nothing yet to disagree with).
    private static void ValidateMetadataAgreement(DocInfoMetadata? info, XmpPacket? xmp)
    {
        if (info is null || xmp is null)
        {
            return;
        }

        if (info.Title is not null && xmp.Title is not null && !string.Equals(info.Title, xmp.Title, StringComparison.Ordinal))
        {
            throw new PlumePdfException("PLUME6057", $"DocInfo's /Title (\"{info.Title}\") disagrees with the XMP packet's dc:title (\"{xmp.Title}\") — DocInfo and XMP must agree. Set both to the same value, or set only one.");
        }

        if (info.CreationDate is { } created && xmp.CreateDate is { } xmpCreated && created != xmpCreated)
        {
            throw new PlumePdfException("PLUME6057", $"DocInfo's /CreationDate ({created:O}) disagrees with the XMP packet's xmp:CreateDate ({xmpCreated:O}) — DocInfo and XMP must agree. Set both to the same value, or set only one.");
        }

        if (info.ModDate is { } modified && xmp.ModifyDate is { } xmpModified && modified != xmpModified)
        {
            throw new PlumePdfException("PLUME6057", $"DocInfo's /ModDate ({modified:O}) disagrees with the XMP packet's xmp:ModifyDate ({xmpModified:O}) — DocInfo and XMP must agree. Set both to the same value, or set only one.");
        }
    }

    private static readonly PdfName XmlSubtypeName = PdfName.Get("XML");

    // Object streams and cross-reference streams were introduced in
    // PDF 1.5 — a save that asks for them under an earlier header version (including the
    // "1.4" that PdfAConformance.A1b forces via ApplyVersionKnob, which runs before this
    // check) is an explicit caller request that cannot be honored, so it refuses with a code
    // rather than silently writing an un-optimized (or non-conformant) file.
    private static void EnsureVersionSupportsOptimization(PdfOptions options)
    {
        var version = string.IsNullOrWhiteSpace(options.PdfVersion) ? "1.7" : options.PdfVersion;
        var separator = version.IndexOf('.', StringComparison.Ordinal);
        int major = 0, minor = 0;
        var parseable = separator > 0
            && int.TryParse(version.AsSpan(0, separator), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out major)
            && int.TryParse(version.AsSpan(separator + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out minor);

        if (!parseable)
        {
            throw new PlumePdfException("PLUME5017", $"PdfOptions.Optimize requires PDF 1.5 or later (object streams and cross-reference streams, ISO 32000-1 §7.5.7/§7.5.8), but PdfOptions.PdfVersion (\"{options.PdfVersion}\") does not parse as a 'major.minor' PDF version — set a parseable version at or above \"1.5\" (or leave the default \"1.7\"), or drop Optimize.");
        }

        if (major < 1 || (major == 1 && minor < 5))
        {
            var pdfA1bHint = options.PdfAConformance == PdfAConformance.A1b
                ? " PDF/A-1b forces the \"1.4\" header, so PDF/A-1b output can never be optimized — target PdfAConformance.A2b instead, or drop Optimize."
                : " Raise PdfOptions.PdfVersion to \"1.5\" or later (or leave the default \"1.7\"), or drop Optimize.";
            throw new PlumePdfException("PLUME5017", $"PdfOptions.Optimize requires PDF 1.5 or later (object streams and cross-reference streams, ISO 32000-1 §7.5.7/§7.5.8), but PdfOptions.PdfVersion is \"{version}\".{pdfA1bHint}");
        }
    }

    /// <summary>
    /// Redacts this document against <paramref name="targets"/> — true removal, not a drawn-over
    /// box: matched text-showing operators are deleted from page content,
    /// intersecting image XObjects are removed in full, and matching text is scrubbed from the
    /// enumerated metadata surfaces (DocInfo, XMP, annotation <c>/Contents</c>, outline titles,
    /// embedded-file names, <c>/PieceInfo</c>). Always inspect the returned
    /// <see cref="RedactionResult"/> — a zero-match redaction is loud in the result
    /// (<see cref="RedactionResult.HadNoMatches"/>), never an exception and never a silent
    /// success. Once anything was actually removed, only <see cref="Save"/> (the full-rewrite,
    /// garbage-collecting path) may write the result:
    /// <see cref="SaveIncremental(string,PdfOptions?)"/> refuses (<c>PLUME5016</c>), because an
    /// incremental update appends onto the original bytes that still carry the unredacted
    /// content. See <c>docs/cookbook/redact.md</c>.
    /// </summary>
    /// <param name="targets">What to remove: <see cref="RedactionTarget.Text"/>, <see cref="RedactionTarget.Pattern"/>, and/or <see cref="RedactionTarget.Region"/> targets.</param>
    /// <param name="options">Options controlling this redaction call. Defaults to <see cref="PdfRedactOptions.Default"/>.</param>
    /// <returns>The rich per-call report — match count, operators/images removed, signatures stripped, metadata surfaces scrubbed.</returns>
    /// <exception cref="PlumePdfException">
    /// This document was opened from an encrypted source (<c>PLUME6061</c> — encrypted write is
    /// out of scope until encryption write ships, a documented 1.x gap); the source carries
    /// existing signatures/document timestamps and
    /// <see cref="PdfRedactOptions.AllowInvalidatingSignatures"/> was not set
    /// (<c>PLUME6062</c>); or target resolution exceeded
    /// <see cref="PdfRedactOptions.MaxMatches"/> (<c>PLUME6060</c>).
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("contract.pdf");
    /// var result = document.Redact([RedactionTarget.Text("Jane Doe")]);
    /// if (result.HadNoMatches)
    /// {
    ///     throw new InvalidOperationException("Nothing matched — nothing was redacted.");
    /// }
    ///
    /// document.Save("contract-redacted.pdf"); // Save only — SaveIncremental refuses (PLUME5016).
    /// </code>
    /// </example>
    public RedactionResult Redact(IReadOnlyList<RedactionTarget> targets, PdfRedactOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return RedactionEngine.Redact(this, targets, options);
    }

    /// <summary>
    /// Paints <paramref name="stamp"/> onto this already-opened document's pages — the
    /// opened-document counterpart to <see cref="Elements.Section.Stamps"/> — closing the
    /// v1.0 opened-document-stamping commitment an earlier phase deferred.
    /// Purely additive: it appends a new content stream per page (wrapping the existing content
    /// in <c>q</c>/<c>Q</c> so a dangling graphics state cannot displace the stamp) and never
    /// rewrites existing bytes, so it works through
    /// <see cref="SaveIncremental(string,PdfOptions?)"/> — the signature-preserving path — as
    /// well as <see cref="Save"/>. The stamp text is drawn in Helvetica-Bold (Standard-14,
    /// nothing embedded), marked as a pagination <c>/Artifact</c>, anchored to
    /// <see cref="Elements.Stamp.Position"/> inside a fixed 36-point inset from the page's
    /// <c>/MediaBox</c> edges, compensating for the page's <c>/Rotate</c> so the stamp reads
    /// upright as displayed.
    /// </summary>
    /// <param name="stamp">What to paint: text, size, opacity, corner, color — the same descriptor <see cref="Elements.Section.Stamps"/> uses for composed documents.</param>
    /// <param name="pageIndexes">The zero-based pages to stamp, or <see langword="null"/> (the default) for every page.</param>
    /// <exception cref="ArgumentOutOfRangeException">A <paramref name="pageIndexes"/> entry is outside this document's page range.</exception>
    /// <exception cref="PlumePdfException">
    /// This document has no pages (<c>PLUME6073</c>), or <paramref name="stamp"/>'s text
    /// contains a character the Standard-14 Helvetica-Bold WinAnsi encoding cannot represent
    /// (<c>PLUME6072</c> — compose a document with an embedded <see cref="PdfFont"/> via
    /// <see cref="Elements.Section.Stamps"/> for text beyond WinAnsi).
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("report.pdf");
    /// document.Stamp(new Elements.Stamp { Text = "CONFIDENTIAL" });
    /// document.SaveIncremental("report.pdf"); // additive — prior revisions (and signatures) stay intact
    /// </code>
    /// </example>
    public void Stamp(Elements.Stamp stamp, IReadOnlyList<int>? pageIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ObjectDisposedException.ThrowIf(_disposed, this);

        StampEngine.Stamp(this, stamp, pageIndexes);
    }

    /// <summary>The original byte source backing this document, or <see langword="null"/> for a synthetic document with no backing file (e.g. the result of <c>Pdf.Merge</c>/<c>Pdf.Split</c>).</summary>
    internal ByteSource? Source => _source;

    /// <summary>The offset the source's own <c>startxref</c> pointed at, or <see langword="null"/> when unavailable (recovered via brute-force scan, or no backing source) — <see cref="SaveIncremental(string,PdfOptions?)"/> chains its new trailer's <c>/Prev</c> onto this.</summary>
    internal long? StartXrefOffset => _startXrefOffset;

    /// <summary>Opens a PDF document from a file path.</summary>
    /// <param name="path">The path to the PDF file.</param>
    /// <param name="options">Options controlling reading behavior. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException">The file could not be read, or its structure could not be recovered.</exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// </code>
    /// </example>
    public static PdfDocument Open(string path, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var opts = options ?? PdfOptions.Default;
        var source = OpenPathSource(path, opts);
        return OpenCore(source, path, opts);
    }

    /// <summary>Opens a PDF document from a seekable stream. PlumePDF does not take ownership of <paramref name="stream"/> — the caller disposes it.</summary>
    /// <param name="stream">A seekable, readable stream positioned to read the whole document from position 0.</param>
    /// <param name="options">Options controlling reading behavior. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException">The stream's content could not be recovered as a PDF document.</exception>
    public static PdfDocument Open(Stream stream, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var opts = options ?? PdfOptions.Default;
        return OpenCore(new StreamByteSource(stream), null, opts);
    }

    /// <summary>Opens a PDF document from an in-memory buffer.</summary>
    /// <param name="data">The complete document bytes.</param>
    /// <param name="options">Options controlling reading behavior. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException">The buffer's content could not be recovered as a PDF document.</exception>
    public static PdfDocument Open(ReadOnlyMemory<byte> data, PdfOptions? options = null)
    {
        var opts = options ?? PdfOptions.Default;
        return OpenCore(new StreamByteSource(data), null, opts);
    }

    /// <summary>
    /// Asynchronously opens a PDF document from a file path, reading the file's bytes
    /// without blocking the calling thread on disk I/O. When <paramref name="options"/>
    /// prefers memory-mapped I/O (the default, <see cref="PdfOptions.PreferStreamIo"/>
    /// unset) there is no true-async memory-mapping primitive in .NET, so the (fast,
    /// non-blocking-I/O) mapping setup runs on the thread pool instead — a documented Phase
    /// 1 simplification, not sync-over-async.
    /// </summary>
    public static async Task<PdfDocument> OpenAsync(string path, PdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var opts = options ?? PdfOptions.Default;

        if (opts.PreferStreamIo)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return Open(bytes, opts);
        }

        return await Task.Run(() => Open(path, opts), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously opens a PDF document from a stream. A non-seekable
    /// <paramref name="stream"/> is buffered into memory asynchronously first (documented:
    /// PlumePDF needs random access to parse cross-reference data), then parsed
    /// synchronously — parsing already-in-memory bytes is CPU-bound, not I/O.
    /// </summary>
    public static async Task<PdfDocument> OpenAsync(Stream stream, PdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var opts = options ?? PdfOptions.Default;

        if (!stream.CanSeek)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Open(buffer.ToArray().AsMemory(), opts);
        }

        return Open(stream, opts);
    }

    /// <summary>
    /// Performs a full rewrite: streams every reachable object to <paramref name="path"/>
    /// with unreferenced objects garbage-collected and every object renumbered
    /// (docs/architecture.md "Writing pipeline"). The page tree is always flattened to a
    /// single <c>/Pages</c> node reflecting <see cref="Pages"/>' current order (a documented
    /// Phase 1 simplification). Writes to a temporary file in the same directory and
    /// atomically replaces <paramref name="path"/> — safe to call with <paramref name="path"/>
    /// equal to the path this document was opened from.
    /// </summary>
    /// <param name="path">The destination path.</param>
    /// <param name="options">
    /// Options controlling the write, notably <see cref="PdfOptions.Deterministic"/>
    /// (two runs over the same input/options produce byte-identical output — never
    /// byte-identical to the source, since renumbering is not the identity transform).
    /// Defaults to the options this document was opened with.
    /// </param>
    /// <exception cref="PlumePdfException">This document's source is encrypted (<c>PLUME5001</c>) — Phase 1 does not support saving an encrypted source.</exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// document.Save("output.pdf", new PdfOptions { Deterministic = true });
    /// </code>
    /// </example>
    public void Save(string path, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HasEncryptedSource)
        {
            throw new PlumePdfException("PLUME5001", "Save is not supported on a document opened from an encrypted source (Phase 1 does not support encryption write).");
        }

        var effectiveOptions = options ?? _options;

        // A PdfAConformance carried by the effective options —
        // captured at Manuscript.Render/Compose time, or supplied directly to this call —
        // re-applies the version-knob rules here too, so a PDF/A-1B document keeps its
        // "%PDF-1.4" header even when a caller passes fresh options to Save, and an A2b save
        // with a header above the 1.7 ceiling refuses (PLUME6071) instead of silently writing
        // a non-conformant file.
        if (effectiveOptions.PdfAConformance != PdfAConformance.None)
        {
            effectiveOptions = Documents.PdfA.PdfACreationSupport.ApplyVersionKnob(effectiveOptions);
        }

        // The two Phase 6 save-time layout options are validated
        // here, at the orchestration point, before any bytes are written. Both are explicit
        // caller requests, so an unsatisfiable combination is a coded refusal — never a
        // silently different file layout than the one asked for.
        if (effectiveOptions.Optimize && effectiveOptions.Linearize)
        {
            throw new PlumePdfException("PLUME5018", "PdfOptions.Optimize and PdfOptions.Linearize cannot be combined in v1.0 — PlumePDF's linearized output uses classic cross-reference tables, not object/cross-reference streams. Drop one of the two options (linearized files are already ordered for fast first-page display; Optimize is the size play).");
        }

        if (effectiveOptions.Optimize)
        {
            EnsureVersionSupportsOptimization(effectiveOptions);
        }

        if (effectiveOptions.Linearize && Pages.Count == 0)
        {
            throw new PlumePdfException("PLUME5020", "PdfOptions.Linearize requires at least one page — linearization's entire layout (ISO 32000-1 Annex F) is organized around a first page, so a zero-page document has nothing to linearize. Save without the option instead.");
        }

        // Pages removed since open must not come back through anything that still references
        // them: the writers exclude the removed pages, the original page-tree nodes and what
        // belonged only to those pages, and the save-time clean-up rewrites (as copies — this
        // document is not modified) the bookmarks, form fields, links, open action, named
        // destinations and structure elements that pointed at them. Computed before the
        // signature scan so a signature field removed with its page is not counted as
        // invalidated.
        var cleanup = PrepareFullRewrite(effectiveOptions);

        // A full rewrite renumbers every object, which invalidates any existing
        // signature's /ByteRange (the offsets it names no longer point at the same bytes) —
        // the asymmetry SaveIncremental's own XML docs already flag ("required for signed
        // documents"). Strict mode refuses outright; the default mode proceeds with a coded
        // diagnostic naming what will break, so the caller finds out before reopening in a
        // signature-aware viewer, not after.
        IReadOnlyList<SignatureDictionaryInfo> existingSignatures;
        try
        {
            existingSignatures = SignatureDictionaryReader.ReadAll(this);
        }
        catch (PlumePdfException scanFailure)
        {
            // This scan is advisory — a document whose signature surface trips the read
            // caps (PLUME6052) must not make an ordinary full rewrite start throwing in default
            // mode. Strict propagates: an unscannable signature surface can't be proven
            // signature-free, and Strict's contract is to refuse rather than guess.
            if (effectiveOptions.Strict)
            {
                throw;
            }

            Diagnostics.Add(new PdfDiagnostic(scanFailure.Code, DiagnosticSeverity.Warning, $"Could not scan for existing signatures before this full rewrite ({scanFailure.Message}) — if the document is signed, Save invalidates its signatures; use SaveIncremental instead."));
            existingSignatures = [];
        }

        existingSignatures = [.. existingSignatures.Where(s => !cleanup.RemovedFields.Contains(s.FieldReference.Number))];

        if (existingSignatures.Count > 0)
        {
            var names = string.Join(", ", existingSignatures.Select(static s => s.FieldName));
            if (effectiveOptions.Strict)
            {
                throw new PlumePdfException("PLUME5014", $"Save (full rewrite) would invalidate {existingSignatures.Count} existing signature(s) ({names}) — every object is renumbered, which moves the bytes their /ByteRange names. Use SaveIncremental instead (PdfOptions.Strict refuses).");
            }

            Diagnostics.Add(new PdfDiagnostic("PLUME5014", DiagnosticSeverity.Warning, $"Save (full rewrite) invalidates {existingSignatures.Count} existing signature(s) ({names}); proceeding anyway — use SaveIncremental to keep them valid."));
        }

        // Windows refuses to replace a file that still has a live mapped section, no
        // matter how the original handle was shared — so saving over the currently-open file
        // first buffers the source into memory and releases the OS handles. The document
        // (and its lazy resolver) stays fully readable off the in-memory copy.
        DetachMappedSourceIfSamePath(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, $".{Path.GetFileName(path)}.plumepdf-tmp-{Guid.NewGuid():N}");

        try
        {
            using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var pages = cleanup.Pages;
                if (effectiveOptions.Linearize)
                {
                    // Two-pass staged output through this already-existing temp
                    // file (the recorded carve-out of the "no whole-document byte buffer"
                    // promise — staging through the temp FILE is fine; a whole-document MEMORY
                    // buffer is what the promise forbids).
                    Linearizer.Write(tempStream, Objects, Catalog?.Reference, cleanup.Catalog, pages, effectiveOptions, cleanup.Excluded, cleanup.Replacements);
                }
                else
                {
                    FullRewriteWriter.Write(tempStream, Objects, Catalog?.Reference, cleanup.Catalog, pages, effectiveOptions, cleanup.Excluded, cleanup.Replacements);
                }
            }

            File.Move(tempPath, path, overwrite: true);

            if (effectiveOptions.Linearize)
            {
                // Remembered for this instance's lifetime so a later
                // SaveIncremental can record the de-linearization diagnostic (PLUME5019).
                _linearizedSave = true;
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    // The exclusion set and the clean-up copies for one full rewrite of the current pages.
    private SaveCleanupContext PrepareFullRewrite(PdfOptions options)
    {
        var pages = Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var (excluded, removedPages) = RemovedSetBuilder.Build(Objects, Catalog?.Reference, Catalog?.Dictionary, _openTimePageTree, _openTimePages, pages, options);
        var context = new SaveCleanupContext(Objects, Catalog?.Reference, Catalog?.Dictionary, pages, excluded, removedPages, options);
        SaveCleanup.Compute(context);
        return context;
    }

    /// <summary>
    /// Appends changed objects and a new cross-reference section onto <paramref name="path"/>
    /// — the default save path, preserving prior revisions (required for signed documents;
    /// docs/architecture.md "Writing pipeline"). When <paramref name="path"/> is the path this
    /// document was opened from, writes through an independent append handle rather than
    /// through the read-only mapped view; otherwise copies the original bytes to
    /// <paramref name="path"/> first, then appends.
    /// </summary>
    /// <param name="path">The destination path.</param>
    /// <param name="options">Options controlling the write. Defaults to the options this document was opened with.</param>
    /// <exception cref="PlumePdfException">
    /// This document's source is encrypted and, unexpectedly, no security handler was
    /// attached at open time to re-encrypt the appended revision (<c>PLUME5002</c> — should not
    /// happen in practice, since <see cref="Open(string,PdfOptions?)"/> already authenticates
    /// and attaches one, or throws, for any encrypted source; kept as a defensive check rather
    /// than an assumption); this document has no backing byte source, e.g. the result of
    /// <c>Pdf.Merge</c> (<c>PLUME5003</c>); or the source's cross-reference data was recovered
    /// via brute-force scan and has no reliable prior <c>startxref</c> to chain onto
    /// (<c>PLUME5005</c>) — call <see cref="Save"/> instead.
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// document.Pages.RemoveAt(0);
    /// document.SaveIncremental("input.pdf");
    /// </code>
    /// </example>
    public void SaveIncremental(string path, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Redaction's "unrecoverable at the object level" exit criterion is
        // satisfiable only by a full rewrite (Save/FullRewriteWriter's reachability GC) —
        // SaveIncremental appends onto this document's own original backing source by
        // construction, which by definition still carries the unredacted bytes redaction
        // exists to remove. Always a hard refusal (no PdfOptions.Strict escape hatch, unlike
        // most of this codebase's diagnostics): there is no lenient reading of "still
        // recoverable," so there is nothing to diagnose-and-proceed with here.
        if (_redactionDirty)
        {
            throw new PlumePdfException("PLUME5016", "SaveIncremental is refused on a document with an applied redaction — an incremental update appends onto this document's own original source bytes, which still carry the unredacted content redaction removed. Call Save instead, which performs the full rewrite + garbage collection redaction's unrecoverability guarantee requires.");
        }

        // An encrypted source no longer refuses SaveIncremental outright —
        // the appended revision is re-encrypted with the already-derived file key and the
        // source's own /Encrypt dictionary (StandardSecurityHandler.EncryptString/EncryptStream
        // via IncrementalUpdateWriter). HasEncryptedSource true always implies a handler here in
        // practice (Open authenticates and attaches one, or throws, before this type ever exists
        // with HasEncryptedSource set) — this stays a defensive throw, not the common path.
        if (HasEncryptedSource && _securityHandler is null)
        {
            throw new PlumePdfException("PLUME5002", "SaveIncremental cannot re-encrypt the appended revision for this encrypted source — no security handler was attached at open time.");
        }

        if (_source is null)
        {
            throw new PlumePdfException("PLUME5003", "SaveIncremental requires a document opened from a file, stream, or byte buffer; this document has no backing byte source (e.g. the result of Pdf.Merge/Pdf.Split) — call Save instead.");
        }

        if (_startXrefOffset is not long prevOffset)
        {
            throw new PlumePdfException("PLUME5005", "The source's cross-reference data was recovered via a brute-force scan and has no reliable prior 'startxref' offset to chain an incremental update onto — call Save instead.");
        }

        var effectiveOptions = options ?? _options;

        // Linearization is destroyed by construction the moment any incremental
        // update appends onto the file — a viewer can no longer trust the first-page-first
        // hint tables. Diagnostic by default, refusal under Strict; never auto-promotion to a
        // full rewrite, which would silently invalidate a signed source's signatures (the
        // exact failure this guards against).
        if (_linearizedSave)
        {
            if (effectiveOptions.Strict)
            {
                throw new PlumePdfException("PLUME5019", "SaveIncremental after a linearized Save de-linearizes the output — an incremental update invalidates the linearized file's first-page-first hint tables by construction (ISO 32000-1 Annex F). Re-run Save with PdfOptions.Linearize for a linearized result (PdfOptions.Strict refuses; the default mode records this as a diagnostic and proceeds).");
            }

            Diagnostics.Add(new PdfDiagnostic("PLUME5019", DiagnosticSeverity.Warning, "SaveIncremental after a linearized Save de-linearizes the output (ISO 32000-1 Annex F: hint tables are invalidated by any appended update); proceeding anyway — re-run Save with PdfOptions.Linearize to produce a linearized file."));
        }

        var isSamePath = _openPath is not null && PathsReferToSameFile(_openPath, path);
        var pages = Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var topPagesReference = Catalog?.Dictionary.TryGetValue(PdfName.Get("Pages"), out var pagesValue) == true && pagesValue is PdfReference pagesRef
            ? pagesRef.Target
            : (IndirectReference?)null;

        if (isSamePath)
        {
            using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            IncrementalUpdateWriter.WriteAppendix(append, Objects, PagesTreeDirty, topPagesReference, pages, prevOffset, effectiveOptions, _securityHandler);
        }
        else
        {
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _source.CopyTo(output);
            IncrementalUpdateWriter.WriteAppendix(output, Objects, PagesTreeDirty, topPagesReference, pages, prevOffset, effectiveOptions, _securityHandler);
        }
    }

    /// <summary>Asynchronous equivalent of <see cref="Save"/>; the write is offloaded to the thread pool.</summary>
    public Task SaveAsync(string path, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Save(path, options), cancellationToken);

    /// <summary>Asynchronous equivalent of <see cref="SaveIncremental(string,PdfOptions?)"/>; the write is offloaded to the thread pool.</summary>
    public Task SaveIncrementalAsync(string path, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => SaveIncremental(path, options), cancellationToken);

    /// <summary>Releases the underlying byte source (memory-mapped view or stream), if any. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source?.Dispose();
    }

    /// <summary>Creates a synthetic, in-memory document with no backing byte source — used by <c>Pdf.Merge</c>/<c>Pdf.Split</c>. Delegates to <see cref="CreateSynthetic(IObjectSource,DiagnosticCollection,PdfOptions)"/> with <see cref="PdfOptions.Default"/>.</summary>
    /// <param name="objectSource">The composed object graph.</param>
    /// <param name="diagnostics">Deviations recorded while composing (e.g. an imported page originating from an encrypted source), or <see langword="null"/> for none.</param>
    internal static PdfDocument CreateSynthetic(IObjectSource objectSource, DiagnosticCollection? diagnostics = null) =>
        CreateSynthetic(objectSource, diagnostics ?? new DiagnosticCollection(), PdfOptions.Default);

    /// <summary>
    /// Creates a synthetic, in-memory document with no backing byte source, under caller-supplied
    /// options — the route <see cref="Manuscript.Render(PdfOptions?)"/> and <see cref="Compose"/>
    /// use, so a composed document's write behavior (e.g. <see cref="PdfOptions.Deterministic"/>)
    /// is controlled by the same options passed to <c>Render</c>/<c>Compose</c>, not hardcoded.
    /// </summary>
    /// <param name="objectSource">The composed object graph.</param>
    /// <param name="diagnostics">Deviations recorded while composing.</param>
    /// <param name="options">The options this document is considered composed under; also the default for its later <see cref="Save"/>/<see cref="SaveIncremental(string,PdfOptions?)"/> calls.</param>
    internal static PdfDocument CreateSynthetic(IObjectSource objectSource, DiagnosticCollection diagnostics, PdfOptions options) =>
        new(
            source: null,
            openPath: null,
            objectSource,
            options,
            diagnostics,
            hasEncryptedSource: false,
            startXrefOffset: null,
            nextObjectNumber: NextObjectNumberFromTrailerSize(objectSource.Trailer));

    // A synthetic (Merge/Split/Compose) document is fully in-memory and self-authored — its own
    // trailer's /Size is the authority on how many object numbers it already uses (there is no
    // recovered/lenient cross-reference table to double-check against, unlike OpenCore below).
    // No free list: a synthetic source never carries deleted-object history to reuse.
    private static int NextObjectNumberFromTrailerSize(PdfDictionary trailer) =>
        trailer.TryGetValue(PdfName.Size, out var sizeValue) && sizeValue is PdfNumber { IsInteger: true } size && size.TryToInt32(out var converted) && converted > 0
            ? converted
            : 1;

    /// <summary>
    /// Composes a new document from a fluent, nested-closure description of one section
    /// (Variant A) — builds a <see cref="Manuscript"/> internally and renders it.
    /// Use <see cref="Manuscript"/>/<see cref="Elements.Section"/> directly instead when you
    /// need to build, inspect, or transform the composed structure as data before rendering
    /// it (the "Compose vs Manuscript" rule).
    /// </summary>
    /// <param name="compose">Describes the section's page size, margins, header, body, and footer.</param>
    /// <param name="options">Options controlling layout resource limits and write determinism. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PdfLayoutException">
    /// <paramref name="compose"/> never calls <c>page.Content()</c>, describes a container with
    /// no content, or the described element tree cannot be laid out within the space it is
    /// given.
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Compose(page =>
    /// {
    ///     page.Size(PageSize.A4).Margin(40);
    ///     page.Content().Text("Bill to: Acme Corp");
    /// });
    /// document.Save("invoice.pdf");
    /// </code>
    /// </example>
    public static PdfDocument Compose(Action<Compose.PageDescriptor> compose, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(compose);

        var page = new Compose.PageDescriptor();
        compose(page);
        var manuscript = new Manuscript { Sections = [page.Build()] };
        return ManuscriptRenderer.Render(manuscript, options ?? PdfOptions.Default);
    }

    private static ByteSource OpenPathSource(string path, PdfOptions options)
    {
        if (options.PreferStreamIo)
        {
            return new StreamByteSource(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ownsStream: true);
        }

        try
        {
            return new MemoryMappedByteSource(path);
        }
        catch (PlumePdfException ex) when (ex.Code is "PLUME1003" or "PLUME1004")
        {
            // Empty file, or the platform/filesystem couldn't memory-map it: fall back to
            // buffered stream I/O rather than failing outright.
            return new StreamByteSource(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ownsStream: true);
        }
    }

    private static PdfDocument OpenCore(ByteSource source, string? openPath, PdfOptions options)
    {
        var diagnostics = new DiagnosticCollection();
        try
        {
            var table = CrossReferenceReader.ReadWithRecovery(source, options, diagnostics);
            var resolver = new ObjectResolver(source, table, options, diagnostics);
            var hasEncryptedSource = table.Trailer.ContainsKey(PdfName.Encrypt);
            int? rawPermissions = null;
            StandardSecurityHandler? securityHandler = null;
            if (hasEncryptedSource)
            {
                var validated = ValidateEncryption(resolver, table.Trailer, options);
                rawPermissions = validated?.Permissions;
                securityHandler = validated?.Handler;
            }

            // The allocator's starting point must clear every object number this
            // document is already known to use, which is not always table.Trailer's own /Size —
            // a lenient/recovered table can have a real maximum object number the declared
            // /Size undercounts. Taking the max of both, rather than trusting /Size alone, is
            // what the AllocatorTests "collision with existing numbers" case guards.
            var highestKnownNumber = table.EntriesByObjectNumber.Count == 0 ? 0 : table.EntriesByObjectNumber.Keys.Max();
            var declaredSize = table.Trailer.TryGetValue(PdfName.Size, out var sizeValue) && sizeValue is PdfNumber { IsInteger: true } size && size.TryToInt32(out var sizeInt) ? sizeInt : 0;
            var nextObjectNumber = Math.Max(highestKnownNumber + 1, Math.Max(declaredSize, 0));
            // ISO 32000-1 §7.5.4: a free entry whose generation reached 65535 must never be
            // reused — but CrossReferenceEntry does not retain the free generation, so the
            // only always-safe policy is to reuse NO free-list numbers at all and allocate
            // monotonically past the highest known number. Slightly less
            // compact output; never a resurrected-object collision.
            int[] freeObjectNumbers = [];

            return new PdfDocument(source, openPath, resolver, options, diagnostics, hasEncryptedSource, table.StartXrefOffset, nextObjectNumber, freeObjectNumbers, rawPermissions, securityHandler);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    // Authenticates against PdfOptions' passwords (throwing a coded PLUME4xxx exception if
    // none work, satisfying "open cleanly or throw a coded exception" for encrypted sources),
    // then attaches the resulting handler to the resolver so every object resolved from this
    // point on transparently decrypts its strings/streams (§7.6.2) on first materialization.
    // Resolving the /Encrypt dictionary itself happens above, before the handler exists to
    // attach - which is exactly correct, since its own strings must never be decrypted
    // (§7.6.1) and this resolution is what leaves it permanently cached that way.
    // Returns the raw /P permission bits (surfaced publicly as PdfDocument.Permissions) and the
    // handler itself (kept by PdfDocument so SaveIncremental's re-encryption
    // can reuse the same already-derived file key) once authentication succeeds, or null when
    // there was no encryption dictionary to authenticate against after all.
    private static (int Permissions, StandardSecurityHandler Handler)? ValidateEncryption(ObjectResolver resolver, PdfDictionary trailer, PdfOptions options)
    {
        var encryptValue = trailer[PdfName.Encrypt];
        var encryptDict = encryptValue switch
        {
            PdfReference reference => resolver.Resolve(reference.Target) as PdfDictionary,
            PdfDictionary direct => direct,
            _ => null,
        };

        if (encryptDict is null)
        {
            return null;
        }

        byte[]? fileId = null;
        if (trailer.TryGetValue(PdfName.Id, out var idValue) && idValue is PdfArray { Count: > 0 } idArray && idArray[0] is PdfString id0)
        {
            fileId = id0.Bytes.ToArray();
        }

        var handler = new StandardSecurityHandler(encryptDict, fileId, options);
        resolver.AttachSecurityHandler(handler);
        return (handler.Permissions, handler);
    }

    /// <summary>
    /// Releases this document's memory-mapped view when <paramref name="outputPath"/>
    /// names the same file it was opened from — Windows refuses to replace a file that still
    /// has a live mapped section, no matter how the original handle was shared. Shared by
    /// <see cref="Save"/> and <see cref="SignatureCollection"/>'s signing/DSS/timestamp writes,
    /// every one of which may write back over the currently-open path. The document (and its
    /// lazy resolver) stays fully readable off the in-memory copy afterward.
    /// </summary>
    internal void DetachMappedSourceIfSamePath(string outputPath)
    {
        if (_openPath is not null && _source is IO.MemoryMappedByteSource mapped && PathsReferToSameFile(_openPath, outputPath))
        {
            mapped.DetachFromFile();
        }
    }

    private static bool PathsReferToSameFile(string a, string b)
    {
        try
        {
            // Case-insensitive comparison is only correct where the filesystem is (Windows,
            // macOS defaults); on Linux "input.pdf" and "Input.pdf" are different files, and
            // treating them as the same would append to a brand-new file instead of copying
            // the source first — silently producing a corrupt appendix-only "PDF".
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
        }
        catch (ArgumentException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
