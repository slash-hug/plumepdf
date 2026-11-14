using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// A transparency group whose content paints nothing composites nothing: the page renders as if
/// the group were not there. Its device bounds used to come back as the
/// <c>int.MaxValue</c>/<c>int.MinValue</c> sentinels, and <c>MinValue - MaxValue</c> overflows to
/// 1, so the empty-group guard let a region at <c>int.MaxValue</c> through to the backdrop capture
/// (non-isolated) or the unpremultiply sweep (isolated), which threw
/// <see cref="ArgumentOutOfRangeException"/>.
/// </summary>
public class EmptyTransparencyGroupTests
{
    private const string EmptyContent = "/Tx BMC q Q EMC";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnEmptyGroup_PaintedFromPageContent_RendersAsIfAbsent(bool isolated, bool knockout)
    {
        var withGroup = Render(Page("/Fm0 Do", Group(EmptyContent, isolated, knockout)));
        var without = Render(Page(string.Empty, Group(EmptyContent, isolated, knockout)));

        Assert.Equal(without, withGroup);
    }

    [Fact]
    public void AnEmptyGroup_InsideAGroupThatPaints_LeavesThatGroupsOutputUnchanged()
    {
        // The outer group paints a square and also draws the empty inner group.
        var nested = Render(Page("/Fm0 Do", Group("q 0 0 1 rg 100 100 20 20 re f Q /Fm1 Do", isolated: false, knockout: false, inner: Group(EmptyContent, isolated: true, knockout: false))));
        var alone = Render(Page("/Fm0 Do", Group("q 0 0 1 rg 100 100 20 20 re f Q", isolated: false, knockout: false)));

        Assert.Equal(alone, nested);
    }

    [Fact]
    public void AnEmptyGroup_AsAnAnnotationAppearance_RendersAsIfAbsent()
    {
        var options = PdfRasterizeOptions.Default with { Dpi = 72, RenderAnnotations = true };

        var withAnnotation = Render(FreeTextWithEmptyGroupAppearance(), options);
        var plain = Render(Page(string.Empty, Group(EmptyContent, isolated: false, knockout: false)), options);

        Assert.Equal(plain, withAnnotation);
    }

    private static byte[] Render(byte[] pdf, PdfRasterizeOptions? options = null)
    {
        using var document = PdfDocument.Open(pdf);
        return document.Pages[0].Rasterize(options ?? PdfRasterizeOptions.Default with { Dpi = 72 }).Frames[0].Pixels.ToArray();
    }

    private static string Group(string content, bool isolated, bool knockout, string? inner = null) =>
        $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /I {(isolated ? "true" : "false")} /K {(knockout ? "true" : "false")} >>"
        + (inner is null ? string.Empty : " /Resources << /XObject << /Fm1 6 0 R >> >>")
        + $" /Length {content.Length} >>\nstream\n{content}\nendstream"
        + (inner is null ? string.Empty : "\u0000" + inner);

    // A one-page document whose content is pageContent, with form XObject 5 (and, when the group
    // string carries one after a NUL separator, its nested group 6) available as /Fm0.
    private static byte[] Page(string pageContent, string group)
    {
        var parts = group.Split('\u0000');
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Fm0 5 0 R >> >> >>"),
            (4, $"<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream"),
            (5, parts[0]),
        };
        if (parts.Length > 1)
        {
            objects.Add((6, parts[1]));
        }

        return PdfFixtureBuilder.BuildClassicXrefPdf(objects, rootObjectNumber: 1);
    }

    // The issue's annotation shape: a /FreeText whose normal appearance draws, through a
    // /Multiply ExtGState, a non-isolated group whose content paints nothing.
    private static byte[] FreeTextWithEmptyGroupAppearance() => PdfFixtureBuilder.BuildClassicXrefPdf(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> /Annots [8 0 R] >>"),
            (4, "<< /Length 0 >>\nstream\n\nendstream"),
            (5, $"<< /Type /XObject /Subtype /Form /BBox [100 100 104 108] /Matrix [1 0 0 1 -100 -100] /Resources << /ExtGState << /GS0 7 0 R >> /XObject << /Fm0 6 0 R >> >> /Length 15 >>\nstream\n/GS0 gs /Fm0 Do\nendstream"),
            (6, $"<< /Type /XObject /Subtype /Form /BBox [100 100 104 108] /Group << /S /Transparency /I false /K false >> /Length {EmptyContent.Length} >>\nstream\n{EmptyContent}\nendstream"),
            (7, "<< /Type /ExtGState /BM /Multiply >>"),
            (8, "<< /Type /Annot /Subtype /FreeText /Rect [100 100 104 108] /F 4 /BM /Multiply /DA (0 0 1 rg) /AP << /N 5 0 R >> >>"),
        ],
        rootObjectNumber: 1);
}
