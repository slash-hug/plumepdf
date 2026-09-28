using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <c>PdfDocument.SaveIncremental</c>: output is the input bytes followed by
/// a valid appendix; reopening the output resolves the updated objects.
/// </summary>
public class IncrementalSaveTests
{
    [Fact]
    public void SaveIncremental_NoMutation_OutputEqualsInput()
    {
        // ISO 32000-1 §7.5.4 forbids a cross-reference section with zero
        // subsections, so "nothing changed" cannot be expressed as an appendix — the only
        // well-formed incremental save of an unmutated document is a byte-identical copy.
        var sourceBytes = WriterTestDocuments.BuildDocument(pageCount: 2);
        var sourcePath = WriterTestDocuments.WriteTempFile(sourceBytes);
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-incremental-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.SaveIncremental(outputPath);
            }

            var outputBytes = File.ReadAllBytes(outputPath);
            Assert.Equal(sourceBytes, outputBytes);

            using var reopened = PdfDocument.Open(outputPath);
            Assert.NotNull(reopened.Objects.Trailer);
            Assert.Equal(2, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_SourceEndsWithoutNewline_AppendixStartsOnItsOwnLine()
    {
        // BuildDocument ends exactly at "%%EOF" with no trailing newline;
        // without a leading EOL the appendix's first object glues onto the %%EOF comment line.
        var sourceBytes = WriterTestDocuments.BuildDocument(pageCount: 3);
        Assert.Equal((byte)'F', sourceBytes[^1]);
        var sourcePath = WriterTestDocuments.WriteTempFile(sourceBytes);
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-incremental-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Pages.RemoveAt(2);
                document.SaveIncremental(outputPath);
            }

            var outputBytes = File.ReadAllBytes(outputPath);
            Assert.True(outputBytes.Length > sourceBytes.Length);
            Assert.Equal((byte)'\n', outputBytes[sourceBytes.Length]);

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Equal(2, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_PageRemoved_ReopenedDocumentReflectsRemoval()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 3));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-incremental-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Pages.RemoveAt(1);
                document.SaveIncremental(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Equal(2, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_PagesReordered_ReopenedDocumentReflectsNewOrder()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 3));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-incremental-{Guid.NewGuid():N}.pdf");
        try
        {
            IndirectReference expectedFirst;
            using (var document = PdfDocument.Open(sourcePath))
            {
                expectedFirst = document.Pages[2].Reference;
                document.Pages.Move(2, 0);
                document.SaveIncremental(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Equal(3, reopened.Pages.Count);
            Assert.Equal(expectedFirst, reopened.Pages[0].Reference);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_OnMergedDocument_ThrowsCodedException()
    {
        var pathA = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            using var merged = Pdf.Merge(pathA);
            var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-incremental-{Guid.NewGuid():N}.pdf");
            var ex = Assert.Throws<PlumePdfException>(() => merged.SaveIncremental(outputPath));
            Assert.Equal("PLUME5003", ex.Code);
        }
        finally
        {
            File.Delete(pathA);
        }
    }
}
