using System.Text;
using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks;

/// <summary>
/// <see cref="PdfDocument.Open(string)"/> throughput across the four corners of
/// Phase 1's reading scope (the "open any real-world PDF —
/// including damaged ones" exit demo): small vs. large documents, crossed with clean vs.
/// damaged cross-reference data. The damaged cases specifically walk the recovery
/// ladder's brute-force rung (docs/architecture.md "Recovery ladder"), which is
/// expected to cost measurably more than the classic-xref fast path — that gap
/// is exactly what this suite tracks release over release.
///
/// Self-contained: builds its own sample documents at <see cref="GlobalSetupAttribute"/>
/// time rather than depending on <c>corpora/</c> (fetched, not always present) or
/// <c>tests/PlumePdf.CorpusTests/Fixtures/</c> (a different project) — a benchmark
/// suite should run with nothing but <c>dotnet run</c>.
/// </summary>
[MemoryDiagnoser]
public class OpenBenchmarks
{
    private string _smallCleanPath = null!;
    private string _largeCleanPath = null!;
    private string _smallDamagedPath = null!;
    private string _largeDamagedPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallCleanPath = WriteTempFile(SampleDocuments.BuildClassicXrefDocument(pageCount: 1));
        _largeCleanPath = WriteTempFile(SampleDocuments.BuildClassicXrefDocument(pageCount: 500));
        _smallDamagedPath = WriteTempFile(SampleDocuments.Damage(SampleDocuments.BuildClassicXrefDocument(pageCount: 1)));
        _largeDamagedPath = WriteTempFile(SampleDocuments.Damage(SampleDocuments.BuildClassicXrefDocument(pageCount: 500)));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var path in new[] { _smallCleanPath, _largeCleanPath, _smallDamagedPath, _largeDamagedPath })
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Benchmark(Baseline = true)]
    public long SmallClean() => OpenAndCountObjects(_smallCleanPath);

    [Benchmark]
    public long LargeClean() => OpenAndCountObjects(_largeCleanPath);

    [Benchmark]
    public long SmallDamaged() => OpenAndCountObjects(_smallDamagedPath);

    [Benchmark]
    public long LargeDamaged() => OpenAndCountObjects(_largeDamagedPath);

    private static long OpenAndCountObjects(string path)
    {
        using var document = PdfDocument.Open(path);
        return document.Diagnostics.LongCount() + 1;
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}

/// <summary>
/// Hand-rolled classic-xref PDF construction shared by every Phase 1 benchmark
/// suite in this assembly (Open/Save/Tokenizer) — deliberately independent of
/// PlumePdf's own writer, so these benchmarks measure the reader/writer under
/// test against a fixed, external byte source rather than a moving target.
/// Written directly from ISO 32000-1's object/xref-table grammar (the same
/// clean-room-permitted approach, per AGENTS.md, as
/// <c>tests/PlumePdf.CorpusTests/Fixtures/generate_fixtures.py</c>),
/// just in C# so the benchmark project has no cross-project dependency.
/// </summary>
internal static class SampleDocuments
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

    /// <summary>
    /// A minimal, well-formed classic cross-reference-table document with
    /// <paramref name="pageCount"/> pages, each with its own content stream.
    /// </summary>
    public static byte[] BuildClassicXrefDocument(int pageCount)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);

        var offsets = new Dictionary<int, int>();
        var pageRefs = new List<int>();

        // Object numbering: 1=Catalog, 2=Pages, 3=Font, then 2 objects (page,
        // content) per page starting at 4.
        const int catalogNum = 1;
        const int pagesNum = 2;
        const int fontNum = 3;
        var nextObjNum = 4;
        var pageNums = new List<int>();
        var contentNums = new List<int>();
        for (var i = 0; i < pageCount; i++)
        {
            pageNums.Add(nextObjNum++);
            contentNums.Add(nextObjNum++);
        }

        int totalObjects = nextObjNum;

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");

        var kids = string.Join(" ", pageNums.ConvertAll(n => $"{n} 0 R"));
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

        WriteObject(fontNum, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (var i = 0; i < pageCount; i++)
        {
            WriteObject(
                pageNums[i],
                $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 100] " +
                $"/Resources << /Font << /F1 {fontNum} 0 R >> >> /Contents {contentNums[i]} 0 R >>");

            var content = $"BT /F1 12 Tf 20 50 Td (Page {i + 1} of {pageCount}) Tj ET";
            offsets[contentNums[i]] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{contentNums[i]} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));
        }

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return buffer.ToArray();
    }

    /// <summary>
    /// Corrupts every classic-xref offset in a document built by
    /// <see cref="BuildClassicXrefDocument"/> so the recovery ladder's
    /// brute-force rung must run (structurally identical to
    /// <c>tests/PlumePdf.CorpusTests/Fixtures/broken-xref.pdf</c>).
    /// </summary>
    public static byte[] Damage(byte[] document)
    {
        var text = Encoding.ASCII.GetString(document);
        var xrefIndex = text.IndexOf("\nxref\n", StringComparison.Ordinal);
        var trailerIndex = text.IndexOf("trailer", xrefIndex, StringComparison.Ordinal);
        var xrefSection = text[(xrefIndex + 1)..trailerIndex];
        var lines = xrefSection.Split('\n');
        for (var i = 2; i < lines.Length; i++)
        {
            if (lines[i].EndsWith(" n ", StringComparison.Ordinal))
            {
                lines[i] = "0000000001 00000 n ";
            }
        }

        var damagedSection = string.Join('\n', lines);
        var damaged = string.Concat(text.AsSpan(0, xrefIndex + 1), damagedSection, text.AsSpan(trailerIndex));
        return Encoding.ASCII.GetBytes(damaged);
    }
}
