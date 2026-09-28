using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks;

/// <summary>
/// PlumePdf-only extraction throughput (maintainer/agent-run, not CI-gated). Mirrors
/// <c>benchmarks/PlumePdf.Benchmarks.Comparisons/ExtractionComparisons.cs</c>'s
/// <c>MultiPageRealWorld</c>/<c>SinglePageTrivial</c> scenario split and pinned corpus files
/// (<c>basicapi.pdf</c>/<c>issue4575.pdf</c>) for a like-for-like three-way comparison across
/// both benchmark projects (this one stays competitor-free — the AGPL isolation wall).
/// <c>issue4575.pdf</c> also carries an embedded image XObject, so it doubles
/// as the <see cref="ImageExtraction_SinglePageTrivial"/> scenario rather than needing a
/// separate pinned file.
/// </summary>
[MemoryDiagnoser]
public class ExtractionBenchmarks
{
    private PdfDocument _multiPageRealWorld = null!;
    private PdfDocument _singlePageTrivial = null!;
    private string _multiPageRealWorldPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _multiPageRealWorldPath = ComparisonCorpora.ResolvePdfJsSubsetFile("basicapi.pdf");
        _multiPageRealWorld = PdfDocument.Open(_multiPageRealWorldPath);
        _singlePageTrivial = PdfDocument.Open(ComparisonCorpora.ResolvePdfJsSubsetFile("issue4575.pdf"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _multiPageRealWorld.Dispose();
        _singlePageTrivial.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int PerPageTextExtraction_MultiPageRealWorld()
    {
        var total = 0;
        foreach (var page in _multiPageRealWorld.Pages)
        {
            total += page.ExtractText().Text.Length;
        }

        return total;
    }

    [Benchmark]
    public int PerPageTextExtraction_SinglePageTrivial() => _singlePageTrivial.Pages[0].ExtractText().Text.Length;

    /// <summary>Pdf.ExtractText(path) — the flattened-string convenience verb — timed end-to-end, including Open.</summary>
    [Benchmark]
    public int WholeDocumentVerb_MultiPageRealWorld() => Pdf.ExtractText(_multiPageRealWorldPath).Length;

    [Benchmark]
    public int ImageExtraction_SinglePageTrivial() => _singlePageTrivial.Pages[0].ExtractImages().Count;
}

/// <summary>
/// Locates the pinned mozilla/pdf.js <c>test/pdfs</c> subset (<c>scripts/fetch-corpora.sh</c>)
/// from this benchmark project's own output directory — a duplicate of
/// <c>PlumePdf.Benchmarks.Comparisons/ExtractionComparisons.cs</c>'s <c>ComparisonCorpora</c>
/// (that type is <see langword="internal"/> to a different assembly, so it can't be shared
/// directly; see that file's own doc comment for why this project deliberately doesn't take a
/// cross-project dependency to avoid duplicating it). Extraction benchmarks need real-world
/// documents (embedded fonts, real layout) — unlike <see cref="OpenBenchmarks"/>'s
/// hand-rolled synthetic documents, which are fine for xref-parsing throughput but wouldn't
/// stress real glyph/font decoding — so this is the one suite in this project that isn't
/// self-contained with plain <c>dotnet run</c>; it throws a clear, actionable message instead
/// of silently no-op'ing when <c>corpora/</c> isn't fetched.
/// </summary>
internal static class ComparisonCorpora
{
    private const string SolutionFileName = "bench.sln";
    private const string PdfJsSubsetDirectoryPrefix = "pdfjs-subset-";

    public static string ResolvePdfJsSubsetFile(string fileName)
    {
        var corporaRoot = Path.Combine(FindRepoRoot(), "corpora");
        if (!Directory.Exists(corporaRoot))
        {
            throw new InvalidOperationException(
                $"corpora/ not found at {corporaRoot} — run ./scripts/fetch-corpora.sh before running " +
                "PlumePdf.Benchmarks' extraction suite (this suite is maintainer-run against real-world " +
                "pinned corpus files, not self-contained like OpenBenchmarks).");
        }

        var subsetDir = Directory.EnumerateDirectories(corporaRoot, $"{PdfJsSubsetDirectoryPrefix}*").FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No '{PdfJsSubsetDirectoryPrefix}*' directory found under {corporaRoot} — " +
                "run ./scripts/fetch-corpora.sh (it fetches the pinned pdf.js test/pdfs subset).");

        var path = Path.Combine(subsetDir, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Expected corpus file not found: {path}.");
        }

        return path;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.Parent?.FullName
                    ?? throw new InvalidOperationException($"{dir.FullName} ({SolutionFileName}) has no parent directory.");
            }
        }

        throw new InvalidOperationException($"Could not locate the benchmarks directory ({SolutionFileName}) above {AppContext.BaseDirectory}.");
    }
}
