using System.Collections.Generic;
using System.Text;
using PlumePdf.Content;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Raster.Annotations;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// <c>PdfRasterizeOptions.AntiAlias</c>
/// proven live end-to-end through every entry point that reaches <see cref="ScanlineRasterizer.Sweep"/>
/// (path fills, strokes, glyphs, annotation <c>/AP</c> appearances, tiling-pattern cells, and a
/// transparency-group child — all six thread <c>RasterPaintContext.AntiAlias</c> unchanged), and
/// the declared scope exclusions never move: image resampling never consults
/// <c>AntiAlias</c> at all; a shading fill (<c>sh</c>/shading pattern) never reaches <c>Sweep</c>
/// (it fills its whole clip region directly); a group soft mask built from shading content
/// inherits that same exclusion. The one entry point easy to over-include —
/// a non-rectangular <b>clip</b> — is corrected here to match the ruling actually shipped:
/// clip-region coverage is <em>always</em> anti-aliased —
/// <see cref="Raster.ClipRegionResolver"/> takes no flag and its own sweep passes a literal
/// <see langword="true"/> — so a clip's own edge is a seventh scope exclusion, not an eighth
/// aliased entry point, and the two-call invariant below is written that way.
/// </summary>
public class AntiAliasPipelineTests
{
    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static RasterPaintContext Context(bool antiAlias) => new(ImageResamplingMode.Auto, antiAlias);

    private static ObjectRegistry EmptyObjects() => new(new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject>()));

    // Any B/G/R channel strictly between the two extremes (0 and 255) means some pixel was
    // alpha-blended between a pure-black foreground and a pure-white background — proof the
    // sweep that produced it reported fractional coverage.
    private static bool HasStrictlyIntermediateChannel(ReadOnlySpan<byte> bgra)
    {
        for (var i = 0; i + 2 < bgra.Length; i += 4)
        {
            if (bgra[i] is > 0 and < 255 || bgra[i + 1] is > 0 and < 255 || bgra[i + 2] is > 0 and < 255)
            {
                return true;
            }
        }

        return false;
    }

    private static RasterSurface RenderSurface(string content, PdfDictionary? resources, RasterPaintContext context, int size = 100)
    {
        var displayList = RasterInterpreter.BuildDisplayList(Bytes(content), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);
        var surface = RasterSurface.Create(size, size);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, context, PdfOptions.Default);
        return surface;
    }

    // =====================================================================================
    // Entry points that DO alias: a diagonal edge is opaque black on opaque white with
    // no soft mask, so any span whose coverage the confirmed threshold rule collapses to 0/255
    // paints an exact background/foreground byte, never anything in between.
    // =====================================================================================

    [Fact]
    public void PathFill_DiagonalEdge_AliasesUnderAntiAliasOff_AndIsSmoothByDefault()
    {
        const string content = "0 0 m 100 0 l 0 100 l f"; // default nonstroking color is black.

        var aaOn = RenderSurface(content, resources: null, Context(true));
        var aaOff = RenderSurface(content, resources: null, Context(false));

        Assert.True(HasStrictlyIntermediateChannel(aaOn.Pixels), "sanity: the default render must actually show an anti-aliased edge.");
        Assert.False(HasStrictlyIntermediateChannel(aaOff.Pixels), "AntiAlias = false must leave no partially-blended pixel on a path fill's edge.");
    }

    [Fact]
    public void Stroke_DiagonalLine_AliasesUnderAntiAliasOff_AndIsSmoothByDefault()
    {
        const string content = "6 w 10 10 m 90 90 l S"; // default stroking color is black.

        var aaOn = RenderSurface(content, resources: null, Context(true));
        var aaOff = RenderSurface(content, resources: null, Context(false));

        Assert.True(HasStrictlyIntermediateChannel(aaOn.Pixels), "sanity: the default render must actually show an anti-aliased stroke edge.");
        Assert.False(HasStrictlyIntermediateChannel(aaOff.Pixels), "AntiAlias = false must leave no partially-blended pixel on a stroke's edge.");
    }

    [Fact]
    public void Glyph_DiagonalStroke_AliasesUnderAntiAliasOff_AndIsSmoothByDefault()
    {
        // "A" has clean diagonal strokes on both sides — a real glyph outline, not a synthetic
        // shape, exercising GlyphRasterizer.Paint -> ScanlineRasterizer.Sweep exactly as a real
        // document's text does. Non-embedded Standard-14 Helvetica substitutes Liberation
        // (PLUME7510), which is irrelevant here — only the sweep's own coverage is under test,
        // a **declared oracle gap** (PDFium's build ignores this flag for text).
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Helvetica"));
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), fontDict);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), fontResources);

        const string content = "BT /F1 90 Tf 5 10 Td (A) Tj ET";

        RasterImageFrame Render(bool antiAlias) => Rasterizer.Rasterize(
            Bytes(content), resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100,
            PdfOptions.Default, Context(antiAlias), diagnostics: null, EmptyObjects());

        var aaOn = Render(true);
        var aaOff = Render(false);

        Assert.True(HasStrictlyIntermediateChannel(aaOn.Pixels.Span), "sanity: the default render must actually show an anti-aliased glyph edge.");
        Assert.False(HasStrictlyIntermediateChannel(aaOff.Pixels.Span), "AntiAlias = false must leave no partially-blended pixel on a glyph's edge.");
    }

    [Fact]
    public void AnnotationAppearance_DiagonalEdge_AliasesUnderAntiAliasOff_AndIsSmoothByDefault()
    {
        var path = WriteTempFile(BuildDiagonalAnnotationFixture());
        using var document = PdfDocument.Open(path);
        var page = document.Objects[new IndirectReference(4, 0)] as PdfDictionary ?? throw new InvalidOperationException();
        var annotations = page[PdfName.Annots] as PdfArray;
        var annotationOptions = new AnnotationRenderOptions(annotations, PrintIntent: false, NeedAppearances: false, WidgetAppearanceResolver: null);

        RasterImageFrame Render(bool antiAlias) => Rasterizer.Rasterize(
            ReadOnlyMemory<byte>.Empty, resources: null, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100,
            PdfOptions.Default, Context(antiAlias), diagnostics: null, document.Objects, annotationOptions: annotationOptions);

        var aaOn = Render(true);
        var aaOff = Render(false);

        Assert.True(HasStrictlyIntermediateChannel(aaOn.Pixels.Span), "sanity: the default render must actually show an anti-aliased annotation edge.");
        Assert.False(HasStrictlyIntermediateChannel(aaOff.Pixels.Span), "AntiAlias = false must leave no partially-blended pixel on an annotation /AP edge.");
    }

    [Fact]
    public void TilingPatternCell_DiagonalEdge_AliasesUnderAntiAliasOff_AndIsSmoothByDefault()
    {
        // A single cell (BBox/XStep/YStep all 50) tiled exactly once under a same-size fill —
        // no tile seam falls inside the sampled region — whose cell content is a black diagonal
        // triangle on the cell's own transparent ground, composited over the page's white
        // background. RenderInterpreter.PaintTilingPatternFill threads `context` into the
        // cell's own PaintObject call, so the cell's internal edge is a real, wired-up AA
        // consumer, not one of the declared exclusions.
        var patternDict = new PdfDictionary();
        patternDict.Set(PdfName.Get("Type"), PdfName.Get("Pattern"));
        patternDict.Set(PdfName.Get("PatternType"), PdfNumber.Get(1));
        patternDict.Set(PdfName.Get("PaintType"), PdfNumber.Get(1));
        patternDict.Set(PdfName.Get("TilingType"), PdfNumber.Get(1));
        patternDict.Set(PdfName.Get("BBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(50), PdfNumber.Get(50)]));
        patternDict.Set(PdfName.Get("XStep"), PdfNumber.Get(50));
        patternDict.Set(PdfName.Get("YStep"), PdfNumber.Get(50));
        patternDict.Set(PdfName.Get("Resources"), new PdfDictionary());
        var patternStream = new PdfStream(patternDict, Bytes("0 0 m 50 0 l 0 50 l f"));

        var patterns = new PdfDictionary();
        patterns.Set(PdfName.Get("p0"), patternStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Pattern"), patterns);

        const string content = "/Pattern cs /p0 scn 0 0 50 50 re f";

        var aaOn = RenderSurface(content, resources, Context(true), size: 50);
        var aaOff = RenderSurface(content, resources, Context(false), size: 50);

        Assert.True(HasStrictlyIntermediateChannel(aaOn.Pixels), "sanity: the default render must actually show an anti-aliased cell edge.");
        Assert.False(HasStrictlyIntermediateChannel(aaOff.Pixels), "AntiAlias = false must leave no partially-blended pixel on a tiling-pattern cell's edge.");
    }

    [Fact]
    public void TransparencyGroupChild_DiagonalEdge_AliasesUnderAntiAliasOff_AndIsSmoothByDefault()
    {
        // An isolated transparency group (opaque, no soft mask, FillAlpha 1.0 via a plain fill
        // with no ca/CA set) whose only child is a diagonal-edged fill — the group's own
        // compositing must not itself introduce or remove intermediate values.
        var groupDict = new PdfDictionary();
        groupDict.Set(PdfName.Get("S"), PdfName.Get("Transparency"));
        groupDict.Set(PdfName.Get("I"), PdfBoolean.True);
        var formDict = new PdfDictionary();
        formDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        formDict.Set(PdfName.Get("Group"), groupDict);
        var formStream = new PdfStream(formDict, Bytes("0 0 m 100 0 l 0 100 l f"));

        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Fm1"), formStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        const string content = "/Fm1 Do";

        var aaOn = RenderSurface(content, resources, Context(true));
        var aaOff = RenderSurface(content, resources, Context(false));

        Assert.True(HasStrictlyIntermediateChannel(aaOn.Pixels), "sanity: the default render must actually show an anti-aliased group-child edge.");
        Assert.False(HasStrictlyIntermediateChannel(aaOff.Pixels), "AntiAlias = false must leave no partially-blended pixel on a transparency-group child's edge.");
    }

    // =====================================================================================
    // Declared scope exclusions: AntiAlias never reaches image
    // resampling, a shading fill, or (as built here, from shading content) a group soft mask's
    // own coverage — every one of these renders byte-identically regardless of the flag.
    // =====================================================================================

    [Fact]
    public void ImageFootprint_NeverConsultsAntiAlias()
    {
        // A 2x2 gray image magnified across the whole surface — ImagePainter.Paint never reads
        // RasterPaintContext.AntiAlias at all, so this must be byte-identical either way.
        var samples = new byte[] { 0x00, 0xFF, 0x80, 0x40 };
        RasterInterpreter.ImageResolver resolver = (_, _) => new RasterImageFrame(samples, 2, 2, RasterPixelFormat.Gray8);
        const string content = "q 100 0 0 100 0 0 cm BI /W 2 /H 2 /BPC 8 /CS /G ID ";
        var image = Bytes(content).Concat(samples).Concat(Bytes(" EI Q")).ToArray();

        byte[] Render(bool antiAlias)
        {
            var displayList = RasterInterpreter.BuildDisplayList(image, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: resolver);
            var surface = RasterSurface.Create(100, 100);
            surface.Clear(255, 255, 255, 255);
            RasterInterpreter.Paint(surface, displayList, Context(antiAlias), PdfOptions.Default);
            return surface.Pixels.ToArray();
        }

        Assert.True(Render(true).AsSpan().SequenceEqual(Render(false)), "image resampling must never depend on AntiAlias.");
    }

    [Fact]
    public void AxialShading_NeverConsultsAntiAlias()
    {
        // `sh` fills its whole clip region directly (RasterInterpreter never routes a shading
        // through ScanlineRasterizer.Sweep) — the gradient itself must be byte-identical.
        var (content, resources) = BuildAxialShadingContent();

        byte[] Render(bool antiAlias)
        {
            var displayList = RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);
            var surface = RasterSurface.Create(10, 10);
            surface.Clear(255, 255, 255, 255);
            RasterInterpreter.Paint(surface, displayList, Context(antiAlias), PdfOptions.Default);
            return surface.Pixels.ToArray();
        }

        Assert.True(Render(true).AsSpan().SequenceEqual(Render(false)), "a shading fill must never depend on AntiAlias.");
    }

    /// <summary>
    /// Vector content painted INSIDE a
    /// luminosity soft-mask group is rendered with the caller's context, so a mask drawn from
    /// paths gets hard edges under <c>AntiAlias = false</c> (its compositing is unchanged) — exactly
    /// as PDFium renders the mask group with the same render options. The mask is a white diagonal
    /// triangle over the group's black backdrop, so the red fill shows through inside it; both
    /// renders must paint, and they must differ along the hypotenuse.
    /// </summary>
    [Fact]
    public void SoftMaskLuminosity_BuiltFromPaths_IsThresholdedLikeAnyFill()
    {
        var (aaOn, aaOff) = RenderLuminosityMaskedFill(Bytes("1 1 1 rg 0 0 m 40 0 l 0 40 l h f"), resources: null);

        Assert.Contains(aaOn, static b => b != 255);  // the masked fill painted (not two blank frames)
        Assert.Contains(aaOff, static b => b != 255);
        Assert.False(aaOn.AsSpan().SequenceEqual(aaOff), "a path-built luminosity mask must alias under AntiAlias = false — its content is a fill like any other.");
    }

    // The companion exclusion — a SHADING-built luminosity mask — has no test here yet: such a
    // mask currently composites to zero coverage (PDFium paints it),
    // so any "identical under both flags" assertion would compare two blank frames. A follow-up
    // `SoftMaskLuminosity_BuiltFromShading_NeverConsultsAntiAlias` should add the ink assertion
    // once that composition paints real coverage.

    /// <summary>Renders a 40×40 page: a red full-page fill inside a transparency group, masked by a luminosity soft mask whose <c>/G</c> form paints <paramref name="maskContent"/> — once per <c>AntiAlias</c> value.</summary>
    private static (byte[] AntiAliasOn, byte[] AntiAliasOff) RenderLuminosityMaskedFill(byte[] maskContent, PdfDictionary? resources)
    {
        var maskFormDict = new PdfDictionary();
        maskFormDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        maskFormDict.Set(PdfName.Get("BBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(40), PdfNumber.Get(40)]));
        var maskGroup = new PdfDictionary();
        maskGroup.Set(PdfName.Get("S"), PdfName.Get("Transparency"));
        maskGroup.Set(PdfName.Get("CS"), PdfName.Get("DeviceGray"));
        maskFormDict.Set(PdfName.Get("Group"), maskGroup);
        if (resources is not null)
        {
            maskFormDict.Set(PdfName.Get("Resources"), resources);
        }

        var maskFormStream = new PdfStream(maskFormDict, maskContent);

        var smaskDict = new PdfDictionary();
        smaskDict.Set(PdfName.Get("S"), PdfName.Get("Luminosity"));
        smaskDict.Set(PdfName.Get("G"), maskFormStream);
        var gs1 = new PdfDictionary();
        gs1.Set(PdfName.Get("SMask"), smaskDict);
        var extGState = new PdfDictionary();
        extGState.Set(PdfName.Get("GS1"), gs1);

        var groupDict = new PdfDictionary();
        groupDict.Set(PdfName.Get("S"), PdfName.Get("Transparency"));
        var groupFormDict = new PdfDictionary();
        groupFormDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        groupFormDict.Set(PdfName.Get("BBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(40), PdfNumber.Get(40)]));
        groupFormDict.Set(PdfName.Get("Group"), groupDict);
        var groupFormStream = new PdfStream(groupFormDict, Bytes("1 0 0 rg 0 0 40 40 re f"));

        var pageXObjects = new PdfDictionary();
        pageXObjects.Set(PdfName.Get("Fm1"), groupFormStream);
        var pageResources = new PdfDictionary();
        pageResources.Set(PdfName.Get("ExtGState"), extGState);
        pageResources.Set(PdfName.Get("XObject"), pageXObjects);

        byte[] Render(bool antiAlias)
        {
            var frame = Rasterizer.Rasterize(Bytes("/GS1 gs /Fm1 Do"), pageResources, mediaBoxWidth: 40, mediaBoxHeight: 40, pixelWidth: 40, pixelHeight: 40, PdfOptions.Default, Context(antiAlias));
            return frame.Pixels.ToArray();
        }

        return (Render(true), Render(false));
    }

    private static (byte[] Content, PdfDictionary Resources) BuildAxialShadingContent()
    {
        var function = new PdfDictionary();
        function.Set(PdfName.Get("FunctionType"), PdfNumber.Get(2));
        function.Set(PdfName.Get("Domain"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(1)]));
        function.Set(PdfName.Get("C0"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0)]));
        function.Set(PdfName.Get("C1"), new PdfArray([PdfNumber.Get(1), PdfNumber.Get(1), PdfNumber.Get(1)]));
        function.Set(PdfName.Get("N"), PdfNumber.Get(1));

        var shading = new PdfDictionary();
        shading.Set(PdfName.Get("ShadingType"), PdfNumber.Get(2));
        shading.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
        shading.Set(PdfName.Get("Coords"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0)]));
        shading.Set(PdfName.Get("Function"), function);

        var shadingDict = new PdfDictionary();
        shadingDict.Set(PdfName.Get("Sh1"), shading);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Shading"), shadingDict);

        return (Bytes("/Sh1 sh"), resources);
    }

    // =====================================================================================
    // Clip coverage is always anti-aliased and ClipRegionResolver takes no flag, so a
    // non-rectangular clip's own edge is a scope exclusion, not a toggled entry point — the
    // two-call invariant: two Rasterize calls on the same content with different AntiAlias
    // values, in both orders, must (a) produce IDENTICAL pixels on the clip's own edge (it never
    // aliases), (b) produce IDENTICAL pixels in the clip's interior (trivially, both fully
    // filled), and (c) never depend on call order (proving no cross-call state leaks between
    // them — each call's
    // ClipPath/coverage cache is built fresh inside that call's own display-list build).
    // =====================================================================================

    [Fact]
    public void NonRectangularClip_EdgeAndInteriorAreIdentical_RegardlessOfAntiAlias_AndOfCallOrder()
    {
        // A triangular clip (bbox is the whole square) over a full-page black fill: only the
        // triangle's interior paints, and its hypotenuse is the clip's own (always-anti-aliased)
        // edge — never the fill's, since a same-shape fill would alias under AntiAlias:false and
        // confound the two edges. The fill here is a big rectangle strictly larger than the clip,
        // so the fill contributes no edge of its own inside the sampled region.
        const string content = "q 10 10 m 90 10 l 10 90 l h W n 0 0 0 rg -10 -10 120 120 re f Q";

        byte[] RunOrder(bool first, bool second)
        {
            // Two independent Rasterize calls, back to back — order is the only thing varied.
            _ = RenderSurface(content, resources: null, Context(first)); // discarded: only the second call's frame is asserted per ordering below.
            return RenderSurface(content, resources: null, Context(second)).Pixels.ToArray();
        }

        var trueThenFalse = RunOrder(first: true, second: false);
        var falseThenTrue = RunOrder(first: false, second: true);
        var falseAlone = RenderSurface(content, resources: null, Context(false)).Pixels.ToArray();
        var trueAlone = RenderSurface(content, resources: null, Context(true)).Pixels.ToArray();

        // (c) Order independence: a call preceded by the opposite flag renders exactly as it
        // would alone — no state survives from the first call into the second.
        Assert.True(trueThenFalse.AsSpan().SequenceEqual(falseAlone), "AntiAlias:false after AntiAlias:true must render exactly as AntiAlias:false alone.");
        Assert.True(falseThenTrue.AsSpan().SequenceEqual(trueAlone), "AntiAlias:true after AntiAlias:false must render exactly as AntiAlias:true alone.");

        // (a)/(b) The clip's own hypotenuse (and its fully-covered interior) never move: clip
        // coverage is always anti-aliased regardless of the flag.
        Assert.True(trueAlone.AsSpan().SequenceEqual(falseAlone), "a non-rectangular clip's own edge must be identical under AntiAlias true and false (it is always anti-aliased).");
        Assert.True(HasStrictlyIntermediateChannel(trueAlone), "sanity: the triangular clip must actually produce a soft edge to prove this test is looking at one.");
    }

    // Objects: 1 Catalog, 2 Pages, 4 Page (Annots [6]), 5 empty content, 6 the visible /AP
    // annotation, 7 the diagonal-triangle appearance form.
    private static byte[] BuildDiagonalAnnotationFixture()
    {
        var header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        var buffer = new List<byte>();
        buffer.AddRange(header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        void WriteStream(int num, string dictBody, string content)
        {
            offsets[num] = buffer.Count;
            var bytes = Encoding.ASCII.GetBytes(content);
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< {dictBody} /Length {bytes.Length} >>\nstream\n"));
            buffer.AddRange(bytes);
            buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        const int totalObjects = 7;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 5 0 R /Annots [6 0 R] >>");
        WriteStream(5, string.Empty, string.Empty);
        WriteObject(6, "<< /Type /Annot /Subtype /Square /Rect [0 0 100 100] /AP << /N 7 0 R >> >>");
        WriteStream(7, "/Type /XObject /Subtype /Form /BBox [0 0 100 100]", "0 0 m 100 0 l 0 100 l f");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects + 1}\n0000000000 65535 f \n"));
        for (var n = 1; n <= totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-antialias-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
