using PlumePdf.Fonts.Reading;
using PlumePdf.Objects;

namespace PlumePdf.Content;

/// <summary>
/// An axis-aligned rectangle in page space — the Content layer's own copy of the shape
/// <c>PlumePdf.Documents.PdfRectangle</c> carries, kept as a distinct type here because Content
/// must never depend on Documents (docs/architecture.md's layering rule: a lower layer never
/// references a higher one). <c>RedactionEngine</c> (Documents layer) converts to this at the
/// call boundary and never the other way around.
/// </summary>
internal readonly record struct EditorRect(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Whether this rectangle overlaps <paramref name="other"/> (open intervals — sharing only an edge does not count as intersecting).</summary>
    public bool IntersectsWith(EditorRect other) =>
        Left < other.Right && Right > other.Left && Bottom < other.Top && Top > other.Bottom;
}

/// <summary>One page's <see cref="ContentStreamEditor.RedactPage"/> outcome.</summary>
internal sealed class ContentRedactionOutcome
{
    /// <summary>The shared "nothing changed" result — a page with no intersecting content returns this exact instance.</summary>
    public static readonly ContentRedactionOutcome Unchanged = new(false, 0, 0, 0, 0);

    internal ContentRedactionOutcome(bool changed, int textOperatorsRemoved, int imagesRemoved, int inlineImagesRemoved, int inlineImagesSkipped)
    {
        Changed = changed;
        TextOperatorsRemoved = textOperatorsRemoved;
        ImagesRemoved = imagesRemoved;
        InlineImagesRemoved = inlineImagesRemoved;
        InlineImagesSkipped = inlineImagesSkipped;
    }

    /// <summary>Whether this page's <c>/Contents</c> was rewritten.</summary>
    public bool Changed { get; }

    /// <summary>How many text-showing operators were removed, on this page and every Form XObject reached from it.</summary>
    public int TextOperatorsRemoved { get; }

    /// <summary>How many image XObjects were removed in full, on this page and every Form XObject reached from it.</summary>
    public int ImagesRemoved { get; }

    /// <summary>How many inline images (<c>BI</c>…<c>EI</c>) were dropped from the rebuilt stream — because they intersected a target region, or because their raw byte span could not be determined and re-emitting them verbatim would risk splicing stale bytes (the fail-safe direction — see <see cref="ContentStreamEditor"/>'s remarks).</summary>
    public int InlineImagesRemoved { get; }

    /// <summary>How many inline images had genuinely indeterminate geometry (a non-finite CTM at the <c>BI</c>) and so could not be intersection-tested; they are re-emitted verbatim and surfaced here loudly rather than silently (see <see cref="ContentStreamEditor"/>'s remarks).</summary>
    public int InlineImagesSkipped { get; }
}

/// <summary>
/// Operator-level content-stream redaction: removes every text-showing
/// (<c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c>) operator whose painted glyphs intersect a target
/// region, and removes every image XObject whose painted area intersects one, leaving
/// surrounding graphics-state operators (<c>q</c>/<c>Q</c>/<c>cm</c>/color/clipping/...)
/// untouched (content-level removal, not merely object-graph unreachability — the
/// famous real-world redaction failure is text surviving under a drawn-over box). A Form
/// XObject reached via <c>Do</c> is recursed into with the accumulated CTM; when anything
/// inside it matched, the whole form is wiped to an empty stream in place rather than
/// partially edited (a v1.0 simplification — see the class remarks below) so the matched
/// content cannot survive through a Form XObject shared with another page/invocation.
/// </summary>
/// <remarks>
/// The v1.0 scope simplifications, all in the safe (over-redact, never under-redact)
/// direction, documented here and in <c>docs/cookbook/redact.md</c>:
/// <list type="bullet">
/// <item>Every image XObject that intersects a target region is removed in full, whether its
/// pixel data is codec-encoded (DCT/JPX/CCITT/JBIG2) or raw samples (Flate/LZW/RunLength) —
/// PlumePDF always performs the stronger whole-removal in v1.0, rather than sample-level
/// blanking for raw images. Sample-level partial blanking is 1.x. A caller that would rather
/// fail loudly than lose an image opts into a coded refusal instead
/// (<c>PdfRedactOptions.RefuseOnImageRemoval</c>, <c>PLUME6074</c>).</item>
/// <item>An inline image (<c>BI</c>…<c>ID</c>…<c>EI</c>) that intersects a target region is
/// dropped whole from the rebuilt stream (its <c>BI</c>…<c>EI</c> raw span, recorded by
/// <c>ContentStreamReader</c> at scan time, is simply not re-emitted) — never partially edited,
/// since inline image binary data is never materialized (Phase 3). An inline
/// image whose span could not be determined (a malformed terminator) is dropped too, whether or
/// not it intersects — emitting nothing is safe; splicing guessed raw bytes is how removed
/// content resurrects. Only an inline image whose <em>geometry</em> is indeterminate (a
/// non-finite CTM) survives, re-emitted verbatim and counted in
/// <see cref="ContentRedactionOutcome.InlineImagesSkipped"/> so the caller sees the gap loudly.</item>
/// </list>
/// Work across a page and every Form XObject reached from it is bounded <em>cumulatively</em>
/// by <c>PdfOptions.MaxContentStreamOperators</c> (<c>PLUME7018</c>) — the same guard shape
/// <c>TextExtractor</c> uses — because per-stream caps plus a nesting-depth cap alone still
/// admit fanout^depth total work from a small, shallow-looking form graph.
/// A whole-removed image XObject is replaced with a 1×1 opaque black <c>DeviceGray</c> image
/// via <c>ObjectRegistry.RegisterNew</c> shadowing its own reference — the original encoded
/// bytes are never written by a subsequent full-rewrite <c>Save</c> (the object-level GC
/// serializes the shadow value, not the original), which is also why a shared image reached
/// from more than one page is redacted everywhere it appears, not just the targeted page — the
/// conservative, privacy-preserving choice for identical bytes.
/// </remarks>
internal static class ContentStreamEditor
{
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName FontName = PdfName.Get("Font");
    private static readonly PdfName XObjectName = PdfName.Get("XObject");
    private static readonly PdfName SubtypeName = PdfName.Subtype;
    private static readonly PdfName MatrixName = PdfName.Get("Matrix");
    private static readonly PdfName TypeName = PdfName.Type;
    private static readonly PdfName WidthName = PdfName.Get("Width");
    private static readonly PdfName HeightName = PdfName.Get("Height");

    // Mirrors PlumePdf.Documents.TextExtractor's own FallbackFont exactly (that type is a
    // Documents-layer internal this file must not depend on — docs/architecture.md's layering
    // rule) so a text-showing operator with an unresolvable font is still measured (ASCII
    // passes through, everything else U+FFFD) rather than silently un-measurable, which would
    // make this walk under-count glyphs and risk missing a match.
    private static readonly ExtractionFont FallbackFont = new SimpleExtractionFont(
        "Unknown",
        BuildFallbackEncoding(),
        new Dictionary<int, double>(),
        missingWidth: 500,
        toUnicode: null,
        PdfOptions.Default,
        diagnostics: null);

    /// <summary>
    /// Redacts <paramref name="pageDictionary"/>'s content in place: when anything intersects
    /// <paramref name="regions"/>, consolidates the page's (possibly multi-stream) content into
    /// one freshly-registered stream with the matched operators removed, and rewrites
    /// <paramref name="pageDictionary"/>'s <c>/Contents</c> to reference it. Does nothing (and
    /// returns <see cref="ContentRedactionOutcome.Unchanged"/>) when <paramref name="regions"/>
    /// is empty, the page has no decodable content, or nothing on the page intersects any
    /// region.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME6074</c> — <paramref name="refuseOnImageRemoval"/> is set and a region intersects
    /// an image (XObject or inline) this editor would otherwise remove whole.
    /// <c>PLUME7018</c> — the page and its nested Form XObjects exceeded
    /// <c>PdfOptions.MaxContentStreamOperators</c> cumulatively.
    /// </exception>
    public static ContentRedactionOutcome RedactPage(
        PdfDictionary pageDictionary,
        IndirectReference pageReference,
        ObjectRegistry objects,
        PdfFilterRegistry filters,
        PdfOptions options,
        (double Llx, double Lly, double Urx, double Ury) mediaBox,
        int rotate,
        IReadOnlyList<EditorRect> regions,
        bool refuseOnImageRemoval = false)
    {
        ArgumentNullException.ThrowIfNull(pageDictionary);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(regions);

        if (regions.Count == 0)
        {
            return ContentRedactionOutcome.Unchanged;
        }

        var contentBytes = ReadContentBytes(pageDictionary, objects, filters, options);
        if (contentBytes.Length == 0)
        {
            return ContentRedactionOutcome.Unchanged;
        }

        var resources = ResolveDictionary(pageDictionary.TryGetValue(ResourcesName, out var r) ? r : null, objects);
        var normalization = NormalizationMatrix(mediaBox, rotate);
        var counters = new Counters();
        var fontFactory = new ExtractionFontFactory(objects, filters, options, diagnostics: null);

        var (newBytes, changed) = ProcessStream(contentBytes, resources, normalization, regions, objects, fontFactory, filters, options, refuseOnImageRemoval, depth: 0, counters);
        if (!changed)
        {
            return ContentRedactionOutcome.Unchanged;
        }

        var newReference = objects.AllocateNumber();
        objects.RegisterNew(newReference, new PdfStream(new PdfDictionary(), newBytes));
        pageDictionary.Set(ContentsName, new PdfReference(newReference));
        objects.MarkDirty(pageReference);

        return new ContentRedactionOutcome(true, counters.TextOperatorsRemoved, counters.ImagesRemoved, counters.InlineImagesRemoved, counters.InlineImagesSkipped);
    }

    private sealed class Counters
    {
        public int TextOperatorsRemoved;
        public int ImagesRemoved;
        public int InlineImagesRemoved;
        public int InlineImagesSkipped;

        // Cumulative across the page and every nested Form XObject (never reset per stream) —
        // the TextExtractor pattern: bounding total work done, not merely nesting depth, so a
        // fanout^depth form graph is refused early (PLUME7018).
        public long TotalOperators;
    }

    private static (byte[] NewBytes, bool Changed) ProcessStream(
        byte[] content,
        PdfDictionary? resources,
        PdfMatrix initialCtm,
        IReadOnlyList<EditorRect> regions,
        ObjectRegistry objects,
        ExtractionFontFactory fontFactory,
        PdfFilterRegistry filters,
        PdfOptions options,
        bool refuseOnImageRemoval,
        int depth,
        Counters counters)
    {
        var operations = ContentStreamReader.Read(content, options, diagnostics: null, options.MaxContentStreamOperators);

        // Cumulative across the whole page, including every nested Form XObject's own content
        // stream — not reset per call — so a document that fans a small, shallow-looking form
        // graph out into an enormous total number of Do invocations is bounded by total work
        // done, not merely by nesting depth (see the class remarks).
        counters.TotalOperators += operations.Count;
        if (counters.TotalOperators > options.MaxContentStreamOperators)
        {
            throw new PlumePdfException("PLUME7018", $"Redaction content-stream editing processed more than {options.MaxContentStreamOperators} operators across the page and its nested Form XObjects (PdfOptions.MaxContentStreamOperators); refusing to continue (a resource-limit guard against a hostile or pathological XObject graph).");
        }

        var graphics = new GraphicsStateStack();
        graphics.Concatenate(initialCtm);
        var text = new TextState();
        var paramStack = new Stack<(double Tc, double Tw, double Th, double Tl, string? Font, double Tfs, double Ts, int Tr)>();

        var keep = new bool[operations.Count];
        Array.Fill(keep, true);
        var changedHere = false;

        ExtractionFont? currentFont = null;
        string? currentFontResourceName = null;

        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            switch (op.Operator)
            {
                case "q":
                    graphics.Save();
                    paramStack.Push((text.CharacterSpacing, text.WordSpacing, text.HorizontalScaling, text.Leading, text.FontResourceName, text.FontSize, text.Rise, text.RenderingMode));
                    break;

                case "Q":
                    graphics.Restore();
                    if (paramStack.TryPop(out var snap))
                    {
                        (text.CharacterSpacing, text.WordSpacing, text.HorizontalScaling, text.Leading, text.FontResourceName, text.FontSize, text.Rise, text.RenderingMode) = snap;
                        currentFontResourceName = null;
                    }

                    break;

                case "cm":
                    var cm = ReadMatrix(op.Operands);
                    if (cm.IsFinite)
                    {
                        graphics.Concatenate(cm);
                    }

                    break;

                case "BT":
                    text.BeginTextObject();
                    break;

                case "ET":
                    text.EndTextObject();
                    break;

                case "Tf":
                    text.FontResourceName = op.Operands.Count > 0 && op.Operands[0] is PdfName fn ? fn.Value : null;
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
                    var tm = ReadMatrix(op.Operands);
                    if (tm.IsFinite)
                    {
                        text.SetTextMatrix(tm);
                    }

                    break;

                case "T*":
                    text.MoveToNextLineWithLeading();
                    break;

                case "Tj":
                    if (ShowTextIntersects(FirstStringBytes(op.Operands), graphics, text, resources, objects, fontFactory, regions, ref currentFont, ref currentFontResourceName))
                    {
                        keep[i] = false;
                        counters.TextOperatorsRemoved++;
                        changedHere = true;
                    }

                    break;

                case "'":
                    text.MoveToNextLineWithLeading();
                    if (ShowTextIntersects(FirstStringBytes(op.Operands), graphics, text, resources, objects, fontFactory, regions, ref currentFont, ref currentFontResourceName))
                    {
                        keep[i] = false;
                        counters.TextOperatorsRemoved++;
                        changedHere = true;
                    }

                    break;

                case "\"":
                    text.WordSpacing = Num(op.Operands, 0);
                    text.CharacterSpacing = Num(op.Operands, 1);
                    text.MoveToNextLineWithLeading();
                    if (ShowTextIntersects(op.Operands.Count > 2 ? StringBytes(op.Operands[2]) : null, graphics, text, resources, objects, fontFactory, regions, ref currentFont, ref currentFontResourceName))
                    {
                        keep[i] = false;
                        counters.TextOperatorsRemoved++;
                        changedHere = true;
                    }

                    break;

                case "TJ":
                    if (op.Operands.Count > 0 && op.Operands[0] is PdfArray array)
                    {
                        var any = false;
                        foreach (var element in array)
                        {
                            if (element is PdfNumber adjustment)
                            {
                                text.ApplyPositioningAdjustment(adjustment.Value);
                            }
                            else
                            {
                                var bytes = StringBytes(element);
                                if (bytes is not null && ShowTextIntersects(bytes, graphics, text, resources, objects, fontFactory, regions, ref currentFont, ref currentFontResourceName))
                                {
                                    any = true;
                                }
                            }
                        }

                        if (any)
                        {
                            keep[i] = false;
                            counters.TextOperatorsRemoved++;
                            changedHere = true;
                        }
                    }

                    break;

                case "Do":
                    if (HandleDo(op, resources, graphics, objects, fontFactory, filters, options, refuseOnImageRemoval, regions, depth, counters))
                    {
                        keep[i] = false;
                        changedHere = true;
                    }

                    break;

                case "BI":
                    if (op.InlineImageSpan is null)
                    {
                        // No safe raw span exists (malformed BI…EI run): re-emitting by guesswork
                        // is how removed content resurrects — drop the image instead (fail safe;
                        // see the class remarks) and count it loudly.
                        keep[i] = false;
                        counters.InlineImagesRemoved++;
                        changedHere = true;
                        break;
                    }

                    var painted = PaintedRect(graphics.CurrentTransform);
                    if (!IsFinite(painted))
                    {
                        // Genuinely indeterminate geometry: intersection cannot be decided, so
                        // the image survives verbatim — surfaced loudly via the skip counter.
                        counters.InlineImagesSkipped++;
                        break;
                    }

                    if (Intersects(painted, regions))
                    {
                        if (refuseOnImageRemoval)
                        {
                            throw new PlumePdfException("PLUME6074", $"A redaction region intersects the inline image at content-stream offset {op.Offset} (painted area {Describe(painted)}), and PdfRedactOptions.RefuseOnImageRemoval is set: PlumePDF removes an intersecting image whole rather than best-effort blanking pixels, so this call refuses instead of losing the image. Redact without the switch to remove it, or adjust the region.");
                        }

                        keep[i] = false;
                        counters.InlineImagesRemoved++;
                        changedHere = true;
                    }

                    break;
            }
        }

        return (Rebuild(content, operations, keep), changedHere);
    }

    private static bool HandleDo(
        ContentOperation op,
        PdfDictionary? resources,
        GraphicsStateStack graphics,
        ObjectRegistry objects,
        ExtractionFontFactory fontFactory,
        PdfFilterRegistry filters,
        PdfOptions options,
        bool refuseOnImageRemoval,
        IReadOnlyList<EditorRect> regions,
        int depth,
        Counters counters)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not PdfName xobjectName || resources is null)
        {
            return false;
        }

        var xobjectDict = ResolveDictionary(resources.TryGetValue(XObjectName, out var xo) ? xo : null, objects);
        if (xobjectDict is null || !xobjectDict.TryGetValue(xobjectName, out var entry) || entry is not PdfReference xobjectRef)
        {
            return false;
        }

        if (objects[xobjectRef.Target] is not PdfStream stream)
        {
            return false;
        }

        var subtype = stream.Dictionary.TryGetValue(SubtypeName, out var st) ? (st as PdfName)?.Value : null;

        if (subtype == "Image")
        {
            var paintedRect = PaintedRect(graphics.CurrentTransform);
            if (!Intersects(paintedRect, regions))
            {
                return false;
            }

            if (refuseOnImageRemoval)
            {
                throw new PlumePdfException("PLUME6074", $"A redaction region intersects the image XObject /{xobjectName.Value} (object {xobjectRef.Target.Number}, painted area {Describe(paintedRect)}), and PdfRedactOptions.RefuseOnImageRemoval is set: PlumePDF removes an intersecting image whole rather than best-effort blanking pixels, so this call refuses instead of losing the image. Redact without the switch to remove it, or adjust the region.");
            }

            objects.RegisterNew(xobjectRef.Target, BuildBlankImageStream());
            counters.ImagesRemoved++;
            return true;
        }

        if (subtype != "Form" || depth >= options.MaxXObjectNestingDepth)
        {
            return false;
        }

        byte[] formBytes;
        try
        {
            formBytes = stream.GetDecodedBytes(filters, options, r => objects[r]);
        }
        catch (PlumePdfException)
        {
            return false;
        }

        var formResources = ResolveDictionary(stream.Dictionary.TryGetValue(ResourcesName, out var fr) ? fr : null, objects) ?? resources;
        var formCtm = graphics.CurrentTransform;
        if (stream.Dictionary.TryGetValue(MatrixName, out var matrixValue) && matrixValue is PdfArray { Count: 6 } matrixArray)
        {
            var m = ReadMatrix([.. matrixArray]);
            if (m.IsFinite)
            {
                formCtm = PdfMatrix.Multiply(m, formCtm);
            }
        }

        var (_, formChanged) = ProcessStream(formBytes, formResources, formCtm, regions, objects, fontFactory, filters, options, refuseOnImageRemoval, depth + 1, counters);
        if (!formChanged)
        {
            return false;
        }

        // Coarse whole-form wipe (this file's remarks): a partial in-form edit would need to
        // shadow the form's reference with the recursive call's own rebuilt bytes, but that
        // would leave any *other* invocation of the same shared form on this page or another
        // page seeing only the redacted remainder — fine for what was matched, but the simplest
        // implementation that cannot under-redact is to drop the form's content entirely once
        // anything inside it matched.
        var emptyDictionary = new PdfDictionary();
        foreach (var (key, value) in stream.Dictionary)
        {
            if (key.Value is "Type" or "Subtype" or "BBox" or "Matrix" or "Group")
            {
                emptyDictionary.Set(key, value);
            }
        }

        objects.RegisterNew(xobjectRef.Target, new PdfStream(emptyDictionary, Array.Empty<byte>()));
        return true;
    }

    private static bool ShowTextIntersects(
        byte[]? bytes,
        GraphicsStateStack graphics,
        TextState text,
        PdfDictionary? resources,
        ObjectRegistry objects,
        ExtractionFontFactory fontFactory,
        IReadOnlyList<EditorRect> regions,
        ref ExtractionFont? currentFont,
        ref string? currentFontResourceName)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        if (currentFont is null || currentFontResourceName != text.FontResourceName)
        {
            currentFont = ResolveFont(resources, text.FontResourceName, objects, fontFactory);
            currentFontResourceName = text.FontResourceName;
        }

        var font = currentFont ?? FallbackFont;
        ReadOnlySpan<byte> remaining = bytes;
        var intersects = false;

        while (remaining.Length > 0)
        {
            font.DecodeNext(remaining, out var codeLength, out _, out var widthGlyphSpace);
            if (codeLength <= 0)
            {
                break;
            }

            var isWordSpaceCode = codeLength == 1 && remaining[0] == 32;
            var widthEm = widthGlyphSpace / 1000.0;

            var origin = text.ComputeRenderingMatrix(graphics.CurrentTransform);
            var p0 = origin.Transform(0, 0);
            var p1 = origin.Transform(widthEm, 0);
            var p2 = origin.Transform(widthEm, 1);
            var p3 = origin.Transform(0, 1);

            if (double.IsFinite(p0.X) && double.IsFinite(p0.Y) && double.IsFinite(p1.X) && double.IsFinite(p1.Y)
                && double.IsFinite(p2.X) && double.IsFinite(p2.Y) && double.IsFinite(p3.X) && double.IsFinite(p3.Y))
            {
                var glyphRect = new EditorRect(Min4(p0.X, p1.X, p2.X, p3.X), Min4(p0.Y, p1.Y, p2.Y, p3.Y), Max4(p0.X, p1.X, p2.X, p3.X), Max4(p0.Y, p1.Y, p2.Y, p3.Y));
                if (Intersects(glyphRect, regions))
                {
                    intersects = true;
                }
            }

            text.Advance(widthEm, isWordSpaceCode);
            remaining = remaining[codeLength..];
        }

        return intersects;
    }

    private static byte[] Rebuild(byte[] originalContent, IReadOnlyList<ContentOperation> operations, bool[] keep)
    {
        using var output = new MemoryStream();

        for (var i = 0; i < operations.Count; i++)
        {
            if (!keep[i])
            {
                continue;
            }

            var op = operations[i];
            if (op.Operator == "BI")
            {
                // A kept BI always carries the reader-recorded span (a span-less one was
                // dropped in ProcessStream) — but never splice raw bytes without one.
                if (op.InlineImageSpan is (int start, int end))
                {
                    output.Write(originalContent, start, end - start);
                    output.WriteByte((byte)'\n');
                }

                continue;
            }

            foreach (var operand in op.Operands)
            {
                ObjectSerializer.WriteValue(output, operand);
                output.WriteByte((byte)' ');
            }

            var opBytes = System.Text.Encoding.ASCII.GetBytes(op.Operator);
            output.Write(opBytes);
            output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }

    private static PdfStream BuildBlankImageStream()
    {
        var dict = new PdfDictionary();
        dict.Set(TypeName, PdfName.Get("XObject"));
        dict.Set(SubtypeName, PdfName.Get("Image"));
        dict.Set(WidthName, PdfNumber.Get(1));
        dict.Set(HeightName, PdfNumber.Get(1));
        dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
        dict.Set(PdfName.Get("BitsPerComponent"), PdfNumber.Get(8));
        return new PdfStream(dict, new byte[] { 0 });
    }

    private static EditorRect PaintedRect(PdfMatrix ctm)
    {
        var p0 = ctm.Transform(0, 0);
        var p1 = ctm.Transform(1, 0);
        var p2 = ctm.Transform(1, 1);
        var p3 = ctm.Transform(0, 1);
        return new EditorRect(Min4(p0.X, p1.X, p2.X, p3.X), Min4(p0.Y, p1.Y, p2.Y, p3.Y), Max4(p0.X, p1.X, p2.X, p3.X), Max4(p0.Y, p1.Y, p2.Y, p3.Y));
    }

    private static bool IsFinite(EditorRect rect) =>
        double.IsFinite(rect.Left) && double.IsFinite(rect.Bottom) && double.IsFinite(rect.Right) && double.IsFinite(rect.Top);

    private static string Describe(EditorRect rect) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({rect.Left:0.##}, {rect.Bottom:0.##})-({rect.Right:0.##}, {rect.Top:0.##})");

    private static bool Intersects(EditorRect rect, IReadOnlyList<EditorRect> regions)
    {
        foreach (var region in regions)
        {
            if (rect.IntersectsWith(region))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] ReadContentBytes(PdfDictionary pageDictionary, ObjectRegistry objects, PdfFilterRegistry filters, PdfOptions options)
    {
        if (!pageDictionary.TryGetValue(ContentsName, out var contents))
        {
            return [];
        }

        var streams = PageContents.ResolveStreams(contents, objects);
        if (streams.Count == 0)
        {
            return [];
        }

        using var buffer = new MemoryStream();
        foreach (var stream in streams)
        {
            byte[] decoded;
            try
            {
                decoded = stream.GetDecodedBytes(filters, options, r => objects[r]);
            }
            catch (PlumePdfException)
            {
                continue;
            }

            buffer.Write(decoded);
            buffer.WriteByte((byte)'\n');
        }

        return buffer.ToArray();
    }

    private static ExtractionFont? ResolveFont(PdfDictionary? resources, string? fontResourceName, ObjectRegistry objects, ExtractionFontFactory fontFactory)
    {
        if (resources is null || fontResourceName is null)
        {
            return null;
        }

        var fontsDict = ResolveDictionary(resources.TryGetValue(FontName, out var fv) ? fv : null, objects);
        if (fontsDict is null || !fontsDict.TryGetValue(PdfName.Get(fontResourceName), out var entry))
        {
            return null;
        }

        if (entry is PdfReference reference)
        {
            return objects[reference.Target] is PdfDictionary fontDict ? fontFactory.GetOrBuild(reference.Target, fontDict) : null;
        }

        return entry is PdfDictionary direct ? fontFactory.Build(direct) : null;
    }

    private static PdfObject? Resolve(PdfObject? value, ObjectRegistry objects) => value switch
    {
        null => null,
        PdfReference reference => objects[reference.Target],
        _ => value,
    };

    private static PdfDictionary? ResolveDictionary(PdfObject? value, ObjectRegistry objects) => Resolve(value, objects) as PdfDictionary;

    private static PdfMatrix NormalizationMatrix((double Llx, double Lly, double Urx, double Ury) mediaBox, int rotate)
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

    private static PdfMatrix ReadMatrix(IReadOnlyList<PdfObject> operands) =>
        new(Num(operands, 0), Num(operands, 1), Num(operands, 2), Num(operands, 3), Num(operands, 4), Num(operands, 5));

    private static double Num(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber number ? number.Value : 0;

    private static byte[]? FirstStringBytes(IReadOnlyList<PdfObject> operands) => operands.Count > 0 ? StringBytes(operands[0]) : null;

    private static byte[]? StringBytes(PdfObject value) => value is PdfString s ? s.Bytes.ToArray() : null;

    private static double Min4(double a, double b, double c, double d) => Math.Min(Math.Min(a, b), Math.Min(c, d));

    private static double Max4(double a, double b, double c, double d) => Math.Max(Math.Max(a, b), Math.Max(c, d));

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
