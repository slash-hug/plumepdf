using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <c>PdfOptions.Deterministic</c>: two full-rewrite
/// <c>Save</c> runs over the same input and options produce byte-identical output; without
/// the flag, the two runs differ (a fresh random document <c>/ID</c> each time).
/// </summary>
public class DeterministicTests
{
    [Fact]
    public void Save_Deterministic_TwoRunsProduceByteIdenticalOutput()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2, includeInfo: true));
        var pathA = Path.Combine(Path.GetTempPath(), $"plumepdf-det-a-{Guid.NewGuid():N}.pdf");
        var pathB = Path.Combine(Path.GetTempPath(), $"plumepdf-det-b-{Guid.NewGuid():N}.pdf");
        try
        {
            var options = new PdfOptions { Deterministic = true };
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(pathA, options);
            }

            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(pathB, options);
            }

            Assert.Equal(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void Save_NonDeterministic_TwoRunsProduceDifferentDocumentIds()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var pathA = Path.Combine(Path.GetTempPath(), $"plumepdf-nondet-a-{Guid.NewGuid():N}.pdf");
        var pathB = Path.Combine(Path.GetTempPath(), $"plumepdf-nondet-b-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(pathA);
            }

            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(pathB);
            }

            Assert.NotEqual(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void SaveIncremental_Deterministic_PreservesOriginalId0()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-det-inc-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Pages.RemoveAt(0);
                document.SaveIncremental(outputPath, new PdfOptions { Deterministic = true });
            }

            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(outputPath));
            Assert.Contains("00112233445566778899aabbccddeeff", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }
}
