using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="Pdf.Rasterize(string,PdfRasterizeOptions?)"/> and
/// <see cref="PdfPage.Rasterize"/> actually wired to the Phase 8 rasterizer and reachable
/// through the public API
/// (the rasterizer engine once existed and was tested, but nothing outside <c>PlumePdf.Raster</c>
/// could reach it).
/// </summary>
public class RasterizeTests
{
    [Fact]
    public void PdfPage_Rasterize_DefaultDpi_FillsWholePageWithItsColor()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithFilledRect());
        using var document = PdfDocument.Open(path);

        var image = document.Pages[0].Rasterize();

        Assert.Single(image.Frames);
        var frame = image.Frames[0];
        // 200x100 pt page at the default 96 DPI: 200/72*96 = 266.67 -> 267, 100/72*96 = 133.33 -> 133.
        Assert.Equal(267, frame.Width);
        Assert.Equal(133, frame.Height);
        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);

        var centerOffset = (((frame.Height / 2) * frame.Width) + (frame.Width / 2)) * 4;
        var span = frame.Pixels.Span;
        Assert.Equal(255, span[centerOffset]); // R
        Assert.Equal(0, span[centerOffset + 1]); // G
        Assert.Equal(0, span[centerOffset + 2]); // B
    }

    [Fact]
    public void PdfPage_Rasterize_ExplicitPixelSize_UsesItExactly()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithFilledRect());
        using var document = PdfDocument.Open(path);

        var options = PdfRasterizeOptions.Default with { Dpi = null, PixelWidth = 40, PixelHeight = 20 };
        var image = document.Pages[0].Rasterize(options);

        Assert.Equal(40, image.Frames[0].Width);
        Assert.Equal(20, image.Frames[0].Height);
    }

    [Fact]
    public void PdfPage_Rasterize_RespectsEffectiveRotate_SwappingDisplayDimensions()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithFilledRect(rotate: 90));
        using var document = PdfDocument.Open(path);

        var image = document.Pages[0].Rasterize();

        // MediaBox is [0 0 200 100]; /Rotate 90 swaps the *displayed* size to 100x200 before
        // DPI is applied: 100/72*96 -> 133, 200/72*96 -> 267 (the unrotated test's 267x133,
        // swapped) — proving PageRasterAdapter derived pixel size from the post-rotation
        // display size, not the raw MediaBox.
        Assert.Equal(133, image.Frames[0].Width);
        Assert.Equal(267, image.Frames[0].Height);
    }

    [Fact]
    public void Pdf_Rasterize_OpensPathAndRendersFirstPageByDefault()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithFilledRect());

        var image = Pdf.Rasterize(path);

        Assert.Single(image.Frames);
        Assert.Equal(267, image.Frames[0].Width);
        Assert.Equal(133, image.Frames[0].Height);
    }

    [Fact]
    public void Pdf_Rasterize_PageIndices_RendersEachRequestedPageInOrder()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 3));

        var image = Pdf.Rasterize(path, PdfRasterizeOptions.Default with
        {
            PageIndices = [2, 0],
            Dpi = null,
            PixelWidth = 10,
            PixelHeight = 10,
        });

        Assert.Equal(2, image.Frames.Count);
        Assert.All(image.Frames, f => Assert.Equal(10, f.Width));
    }

    [Fact]
    public void Pdf_Rasterize_PageIndexOutOfRange_Throws()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Pdf.Rasterize(path, PdfRasterizeOptions.Default with { PageIndices = [5] }));
    }

    [Fact]
    public void Rasterize_BothPixelSizeAndDpiSet_ThrowsArgumentException()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithFilledRect());

        Assert.Throws<ArgumentException>(() =>
            Pdf.Rasterize(path, new PdfRasterizeOptions { PixelWidth = 10, PixelHeight = 10, Dpi = 96 }));
    }

    [Fact]
    public void PdfPage_Rasterize_MaxRasterSurfaceBytesCap_IsReadFromPdfOptions()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithFilledRect());
        var limited = PdfOptions.Default with { MaxRasterSurfaceBytes = 100 };
        using var document = PdfDocument.Open(path, limited);

        var ex = Assert.Throws<PlumePdfException>(() => document.Pages[0].Rasterize());
        Assert.Equal("PLUME7500", ex.Code);
    }

    [Fact]
    public void PdfPage_Rasterize_ContentsAsIndirectReferenceToArray_PaintsAllStreams()
    {
        // A /Contents that is an indirect reference TO the array (LiveCycle/AEM
        // AcroForms) rendered as a silently blank page — the reader recognized only
        // "reference to a stream" and "direct array".
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithIndirectContentsArray());
        using var document = PdfDocument.Open(path);

        var image = document.Pages[0].Rasterize();

        var frame = image.Frames[0];
        var centerOffset = (((frame.Height / 2) * frame.Width) + (frame.Width / 2)) * 4;
        var span = frame.Pixels.Span;
        Assert.Equal(255, span[centerOffset]); // R — the first array stream's full-page red fill.
        Assert.Equal(0, span[centerOffset + 1]); // G
        Assert.DoesNotContain(image.Diagnostics, d => d.Code == "PLUME6083");
    }

    [Fact]
    public void PdfPage_ExtractText_ContentsAsIndirectReferenceToArray_FindsText()
    {
        // The same shape through the shared ReadContentBytes reader's other consumer:
        // extraction was equally blank before this fix.
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithIndirectContentsArray());
        using var document = PdfDocument.Open(path);

        Assert.Contains("Hello", document.Pages[0].ExtractText().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfPage_Rasterize_ContentsResolvingToNonStreamNonArray_RendersEmptyWithPlume6083()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithIndirectContentsArray(unusableContents: true));
        using var document = PdfDocument.Open(path);

        var image = document.Pages[0].Rasterize();

        var frame = image.Frames[0];
        var centerOffset = (((frame.Height / 2) * frame.Width) + (frame.Width / 2)) * 4;
        Assert.Equal(255, frame.Pixels.Span[centerOffset + 1]); // G — background white, nothing painted.
        Assert.Contains(image.Diagnostics, d => d.Code == "PLUME6083");
    }
}
