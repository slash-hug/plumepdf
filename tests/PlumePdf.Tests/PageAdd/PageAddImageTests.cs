using System.Text;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// Image pages (<see cref="PageCollection.AddImage"/>, <see cref="PageCollection.InsertImage"/>):
/// built the way <see cref="Pdf.FromImages(IEnumerable{RasterImage}, PdfOptions?)"/> builds a
/// page (size from DPI, Flate pixels, alpha as a soft mask, a decoded JPEG kept as DCT), marked as
/// an artifact in a tagged document, and fine in a PDF/A one.
/// </summary>
public class PageAddImageTests
{
    private static readonly PdfName MediaBox = PdfName.Get("MediaBox");
    private static readonly PdfName Resources = PdfName.Get("Resources");
    private static readonly PdfName XObject = PdfName.Get("XObject");
    private static readonly PdfName Filter = PdfName.Get("Filter");
    private static readonly PdfName SMask = PdfName.Get("SMask");
    private static readonly PdfName Contents = PdfName.Get("Contents");

    [Theory]
    [InlineData(null, 72.0, 36.0)]   // 96 dpi when the frame has none
    [InlineData(192.0, 36.0, 18.0)]
    public void ThePageIsTheFramesSizeAtItsDpi(double? dpi, double width, double height)
    {
        using var document = PdfDocument.Open(Text(["One"]));
        var frame = new RasterImageFrame(new byte[96 * 48 * 3], 96, 48, RasterPixelFormat.Rgb24, dpi, dpi);

        var page = document.Pages.AddImage(frame);

        var box = (PdfArray)page.Dictionary[MediaBox];
        Assert.Equal(width, ((PdfNumber)box[2]).Value, precision: 3);
        Assert.Equal(height, ((PdfNumber)box[3]).Value, precision: 3);
        Assert.Equal(2, document.Pages.Count);
    }

    [Fact]
    public void InsertImage_PutsThePageAtTheIndex()
    {
        using var document = PdfDocument.Open(Text(["One", "Two"]));

        var page = document.Pages.InsertImage(1, new RasterImageFrame(new byte[3], 1, 1, RasterPixelFormat.Rgb24));

        Assert.Same(page, document.Pages[1]);
        Assert.Equal(3, document.Pages.Count);
    }

    [Fact]
    public void AnAlphaChannel_BecomesASoftMask_AndThePixelsAreFlateCompressed()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        var frame = new RasterImageFrame(new byte[] { 255, 0, 0, 128, 0, 255, 0, 255 }, 2, 1, RasterPixelFormat.Rgba32);

        var page = document.Pages.AddImage(frame);

        var image = Image(document, page);
        Assert.Equal("FlateDecode", ((PdfName)image.Dictionary[Filter]).Value);
        Assert.True(image.Dictionary.ContainsKey(SMask));
    }

    [Fact]
    public void ADecodedJpeg_KeepsItsJpegBytes()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        var jpeg = File.ReadAllBytes(Fixture(Path.Combine("jpeg", "baseline-420-q90.jpg")));
        var frame = RasterImage.Decode(jpeg).Frames[0];

        var page = document.Pages.AddImage(frame);

        var image = Image(document, page);
        Assert.Equal("DCTDecode", ((PdfName)image.Dictionary[Filter]).Value);
        Assert.Equal(jpeg, image.RawBytes.ToArray());
    }

    [Fact]
    public void InATaggedDocument_TheImageIsAnArtifact_AndTheClaimIsReported()
    {
        using var document = PdfDocument.Open(Text(["One"], catalogExtra: "/StructTreeRoot << /Type /StructTreeRoot >> /MarkInfo << /Marked true >>"));

        var page = document.Pages.AddImage(new RasterImageFrame(new byte[3], 1, 1, RasterPixelFormat.Rgb24));

        var content = Encoding.Latin1.GetString(ContentBytes(document, page)).Trim();
        Assert.StartsWith("/Artifact BMC", content, StringComparison.Ordinal);
        Assert.EndsWith("EMC", content, StringComparison.Ordinal);
        Assert.Single(document.Diagnostics, static d => d.Code == "PLUME6095");
        Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME6093");
    }

    [Fact]
    public void InAnUntaggedDocument_TheImageIsNotWrapped()
    {
        using var document = PdfDocument.Open(Text(["One"]));

        var page = document.Pages.AddImage(new RasterImageFrame(new byte[3], 1, 1, RasterPixelFormat.Rgb24));

        Assert.DoesNotContain("Artifact", Encoding.Latin1.GetString(ContentBytes(document, page)), StringComparison.Ordinal);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void InAPdfADocument_ThePageIsAdded()
    {
        using var document = PdfDocument.Open(Text(["One"]), new PdfOptions { PdfAConformance = PdfAConformance.A2b });

        document.Pages.AddImage(new RasterImageFrame(new byte[3], 1, 1, RasterPixelFormat.Rgb24));

        Assert.Equal(2, document.Pages.Count);
    }

    [Fact]
    public void TheSavedPage_RasterizesToTheFramesPixels()
    {
        var input = WriteTemp(Text(["One"]));
        var output = TempPath();
        var pixels = new byte[4 * 2 * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 10);
        }

        try
        {
            using (var document = PdfDocument.Open(input))
            {
                document.Pages.AddImage(new RasterImageFrame(pixels, 4, 2, RasterPixelFormat.Rgb24));
                document.SaveIncremental(output);
            }

            using var reopened = PdfDocument.Open(output);
            var raster = reopened.Pages[1].Rasterize(new PdfRasterizeOptions { Dpi = 96 }).Frames[0];
            Assert.Equal(4, raster.Width);
            Assert.Equal(2, raster.Height);
            Assert.Equal(pixels, Rgb(raster));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static byte[] Rgb(RasterImageFrame frame)
    {
        var source = frame.Pixels.Span;
        var stride = frame.Format == RasterPixelFormat.Rgba32 ? 4 : 3;
        var rgb = new byte[frame.Width * frame.Height * 3];
        for (int i = 0, j = 0; j < rgb.Length; i += stride, j += 3)
        {
            rgb[j] = source[i];
            rgb[j + 1] = source[i + 1];
            rgb[j + 2] = source[i + 2];
        }

        return rgb;
    }

    private static PdfStream Image(PdfDocument document, PdfPage page)
    {
        var xobjects = (PdfDictionary)Resolve(document, ((PdfDictionary)Resolve(document, page.Dictionary[Resources]))[XObject]);
        return (PdfStream)Resolve(document, xobjects.Single().Value);
    }

    private static byte[] ContentBytes(PdfDocument document, PdfPage page) =>
        ((PdfStream)Resolve(document, page.Dictionary[Contents])).GetDecodedBytes(PdfFilterRegistry.Default).ToArray();

    private static PdfObject Resolve(PdfDocument document, PdfObject value) => value is PdfReference r ? document.Objects[r.Target] : value;

    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return Path.Combine(dir.FullName, "tests", "PlumePdf.CorpusTests", "Fixtures", name);
            }
        }

        throw new InvalidOperationException("PlumePdf.sln not found.");
    }
}
