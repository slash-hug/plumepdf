using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The armed oracle gate for the non-embedded symbol-font fixture: <c>symbol-fonts.pdf</c> —
/// non-embedded <c>Symbol</c> and <c>ZapfDingbats</c> page text plus an on checkbox whose
/// appearance is the standard <c>/ZaDb … (4) Tj</c> check — rendered <em>form-aware</em> against
/// PDFium's <c>--render-forms</c> output. The plain SSIM theory in <see cref="RasterOracleTests"/>
/// cannot fail for this fixture (a mostly-white page clears the 0.90 floor even when every glyph
/// is missing), so this test asserts what the feature actually changes: ink inside each glyph
/// and checkbox rectangle, a strictly better SSIM than a blank page, and the floor. The
/// non-form-aware leg pins that a plain <c>Rasterize()</c> still paints page text but no widget.
/// </summary>
public class SymbolFontOracleTests
{
    private const int PixelWidth = 800;
    private const double PageWidth = 200; // symbol-fonts.pdf MediaBox is 200 x 100 pt.
    private const double PageHeight = 100;

    // User-space boxes around each element (see Fixtures/generate_fixtures.py build_symbol_fonts).
    private static readonly (string Name, double X0, double Y0, double X1, double Y1) HelloBox = ("Helvetica 'Hello' (control)", 10, 78, 45, 92);
    private static readonly (string Name, double X0, double Y0, double X1, double Y1) SymbolBox = ("Symbol 'abg' → αβγ", 10, 46, 60, 66);
    private static readonly (string Name, double X0, double Y0, double X1, double Y1) DingbatBox = ("ZapfDingbats '4' → ✔ (named /Encoding ignored)", 80, 46, 100, 66);
    private static readonly (string Name, double X0, double Y0, double X1, double Y1) CheckboxBox = ("checkbox widget /ZaDb check", 150, 40, 168, 58);

    [Fact]
    public void SymbolFonts_FormAware_PaintsGlyphsAndCheckbox_AndBeatsBlank()
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (pdfium shim not installed at {PdfiumOracle.ShimPath})");
            return;
        }

        var pdfPath = Path.Combine(CorpusFixture.FixturesRoot, "symbol-fonts.pdf");
        Assert.True(File.Exists(pdfPath), "Curated fixture missing: symbol-fonts.pdf");

        var pdfiumPng = Path.Combine(Path.GetTempPath(), $"plumepdf-symbol-fonts-{Guid.NewGuid():N}.png");
        try
        {
            Assert.True(PdfiumOracle.RenderFormAware(pdfPath, 0, PixelWidth, pdfiumPng), "pdfium --render-forms failed to render symbol-fonts.pdf.");
            var pdfiumFrame = RasterImage.Decode(File.ReadAllBytes(pdfiumPng)).Frames[0];

            using var document = PdfDocument.Open(pdfPath);
            var ours = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with
            {
                PixelWidth = pdfiumFrame.Width,
                PixelHeight = pdfiumFrame.Height,
                Dpi = null,
                RenderAnnotations = true,
            }).Frames[0];

            // (1) The oracle itself paints every element — otherwise the comparison below is meaningless.
            foreach (var box in new[] { HelloBox, SymbolBox, DingbatBox, CheckboxBox })
            {
                Assert.True(InkPixels(pdfiumFrame, box) > 0, $"PDFium painted no ink for {box.Name} — the fixture or the oracle render is wrong.");
            }

            // (2) So do we — this is the assertion that failed on main (PLUME7729 skipped the runs).
            foreach (var box in new[] { HelloBox, SymbolBox, DingbatBox, CheckboxBox })
            {
                Assert.True(InkPixels(ours, box) > 0, $"PlumePDF painted no ink for {box.Name}.");
            }

            // (3) Strictly better than a blank page against the oracle (anti-vacuity), and above the floor.
            var blank = new RasterImageFrame(Enumerable.Repeat((byte)255, ours.Width * ours.Height * 4).ToArray(), ours.Width, ours.Height, RasterPixelFormat.Rgba32);
            var oursSsim = RasterSsim.ComputeFrames(ours, pdfiumFrame);
            var blankSsim = RasterSsim.ComputeFrames(blank, pdfiumFrame);
            Assert.True(oursSsim > blankSsim, $"Form-aware SSIM {oursSsim:0.0000} does not beat a blank page's {blankSsim:0.0000} — the gate would be vacuous.");
            var floor = RasterSsim.LoadCalibratedFloor();
            Assert.True(oursSsim >= floor, $"Form-aware SSIM {oursSsim:0.0000} is below the calibrated floor {floor:0.0000}.");
        }
        finally
        {
            File.Delete(pdfiumPng);
        }
    }

    [Fact]
    public void SymbolFonts_PlainRasterize_PaintsPageTextButNoWidget()
    {
        var pdfPath = Path.Combine(CorpusFixture.FixturesRoot, "symbol-fonts.pdf");
        var frame = Pdf.Rasterize(pdfPath, PdfRasterizeOptions.Default with { PixelWidth = PixelWidth, PixelHeight = PixelWidth / 2, Dpi = null }).Frames[0];

        Assert.True(InkPixels(frame, SymbolBox) > 0, "Symbol page text should paint without RenderAnnotations.");
        Assert.True(InkPixels(frame, DingbatBox) > 0, "ZapfDingbats page text should paint without RenderAnnotations.");
        Assert.Equal(0, InkPixels(frame, CheckboxBox)); // Widgets are annotations: off by default (RenderAnnotations = false).
    }

    private static int InkPixels(RasterImageFrame frame, (string Name, double X0, double Y0, double X1, double Y1) box)
    {
        var sx = frame.Width / PageWidth;
        var sy = frame.Height / PageHeight;
        var left = Math.Clamp((int)(box.X0 * sx), 0, frame.Width);
        var right = Math.Clamp((int)(box.X1 * sx), 0, frame.Width);
        var top = Math.Clamp((int)((PageHeight - box.Y1) * sy), 0, frame.Height);    // PDF y-up → raster y-down
        var bottom = Math.Clamp((int)((PageHeight - box.Y0) * sy), 0, frame.Height);

        var bpp = RasterImageFrame.BytesPerPixel(frame.Format);
        var pixels = frame.Pixels.Span;
        var ink = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                var o = ((y * frame.Width) + x) * bpp;
                var luma = bpp == 1 ? pixels[o] : (pixels[o] * 299 + pixels[o + 1] * 587 + pixels[o + 2] * 114) / 1000;
                if (luma < 200)
                {
                    ink++;
                }
            }
        }

        return ink;
    }
}
