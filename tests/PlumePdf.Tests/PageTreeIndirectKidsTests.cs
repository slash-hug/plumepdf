using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// A <c>/Type /Pages</c> node whose <c>/Kids</c> is written as an indirect reference
/// to an array object (<c>/Kids 8 0 R</c> → <c>8 0 obj [4 0 R] endobj</c>) is legal PDF
/// (ISO 32000-1 §7.3.10 — any value may be indirect) and is what two real-world producer
/// families emit; PDFium and pypdf resolve it. <c>PageTreeReader</c> matched only a direct
/// array, so every such document opened with zero pages and a <c>PLUME6010</c>.
/// </summary>
public class PageTreeIndirectKidsTests
{
    private static readonly (int, string)[] Common =
    [
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (3, "<< /Producer (test) >>"),
        (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>"),
    ];

    private static byte[] Build(string pagesBody, params (int, string)[] extra)
    {
        var objects = new List<(int, string)>(Common) { (2, pagesBody) };
        objects.AddRange(extra);
        return PdfFixtureBuilder.BuildClassicXrefPdf(objects, rootObjectNumber: 1);
    }

    [Fact]
    public void KidsAsIndirectArray_ResolvesToPages()
    {
        var bytes = Build("<< /Type /Pages /Count 1 /Kids 8 0 R >>", (8, "[4 0 R]"));

        using var doc = PdfDocument.Open(bytes);

        Assert.Single(doc.Pages);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "PLUME6010");
    }

    [Fact]
    public void KidsAsReferenceChain_ResolvesToPages()
    {
        // 8 0 obj → 9 0 R → [4 0 R]: an indirect object whose value is itself a reference.
        var bytes = Build("<< /Type /Pages /Count 1 /Kids 8 0 R >>", (8, "9 0 R"), (9, "[4 0 R]"));

        using var doc = PdfDocument.Open(bytes);

        Assert.Single(doc.Pages);
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == "PLUME6010");
    }

    [Fact]
    public void KidsAsIndirectNonArray_StillRecords6010()
    {
        var bytes = Build("<< /Type /Pages /Count 1 /Kids 8 0 R >>", (8, "<< /Not /AnArray >>"));

        using var doc = PdfDocument.Open(bytes);

        Assert.Empty(doc.Pages);
        Assert.Contains(doc.Diagnostics, d => d.Code == "PLUME6010");
    }

    [Fact]
    public void KidsAsReferenceCycle_DoesNotSpin_Records6010()
    {
        var bytes = Build("<< /Type /Pages /Count 1 /Kids 8 0 R >>", (8, "9 0 R"), (9, "8 0 R"));

        using var doc = PdfDocument.Open(bytes);

        Assert.Empty(doc.Pages);
        Assert.Contains(doc.Diagnostics, d => d.Code == "PLUME6010");
    }

    [Fact]
    public void KidsAsDirectArray_Unchanged()
    {
        var bytes = Build("<< /Type /Pages /Count 1 /Kids [4 0 R] >>");

        using var doc = PdfDocument.Open(bytes);

        Assert.Single(doc.Pages);
        Assert.Empty(doc.Diagnostics);
    }
}

/// <summary>
/// The same resolve-before-type-check defect for the page's
/// geometry. <c>/MediaBox 9 0 R</c> silently fell back to US Letter and <c>/Rotate 15 0 R</c> to 0
/// because the merged page dictionary handed the unresolved reference to <c>PageSpace</c>, which
/// has no registry access. An indirect <c>/Type</c> likewise slipped past the Pages/Page test and
/// emitted a phantom page.
/// </summary>
public class PageTreeIndirectGeometryTests
{
    private static byte[] Build(string pageBody, string pagesBody = "<< /Type /Pages /Count 1 /Kids [4 0 R] >>", params (int, string)[] extra)
    {
        var objects = new List<(int, string)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, pagesBody),
            (3, "<< /Producer (test) >>"),
            (4, pageBody),
        };
        objects.AddRange(extra);
        return PdfFixtureBuilder.BuildClassicXrefPdf(objects, rootObjectNumber: 1);
    }

    private static (int Width, int Height) RasterSize(byte[] bytes)
    {
        using var doc = PdfDocument.Open(bytes);
        var frame = doc.Pages[0].Rasterize(new PdfRasterizeOptions { Dpi = 72 }).Frames[0];
        return (frame.Width, frame.Height);
    }

    [Fact]
    public void MediaBoxAsIndirectArray_SizesThePage()
    {
        var bytes = Build("<< /Type /Page /Parent 2 0 R /MediaBox 9 0 R >>", extra: (9, "[0 0 200 100]"));
        Assert.Equal((200, 100), RasterSize(bytes));
    }

    [Fact]
    public void MediaBoxWithIndirectElements_SizesThePage()
    {
        var bytes = Build("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 9 0 R 10 0 R] >>", extra: [(9, "200"), (10, "100")]);
        Assert.Equal((200, 100), RasterSize(bytes));
    }

    [Fact]
    public void InheritedIndirectMediaBox_SizesThePage()
    {
        var bytes = Build("<< /Type /Page /Parent 2 0 R >>", "<< /Type /Pages /Count 1 /Kids [4 0 R] /MediaBox 9 0 R >>", (9, "[0 0 200 100]"));
        Assert.Equal((200, 100), RasterSize(bytes));
    }

    [Fact]
    public void RotateAsIndirectInteger_RotatesThePage()
    {
        var bytes = Build("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Rotate 15 0 R >>", extra: (15, "90"));
        Assert.Equal((100, 200), RasterSize(bytes));
    }

    [Fact]
    public void TypeAsIndirectName_PagesNodeWithoutKids_IsNotAPhantomPage()
    {
        var bytes = Build("<< /Type 13 0 R /Count 0 >>", extra: (13, "/Pages"));
        using var doc = PdfDocument.Open(bytes);
        Assert.Empty(doc.Pages);
        Assert.Contains(doc.Diagnostics, d => d.Code == "PLUME6010");
    }

    [Fact]
    public void DirectGeometry_Unchanged()
    {
        var bytes = Build("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Rotate 90 >>");
        Assert.Equal((100, 200), RasterSize(bytes));
    }
}
