using System.Text;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// <see cref="PdfOptions.Linearize"/> lays a full
/// rewrite out per ISO 32000-1 Annex F — linearization parameter dictionary inside the first
/// 1024 bytes, first-page objects first, primary hint stream, two cross-reference tables —
/// and a later <c>SaveIncremental</c> records the de-linearization diagnostic
/// (<c>PLUME5019</c>; <see cref="PdfOptions.Strict"/> refuses). Annex F conformance itself is
/// proven by the corpus lane's <c>qpdf --check</c> oracle (<c>QpdfLinearizationTests</c>);
/// these are the hermetic structural, interaction, and determinism cases.
/// </summary>
public class LinearizationTests
{
    private static string TempPdfPath() =>
        Path.Combine(Path.GetTempPath(), $"plumepdf-linearization-{Guid.NewGuid():N}.pdf");

    private static string SaveLinearized(int pageCount, PdfOptions? saveOptions = null)
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount, includeInfo: true));
        var outputPath = TempPdfPath();
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            document.Save(outputPath, (saveOptions ?? PdfOptions.Default) with { Linearize = true });
        }
        finally
        {
            File.Delete(sourcePath);
        }

        return outputPath;
    }

    [Fact]
    public void LinearizedSave_ReopensCleanWithSamePages()
    {
        var outputPath = SaveLinearized(pageCount: 4);
        try
        {
            using var reopened = PdfDocument.Open(outputPath, PdfOptions.Default with { Strict = true });
            Assert.Equal(4, reopened.Pages.Count);
            Assert.Empty(reopened.Diagnostics);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void LinearizedSave_SinglePage_ReopensClean()
    {
        // The single-page shape exercises Annex F's degenerate case: no remaining pages, an
        // empty shared objects section, every first-page object treated as shared (§F.4.2's
        // one-page note).
        var outputPath = SaveLinearized(pageCount: 1);
        try
        {
            using var reopened = PdfDocument.Open(outputPath, PdfOptions.Default with { Strict = true });
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void LinearizedSave_ParameterDictionaryInsideFirstKilobyte()
    {
        var outputPath = SaveLinearized(pageCount: 3);
        try
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(outputPath));
            var linearized = text.IndexOf("/Linearized", StringComparison.Ordinal);
            Assert.InRange(linearized, 1, 1023); // §F.3.3: entirely within the first 1024 bytes
            Assert.Contains("/H [", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void LinearizedSave_ComposedDocument_ReopensClean()
    {
        // The synthetic-document route (no backing byte source): Compose → linearized Save.
        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Compose(page => page.Content().Text("Linearize me.")))
            {
                document.Save(outputPath, PdfOptions.Default with { Linearize = true });
            }

            using var reopened = PdfDocument.Open(outputPath, PdfOptions.Default with { Strict = true });
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_AfterLinearizedSave_RecordsPlume5019Diagnostic()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        var linearizedPath = TempPdfPath();
        var incrementalPath = TempPdfPath();
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            document.Save(linearizedPath, PdfOptions.Default with { Linearize = true });
            Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5019");

            document.SaveIncremental(incrementalPath);

            Assert.Contains(document.Diagnostics, static d => d.Code == "PLUME5019");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(linearizedPath);
            File.Delete(incrementalPath);
        }
    }

    [Fact]
    public void SaveIncremental_AfterLinearizedSave_UnderStrict_ThrowsPlume5019()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        var linearizedPath = TempPdfPath();
        var incrementalPath = TempPdfPath();
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            document.Save(linearizedPath, PdfOptions.Default with { Linearize = true });

            var ex = Assert.Throws<PlumePdfException>(() =>
                document.SaveIncremental(incrementalPath, PdfOptions.Default with { Strict = true }));
            Assert.Equal("PLUME5019", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(linearizedPath);
            File.Delete(incrementalPath);
        }
    }

    [Fact]
    public void SaveIncremental_WithoutLinearizedSave_RecordsNoPlume5019()
    {
        // The control case: an ordinary Save → SaveIncremental sequence stays diagnostic-free.
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        var savedPath = TempPdfPath();
        var incrementalPath = TempPdfPath();
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            document.Save(savedPath);
            document.SaveIncremental(incrementalPath);

            Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5019");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(savedPath);
            File.Delete(incrementalPath);
        }
    }

    [Fact]
    public void LinearizedSave_ZeroPages_ThrowsPlume5020()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = TempPdfPath();
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            document.Pages.RemoveAt(0);

            var ex = Assert.Throws<PlumePdfException>(() =>
                document.Save(outputPath, PdfOptions.Default with { Linearize = true }));
            Assert.Equal("PLUME5020", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void LinearizedSave_Deterministic_TwiceProducesByteIdenticalOutput()
    {
        // Linearized layout + hint-stream contents are one of Phase 6's four
        // new writer surfaces, each with its own byte-identical double-save regression.
        var deterministic = PdfOptions.Default with { Deterministic = true };
        var pathA = SaveLinearized(pageCount: 4, deterministic);
        var pathB = SaveLinearized(pageCount: 4, deterministic);
        try
        {
            Assert.Equal(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void LinearizedSave_NonDeterministic_TwiceProducesDifferentOutput()
    {
        var pathA = SaveLinearized(pageCount: 4);
        var pathB = SaveLinearized(pageCount: 4);
        try
        {
            Assert.NotEqual(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }
}
