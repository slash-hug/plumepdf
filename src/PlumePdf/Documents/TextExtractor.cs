using PlumePdf.Content;
using PlumePdf.Documents.Structure;
using PlumePdf.Fonts.Reading;
using PlumePdf.Objects;

namespace PlumePdf.Documents;

/// <summary>
/// Page-space geometry helpers shared by <see cref="TextExtractor"/> and
/// <c>ImageExtractor</c>: resolving a page's effective <c>/MediaBox</c>/<c>/Rotate</c>
/// (ISO 32000-1 §7.7.3.3, §14.11.2) and building the normalization matrix that maps raw PDF
/// user-space coordinates onto page space (points, origin at the page's
/// visual bottom-left corner as displayed, y increasing upward, after <c>/Rotate</c> is
/// applied) — the same convention <c>PlumePdf.Layout</c> uses for composed documents.
/// </summary>
internal static class PageSpace
{
    private static readonly PdfName MediaBoxName = PdfName.Get("MediaBox");
    private static readonly PdfName RotateName = PdfName.Get("Rotate");

    /// <summary>Resolves a page's effective <c>/MediaBox</c> as <c>(llx, lly, urx, ury)</c>, defaulting to US Letter (§7.7.3.3) when absent or malformed.</summary>
    public static (double Llx, double Lly, double Urx, double Ury) GetMediaBox(PdfDictionary pageDictionary)
    {
        if (pageDictionary.TryGetValue(MediaBoxName, out var value) && value is PdfArray { Count: 4 } array
            && array[0] is PdfNumber a && array[1] is PdfNumber b && array[2] is PdfNumber c && array[3] is PdfNumber d)
        {
            var llx = Math.Min(a.Value, c.Value);
            var lly = Math.Min(b.Value, d.Value);
            var urx = Math.Max(a.Value, c.Value);
            var ury = Math.Max(b.Value, d.Value);
            if (double.IsFinite(llx) && double.IsFinite(lly) && double.IsFinite(urx) && double.IsFinite(ury) && urx > llx && ury > lly)
            {
                return (llx, lly, urx, ury);
            }
        }

        return (0, 0, 612, 792);
    }

    /// <summary>Resolves a page's effective <c>/Rotate</c>, normalized to one of 0/90/180/270 (§7.7.3.3: a multiple of 90; negative and out-of-range values wrap).</summary>
    public static int GetRotation(PdfDictionary pageDictionary)
    {
        if (pageDictionary.TryGetValue(RotateName, out var value) && value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var degrees))
        {
            var normalized = ((degrees % 360) + 360) % 360;
            return normalized - (normalized % 90);
        }

        return 0;
    }

    /// <summary>
    /// Builds the matrix mapping a raw PDF user-space point within <paramref name="mediaBox"/>
    /// onto page space: translated so the box's origin is (0,0), then rotated by
    /// <paramref name="rotate"/> degrees clockwise (the direction <c>/Rotate</c> specifies for
    /// display) about that origin, keeping the result in the first quadrant.
    /// </summary>
    public static PdfMatrix NormalizationMatrix((double Llx, double Lly, double Urx, double Ury) mediaBox, int rotate)
    {
        var (llx, lly, urx, ury) = mediaBox;
        var width = urx - llx;
        var height = ury - lly;

        return rotate switch
        {
            90 => new PdfMatrix(0, -1, 1, 0, -lly, width + llx),
            180 => new PdfMatrix(-1, 0, 0, -1, width + llx, height + lly),
            270 => new PdfMatrix(0, 1, -1, 0, height + lly, -llx),
            _ => new PdfMatrix(1, 0, 0, 1, -llx, -lly),
        };
    }

    /// <summary>The page's displayed size (after <see cref="GetRotation"/> is applied — width/height swap for a 90/270 rotation).</summary>
    public static (double Width, double Height) GetDisplaySize((double Llx, double Lly, double Urx, double Ury) mediaBox, int rotate)
    {
        var width = mediaBox.Urx - mediaBox.Llx;
        var height = mediaBox.Ury - mediaBox.Lly;
        return rotate is 90 or 270 ? (height, width) : (width, height);
    }

    /// <summary>Resolves an indirect reference to its target, or returns <paramref name="value"/> unchanged when it is already direct (or <see langword="null"/>).</summary>
    public static PdfObject? Resolve(PdfObject? value, ObjectRegistry objects) => value switch
    {
        null => null,
        PdfReference reference => objects[reference.Target],
        _ => value,
    };

    /// <summary>Resolves <paramref name="value"/> (see <see cref="Resolve"/>) and returns it as a <see cref="PdfDictionary"/>, or <see langword="null"/> when it isn't one.</summary>
    public static PdfDictionary? ResolveDictionary(PdfObject? value, ObjectRegistry objects) => Resolve(value, objects) as PdfDictionary;
}

/// <summary>
/// Walks a page's content stream(s), tracking graphics and text state (§8, §9), and produces
/// the page's <see cref="ExtractedText"/> result: positioned <see cref="Letter"/>s plus the
/// <c>WordAssembler</c>/<c>ReadingOrderer</c> assembly built on top of them. Font-code decoding
/// (character code → Unicode, and code → advance width) delegates to
/// <see cref="ExtractionFontFactory"/> — simple-font base-encoding/<c>/Differences</c>
/// resolution (falling back to the Standard-14 AFM metrics for a font with no <c>/Widths</c>
/// array), Type0/Identity-H and embedded-CMap decoding, and the <c>/ToUnicode</c> overlay all
/// go through one <see cref="ExtractionFontFactory"/> shared across this one extraction
/// call (fresh per call — see the factory's own lifetime remarks).
/// "Threading &amp; mutation": each extraction call is independent, so nothing here is shared
/// across calls or pages). Form XObject (<c>Do</c>) recursion is bounded two ways
/// against a hostile or pathological content stream/XObject graph:
/// <see cref="PdfTextExtractionOptions.MaxXObjectNestingDepth"/> caps how deep <c>Do</c> may
/// nest, and <see cref="PdfOptions.MaxContentStreamOperators"/> caps the *cumulative* number of
/// operators processed across the whole page — including every nested form's own content
/// stream, not just the top-level one — so a small, shallow-looking document that fans out into
/// an enormous number of total Form XObject invocations (fanout^depth) is refused long before it
/// can burn CPU, independent of any single stream's own operator count staying small. Total
/// letters per page is bounded separately by
/// <see cref="PdfTextExtractionOptions.MaxLettersPerPage"/>.
/// </summary>
internal static class TextExtractor
{
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName FontName = PdfName.Get("Font");
    private static readonly PdfName XObjectName = PdfName.Get("XObject");
    private static readonly PdfName SubtypeName = PdfName.Subtype;
    private static readonly PdfName MatrixName = PdfName.Get("Matrix");
    private static readonly PdfName PropertiesName = PdfName.Get("Properties");
    private static readonly PdfName McidName = PdfName.Get("MCID");

    // Used only when a text-showing operator's font resource can't be resolved at all (no
    // /Font entry, or the resource name isn't in it) — the text is never dropped
    // silently. ASCII passes through as itself (matching the printable range every simple
    // encoding agrees on); anything else decodes to U+FFFD. No diagnostics collection (this
    // instance is shared across every extraction call), no /Widths, default width 500/1000 em.
    private static readonly ExtractionFont FallbackFont = new SimpleExtractionFont(
        "Unknown",
        BuildFallbackEncoding(),
        new Dictionary<int, double>(),
        missingWidth: 500,
        toUnicode: null,
        PdfOptions.Default,
        diagnostics: null);

    /// <summary>
    /// Extracts <paramref name="page"/>'s positioned text. When <paramref name="owner"/> and
    /// <paramref name="pageIndex"/> identify the page within an owning document that carries a
    /// parseable <c>/StructTreeRoot</c>, reading order is structure-tree-driven:
    /// words are ranked by the tag tree's own document order via the MCID each letter
    /// was painted under. Otherwise — untagged document, unparseable tree, or no tagged word
    /// on this page — ordering falls back to the geometric heuristic and a <c>PLUME6070</c>
    /// informational diagnostic records why.
    /// </summary>
    /// <param name="page">The page to extract.</param>
    /// <param name="objects">The owning document's object registry.</param>
    /// <param name="options">The owning document's options.</param>
    /// <param name="extractionOptions">Per-call resource-limit overrides.</param>
    /// <param name="owner">The owning document, for the structure-tree reading-order pass — or <see langword="null"/> for a page with no owning document (geometric ordering only).</param>
    /// <param name="pageIndex">The page's zero-based index in <paramref name="owner"/>'s page list, or -1 when unknown.</param>
    public static ExtractedText Extract(PdfPage page, ObjectRegistry objects, PdfOptions options, PdfTextExtractionOptions? extractionOptions, PdfDocument? owner = null, int pageIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);

        var effective = extractionOptions ?? PdfTextExtractionOptions.Default;
        var diagnostics = new DiagnosticCollection();
        var letters = new List<Letter>();

        var contentBytes = ReadContentBytes(page.Dictionary, objects, options, diagnostics);
        if (contentBytes.Length > 0)
        {
            var mediaBox = PageSpace.GetMediaBox(page.Dictionary);
            var rotate = PageSpace.GetRotation(page.Dictionary);
            var normalization = PageSpace.NormalizationMatrix(mediaBox, rotate);

            var resources = ResolveDictionary(page.Dictionary.TryGetValue(ResourcesName, out var r) ? r : null, objects);
            var graphics = new GraphicsStateStack();
            graphics.Concatenate(normalization);
            var text = new TextState();
            var paramStack = new Stack<TextParamsSnapshot>();
            var fontFactory = new ExtractionFontFactory(objects, options.Filters, options, diagnostics);
            var letterCount = 0;

            var context = new WalkContext(
                objects,
                options,
                diagnostics,
                fontFactory,
                letters,
                effective.MaxLettersPerPage ?? options.MaxLettersPerPage,
                effective.MaxXObjectNestingDepth ?? options.MaxXObjectNestingDepth);

            WalkContent(contentBytes, resources, graphics, text, paramStack, context, depth: 0, ref letterCount);
        }

        var words = WordAssembler.Assemble(letters);

        // A document with a parseable /StructTreeRoot gets structure-tree-driven
        // reading order — the tree's own document order, resolved page-side via each word's
        // MCID provenance. The orderer itself falls back geometrically (recording PLUME6070)
        // when no usable order exists: untagged document, unparseable/absent tree, or a page
        // none of whose words carry a structure-referenced MCID.
        IReadOnlyList<int>? structureOrder = null;
        if (owner is not null && pageIndex >= 0)
        {
            var structure = StructureTreeReader.Read(owner, options, diagnostics);
            if (structure.Root is { } structureRoot)
            {
                structureOrder = ReadingOrderer.PageMcidOrder(structureRoot, pageIndex);
            }
        }

        var mcidPerWord = new int?[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            mcidPerWord[i] = words[i].Mcid;
        }

        var (lines, orderedWords, orderedText) = ReadingOrderer.Order(words, mcidPerWord, structureOrder, diagnostics);

        return new ExtractedText(page.Reference, orderedText, lines, orderedWords, letters, diagnostics);
    }

    // Bundles the per-extraction, non-changing collaborators so the recursive WalkContent
    // signature stays readable instead of threading eight separate parameters through every
    // call. TotalOperators is the one mutable member — the cumulative content-stream-operator
    // count enforced across the whole page (see the class remarks' Form XObject fan-out guard).
    private sealed class WalkContext(
        ObjectRegistry objects,
        PdfOptions options,
        DiagnosticCollection diagnostics,
        ExtractionFontFactory fontFactory,
        List<Letter> letters,
        int maxLettersPerPage,
        int maxXObjectDepth)
    {
        public ObjectRegistry Objects { get; } = objects;
        public PdfOptions Options { get; } = options;
        public DiagnosticCollection Diagnostics { get; } = diagnostics;
        public ExtractionFontFactory FontFactory { get; } = fontFactory;
        public List<Letter> Letters { get; } = letters;
        public int MaxLettersPerPage { get; } = maxLettersPerPage;
        public int MaxXObjectDepth { get; } = maxXObjectDepth;
        public long TotalOperators { get; set; }

        /// <summary>
        /// The open marked-content sequences (<c>BMC</c>/<c>BDC</c>…<c>EMC</c>, ISO 32000-1
        /// §14.6) enclosing the operator being processed right now — one entry per open
        /// sequence, carrying that sequence's effective MCID (its own <c>/MCID</c> property
        /// when it declares one, else the enclosing sequence's, so nested non-MCID sequences
        /// inherit correctly). Shared across nested Form XObject walks, since a page's
        /// marked-content nesting encloses whatever a <c>Do</c> inside it paints. Bounded for
        /// free by <see cref="PdfOptions.MaxContentStreamOperators"/>: each entry costs one
        /// <c>BMC</c>/<c>BDC</c> operator, which the cumulative operator cap already counts.
        /// </summary>
        public Stack<int?> MarkedContentStack { get; } = new();

        /// <summary>The innermost effective MCID, or <see langword="null"/> outside any MCID-carrying sequence.</summary>
        public int? CurrentMcid => MarkedContentStack.TryPeek(out var mcid) ? mcid : null;
    }

    private readonly record struct TextParamsSnapshot(double Tc, double Tw, double Th, double Tl, string? Font, double Tfs, double Ts, int Tr);

    private static void WalkContent(byte[] content, PdfDictionary? resources, GraphicsStateStack graphics, TextState text, Stack<TextParamsSnapshot> paramStack, WalkContext ctx, int depth, ref int letterCount)
    {
        var operations = ContentStreamReader.Read(content, ctx.Options, ctx.Diagnostics, ctx.Options.MaxContentStreamOperators);

        // Cumulative across the whole page, including every nested Form XObject's own content
        // stream — not reset per call — so a document that fans a small, shallow-looking form
        // graph out into an enormous total number of Do invocations is bounded by total work
        // done, not merely by nesting depth (see the class remarks).
        ctx.TotalOperators += operations.Count;
        if (ctx.TotalOperators > ctx.Options.MaxContentStreamOperators)
        {
            throw new PlumePdfException("PLUME6028", $"Page text extraction processed more than {ctx.Options.MaxContentStreamOperators} content-stream operators across the page and its nested Form XObjects; refusing to continue (a resource-limit guard against a hostile or pathological XObject graph).");
        }

        ExtractionFont? currentFont = null;
        string? currentFontResourceName = null;

        foreach (var op in operations)
        {
            switch (op.Operator)
            {
                case "q":
                    graphics.Save();
                    paramStack.Push(new TextParamsSnapshot(text.CharacterSpacing, text.WordSpacing, text.HorizontalScaling, text.Leading, text.FontResourceName, text.FontSize, text.Rise, text.RenderingMode));
                    break;

                case "Q":
                    if (!graphics.Restore())
                    {
                        Report("PLUME7014", $"'Q' near offset {op.Offset} has no matching 'q' to restore; ignoring it.", ctx);
                    }

                    if (paramStack.TryPop(out var snapshot))
                    {
                        text.CharacterSpacing = snapshot.Tc;
                        text.WordSpacing = snapshot.Tw;
                        text.HorizontalScaling = snapshot.Th;
                        text.Leading = snapshot.Tl;
                        text.FontResourceName = snapshot.Font;
                        text.FontSize = snapshot.Tfs;
                        text.Rise = snapshot.Ts;
                        text.RenderingMode = snapshot.Tr;
                        currentFontResourceName = null; // force font re-resolution against the restored font
                    }

                    break;

                case "cm":
                    {
                        var m = ReadMatrix(op.Operands);
                        if (!m.IsFinite)
                        {
                            Report("PLUME7015", $"'cm' near offset {op.Offset} has a non-finite operand; the CTM was left unchanged.", ctx);
                            break;
                        }

                        graphics.Concatenate(m);
                        break;
                    }

                case "BT":
                    text.BeginTextObject();
                    break;

                case "ET":
                    text.EndTextObject();
                    break;

                case "Tf":
                    text.FontResourceName = op.Operands.Count > 0 && op.Operands[0] is PdfName fontName ? fontName.Value : null;
                    text.FontSize = Num(op.Operands, 1);
                    currentFontResourceName = null;
                    break;

                case "Tc":
                    text.CharacterSpacing = Num(op.Operands, 0);
                    break;

                case "Tw":
                    text.WordSpacing = Num(op.Operands, 0);
                    break;

                case "Tz":
                    text.HorizontalScaling = Num(op.Operands, 0) / 100.0;
                    break;

                case "TL":
                    text.Leading = Num(op.Operands, 0);
                    break;

                case "Ts":
                    text.Rise = Num(op.Operands, 0);
                    break;

                case "Tr":
                    text.RenderingMode = (int)Num(op.Operands, 0);
                    break;

                case "Td":
                    text.MoveToNextLine(Num(op.Operands, 0), Num(op.Operands, 1));
                    break;

                case "TD":
                    text.Leading = -Num(op.Operands, 1);
                    text.MoveToNextLine(Num(op.Operands, 0), Num(op.Operands, 1));
                    break;

                case "Tm":
                    {
                        var m = ReadMatrix(op.Operands);
                        if (!m.IsFinite)
                        {
                            Report("PLUME7015", $"'Tm' near offset {op.Offset} has a non-finite operand; the text matrix was left unchanged.", ctx);
                            break;
                        }

                        text.SetTextMatrix(m);
                        break;
                    }

                case "T*":
                    text.MoveToNextLineWithLeading();
                    break;

                case "Tj":
                    ShowText(FirstStringBytes(op.Operands), graphics, text, resources, ctx, ref currentFont, ref currentFontResourceName, ref letterCount);
                    break;

                case "'":
                    text.MoveToNextLineWithLeading();
                    ShowText(FirstStringBytes(op.Operands), graphics, text, resources, ctx, ref currentFont, ref currentFontResourceName, ref letterCount);
                    break;

                case "\"":
                    text.WordSpacing = Num(op.Operands, 0);
                    text.CharacterSpacing = Num(op.Operands, 1);
                    text.MoveToNextLineWithLeading();
                    ShowText(op.Operands.Count > 2 ? StringBytes(op.Operands[2]) : null, graphics, text, resources, ctx, ref currentFont, ref currentFontResourceName, ref letterCount);
                    break;

                case "TJ":
                    if (op.Operands.Count > 0 && op.Operands[0] is PdfArray array)
                    {
                        foreach (var element in array)
                        {
                            if (element is PdfNumber adjustment)
                            {
                                text.ApplyPositioningAdjustment(adjustment.Value);
                            }
                            else
                            {
                                var bytes = StringBytes(element);
                                if (bytes is not null)
                                {
                                    ShowText(bytes, graphics, text, resources, ctx, ref currentFont, ref currentFontResourceName, ref letterCount);
                                }
                            }
                        }
                    }

                    break;

                case "Do":
                    HandleDo(op, resources, graphics, text, paramStack, ctx, depth, ref letterCount);
                    break;

                // Marked content (ISO 32000-1 §14.6): tracked purely for MCID provenance —
                // which structure-tree-referenced sequence each letter is painted inside
                // (the structure-tree reading order). BMC opens a sequence with no
                // properties (inherits the enclosing MCID); BDC's /MCID property, when present
                // (inline dictionary or a named /Properties resource), becomes the effective
                // MCID for everything until the matching EMC.
                case "BMC":
                    ctx.MarkedContentStack.Push(ctx.CurrentMcid);
                    break;

                case "BDC":
                    ctx.MarkedContentStack.Push(ReadBdcMcid(op, resources, ctx) ?? ctx.CurrentMcid);
                    break;

                case "EMC":
                    ctx.MarkedContentStack.TryPop(out _); // an unbalanced EMC is tolerated, matching the reader's lenient posture for the operators above.
                    break;

                default:
                    break; // path painting, color, clipping, ... - not needed for text extraction
            }
        }
    }

    /// <summary>
    /// Resolves a <c>BDC</c> operator's <c>/MCID</c>, or <see langword="null"/> when it carries
    /// none: the properties operand is either an inline dictionary or the name of a
    /// <c>/Properties</c> resource entry (ISO 32000-1 §14.6.2). Read leniently per the
    /// read-side convention: a non-integer or out-of-<see cref="int"/>-range <c>/MCID</c>
    /// (e.g. a hostile <c>1e20</c>) records a diagnostic and reads as "no MCID" — never a raw
    /// cast that would silently wrap to <see cref="int.MinValue"/>.
    /// </summary>
    private static int? ReadBdcMcid(ContentOperation op, PdfDictionary? resources, WalkContext ctx)
    {
        if (op.Operands.Count < 2)
        {
            return null;
        }

        var properties = op.Operands[1] switch
        {
            PdfDictionary inline => inline,
            PdfName named when resources is not null
                && ResolveDictionary(resources.TryGetValue(PropertiesName, out var props) ? props : null, ctx.Objects) is { } propertiesDict
                && propertiesDict.TryGetValue(named, out var entry)
                => ResolveDictionary(entry, ctx.Objects),
            _ => null,
        };

        if (properties is null || !properties.TryGetValue(McidName, out var mcidValue) || mcidValue is not PdfNumber mcidNumber)
        {
            return null;
        }

        if (!mcidNumber.TryToInt32(out var mcid) || mcid < 0)
        {
            Report("PLUME6082", $"'BDC' near offset {op.Offset} declares an /MCID of {mcidNumber.Value}, which is not a non-negative integer in range; treating the sequence as carrying no MCID.", ctx);
            return null;
        }

        return mcid;
    }

    private static void HandleDo(ContentOperation op, PdfDictionary? resources, GraphicsStateStack graphics, TextState text, Stack<TextParamsSnapshot> paramStack, WalkContext ctx, int depth, ref int letterCount)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not PdfName xobjectName || resources is null)
        {
            return;
        }

        var xobjectDict = ResolveDictionary(resources.TryGetValue(XObjectName, out var xo) ? xo : null, ctx.Objects);
        if (xobjectDict is null || !xobjectDict.TryGetValue(xobjectName, out var entry))
        {
            return;
        }

        if (Resolve(entry, ctx.Objects) is not PdfStream stream)
        {
            return;
        }

        var subtype = stream.Dictionary.TryGetValue(SubtypeName, out var st) ? (st as PdfName)?.Value : null;
        if (subtype != "Form")
        {
            return; // Image XObjects are ImageExtractor's job, not text extraction's.
        }

        if (depth >= ctx.MaxXObjectDepth)
        {
            throw new PlumePdfException("PLUME6020", $"Form XObject recursion exceeded the configured depth limit ({ctx.MaxXObjectDepth}) at offset {op.Offset} — refusing to continue (a resource-limit guard against a hostile or pathological XObject graph).");
        }

        byte[] formBytes;
        try
        {
            formBytes = stream.GetDecodedBytes(ctx.Options.Filters, ctx.Options, r => ctx.Objects[r]);
        }
        catch (PlumePdfException ex)
        {
            Report(ex.Code, $"Form XObject at offset {op.Offset} could not be decoded ({ex.Message}); skipping it.", ctx);
            return;
        }

        var formResources = ResolveDictionary(stream.Dictionary.TryGetValue(ResourcesName, out var fr) ? fr : null, ctx.Objects) ?? resources;

        graphics.Save();
        paramStack.Push(new TextParamsSnapshot(text.CharacterSpacing, text.WordSpacing, text.HorizontalScaling, text.Leading, text.FontResourceName, text.FontSize, text.Rise, text.RenderingMode));

        if (stream.Dictionary.TryGetValue(MatrixName, out var matrixValue) && matrixValue is PdfArray { Count: 6 } matrixArray)
        {
            var formMatrix = ReadMatrix([.. matrixArray]);
            if (formMatrix.IsFinite)
            {
                graphics.Concatenate(formMatrix);
            }
        }

        WalkContent(formBytes, formResources, graphics, text, paramStack, ctx, depth + 1, ref letterCount);

        paramStack.TryPop(out _);
        graphics.Restore();
    }

    private static void ShowText(byte[]? bytes, GraphicsStateStack graphics, TextState text, PdfDictionary? resources, WalkContext ctx, ref ExtractionFont? currentFont, ref string? currentFontResourceName, ref int letterCount)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        if (currentFont is null || currentFontResourceName != text.FontResourceName)
        {
            currentFont = ResolveFont(resources, text.FontResourceName, ctx);
            currentFontResourceName = text.FontResourceName;
        }

        var font = currentFont ?? FallbackFont;
        ReadOnlySpan<byte> remaining = bytes;

        while (remaining.Length > 0)
        {
            font.DecodeNext(remaining, out var codeLength, out var glyphText, out var widthGlyphSpace);
            if (codeLength <= 0)
            {
                break; // ExtractionFont.DecodeNext's contract guarantees forward progress; this is only a defensive floor.
            }

            // Word spacing (Tw, §9.3.3) applies only to the single-byte code 32 - never to a
            // byte value of 32 that's part of a multi-byte (Type0) code.
            var isWordSpaceCode = codeLength == 1 && remaining[0] == 32;
            var widthEm = widthGlyphSpace / 1000.0;

            var origin = text.ComputeRenderingMatrix(graphics.CurrentTransform);
            var p0 = origin.Transform(0, 0);
            var p1 = origin.Transform(widthEm, 0);
            var p2 = origin.Transform(widthEm, 1);
            var p3 = origin.Transform(0, 1);

            var minX = Min4(p0.X, p1.X, p2.X, p3.X);
            var maxX = Max4(p0.X, p1.X, p2.X, p3.X);
            var minY = Min4(p0.Y, p1.Y, p2.Y, p3.Y);
            var maxY = Max4(p0.Y, p1.Y, p2.Y, p3.Y);

            if (double.IsFinite(p0.X) && double.IsFinite(p0.Y) && double.IsFinite(minX) && double.IsFinite(maxX) && double.IsFinite(minY) && double.IsFinite(maxY))
            {
                var dx = p1.X - p0.X;
                var dy = p1.Y - p0.Y;
                var advance = Math.Sqrt((dx * dx) + (dy * dy));
                var (directionX, directionY) = advance > 1e-9 ? (dx / advance, dy / advance) : (1.0, 0.0);

                ctx.Letters.Add(new Letter(glyphText, p0.X, p0.Y, new PdfRectangle(minX, minY, maxX, maxY), text.FontResourceName ?? string.Empty, text.FontSize, text.RenderingMode, advance, directionX, directionY, ctx.CurrentMcid));
                letterCount++;
                if (letterCount > ctx.MaxLettersPerPage)
                {
                    throw new PlumePdfException("PLUME6021", $"Page text extraction produced more than {ctx.MaxLettersPerPage} letters; refusing to continue (a resource-limit guard against a hostile or pathological content stream).");
                }
            }

            text.Advance(widthEm, isWordSpaceCode);
            remaining = remaining[codeLength..];
        }
    }

    private static ExtractionFont? ResolveFont(PdfDictionary? resources, string? fontResourceName, WalkContext ctx)
    {
        if (resources is null || fontResourceName is null)
        {
            return null;
        }

        var fontsDict = ResolveDictionary(resources.TryGetValue(FontName, out var fv) ? fv : null, ctx.Objects);
        if (fontsDict is null || !fontsDict.TryGetValue(PdfName.Get(fontResourceName), out var entry))
        {
            return null;
        }

        if (entry is PdfReference reference)
        {
            return ctx.Objects[reference.Target] is PdfDictionary fontDict
                ? ctx.FontFactory.GetOrBuild(reference.Target, fontDict)
                : null;
        }

        return entry is PdfDictionary direct ? ctx.FontFactory.Build(direct) : null;
    }

    /// <summary>
    /// Reads and filter-decodes a page's <c>/Contents</c> — a single stream or an array of
    /// streams (either shape possibly behind an indirect reference — <see cref="PageContents.ResolveStreams"/>)
    /// concatenated with an interposed newline (§7.8.2's requirement so two adjacent
    /// streams' tokens never fuse) — skipping (and recording a diagnostic for) any individual
    /// stream that fails to decode rather than aborting the whole page. Shared with
    /// <c>Documents.PageRasterAdapter</c> (Phase 8 <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c>)
    /// so both extraction and rasterization resolve a page's content bytes
    /// exactly the same way — one reader, not two independently-maintained copies.
    /// </summary>
    internal static byte[] ReadContentBytes(PdfDictionary pageDictionary, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        if (!pageDictionary.TryGetValue(ContentsName, out var contents))
        {
            return [];
        }

        var streams = PageContents.ResolveStreams(contents, objects);
        if (streams.Count == 0)
        {
            // A /Contents entry that is PRESENT but yields nothing usable is a deviation worth
            // surfacing, not a page that legitimately has no content (that is the absent-key
            // return above) — this shape used to render/extract as a blank page
            // with zero diagnostics, which cost a corpus round-trip to even localize.
            Report("PLUME6083", "The page's /Contents entry resolved to no usable content streams (not a stream, not an array of streams); the page is treated as empty.", options, diagnostics);
            return [];
        }

        using var buffer = new MemoryStream();
        foreach (var stream in streams)
        {
            byte[] decoded;
            try
            {
                decoded = stream.GetDecodedBytes(options.Filters, options, r => objects[r]);
            }
            catch (PlumePdfException ex)
            {
                Report(ex.Code, $"A page content stream could not be decoded ({ex.Message}); its contribution was skipped.", options, diagnostics);
                continue;
            }

            buffer.Write(decoded);
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    private static PdfDictionary? ResolveDictionary(PdfObject? value, ObjectRegistry objects) => Resolve(value, objects) as PdfDictionary;

    private static PdfObject? Resolve(PdfObject? value, ObjectRegistry objects) => value switch
    {
        null => null,
        PdfReference reference => objects[reference.Target],
        _ => value,
    };

    private static PdfMatrix ReadMatrix(IReadOnlyList<PdfObject> operands) =>
        new(Num(operands, 0), Num(operands, 1), Num(operands, 2), Num(operands, 3), Num(operands, 4), Num(operands, 5));

    private static double Num(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber number ? number.Value : 0;

    private static byte[]? FirstStringBytes(IReadOnlyList<PdfObject> operands) => operands.Count > 0 ? StringBytes(operands[0]) : null;

    private static byte[]? StringBytes(PdfObject value) => value is PdfString s ? s.Bytes.ToArray() : null;

    private static double Min4(double a, double b, double c, double d) => Math.Min(Math.Min(a, b), Math.Min(c, d));

    private static double Max4(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

    private static void Report(string code, string message, WalkContext ctx) => Report(code, message, ctx.Options, ctx.Diagnostics);

    private static void Report(string code, string message, PdfOptions options, DiagnosticCollection diagnostics)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }

    private static (string GlyphName, int Unicode)[] BuildFallbackEncoding()
    {
        var table = new (string, int)[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = i is >= 0x20 and <= 0x7E ? (string.Empty, i) : (string.Empty, -1);
        }

        return table;
    }
}
