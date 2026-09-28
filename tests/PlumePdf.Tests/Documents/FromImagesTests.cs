using PlumePdf.Filters.Png;
using Xunit;

namespace PlumePdf.Tests.Documents;

/// <summary>
/// Coverage for <see cref="Pdf.FromImages(IEnumerable{string},PdfOptions?)"/> and its
/// sibling overloads: DPI-derived page sizes (page-size assertions use a one-decimal-place
/// tolerance, not exact equality — PNG's own <c>pHYs</c> chunk stores resolution as integer
/// pixels-per-meter, ISO/IEC 15948 §11.3.5.3, so a round-trip through <see cref="PngEncoder"/>
/// then <see cref="PngDecoder"/> recovers a DPI value fractionally off the original, e.g. 300
/// -> 11811 ppm -> 299.9994... — a PNG format property, not a PlumePdf rounding bug), the
/// PDF/A coded refusal, batch degradation (a source that fails to decode is skipped with a
/// diagnostic unless it's the only source or <see cref="PdfOptions.Strict"/> is set), the
/// output-path collision guard, and a clean extraction round-trip through
/// <see cref="ManuscriptRenderer"/>'s grayscale/<c>SMask</c> payload variants, exercised
/// end to end here.
/// </summary>
public class FromImagesTests
{
    private static byte[] Rgb24Png(int width, int height, double dpi)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 61) % 256);
        }

        var frame = new RasterImageFrame(pixels, width, height, RasterPixelFormat.Rgb24, dpi, dpi);
        return PngEncoder.Encode(frame, PdfOptions.Default);
    }

    private static byte[] Gray8Png(int width, int height, double dpi)
    {
        var pixels = new byte[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 23) % 256);
        }

        var frame = new RasterImageFrame(pixels, width, height, RasterPixelFormat.Gray8, dpi, dpi);
        return PngEncoder.Encode(frame, PdfOptions.Default);
    }

    private static byte[] Rgba32Png(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)(x * 50);
                pixels[i + 1] = (byte)(y * 50);
                pixels[i + 2] = 128;
                pixels[i + 3] = (byte)((x + y) % 2 == 0 ? 255 : 64);
            }
        }

        var frame = new RasterImageFrame(pixels, width, height, RasterPixelFormat.Rgba32);
        return PngEncoder.Encode(frame, PdfOptions.Default);
    }

    [Fact]
    public void SingleImage_ProducesOnePageAtExactDpiDerivedSize()
    {
        // A 300-dpi, 900x1200-pixel source is US Letter proportions at 300 dpi — 216x288
        // points, up to the pHYs integer-pixels-per-meter rounding this class's own remarks
        // explain (well within LayoutEngine.MeasureImage's own +0.01pt fit-check epsilon).
        using var document = Pdf.FromImages([(ReadOnlyMemory<byte>)Rgb24Png(900, 1200, 300)]);

        var page = Assert.Single(document.Pages);
        var (width, height) = MediaBoxSize(page);
        Assert.Equal(216.0, width, precision: 1);
        Assert.Equal(288.0, height, precision: 1);
    }

    [Fact]
    public void MultipleImages_ProduceOnePagePerImage_InOrder()
    {
        using var document = Pdf.FromImages(
        [
            (ReadOnlyMemory<byte>)Rgb24Png(100, 50, 96),
            (ReadOnlyMemory<byte>)Rgb24Png(200, 400, 96),
        ]);

        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(75.0, MediaBoxSize(document.Pages[0]).Width, precision: 1); // 100px @ 96dpi -> 75pt
        Assert.Equal(150.0, MediaBoxSize(document.Pages[1]).Width, precision: 1); // 200px @ 96dpi -> 150pt
    }

    [Fact]
    public void NoDpiDeclared_FallsBackTo96Dpi()
    {
        var pixels = new byte[96 * 96 * 3];
        var frame = new RasterImageFrame(pixels, 96, 96, RasterPixelFormat.Rgb24); // no DPI
        var png = PngEncoder.Encode(frame, PdfOptions.Default);

        using var document = Pdf.FromImages([(ReadOnlyMemory<byte>)png]);

        Assert.Equal(72.0, MediaBoxSize(document.Pages[0]).Width, precision: 1); // 96px @ 96dpi fallback -> 72pt
    }

    private static (double Width, double Height) MediaBoxSize(PdfPage page)
    {
        var mediaBox = (PdfArray)page.Dictionary[PdfName.Get("MediaBox")];
        var llx = ((PdfNumber)mediaBox[0]).Value;
        var lly = ((PdfNumber)mediaBox[1]).Value;
        var urx = ((PdfNumber)mediaBox[2]).Value;
        var ury = ((PdfNumber)mediaBox[3]).Value;
        return (urx - llx, ury - lly);
    }

    [Fact]
    public void EmptySourceList_ThrowsPlume3611()
    {
        var ex = Assert.Throws<PlumePdfException>(() => Pdf.FromImages(Array.Empty<string>()));
        Assert.Equal("PLUME3611", ex.Code);
    }

    [Fact]
    public void PdfAConformanceRequested_ThrowsPlume3610()
    {
        var ex = Assert.Throws<PlumePdfException>(() =>
            Pdf.FromImages([(ReadOnlyMemory<byte>)Rgb24Png(10, 10, 96)], PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b }));
        Assert.Equal("PLUME3610", ex.Code);
    }

    [Fact]
    public void SingleUndecodableSource_AlwaysThrows_EvenNonStrict()
    {
        byte[] garbage = [1, 2, 3, 4];
        var ex = Assert.Throws<PlumePdfException>(() => Pdf.FromImages([(ReadOnlyMemory<byte>)garbage]));
        Assert.Equal("PLUME3600", ex.Code);
    }

    [Fact]
    public void MultiSourceBatch_SkipsUndecodableSourceWithDiagnostic_ByDefault()
    {
        byte[] garbage = [1, 2, 3, 4];
        using var document = Pdf.FromImages(
        [
            (ReadOnlyMemory<byte>)Rgb24Png(10, 10, 96),
            (ReadOnlyMemory<byte>)garbage,
            (ReadOnlyMemory<byte>)Rgb24Png(20, 20, 96),
        ]);

        Assert.Equal(2, document.Pages.Count);
        Assert.Contains(document.Diagnostics, d => d.Code == "PLUME3614");
    }

    [Fact]
    public void MultiSourceBatch_UnderStrict_ThrowsInsteadOfSkipping()
    {
        byte[] garbage = [1, 2, 3, 4];
        var ex = Assert.Throws<PlumePdfException>(() => Pdf.FromImages(
            [(ReadOnlyMemory<byte>)Rgb24Png(10, 10, 96), (ReadOnlyMemory<byte>)garbage],
            PdfOptions.Default with { Strict = true }));
        Assert.Equal("PLUME3600", ex.Code);
    }

    [Fact]
    public void OutputPathEqualsInputPath_ThrowsPlume3612()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-fromimages-collide-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, Rgb24Png(10, 10, 96));
            var ex = Assert.Throws<PlumePdfException>(() => Pdf.FromImages([path], path));
            Assert.Equal("PLUME3612", ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PathOutputOverload_WritesAFileThatOpensBack()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"plumepdf-fromimages-src-{Guid.NewGuid():N}.png");
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-fromimages-out-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(imagePath, Rgb24Png(50, 50, 96));
            Pdf.FromImages([imagePath], outputPath);

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(imagePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void GrayscaleSource_RoundTripsThroughExtractImages_AsDeviceGray()
    {
        using var document = Pdf.FromImages([(ReadOnlyMemory<byte>)Gray8Png(4, 4, 96)]);

        var extracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.Equal("DeviceGray", extracted.ColorSpaceName);
        Assert.Equal(8, extracted.BitsPerComponent);
        Assert.False(extracted.IsRawEncoded);

        var original = RasterImage.Decode(Gray8Png(4, 4, 96)).Frames[0].Pixels.ToArray();
        Assert.Equal(original, extracted.Data.ToArray());
    }

    [Fact]
    public void RgbaSource_RoundTripsAsRgbPlusSoftMask()
    {
        using var document = Pdf.FromImages([(ReadOnlyMemory<byte>)Rgba32Png(4, 4)]);

        var extracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.Equal("DeviceRGB", extracted.ColorSpaceName);
        Assert.NotNull(extracted.SoftMaskReference);
    }
}
