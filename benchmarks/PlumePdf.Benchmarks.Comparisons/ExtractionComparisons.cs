using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

namespace PlumePdf.Benchmarks.Comparisons;

/// <summary>
/// Phase 3 extraction, informational competitor comparison (docs/architecture.md's
/// Enforcement section: "manual and informational, run per-phase, not CI-gated").
/// Per-page text extraction, timed head-to-head
/// against the same pinned real-world corpus files, using ONLY each competitor's
/// published binary NuGet API (their source is never read).
///
/// A PlumePdf-side benchmark method (calling <c>PdfPage.ExtractText()</c>) shares this
/// class's [Benchmark] table so both libraries show up in one BenchmarkDotNet
/// summary — <see cref="PlumePdf_MultiPageRealWorld"/>/<see cref="PlumePdf_SinglePageTrivial"/>,
/// added once extraction landed <c>PdfPage.ExtractText()</c> (this project's
/// ProjectReference to src/PlumePdf/PlumePdf.csproj was already in place for exactly this).
/// Note PlumePdf.PdfDocument and iText.Kernel.Pdf.PdfDocument share a type name —
/// this file deliberately fully-qualifies every competitor type instead of `using`-importing
/// their namespaces, so adding that PlumePdf row later doesn't need every existing call site
/// touched to disambiguate.
///
/// Self-contained the way Phase 1's OpenBenchmarks is NOT possible here: a meaningful
/// extraction comparison needs real-world documents (embedded fonts, real layout), not a
/// hand-rolled one-line PDF. <see cref="GlobalSetup"/> throws a clear, actionable message
/// instead of silently no-op'ing when corpora/ isn't fetched, since this suite is
/// maintainer-run, never part of the automated hermetic test lane.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ExtractionComparisons
{
    private string _multiPageRealWorldPath = null!;
    private string _singlePageTrivialPath = null!;

    /// <summary>
    /// Fetched by <c>scripts/fetch-corpora.sh</c>: a 3-page, real-world (TCPDF-generated)
    /// document with an embedded, subsetted CID TrueType font under Identity-H — the same
    /// file used as the ground-truth fixture
    /// <c>tests/PlumePdf.CorpusTests/GroundTruth/truetype-identity-h-header-footer-basicapi.json</c>,
    /// reused here so the benchmark scenario and the correctness fixture describe the same
    /// document rather than drifting apart.
    /// </summary>
    private const string MultiPageRealWorldFileName = "basicapi.pdf";

    /// <summary>
    /// A trivial single-line, single-page, structurally clean document — the
    /// extraction-benchmark equivalent of Phase 1's OpenBenchmarks "SmallClean"
    /// baseline. Deliberately NOT one of pdf.js's "-bad"/damaged-xref fixtures
    /// (e.g. helloworld-bad.pdf): a comparison library that cannot repair that damage
    /// would make this scenario measure "does it open at all", not extraction throughput. The clean-vs-damaged axis is Phase 1's OpenBenchmarks
    /// concern, not this suite's.
    /// </summary>
    private const string SinglePageTrivialFileName = "issue4575.pdf";

    [GlobalSetup]
    public void Setup()
    {
        _multiPageRealWorldPath = ComparisonCorpora.ResolvePdfJsSubsetFile(MultiPageRealWorldFileName);
        _singlePageTrivialPath = ComparisonCorpora.ResolvePdfJsSubsetFile(SinglePageTrivialFileName);
    }

    [Benchmark]
    [BenchmarkCategory("MultiPageRealWorld")]
    public int ITextSeven_MultiPageRealWorld() => ExtractTotalTextLengthWithItextSeven(_multiPageRealWorldPath);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("MultiPageRealWorld")]
    public int PlumePdf_MultiPageRealWorld() => ExtractTotalTextLengthWithPlumePdf(_multiPageRealWorldPath);

    [Benchmark]
    [BenchmarkCategory("SinglePageTrivial")]
    public int ITextSeven_SinglePageTrivial() => ExtractTotalTextLengthWithItextSeven(_singlePageTrivialPath);

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("SinglePageTrivial")]
    public int PlumePdf_SinglePageTrivial() => ExtractTotalTextLengthWithPlumePdf(_singlePageTrivialPath);

    /// <summary>Per-page text extraction via iText7's public binary API (PdfTextExtractor.GetTextFromPage) — never its source.</summary>
    private static int ExtractTotalTextLengthWithItextSeven(string path)
    {
        using var reader = new iText.Kernel.Pdf.PdfReader(path);
        using var document = new iText.Kernel.Pdf.PdfDocument(reader);
        var total = 0;
        for (var pageNumber = 1; pageNumber <= document.GetNumberOfPages(); pageNumber++)
        {
            var text = iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(document.GetPage(pageNumber));
            total += text.Length;
        }

        return total;
    }

    /// <summary>Per-page text extraction via PlumePdf.PdfPage.ExtractText() — the row this project's ProjectReference to src/PlumePdf was already in place for.</summary>
    private static int ExtractTotalTextLengthWithPlumePdf(string path)
    {
        using var document = global::PlumePdf.PdfDocument.Open(path);
        var total = 0;
        foreach (var page in document.Pages)
        {
            total += page.ExtractText().Text.Length;
        }

        return total;
    }
}

/// <summary>
/// Locates the pinned mozilla/pdf.js <c>test/pdfs</c> subset (<c>scripts/fetch-corpora.sh</c>)
/// from this benchmark project's own output directory. Deliberately not a fixed
/// relative path (see <c>tests/PlumePdf.CorpusTests/CorpusFixture.cs</c>'s identical
/// rationale) — this project cannot reference that test-project helper directly (it isn't
/// a shared library, and this project must stay free of any project reference other than
/// PlumePdf and the two competitor packages per the AGPL isolation wall), so the same small
/// amount of root-finding logic is duplicated here rather than factored into a shared
/// dependency that would blur the wall.
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
                "PlumePdf.Benchmarks.Comparisons (this suite is maintainer-run against real-world " +
                "pinned corpus files, not self-contained like Phase 1's OpenBenchmarks).");
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

    /// <summary>
    /// The repository root (the parent of the <c>benchmarks/</c> directory, where
    /// <c>corpora/</c> lives) — found by walking up from the build output directory
    /// until <c>bench.sln</c> is found (that locates <c>benchmarks/</c>), then going up
    /// one more level. Not a fixed "../../../.." relative path for the same
    /// fragility reason as <c>tests/PlumePdf.CorpusTests/CorpusFixture.cs</c>.
    /// </summary>
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
