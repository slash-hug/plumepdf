using System.Text;
using PlumePdf.Elements;
using VerifyXunit;
using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// Cookbook recipe backing test for <c>docs/cookbook/compress.md</c>: a lossless-only call,
/// then the image levers with the result read to tune them. The source is composed here — a
/// 900-pixel noisy image drawn two inches wide (450 DPI) — so the recipe has no external
/// fixture. Byte counts vary with the zlib build, so the snapshot records comparisons, not sizes.
/// </summary>
public partial class CookbookTests
{
    [Fact]
    public Task Compress()
    {
        var report = new StringBuilder();
        BuildScanLikeSource("output/scan.pdf");

        // begin-snippet: compress-lossless
        // No options: lossless only. Unfiltered streams are Flated, objects packed, identical
        // streams written once; no image's samples change.
        var lossless = Pdf.Compress("output/scan.pdf", "output/scan-lossless.pdf");

        report.AppendLine($"Lossless smaller: {lossless.OutputBytes < lossless.InputBytes}");
        report.AppendLine($"Lossless image actions: {string.Join(", ", lossless.Images.Select(static i => i.Action))}");
        report.AppendLine($"No-lever hint (PLUME6090): {lossless.Diagnostics.Any(static d => d.Code == "PLUME6090")}");
        // end-snippet

        // begin-snippet: compress-images
        // Opt in to the lossy levers: downsample images drawn above 1.5 x 200 DPI to 200 DPI,
        // and re-encode them as JPEG at quality 70. A new encoding that isn't smaller is
        // dropped (KeptOriginal), and every image's fate is in result.Images.
        var result = Pdf.Compress("output/scan.pdf", "output/scan-small.pdf", new PdfCompressOptions
        {
            Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
        });

        report.AppendLine($"Saved bytes: {result.SavedBytes > 0}");
        foreach (var group in result.Images.GroupBy(static i => (i.Action, i.SkipReason)))
        {
            report.AppendLine($"{group.Key.Action} {group.Key.SkipReason}: {group.Count()}");
        }

        var image = result.Images[0];
        report.AppendLine($"{image.WidthBefore}x{image.HeightBefore} at {image.EffectiveDpiBefore:F0} DPI -> " +
            $"{image.WidthAfter}x{image.HeightAfter} at {image.EffectiveDpiAfter:F0} DPI");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    private static void BuildScanLikeSource(string path)
    {
        const int pixels = 900;
        var random = new Random(7);
        var samples = new byte[pixels * pixels * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)Math.Clamp(((i / 3 % pixels) * 255 / pixels) + random.Next(-12, 13), 0, 255);
        }

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(samples, pixelWidth: pixels, pixelHeight: pixels) { Width = 144, Height = 144 });
        });
        document.Save(path);
    }
}
