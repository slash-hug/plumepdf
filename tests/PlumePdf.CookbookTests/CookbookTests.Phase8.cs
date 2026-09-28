using System.Text;
using PlumePdf.Filters;
using VerifyXunit;
using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// The Phase 8 (page rasterization) cookbook recipes: <c>rasterize-page.md</c> and
/// <c>rasterize-substitute-fonts.md</c>. Same snapshot-verified-per-recipe
/// shape as <see cref="CookbookTests"/>'s own remarks describe.
/// </summary>
public partial class CookbookTests
{
    [Fact]
    public Task RasterizePage()
    {
        var report = new StringBuilder();

        // begin-snippet: rasterize-page
        var image = Pdf.Rasterize("samples/classic-xref.pdf", PdfRasterizeOptions.Default with { Dpi = 150 });
        var frame = image.Frames[0];
        File.WriteAllBytes("output/page-0.png", frame.EncodePng());
        // end-snippet

        report.AppendLine($"{frame.Width}x{frame.Height} {frame.Format}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task RasterizePage_OpenDocumentDoor()
    {
        var report = new StringBuilder();

        using var document = PdfDocument.Open("samples/classic-xref.pdf");

        // begin-snippet: rasterize-page-open-door
        var options = PdfRasterizeOptions.Default with { PixelWidth = 200, PixelHeight = 100, Dpi = null };
        var image = document.Pages[0].Rasterize(options);
        // end-snippet

        report.AppendLine($"{image.Frames[0].Width}x{image.Frames[0].Height}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task RasterizePage_ResourceCaps()
    {
        var report = new StringBuilder();

        // begin-snippet: rasterize-page-resource-caps
        // PdfOptions.MaxRasterSurfaceBytes/MaxDisplayListObjects/MaxShadingSamples guard a
        // rasterize call the same way every other Phase 7 codec's Max* cap guards a decode —
        // a document/output-controlled dimension refused before it drives an allocation.
        var strictCap = PdfOptions.Default with { MaxRasterSurfaceBytes = 1024 }; // trivially exceeded
        using var document = PdfDocument.Open("samples/classic-xref.pdf", strictCap);
        try
        {
            document.Pages[0].Rasterize();
            report.AppendLine("rasterized (unexpected)");
        }
        catch (PlumePdfException ex)
        {
            report.AppendLine($"refused: {ex.Code}");
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task RasterizePage_ResamplingPoint()
    {
        var report = new StringBuilder();

        // begin-snippet: rasterize-page-resampling-point
        // Point (nearest-neighbour) keeps 1-bit/scanned content edge-sharp instead of blending
        // it with Auto's PDFium-parity box/bilinear filter — the edge-preserving choice for
        // signatures, stamps, and thin rule lines a downstream OCR/vision model should see crisp.
        var crisp = PdfRasterizeOptions.Default with { ImageResampling = ImageResamplingMode.Point };
        var image = Pdf.Rasterize("samples/classic-xref.pdf", crisp);
        // end-snippet

        report.AppendLine($"{image.Frames[0].Width}x{image.Frames[0].Height}, resampling={crisp.ImageResampling}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task RasterizePage_Aliased()
    {
        var report = new StringBuilder();

        // begin-snippet: rasterize-page-aliased
        // AntiAlias only governs vector/glyph coverage (fills, strokes, glyphs) — images still
        // resample smoothly under Auto/Box/Bilinear even with AntiAlias = false, and clip edges and
        // shadings stay smooth regardless. Pair it with Point for hard-edged vector AND image content.
        var hardEdged = PdfRasterizeOptions.Default with
        {
            ImageResampling = ImageResamplingMode.Point,
            AntiAlias = false,
        };
        var image = Pdf.Rasterize("samples/classic-xref.pdf", hardEdged);
        // end-snippet

        report.AppendLine($"{image.Frames[0].Width}x{image.Frames[0].Height}, antiAlias={hardEdged.AntiAlias}");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task RasterizeSubstituteFonts()
    {
        var report = new StringBuilder();

        // begin-snippet: rasterize-substitute-fonts
        // A page whose text uses a font not embedded in the source PDF still rasterizes
        // successfully, glyphs included — non-embedded text falls back to one of fourteen bundled
        // substitute faces (matched automatically by the font's declared bold/italic/serif/fixed-pitch
        // style, or to the Foxit Symbol/Dingbats face for a Standard-14 symbol font; no caller action
        // needed). Every other kind of content — vector paths, axial/radial shadings — renders
        // regardless of which fonts the page's text uses.
        var image = Pdf.Rasterize("samples/classic-xref.pdf");
        // end-snippet

        report.AppendLine($"Rendered {image.Frames[0].Width}x{image.Frames[0].Height} without needing any font embedded.");

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task RasterizeSymbolFonts()
    {
        var report = new StringBuilder();

        // begin-snippet: rasterize-symbol-fonts
        // Checkbox and radio "on" marks are drawn with the Standard-14 ZapfDingbats font, which is
        // never embedded; PlumePDF renders it (and Symbol) with PDFium's own Foxit faces. Widget
        // appearances are annotations, so ask for them — a plain Rasterize() paints page text only.
        var image = Pdf.Rasterize("samples/symbol-fonts.pdf", PdfRasterizeOptions.Default with { RenderAnnotations = true });
        var frame = image.Frames[0];

        // The sample's checkbox sits at [150 40 168 58] on a 200x100 pt page; look for dark pixels there.
        var checkMarkPainted = HasInk(frame, x0: 150, y0: 40, x1: 168, y1: 58, pageWidth: 200, pageHeight: 100);

        // Each substituted font records a PLUME7510 diagnostic naming the bundled face it used.
        var substitutedFaces = image.Diagnostics
            .Where(d => d.Code == "PLUME7510")
            .Select(d => d.Message.Contains("FoxitDingbats") ? "FoxitDingbats" : d.Message.Contains("FoxitSymbol") ? "FoxitSymbol" : "Liberation")
            .Distinct()
            .OrderBy(f => f, StringComparer.Ordinal);
        // end-snippet

        report.AppendLine($"Rendered {frame.Width}x{frame.Height} with RenderAnnotations = true.");
        report.AppendLine($"Check mark painted inside the checkbox: {checkMarkPainted}");
        report.AppendLine($"Substitute faces used: {string.Join(", ", substitutedFaces)}");

        return Verifier.Verify(report.ToString());
    }

    private static bool HasInk(RasterImageFrame frame, double x0, double y0, double x1, double y1, double pageWidth, double pageHeight)
    {
        var sx = frame.Width / pageWidth;
        var sy = frame.Height / pageHeight;
        var bpp = RasterImageFrame.BytesPerPixel(frame.Format);
        var pixels = frame.Pixels.Span;
        for (var y = (int)((pageHeight - y1) * sy); y < (int)((pageHeight - y0) * sy); y++)
        {
            for (var x = (int)(x0 * sx); x < (int)(x1 * sx); x++)
            {
                var o = ((y * frame.Width) + x) * bpp;
                if ((pixels[o] * 299 + pixels[o + 1] * 587 + pixels[o + 2] * 114) / 1000 < 200)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
