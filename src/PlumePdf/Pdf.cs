using System.Security.Cryptography.X509Certificates;
using System.Text;
using PlumePdf.Documents;
using PlumePdf.Documents.Compression;
using PlumePdf.Documents.PdfA;
using PlumePdf.Documents.Redaction;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Static task verbs built on top of the document model: an agent needs exactly
/// two names to start, <see cref="PdfDocument"/> and <see cref="Pdf"/>. Phase 1 ships
/// <see cref="Merge(PdfDocument[])"/> and <see cref="Split(PdfDocument)"/>; every imported
/// page is deep-copied into the result (the copy-on-materialize contract means the
/// copy owns fully independent byte buffers), so source documents may be disposed
/// immediately after a verb returns.
/// </summary>
/// <example>
/// <code>
/// using var merged = Pdf.Merge("a.pdf", "b.pdf");
/// merged.Save("combined.pdf");
///
/// using var combined = PdfDocument.Open("combined.pdf");
/// using var split = Pdf.Split(combined);
/// split.SaveAll("part-{n}.pdf");
/// </code>
/// </example>
public static class Pdf
{
    /// <summary>Opens and merges the documents at <paramref name="paths"/>, in order, into one new document. Each opened source is disposed before returning.</summary>
    /// <exception cref="ArgumentException"><paramref name="paths"/> is empty.</exception>
    public static PdfDocument Merge(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length == 0)
        {
            throw new ArgumentException("At least one path is required to merge.", nameof(paths));
        }

        var opened = new List<PdfDocument>(paths.Length);
        try
        {
            foreach (var path in paths)
            {
                opened.Add(PdfDocument.Open(path));
            }

            return Merge([.. opened]);
        }
        finally
        {
            foreach (var document in opened)
            {
                document.Dispose();
            }
        }
    }

    /// <summary>Merges the pages of every document in <paramref name="documents"/>, in order, into one new document.</summary>
    /// <remarks>
    /// A document (or page) passed more than once gives one page per occurrence: each repeated
    /// occurrence is copied on its own, and its form fields are independent copies renamed like any
    /// field-name collision between documents.
    /// <para>
    /// A new document holds only the pages it is given. Nothing that belongs to a page left behind
    /// comes along through a reference: a link, a pop-up or reply, a form widget, a radio group
    /// spanning pages. Links to a page left behind are removed, form fields whose widgets were all
    /// on such pages are left out, and a kept field keeps only its imported widgets — the same
    /// rules <see cref="PdfDocument.Save"/> applies after <see cref="PageCollection.RemoveAt"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="documents"/> is empty.</exception>
    public static PdfDocument Merge(params PdfDocument[] documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Length == 0)
        {
            throw new ArgumentException("At least one document is required to merge.", nameof(documents));
        }

        var pages = documents.SelectMany(static document => document.Pages.Select(page => (document, page.Reference, page.Dictionary)));
        return PageImporter.Compose(pages);
    }

    /// <summary>Splits <paramref name="document"/> into one new single-page document per page, in order.</summary>
    /// <remarks>
    /// A new document holds only the pages it is given. Nothing that belongs to a page left behind
    /// comes along through a reference: a link, a pop-up or reply, a form widget, a radio group
    /// spanning pages. Links to a page left behind are removed, form fields whose widgets were all
    /// on such pages are left out, and a kept field keeps only its imported widgets — the same
    /// rules <see cref="PdfDocument.Save"/> applies after <see cref="PageCollection.RemoveAt"/>.
    /// </remarks>
    public static SplitResult Split(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var results = new List<PdfDocument>(document.Pages.Count);
        foreach (var page in document.Pages)
        {
            results.Add(PageImporter.Compose([(document, page.Reference, page.Dictionary)]));
        }

        return new SplitResult(results);
    }

    /// <summary>Asynchronous equivalent of <see cref="Merge(string[])"/>; the merge is offloaded to the thread pool.</summary>
    public static Task<PdfDocument> MergeAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default) =>
        Task.Run(() => Merge([.. paths]), cancellationToken);

    /// <summary>Asynchronous equivalent of <see cref="Split(PdfDocument)"/>; the split is offloaded to the thread pool.</summary>
    public static Task<SplitResult> SplitAsync(PdfDocument document, CancellationToken cancellationToken = default) =>
        Task.Run(() => Split(document), cancellationToken);

    /// <summary>
    /// Opens the document at <paramref name="path"/> and extracts every page's text, joined
    /// with newlines, as one flattened convenience string (the two-door shape:
    /// this verb is the "quick door"; <c>PdfPage.ExtractText</c> is the "rich
    /// door" for positions and per-page results). Pages are extracted and appended one at a
    /// time — no more than one page's positioned-letter graph is held in memory at once,
    /// though the final joined string is of course fully materialized.
    /// </summary>
    /// <param name="path">The path to the PDF file.</param>
    /// <exception cref="PlumePdfException">The file could not be opened or recovered as a PDF document.</exception>
    /// <example>
    /// <code>
    /// string text = Pdf.ExtractText("input.pdf");
    /// Console.WriteLine(text);
    /// </code>
    /// </example>
    public static string ExtractText(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var document = PdfDocument.Open(path);
        var builder = new StringBuilder();
        for (var i = 0; i < document.Pages.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }

            builder.Append(document.Pages[i].ExtractText().Text);
        }

        return builder.ToString();
    }

    /// <summary>Asynchronous equivalent of <see cref="ExtractText(string)"/>; the extraction is offloaded to the thread pool.</summary>
    public static Task<string> ExtractTextAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Task.Run(() => ExtractText(path), cancellationToken);
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/> and renders one or more pages to pixels
    /// (Phase 8's rasterizer) — the "quick door" whole-document verb
    /// (the two-door shape, mirroring <see cref="ExtractText(string)"/>'s relationship to
    /// <c>PdfPage.ExtractText</c>); <c>doc.Pages[i].Rasterize</c> is the "rich door" for a
    /// single already-open page. Which pages render is <paramref name="options"/>'s own
    /// <see cref="PdfRasterizeOptions.PageIndices"/> — <see langword="null"/> (the default)
    /// renders only the first page (index 0); an explicit list renders exactly those pages, in
    /// the order given, as the result's <see cref="RasterImage.Frames"/> (mirroring multi-page
    /// TIFF decode, which <see cref="RasterImage"/> already models). See <c>doc.Pages[i].Rasterize</c>'s
    /// remarks for what paints.
    /// </summary>
    /// <param name="path">The path to the PDF file.</param>
    /// <param name="options">Target size/DPI, page selection, and background. Defaults to <see cref="PdfRasterizeOptions.Default"/> (96 DPI, first page only, opaque white background).</param>
    /// <exception cref="ArgumentException"><paramref name="options"/>'s target-size fields are malformed — see <see cref="PdfRasterizeOptions.Validate"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A <see cref="PdfRasterizeOptions.PageIndices"/> entry names a page index outside the document's actual page count.</exception>
    /// <exception cref="PlumePdfException">The file could not be opened or recovered as a PDF document; or any exception <c>doc.Pages[i].Rasterize</c> can throw for one of the requested pages.</exception>
    /// <example>
    /// <code>
    /// var image = Pdf.Rasterize("input.pdf", PdfRasterizeOptions.Default with { Dpi = 150 });
    /// File.WriteAllBytes("page-0.png", image.Frames[0].EncodePng());
    /// </code>
    /// </example>
    public static RasterImage Rasterize(string path, PdfRasterizeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var rasterizeOptions = options ?? PdfRasterizeOptions.Default;
        rasterizeOptions.Validate();

        using var document = PdfDocument.Open(path);
        var pageIndices = rasterizeOptions.PageIndices ?? [0];
        var diagnostics = new DiagnosticCollection();
        var frames = new List<RasterImageFrame>(pageIndices.Count);

        foreach (var pageIndex in pageIndices)
        {
            if (pageIndex < 0 || pageIndex >= document.Pages.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(options), pageIndex, $"PdfRasterizeOptions.PageIndices names page index {pageIndex}, but '{path}' has only {document.Pages.Count} page(s).");
            }

            frames.Add(PageRasterAdapter.RasterizePageFrame(document.Pages[pageIndex], document.Objects, document.Options, rasterizeOptions, diagnostics, document));
        }

        return RasterImage.FromFrames(frames, diagnostics);
    }

    /// <summary>Asynchronous equivalent of <see cref="Rasterize(string,PdfRasterizeOptions?)"/>; rendering is offloaded to the thread pool.</summary>
    public static Task<RasterImage> RasterizeAsync(string path, PdfRasterizeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Task.Run(() => Rasterize(path, options), cancellationToken);
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/>, fills <paramref name="values"/> into
    /// its form fields (exact match, else a unique trailing-segment match), optionally
    /// <see cref="PdfForm.Flatten"/>s it, and saves back to <paramref name="path"/> — via
    /// <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/> when not flattening (the
    /// signature-preserving path: prior revisions, including any existing digital signature,
    /// stay intact).
    /// <see cref="Pdf.FillForm(string,ValueTuple{string,string}[])"/> is the params-array
    /// convenience for a handful of literal name/value pairs.
    /// </summary>
    /// <param name="path">The path to open, fill, and save back to.</param>
    /// <param name="values">Field name → value pairs. Names use <see cref="FormFieldCollection"/>'s lookup (exact match, else a unique trailing-segment match).</param>
    /// <param name="flatten">When <see langword="true"/>, flattens the form after filling (<see cref="PdfForm.Flatten"/>) before saving — the result has no <c>/AcroForm</c> left at all.</param>
    /// <param name="options">Options controlling the open and save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException">
    /// A name doesn't resolve (<c>PLUME6030</c>/<c>PLUME6031</c>), a value is invalid for its
    /// field (<c>PLUME6032</c>-<c>PLUME6034</c>), or (without <paramref name="flatten"/>) the
    /// source's cross-reference data was recovered via a brute-force scan and has no reliable
    /// prior <c>startxref</c> offset for <c>SaveIncremental</c> to chain onto
    /// (<c>PLUME5005</c>) — fill via <see cref="PdfForm.For"/>/<see cref="PdfForm.Fill"/> and
    /// call <see cref="PdfDocument.Save"/> directly instead for such a source.
    /// </exception>
    /// <example>
    /// <code>
    /// Pdf.FillForm("f1040.pdf", new Dictionary&lt;string, string&gt; { ["c1_01"] = "1", ["f1_02"] = "Jane Q. Public" });
    /// </code>
    /// </example>
    public static void FillForm(string path, IEnumerable<KeyValuePair<string, string>> values, bool flatten = false, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(values);

        using var document = PdfDocument.Open(path, options);
        var form = PdfForm.For(document);
        form.Fill(values);

        if (flatten)
        {
            using var flattened = form.Flatten();

            // Flatten() already deep-copied everything the new document needs
            // (PdfStream.RawBytes are managed copies, never a view over mapped memory), so the
            // source document's own memory-mapped handles are safe to release before writing
            // the flattened result back over the same path — required on Windows, which
            // refuses to replace a file that still has a live mapped section.
            document.Dispose();
            flattened.Save(path, options);
        }
        else
        {
            // The general dirty-object-set/allocator is what makes this
            // safe: FieldValues.SetValue/FormFiller.Fill mark every mutated field/widget/
            // AcroForm dictionary dirty on document.Objects, so SaveIncremental — the
            // signature-preserving path — actually picks the fill up instead of silently
            // appending nothing.
            document.SaveIncremental(path, options);
        }
    }

    /// <summary>The params-array convenience for <see cref="FillForm(string,IEnumerable{KeyValuePair{string,string}},bool,PdfOptions?)"/> — a handful of literal name/value pairs, never flattening.</summary>
    /// <example>
    /// <code>
    /// Pdf.FillForm("f1040.pdf", ("c1_01", "1"), ("f1_02", "Jane Q. Public"));
    /// </code>
    /// </example>
    public static void FillForm(string path, params (string Name, string Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        FillForm(path, values.Select(static v => new KeyValuePair<string, string>(v.Name, v.Value)));
    }

    /// <summary>Asynchronous equivalent of <see cref="FillForm(string,IEnumerable{KeyValuePair{string,string}},bool,PdfOptions?)"/>; the fill and save are offloaded to the thread pool.</summary>
    public static Task FillFormAsync(string path, IEnumerable<KeyValuePair<string, string>> values, bool flatten = false, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => FillForm(path, values, flatten, options), cancellationToken);

    /// <summary>
    /// Opens the document at <paramref name="path"/>, flattens its form
    /// (<see cref="PdfForm.Flatten"/>) without changing any field's value, and saves to
    /// <paramref name="outputPath"/> (or back to <paramref name="path"/> when omitted).
    /// </summary>
    /// <param name="path">The path to open.</param>
    /// <param name="outputPath">The path to save the flattened result to. Defaults to <paramref name="path"/>.</param>
    /// <param name="options">Options controlling the open and save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <remarks>A widget with no <c>/AP</c> gets its appearance synthesized from its field value ("generated or pre-existing"); one with nothing synthesizable either is left live with a <c>PLUME6036</c> Warning diagnostic — never a whole-document failure. See <see cref="PdfForm.Flatten"/>.</remarks>
    /// <example>
    /// <code>
    /// Pdf.FlattenForm("f1040-filled.pdf", "f1040-flattened.pdf");
    /// </code>
    /// </example>
    public static void FlattenForm(string path, string? outputPath = null, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var document = PdfDocument.Open(path, options);
        using var flattened = PdfForm.For(document).Flatten();

        // See FillForm's flatten branch above — safe to release document's handles
        // before writing, since Flatten() already deep-copied everything flattened needs.
        document.Dispose();
        flattened.Save(outputPath ?? path, options);
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/>, signs it (<see cref="SignatureCollection.Add"/>),
    /// and writes the result to <paramref name="outputPath"/> — the synchronous door, valid
    /// only for a request that never touches the network (<see cref="PdfSignatureLevel.B"/>, no
    /// timestamp authority). Use <see cref="SignAsync"/> for
    /// <see cref="PdfSignatureLevel.T"/> or later.
    /// </summary>
    /// <param name="path">The path to open and sign.</param>
    /// <param name="outputPath">Where to write the signed document. May equal <paramref name="path"/>.</param>
    /// <param name="signOptions">Who signs, and the signature dictionary's descriptive fields.</param>
    /// <param name="options">Options controlling the open and write. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException">See <see cref="SignatureCollection.Add"/>; also <paramref name="path"/> can't be opened (<c>PLUME1001</c>/<c>PLUME1002</c>).</exception>
    /// <example>
    /// <code>
    /// Pdf.Sign("input.pdf", "signed.pdf", new PdfSignOptions { Certificate = myCertificate, Reason = "Approved" });
    /// </code>
    /// </example>
    public static void Sign(string path, string outputPath, PdfSignOptions signOptions, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        using var document = PdfDocument.Open(path, options);
        document.Signatures.Add(outputPath, signOptions, options);
    }

    /// <summary>
    /// Asynchronous equivalent of <see cref="Sign"/>, supporting every
    /// <see cref="PdfSignatureLevel"/> including one requiring a network round trip (an RFC
    /// 3161 timestamp fetch) — real awaits down to <c>HttpClient</c>, never <c>Task.Run</c>
    /// (this phase's first true-async member).
    /// </summary>
    /// <example>
    /// <code>
    /// await Pdf.SignAsync("input.pdf", "signed.pdf", new PdfSignOptions
    /// {
    ///     Certificate = myCertificate,
    ///     Level = PdfSignatureLevel.T,
    ///     TimestampAuthorityUrl = new Uri("https://freetsa.org/tsr"),
    /// });
    /// </code>
    /// </example>
    public static async Task SignAsync(string path, string outputPath, PdfSignOptions signOptions, PdfOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        using var document = await PdfDocument.OpenAsync(path, options, cancellationToken).ConfigureAwait(false);
        await document.Signatures.SignAsync(outputPath, signOptions, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/> and verifies every signature it carries
    /// (<see cref="PdfSignature.Verify"/>), in field-tree (document) order.
    /// </summary>
    /// <param name="path">The path to open and verify.</param>
    /// <param name="trustedRoots">
    /// Trust anchors for certificate-chain building, forwarded to every signature's own
    /// <see cref="PdfSignature.Verify"/> call. Omit to skip chain evaluation entirely
    /// (<see cref="SignatureChainStatus.NotEvaluated"/> on every result) — PlumePDF never
    /// silently falls back to the OS trust store.
    /// </param>
    /// <returns>One result per signature; empty when the document carries none.</returns>
    /// <example>
    /// <code>
    /// foreach (var result in Pdf.Verify("signed.pdf"))
    /// {
    ///     Console.WriteLine(result.IsValid ? "valid" : $"invalid: {result.CryptographicStatus}");
    /// }
    /// </code>
    /// </example>
    public static IReadOnlyList<SignatureVerificationResult> Verify(string path, IReadOnlyList<X509Certificate2>? trustedRoots = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var document = PdfDocument.Open(path);
        return document.Signatures.Select(signature => signature.Verify(trustedRoots)).ToList();
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/>, redacts it against
    /// <paramref name="targets"/> (<see cref="PdfDocument.Redact"/> — true removal at the
    /// object and content-stream level), and writes the result to
    /// <paramref name="outputPath"/> via <see cref="PdfDocument.Save"/> — always the
    /// full-rewrite, garbage-collecting path, never <c>SaveIncremental</c>, because an
    /// incremental update preserves the original revision's bytes and would leave the
    /// "redacted" content fully recoverable. Always inspect the returned
    /// <see cref="RedactionResult"/>: a target that matched nothing is loud in the result
    /// (<see cref="RedactionResult.HadNoMatches"/>), never an exception — the output file is
    /// still written, so a caller that skips the check can silently ship an unredacted
    /// document.
    /// </summary>
    /// <param name="path">The path to open and redact.</param>
    /// <param name="outputPath">Where to write the redacted document. May equal <paramref name="path"/>.</param>
    /// <param name="targets">What to remove: <see cref="RedactionTarget.Text"/>, <see cref="RedactionTarget.Pattern"/>, and/or <see cref="RedactionTarget.Region"/> targets.</param>
    /// <param name="redactOptions">Options controlling the redaction call. Defaults to <see cref="PdfRedactOptions.Default"/>.</param>
    /// <param name="options">Options controlling the open and save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <returns>The rich per-call report — check <see cref="RedactionResult.MatchCount"/> before trusting the output.</returns>
    /// <exception cref="PlumePdfException">See <see cref="PdfDocument.Redact"/> — encrypted source (<c>PLUME6061</c>), signed source without <see cref="PdfRedactOptions.AllowInvalidatingSignatures"/> (<c>PLUME6062</c>), or the match cap exceeded (<c>PLUME6060</c>).</exception>
    /// <example>
    /// <code>
    /// var result = Pdf.Redact("contract.pdf", "contract-redacted.pdf", [RedactionTarget.Text("Jane Doe")]);
    /// if (result.HadNoMatches)
    /// {
    ///     throw new InvalidOperationException("Nothing matched — nothing was redacted.");
    /// }
    /// </code>
    /// </example>
    public static RedactionResult Redact(string path, string outputPath, IReadOnlyList<RedactionTarget> targets, PdfRedactOptions? redactOptions = null, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(targets);

        using var document = PdfDocument.Open(path, options);
        var result = document.Redact(targets, redactOptions);
        document.Save(outputPath, options);
        return result;
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/>, makes it smaller with the levers
    /// <paramref name="compressOptions"/> turns on, and writes the result to
    /// <paramref name="outputPath"/> — a full rewrite through <see cref="PdfDocument.Save"/>,
    /// written to a temporary file and moved into place, so a failure leaves no partial output.
    /// With no options the call is lossless: unfiltered streams are Flate-compressed, objects
    /// are packed into object streams, and byte-identical streams are written once. Images are
    /// downsampled (<see cref="PdfImageCompression.MaxDpi"/>) or re-encoded as JPEG
    /// (<see cref="PdfImageCompression.JpegQuality"/>) only when asked, and every rewritten
    /// image follows the never-larger rule. The returned <see cref="CompressionResult"/> says
    /// what happened to every image, so the levers can be tuned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Memory is bounded by the largest image rather than the file: images are decoded,
    /// resampled and encoded one at a time while writing, with a working set of roughly five
    /// times the largest decoded image. Disk needs room for the output-sized temporary file
    /// beside <paramref name="outputPath"/>.
    /// </para>
    /// <para>
    /// <paramref name="outputPath"/> may equal <paramref name="path"/>, but then the
    /// memory-mapped source is buffered into memory before writing, and a source over 2 GiB is
    /// refused before any work (<c>PLUME1006</c>); write to a different path for large inputs.
    /// </para>
    /// </remarks>
    /// <param name="path">The path to open and compress.</param>
    /// <param name="outputPath">Where to write the compressed document. May equal <paramref name="path"/> (see remarks).</param>
    /// <param name="compressOptions">The levers to apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and save, such as <see cref="PdfOptions.Deterministic"/>, <see cref="PdfOptions.Strict"/> and the image resource caps. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <returns>Input and output sizes, one decision per image, and the call's result-scoped diagnostics.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> or <paramref name="outputPath"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PdfImageCompression.MaxDpi"/> is not a positive finite number, or <see cref="PdfImageCompression.JpegQuality"/> is outside 1–100.</exception>
    /// <exception cref="PlumePdfException">
    /// Refused before anything is planned or written: <paramref name="options"/> asks for
    /// <see cref="PdfOptions.Linearize"/> (<c>PLUME5018</c>); the source is encrypted
    /// (<c>PLUME5001</c>); the source is signed and
    /// <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/> is not set
    /// (<c>PLUME6084</c>); or <paramref name="outputPath"/> is <paramref name="path"/> and the
    /// memory-mapped source is over 2 GiB (<c>PLUME1006</c>). An image that can't be decoded is never a
    /// refusal, even under <see cref="PdfOptions.Strict"/>: its samples are written unchanged
    /// and it is reported (<c>PLUME6086</c>). Under <see cref="PdfOptions.Strict"/>, a
    /// reference the document can't resolve (an image's <c>/SMask</c> naming a missing object,
    /// say) refuses the save with <c>PLUME2060</c>, as <see cref="PdfDocument.Save(string, PdfOptions?)"/>
    /// does; that refusal comes after planning, and no output file is written. The source path
    /// can't be opened (<c>PLUME1001</c>/<c>PLUME1002</c>), or the output file can't be written
    /// — a missing directory, a path that is a directory, no write permission, a full disk
    /// (<c>PLUME1007</c>, the operating system's exception as the inner exception; no
    /// temporary file is left behind and an existing file at <paramref name="outputPath"/> is untouched).
    /// </exception>
    /// <example>
    /// <code>
    /// var result = Pdf.Compress("scan.pdf", "scan-small.pdf", new PdfCompressOptions
    /// {
    ///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
    /// });
    /// Console.WriteLine($"{result.InputBytes:N0} -> {result.OutputBytes:N0} bytes");
    /// foreach (var group in result.Images.GroupBy(static i => (i.Action, i.SkipReason)))
    /// {
    ///     Console.WriteLine($"{group.Key.Action} {group.Key.SkipReason}: {group.Count()}");
    /// }
    /// </code>
    /// </example>
    public static CompressionResult Compress(string path, string outputPath, PdfCompressOptions? compressOptions = null, PdfOptions? options = null) =>
        CompressionEngine.Compress(path, outputPath, compressOptions, options, CancellationToken.None);

    /// <summary>
    /// Asynchronous equivalent of <see cref="Compress(string, string, PdfCompressOptions?, PdfOptions?)"/>, run on the thread pool, with
    /// cooperative cancellation: <paramref name="cancellationToken"/> is checked between pages
    /// while planning and between streams while writing. A cancelled call throws
    /// <see cref="OperationCanceledException"/> and leaves no output file (an existing file at
    /// <paramref name="outputPath"/> is left as it was).
    /// </summary>
    /// <param name="path">The path to open and compress.</param>
    /// <param name="outputPath">Where to write the compressed document. May equal <paramref name="path"/> (see <see cref="Compress(string, string, PdfCompressOptions?, PdfOptions?)"/>).</param>
    /// <param name="compressOptions">The levers to apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops the call between pages or streams.</param>
    /// <returns>The same report <see cref="Compress(string, string, PdfCompressOptions?, PdfOptions?)"/> returns.</returns>
    /// <exception cref="PlumePdfException">See <see cref="Compress(string, string, PdfCompressOptions?, PdfOptions?)"/>.</exception>
    /// <example>
    /// <code>
    /// using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    /// var result = await Pdf.CompressAsync("scan.pdf", "scan-small.pdf", new PdfCompressOptions
    /// {
    ///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
    /// }, cancellationToken: timeout.Token);
    /// </code>
    /// </example>
    public static Task<CompressionResult> CompressAsync(string path, string outputPath, PdfCompressOptions? compressOptions = null, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => CompressionEngine.Compress(path, outputPath, compressOptions, options, cancellationToken), cancellationToken);

    /// <summary>
    /// Reads a document from <paramref name="input"/>, makes it smaller with the levers
    /// <paramref name="compressOptions"/> turns on, and writes the result to
    /// <paramref name="output"/> — the stream form of
    /// <see cref="Compress(string, string, PdfCompressOptions?, PdfOptions?)"/>, for a document
    /// that arrives as a download and leaves as an upload. The levers, the
    /// <see cref="CompressionResult"/>, the diagnostics and the refusals are the same, and so are
    /// the bytes: for the same input and options, the output is byte-identical to the path
    /// overload's under <see cref="PdfOptions.Deterministic"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seekable <paramref name="input"/> is read in place, from position 0, through a small
    /// window, never buffered whole. A non-seekable one (an HTTP response body, a blob download)
    /// is first copied to a temporary file in <see cref="Path.GetTempPath"/>, never into memory,
    /// and that file is deleted when the call returns or throws; the temporary directory needs
    /// room for a copy of the input. <see cref="CompressionResult.InputBytes"/> is the stream's
    /// length, or the number of bytes copied.
    /// </para>
    /// <para>
    /// <paramref name="output"/> need not be seekable. Writing starts at its current position,
    /// and the PDF's offsets are counted from there, so a stream already positioned past 0 holds
    /// a valid PDF from that point on. <paramref name="output"/> is flushed at the end, and
    /// <see cref="CompressionResult.OutputBytes"/> is the number of bytes written. Neither stream
    /// is disposed or closed: the caller owns both.
    /// </para>
    /// <para>
    /// A caller's stream can't be written atomically. Every refusal and argument error comes
    /// before the first byte, so nothing is written to <paramref name="output"/> then; a failure
    /// or cancellation during the write can leave a partial, unusable PDF in
    /// <paramref name="output"/>, which the caller discards (aborts the upload, deletes the
    /// blob). Memory is bounded by the largest image, as for the path overload.
    /// </para>
    /// </remarks>
    /// <param name="input">A readable stream holding the document. Seekable or not.</param>
    /// <param name="output">A writable stream to write the compressed document to. Seekable or not; must not be <paramref name="input"/>.</param>
    /// <param name="compressOptions">The levers to apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and save, such as <see cref="PdfOptions.Deterministic"/>, <see cref="PdfOptions.Strict"/> and the image resource caps. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <returns>Input and output sizes, one decision per image, and the call's result-scoped diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> can't be read, <paramref name="output"/> can't be written (a read-only or disposed stream), or both are the same stream.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PdfImageCompression.MaxDpi"/> is not a positive finite number, or <see cref="PdfImageCompression.JpegQuality"/> is outside 1–100.</exception>
    /// <exception cref="PlumePdfException">
    /// Refused before anything is written: <paramref name="options"/> asks for
    /// <see cref="PdfOptions.Linearize"/> (<c>PLUME5018</c>, before <paramref name="input"/> is
    /// read); the source is encrypted (<c>PLUME5001</c>); or the source is signed and
    /// <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/> is not set
    /// (<c>PLUME6084</c>). The input couldn't be read as a PDF (as
    /// <see cref="PdfDocument.Open(Stream, PdfOptions?)"/> refuses). Under
    /// <see cref="PdfOptions.Strict"/>, a reference the document can't resolve refuses the save
    /// with <c>PLUME2060</c> after planning, before writing. A non-seekable
    /// <paramref name="input"/> can't be spooled to its temporary copy in the temp directory
    /// (<c>PLUME1008</c>, before anything is written). An exception thrown by
    /// <paramref name="input"/> or <paramref name="output"/> themselves propagates unchanged.
    /// </exception>
    /// <example>
    /// <code>
    /// using var download = await http.GetStreamAsync(sourceUrl);
    /// await using var upload = File.Create("scan-small.pdf");
    /// var result = Pdf.Compress(download, upload, new PdfCompressOptions
    /// {
    ///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
    /// });
    /// Console.WriteLine($"{result.InputBytes:N0} -> {result.OutputBytes:N0} bytes");
    /// </code>
    /// </example>
    public static CompressionResult Compress(Stream input, Stream output, PdfCompressOptions? compressOptions = null, PdfOptions? options = null) =>
        CompressionEngine.Compress(input, output, compressOptions, options, CancellationToken.None);

    /// <summary>
    /// Asynchronous equivalent of <see cref="Compress(Stream, Stream, PdfCompressOptions?, PdfOptions?)"/>:
    /// a non-seekable <paramref name="input"/> is copied to its temporary file asynchronously,
    /// then the compression runs on the thread pool, with cooperative cancellation —
    /// <paramref name="cancellationToken"/> is checked while copying, between pages while
    /// planning and between streams while writing. A cancelled call throws
    /// <see cref="OperationCanceledException"/>; if it was already writing,
    /// <paramref name="output"/> may hold a partial PDF to discard.
    /// </summary>
    /// <param name="input">A readable stream holding the document. Seekable or not.</param>
    /// <param name="output">A writable stream to write the compressed document to. Seekable or not; must not be <paramref name="input"/>.</param>
    /// <param name="compressOptions">The levers to apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops the call while copying the input, or between pages or streams.</param>
    /// <returns>The same report <see cref="Compress(Stream, Stream, PdfCompressOptions?, PdfOptions?)"/> returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.</exception>
    /// <exception cref="ArgumentException">See <see cref="Compress(Stream, Stream, PdfCompressOptions?, PdfOptions?)"/>.</exception>
    /// <exception cref="PlumePdfException">See <see cref="Compress(Stream, Stream, PdfCompressOptions?, PdfOptions?)"/>.</exception>
    /// <example>
    /// <code>
    /// using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    /// await using var download = await http.GetStreamAsync(sourceUrl, timeout.Token);
    /// await using var upload = File.Create("scan-small.pdf");
    /// var result = await Pdf.CompressAsync(download, upload, new PdfCompressOptions
    /// {
    ///     Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
    /// }, cancellationToken: timeout.Token);
    /// </code>
    /// </example>
    public static Task<CompressionResult> CompressAsync(Stream input, Stream output, PdfCompressOptions? compressOptions = null, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        CompressionEngine.CompressAsync(input, output, compressOptions, options, cancellationToken);

    /// <summary>
    /// A dry run of <see cref="Compress(string, string, PdfCompressOptions?, PdfOptions?)"/>:
    /// opens the document at <paramref name="path"/>, plans exactly what Compress would do with
    /// the same options, trial-encodes a small, capped sample of the images it would rewrite,
    /// and estimates the output size — without writing anything. On a large scan, planning takes
    /// seconds while writing takes minutes, so an estimate is a cheap way to choose levers, or to
    /// decide whether compressing is worth it, before paying for the write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The refusals and argument checks are Compress's, in the same order and with the same
    /// codes, except those about the output (there is none): an encrypted source refuses
    /// (<c>PLUME5001</c>), and so do a signed source without
    /// <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/> (<c>PLUME6084</c>) and
    /// <see cref="PdfOptions.Linearize"/> (<c>PLUME5018</c>), since the Compress call being
    /// estimated would refuse. With <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/>
    /// the signatures are stripped only from the estimate's own copy of the document, never
    /// from the file. The writer's own late refusal under <see cref="PdfOptions.Strict"/>
    /// (<c>PLUME2060</c>, a reference the document can't resolve) isn't predicted: the estimate
    /// never runs the writer.
    /// </para>
    /// <para>
    /// At most sixteen images are trial-encoded, one at a time, through the same code Compress
    /// writes them with, so memory is bounded by the largest sampled image as Compress's is by
    /// the largest image. <see cref="PdfCompressOptions.Progress"/> reports
    /// <see cref="CompressionStage.Planning"/> then <see cref="CompressionStage.Sampling"/>.
    /// How the sample is chosen and the rest extrapolated, and the exact definition of
    /// <see cref="CompressionEstimate.EstimatedOutputBytes"/>, are in
    /// <see cref="CompressionEstimate"/>'s remarks; the estimate is conservative (it tends to be
    /// at or above what Compress writes). For an exact size, run Compress to a stream that only
    /// counts what is written to it.
    /// </para>
    /// </remarks>
    /// <param name="path">The path to open and estimate.</param>
    /// <param name="compressOptions">The levers Compress would apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and the planned save, as for Compress. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <returns>The input size, the estimated output size, one planned decision per image, and the call's result-scoped diagnostics.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PdfImageCompression.MaxDpi"/> is not a positive finite number, or <see cref="PdfImageCompression.JpegQuality"/> is outside 1–100.</exception>
    /// <exception cref="PlumePdfException">
    /// <paramref name="options"/> asks for <see cref="PdfOptions.Linearize"/> (<c>PLUME5018</c>);
    /// the source is encrypted (<c>PLUME5001</c>); the source is signed and
    /// <see cref="PdfCompressOptions.AllowInvalidatingSignatures"/> is not set (<c>PLUME6084</c>);
    /// or the source path can't be opened (<c>PLUME1001</c>/<c>PLUME1002</c>). An image that
    /// can't be decoded is never a refusal: a sampled one is reported (<c>PLUME6086</c>).
    /// </exception>
    /// <example>
    /// <code>
    /// var levers = new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 150, JpegQuality = 70 } };
    /// var estimate = Pdf.EstimateCompression("scan.pdf", levers);
    /// Console.WriteLine($"{estimate.InputBytes:N0} -> about {estimate.EstimatedOutputBytes:N0} bytes");
    /// if (estimate.EstimatedOutputBytes &lt; 500_000_000)
    /// {
    ///     Pdf.Compress("scan.pdf", "scan-small.pdf", levers);
    /// }
    /// </code>
    /// </example>
    public static CompressionEstimate EstimateCompression(string path, PdfCompressOptions? compressOptions = null, PdfOptions? options = null) =>
        CompressionEngine.Estimate(path, compressOptions, options, CancellationToken.None);

    /// <summary>
    /// Asynchronous equivalent of <see cref="EstimateCompression(string, PdfCompressOptions?, PdfOptions?)"/>,
    /// run on the thread pool, with cooperative cancellation: <paramref name="cancellationToken"/>
    /// is checked between pages while planning and between sampled images. A cancelled call
    /// throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="path">The path to open and estimate.</param>
    /// <param name="compressOptions">The levers Compress would apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and the planned save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops the call between pages or sampled images.</param>
    /// <returns>The same estimate <see cref="EstimateCompression(string, PdfCompressOptions?, PdfOptions?)"/> returns.</returns>
    /// <exception cref="PlumePdfException">See <see cref="EstimateCompression(string, PdfCompressOptions?, PdfOptions?)"/>.</exception>
    /// <example>
    /// <code>
    /// var estimate = await Pdf.EstimateCompressionAsync("scan.pdf", new PdfCompressOptions
    /// {
    ///     Images = new PdfImageCompression { MaxDpi = 150, JpegQuality = 70 },
    /// }, cancellationToken: timeout.Token);
    /// </code>
    /// </example>
    public static Task<CompressionEstimate> EstimateCompressionAsync(string path, PdfCompressOptions? compressOptions = null, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => CompressionEngine.Estimate(path, compressOptions, options, cancellationToken), cancellationToken);

    /// <summary>
    /// A dry run of <see cref="Compress(Stream, Stream, PdfCompressOptions?, PdfOptions?)"/>
    /// for a document that arrives as a stream: the same estimate
    /// <see cref="EstimateCompression(string, PdfCompressOptions?, PdfOptions?)"/> makes, with
    /// nothing written.
    /// </summary>
    /// <remarks>
    /// A seekable <paramref name="input"/> is read in place from position 0 and left as it was
    /// (its position may move). A non-seekable one is first copied to a temporary file in
    /// <see cref="Path.GetTempPath"/>, as Compress does, and that file is deleted when the call
    /// returns or throws; <see cref="CompressionEstimate.InputBytes"/> is then the number of
    /// bytes copied, and <see cref="PdfCompressOptions.Progress"/> reports
    /// <see cref="CompressionStage.Spooling"/> first. Because the input can then be read only
    /// once, estimating and then compressing a non-seekable stream means reading it twice:
    /// copy it to a file and pass paths instead. <paramref name="input"/> is not disposed.
    /// </remarks>
    /// <param name="input">A readable stream holding the document. Seekable or not.</param>
    /// <param name="compressOptions">The levers Compress would apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and the planned save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <returns>The input size, the estimated output size, one planned decision per image, and the call's result-scoped diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> can't be read (a write-only or disposed stream).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="PdfImageCompression.MaxDpi"/> is not a positive finite number, or <see cref="PdfImageCompression.JpegQuality"/> is outside 1–100.</exception>
    /// <exception cref="PlumePdfException">
    /// As for <see cref="EstimateCompression(string, PdfCompressOptions?, PdfOptions?)"/>
    /// (<c>PLUME5018</c> before <paramref name="input"/> is read); the input couldn't be read as
    /// a PDF; or a non-seekable <paramref name="input"/> can't be spooled to the temp directory
    /// (<c>PLUME1008</c>). An exception thrown by <paramref name="input"/> itself propagates unchanged.
    /// </exception>
    /// <example>
    /// <code>
    /// await using var download = await http.GetStreamAsync(sourceUrl);
    /// var estimate = Pdf.EstimateCompression(download, new PdfCompressOptions
    /// {
    ///     Images = new PdfImageCompression { MaxDpi = 150, JpegQuality = 70 },
    /// });
    /// Console.WriteLine($"about {estimate.EstimatedSavedBytes:N0} bytes would be saved");
    /// </code>
    /// </example>
    public static CompressionEstimate EstimateCompression(Stream input, PdfCompressOptions? compressOptions = null, PdfOptions? options = null) =>
        CompressionEngine.Estimate(input, compressOptions, options, CancellationToken.None);

    /// <summary>
    /// Asynchronous equivalent of <see cref="EstimateCompression(Stream, PdfCompressOptions?, PdfOptions?)"/>:
    /// a non-seekable <paramref name="input"/> is copied to its temporary file asynchronously,
    /// then the estimate runs on the thread pool, with cooperative cancellation —
    /// <paramref name="cancellationToken"/> is checked while copying, between pages while
    /// planning and between sampled images.
    /// </summary>
    /// <param name="input">A readable stream holding the document. Seekable or not.</param>
    /// <param name="compressOptions">The levers Compress would apply. Defaults to <see cref="PdfCompressOptions.Default"/> (lossless only).</param>
    /// <param name="options">Options controlling the open and the planned save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <param name="cancellationToken">Stops the call while copying the input, or between pages or sampled images.</param>
    /// <returns>The same estimate <see cref="EstimateCompression(Stream, PdfCompressOptions?, PdfOptions?)"/> returns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="ArgumentException">See <see cref="EstimateCompression(Stream, PdfCompressOptions?, PdfOptions?)"/>.</exception>
    /// <exception cref="PlumePdfException">See <see cref="EstimateCompression(Stream, PdfCompressOptions?, PdfOptions?)"/>.</exception>
    /// <example>
    /// <code>
    /// await using var download = await http.GetStreamAsync(sourceUrl, timeout.Token);
    /// var estimate = await Pdf.EstimateCompressionAsync(download, cancellationToken: timeout.Token);
    /// </code>
    /// </example>
    public static Task<CompressionEstimate> EstimateCompressionAsync(Stream input, PdfCompressOptions? compressOptions = null, PdfOptions? options = null, CancellationToken cancellationToken = default) =>
        CompressionEngine.EstimateAsync(input, compressOptions, options, cancellationToken);

    /// <summary>
    /// Opens the document at <paramref name="path"/> and runs PlumePDF's in-process PDF/A
    /// structural self-check over it (<c>PdfAValidator</c>): the enumerated
    /// 1b/2b subset — encryption absent, version ceiling, <c>pdfaid</c> identification,
    /// XMP/DocInfo agreement, OutputIntent present, forbidden filters absent,
    /// <c>/NeedAppearances</c> absent, plus the part-specific rule set the document's own
    /// declaration selects. Honestly bounded: a rule PlumePDF does not check is reported
    /// <see cref="PdfARuleStatus.NotChecked"/>, never silently folded into "passing" — the
    /// veraPDF CLI remains the full-coverage oracle (see <c>docs/cookbook/validate-pdfa.md</c>'s
    /// coverage table).
    /// </summary>
    /// <param name="path">The path to the PDF/A candidate.</param>
    /// <param name="options">Options controlling the open. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <returns>Per-rule findings plus the aggregate <see cref="PdfAValidationResult.IsConformant"/> verdict (true = no <em>checked</em> rule failed).</returns>
    /// <exception cref="PlumePdfException">The file could not be opened or recovered as a PDF document.</exception>
    /// <example>
    /// <code>
    /// var result = Pdf.ValidatePdfA("archival.pdf");
    /// Console.WriteLine($"PDF/A-{result.DeclaredPart}{result.DeclaredConformance}: {(result.IsConformant ? "conformant (per this self-check)" : "NOT conformant")}");
    /// foreach (var failure in result.Failures)
    /// {
    ///     Console.WriteLine($"FAIL [{failure.RuleId}] {failure.Message}");
    /// }
    /// </code>
    /// </example>
    public static PdfAValidationResult ValidatePdfA(string path, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var document = PdfDocument.Open(path, options);
        return PdfAValidator.Validate(document);
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/> and saves a linearized ("fast web view",
    /// ISO 32000-1 Annex F) copy to <paramref name="outputPath"/> — equivalent to
    /// <see cref="PdfDocument.Save"/> with <see cref="PdfOptions.Linearize"/> set: first-page
    /// objects first, a linearization parameter dictionary in the first kilobyte, and a
    /// primary hint stream, so a byte-range-capable viewer renders page one before the rest of
    /// the file arrives. A linearizing save is a full rewrite: on a signed
    /// source it inherits the same signed-source guard unchanged — a <c>PLUME5014</c> diagnostic by
    /// default, a refusal under <see cref="PdfOptions.Strict"/> — and any later incremental
    /// update de-linearizes the output by construction (<c>PLUME5019</c>).
    /// </summary>
    /// <param name="path">The path to open.</param>
    /// <param name="outputPath">Where to write the linearized document. May equal <paramref name="path"/>.</param>
    /// <param name="options">Options controlling the open and save; <see cref="PdfOptions.Linearize"/> is forced on. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException">
    /// The source is encrypted (<c>PLUME5001</c> — no encryption write in v1.0), the document
    /// has no pages (<c>PLUME5020</c>), or <paramref name="options"/> also requests
    /// <see cref="PdfOptions.Optimize"/> (<c>PLUME5018</c> — linearized output uses classic
    /// cross-reference tables, the two cannot combine in v1.0).
    /// </exception>
    /// <example>
    /// <code>
    /// Pdf.Linearize("report.pdf", "report-web.pdf");
    /// </code>
    /// </example>
    public static void Linearize(string path, string outputPath, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        using var document = PdfDocument.Open(path, options);
        document.Save(outputPath, (options ?? PdfOptions.Default) with { Linearize = true });
    }

    /// <summary>
    /// Opens the document at <paramref name="path"/>, paints <paramref name="stamp"/> onto
    /// every page (<see cref="PdfDocument.Stamp"/> — opened-document stamping),
    /// and writes the result to <paramref name="outputPath"/> via
    /// <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/> — stamping is purely
    /// additive, so the signature-preserving incremental path is the right default: prior
    /// revisions, including any existing digital signature's signed bytes, stay intact.
    /// </summary>
    /// <param name="path">The path to open and stamp.</param>
    /// <param name="outputPath">Where to write the stamped document. May equal <paramref name="path"/>.</param>
    /// <param name="stamp">What to paint — the same descriptor <see cref="Elements.Section.Stamps"/> uses for composed documents.</param>
    /// <param name="options">Options controlling the open and save. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException">
    /// The document has no pages (<c>PLUME6073</c>); the stamp text has a character outside
    /// the Standard-14 Helvetica-Bold WinAnsi encoding (<c>PLUME6072</c>); or the source's
    /// cross-reference data was recovered via a brute-force scan and has no reliable prior
    /// <c>startxref</c> offset for <c>SaveIncremental</c> to chain onto (<c>PLUME5005</c>) —
    /// open the document, call <see cref="PdfDocument.Stamp"/>, and <see cref="PdfDocument.Save"/>
    /// directly instead for such a source.
    /// </exception>
    /// <example>
    /// <code>
    /// Pdf.Stamp("report.pdf", "report-stamped.pdf", new Elements.Stamp
    /// {
    ///     Text = "CONFIDENTIAL",
    ///     Position = Elements.StampPosition.TopRight,
    /// });
    /// </code>
    /// </example>
    public static void Stamp(string path, string outputPath, Elements.Stamp stamp, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(stamp);

        using var document = PdfDocument.Open(path, options);
        document.Stamp(stamp);
        document.SaveIncremental(outputPath, options);
    }

    /// <summary>The plain-text convenience for <see cref="Stamp(string,string,Elements.Stamp,PdfOptions?)"/> — <paramref name="text"/> with the <see cref="Elements.Stamp"/> defaults (12pt, opaque, dark red, top-right corner).</summary>
    /// <example>
    /// <code>
    /// Pdf.Stamp("report.pdf", "report-stamped.pdf", "APPROVED");
    /// </code>
    /// </example>
    public static void Stamp(string path, string outputPath, string text, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        Stamp(path, outputPath, new Elements.Stamp { Text = text }, options);
    }

    // --- Phase 7 (Raster codecs + image->PDF) -----------------------------------------------
    // One frame -> one page, sized in points
    // from the frame's own DPI (96 fallback, matching LayoutEngine.MeasureImage's existing
    // default — Section.PageSize + Margins.Uniform(0)). Batch degradation:
    // a source that fails to decode is skipped with a diagnostic and the rest of the batch
    // proceeds, UNLESS it is the only source (nothing left to return) or PdfOptions.Strict
    // upgrades every deviation to a throw. PDF/A output is a coded refusal (PLUME3610) —
    // Compose via Manuscript with PdfOptions.PdfAConformance for archival output instead.

    /// <summary>
    /// Decodes every image at <paramref name="paths"/> (<see cref="RasterImage.Decode(string,PdfOptions?)"/>
    /// — PNG, JPEG, TIFF, and JPEG 2000 (<c>.jp2</c>/<c>.j2k</c>) sources all decode)
    /// and composes one new document with one DPI-sized page per decoded frame, in order.
    /// </summary>
    /// <param name="paths">The image file paths to decode, in the order pages should appear.</param>
    /// <param name="options">Options controlling decode caps/<see cref="PdfOptions.Strict"/> and the compose. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <returns>A new, unsaved <see cref="PdfDocument"/> — call <see cref="PdfDocument.Save(string,PdfOptions?)"/> to write it.</returns>
    /// <exception cref="PlumePdfException">
    /// <paramref name="paths"/> is empty (<c>PLUME3611</c>); <paramref name="options"/> requests
    /// <see cref="PdfOptions.PdfAConformance"/> (<c>PLUME3610</c> — not supported by this verb);
    /// or every source failed to decode, or the only source did
    /// (<see cref="RasterImage.Decode(string,PdfOptions?)"/>'s own codes propagate).
    /// </exception>
    /// <example>
    /// <code>
    /// using var scan = Pdf.FromImages(["page-01.png", "page-02.png"]);
    /// scan.Save("scan.pdf");
    /// </code>
    /// </example>
    public static PdfDocument FromImages(IEnumerable<string> paths, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var list = paths.ToList();
        var attempts = list.Select(static path => (Label: $"'{path}'", Decode: (Func<PdfOptions, RasterImage>)(opts => RasterImage.Decode(path, opts)))).ToList();
        return ComposeFromAttempts(attempts, options ?? PdfOptions.Default);
    }

    /// <summary>The bytes-in-hand overload of <see cref="FromImages(IEnumerable{string},PdfOptions?)"/> — decodes each already-in-memory image file's bytes (<see cref="RasterImage.Decode(ReadOnlyMemory{byte},PdfOptions?)"/>).</summary>
    /// <param name="images">The encoded image file bytes to decode, in the order pages should appear.</param>
    /// <param name="options">See <see cref="FromImages(IEnumerable{string},PdfOptions?)"/>.</param>
    /// <example>
    /// <code>
    /// byte[][] downloaded = await Task.WhenAll(urls.Select(DownloadImageBytesAsync));
    /// using var scan = Pdf.FromImages(downloaded.Select(b => (ReadOnlyMemory&lt;byte&gt;)b));
    /// scan.Save("scan.pdf");
    /// </code>
    /// </example>
    public static PdfDocument FromImages(IEnumerable<ReadOnlyMemory<byte>> images, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        var list = images.ToList();
        var attempts = list.Select(static (bytes, i) => (Label: $"byte source #{i} ({bytes.Length} bytes)", Decode: (Func<PdfOptions, RasterImage>)(opts => RasterImage.Decode(bytes, opts)))).ToList();
        return ComposeFromAttempts(attempts, options ?? PdfOptions.Default);
    }

    /// <summary>
    /// The already-decoded overload of <see cref="FromImages(IEnumerable{string},PdfOptions?)"/>
    /// — composes directly from <see cref="RasterImage"/>s a caller decoded itself (e.g. to
    /// inspect or re-check <see cref="RasterImage.Diagnostics"/> before composing). Every frame
    /// of every supplied <see cref="RasterImage"/> becomes one page, in order — a multi-frame
    /// source (a multi-page TIFF) contributes one page per frame.
    /// </summary>
    /// <param name="images">The already-decoded images, in the order pages should appear.</param>
    /// <param name="options">See <see cref="FromImages(IEnumerable{string},PdfOptions?)"/>. Only <see cref="PdfOptions.PdfAConformance"/>/<see cref="PdfOptions.Strict"/> and the compose step consult this — the images are already decoded.</param>
    /// <example>
    /// <code>
    /// var decoded = paths.Select(p => RasterImage.Decode(p)).ToList();
    /// foreach (var image in decoded)
    /// {
    ///     if (image.Diagnostics.Count &gt; 0)
    ///     {
    ///         Console.WriteLine($"{image.Diagnostics.Count} deviation(s) while decoding.");
    ///     }
    /// }
    ///
    /// using var scan = Pdf.FromImages(decoded);
    /// scan.Save("scan.pdf");
    /// </code>
    /// </example>
    public static PdfDocument FromImages(IEnumerable<RasterImage> images, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        var list = images.ToList();
        var attempts = list.Select(static (image, i) => (Label: $"RasterImage #{i}", Decode: (Func<PdfOptions, RasterImage>)(_ => image))).ToList();
        return ComposeFromAttempts(attempts, options ?? PdfOptions.Default);
    }

    /// <summary>The open-decode-compose-save convenience for <see cref="FromImages(IEnumerable{string},PdfOptions?)"/> — writes the result to <paramref name="outputPath"/> directly.</summary>
    /// <param name="paths">The image file paths to decode, in the order pages should appear.</param>
    /// <param name="outputPath">Where to write the composed document. Must not equal any entry in <paramref name="paths"/> (<c>PLUME3612</c>) — <see cref="RasterImage.Decode(string,PdfOptions?)"/> streams each source file open while decoding it, so overwriting one mid-batch is refused rather than risked.</param>
    /// <param name="options">See <see cref="FromImages(IEnumerable{string},PdfOptions?)"/>.</param>
    /// <example>
    /// <code>
    /// Pdf.FromImages(["page-01.png", "page-02.png"], "scan.pdf");
    /// </code>
    /// </example>
    public static void FromImages(IEnumerable<string> paths, string outputPath, PdfOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var list = paths.ToList();
        var fullOutputPath = ResolveFullPathOrThrow(outputPath);
        foreach (var path in list)
        {
            if (string.Equals(ResolveFullPathOrThrow(path), fullOutputPath, SelfOverwritePathComparison))
            {
                throw new PlumePdfException("PLUME3612", $"Pdf.FromImages: outputPath ('{outputPath}') is also one of the input sources — refusing to overwrite a source this batch still needs to read.");
            }
        }

        using var document = FromImages(list, options);
        document.Save(outputPath, options);
    }

    /// <summary>
    /// The comparison the self-overwrite guard above uses to compare two full paths.
    /// Case-sensitive filesystems (default Linux) genuinely distinguish <c>scan.pdf</c> from
    /// <c>scan.PDF</c> as different files; the platforms whose default filesystem does not
    /// (Windows' NTFS, macOS' default APFS/HFS+) need a case-insensitive comparison here or
    /// the guard silently misses a same-file collision that differs only in case.
    /// </summary>
    private static StringComparison SelfOverwritePathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Resolves <paramref name="path"/> to a full path, translating a malformed path's raw BCL exception into a coded <see cref="PlumePdfException"/> (<c>PLUME3615</c>) rather than letting it escape this verb.</summary>
    private static string ResolveFullPathOrThrow(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PlumePdfException("PLUME3615", $"Pdf.FromImages: '{path}' is not a valid file path ({ex.Message}).", ex);
        }
    }

    private static PdfDocument ComposeFromAttempts(IReadOnlyList<(string Label, Func<PdfOptions, RasterImage> Decode)> attempts, PdfOptions options)
    {
        if (options.PdfAConformance != PdfAConformance.None)
        {
            throw new PlumePdfException("PLUME3610", $"Pdf.FromImages does not produce PDF/A output (requested: {options.PdfAConformance}) — compose via Manuscript with PdfOptions.PdfAConformance instead, embedding each RasterImage's decoded RGB/gray pixel frame(s) as Elements.Image. PNG, TIFF, and JPEG 2000 (.jp2/.j2k) sources already decode to pixel frames this way; only a CMYK-JPEG source's original DCT bytes are not a supported path into PDF/A in this version (ManuscriptRenderer's DCT pass-through has no PdfAConformance color guard) — decode it (RasterImage already normalizes CMYK/YCCK JPEGs to RGB) rather than passing OriginalJpegBytes through.");
        }

        if (attempts.Count == 0)
        {
            throw new PlumePdfException("PLUME3611", "Pdf.FromImages: no image sources were supplied.");
        }

        var diagnostics = new DiagnosticCollection();
        var frames = new List<RasterImageFrame>();
        PlumePdfException? lastFailure = null;

        for (var i = 0; i < attempts.Count; i++)
        {
            var (label, decode) = attempts[i];
            try
            {
                var decoded = decode(options);
                frames.AddRange(decoded.Frames);
                foreach (var deviation in decoded.Diagnostics)
                {
                    diagnostics.Add(deviation);
                }
            }
            catch (PlumePdfException ex)
            {
                // R8: a single source with nothing else to fall back on always throws; in a
                // multi-source batch, Strict upgrades every skip to a throw too. Otherwise the
                // source is skipped with a diagnostic and the rest of the batch proceeds.
                if (attempts.Count == 1 || options.Strict)
                {
                    throw;
                }

                lastFailure = ex;
                diagnostics.Add(new PdfDiagnostic("PLUME3614", DiagnosticSeverity.Warning, $"Pdf.FromImages: source {i} ({label}) could not be decoded ({ex.Code}: {ex.Message}); skipped.", subject: null));
            }
        }

        if (frames.Count == 0)
        {
            throw lastFailure ?? new PlumePdfException("PLUME3611", "Pdf.FromImages: no source produced a decodable frame.");
        }

        var manuscript = new Manuscript { Sections = frames.Select(BuildPageSection).ToList() };
        var document = manuscript.Render(options);
        foreach (var deviation in diagnostics)
        {
            document.Diagnostics.Add(deviation);
        }

        return document;
    }

    /// <summary>One frame -> one same-size page: <see cref="Elements.Section.PageSize"/> and the body <see cref="Elements.Image"/>'s rendered size both come from the same DPI-derived point size (96 dpi fallback, <see cref="Elements.Image(RasterImageFrame)"/>'s own default), so <c>LayoutEngine.MeasureImage</c>'s width-fits-page check always passes exactly — <see cref="Elements.Margins.Uniform"/>(0) leaves no margin to mismatch against.</summary>
    private static Elements.Section BuildPageSection(RasterImageFrame frame)
    {
        var dpiX = frame.XDpi ?? 96.0;
        var dpiY = frame.YDpi ?? 96.0;
        var pageWidth = frame.Width * 72.0 / dpiX;
        var pageHeight = frame.Height * 72.0 / dpiY;

        return new Elements.Section
        {
            PageSize = new Elements.PageSize(pageWidth, pageHeight),
            Margins = Elements.Margins.Uniform(0),
            Body = new Elements.Image(frame) { Width = pageWidth, Height = pageHeight },
        };
    }
}
