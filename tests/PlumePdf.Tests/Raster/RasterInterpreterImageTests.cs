using System.Linq;
using System.Text;
using PlumePdf.Content;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Raster.Color;
using PlumePdf.Raster.DisplayList;
using PlumePdf.Raster.OptionalContent;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// The drop-path diagnostics
/// (<c>PLUME7747</c>/<c>PLUME7748</c>), XObject-level <c>/OC</c> suppression (<c>PLUME7750</c>),
/// inline-image (<c>BI</c>/<c>ID</c>/<c>EI</c>) dispatch, and <see cref="ImagePainter.Paint"/>'s
/// stencil-fill color-machinery completion — all exercised through a stub
/// <see cref="RasterInterpreter.ImageResolver"/> (the shared delegate
/// signature means this needs no real decoder — <c>ImageXObjectResolver</c>
/// wires in independently).
/// </summary>
public class RasterInterpreterImageTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static RasterImageFrame SolidFrame(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 3] = r;
            pixels[(i * 3) + 1] = g;
            pixels[(i * 3) + 2] = b;
        }

        return new RasterImageFrame(pixels, width, height, RasterPixelFormat.Rgb24);
    }

    private static RasterInterpreter.ImageResolver StubResolver(byte r = 200, byte g = 100, byte b = 50) =>
        (_, _) => SolidFrame(1, 1, r, g, b);

    private static PdfDictionary EmptyOcProperties()
    {
        var ocProperties = new PdfDictionary();
        ocProperties.Set(PdfName.Get("D"), new PdfDictionary());
        return ocProperties;
    }

    // A minimal IObjectSource for tests that need real object-number identity (OCG /OFF-array
    // membership resolves by reference, not by dictionary content) without a full PdfDocument —
    // mirrors OptionalContentTests.cs's own helper (not shared across files: each test file owns
    // its fixtures).
    private sealed class InMemorySource(Dictionary<int, PdfObject> objects) : IObjectSource
    {
        public PdfDictionary Trailer => new();

        public PdfObject Resolve(IndirectReference reference) =>
            objects.TryGetValue(reference.Number, out var value) ? value : PdfNull.Instance;
    }

    // =========================================================================================
    // drop-path diagnostics (PLUME7747/PLUME7748), never double-reported when the
    // resolver itself already diagnosed a decode failure.
    // =========================================================================================

    [Fact]
    public void HandleDo_MissingXObjectResource_RecordsExactlyOnePlume7747()
    {
        var content = Bytes("/Missing Do");
        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        Assert.Empty(displayList.Children);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7747", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    /// <summary>
    /// Review-finding regression: an <c>/Image</c> XObject whose <c>/ColorSpace</c> is a bare
    /// non-device NAME (e.g. <c>/CS0</c>) refers to the page's <c>/Resources /ColorSpace</c>
    /// dictionary — PDFium/poppler resolve it there, and the inline-image path already did; the
    /// XObject path handed the bare name to the resolver, which refused the whole image. The
    /// interpreter now substitutes the resolved value into the dictionary the resolver receives.
    /// </summary>
    [Fact]
    public void HandleDo_ImageWithNamedColorSpaceResource_ResolvesThroughPageResources()
    {
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Get("Subtype"), PdfName.Get("Image"));
        imageDict.Set(PdfName.Get("Width"), PdfNumber.Get(1));
        imageDict.Set(PdfName.Get("Height"), PdfNumber.Get(1));
        imageDict.Set(PdfName.Get("BitsPerComponent"), PdfNumber.Get(8));
        imageDict.Set(PdfName.Get("ColorSpace"), PdfName.Get("CS0"));
        var imageStream = new PdfStream(imageDict, new byte[] { 0x80 });

        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Im0"), imageStream);
        var colorSpaces = new PdfDictionary();
        colorSpaces.Set(PdfName.Get("CS0"), PdfName.Get("DeviceGray"));
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);
        resources.Set(PdfName.Get("ColorSpace"), colorSpaces);

        PdfDictionary? received = null;
        RasterInterpreter.ImageResolver capturing = (dict, _) =>
        {
            received = dict;
            return SolidFrame(1, 1, 10, 20, 30);
        };

        RasterInterpreter.BuildDisplayList(Bytes("/Im0 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: capturing);

        Assert.NotNull(received);
        Assert.Same(PdfName.Get("DeviceGray"), received![PdfName.Get("ColorSpace")]);
    }

    [Fact]
    public void HandleDo_NonStreamXObjectResource_RecordsExactlyOnePlume7747()
    {
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Im0"), new PdfDictionary()); // A dictionary, not a stream — every real XObject is a stream.
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        var diagnostics = new DiagnosticCollection();
        RasterInterpreter.BuildDisplayList(Bytes("/Im0 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7747", diagnostic.Code);
    }

    [Fact]
    public void HandleDo_MissingSubtype_RecordsExactlyOnePlume7748()
    {
        var stream = new PdfStream(new PdfDictionary(), Array.Empty<byte>()); // No /Subtype at all.
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("X0"), stream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        var diagnostics = new DiagnosticCollection();
        RasterInterpreter.BuildDisplayList(Bytes("/X0 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7748", diagnostic.Code);
    }

    [Fact]
    public void HandleDo_UnsupportedSubtype_RecordsExactlyOnePlume7748()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, PdfName.Get("PS")); // Deprecated PostScript XObject — neither /Image nor /Form.
        var stream = new PdfStream(dict, Array.Empty<byte>());
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("X0"), stream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        var diagnostics = new DiagnosticCollection();
        RasterInterpreter.BuildDisplayList(Bytes("/X0 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7748", diagnostic.Code);
    }

    [Fact]
    public void HandleDo_ImageResolverReturnsNull_DoesNotDoubleReportADiagnostic()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Subtype, PdfName.Get("Image"));
        var stream = new PdfStream(dict, Array.Empty<byte>());
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Im0"), stream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        var diagnostics = new DiagnosticCollection();

        // A present resolver that declines to decode already records its own diagnostic
        // (mirrors ImageXObjectResolver's own PLUME7744/7745/7746 contract) — HandleDo must not
        // add a second one on top of it.
        RasterInterpreter.ImageResolver resolver = (_, _) =>
        {
            diagnostics.Add(new PdfDiagnostic("PLUME7744", DiagnosticSeverity.Warning, "simulated decode failure"));
            return null;
        };

        var displayList = RasterInterpreter.BuildDisplayList(Bytes("/Im0 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics, imageResolver: resolver);

        Assert.Empty(displayList.Children);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7744", diagnostic.Code);
    }

    // =========================================================================================
    // XObject-level /OC (PLUME7750): a default-OFF layer image/form must not paint, an
    // ON one must.
    // =========================================================================================

    [Fact]
    public void HandleDo_ImageWithOffOcg_SkipsPaintingAndRecordsPlume7750()
    {
        var ocgRef = new IndirectReference(10, 0);
        var ocg = new PdfDictionary();
        ocg.Set(PdfName.Type, PdfName.Get("OCG"));
        var objects = new ObjectRegistry(new InMemorySource(new Dictionary<int, PdfObject> { [10] = ocg }));

        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        imageDict.Set(PdfName.Get("OC"), new PdfReference(ocgRef));
        var imageStream = new PdfStream(imageDict, Array.Empty<byte>());
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Im0"), imageStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        var ocProperties = new PdfDictionary();
        var d = new PdfDictionary();
        d.Set(PdfName.Get("OFF"), new PdfArray([new PdfReference(ocgRef)]));
        ocProperties.Set(PdfName.Get("D"), d);
        var config = OptionalContentConfig.Parse(ocProperties, objects, null);

        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(Bytes("/Im0 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics, objects: objects, imageResolver: StubResolver(), optionalContent: config);

        Assert.Empty(displayList.Children.OfType<ImagePageObject>());
        var diagnostic = Assert.Single(diagnostics, dd => dd.Code == "PLUME7750");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void HandleDo_ImageWithOnOcg_PaintsNormally()
    {
        var ocgRef = new IndirectReference(10, 0);
        var ocg = new PdfDictionary();
        ocg.Set(PdfName.Type, PdfName.Get("OCG"));
        var objects = new ObjectRegistry(new InMemorySource(new Dictionary<int, PdfObject> { [10] = ocg }));

        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        imageDict.Set(PdfName.Get("OC"), new PdfReference(ocgRef));
        var imageStream = new PdfStream(imageDict, Array.Empty<byte>());
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Im0"), imageStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        // No /OFF array at all -> every OCG is ON by default.
        var config = OptionalContentConfig.Parse(EmptyOcProperties(), objects, null);

        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(Bytes("/Im0 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics, objects: objects, imageResolver: StubResolver(), optionalContent: config);

        Assert.Single(displayList.Children.OfType<ImagePageObject>());
        Assert.DoesNotContain(diagnostics, dd => dd.Code == "PLUME7750");
    }

    [Fact]
    public void HandleDo_FormWithOffOcg_SkipsPaintingItsContentAndRecordsPlume7750()
    {
        var ocgRef = new IndirectReference(11, 0);
        var ocg = new PdfDictionary();
        ocg.Set(PdfName.Type, PdfName.Get("OCG"));
        var objects = new ObjectRegistry(new InMemorySource(new Dictionary<int, PdfObject> { [11] = ocg }));

        var formDict = new PdfDictionary();
        formDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        formDict.Set(PdfName.Get("OC"), new PdfReference(ocgRef));
        var formStream = new PdfStream(formDict, Bytes("1 0 0 rg 0 0 10 10 re f"));
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Fm1"), formStream);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);

        var ocProperties = new PdfDictionary();
        var d = new PdfDictionary();
        d.Set(PdfName.Get("OFF"), new PdfArray([new PdfReference(ocgRef)]));
        ocProperties.Set(PdfName.Get("D"), d);
        var config = OptionalContentConfig.Parse(ocProperties, objects, null);

        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(Bytes("/Fm1 Do"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics, objects: objects, optionalContent: config);

        Assert.Empty(displayList.Children);
        var diagnostic = Assert.Single(diagnostics, dd => dd.Code == "PLUME7750");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    // =========================================================================================
    // inline-image (BI/ID/EI) dispatch: abbreviation expansion, resolver reuse, and the
    // no-resolver drop diagnostic.
    // =========================================================================================

    [Fact]
    public void BuildDisplayList_AbbreviatedInlineImage_PaintsPixels()
    {
        var header = Bytes("q 20 0 0 20 0 0 cm BI /W 2 /H 2 /BPC 8 /CS /RGB ID ");
        var payload = new byte[12];
        Array.Fill(payload, (byte)0x10); // 2x2 RGB8 — the stub resolver ignores the actual bytes.
        var footer = Bytes(" EI Q");
        var content = Concat(header, payload, footer);

        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: StubResolver(1, 2, 3));
        Assert.Single(displayList.Children.OfType<ImagePageObject>());

        var surface = RasterSurface.Create(20, 20);
        surface.Clear(255, 255, 255, 255);
        RasterInterpreter.Paint(surface, displayList, RasterPaintContext.Default);

        var (b, g, r, _) = surface.GetPixel(10, 10);
        Assert.Equal((1, 2, 3), (r, g, b));
    }

    [Fact]
    public void BuildDisplayList_InlineImageAbbreviatedKeys_ExpandsToCanonicalDictionary()
    {
        // Table 93 dictionary-key abbreviations, Table 94's /G colorspace-name abbreviation, and
        // Table 95's already-registered /Fl filter abbreviation (left untouched).
        var header = Bytes("BI /W 2 /H 2 /BPC 8 /CS /G /IM true /F /Fl /D [1 0] ID ");
        var payload = new byte[4];
        Array.Fill(payload, (byte)0x20);
        var footer = Bytes(" EI");
        var content = Concat(header, payload, footer);

        PdfDictionary? captured = null;
        RasterInterpreter.ImageResolver resolver = (dict, _) =>
        {
            captured = dict;
            return SolidFrame(1, 1, 0, 0, 0);
        };

        RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: resolver);

        Assert.NotNull(captured);
        Assert.Equal(2.0, ((PdfNumber)captured![PdfName.Get("Width")]).Value);
        Assert.Equal(2.0, ((PdfNumber)captured[PdfName.Get("Height")]).Value);
        Assert.Equal(8.0, ((PdfNumber)captured[PdfName.Get("BitsPerComponent")]).Value);
        Assert.Equal("DeviceGray", ((PdfName)captured[PdfName.Get("ColorSpace")]).Value);
        Assert.True(((PdfBoolean)captured[PdfName.Get("ImageMask")]).Value);
        Assert.Equal("Fl", ((PdfName)captured[PdfName.Get("Filter")]).Value); // Filter abbreviation left as-is.
        Assert.IsType<PdfArray>(captured[PdfName.Get("Decode")]);

        // The abbreviated keys themselves must be gone once expanded.
        Assert.False(captured.TryGetValue(PdfName.Get("W"), out _));
        Assert.False(captured.TryGetValue(PdfName.Get("CS"), out _));
        Assert.False(captured.TryGetValue(PdfName.Get("BPC"), out _));
    }

    /// <summary>
    /// Regression: §8.9.7 permits mixing spelled-out and abbreviated forms within one array
    /// colorspace - <c>[/Indexed /RGB 1 &lt;...&gt;]</c> spells out <c>/Indexed</c> but abbreviates
    /// its base space. The array's base-space expansion previously only fired when element 0
    /// itself was abbreviated, so this shape fell through unexpanded and <c>ColorSpace.Parse</c>
    /// would fail on the raw <c>/RGB</c> name.
    /// </summary>
    [Fact]
    public void BuildDisplayList_InlineImageIndexedArrayWithAbbreviatedBase_ExpandsBaseSpace()
    {
        var header = Bytes("BI /W 1 /H 1 /BPC 8 /CS [/Indexed /RGB 1 <0000FF0000FF>] ID ");
        var payload = new byte[] { 0x00 };
        var footer = Bytes(" EI");
        var content = Concat(header, payload, footer);

        PdfDictionary? captured = null;
        RasterInterpreter.ImageResolver resolver = (dict, _) =>
        {
            captured = dict;
            return SolidFrame(1, 1, 0, 0, 0);
        };

        RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: resolver);

        Assert.NotNull(captured);
        var colorSpace = Assert.IsType<PdfArray>(captured![PdfName.Get("ColorSpace")]);
        Assert.Equal("Indexed", ((PdfName)colorSpace[0]).Value);
        Assert.Equal("DeviceRGB", ((PdfName)colorSpace[1]).Value); // Was left as raw "RGB" before the fix.
    }

    [Fact]
    public void BuildDisplayList_InlineImageNamedColorSpace_ResolvesViaPageResources()
    {
        var colorSpaces = new PdfDictionary();
        colorSpaces.Set(PdfName.Get("CS0"), PdfName.Get("DeviceGray")); // A page /ColorSpace resource the inline image shares.
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("ColorSpace"), colorSpaces);

        var header = Bytes("BI /W 1 /H 1 /BPC 8 /CS /CS0 ID ");
        var payload = new byte[] { 0x40 };
        var footer = Bytes(" EI");
        var content = Concat(header, payload, footer);

        PdfDictionary? captured = null;
        RasterInterpreter.ImageResolver resolver = (dict, _) =>
        {
            captured = dict;
            return SolidFrame(1, 1, 0, 0, 0);
        };

        RasterInterpreter.BuildDisplayList(content, resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: resolver);

        Assert.NotNull(captured);
        Assert.Equal("DeviceGray", ((PdfName)captured![PdfName.Get("ColorSpace")]).Value);
    }

    [Fact]
    public void BuildDisplayList_InlineImageWithNoResolver_RecordsPlume7744AndPaintsNothing()
    {
        var header = Bytes("BI /W 1 /H 1 /BPC 8 /CS /G ID ");
        var payload = new byte[] { 0x00 };
        var footer = Bytes(" EI");
        var content = Concat(header, payload, footer);

        var diagnostics = new DiagnosticCollection();
        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics);

        Assert.Empty(displayList.Children.OfType<ImagePageObject>());
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7744", diagnostic.Code);
    }

    // =========================================================================================
    // ImagePainter.StencilToRgb: a non-DeviceGray/RGB stencil fill must paint its
    // converted color, not the old unconditional black.
    // =========================================================================================

    /// <summary>
    /// Root cause: a fill colored via a NAMED colorspace (<c>/CSp cs … scn</c>, the
    /// shape Qt's PDF engine emits for EVERY fill) kept only the raw resource name, which the
    /// paint-time fallback rendered as BLACK — whole Qt-generated pages went dark. A name
    /// aliasing a device space must resolve to that device space.
    /// </summary>
    [Fact]
    public void Cs_NamedDeviceRgbAlias_ScnCarriesResolvedDeviceColor()
    {
        var colorSpaces = new PdfDictionary();
        colorSpaces.Set(PdfName.Get("CSp"), PdfName.Get("DeviceRGB"));
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("ColorSpace"), colorSpaces);

        var displayList = RasterInterpreter.BuildDisplayList(
            Bytes("/CSp cs 0.9 0.2 0.1 scn 0 0 10 10 re f"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);

        var path = Assert.IsType<PathPageObject>(Assert.Single(displayList.Children));
        Assert.NotNull(path.FillColor);
        Assert.Equal("DeviceRGB", path.FillColor!.Value.ColorSpaceName);
        Assert.Equal([0.9, 0.2, 0.1], path.FillColor.Value.Components);
    }

    /// <summary>
    /// The non-alias half of the fix: a named colorspace resolving to a real parsed
    /// space (here <c>/CalGray</c>) converts <c>scn</c> components to device RGB through the
    /// color machinery at set-color time — full-intensity CalGray must come out near-white,
    /// not fall to the black fallback.
    /// </summary>
    [Fact]
    public void Cs_NamedCalGray_ScnConvertsThroughResolvedSpace()
    {
        var whitePoint = new PdfArray([PdfNumber.Get(0.9505), PdfNumber.Get(1), PdfNumber.Get(1.089)]);
        var calGrayParams = new PdfDictionary();
        calGrayParams.Set(PdfName.Get("WhitePoint"), whitePoint);
        var colorSpaces = new PdfDictionary();
        colorSpaces.Set(PdfName.Get("CG"), new PdfArray([PdfName.Get("CalGray"), calGrayParams]));
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("ColorSpace"), colorSpaces);

        var displayList = RasterInterpreter.BuildDisplayList(
            Bytes("/CG cs 1 scn 0 0 10 10 re f"), resources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null);

        var path = Assert.IsType<PathPageObject>(Assert.Single(displayList.Children));
        Assert.NotNull(path.FillColor);
        Assert.Equal("DeviceRGB", path.FillColor!.Value.ColorSpaceName);
        Assert.All(path.FillColor.Value.Components, static c => Assert.True(c > 0.9, $"full CalGray intensity converted to {c} — expected near-white, not the black fallback"));
    }

    [Fact]
    public void ImagePainter_StencilMaskWithCmykFillColor_PaintsConvertedColorNotBlack()
    {
        var surface = RasterSurface.Create(4, 4);
        surface.Clear(255, 255, 255, 255);

        var maskPixels = new byte[2 * 2]; // Gray8, all-zero -> "on" everywhere under the default /Decode.
        var frame = new RasterImageFrame(maskPixels, 2, 2, RasterPixelFormat.Gray8);

        var cmyk = new PaintColor("DeviceCMYK", [0.0, 1.0, 1.0, 0.0]);
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(4, 0, 0, 4, 0, 0),
            Frame = frame,
            IsStencilMask = true,
            StencilColor = cmyk,
        };

        ImagePainter.Paint(surface, image, 0, 0, 4, 4, RasterPaintContext.Default);

        var (expectedR, expectedG, expectedB) = DeviceCmyk.ToSrgb(0.0, 1.0, 1.0, 0.0);
        var (b, g, r, _) = surface.GetPixel(2, 2);
        Assert.Equal(expectedR, r);
        Assert.Equal(expectedG, g);
        Assert.Equal(expectedB, b);
        Assert.False(r == 0 && g == 0 && b == 0); // Not the old always-black fallback.
    }

    [Fact]
    public void ImagePainter_StencilMaskWithUnrecognizedThreeComponentFillColor_ApproximatesAsRgbNotBlack()
    {
        // A colorspace resolved through a page /ColorSpace resource (e.g. CalRGB/Lab) carries
        // only its resource name here, not a resolved alternate space — StencilToRgb falls back
        // to a component-count approximation rather than black.
        var surface = RasterSurface.Create(4, 4);
        surface.Clear(0, 0, 0, 255);

        var maskPixels = new byte[1 * 1];
        var frame = new RasterImageFrame(maskPixels, 1, 1, RasterPixelFormat.Gray8);

        var namedSpace = new PaintColor("CS0", [0.2, 0.4, 0.6]);
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(4, 0, 0, 4, 0, 0),
            Frame = frame,
            IsStencilMask = true,
            StencilColor = namedSpace,
        };

        ImagePainter.Paint(surface, image, 0, 0, 4, 4, RasterPaintContext.Default);

        var (b, g, r, _) = surface.GetPixel(2, 2);
        Assert.Equal((byte)Math.Round(0.2 * 255), r);
        Assert.Equal((byte)Math.Round(0.4 * 255), g);
        Assert.Equal((byte)Math.Round(0.6 * 255), b);
    }

    /// <summary>
    /// Regression for the full-tint-paints-white bug: the classic prepress
    /// <c>/CS0 cs 1 scn /Im0 Do</c> spot-colour stencil only ever reaches <c>StencilToRgb</c> as
    /// a 1-component <c>PaintColor("CS0", [1.0])</c> (the resource name only, never a resolved
    /// Separation/DeviceN space - see <c>RasterInterpreter.ReadScn</c>). Per §8.6.6.4 a tint of
    /// 1.0 is the DARKEST amount of colorant, the opposite of a gray value of 1.0 (white) - an
    /// unrecognized 1-component fallback that treats tint as gray paints this stencil invisible
    /// on a white page. It must paint dark, not white.
    /// </summary>
    [Fact]
    public void ImagePainter_StencilMaskWithFullTintOneComponentFillColor_PaintsDarkNotWhite()
    {
        var surface = RasterSurface.Create(4, 4);
        surface.Clear(255, 255, 255, 255);

        var maskPixels = new byte[1 * 1]; // Gray8, zero -> "on" under the default /Decode.
        var frame = new RasterImageFrame(maskPixels, 1, 1, RasterPixelFormat.Gray8);

        var fullTint = new PaintColor("CS0", [1.0]);
        var image = new ImagePageObject
        {
            Ctm = new PdfMatrix(4, 0, 0, 4, 0, 0),
            Frame = frame,
            IsStencilMask = true,
            StencilColor = fullTint,
        };

        ImagePainter.Paint(surface, image, 0, 0, 4, 4, RasterPaintContext.Default);

        var (b, g, r, _) = surface.GetPixel(2, 2);
        Assert.False(r == 255 && g == 255 && b == 255, "A full-tint (1.0) Separation/DeviceN stencil must not paint white/invisible.");
        Assert.Equal((0, 0, 0), (r, g, b));
    }

    // =========================================================================================
    // Free-win coverage: the same injected resolver already reaches images inside
    // a nested Form XObject, an annotation-appearance-shaped content stream, and an ExtGState
    // /SMask soft-mask group — wiring it in PageRasterAdapter activates all three
    // automatically; this only has to prove RasterInterpreter itself never blocks the path.
    // =========================================================================================

    [Fact]
    public void BuildDisplayList_ImageInsideNestedFormXObject_Paints()
    {
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        var imageStream = new PdfStream(imageDict, Array.Empty<byte>());

        var formXObjects = new PdfDictionary();
        formXObjects.Set(PdfName.Get("Im0"), imageStream);
        var formResources = new PdfDictionary();
        formResources.Set(PdfName.Get("XObject"), formXObjects);

        var formDict = new PdfDictionary();
        formDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        formDict.Set(PdfName.Get("Resources"), formResources);
        var formStream = new PdfStream(formDict, Bytes("/Im0 Do"));

        var pageXObjects = new PdfDictionary();
        pageXObjects.Set(PdfName.Get("Fm1"), formStream);
        var pageResources = new PdfDictionary();
        pageResources.Set(PdfName.Get("XObject"), pageXObjects);

        var displayList = RasterInterpreter.BuildDisplayList(Bytes("/Fm1 Do"), pageResources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: StubResolver());

        var nestedForm = Assert.Single(displayList.Children.OfType<FormPageObject>());
        Assert.Single(nestedForm.Children.OfType<ImagePageObject>());
    }

    [Fact]
    public void BuildDisplayList_AnnotationAppearanceShapedStreamContainingImage_Paints()
    {
        // An annotation's /AP /N appearance stream is itself Form-XObject-shaped content
        // (ISO 32000-1 §12.5.5) fed straight through this same BuildDisplayList/HandleDo path by
        // PageRasterAdapter — nothing annotation-specific exists in RasterInterpreter, so
        // proving an image Do inside such a stream paints is a free win.
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        var imageStream = new PdfStream(imageDict, Array.Empty<byte>());
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Im0"), imageStream);
        var appearanceResources = new PdfDictionary();
        appearanceResources.Set(PdfName.Get("XObject"), xobjects);

        var appearanceContent = Bytes("q 1 0 0 1 0 0 cm /Im0 Do Q");
        var displayList = RasterInterpreter.BuildDisplayList(appearanceContent, appearanceResources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: StubResolver());

        Assert.Single(displayList.Children.OfType<ImagePageObject>());
    }

    [Fact]
    public void BuildDisplayList_ImageInsideExtGStateSoftMaskGroup_ReachesTheSameResolver()
    {
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Subtype, PdfName.Get("Image"));
        var imageStream = new PdfStream(imageDict, Array.Empty<byte>());
        var maskXObjects = new PdfDictionary();
        maskXObjects.Set(PdfName.Get("Im0"), imageStream);
        var maskResources = new PdfDictionary();
        maskResources.Set(PdfName.Get("XObject"), maskXObjects);

        var maskFormDict = new PdfDictionary();
        maskFormDict.Set(PdfName.Subtype, PdfName.Get("Form"));
        maskFormDict.Set(PdfName.Get("Resources"), maskResources);
        var maskFormStream = new PdfStream(maskFormDict, Bytes("/Im0 Do"));

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
        groupFormDict.Set(PdfName.Get("Group"), groupDict);
        var groupFormStream = new PdfStream(groupFormDict, Bytes("0 0 1 1 re f"));

        var pageXObjects = new PdfDictionary();
        pageXObjects.Set(PdfName.Get("Fm1"), groupFormStream);
        var pageResources = new PdfDictionary();
        pageResources.Set(PdfName.Get("ExtGState"), extGState);
        pageResources.Set(PdfName.Get("XObject"), pageXObjects);

        var displayList = RasterInterpreter.BuildDisplayList(Bytes("/GS1 gs /Fm1 Do"), pageResources, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: StubResolver());

        var groupForm = Assert.Single(displayList.Children.OfType<FormPageObject>());
        Assert.True(groupForm.IsTransparencyGroup);
        Assert.NotNull(groupForm.SoftMask);
        Assert.Single(groupForm.SoftMask!.Content.Children.OfType<ImagePageObject>());
    }
}
