using System.Text;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// <see cref="PdfOptions.Optimize"/> packs
/// compressible objects into object streams and closes with a cross-reference stream —
/// smaller output that reopens clean — and refuses below PDF 1.5 (including PDF/A-1b's
/// forced 1.4 header) rather than silently writing something other than what was asked for.
/// The external `qpdf --check` proof lives in the corpus lane
/// (<c>QpdfLinearizationTests</c>); these are the hermetic structural and refusal cases.
/// </summary>
public class OptimizationTests
{
    private static string TempPdfPath() =>
        Path.Combine(Path.GetTempPath(), $"plumepdf-optimization-{Guid.NewGuid():N}.pdf");

    private static string SaveDocument(int pageCount, PdfOptions saveOptions)
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount, includeInfo: true));
        var outputPath = TempPdfPath();
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            document.Save(outputPath, saveOptions);
        }
        finally
        {
            File.Delete(sourcePath);
        }

        return outputPath;
    }

    [Fact]
    public void OptimizedSave_ReopensCleanWithSamePages()
    {
        var outputPath = SaveDocument(pageCount: 5, PdfOptions.Default with { Optimize = true });
        try
        {
            using var reopened = PdfDocument.Open(outputPath, PdfOptions.Default with { Strict = true });
            Assert.Equal(5, reopened.Pages.Count);
            Assert.Empty(reopened.Diagnostics);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void OptimizedSave_WritesObjectAndCrossReferenceStreams()
    {
        var outputPath = SaveDocument(pageCount: 3, PdfOptions.Default with { Optimize = true });
        try
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(outputPath));
            Assert.Contains("/ObjStm", text, StringComparison.Ordinal);
            Assert.Contains("/XRef", text, StringComparison.Ordinal);
            // No classic cross-reference table or trailer keyword anywhere: the
            // cross-reference stream replaces both (ISO 32000-1 §7.5.8).
            Assert.DoesNotContain("\ntrailer", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\nxref\n", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void OptimizedSave_IsNoLargerThanClassicSave()
    {
        var optimizedPath = SaveDocument(pageCount: 40, PdfOptions.Default with { Optimize = true });
        var classicPath = SaveDocument(pageCount: 40, PdfOptions.Default);
        try
        {
            var optimized = new FileInfo(optimizedPath).Length;
            var classic = new FileInfo(classicPath).Length;
            Assert.True(optimized <= classic, $"Optimized save produced {optimized} bytes, larger than the classic save's {classic}.");
        }
        finally
        {
            File.Delete(optimizedPath);
            File.Delete(classicPath);
        }
    }

    [Fact]
    public void OptimizedSave_BelowPdf15_ThrowsPlume5017()
    {
        var ex = Assert.Throws<PlumePdfException>(() =>
            SaveDocument(pageCount: 1, PdfOptions.Default with { Optimize = true, PdfVersion = "1.4" }));
        Assert.Equal("PLUME5017", ex.Code);
    }

    [Fact]
    public void OptimizedSave_UnparseableVersion_ThrowsPlume5017()
    {
        var ex = Assert.Throws<PlumePdfException>(() =>
            SaveDocument(pageCount: 1, PdfOptions.Default with { Optimize = true, PdfVersion = "next" }));
        Assert.Equal("PLUME5017", ex.Code);
    }

    [Fact]
    public void OptimizedSave_UnderPdfA1b_ThrowsPlume5017()
    {
        // PdfAConformance.A1b forces the "1.4" header via the version knob, which runs before
        // the optimization check — so PDF/A-1b + Optimize is the same refusal, with the
        // message naming the PDF/A-1b interaction.
        var ex = Assert.Throws<PlumePdfException>(() =>
            SaveDocument(pageCount: 1, PdfOptions.Default with { Optimize = true, PdfAConformance = PdfAConformance.A1b }));
        Assert.Equal("PLUME5017", ex.Code);
        Assert.Contains("PDF/A-1b", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizedSave_CombinedWithLinearize_ThrowsPlume5018()
    {
        var ex = Assert.Throws<PlumePdfException>(() =>
            SaveDocument(pageCount: 1, PdfOptions.Default with { Optimize = true, Linearize = true }));
        Assert.Equal("PLUME5018", ex.Code);
    }

    [Fact]
    public void OptimizedSave_Deterministic_TwiceProducesByteIdenticalOutput()
    {
        // Object-stream packing is one of Phase 6's four new writer surfaces,
        // each of which gets its own byte-identical double-save regression test.
        var deterministic = PdfOptions.Default with { Optimize = true, Deterministic = true };
        var pathA = SaveDocument(pageCount: 5, deterministic);
        var pathB = SaveDocument(pageCount: 5, deterministic);
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
    public void OptimizedSave_NonDeterministic_TwiceProducesDifferentOutput()
    {
        // The control case: proves the byte-identical test above isn't passing vacuously —
        // without the flag, the fresh random /ID varies run to run.
        var pathA = SaveDocument(pageCount: 5, PdfOptions.Default with { Optimize = true });
        var pathB = SaveDocument(pageCount: 5, PdfOptions.Default with { Optimize = true });
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
