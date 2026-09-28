namespace PlumePdf;

/// <summary>
/// Options controlling how PlumePDF reads and writes a document. Immutable once
/// constructed — use object initializers or <c>with</c> off <see cref="Default"/>.
/// Resource-limit properties thread through the tokenizer, parser,
/// cross-reference reader, and filter pipeline from their first call — every one of them
/// guards against a specific untrusted-input hazard (unbounded nesting, unbounded
/// cross-reference chains, decompression bombs, brute-force scans, object-stream cycles)
/// rather than relying on the .NET runtime to eventually run out of memory.
/// </summary>
/// <example>
/// <code>
/// var strict = PdfOptions.Default with { Strict = true };
/// using var doc = PdfDocument.Open("input.pdf", strict);
/// </code>
/// </example>
public sealed record PdfOptions
{
    /// <summary>The default options: lenient reading, no passwords, mmap I/O, conservative resource limits.</summary>
    public static PdfOptions Default { get; } = new();

    /// <summary>
    /// When <see langword="true"/>, writers produce output whose bytes are identical across
    /// repeated runs for the same input and options: a fixed document ID, fixed or omitted
    /// timestamps, and stable object ordering. The backbone of agent self-verification and
    /// reproducible builds (<c>docs/agent-forward.md</c>). Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Scoping (one phase ahead of the identical Phase-8 concession): for raster
    /// encode paths (<c>RasterImage.EncodePng</c>/<c>EncodeJpeg</c>, <c>Pdf.FromImages</c>' own
    /// re-encode path), byte-identity holds only <em>on the same platform, architecture, and
    /// .NET runtime version</em> — not universally. <see cref="System.IO.Compression.ZLibStream"/>
    /// (which <c>FlateDecode</c>'s encoder, and PNG's IDAT encoding, both use) emits bytes that
    /// are a property of the runtime's own zlib implementation, and a JPEG float-DCT's exact
    /// rounding is an IEEE-754 cross-platform question this repo measures rather than assumes.
    /// DCT pass-through (embedding an already-JPEG source's original bytes unchanged) is
    /// byte-identical everywhere by construction, since nothing is re-encoded. No raster path
    /// promises cross-platform byte-identity in v1.0; building a deterministic in-house deflate
    /// is explicitly out of scope (1.x backlog).
    /// <para>
    /// Scoping: the same same-platform/architecture/.NET-runtime-version scope above extends to the
    /// <c>Rasterize</c> pixel-generation path (the display-list interpreter and the AGG23-derived
    /// scan converter) — the raw BGRA pixels PlumePDF produces for a given page are byte-identical
    /// across repeated runs only on the same platform, not across platforms. This is a documented,
    /// deliberate exception to the norm that
    /// anything outside a <see cref="Deterministic"/> guarantee's exact scope must be a coded
    /// refusal rather than silently non-reproducible output: no per-call refusal is buildable here
    /// because the calling platform is undetectable at call time. Cross-platform rendering
    /// agreement is instead covered by the SSIM-vs-PDFium oracle (<c>tests/PlumePdf.CorpusTests</c>);
    /// the byte-identical PNG golden pins one linux platform. The deterministic raster path itself
    /// uses integer/fixed-point math throughout (no platform <c>libm</c> transcendentals, no
    /// unordered floating-point summation, no <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
    /// iteration-order dependence, no <see cref="System.Threading.Tasks.Parallel"/> in the scan
    /// converter) so that the same-platform promise above actually holds for the pixels themselves,
    /// not only for the PNG bytes downstream of them.
    /// </para>
    /// </remarks>
    public bool Deterministic { get; init; }

    /// <summary>
    /// When <see langword="true"/>, deviations that would otherwise be recorded to
    /// <c>doc.Diagnostics</c> and tolerated instead throw a coded <see cref="PlumePdfException"/>.
    /// For validator/compliance scenarios that need to know a source PDF is not well-formed,
    /// rather than have PlumePDF quietly repair it. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>The user password to try when opening an encrypted document, if any.</summary>
    public string? UserPassword { get; init; }

    /// <summary>The owner password to try when opening an encrypted document, if any.</summary>
    public string? OwnerPassword { get; init; }

    /// <summary>
    /// When <see langword="true"/>, a path-based <c>Open</c> uses a buffered <see cref="System.IO.FileStream"/>
    /// instead of memory-mapping the file. Opt into this when the source file may be
    /// truncated or replaced by another process while open, or on platforms/filesystems
    /// where memory-mapping is unreliable. Defaults to <see langword="false"/> (mmap by default).
    /// </summary>
    public bool PreferStreamIo { get; init; }

    /// <summary>
    /// The filter registry consulted whenever PlumePDF decodes or encodes a stream's
    /// <c>/Filter</c> chain on this document — object streams, cross-reference streams,
    /// image/content-stream payloads, and (on the write path) newly-created streams.
    /// Defaults to <see cref="PdfFilterRegistry.Default"/>, so supplying a custom
    /// <see cref="PdfOptions"/> with this left unset preserves existing decode/encode
    /// behavior exactly. Every ISO 32000-1 §7.4 filter — including <c>JBIG2Decode</c> and
    /// <c>JPXDecode</c> — is registered by default; set this to a registry with
    /// additional <see cref="IPdfFilter"/>/<see cref="IPdfEncodingFilter"/> registrations to
    /// reach a vendor codec PlumePDF doesn't ship, or to override a built-in one (e.g. to
    /// refuse a filter instead of decoding it) — the seam <c>docs/architecture.md</c> names
    /// as the public extension point.
    /// </summary>
    /// <example>
    /// <code>
    /// var registry = new PdfFilterRegistry();
    /// registry.Register("AcmeCompress", new MyAcmeCompressFilter());
    /// var withAcme = PdfOptions.Default with { Filters = registry };
    /// using var doc = PdfDocument.Open("scanned.pdf", withAcme);
    /// </code>
    /// </example>
    public PdfFilterRegistry Filters { get; init; } = PdfFilterRegistry.Default;

    /// <summary>
    /// The maximum number of bytes a single filter decode (e.g. Flate) may produce before
    /// it is refused as a likely decompression bomb. Defaults to 256 MiB.
    /// </summary>
    public long MaxDecompressedStreamBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// The maximum nesting depth (arrays within arrays, dictionaries within dictionaries,
    /// or a mix) the object parser will descend before refusing to continue. Defaults to 64.
    /// </summary>
    public int MaxObjectNestingDepth { get; init; } = 64;

    /// <summary>
    /// The maximum number of cross-reference sections the <c>/Prev</c> chain walk will
    /// follow before refusing to continue, independent of the cycle guard (which catches
    /// a chain that revisits an offset; this catches a chain that is merely very long).
    /// Defaults to 1024.
    /// </summary>
    public int MaxCrossReferencePrevChainLength { get; init; } = 1024;

    /// <summary>
    /// The maximum number of bytes the brute-force recovery scanner (the last rung of the
    /// recovery ladder) will examine before giving up. Defaults to <see cref="long.MaxValue"/>
    /// (scan the whole source) — tighten this when processing untrusted input under a time budget.
    /// </summary>
    public long MaxBruteForceScanBytes { get; init; } = long.MaxValue;

    /// <summary>
    /// The maximum depth of an object stream's <c>/Extends</c> chain the reader will follow
    /// before refusing to continue and reporting a cycle. Defaults to 32.
    /// </summary>
    public int MaxObjectStreamExtendsDepth { get; init; } = 32;

    /// <summary>
    /// The maximum number of entries an object stream's <c>/N</c> is trusted for outright,
    /// independent of whether its decoded payload could plausibly hold that many (which is
    /// checked too, and is usually the tighter bound). Guards a hostile <c>/N</c> against
    /// allocating an absurdly large array before the payload-size check even runs. Defaults
    /// to 1,000,000 — far beyond any real-world object stream, which typically holds tens to
    /// low thousands of compressed objects.
    /// </summary>
    public int MaxObjectStreamEntries { get; init; } = 1_000_000;

    /// <summary>
    /// The maximum size, in bytes, of a single font file PlumePDF will parse when embedding a
    /// caller-supplied TrueType/OpenType font (<c>PdfFont.FromFile</c>/<c>FromBytes</c>).
    /// Guards against a hostile or accidentally huge font file being read into memory before
    /// any table validation happens (the untrusted-font-input hardening every resource limit
    /// here follows). Defaults to 64 MiB — far beyond any real-world
    /// TrueType/OpenType font, which typically ranges from tens of KB to a few MB.
    /// </summary>
    /// <example>
    /// <code>
    /// var strictFonts = PdfOptions.Default with { MaxFontFileBytes = 8L * 1024 * 1024 };
    /// var font = PdfFont.FromFile("untrusted.ttf", strictFonts);
    /// </code>
    /// </example>
    public long MaxFontFileBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// The maximum number of glyphs a parsed font's <c>maxp</c> table is trusted for, before
    /// PlumePDF refuses to continue parsing it as a likely hostile or corrupt value. Guards
    /// against a crafted glyph count driving an oversized allocation in the SFNT reader.
    /// Defaults to 65,535 (the largest glyph ID a 16-bit <c>glyf</c>/<c>loca</c> font format
    /// can address), matching the format's own ceiling rather than an arbitrary lower number.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxFontGlyphCount = 20_000 };
    /// </code>
    /// </example>
    public int MaxFontGlyphCount { get; init; } = 65_535;

    /// <summary>
    /// The maximum depth of composite-glyph nesting (a glyph made of component references to
    /// other glyphs, which may themselves be composite) PlumePDF will resolve before refusing
    /// to continue. Composite-glyph resolution walks a work queue, never recursion, so this
    /// limit exists to reject cyclic or absurdly deep composite chains cheaply rather than to
    /// guard the call stack. Defaults to 16 — far beyond any real-world font's composite
    /// nesting, which is rarely more than 2–3 levels deep.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxCompositeGlyphDepth = 4 };
    /// </code>
    /// </example>
    public int MaxCompositeGlyphDepth { get; init; } = 16;

    /// <summary>
    /// The maximum number of distinct glyphs a single font subset may retain. Guards against a
    /// pathological Manuscript (e.g. programmatically generated text covering an enormous
    /// codepoint range) driving the subsetter to retain most of a large font's glyph set,
    /// defeating the point of subsetting and ballooning output size. Defaults to 10,000 — far
    /// beyond the glyph count any single realistic document's text actually uses.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxSubsetGlyphs = 2_000 };
    /// </code>
    /// </example>
    public int MaxSubsetGlyphs { get; init; } = 10_000;

    /// <summary>
    /// The maximum number of Element nodes a single Manuscript's layout tree may contain
    /// before the layout engine refuses to continue. Guards a runaway document-generating
    /// agent (e.g. an unbounded loop appending Elements) against building a tree so large that
    /// layout, font shaping, and content-stream generation stall or exhaust memory before the
    /// caller notices. Defaults to 100,000.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxLayoutElementCount = 5_000 };
    /// </code>
    /// </example>
    public int MaxLayoutElementCount { get; init; } = 100_000;

    /// <summary>
    /// The maximum number of Pages a single <c>Manuscript.Render</c>/<c>PdfDocument.Compose</c>
    /// call may produce via pagination before refusing to continue. Guards a runaway
    /// document-generating agent or a pathologically small page size against an effectively
    /// unbounded page count. Defaults to 10,000.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxRenderedPages = 500 };
    /// </code>
    /// </example>
    public int MaxRenderedPages { get; init; } = 10_000;

    /// <summary>
    /// The maximum number of content-stream operators <c>PlumePdf.Content</c>'s reader will
    /// process for a single page (or form XObject) before refusing to continue. Guards a
    /// hostile or pathological content stream — an operator-count bomb that is small on disk
    /// but decodes to millions of trivial operators — from stalling text/graphics extraction
    /// indefinitely. Defaults to 5,000,000 — far beyond any real-world page, which typically
    /// carries at most tens of thousands of operators even for dense vector art.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxContentStreamOperators = 200_000 };
    /// </code>
    /// </example>
    public int MaxContentStreamOperators { get; init; } = 5_000_000;

    /// <summary>
    /// The maximum number of positioned <c>Letter</c>s a single page's text extraction may
    /// produce before refusing to continue. Guards a hostile content stream that paints an
    /// enormous number of degenerate (e.g. zero-width, fully overlapping) glyphs against an
    /// effectively unbounded extraction result. Defaults to 1,000,000 — far beyond any
    /// real-world page's actual glyph count.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxLettersPerPage = 50_000 };
    /// </code>
    /// </example>
    public int MaxLettersPerPage { get; init; } = 1_000_000;

    /// <summary>
    /// The maximum number of entries (<c>bfchar</c>/<c>bfrange</c>/<c>cidchar</c>/<c>cidrange</c>
    /// mappings, combined) a single parsed CMap — a <c>/ToUnicode</c> stream or a Type0 font's
    /// embedded <c>/Encoding</c> CMap — is trusted for before PlumePDF stops adding entries and
    /// records a diagnostic. Guards a hostile CMap stream that declares an entry-count bomb
    /// against an unbounded in-memory map. Defaults to 1,000,000 — far beyond any real-world
    /// CMap, which typically maps at most tens of thousands of codes.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxCMapEntries = 100_000 };
    /// </code>
    /// </example>
    public int MaxCMapEntries { get; init; } = 1_000_000;

    /// <summary>
    /// The maximum depth of nested form XObject <c>Do</c> invocations text/image extraction
    /// will recurse into before refusing to continue. Extraction walks form XObject recursion
    /// on a work stack, not the call stack, so this limit exists to reject cyclic or absurdly
    /// deep XObject nesting cheaply rather than to guard against a native stack overflow.
    /// Defaults to 32 — far beyond any real-world document's form XObject nesting, which is
    /// rarely more than 2-3 levels deep.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxXObjectNestingDepth = 8 };
    /// </code>
    /// </example>
    public int MaxXObjectNestingDepth { get; init; } = 32;

    /// <summary>
    /// The maximum number of fields a document's AcroForm <c>/Fields</c> tree will enumerate
    /// before refusing to continue. Guards a hostile or pathological field tree — an
    /// enormous, mostly-empty forest of field dictionaries — from driving field-tree reading
    /// or <c>doc.Form.Fields</c> enumeration to stall or exhaust memory. Defaults to 100,000 —
    /// far beyond any real-world form, which typically has at most a few hundred fields (the
    /// f1040 exit-demo fixture has 136).
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxFormFields = 5_000 };
    /// </code>
    /// </example>
    public int MaxFormFields { get; init; } = 100_000;

    /// <summary>
    /// The maximum depth of nested field <c>/Kids</c> the AcroForm field-tree reader will
    /// descend before refusing to continue. Independent of the reader's own cycle guard
    /// (which tracks visited object numbers and catches a shared-node cycle regardless of
    /// depth); this limit rejects a merely very deep — not necessarily cyclic — field tree.
    /// Defaults to 64, matching <see cref="MaxObjectNestingDepth"/>'s object-graph depth cap —
    /// real-world XFA-style field hierarchies (e.g. <c>topmostSubform[0].Page1[0].c1_01[0]</c>)
    /// rarely exceed 5-10 levels.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxFieldTreeDepth = 16 };
    /// </code>
    /// </example>
    public int MaxFieldTreeDepth { get; init; } = 64;

    /// <summary>
    /// The maximum number of widget annotations a single page's <c>/Annots</c> array is
    /// trusted for when reading field/widget annotations. Guards a hostile page declaring an
    /// enormous <c>/Annots</c> array from driving widget-annotation reading to stall or
    /// exhaust memory before the caller notices. Defaults to 10,000 — far beyond any
    /// real-world page's widget count, which is typically in the tens to low hundreds even for
    /// dense multi-column government forms.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxWidgetsPerPage = 1_000 };
    /// </code>
    /// </example>
    public int MaxWidgetsPerPage { get; init; } = 10_000;

    /// <summary>
    /// The maximum size, in bytes, of a single generated widget appearance stream
    /// (<c>Fill</c>'s appearance-generation path) before generation refuses to continue.
    /// Guards a pathological combination — an
    /// auto-size <c>/DA</c> (<c>0 Tf</c>) over an enormous multiline text value in a tiny
    /// <c>/Rect</c> — from producing an unbounded content stream. Defaults to 1 MiB, far beyond
    /// any real-world field's appearance content, which is typically well under 1 KiB.
    /// </summary>
    /// <remarks>
    /// Enforced by <c>Documents.Forms.Appearances.AppearanceGenerator</c> — every real appearance stream Fill's appearance-generation path
    /// synthesizes is checked against this cap before being written; <c>/NeedAppearances</c>
    /// (<see cref="NeedAppearances"/>) remains the caller's escape hatch for skipping
    /// generation entirely, but it is no longer the only path this cap has to reason about.
    /// </remarks>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxGeneratedAppearanceBytes = 64 * 1024 };
    /// </code>
    /// </example>
    public long MaxGeneratedAppearanceBytes { get; init; } = 1024L * 1024;

    /// <summary>
    /// When <see langword="true"/>, a fill call (<c>Pdf.FillForm</c>/<c>doc.Form.Fill</c>) sets
    /// the AcroForm dictionary's <c>/NeedAppearances</c> flag instead of generating a normal
    /// appearance stream (<c>/AP /N</c>) for each field it fills — an explicit escape hatch for
    /// callers who accept viewer-side appearance regeneration and don't need the result to be
    /// <c>Flatten</c>-able. Fill-time only:
    /// this option has no effect on <c>Flatten</c>, which always requires a real appearance
    /// stream — generated or pre-existing — since flattening bakes an appearance into page
    /// content and there is nothing to bake in if none exists. Defaults to
    /// <see langword="false"/> (PlumePDF generates appearances itself by default; real-world
    /// forms like the f1040 exit-demo fixture set no <c>/NeedAppearances</c> and 97 of its 136
    /// fields have no pre-existing <c>/AP</c>, so relying on the flag would leave most fields
    /// with no visible content in a viewer that doesn't honour it).
    /// </summary>
    /// <example>
    /// <code>
    /// var viewerRegenerated = PdfOptions.Default with { NeedAppearances = true };
    /// Pdf.FillForm("form.pdf", new Dictionary&lt;string, string&gt; { ["Name"] = "Jane Doe" }, options: viewerRegenerated);
    /// </code>
    /// </example>
    public bool NeedAppearances { get; init; }

    // --- Phase 5 (Digital signatures) caps ------------------------------------------------
    // Every new untrusted-input surface signing
    // introduces gets its own per-hazard cap, same discipline as every property above: a
    // document-supplied signature dictionary is exactly as adversarial as any other
    // document-supplied structure, and a TSA/OCSP responder is exactly as adversarial as any
    // other network peer PlumePDF was never designed to trust by default.

    /// <summary>
    /// The maximum size, in bytes, of a single document-supplied signature or
    /// <c>/DocTimeStamp</c> dictionary's <c>/Contents</c> hex string PlumePDF will read before
    /// refusing to continue. The read-side decompression-bomb analog for signature content:
    /// a hostile document could otherwise declare an absurdly long <c>/Contents</c> hex
    /// string to drive an oversized allocation before verification ever inspects a single
    /// byte of it. Defaults to 4 MiB — far beyond any real-world CMS blob, which typically
    /// runs from a few KB (B-B, no timestamp) to tens of KB even for a B-LTA signature
    /// carrying a full certificate chain, an RFC 3161 token, and embedded revocation data.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxSignatureContentsBytes = 512 * 1024 };
    /// </code>
    /// </example>
    public long MaxSignatureContentsBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>
    /// The maximum number of offset/length segments a document-supplied signature or
    /// <c>/DocTimeStamp</c> dictionary's <c>/ByteRange</c> array is trusted for before
    /// PlumePDF refuses to continue reading it. Guards a hostile <c>/ByteRange</c> declaring
    /// an enormous number of segments from driving ByteRange-coverage verification (a
    /// first-class, un-hideable property of every verification result) to stall or exhaust
    /// memory. Defaults to 16 — a real signature's <c>/ByteRange</c> is, in practice, always
    /// exactly 2 segments (4 integers: before <c>/Contents</c>, after it), so 16 is already
    /// generous headroom for producer variance PlumePDF has never observed.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxByteRangeSegments = 4 };
    /// </code>
    /// </example>
    public int MaxByteRangeSegments { get; init; } = 16;

    /// <summary>
    /// The maximum number of certificates verification's chain-building code will walk when
    /// resolving a signature's certificate chain, before refusing to continue. Guards a
    /// hostile or pathological chain
    /// (e.g. a document-supplied <c>/DSS</c> engineered to make chain-building loop through an
    /// enormous number of candidate issuers) from stalling verification. Defaults to 16 — far
    /// beyond any real-world chain, which is rarely more than 3-4 certificates deep (leaf,
    /// one or two intermediates, root).
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxCertificateChainDepth = 6 };
    /// </code>
    /// </example>
    public int MaxCertificateChainDepth { get; init; } = 16;

    /// <summary>
    /// The maximum certificate-chain length <c>SignatureCollection.AddLtvAsync</c> will walk,
    /// per existing signature, while assembling a fresh <c>/DSS</c> to <em>write</em>. Guards a
    /// pathological or adversarial certificate chain embedded in one of the document's own
    /// signatures from driving an unbounded number of OCSP/CRL fetches during LTV material
    /// collection. Not a read-side guard — PlumePDF never enumerates a document-supplied
    /// <c>/DSS</c>'s own arrays at all (there is no such read path this phase). Defaults to
    /// 256 — far beyond any real-world chain, which typically runs 2-4 certificates deep.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxDssCertificates = 64 };
    /// </code>
    /// </example>
    public int MaxDssCertificates { get; init; } = 256;

    /// <summary>
    /// The maximum combined number of OCSP/CRL entries <c>SignatureCollection.AddLtvAsync</c>
    /// will collect across every existing signature before refusing to write the resulting
    /// <c>/DSS</c>. Guards a chain that yields an unbounded amount of fetched revocation
    /// material from being embedded uncapped. Not a read-side guard — PlumePDF never enumerates
    /// a document-supplied <c>/DSS</c>'s own arrays at all (there is no such read path this
    /// phase). Defaults to 256 — far beyond any real-world document's revocation material, even
    /// one accumulating OCSP responses across several B-LT/B-LTA maintenance cycles.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxDssRevocationEntries = 64 };
    /// </code>
    /// </example>
    public int MaxDssRevocationEntries { get; init; } = 256;

    /// <summary>
    /// The request timeout for an <c>ITimestampAuthority</c> call (RFC 3161) made through a
    /// caller-opted-in client — never consulted unless the caller supplies a timestamp
    /// authority (offline by default).
    /// Guards a slow or hostile TSA from hanging a signing call indefinitely. Defaults to 30
    /// seconds — generous for a well-behaved TSA (a real RFC 3161 round trip is typically
    /// sub-second) while still bounding the worst case for an interactive caller.
    /// </summary>
    /// <example>
    /// <code>
    /// var fastFail = PdfOptions.Default with { TimestampTimeout = TimeSpan.FromSeconds(5) };
    /// </code>
    /// </example>
    public TimeSpan TimestampTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The per-call timeout <c>Objects.Signing.CertificateChainResolver.CollectRevocationMaterialAsync</c>
    /// enforces around every individual <c>IRevocationFetcher</c> call (OCSP/CRL) during
    /// <c>SignatureCollection.AddLtvAsync</c> — never consulted unless the caller opts into LTV
    /// (offline by default). Enforced independently of whatever timeout the supplied
    /// <see cref="IO.Http.HttpRevocationFetcher"/> (or any other <c>IRevocationFetcher</c>
    /// implementation) may or may not apply internally, so a slow or hostile OCSP responder or
    /// CRL distribution point cannot hang an LTV call indefinitely even behind a fetcher with no
    /// timeout of its own. Defaults to 30 seconds, matching <see cref="TimestampTimeout"/>'s
    /// reasoning for the same class of network peer.
    /// </summary>
    /// <example>
    /// <code>
    /// var fastFail = PdfOptions.Default with { RevocationTimeout = TimeSpan.FromSeconds(5) };
    /// </code>
    /// </example>
    public TimeSpan RevocationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The maximum size, in bytes, of a single RFC 3161 timestamp-token response PlumePDF
    /// will accept from an <c>ITimestampAuthority</c> before refusing it. Guards a hostile or
    /// misbehaving TSA from streaming an unbounded response into memory. Defaults to 1 MiB —
    /// far beyond any real-world timestamp token, which typically runs a few KB.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxTimestampResponseBytes = 128 * 1024 };
    /// </code>
    /// </example>
    public long MaxTimestampResponseBytes { get; init; } = 1024L * 1024;

    /// <summary>
    /// The maximum size, in bytes, of a single OCSP response or CRL PlumePDF will accept from
    /// an <c>IRevocationFetcher</c> before refusing it. Guards a hostile or misbehaving
    /// responder/distribution point from streaming an unbounded response into memory. Defaults
    /// to 16 MiB — an OCSP response is typically well under a KB, but a large certificate
    /// authority's full CRL can legitimately run to several MiB, so this cap sits well above
    /// real-world CRL sizes rather than the much smaller OCSP-response norm.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxRevocationResponseBytes = 4L * 1024 * 1024 };
    /// </code>
    /// </example>
    public long MaxRevocationResponseBytes { get; init; } = 16L * 1024 * 1024;

    // --- Phase 6 (Compliance & polish) shared primitives ----------------------------------
    // PdfVersion is a write-time knob, not a
    // resource-limit cap (it belongs here anyway — every writer-facing option lives on this
    // one record); the four caps below follow the same per-hazard discipline as every prior
    // phase's caps: a document-supplied structure tree, a caller-supplied XMP packet, and a
    // caller-supplied redaction target list are each exactly as capable of being pathological
    // as any other untrusted-input surface this type already guards.

    /// <summary>
    /// The PDF header version <see cref="PdfDocument.Save"/> (the full-rewrite path)
    /// writes — <c>%PDF-&lt;value&gt;</c>. Defaults to <c>"1.7"</c>, matching every version
    /// PlumePDF has ever written before this option existed. PDF/A-1b
    /// (<see cref="PdfAConformance.A1b"/>) requires
    /// <c>"1.4"</c> — no object streams or cross-reference streams exist at that version, so a
    /// PDF/A-1b save also implies not using Phase 6's optimization switch; every other
    /// conformance target keeps the default. Ignored by
    /// <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/>, which always preserves
    /// the source's own header untouched — an incremental update never rewrites bytes that
    /// came before the first revision.
    /// </summary>
    /// <example>
    /// <code>
    /// var pdfA1b = PdfOptions.Default with { PdfVersion = "1.4" };
    /// document.Save("archival.pdf", pdfA1b);
    /// </code>
    /// </example>
    public string PdfVersion { get; init; } = "1.7";

    /// <summary>
    /// When <see langword="true"/>, <see cref="PdfDocument.Save"/> (the full-rewrite path)
    /// packs every compressible object into object streams (<c>/Type /ObjStm</c>,
    /// ISO 32000-1 §7.5.7) and writes a cross-reference stream (§7.5.8) instead of a classic
    /// cross-reference table — typically a substantially smaller file for object-heavy
    /// documents, since dictionary-and-array clutter compresses together instead of being
    /// written as bare text. Requires <see cref="PdfVersion"/> 1.5 or later (where both
    /// constructs were introduced): a save with this set under an earlier version — including
    /// the <c>1.4</c> that <see cref="PdfAConformance.A1b"/> forces — is a coded refusal
    /// (<c>PLUME5017</c>), never a silently un-optimized file. Stream
    /// objects themselves, and the cross-reference stream, stay direct per §7.5.7. Ignored by
    /// <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/>, which always appends in
    /// the source's own style. Defaults to <see langword="false"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// document.Save("smaller.pdf", PdfOptions.Default with { Optimize = true });
    /// </code>
    /// </example>
    public bool Optimize { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="PdfDocument.Save"/> writes a linearized
    /// ("fast web view") file per ISO 32000-1 Annex F: first-page objects first, a
    /// linearization parameter dictionary in the first 1024 bytes, and a primary hint stream,
    /// so a byte-range-capable viewer can render page one before the rest of the file
    /// arrives. A <c>Save</c>-time-only option: a later
    /// <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/> on the same document
    /// de-linearizes by construction and records a <c>PLUME5019</c> diagnostic
    /// (<see cref="Strict"/> upgrades it to a refusal). Not combinable with
    /// <see cref="Optimize"/> in v1.0 (<c>PLUME5018</c>): linearized output uses classic
    /// cross-reference tables. Defaults to <see langword="false"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// document.Save("fast-web-view.pdf", PdfOptions.Default with { Linearize = true });
    /// </code>
    /// </example>
    public bool Linearize { get; init; }

    /// <summary>
    /// The PDF/A conformance level to create against —
    /// consumed by <see cref="Manuscript.Render(PdfOptions?)"/>/<see cref="PdfDocument.Compose"/>
    /// and honored again by <see cref="PdfDocument.Save"/>'s version knob. A
    /// non-<see cref="PdfAConformance.None"/> value makes the render pipeline write the XMP
    /// <c>pdfaid:part</c>/<c>pdfaid:conformance</c> identification (agreeing with the
    /// <c>/Info</c> dictionary), attach a <c>GTS_PDFA1</c> output intent (the bundled CC0 sRGB
    /// profile unless <see cref="PdfAOutputIntentProfile"/> overrides it), force the header
    /// version to the conformance's ceiling (<c>1.4</c> for <see cref="PdfAConformance.A1b"/>),
    /// and refuse (<c>PLUME8023</c>) any Standard-14 font in use — PDF/A requires every font
    /// embedded, and Standard-14 fonts embed nothing by design (supply
    /// <see cref="PdfFont.FromFile(string)"/>/<see cref="PdfFont.FromBytes(byte[])"/> instead).
    /// Under <see cref="Deterministic"/>, PDF/A additionally requires caller-supplied
    /// <see cref="Manuscript.CreateDate"/>/<see cref="Manuscript.ModifyDate"/>
    /// (<c>PLUME6058</c>). Defaults to <see cref="PdfAConformance.None"/> — an
    /// ordinary, non-archival document.
    /// </summary>
    /// <example>
    /// <code>
    /// var pdfA = PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b };
    /// using var document = manuscript.Render(pdfA);
    /// document.Save("archival.pdf");
    /// </code>
    /// </example>
    public PdfAConformance PdfAConformance { get; init; } = PdfAConformance.None;

    /// <summary>
    /// A caller-supplied ICC destination profile for the PDF/A output intent, overriding the
    /// bundled CC0-1.0 compact sRGB v2 profile (<c>Assets/sRGB-v2-micro.icc</c>, see
    /// <c>NOTICE</c>) that <see cref="PdfAConformance"/> attaches by default. Raw (undecoded)
    /// ICC profile bytes; PlumePDF copies the array at render time, so later caller mutation
    /// never reaches the written document. Set <see cref="PdfAOutputConditionIdentifier"/> to
    /// name the output condition the supplied profile actually represents. Ignored when
    /// <see cref="PdfAConformance"/> is <see cref="PdfAConformance.None"/>. Defaults to
    /// <see langword="null"/> — the bundled sRGB profile.
    /// </summary>
    /// <example>
    /// <code>
    /// var fogra = PdfOptions.Default with
    /// {
    ///     PdfAConformance = PdfAConformance.A2b,
    ///     PdfAOutputIntentProfile = File.ReadAllBytes("CoatedFOGRA39.icc"),
    ///     PdfAOutputConditionIdentifier = "FOGRA39",
    /// };
    /// </code>
    /// </example>
    public byte[]? PdfAOutputIntentProfile { get; init; }

    /// <summary>
    /// The <c>/OutputConditionIdentifier</c> written into the PDF/A output intent
    /// (ISO 32000-1 §14.11.5) — a registry name identifying the intended output condition.
    /// Defaults to <c>"sRGB IEC61966-2.1"</c>, matching the bundled sRGB profile; a caller
    /// supplying <see cref="PdfAOutputIntentProfile"/> should set this to the name of that
    /// profile's condition. Ignored when <see cref="PdfAConformance"/> is
    /// <see cref="PdfAConformance.None"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// var named = PdfOptions.Default with { PdfAOutputConditionIdentifier = "FOGRA39" };
    /// </code>
    /// </example>
    public string PdfAOutputConditionIdentifier { get; init; } = "sRGB IEC61966-2.1";

    /// <summary>
    /// The maximum nesting depth of a document's logical structure tree
    /// (<c>/StructTreeRoot</c>, ISO 32000-1 §14.7.2) PlumePDF's structure-tree reader/builder
    /// will descend before refusing to continue. Guards a hostile or pathological structure
    /// tree — an enormous chain of single-child structure elements — from driving tagged-PDF
    /// reading or authoring to stall or exhaust the call stack. Defaults to 64, matching
    /// <see cref="MaxObjectNestingDepth"/>'s general object-graph depth cap — real-world
    /// tagged documents rarely nest structure elements more than 10-15 levels deep even for
    /// dense nested tables.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxStructureTreeDepth = 16 };
    /// </code>
    /// </example>
    public int MaxStructureTreeDepth { get; init; } = 64;

    /// <summary>
    /// The maximum number of structure elements a single document's logical structure tree
    /// may contain — on read, before the reader refuses to continue enumerating it; on write,
    /// before the tagged-output builder refuses to continue authoring one. Guards a hostile
    /// source document (read side) or a runaway document-generating agent producing an
    /// enormous number of tagged Elements (write side) from driving structure-tree processing
    /// to stall or exhaust memory. Defaults to 100,000, matching
    /// <see cref="MaxLayoutElementCount"/>'s ceiling — a tagged structure tree is, at most,
    /// one structure element per rendered Element.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxStructureElementCount = 10_000 };
    /// </code>
    /// </example>
    public int MaxStructureElementCount { get; init; } = 100_000;

    /// <summary>
    /// The maximum size, in bytes, of a single serialized XMP metadata packet
    /// <see cref="PlumePdf.Documents.Metadata.XmpWriter"/> will produce before refusing to
    /// continue (<c>PdfDocument.SetXmpMetadata</c>). Guards a caller-supplied
    /// <see cref="PlumePdf.Documents.Metadata.XmpPacket"/> carrying pathologically large text
    /// fields (e.g. a multi-megabyte <c>Keywords</c> string) from producing an unbounded
    /// <c>/Metadata</c> stream. Defaults to 1 MiB — far beyond any real-world XMP packet,
    /// which typically runs a few hundred bytes to a few KB even with every Dublin Core field
    /// populated.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxXmpPacketWriteBytes = 64 * 1024 };
    /// </code>
    /// </example>
    public long MaxXmpPacketWriteBytes { get; init; } = 1024L * 1024;

    /// <summary>
    /// The maximum size, in bytes, of a document-supplied <c>/Metadata</c> XMP packet
    /// (<c>PdfDocument.GetXmpMetadataBytes</c>/<c>GetXmpMetadataText</c>) PlumePDF will read
    /// before refusing with a coded error (<c>PLUME6080</c>) — the read-side sibling of
    /// <see cref="MaxXmpPacketWriteBytes"/>. Enforced at the single read choke point every
    /// XMP consumer (including <c>Pdf.ValidatePdfA</c>'s scan of the packet text) goes
    /// through, so a hostile packet — e.g. a small Flate stream decompressing to hundreds of
    /// megabytes of XML — is refused before any text scan can burn CPU or memory on it.
    /// Defaults to 8 MiB — far beyond any real-world XMP packet, which typically runs a few
    /// hundred bytes to a few KB (Adobe products pad packets with a few KB of xpacket
    /// whitespace at most).
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxXmpPacketReadBytes = 256 * 1024 };
    /// </code>
    /// </example>
    public long MaxXmpPacketReadBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>
    /// The maximum number of GSUB/GPOS lookup applications a single <c>ILineShaper.Shape</c>
    /// call may perform before refusing to continue. Guards a hostile or
    /// pathological caller-supplied font — a crafted contextual/chaining lookup graph paired
    /// with adversarial input — from driving unbounded shaping work; every lookup application
    /// attempted (matched or not) counts against the budget. Defaults to 100,000, matching the
    /// house convention for a generous-but-finite ceiling (<see cref="MaxLayoutElementCount"/>,
    /// <see cref="MaxFormFields"/>, <see cref="MaxStructureElementCount"/>) — far beyond what
    /// any real-world document's text drives even through a script with heavy contextual
    /// substitution (Arabic, Devanagari).
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxShapingLookupApplications = 10_000 };
    /// </code>
    /// </example>
    public int MaxShapingLookupApplications { get; init; } = 100_000;

    // --- Phase 7 (Raster codecs / image-to-PDF) caps --------------------------------------
    // Every raster codec's own
    // per-hazard cap, same discipline as every prior phase's caps above — a decoded-pixel-count
    // ceiling, a TIFF IFD-chain frame-count ceiling, and JBIG2's segment/symbol-count ceilings
    // each guard a specific decompression-bomb-shaped hazard before the codec allocates the
    // buffer that hazard is aimed at.

    /// <summary>
    /// The maximum <c>width * height</c> pixel count a single raster frame (PNG, JPEG, TIFF,
    /// CCITT, JBIG2, or JPEG 2000 frame/region) may decode to before the codec refuses to
    /// continue. Guards a hostile or accidentally huge image — a small file whose header
    /// declares an enormous raster — from driving an unbounded pixel-buffer allocation before
    /// any pixel data is read. Defaults to 1 &lt;&lt; 27 (~134M pixels, roughly an A0 sheet at
    /// 600 DPI) — the same ceiling every Phase 7 codec enforced privately before this property
    /// existed to carry it.
    /// </summary>
    /// <remarks>
    /// For JPEG 2000, this is a <em>total-sample</em> bound summed across
    /// components rather than a single reference-grid check: each component's own (possibly
    /// sub-sampled) sample grid is charged against the cap, so a 3-component full-resolution
    /// image is admitted up to roughly 44.7M pixels of reference grid at the default cap (the
    /// 134M-sample budget divided three ways), and a sub-sampled encoding of the same nominal
    /// size — crediting the smaller chroma planes rather than penalizing them — is admitted
    /// further. The same cap also derives the decoder's per-decode working-memory budget
    /// (roughly 48 bytes per reference-grid pixel — one live tile's 32-bit coefficients across
    /// up to three multiple-component-transform components, plus the native-width output
    /// planes) and its ceiling on simultaneously-live output planes (8, covering every
    /// combination of colour, opacity, and palette expansion this decoder supports) — both
    /// derived from this single property rather than exposed as caps of their own.
    /// </remarks>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxImagePixels = 20_000_000 };
    /// var image = RasterImage.Decode(File.ReadAllBytes("untrusted.png"), limited);
    /// </code>
    /// </example>
    public long MaxImagePixels { get; init; } = 1L << 27;

    /// <summary>
    /// The maximum number of IFDs (frames) a single TIFF's Image File Directory chain will
    /// enumerate before <see cref="RasterImage.Decode(ReadOnlyMemory{byte},PdfOptions?)"/>
    /// refuses to continue reading it — independent of the reader's own cycle guard (which
    /// catches a chain that revisits an already-seen offset regardless of length). Guards a
    /// hostile or pathological IFD chain from stalling the frame-list walk. Defaults to 1,024
    /// — far beyond any real-world multi-page TIFF, which typically holds at most a few
    /// hundred pages/subfiles.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxImageFrames = 64 };
    /// </code>
    /// </example>
    public int MaxImageFrames { get; init; } = 1024;

    /// <summary>
    /// The maximum number of segments a single JBIG2 stream (embedded data plus an optional
    /// <c>/JBIG2Globals</c> stream, combined) is trusted for before the decoder refuses to
    /// continue. Guards a hostile or pathological segment header sequence — an enormous
    /// segment count that is cheap to declare but expensive to walk — from stalling JBIG2
    /// decoding. Defaults to 100,000 — far beyond any real-world scanned-document JBIG2
    /// stream, which typically carries at most a few dozen segments per page.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxJbig2Segments = 1_000 };
    /// </code>
    /// </example>
    public int MaxJbig2Segments { get; init; } = 100_000;

    /// <summary>
    /// The maximum total number of symbols a single JBIG2 stream's symbol dictionaries
    /// (including any inherited from <c>/JBIG2Globals</c>) may collectively define before the
    /// decoder refuses to continue. Guards a hostile symbol dictionary segment declaring an
    /// enormous export count from driving an unbounded in-memory symbol table. Defaults to
    /// 100,000 — far beyond any real-world scanned-document JBIG2 stream's symbol count, which
    /// typically runs in the hundreds to low thousands even for a dense full-page scan.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxJbig2Symbols = 10_000 };
    /// </code>
    /// </example>
    public int MaxJbig2Symbols { get; init; } = 100_000;

    // Phase 8 rasterizer caps: each guards a document/output-controlled allocation that
    // Rasterize's two-pass pipeline
    // would otherwise size directly from caller/document input before any content is painted.

    /// <summary>
    /// The maximum number of bytes a single <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c>
    /// call's target BGRA surface may occupy (<c>width * height * 4</c>) before
    /// <c>Raster.RasterSurface.Create</c> refuses to allocate it. Guards a caller-chosen
    /// (or DPI-derived) target size that would otherwise drive an unbounded allocation straight
    /// from <see cref="PdfRasterizeOptions"/>'s pixel dimensions/DPI, before a single pixel is
    /// painted. Defaults to 512 MiB (<c>(1L &lt;&lt; 27) * 4</c>) — comfortably above any
    /// reasonable print-resolution page render (a 4096x4096 BGRA surface is 64 MiB) while still
    /// refusing a pathological multi-hundred-megapixel request.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxRasterSurfaceBytes = 64L * 1024 * 1024 };
    /// using var document = PdfDocument.Open("input.pdf", limited);
    /// var image = document.Pages[0].Rasterize(); // throws PLUME7500 past the 64 MiB cap
    /// </code>
    /// </example>
    public long MaxRasterSurfaceBytes { get; init; } = (1L << 27) * 4;

    /// <summary>
    /// The maximum total number of display-list objects (paths, images, shadings, and nested
    /// Form XObject subtrees, cumulative across the whole page including every nested Form's
    /// own content) <c>RasterInterpreter.BuildDisplayList</c>'s pass 1 will build before
    /// refusing to continue. Guards a hostile or pathological content stream that paints an
    /// enormous number of tiny objects — cheap to emit, expensive to hold in memory and sweep
    /// in pass 2 — independent of <see cref="MaxContentStreamOperators"/>'s own operator-count
    /// ceiling (a single operator, e.g. one <c>Do</c> into a deeply fanned-out Form graph, can
    /// still contribute many display-list objects). Defaults to 1,000,000 — far beyond any
    /// real-world page's paint-operation count.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxDisplayListObjects = 50_000 };
    /// </code>
    /// </example>
    public int MaxDisplayListObjects { get; init; } = 1_000_000;

    /// <summary>
    /// The maximum number of precomputed color-ramp steps an axial (<c>/ShadingType 2</c>) or
    /// radial (<c>/ShadingType 3</c>) shading's <c>Raster.Shading.AxialShading</c>/<c>RadialShading.Parse</c>
    /// may allocate before refusing to continue. Guards a shading whose device-space extent (the
    /// caller-chosen sample resolution the paint path scales to) would otherwise drive an
    /// unbounded ramp-array allocation. Defaults to 4,096 — well above PDFium's own 256-step
    /// convention (<c>Raster.Shading.AxialShading</c>'s remarks), leaving headroom for a
    /// high-DPI render while still refusing a pathological request.
    /// </summary>
    /// <example>
    /// <code>
    /// var limited = PdfOptions.Default with { MaxShadingSamples = 256 };
    /// </code>
    /// </example>
    public int MaxShadingSamples { get; init; } = 4096;
}
