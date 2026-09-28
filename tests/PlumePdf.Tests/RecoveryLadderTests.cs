using System.Text;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Exercises the recovery ladder end to end (docs/architecture.md): clean read → a
/// tolerated deviation repaired with a diagnostic → brute-force recovery when the
/// cross-reference data is unusable → a coded exception only when nothing is recoverable
/// → <see cref="PdfOptions.Strict"/> turning every tolerated rung into a failure instead.
/// </summary>
public class RecoveryLadderTests
{
    private static readonly (int Number, string Body)[] SimpleObjects =
    [
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        (3, "<< /Type /Page /Parent 2 0 R >>"),
    ];

    [Fact]
    public void CleanRung_NoDiagnosticsAndFullyResolvable()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(SimpleObjects, rootObjectNumber: 1);
        using var source = new StreamByteSource(bytes);
        var diagnostics = new DiagnosticCollection();

        var table = CrossReferenceReader.ReadWithRecovery(source, PdfOptions.Default, diagnostics);

        Assert.Empty(diagnostics);
        Assert.True(table.EntriesByObjectNumber.ContainsKey(1));
        Assert.True(table.EntriesByObjectNumber.ContainsKey(2));
        Assert.True(table.EntriesByObjectNumber.ContainsKey(3));
    }

    [Fact]
    public void RepairedRung_ToleratesOneMalformedEntryWithDiagnostic()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(SimpleObjects, rootObjectNumber: 1, badEntryFlags: new Dictionary<int, string> { [3] = "q" });
        using var source = new StreamByteSource(bytes);
        var diagnostics = new DiagnosticCollection();

        var table = CrossReferenceReader.ReadWithRecovery(source, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME2037" && d.Severity == DiagnosticSeverity.Warning);
        // The other two objects are still fully usable - lenient reading survives the deviation.
        Assert.True(table.EntriesByObjectNumber.ContainsKey(1));
        Assert.True(table.EntriesByObjectNumber.ContainsKey(2));
    }

    [Fact]
    public void BruteForceRung_RecoversWhenStartxrefIsMissingButATrailerRemains()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in SimpleObjects)
        {
            offsets[number] = sb.Length;
            sb.Append($"{number} 0 obj\n{body}\nendobj\n");
        }

        // A trailer survives, but there is no xref table and no startxref - the clean rung
        // must fail, and recovery has to fall back to the brute-force object scan while
        // still picking up the /Root from the leftover trailer text.
        sb.Append($"trailer\n<< /Size 4 /Root 1 0 R >>\n%%EOF");

        using var source = new StreamByteSource(Encoding.ASCII.GetBytes(sb.ToString()));
        var diagnostics = new DiagnosticCollection();

        var table = CrossReferenceReader.ReadWithRecovery(source, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME2043"); // "falling back to a brute-force object scan"
        Assert.True(table.EntriesByObjectNumber.ContainsKey(1));
        Assert.True(table.EntriesByObjectNumber.ContainsKey(2));
        Assert.True(table.EntriesByObjectNumber.ContainsKey(3));

        var resolver = new ObjectResolver(source, table, PdfOptions.Default, diagnostics);
        var registry = new ObjectRegistry(resolver);
        var rootRef = Assert.IsType<PdfReference>(table.Trailer[PdfName.Root]);
        Assert.IsType<PdfDictionary>(registry[rootRef.Target]);
    }

    [Fact]
    public void BruteForceRung_SynthesizesTrailerFromCatalog_WhenNoTrailerTextSurvives()
    {
        var bytes = PdfFixtureBuilder.BuildObjectsOnlyNoXref(SimpleObjects); // no xref, no trailer at all
        using var source = new StreamByteSource(bytes);
        var diagnostics = new DiagnosticCollection();

        var table = CrossReferenceReader.ReadWithRecovery(source, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME2042"); // "synthesized a minimal trailer"
        var rootRef = Assert.IsType<PdfReference>(table.Trailer[PdfName.Root]);
        Assert.Equal(1, rootRef.Target.Number);
    }

    [Fact]
    public void UnrecoverableRung_ThrowsCodedExceptionWhenNothingCanBeSalvaged()
    {
        var garbage = Encoding.ASCII.GetBytes("%PDF-1.7\nnot a pdf body at all, just noise.\n%%EOF");
        using var source = new StreamByteSource(garbage);

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.ReadWithRecovery(source, PdfOptions.Default, null));
        Assert.Equal("PLUME2041", ex.Code);
    }

    [Fact]
    public void Strict_TurnsTheRepairedRungIntoAFailure()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(SimpleObjects, rootObjectNumber: 1, badEntryFlags: new Dictionary<int, string> { [3] = "q" });
        using var source = new StreamByteSource(bytes);
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.ReadWithRecovery(source, strict, null));
        Assert.Equal("PLUME2037", ex.Code);
    }

    [Fact]
    public void Strict_NeverFallsBackToBruteForce()
    {
        var bytes = PdfFixtureBuilder.BuildObjectsOnlyNoXref(SimpleObjects);
        using var source = new StreamByteSource(bytes);
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.ReadWithRecovery(source, strict, null));
        Assert.Equal("PLUME2040", ex.Code); // the original "no startxref" failure, not a recovered result
    }

    [Fact]
    public void ObjectStreamReader_DetectsExtendsCycle()
    {
        // Two object streams that /Extends each other - a direct cycle.
        var streamA = new PdfDictionary();
        var streamB = new PdfDictionary();
        streamA.Set(PdfName.Extends, new PdfReference(new IndirectReference(2, 0)));
        streamB.Set(PdfName.Extends, new PdfReference(new IndirectReference(1, 0)));
        streamA.Set(PdfName.N, PdfNumber.Get(0));
        streamA.Set(PdfName.First, PdfNumber.Get(0));

        var pdfStreamA = new PdfStream(streamA, ReadOnlyMemory<byte>.Empty);
        var pdfStreamB = new PdfStream(streamB, ReadOnlyMemory<byte>.Empty);

        PdfObject? Resolve(int number) => number switch
        {
            1 => pdfStreamA,
            2 => pdfStreamB,
            _ => null,
        };

        var reader = new ObjectStreamReader();
        var ex = Assert.Throws<PlumePdfException>(() => reader.GetObject(1, 0, Resolve, PdfOptions.Default, null));
        Assert.Equal("PLUME2051", ex.Code);
    }

    [Fact]
    public void ObjectStreamReader_DecodesObjectsAndMemoizes()
    {
        // Object stream containing two integers: object 10 = 42, object 11 = "hi".
        const string header = "10 0 11 3";
        const string valuesText = "42 (hi)";
        var payload = Encoding.ASCII.GetBytes(header + " " + valuesText);

        var dict = new PdfDictionary();
        dict.Set(PdfName.N, PdfNumber.Get(2));
        dict.Set(PdfName.First, PdfNumber.Get(header.Length + 1));

        var stream = new PdfStream(dict, payload);
        var resolveCallCount = 0;

        PdfObject? Resolve(int number)
        {
            resolveCallCount++;
            return number == 5 ? stream : null;
        }

        var reader = new ObjectStreamReader();
        var first = reader.GetObject(5, 0, Resolve, PdfOptions.Default, null);
        var second = reader.GetObject(5, 1, Resolve, PdfOptions.Default, null);

        Assert.Equal(42, ((PdfNumber)first).ToInt32());
        Assert.Equal("hi", ((PdfString)second).GetText());
        Assert.Equal(1, resolveCallCount); // memoized: only decoded once across both GetObject calls
    }
}
