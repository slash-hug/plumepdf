using PlumePdf.Content;
using PlumePdf.Objects;
using PlumePdf.Raster.Annotations;

namespace PlumePdf.Documents.Compression;

/// <summary>
/// Walks every retained page's content (forms, annotation appearances, soft-mask groups,
/// patterns and Type 3 glyphs included) with the graphics state, recording where each image
/// XObject is placed and at what effective DPI. Reads dictionaries only, never image payloads.
/// </summary>
/// <remarks>
/// <para>
/// A placement's DPI is per image axis: the image's pixel count along that axis over the
/// physical length of the edge the CTM maps it onto (the CTM row's magnitude, in points, times
/// the page's <c>/UserUnit</c>). Rotation and shear therefore change nothing but the measured
/// edge lengths. Each image keeps the lowest DPI per axis across all of its placements, so a
/// downsample sized from it never loses detail any placement shows.
/// </para>
/// <para>
/// Content that can't be measured is marked rather than guessed: an image painted inside a
/// tiling pattern is drawn in pattern space at a size the scan doesn't follow, and one inside a
/// Type 3 glyph at the text size. A walk that stops early (a form cycle, the nesting or operator
/// caps, a content stream it can't decode, malformed content refused under
/// <see cref="PdfOptions.Strict"/>) may have missed a larger placement, so every image the
/// page's resources can reach is then marked unmeasured too. A degenerate placement (a
/// zero-area or non-finite CTM) is unmeasured as well. An image a page's resources reach but
/// nothing draws is recorded as not placed, so every image a retained page lists has a fate in
/// the plan.
/// </para>
/// <para>
/// Only stream dictionaries are taken from the registry (<see cref="IObjectSource.ResolveForDiscovery"/>),
/// and content payloads (pages, forms, patterns, glyphs, appearances) are read once each through
/// <see cref="IObjectSource.ReadPayloadOnce"/>, so the scan leaves no stream in the object cache.
/// Tiling patterns and Type 3 fonts are walked once per scan, since nothing in them is measured;
/// an inline image inside one is still recorded once for every page that uses it.
/// </para>
/// </remarks>
internal static class PlacementScanner
{
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName XObjectName = PdfName.Get("XObject");
    private static readonly PdfName ExtGStateName = PdfName.Get("ExtGState");
    private static readonly PdfName PatternName = PdfName.Get("Pattern");
    private static readonly PdfName FontName = PdfName.Get("Font");
    private static readonly PdfName MatrixName = PdfName.Get("Matrix");
    private static readonly PdfName FormName = PdfName.Get("Form");
    private static readonly PdfName GroupName = PdfName.Get("G");
    private static readonly PdfName PatternTypeName = PdfName.Get("PatternType");
    private static readonly PdfName Type3Name = PdfName.Get("Type3");
    private static readonly PdfName CharProcsName = PdfName.Get("CharProcs");
    private static readonly PdfName UserUnitName = PdfName.Get("UserUnit");

    // An annotation's normal, down (pressed) and rollover (hover) appearances.
    private static readonly PdfName[] AppearanceKinds = [PdfName.N, PdfName.Get("D"), PdfName.Get("R")];

    /// <summary>Where a piece of content is drawn, which decides whether its placements can be measured.</summary>
    private enum WalkMode
    {
        Measured = 0,
        Tiling = 1,
        Type3 = 2,
    }

    /// <summary>Scans <paramref name="document"/>'s retained pages.</summary>
    /// <param name="document">The document to scan.</param>
    /// <param name="options">The effective save options.</param>
    /// <param name="cancellationToken">Checked before each page.</param>
    /// <param name="pageScanned">Called with the number of pages scanned so far after each page, or null.</param>
    public static PlacementScan Scan(PdfDocument document, PdfOptions options, CancellationToken cancellationToken, Action<int>? pageScanned = null)
    {
        var scan = new ScanState(document.Objects, options);
        for (var pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanPage(scan, document.Pages[pageIndex].Dictionary, pageIndex);
            pageScanned?.Invoke(pageIndex + 1);
        }

        var images = new Dictionary<int, ImagePlacementSummary>();
        foreach (var (number, placements) in scan.Images.OrderBy(static i => i.Key))
        {
            images[number] = placements.Summarize();
        }

        return new PlacementScan(images, scan.InlineImages, scan.Diagnostics);
    }

    private static void ScanPage(ScanState scan, PdfDictionary page, int pageIndex)
    {
        var resources = scan.ResolveDictionary(page.TryGetValue(ResourcesName, out var r) ? r : null);
        var userUnit = scan.Resolve(page.TryGetValue(UserUnitName, out var u) ? u : null) is PdfNumber { Value: > 0 } unit && double.IsFinite(unit.Value)
            ? unit.Value
            : 1.0;
        var walk = new PageWalk(scan, pageIndex, userUnit);

        try
        {
            var content = ReadPageContent(walk, page.TryGetValue(ContentsName, out var c) ? c : null);
            Walk(walk, content, resources, PdfMatrix.Identity, WalkMode.Measured, depth: 0);
            WalkAnnotations(walk, page, resources);
        }
        catch (PlumePdfException ex)
        {
            walk.Stop(ex.Code, $"The placement scan stopped reading page {pageIndex}'s content ({ex.Message}); its images are treated as unmeasured.");
        }

        if (walk.Truncated)
        {
            // The walk may have missed a placement, so nothing this page can reach is measured.
            VisitReachableImages(scan, page, resources, new Reach(), number => scan.Placements(number).AddUnmeasured(pageIndex));
            foreach (var number in walk.ImagesMet)
            {
                scan.Placements(number).AddUnmeasured(pageIndex);
            }
        }

        // An image the page can reach but nothing draws is still listed, so the plan accounts
        // for it; a placement met on any page outranks this.
        VisitReachableImages(scan, page, resources, scan.Listed, number => scan.Placements(number).AddListed(pageIndex));
    }

    // The page's /Contents, read uncached: each content stream's payload is read for this pass
    // and dropped, then the shared shape rules pick out the streams in order.
    private static byte[] ReadPageContent(PageWalk walk, PdfObject? contents)
    {
        var scan = walk.Scan;
        if (contents is null)
        {
            return [];
        }

        var resolved = contents;
        if (contents is PdfReference reference)
        {
            resolved = scan.Discover(reference.Target, out var isStream);
            if (isStream)
            {
                resolved = scan.Source.ReadPayloadOnce(reference.Target);
            }
        }

        if (resolved is PdfArray array)
        {
            var streams = new PdfArray();
            foreach (var item in array)
            {
                if (item is not PdfReference itemReference)
                {
                    continue;
                }

                scan.Discover(itemReference.Target, out var itemIsStream);
                if (itemIsStream && scan.Source.ReadPayloadOnce(itemReference.Target) is { } stream)
                {
                    streams.Add(stream);
                }
            }

            resolved = streams;
        }

        using var buffer = new MemoryStream();
        foreach (var stream in PageContents.ResolveStreams(resolved, scan.Objects))
        {
            if (walk.TryDecode(stream, "A page content stream") is { } decoded)
            {
                buffer.Write(decoded);
                buffer.WriteByte((byte)'\n');
            }
        }

        return buffer.ToArray();
    }

    private static void Walk(PageWalk walk, byte[] content, PdfDictionary? resources, PdfMatrix ctm, WalkMode mode, int depth)
    {
        var scan = walk.Scan;
        // The content's own lenient deviations are the extraction and rendering surfaces' to
        // report; under Strict they throw, and the page's walk stops.
        var operations = ContentStreamReader.Read(content, scan.Options, diagnostics: null, scan.Options.MaxContentStreamOperators);
        if (!walk.Spend(operations.Count))
        {
            return;
        }

        var graphics = new GraphicsStateStack();
        graphics.Concatenate(ctm);
        foreach (var op in operations)
        {
            if (walk.Stopped)
            {
                return;
            }

            switch (op.Operator)
            {
                case "q":
                    graphics.Save();
                    break;

                case "Q":
                    graphics.Restore(); // An unmatched Q is ignored, as every reader does.
                    break;

                case "cm" when op.Operands.Count == 6:
                    if (TryReadMatrix(op.Operands, out var matrix))
                    {
                        graphics.Concatenate(matrix);
                    }

                    break;

                case "Do" when op.Operands.Count > 0 && op.Operands[0] is PdfName name:
                    HandleDo(walk, name, resources, graphics.CurrentTransform, mode, depth);
                    break;

                case "BI":
                    scan.InlineImages.Add(new InlineImageUse(
                        walk.PageIndex,
                        op.Operands is [PdfDictionary parameters] ? ImageEncodings.OfInlineImage(parameters) : ImageEncoding.Uncompressed));
                    break;

                case "gs" when op.Operands.Count > 0 && op.Operands[0] is PdfName stateName:
                    HandleSoftMaskGroup(walk, stateName, resources, graphics.CurrentTransform, mode, depth);
                    break;

                case "scn" or "SCN" when op.Operands.Count > 0 && op.Operands[^1] is PdfName patternName:
                    HandlePattern(walk, patternName, resources, mode, depth);
                    break;

                case "Tf" when op.Operands.Count > 0 && op.Operands[0] is PdfName fontName:
                    HandleType3Font(walk, fontName, resources, depth);
                    break;
            }
        }
    }

    private static void HandleDo(PageWalk walk, PdfName name, PdfDictionary? resources, PdfMatrix ctm, WalkMode mode, int depth)
    {
        var scan = walk.Scan;
        if (scan.LookupResource(resources, XObjectName, name) is not PdfReference reference)
        {
            return;
        }

        var number = reference.Target.Number;
        var info = scan.XObject(reference.Target);
        if (info.Kind == XObjectKind.Image)
        {
            walk.ImagesMet.Add(number);
            var placements = scan.Placements(number);
            if (mode != WalkMode.Measured)
            {
                placements.AddUnmeasured(walk.PageIndex, tiling: mode == WalkMode.Tiling);
            }
            else if (TryMeasure(info.Width, info.Height, ctm, walk.UserUnit, out var dpiX, out var dpiY))
            {
                placements.AddMeasured(walk.PageIndex, dpiX, dpiY);
            }
            else
            {
                placements.AddUnmeasured(walk.PageIndex);
            }

            return;
        }

        // As every reader does, a Do draws only an XObject typed /Form (or /Image).
        if (info.Kind == XObjectKind.Form)
        {
            WalkForm(walk, reference.Target, ctm, resources, mode, depth, applyMatrix: true);
        }
    }

    // A form-like stream (a form XObject, a soft-mask group, a pattern, a glyph procedure):
    // its own /Matrix composes onto the CTM when it has one, its own /Resources replace the
    // caller's when present, and its content is read uncached.
    private static void WalkForm(PageWalk walk, IndirectReference reference, PdfMatrix ctm, PdfDictionary? inheritedResources, WalkMode mode, int depth, bool applyMatrix)
    {
        var scan = walk.Scan;
        if (depth >= scan.Options.MaxXObjectNestingDepth)
        {
            walk.Truncate("PLUME6025", $"Form XObject nesting during the compression placement scan exceeded the configured depth limit ({scan.Options.MaxXObjectNestingDepth}) on page {walk.PageIndex}; the page's images are treated as unmeasured.", reference);
            return;
        }

        if (!walk.Path.Add(reference.Number))
        {
            walk.Truncate("PLUME6025", $"Object {reference} draws itself (directly or through other forms) during the compression placement scan on page {walk.PageIndex}; the cycle is not followed and the page's images are treated as unmeasured.", reference);
            return;
        }

        try
        {
            if (scan.Source.ReadPayloadOnce(reference) is not { } stream)
            {
                return;
            }

            var dictionary = stream.Dictionary;
            var formCtm = ctm;
            if (applyMatrix && scan.Resolve(dictionary.TryGetValue(MatrixName, out var m) ? m : null) is PdfArray { Count: 6 } matrixArray && TryReadMatrix(matrixArray, out var matrix))
            {
                formCtm = PdfMatrix.Multiply(matrix, ctm);
            }

            var formResources = scan.ResolveDictionary(dictionary.TryGetValue(ResourcesName, out var r) ? r : null) ?? inheritedResources;
            if (walk.TryDecode(stream, $"Object {reference}") is { } content)
            {
                Walk(walk, content, formResources, formCtm, mode, depth + 1);
            }
        }
        finally
        {
            walk.Path.Remove(reference.Number);
        }
    }

    // gs with an /SMask: the mask's /G group is drawn with the CTM in effect at the gs, so its
    // images are placed like a form's.
    private static void HandleSoftMaskGroup(PageWalk walk, PdfName name, PdfDictionary? resources, PdfMatrix ctm, WalkMode mode, int depth)
    {
        var scan = walk.Scan;
        if (scan.ResolveDictionary(scan.LookupResource(resources, ExtGStateName, name)) is not { } state
            || scan.ResolveDictionary(state.TryGetValue(PdfName.SMask, out var s) ? s : null) is not { } softMask
            || !softMask.TryGetValue(GroupName, out var group)
            || group is not PdfReference groupReference)
        {
            return;
        }

        if (scan.XObject(groupReference.Target).IsFormLike)
        {
            WalkForm(walk, groupReference.Target, ctm, resources, mode, depth, applyMatrix: true);
        }
    }

    // scn/SCN naming a tiling pattern: its cell content is drawn in pattern space, which the
    // scan doesn't measure, so it is walked once per scan to mark its images.
    private static void HandlePattern(PageWalk walk, PdfName name, PdfDictionary? resources, WalkMode mode, int depth)
    {
        var scan = walk.Scan;
        if (scan.LookupResource(resources, PatternName, name) is not PdfReference reference)
        {
            return;
        }

        var dictionary = scan.Discover(reference.Target, out var isStream);
        var patternDictionary = dictionary as PdfDictionary ?? (dictionary as PdfStream)?.Dictionary;
        if (!isStream
            || patternDictionary is null
            || scan.Resolve(patternDictionary.TryGetValue(PatternTypeName, out var t) ? t : null) is not PdfNumber { Value: 1 })
        {
            return; // A shading pattern paints no XObjects.
        }

        var patternMode = mode == WalkMode.Type3 ? WalkMode.Type3 : WalkMode.Tiling;
        var key = (reference.Target.Number, patternMode);
        if (!scan.WalkedOnce.Add(key))
        {
            ReplayInlineImages(walk, key);
            return;
        }

        var before = scan.InlineImages.Count;
        WalkForm(walk, reference.Target, PdfMatrix.Identity, resources, patternMode, depth, applyMatrix: false);
        RecordInlineImages(walk, key, before);
    }

    // Tf selecting a Type 3 font: every glyph procedure is walked once per scan, since a glyph
    // is drawn at the text size, which the scan doesn't follow.
    private static void HandleType3Font(PageWalk walk, PdfName name, PdfDictionary? resources, int depth)
    {
        var scan = walk.Scan;
        var entry = scan.LookupResource(resources, FontName, name);
        if (scan.ResolveDictionary(entry) is not { } font
            || scan.Resolve(font.TryGetValue(PdfName.Subtype, out var subtype) ? subtype : null) is not PdfName fontSubtype
            || !fontSubtype.Equals(Type3Name))
        {
            return;
        }

        object identity = entry is PdfReference fontReference ? fontReference.Target.Number : font;
        if (!scan.WalkedFonts.Add(identity))
        {
            ReplayInlineImages(walk, identity);
            return;
        }

        if (scan.ResolveDictionary(font.TryGetValue(CharProcsName, out var procs) ? procs : null) is not { } charProcs)
        {
            return;
        }

        var before = scan.InlineImages.Count;
        var fontResources = scan.ResolveDictionary(font.TryGetValue(ResourcesName, out var r) ? r : null) ?? resources;
        foreach (var (_, procedure) in charProcs)
        {
            if (walk.Stopped)
            {
                break;
            }

            if (procedure is PdfReference procedureReference)
            {
                WalkForm(walk, procedureReference.Target, PdfMatrix.Identity, fontResources, WalkMode.Type3, depth, applyMatrix: false);
            }
        }

        RecordInlineImages(walk, identity, before);
    }

    // Content walked once per scan still holds its inline images on every page that uses it:
    // the first walk records where its entries are, and each later page records them again,
    // with its own page index, once per page however often the page uses it.
    private static void RecordInlineImages(PageWalk walk, object key, int before)
    {
        walk.Scan.OnceWalkedInlineImages[key] = (before, walk.Scan.InlineImages.Count - before);
        walk.Scan.OnceWalkedPages.Add((key, walk.PageIndex));
    }

    private static void ReplayInlineImages(PageWalk walk, object key)
    {
        // A use met while its first walk is still under way (it draws itself) has no count yet.
        var scan = walk.Scan;
        if (scan.OnceWalkedInlineImages.TryGetValue(key, out var recorded) && scan.OnceWalkedPages.Add((key, walk.PageIndex)))
        {
            for (var i = 0; i < recorded.Count; i++)
            {
                scan.InlineImages.Add(scan.InlineImages[recorded.Start + i] with { PageIndex = walk.PageIndex });
            }
        }
    }

    // Every annotation's normal appearance, every state of it, mapped onto the annotation's
    // /Rect. Visibility flags are not consulted: an image any appearance can show is placed.
    private static void WalkAnnotations(PageWalk walk, PdfDictionary page, PdfDictionary? pageResources)
    {
        var scan = walk.Scan;
        if (scan.Resolve(page.TryGetValue(PdfName.Annots, out var a) ? a : null) is not PdfArray annotations)
        {
            return;
        }

        foreach (var entry in annotations)
        {
            if (walk.Stopped)
            {
                return;
            }

            if (scan.ResolveDictionary(entry) is not { } annotation
                || scan.ResolveDictionary(annotation.TryGetValue(PdfName.AP, out var ap) ? ap : null) is not { } appearances)
            {
                continue;
            }

            // The pressed and hover appearances are drawn too, so their images are placed as well.
            foreach (var kind in AppearanceKinds)
            {
                if (!appearances.TryGetValue(kind, out var appearance))
                {
                    continue;
                }

                // A reference is either the appearance stream itself or an indirect states dictionary.
                if (appearance is PdfReference single && scan.XObject(single.Target).IsFormLike)
                {
                    WalkAppearance(walk, annotation, single.Target, pageResources);
                    continue;
                }

                if (scan.ResolveDictionary(appearance) is { } states)
                {
                    foreach (var (_, state) in states)
                    {
                        if (state is PdfReference stateReference && scan.XObject(stateReference.Target).IsFormLike)
                        {
                            WalkAppearance(walk, annotation, stateReference.Target, pageResources);
                        }
                    }
                }
            }
        }
    }

    private static void WalkAppearance(PageWalk walk, PdfDictionary annotation, IndirectReference reference, PdfDictionary? pageResources)
    {
        var scan = walk.Scan;
        if (!walk.Path.Add(reference.Number))
        {
            return;
        }

        try
        {
            // An appearance the renderer can't place paints nothing, so it places nothing.
            if (scan.Source.ReadPayloadOnce(reference) is { } stream
                && AnnotationAppearance.TryResolve(annotation, stream, scan.Objects, scan.Options, diagnostics: null, out var placement))
            {
                Walk(walk, placement.ContentBytes, placement.Resources ?? pageResources, placement.Ctm, WalkMode.Measured, depth: 1);
            }
        }
        finally
        {
            walk.Path.Remove(reference.Number);
        }
    }

    // Every image the page's resources and annotation appearances reach, through forms,
    // patterns, soft-mask groups and Type 3 fonts, from dictionaries alone, each passed to
    // onImage once. Iterative and guarded by 'reach', so neither a deep chain nor a cycle can
    // run away; a 'reach' shared across pages visits each object once per scan.
    private static void VisitReachableImages(ScanState scan, PdfDictionary page, PdfDictionary? resources, Reach reach, Action<int> onImage)
    {
        var pending = new Stack<PdfDictionary>();

        void Push(PdfDictionary? dictionary)
        {
            if (dictionary is not null && reach.Dictionaries.Add(dictionary))
            {
                pending.Push(dictionary);
            }
        }

        void Follow(PdfObject? value)
        {
            if (value is not PdfReference reference || !reach.Numbers.Add(reference.Target.Number))
            {
                return;
            }

            var resolved = scan.Discover(reference.Target, out var isStream);
            var dictionary = resolved as PdfDictionary ?? (resolved as PdfStream)?.Dictionary;
            if (dictionary is null || !isStream)
            {
                return;
            }

            if (scan.Resolve(dictionary.TryGetValue(PdfName.Subtype, out var subtype) ? subtype : null) is PdfName name && name.Equals(PdfName.Image))
            {
                onImage(reference.Target.Number);
                return;
            }

            Push(scan.ResolveDictionary(dictionary.TryGetValue(ResourcesName, out var r) ? r : null));
        }

        Push(resources);
        if (scan.Resolve(page.TryGetValue(PdfName.Annots, out var a) ? a : null) is PdfArray annotations)
        {
            foreach (var entry in annotations)
            {
                if (scan.ResolveDictionary(entry) is { } annotation
                    && scan.ResolveDictionary(annotation.TryGetValue(PdfName.AP, out var ap) ? ap : null) is { } appearances)
                {
                    foreach (var kind in AppearanceKinds)
                    {
                        if (appearances.TryGetValue(kind, out var appearance))
                        {
                            Follow(appearance);
                            foreach (var (_, state) in Entries(scan.ResolveDictionary(appearance)))
                            {
                                Follow(state);
                            }
                        }
                    }
                }
            }
        }

        while (pending.TryPop(out var current))
        {
            foreach (var (_, value) in Entries(scan.ResolveDictionary(current.TryGetValue(XObjectName, out var x) ? x : null)))
            {
                Follow(value);
            }

            foreach (var (_, value) in Entries(scan.ResolveDictionary(current.TryGetValue(PatternName, out var p) ? p : null)))
            {
                Follow(value);
            }

            foreach (var (_, value) in Entries(scan.ResolveDictionary(current.TryGetValue(ExtGStateName, out var g) ? g : null)))
            {
                if (scan.ResolveDictionary(value) is { } state && scan.ResolveDictionary(state.TryGetValue(PdfName.SMask, out var sm) ? sm : null) is { } softMask)
                {
                    Follow(softMask.TryGetValue(GroupName, out var group) ? group : null);
                }
            }

            foreach (var (_, value) in Entries(scan.ResolveDictionary(current.TryGetValue(FontName, out var f) ? f : null)))
            {
                if (scan.ResolveDictionary(value) is { } font)
                {
                    Push(scan.ResolveDictionary(font.TryGetValue(ResourcesName, out var fr) ? fr : null));
                    foreach (var (_, procedure) in Entries(scan.ResolveDictionary(font.TryGetValue(CharProcsName, out var cp) ? cp : null)))
                    {
                        Follow(procedure);
                    }
                }
            }
        }
    }

    // DPI per image axis: the image's unit square maps its x axis onto the CTM's first row and
    // its y axis onto the second, so each row's length is that axis's physical size in points.
    private static bool TryMeasure(int width, int height, PdfMatrix ctm, double userUnit, out double dpiX, out double dpiY)
    {
        dpiX = dpiY = 0;
        if (width <= 0 || height <= 0 || !ctm.IsFinite)
        {
            return false;
        }

        var lengthX = Math.Sqrt((ctm.A * ctm.A) + (ctm.B * ctm.B)) * userUnit;
        var lengthY = Math.Sqrt((ctm.C * ctm.C) + (ctm.D * ctm.D)) * userUnit;
        if (!(lengthX > 0) || !(lengthY > 0) || !double.IsFinite(lengthX) || !double.IsFinite(lengthY))
        {
            return false;
        }

        dpiX = width * 72.0 / lengthX;
        dpiY = height * 72.0 / lengthY;
        return double.IsFinite(dpiX) && double.IsFinite(dpiY);
    }

    private static bool TryReadMatrix(IReadOnlyList<PdfObject> values, out PdfMatrix matrix)
    {
        matrix = PdfMatrix.Identity;
        Span<double> parts = stackalloc double[6];
        for (var i = 0; i < 6; i++)
        {
            if (values[i] is not PdfNumber number || !double.IsFinite(number.Value))
            {
                return false;
            }

            parts[i] = number.Value;
        }

        matrix = new PdfMatrix(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5]);
        return true;
    }

    private static IEnumerable<KeyValuePair<PdfName, PdfObject>> Entries(PdfDictionary? dictionary) =>
        dictionary ?? Enumerable.Empty<KeyValuePair<PdfName, PdfObject>>();

    /// <summary>What a stream reached by reference is, from its dictionary alone.</summary>
    private enum XObjectKind
    {
        /// <summary>Not a stream.</summary>
        None = 0,

        /// <summary>A stream typed <c>/Subtype /Image</c>.</summary>
        Image,

        /// <summary>A stream typed <c>/Subtype /Form</c>.</summary>
        Form,

        /// <summary>A stream with no <c>/Subtype</c>: a soft-mask group or appearance may omit it.</summary>
        Untyped,

        /// <summary>A stream of some other subtype.</summary>
        Other,
    }

    /// <summary>What one XObject is, and an image's size, from its dictionary alone.</summary>
    private readonly record struct XObjectInfo(XObjectKind Kind, int Width, int Height)
    {
        /// <summary>A soft-mask group or appearance stream: typed as a form, or untyped.</summary>
        public bool IsFormLike => Kind is XObjectKind.Form or XObjectKind.Untyped;
    }

    /// <summary>The whole scan's state: the registry, the folded placements, and what was walked once.</summary>
    private sealed class ScanState(ObjectRegistry objects, PdfOptions options)
    {
        private readonly Dictionary<int, XObjectInfo> _xobjects = [];
        private readonly HashSet<(string Code, int Subject)> _reported = [];

        public ObjectRegistry Objects { get; } = objects;

        public IObjectSource Source { get; } = objects;

        public PdfOptions Options { get; } = options;

        public DiagnosticCollection Diagnostics { get; } = new();

        public Dictionary<int, ImagePlacements> Images { get; } = [];

        public List<InlineImageUse> InlineImages { get; } = [];

        public HashSet<(int Number, WalkMode Mode)> WalkedOnce { get; } = [];

        public HashSet<object> WalkedFonts { get; } = [];

        /// <summary>Where each once-walked pattern or Type 3 font's inline images sit in <see cref="InlineImages"/>, from its first walk.</summary>
        public Dictionary<object, (int Start, int Count)> OnceWalkedInlineImages { get; } = [];

        /// <summary>The pages each once-walked pattern or Type 3 font has recorded its inline images for.</summary>
        public HashSet<(object Key, int Page)> OnceWalkedPages { get; } = [];

        /// <summary>What the listing sweep has visited, across every page.</summary>
        public Reach Listed { get; } = new();

        public ImagePlacements Placements(int number)
        {
            if (!Images.TryGetValue(number, out var placements))
            {
                placements = new ImagePlacements();
                Images[number] = placements;
            }

            return placements;
        }

        // An XObject's kind and size, from its dictionary, read once per scan.
        public XObjectInfo XObject(IndirectReference reference)
        {
            if (_xobjects.TryGetValue(reference.Number, out var info))
            {
                return info;
            }

            var resolved = Discover(reference, out var isStream);
            var dictionary = resolved as PdfDictionary ?? (resolved as PdfStream)?.Dictionary;
            info = default;
            if (isStream && dictionary is not null)
            {
                var subtype = dictionary.TryGetValue(PdfName.Subtype, out var s) ? Resolve(s) : null;
                if (subtype is PdfName image && image.Equals(PdfName.Image))
                {
                    NativeImageSamples.TryReadSize(dictionary, Objects, out var width, out var height);
                    info = new XObjectInfo(XObjectKind.Image, width, height);
                }
                else
                {
                    var kind = subtype is null ? XObjectKind.Untyped : subtype is PdfName form && form.Equals(FormName) ? XObjectKind.Form : XObjectKind.Other;
                    info = new XObjectInfo(kind, 0, 0);
                }
            }

            _xobjects[reference.Number] = info;
            return info;
        }

        // The resource entry named in one category, unresolved: an XObject or pattern is a
        // reference whose target is read for its dictionary only.
        public PdfObject? LookupResource(PdfDictionary? resources, PdfName category, PdfName name) =>
            resources is not null
            && ResolveDictionary(resources.TryGetValue(category, out var c) ? c : null) is { } entries
            && entries.TryGetValue(name, out var entry)
                ? entry
                : null;

        // A referenced object for its dictionary: a stream's comes back without its payload
        // (never cached). An object the document can't produce (a dangling reference refused
        // under Strict) is absent, as it is to every reader that skips it.
        public PdfObject? Discover(IndirectReference reference, out bool isStream)
        {
            try
            {
                return Source.ResolveForDiscovery(reference, out isStream);
            }
            catch (PlumePdfException)
            {
                isStream = false;
                return null;
            }
        }

        // A non-stream value; a reference to a stream yields nothing (its dictionary is read
        // through Discover where a stream is expected).
        public PdfObject? Resolve(PdfObject? value)
        {
            if (value is not PdfReference reference)
            {
                return value;
            }

            var resolved = Discover(reference.Target, out var isStream);
            return isStream ? null : resolved;
        }

        public PdfDictionary? ResolveDictionary(PdfObject? value) => Resolve(value) as PdfDictionary;

        // One entry per (code, object): a cycle met on every page is reported once.
        public void Report(string code, DiagnosticSeverity severity, string message, IndirectReference? subject)
        {
            if (_reported.Add((code, subject?.Number ?? -1)))
            {
                Diagnostics.Add(new PdfDiagnostic(code, severity, message, subject: subject));
            }
        }
    }

    /// <summary>One page's walk: its page index and user unit, the work budget, and whether it was cut short.</summary>
    private sealed class PageWalk(ScanState scan, int pageIndex, double userUnit)
    {
        private long _operators;

        public ScanState Scan { get; } = scan;

        public int PageIndex { get; } = pageIndex;

        public double UserUnit { get; } = userUnit;

        /// <summary>The form-like streams on the current recursion path, for cycle detection.</summary>
        public HashSet<int> Path { get; } = [];

        /// <summary>Every image a Do on this page reached.</summary>
        public HashSet<int> ImagesMet { get; } = [];

        /// <summary>A placement may have been missed.</summary>
        public bool Truncated { get; private set; }

        /// <summary>The walk has stopped altogether.</summary>
        public bool Stopped { get; private set; }

        public void Truncate(string code, string message, IndirectReference? subject)
        {
            Truncated = true;
            Scan.Report(code, DiagnosticSeverity.Warning, message, subject);
        }

        // A content payload decoded under the document's caps. A stream the scan can't decode
        // may still draw images for a reader that can (one with another decoder, or lenient
        // about a filter name's spelling), so the page's images are unmeasured, never "not drawn".
        public byte[]? TryDecode(PdfStream stream, string what)
        {
            try
            {
                return stream.GetDecodedBytes(Scan.Options.Filters, Scan.Options, r => Scan.Objects[r]);
            }
            catch (PlumePdfException ex)
            {
                Truncate(ex.Code, $"{what} could not be decoded during the compression placement scan ({ex.Message}); the page's images are treated as unmeasured.", null);
                return null;
            }
        }

        public void Stop(string code, string message)
        {
            Stopped = true;
            Truncate(code, message, null);
        }

        // Cumulative across the page and everything it draws, so a small form graph fanned out
        // into an enormous number of Do invocations is bounded by the total work done.
        public bool Spend(int operators)
        {
            _operators += operators;
            if (_operators <= Scan.Options.MaxContentStreamOperators)
            {
                return true;
            }

            Stop("PLUME7010", $"Page {PageIndex} and the content it draws hold more than {Scan.Options.MaxContentStreamOperators} content-stream operators; the compression placement scan stopped there and treats the page's images as unmeasured.");
            return false;
        }
    }

    /// <summary>The objects and dictionaries a reachability sweep has visited.</summary>
    private sealed class Reach
    {
        public HashSet<int> Numbers { get; } = [];

        public HashSet<PdfDictionary> Dictionaries { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>One image's placements, folded as they are met.</summary>
    private sealed class ImagePlacements
    {
        private double _dpiX = double.PositiveInfinity;
        private double _dpiY = double.PositiveInfinity;
        private double _lowest = double.PositiveInfinity;
        private int _lowestPage = -1;
        private int _firstPage = -1;
        private int _listedPage = -1;
        private bool _measured;
        private bool _tiling;
        private bool _unmeasured;

        public void AddMeasured(int page, double dpiX, double dpiY)
        {
            Meet(page);
            _measured = true;
            _dpiX = Math.Min(_dpiX, dpiX);
            _dpiY = Math.Min(_dpiY, dpiY);
            var lower = Math.Min(dpiX, dpiY);
            if (lower < _lowest)
            {
                _lowest = lower;
                _lowestPage = page;
            }
        }

        public void AddUnmeasured(int page, bool tiling = false)
        {
            Meet(page);
            if (tiling)
            {
                _tiling = true;
            }
            else
            {
                _unmeasured = true;
            }
        }

        // Reachable from a page's resources; counts only when nothing draws the image.
        public void AddListed(int page)
        {
            if (_listedPage < 0)
            {
                _listedPage = page;
            }
        }

        public ImagePlacementSummary Summarize()
        {
            if (!_measured && !_tiling && !_unmeasured)
            {
                return new ImagePlacementSummary(null, null, _listedPage, PlacementKind.NotPlaced);
            }

            if (_unmeasured || (_tiling && _measured))
            {
                return new ImagePlacementSummary(null, null, _firstPage, PlacementKind.Unmeasured);
            }

            return _tiling
                ? new ImagePlacementSummary(null, null, _firstPage, PlacementKind.TilingPatternOnly)
                : new ImagePlacementSummary(_dpiX, _dpiY, _lowestPage, PlacementKind.Measured);
        }

        private void Meet(int page)
        {
            if (_firstPage < 0)
            {
                _firstPage = page;
            }
        }
    }
}
