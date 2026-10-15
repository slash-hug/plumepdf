using System.Globalization;
using System.Text;
using PlumePdf.Documents.Compression;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// The placement scan: every image XObject a page paints is found with its effective DPI per
/// image axis, through forms, annotation appearances and soft-mask groups; placements whose
/// device size can't be measured (tiling patterns, Type 3 glyphs, a walk cut short) are marked
/// so, and the scan never keeps a stream payload in the document's object cache.
/// </summary>
public class PlacementScannerTests
{
    private const int Image = 10;

    [Fact]
    public void ScaledImage_GetsItsDpiPerAxis()
    {
        // 300x150 pixels on a 2 x 0.5 inch box: 150 DPI across, 300 DPI down.
        var pdf = new ScanTestPdf().Image(Image, 300, 150).Page(3, "q 144 0 0 36 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");

        var placement = Assert.Single(Scan(pdf).Images);

        Assert.Equal(Image, placement.Key);
        AssertMeasured(placement.Value, 150, 300, page: 0);
    }

    [Fact]
    public void RotatedImage_KeepsEachImageAxisWithItsOwnPixelCount()
    {
        // Rotated 90 degrees: the image's width now runs up the page and its height across it,
        // so each axis's DPI still comes from the edge that axis was mapped onto.
        var pdf = new ScanTestPdf().Image(Image, 300, 150).Page(3, "q 0 144 -36 0 100 100 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");

        AssertMeasured(Scan(pdf).Images[Image], 150, 300, page: 0);
    }

    [Fact]
    public void ShearedImage_MeasuresTheShearedEdge()
    {
        var pdf = new ScanTestPdf().Image(Image, 200, 200).Page(3, "q 72 0 36 72 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");

        // The vertical edge runs along (36, 72): sqrt(36^2 + 72^2) points long.
        AssertMeasured(Scan(pdf).Images[Image], 200, 200 * 72 / Math.Sqrt((36 * 36) + (72 * 72)), page: 0);
    }

    [Fact]
    public void AnisotropicImage_OnASquareBox_HasDifferentDpiPerAxis()
    {
        var pdf = new ScanTestPdf().Image(Image, 200, 100).Page(3, "q 100 0 0 100 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");

        AssertMeasured(Scan(pdf).Images[Image], 144, 72, page: 0);
    }

    [Fact]
    public void SameImageOnTwoPages_TheLowerDpiWins()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Page(3, "q 72 0 0 72 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R")
            .Page(5, "q 144 0 0 216 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");

        // Page 0 places it at 300 DPI; page 1 at 150 x 100.
        AssertMeasured(Scan(pdf).Images[Image], 150, 100, page: 1);
    }

    [Fact]
    public void LowestDpiIsKeptPerAxis_AcrossPlacements()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Page(3, "q 144 0 0 72 0 0 cm /Im0 Do Q q 72 0 0 144 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");

        AssertMeasured(Scan(pdf).Images[Image], 150, 150, page: 0);
    }

    [Fact]
    public void ImageInsideAForm_ComposesTheFormMatrix()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Stream(11, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Matrix [2 0 0 2 0 0] /Resources << /XObject << /Im0 {Image} 0 R >> >>", "/Im0 Do")
            .Page(3, "q 36 0 0 36 0 0 cm /Fm0 Do Q", xobjects: "/Fm0 11 0 R");

        // 36 points scaled by the form's 2x: one inch.
        var scan = Scan(pdf);

        AssertMeasured(scan.Images[Image], 300, 300, page: 0);
        Assert.False(scan.Images.ContainsKey(11));
    }

    [Fact]
    public void AnnotationAppearance_PlacesItsImageOnTheAnnotationRect()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 150)
            .Stream(11, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 {Image} 0 R >> >>", "/Im0 Do")
            .Object(12, "<< /Type /Annot /Subtype /Stamp /Rect [100 100 172 136] /AP << /N 11 0 R >> >>")
            .Page(3, string.Empty, extra: "/Annots [12 0 R]");

        AssertMeasured(Scan(pdf).Images[Image], 300, 300, page: 0);
    }

    [Fact]
    public void AnnotationAppearanceStates_AreAllPlaced()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Image(13, 300, 300)
            .Stream(11, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 {Image} 0 R >> >>", "/Im0 Do")
            .Stream(14, "/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 13 0 R >> >>", "/Im0 Do")
            .Object(12, "<< /Type /Annot /Subtype /Widget /AS /Off /Rect [0 0 72 72] /AP << /N << /On 11 0 R /Off 14 0 R >> >> >>")
            .Page(3, string.Empty, extra: "/Annots [12 0 R]");

        var scan = Scan(pdf);

        AssertMeasured(scan.Images[Image], 300, 300, page: 0);
        AssertMeasured(scan.Images[13], 300, 300, page: 0);
    }

    [Fact]
    public void SoftMaskGroup_PlacesItsImageWithTheCtmAtGs()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Stream(11, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Group << /S /Transparency >> /Resources << /XObject << /Im0 {Image} 0 R >> >>", "/Im0 Do")
            .Page(3, "q 72 0 0 72 0 0 cm /GS0 gs Q", extra: string.Empty, resources: "/ExtGState << /GS0 << /SMask << /S /Luminosity /G 11 0 R >> >> >>");

        AssertMeasured(Scan(pdf).Images[Image], 300, 300, page: 0);
    }

    [Fact]
    public void TilingPatternOnly_IsNotMeasured()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Stream(11, $"/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /XObject << /Im0 {Image} 0 R >> >>", "q 10 0 0 10 0 0 cm /Im0 Do Q")
            .Page(3, "/Pattern cs /P0 scn 0 0 100 100 re f", resources: "/Pattern << /P0 11 0 R >>");

        var placement = Scan(pdf).Images[Image];

        Assert.Equal(PlacementKind.TilingPatternOnly, placement.Kind);
        Assert.Null(placement.DpiX);
        Assert.Null(placement.DpiY);
        Assert.Equal(0, placement.PageIndex);
    }

    [Fact]
    public void TilingPatternAndPagePlacement_IsUnmeasured()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Stream(11, $"/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /XObject << /Im0 {Image} 0 R >> >>", "/Im0 Do")
            .Page(3, "q 72 0 0 72 0 0 cm /Im0 Do Q /Pattern cs /P0 scn 0 0 100 100 re f", xobjects: $"/Im0 {Image} 0 R", resources: "/Pattern << /P0 11 0 R >>");

        var placement = Scan(pdf).Images[Image];

        Assert.Equal(PlacementKind.Unmeasured, placement.Kind);
        Assert.Null(placement.DpiX);
    }

    [Fact]
    public void Type3Glyph_IsUnmeasured()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Stream(11, "", "0 0 d0 /Im0 Do")
            .Object(12, $"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << /a 11 0 R >> /Encoding << /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1] /Resources << /XObject << /Im0 {Image} 0 R >> >> >>")
            .Page(3, "BT /T3 72 Tf (a) Tj ET", resources: "/Font << /T3 12 0 R >>");

        Assert.Equal(PlacementKind.Unmeasured, Scan(pdf).Images[Image].Kind);
    }

    [Fact]
    public void SelfReferencingForm_TerminatesWithADiagnosticAndLeavesItsImagesUnmeasured()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Stream(11, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 {Image} 0 R /Fm0 11 0 R >> >>", "/Im0 Do 2 0 0 2 0 0 cm /Fm0 Do")
            .Page(3, "q 72 0 0 72 0 0 cm /Fm0 Do Q", xobjects: "/Fm0 11 0 R");

        var scan = Scan(pdf);

        Assert.Equal(PlacementKind.Unmeasured, scan.Images[Image].Kind);
        Assert.Contains(scan.Diagnostics, d => d.Code == "PLUME6025");
    }

    [Fact]
    public void FormNestingPastTheDepthCap_LeavesTheFormsImagesUnmeasured()
    {
        // Three nested forms; the deepest places a second image the cap stops the walk from
        // reaching, so every image the page can reach is treated as unmeasured.
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Image(13, 300, 300)
            .Stream(11, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 {Image} 0 R /Fm1 12 0 R >> >>", "/Im0 Do /Fm1 Do")
            .Stream(12, "/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Fm2 14 0 R >> >>", "/Fm2 Do")
            .Stream(14, "/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im1 13 0 R >> >>", "/Im1 Do")
            .Page(3, "q 72 0 0 72 0 0 cm /Fm0 Do Q", xobjects: "/Fm0 11 0 R");

        var scan = Scan(pdf, PdfOptions.Default with { MaxXObjectNestingDepth = 2 });

        Assert.Contains(scan.Diagnostics, d => d.Code == "PLUME6025");
        Assert.Equal(PlacementKind.Unmeasured, scan.Images[Image].Kind);
        Assert.Equal(PlacementKind.Unmeasured, scan.Images[13].Kind);
    }

    [Fact]
    public void UserUnit_ScalesThePhysicalSize()
    {
        // One user unit is 2/72 inch, so a 72-unit box is two inches.
        var pdf = new ScanTestPdf().Image(Image, 300, 300).Page(3, "q 72 0 0 72 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R", extra: "/UserUnit 2");

        AssertMeasured(Scan(pdf).Images[Image], 150, 150, page: 0);
    }

    [Fact]
    public void UnplacedImage_IsAbsent()
    {
        var pdf = new ScanTestPdf().Image(Image, 300, 300).Page(3, "0 0 10 10 re f", xobjects: $"/Im0 {Image} 0 R");

        Assert.Empty(Scan(pdf).Images);
    }

    [Fact]
    public void ImageOnlyOnARemovedPage_IsAbsent()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Image(13, 300, 300)
            .Page(3, "q 72 0 0 72 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R")
            .Page(5, "q 72 0 0 72 0 0 cm /Im0 Do Q", xobjects: "/Im0 13 0 R");
        using var document = PdfDocument.Open(pdf.Build());
        document.Pages.RemoveAt(0);

        var scan = PlacementScanner.Scan(document, PdfOptions.Default, CancellationToken.None);

        var placement = Assert.Single(scan.Images);
        Assert.Equal(13, placement.Key);
        Assert.Equal(0, placement.Value.PageIndex);
    }

    [Fact]
    public void InlineImages_AreRecordedByPage()
    {
        var pdf = new ScanTestPdf()
            .Page(3, "q 10 0 0 10 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 ID \u0001\u0002 EI Q")
            .Page(5, "BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI BI /W 1 /H 1 /CS /G /BPC 8 ID \u0002 EI");

        Assert.Equal([0, 1, 1], Scan(pdf).InlineImagePages);
    }

    [Fact]
    public void IndirectContentsArray_IsRead()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Stream(11, "", "q 72 0 0 72 0 0 cm")
            .Stream(12, "", "/Im0 Do Q")
            .Object(13, "[11 0 R 12 0 R]")
            .Page(3, null, xobjects: $"/Im0 {Image} 0 R", extra: "/Contents 13 0 R");

        AssertMeasured(Scan(pdf).Images[Image], 300, 300, page: 0);
    }

    [Fact]
    public void Scan_KeepsNoStreamInTheObjectCache()
    {
        // Every stream is padded past the head a dictionary read takes, so a cached one would
        // show in the count (a stream that small is kept by any read, by design).
        var pdf = new ScanTestPdf { MinimumStreamBytes = 8192 }
            .Image(Image, 300, 300)
            .Image(13, 300, 300)
            .Stream(11, $"/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 {Image} 0 R >> >>", "/Im0 Do")
            .Stream(12, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /XObject << /Im0 13 0 R >> >>", "/Im0 Do")
            .Page(3, "q 72 0 0 72 0 0 cm /Fm0 Do Q /Pattern cs /P0 scn 0 0 9 9 re f", xobjects: "/Fm0 11 0 R", resources: "/Pattern << /P0 12 0 R >>")
            .Page(5, "q 72 0 0 72 0 0 cm /Fm0 Do Q", xobjects: "/Fm0 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());
        var before = document.Objects.CachedObjectCount;

        var scan = PlacementScanner.Scan(document, PdfOptions.Default, CancellationToken.None);

        Assert.Equal(2, scan.Images.Count);
        Assert.Equal(before, document.Objects.CachedObjectCount);
    }

    [Fact]
    public void Scan_ChecksTheCancellationToken()
    {
        var pdf = new ScanTestPdf().Image(Image, 300, 300).Page(3, "q 72 0 0 72 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        Assert.Throws<OperationCanceledException>(() => PlacementScanner.Scan(document, PdfOptions.Default, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void MalformedContentUnderStrict_IsADiagnosticNotAThrow()
    {
        var pdf = new ScanTestPdf().Image(Image, 300, 300).Page(3, "q 72 0 0 72 0 0 cm /Im0 Do Q Q 1 2", xobjects: $"/Im0 {Image} 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        var scan = PlacementScanner.Scan(document, PdfOptions.Default with { Strict = true }, CancellationToken.None);

        Assert.NotEmpty(scan.Diagnostics);
        Assert.Equal(PlacementKind.Unmeasured, scan.Images[Image].Kind);
    }

    [Fact]
    public void DanglingXObjectReferenceUnderStrict_IsSkippedNotThrown()
    {
        var pdf = new ScanTestPdf()
            .Image(Image, 300, 300)
            .Page(3, "q 72 0 0 72 0 0 cm /Gone Do /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R /Gone 99 0 R");
        var strict = PdfOptions.Default with { Strict = true };
        using var document = PdfDocument.Open(pdf.Build(), strict);

        var scan = PlacementScanner.Scan(document, strict, CancellationToken.None);

        AssertMeasured(Assert.Single(scan.Images).Value, 300, 300, page: 0);
    }

    [Fact]
    public void DegeneratePlacement_IsUnmeasured()
    {
        var pdf = new ScanTestPdf().Image(Image, 300, 300).Page(3, "q 0 0 0 0 0 0 cm /Im0 Do Q", xobjects: $"/Im0 {Image} 0 R");

        Assert.Equal(PlacementKind.Unmeasured, Scan(pdf).Images[Image].Kind);
    }

    private static PlacementScan Scan(ScanTestPdf pdf, PdfOptions? options = null)
    {
        using var document = PdfDocument.Open(pdf.Build());
        return PlacementScanner.Scan(document, options ?? PdfOptions.Default, CancellationToken.None);
    }

    private static void AssertMeasured(ImagePlacementSummary placement, double dpiX, double dpiY, int page)
    {
        Assert.Equal(PlacementKind.Measured, placement.Kind);
        Assert.NotNull(placement.DpiX);
        Assert.NotNull(placement.DpiY);
        Assert.Equal(dpiX, placement.DpiX.Value, 6);
        Assert.Equal(dpiY, placement.DpiY.Value, 6);
        Assert.Equal(page, placement.PageIndex);
    }
}

/// <summary>
/// A hand-written classic-xref PDF for the planner tests: object 1 is the catalog, 2 the page
/// tree, and every other object is added by number. Streams get a direct, exact <c>/Length</c>,
/// so the reader can take each one's dictionary without its payload.
/// </summary>
internal sealed class ScanTestPdf
{
    private readonly SortedDictionary<int, byte[]> _objects = [];
    private readonly List<int> _pages = [];

    /// <summary>Pads every stream added afterwards to at least this many bytes (content with trailing spaces).</summary>
    public int MinimumStreamBytes { get; init; }

    /// <summary>Adds a non-stream object; <paramref name="body"/> is its full value.</summary>
    public ScanTestPdf Object(int number, string body)
    {
        _objects[number] = Encoding.Latin1.GetBytes($"{number} 0 obj\n{body}\nendobj\n");
        return this;
    }

    /// <summary>Adds a stream; <paramref name="dictionary"/> is its entries without the brackets or <c>/Length</c>.</summary>
    public ScanTestPdf Stream(int number, string dictionary, string content) =>
        Stream(number, dictionary, Encoding.Latin1.GetBytes(content.PadRight(MinimumStreamBytes)));

    /// <summary>Adds a stream with binary content.</summary>
    public ScanTestPdf Stream(int number, string dictionary, byte[] content)
    {
        var head = Encoding.Latin1.GetBytes($"{number} 0 obj\n<< {dictionary} /Length {content.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n");
        _objects[number] = [.. head, .. content, .. "\nendstream\nendobj\n"u8.ToArray()];
        return this;
    }

    /// <summary>Adds an 8-bit RGB image XObject of the given size (the payload is a placeholder; nothing here decodes it).</summary>
    public ScanTestPdf Image(int number, int width, int height, string extra = "") =>
        Stream(number, $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode {extra}", new byte[Math.Max(16, MinimumStreamBytes)]);

    /// <summary>
    /// Adds page <paramref name="number"/> with its content stream at <paramref name="number"/> + 1
    /// (none when <paramref name="content"/> is null), inline <c>/Resources</c>, and any extra page entries.
    /// </summary>
    public ScanTestPdf Page(int number, string? content, string xobjects = "", string resources = "", string extra = "")
    {
        var contents = content is null ? string.Empty : $"/Contents {number + 1} 0 R";
        Object(number, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << {xobjects} >> {resources} >> {contents} {extra} >>");
        if (content is not null)
        {
            Stream(number + 1, string.Empty, content);
        }

        _pages.Add(number);
        return this;
    }

    /// <summary>Writes the document.</summary>
    public byte[] Build()
    {
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, $"<< /Type /Pages /Kids [{string.Join(' ', _pages.Select(static p => $"{p} 0 R"))}] /Count {_pages.Count} >>");

        using var output = new MemoryStream();
        output.Write("%PDF-1.7\n"u8);
        var size = _objects.Keys.Max() + 1;
        var offsets = new long[size];
        foreach (var (number, bytes) in _objects)
        {
            offsets[number] = output.Position;
            output.Write(bytes);
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var n = 1; n < size; n++)
        {
            table.Append(_objects.ContainsKey(n) ? $"{offsets[n]:D10} 00000 n \n" : "0000000000 65535 f \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Encoding.ASCII.GetBytes(table.ToString()));
        return output.ToArray();
    }
}
