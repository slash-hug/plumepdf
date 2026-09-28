using System.Text;
using PlumePdf.Content;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Raster.DisplayList;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// The public per-call render-intent fields on <see cref="PdfRasterizeOptions"/> and the
/// <c>/Interpolate</c> read into the display list. An earlier byte-identity guard was removed
/// once these knobs became non-inert.
/// </summary>
public class RasterPaintContextTests
{
    [Fact]
    public void Default_ResamplingIsAuto_AndAntiAliasIsOn()
    {
        Assert.Equal(ImageResamplingMode.Auto, PdfRasterizeOptions.Default.ImageResampling);
        Assert.True(PdfRasterizeOptions.Default.AntiAlias);
        Assert.Equal(ImageResamplingMode.Auto, new PdfRasterizeOptions().ImageResampling);
        Assert.True(new PdfRasterizeOptions().AntiAlias);
    }

    [Theory]
    [InlineData(ImageResamplingMode.Auto)]
    [InlineData(ImageResamplingMode.Point)]
    [InlineData(ImageResamplingMode.Box)]
    [InlineData(ImageResamplingMode.Bilinear)]
    public void Validate_AcceptsEveryDefinedResamplingMode(ImageResamplingMode mode)
    {
        var options = PdfRasterizeOptions.Default with { ImageResampling = mode, AntiAlias = false };
        options.Validate(); // no throw
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(99)]
    public void Validate_RejectsUndefinedResamplingMode_AsArgumentException(int undefined)
    {
        var options = PdfRasterizeOptions.Default with { ImageResampling = (ImageResamplingMode)undefined };
        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal(nameof(PdfRasterizeOptions.ImageResampling), ex.ParamName);
    }

    [Fact]
    public void RasterPaintContext_Default_MirrorsTheOptionsDefaults()
    {
        var context = RasterPaintContext.Default;
        Assert.Equal(ImageResamplingMode.Auto, context.Resampling);
        Assert.True(context.AntiAlias);
    }

    // ---------------------------------------------------------------------------------------
    // /Interpolate (ISO 32000-1 Table 89) read into the display list at the two
    // ImagePageObject construction sites: XObject and inline image.
    // ---------------------------------------------------------------------------------------

    private static RasterInterpreter.ImageResolver StubResolver() =>
        (_, _) => new RasterImageFrame(new byte[] { 0x80 }, 1, 1, RasterPixelFormat.Gray8);

    private static PdfDictionary XObjectResources(PdfObject? interpolateValue, Dictionary<int, PdfObject>? registryObjects = null)
    {
        var imageDict = new PdfDictionary();
        imageDict.Set(PdfName.Get("Subtype"), PdfName.Get("Image"));
        imageDict.Set(PdfName.Get("Width"), PdfNumber.Get(1));
        imageDict.Set(PdfName.Get("Height"), PdfNumber.Get(1));
        imageDict.Set(PdfName.Get("BitsPerComponent"), PdfNumber.Get(8));
        imageDict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
        if (interpolateValue is not null)
        {
            imageDict.Set(PdfName.Get("Interpolate"), interpolateValue);
        }

        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get("Im0"), new PdfStream(imageDict, new byte[] { 0x80 }));
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), xobjects);
        return resources;
    }

    public static TheoryData<string, bool> InterpolateValues => new()
    {
        { "true", true },
        { "false", false },
        { "absent", false },
        { "indirect-true", true },
        { "garbage-name", false },
    };

    [Theory]
    [MemberData(nameof(InterpolateValues))]
    public void BuildDisplayList_XObjectImage_ReadsInterpolateFlag(string shape, bool expected)
    {
        var registryObjects = new Dictionary<int, PdfObject> { [7] = PdfBoolean.True };
        PdfObject? value = shape switch
        {
            "true" => PdfBoolean.True,
            "false" => PdfBoolean.False,
            "absent" => null,
            "indirect-true" => new PdfReference(new IndirectReference(7, 0)),
            _ => PdfName.Get("yes"),
        };
        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), registryObjects));

        var displayList = RasterInterpreter.BuildDisplayList(Encoding.ASCII.GetBytes("/Im0 Do"), XObjectResources(value), PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, objects: objects, imageResolver: StubResolver());

        var image = Assert.Single(displayList.Children.OfType<ImagePageObject>());
        Assert.Equal(expected, image.Interpolate);
    }

    [Theory]
    [InlineData("/I true ", true)]
    [InlineData("/Interpolate true ", true)]
    [InlineData("/I false ", false)]
    [InlineData("", false)]
    public void BuildDisplayList_InlineImage_ReadsInterpolateFlag(string interpolateEntry, bool expected)
    {
        var header = Encoding.ASCII.GetBytes("q 20 0 0 20 0 0 cm BI /W 1 /H 1 /BPC 8 /CS /G " + interpolateEntry + "ID ");
        var content = header.Concat(new byte[] { 0x80 }).Concat(Encoding.ASCII.GetBytes(" EI Q")).ToArray();

        var displayList = RasterInterpreter.BuildDisplayList(content, resources: null, PdfMatrix.Identity, PdfOptions.Default, diagnostics: null, imageResolver: StubResolver());

        var image = Assert.Single(displayList.Children.OfType<ImagePageObject>());
        Assert.Equal(expected, image.Interpolate);
    }

    /// <summary>
    /// `/Interpolate` is a hint, so reading it must never change what a
    /// document reports or whether it renders. A dangling indirect reference (an object number
    /// absent from the xref, referenced by nothing else) used to be resolved through the registry,
    /// adding `PLUME2060` to <c>doc.Diagnostics</c> and THROWING under <c>Strict</c> — on a file
    /// main rendered silently. It now reads as <see langword="false"/> with no diagnostic.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DanglingIndirectInterpolate_ReadsFalse_NoDiagnostic_NoStrictThrow(bool strict)
    {
        var pdf = BuildPdfWithImageEntry("/Interpolate 99 0 R");
        using var document = PdfDocument.Open(pdf, PdfOptions.Default with { Strict = strict });
        var frame = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { PixelWidth = 40, PixelHeight = 40, Dpi = null }).Frames[0];

        Assert.Empty(document.Diagnostics);
        Assert.Equal(40, frame.Width);
    }

    private static byte[] BuildPdfWithImageEntry(string extraImageEntry)
    {
        var image = new byte[] { 0x00, 0xFF, 0x80, 0x40 }; // 2x2 DeviceGray 8 bpc, no filter
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 40 40] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 30 >>\nstream\nq 40 0 0 40 0 0 cm /Im0 Do Q     \nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 {extraImageEntry} /Length 4 >>\nstream\n" + Encoding.Latin1.GetString(image) + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        }

        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
