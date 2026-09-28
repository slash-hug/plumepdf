using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// An incremental append under <see cref="PdfOptions.Deterministic"/> is
/// byte-identical across repeated runs over the same input and the same sequence of mutations
/// — stable allocator order (<see cref="ObjectRegistry.AllocateNumber"/>), stable dirty-object
/// enumeration order (<see cref="ObjectRegistry.DirtyObjects"/>), and stable serialized bytes.
/// Lands alongside the allocator and dirty set, not bolted on later.
/// </summary>
public class IncrementalMutationDeterminismTests
{
    private static readonly PdfName VName = PdfName.Get("V");

    [Fact]
    public void SaveIncremental_Deterministic_SameMutationsTwice_ProduceByteIdenticalOutput()
    {
        var sourceBytes = WriterTestDocuments.BuildDocument(pageCount: 3);
        var sourcePathA = WriterTestDocuments.WriteTempFile(sourceBytes);
        var sourcePathB = WriterTestDocuments.WriteTempFile(sourceBytes);
        var outputPathA = TempPdfPath();
        var outputPathB = TempPdfPath();
        try
        {
            var deterministic = PdfOptions.Default with { Deterministic = true };

            ApplySameMutationSequence(sourcePathA, outputPathA, deterministic);
            ApplySameMutationSequence(sourcePathB, outputPathB, deterministic);

            var bytesA = File.ReadAllBytes(outputPathA);
            var bytesB = File.ReadAllBytes(outputPathB);
            Assert.Equal(bytesA, bytesB);
        }
        finally
        {
            File.Delete(sourcePathA);
            File.Delete(sourcePathB);
            File.Delete(outputPathA);
            File.Delete(outputPathB);
        }
    }

    [Fact]
    public void SaveIncremental_NonDeterministic_SameMutationsTwice_ProduceDifferentOutput()
    {
        // The control case: proves Deterministic actually changes behavior rather than the
        // "byte-identical" test above passing vacuously because nothing ever varies run to run.
        var sourceBytes = WriterTestDocuments.BuildDocument(pageCount: 3);
        var sourcePathA = WriterTestDocuments.WriteTempFile(sourceBytes);
        var sourcePathB = WriterTestDocuments.WriteTempFile(sourceBytes);
        var outputPathA = TempPdfPath();
        var outputPathB = TempPdfPath();
        try
        {
            ApplySameMutationSequence(sourcePathA, outputPathA, PdfOptions.Default);
            ApplySameMutationSequence(sourcePathB, outputPathB, PdfOptions.Default);

            var bytesA = File.ReadAllBytes(outputPathA);
            var bytesB = File.ReadAllBytes(outputPathB);
            Assert.NotEqual(bytesA, bytesB);
        }
        finally
        {
            File.Delete(sourcePathA);
            File.Delete(sourcePathB);
            File.Delete(outputPathA);
            File.Delete(outputPathB);
        }
    }

    /// <summary>The identical sequence of allocations/registrations/mutations both runs perform, so any divergence in the output is attributable only to allocation/enumeration order or the writer's own byte production, not to the test doing something different each time.</summary>
    private static void ApplySameMutationSequence(string sourcePath, string outputPath, PdfOptions options)
    {
        using var document = PdfDocument.Open(sourcePath, options);

        var fontReference = new IndirectReference(3, 0);
        var font = Assert.IsType<PdfDictionary>(document.Objects[fontReference]);
        font.Set(VName, PdfString.FromLiteral("Filled Field"u8.ToArray()));
        document.Objects.MarkDirty(fontReference);

        for (var i = 0; i < 3; i++)
        {
            var reference = document.Objects.AllocateNumber();
            var dict = new PdfDictionary();
            dict.Set(PdfName.Get("Index"), PdfNumber.Get(i));
            document.Objects.RegisterNew(reference, dict);
        }

        document.SaveIncremental(outputPath, options);
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-determinism-{Guid.NewGuid():N}.pdf");
}
