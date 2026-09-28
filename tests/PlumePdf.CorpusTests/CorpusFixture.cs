using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Conformance-corpus scaffolding: open corpora (Arlington
/// PDF Model, veraPDF-corpus, a pinned mozilla/pdf.js test/pdfs subset) arrive via
/// scripts/fetch-corpora.sh into corpora/ (gitignored; Isartor and other
/// no-redistribution suites are fetched on demand and never committed).
/// Corpus-driven tests skip when the corpora are absent so the default CI lane
/// stays hermetic; the dedicated `corpus` CI job runs the fetch first.
/// </summary>
public static class CorpusFixture
{
    private const string SolutionFileName = "PlumePdf.sln";

    /// <summary>
    /// The repository root, found by walking up from the test assembly's output
    /// directory until <c>PlumePdf.sln</c> is found. Deliberately not a fixed
    /// "../../../../.." relative path: that breaks the moment a project's output
    /// path depth changes (a different TFM, a publish profile, an extra build
    /// configuration segment), which is exactly the fragility that calls for a
    /// "robust root resolution".
    /// </summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Root where <c>scripts/fetch-corpora.sh</c> places downloaded corpora.</summary>
    public static string CorporaRoot => Path.Combine(RepoRoot, "corpora");

    /// <summary>
    /// Root of the hand-crafted, self-authored Phase 1 fixtures (see
    /// <c>Fixtures/README.md</c> for per-file provenance). Always present in the
    /// repo — unlike <see cref="CorporaRoot"/>, nothing needs to be fetched.
    /// </summary>
    public static string FixturesRoot { get; } = Path.Combine(RepoRoot, "tests", "PlumePdf.CorpusTests", "Fixtures");

    /// <summary>Whether the open (Arlington/veraPDF) corpora have been fetched.</summary>
    public static bool CorporaAvailable => Directory.Exists(Path.Combine(CorporaRoot, "veraPDF-corpus-master"));

    /// <summary>
    /// The pinned pdf.js <c>test/pdfs</c> subset directory, if fetched. The
    /// directory name is suffixed with the pinned commit's short SHA (see
    /// <c>scripts/fetch-corpora.sh</c>), so this matches by prefix rather than
    /// hardcoding the pin here too — bumping the pin needs only one edit.
    /// </summary>
    public static string? Phase1PdfJsSubsetRoot => Directory.Exists(CorporaRoot)
        ? Directory.EnumerateDirectories(CorporaRoot, "pdfjs-subset-*").FirstOrDefault()
        : null;

    /// <summary>Whether the Phase 1 pdf.js subset has been fetched and is non-empty.</summary>
    public static bool Phase1CorpusAvailable =>
        Phase1PdfJsSubsetRoot is { } root && Directory.EnumerateFiles(root, "*.pdf").Any();

    /// <summary>Every hand-crafted fixture <c>.pdf</c> file (always available; nothing to fetch).</summary>
    public static IEnumerable<string> FixtureFiles =>
        Directory.Exists(FixturesRoot) ? Directory.EnumerateFiles(FixturesRoot, "*.pdf") : [];

    /// <summary>Every fetched pdf.js subset <c>.pdf</c> file, or empty if not fetched.</summary>
    public static IEnumerable<string> PdfJsSubsetFiles =>
        Phase1PdfJsSubsetRoot is { } root ? Directory.EnumerateFiles(root, "*.pdf") : [];

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root ({SolutionFileName}) above {AppContext.BaseDirectory}.");
    }
}

/// <summary>Proves the corpus-fetch wiring itself works, independent of any reading code.</summary>
public class CorpusSmokeTests
{
    [Fact]
    public void CorpusLaneWiring()
    {
        if (!CorpusFixture.CorporaAvailable)
        {
            // Hermetic lane: corpora not fetched; nothing to assert yet.
            return;
        }

        Assert.True(Directory.EnumerateFiles(CorpusFixture.CorporaRoot, "*.pdf", SearchOption.AllDirectories).Any(),
            "Corpora directory exists but contains no PDFs — fetch-corpora.sh is broken.");
    }

    [Fact]
    public void FixturesAreAlwaysPresent()
    {
        Assert.True(Directory.Exists(CorpusFixture.FixturesRoot), $"Fixtures directory not found at {CorpusFixture.FixturesRoot}.");
        Assert.NotEmpty(CorpusFixture.FixtureFiles);
    }

    [Fact]
    public void Phase1PdfJsSubsetWiringWhenFetched()
    {
        if (!CorpusFixture.Phase1CorpusAvailable)
        {
            // Hermetic lane: pdf.js subset not fetched; nothing to assert yet.
            return;
        }

        Assert.NotEmpty(CorpusFixture.PdfJsSubsetFiles);
    }
}
