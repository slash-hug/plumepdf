using System.Text;
using VerifyXunit;
using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// The Phase 7 (raster codecs / image-to-PDF) cookbook recipes: <c>decode-image.md</c> and
/// <c>from-images.md</c>. Same snapshot-verified-per-recipe shape as
/// <see cref="CookbookTests"/> (see that type's own remarks); split into this partial-class
/// file rather than appended to the phase-1-6.5 file it would otherwise have grown into.
/// </summary>
public partial class CookbookTests
{
    private static void WritePngFixture(string path, int width, int height, byte[] rgb)
    {
        var frame = new RasterImageFrame(rgb, width, height, RasterPixelFormat.Rgb24, xDpi: 300, yDpi: 300);
        File.WriteAllBytes(path, frame.EncodePng());
    }

    [Fact]
    public Task DecodeImage()
    {
        var report = new StringBuilder();

        WritePngFixture("output/photo.png", 2, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);

        // begin-snippet: decode-image
        var image = RasterImage.Decode(File.ReadAllBytes("output/photo.png"));
        // or: RasterImage.Decode("output/photo.png") — the path overload reads the file for you.

        var frame = image.Frames[0];
        report.AppendLine($"{frame.Width}x{frame.Height} {frame.Format} @ {frame.XDpi:0}x{frame.YDpi:0} dpi");

        // Diagnostics is result-scoped, like PdfPage.ExtractImagesWithDiagnostics — never a
        // shared document-wide collection, since there is no document here.
        report.AppendLine($"Diagnostics: {image.Diagnostics.Count()}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task DecodeImage_ReEncode()
    {
        var report = new StringBuilder();

        WritePngFixture("output/reencode-source.png", 2, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);
        var frame = RasterImage.Decode(File.ReadAllBytes("output/reencode-source.png")).Frames[0];

        // begin-snippet: decode-image-reencode
        byte[] png = frame.EncodePng();             // 8-bit gray/RGB/RGBA, pHYs from the frame's DPI
        byte[] jpeg = frame.EncodeJpeg(quality: 90); // baseline JPEG, 4:2:0 chroma subsampling
        // end-snippet

        report.AppendLine($"PNG: {png.Length} bytes");
        report.AppendLine($"JPEG: {jpeg.Length} bytes");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task DecodeImage_ResourceLimit()
    {
        var report = new StringBuilder();

        WritePngFixture("output/resource-limit-source.png", 4, 4, new byte[4 * 4 * 3]);
        var bytes = File.ReadAllBytes("output/resource-limit-source.png");

        // begin-snippet: decode-image-resource-limit
        // PdfOptions.MaxImagePixels (default 1 << 27, ~134M pixels) guards every Phase 7
        // codec against a decompression-bomb-shaped input — a tiny file whose header
        // declares an enormous raster. Tighten it for untrusted input:
        var strictCap = PdfOptions.Default with { MaxImagePixels = 8 }; // this 4x4 photo has 16.
        try
        {
            RasterImage.Decode(bytes, strictCap);
            report.AppendLine("decoded (unexpected)");
        }
        catch (PlumePdfException ex)
        {
            report.AppendLine($"refused: {ex.Code}");
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task FromImages_PathOverload()
    {
        var report = new StringBuilder();

        WritePngFixture("output/page-01.png", 2, 2, [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0]);
        WritePngFixture("output/page-02.png", 2, 2, [0, 255, 255, 255, 0, 255, 255, 255, 0, 0, 0, 0]);

        // begin-snippet: from-images
        using var scan = Pdf.FromImages(["output/page-01.png", "output/page-02.png"]);
        scan.Save("output/scan.pdf");

        // Or write straight to a file in one call:
        Pdf.FromImages(["output/page-01.png", "output/page-02.png"], "output/scan-direct.pdf");
        // end-snippet

        report.AppendLine($"Pages: {scan.Pages.Count}");
        using var reopened = PdfDocument.Open("output/scan-direct.pdf");
        report.AppendLine($"Reopened pages: {reopened.Pages.Count}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task FromImages_BytesAndDecodedOverloads()
    {
        var report = new StringBuilder();

        WritePngFixture("output/bytes-overload-1.png", 2, 2, new byte[2 * 2 * 3]);
        WritePngFixture("output/bytes-overload-2.png", 2, 2, new byte[2 * 2 * 3]);
        IReadOnlyList<ReadOnlyMemory<byte>> imageByteArrays =
        [
            File.ReadAllBytes("output/bytes-overload-1.png"),
            File.ReadAllBytes("output/bytes-overload-2.png"),
        ];
        IReadOnlyList<RasterImage> decodedRasterImages = [.. imageByteArrays.Select(static b => RasterImage.Decode(b))];

        // begin-snippet: from-images-bytes-decoded
        using var fromBytes = Pdf.FromImages(imageByteArrays);       // IEnumerable<ReadOnlyMemory<byte>>
        using var fromDecoded = Pdf.FromImages(decodedRasterImages); // IEnumerable<RasterImage>
        // end-snippet

        report.AppendLine($"fromBytes pages: {fromBytes.Pages.Count}");
        report.AppendLine($"fromDecoded pages: {fromDecoded.Pages.Count}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task FromImages_BatchDegradation()
    {
        var report = new StringBuilder();

        WritePngFixture("output/good.png", 2, 2, new byte[2 * 2 * 3]);
        File.WriteAllBytes("output/corrupt.png", "not a png"u8.ToArray());
        WritePngFixture("output/also-good.png", 2, 2, new byte[2 * 2 * 3]);

        // begin-snippet: from-images-batch-degradation
        using var scan = Pdf.FromImages(["output/good.png", "output/corrupt.png", "output/also-good.png"]);
        report.AppendLine($"Pages: {scan.Pages.Count}");
        foreach (var d in scan.Diagnostics)
        {
            report.AppendLine($"{d.Code}: source skipped"); // PLUME3614 names which one and why.
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }
}
