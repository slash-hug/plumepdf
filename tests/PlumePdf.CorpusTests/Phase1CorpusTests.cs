using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Phase 1 exit-gate corpus tests (docs/spec.md "Quality gates"): every fetched real-world PDF (the pinned
/// mozilla/pdf.js <c>test/pdfs</c> subset, see <c>scripts/fetch-corpora.sh</c>)
/// and every hand-crafted fixture (<c>Fixtures/</c>, see its README for
/// per-file provenance) must either open cleanly or fail with a coded
/// <see cref="PlumePdfException"/> — never a bare exception (AGENTS.md "Rules
/// that bite": "never a bare Exception"). Clean, non-corrupted, non-encrypted
/// fixtures additionally round-trip through both save paths.
///
/// Depends on the merged reading + writing surface
/// (<see cref="PdfDocument"/>, <see cref="PdfOptions"/>) — written against the
/// contract fixed ahead of time so it compiles once that surface lands.
///
/// Skips (produces zero theory cases, not a failure) when a corpus source
/// isn't present, so the default hermetic CI lane stays green; the dedicated
/// `corpus` CI job runs <c>scripts/fetch-corpora.sh</c> first so these
/// actually execute there. The hand-crafted fixtures are always present
/// (nothing to fetch), so their cases always run.
/// </summary>
public class Phase1CorpusTests
{
    /// <summary>Every fetched pdf.js subset file — empty (not failing) if unfetched.</summary>
    public static TheoryData<string> PdfJsSubsetFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in CorpusFixture.PdfJsSubsetFiles)
        {
            data.Add(path);
        }

        if (data.Count == 0)
        {
            // xUnit v2 fails a [Theory] outright when its MemberData yields zero rows
            // ("No data found for ..."), rather than the "skip, don't fail" behavior this
            // suite's own summary documents for an unfetched corpus. A single empty-string
            // sentinel keeps the hermetic lane green without a real fixture to iterate;
            // Open_PdfJsSubsetFile_SucceedsOrThrowsCodedException treats it as a no-op.
            data.Add(string.Empty);
        }

        return data;
    }

    /// <summary>Every hand-crafted fixture file (always present).</summary>
    public static TheoryData<string> HandCraftedFixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var path in CorpusFixture.FixtureFiles)
        {
            data.Add(path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PdfJsSubsetFiles))]
    public void Open_PdfJsSubsetFile_SucceedsOrThrowsCodedException(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            // Sentinel from PdfJsSubsetFiles(): the pdf.js subset was never fetched.
            return;
        }

        AssertOpensCleanlyOrCoded(path);
    }

    [Theory]
    [MemberData(nameof(HandCraftedFixtureFiles))]
    public void Open_HandCraftedFixture_SucceedsOrThrowsCodedException(string path) => AssertOpensCleanlyOrCoded(path);

    private static void AssertOpensCleanlyOrCoded(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            Assert.NotNull(document);
        }
        catch (PlumePdfException codedException)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(codedException.Code),
                $"{Path.GetFileName(path)}: PlumePdfException without a stable Code — every failure needs one (AGENTS.md).");
        }
        catch (Exception bareException)
        {
            Assert.Fail(
                $"{Path.GetFileName(path)}: threw a bare {bareException.GetType().Name} instead of a coded " +
                $"PlumePdfException — {bareException.Message}");
        }
    }

    /// <summary>
    /// Fixtures that are guaranteed clean (no deliberate corruption, no
    /// encryption) — see Fixtures/README.md. These, and only these, are
    /// expected to round-trip through both save paths.
    /// </summary>
    public static TheoryData<string> CleanFixtureFiles()
    {
        string[] cleanNames =
        [
            "classic-xref.pdf",
            "xref-stream.pdf",
            "object-stream.pdf",
            "hybrid.pdf",
            "hybrid-freemarked.pdf",
            "linearized-ish.pdf",
        ];

        var data = new TheoryData<string>();
        foreach (var name in cleanNames)
        {
            var path = Path.Combine(CorpusFixture.FixturesRoot, name);
            if (File.Exists(path))
            {
                data.Add(path);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CleanFixtureFiles))]
    public void CleanFixture_RoundTripsThroughBothSavePaths(string path)
    {
        using var original = PdfDocument.Open(path);
        var expected = SnapshotPages(original);

        var incrementalPath = Path.Combine(Path.GetTempPath(), $"plumepdf-corpus-{Guid.NewGuid():N}-incremental.pdf");
        var fullRewritePath = Path.Combine(Path.GetTempPath(), $"plumepdf-corpus-{Guid.NewGuid():N}-full.pdf");
        try
        {
            original.SaveIncremental(incrementalPath);
            using (var reopened = PdfDocument.Open(incrementalPath))
            {
                Assert.NotNull(reopened.Objects.Trailer);
                AssertSamePages(expected, SnapshotPages(reopened), path, "SaveIncremental");
            }

            original.Save(fullRewritePath, new PdfOptions { Deterministic = true });
            using (var reopened = PdfDocument.Open(fullRewritePath))
            {
                Assert.NotNull(reopened.Objects.Trailer);
                AssertSamePages(expected, SnapshotPages(reopened), path, "Save (full rewrite)");
            }
        }
        finally
        {
            File.Delete(incrementalPath);
            File.Delete(fullRewritePath);
        }
    }

    /// <summary>
    /// Per-page key set and decoded content-stream bytes, for comparing a document against
    /// its own SaveIncremental/Save output. Asserting only <c>doc.Objects.Trailer is not
    /// null</c> (the previous version of this test) stays green even if a save path silently
    /// drops page content or dictionary entries — this is what actually proves nothing was
    /// lost, matching the "open any real-world PDF... save both ways" Phase 1 exit demo.
    /// </summary>
    private static List<(HashSet<string> Keys, byte[] Content)> SnapshotPages(PdfDocument document)
    {
        var result = new List<(HashSet<string> Keys, byte[] Content)>();
        var contentsName = PdfName.Get("Contents");

        foreach (var page in document.Pages)
        {
            var keys = new HashSet<string>(page.Dictionary.Keys.Select(static k => k.Value));

            byte[] content = [];
            if (page.Dictionary.TryGetValue(contentsName, out var contentsValue))
            {
                var resolved = contentsValue is PdfReference reference ? document.Objects[reference.Target] : contentsValue;
                if (resolved is PdfStream stream)
                {
                    content = stream.GetDecodedBytes(PdfFilterRegistry.Default);
                }
            }

            result.Add((keys, content));
        }

        return result;
    }

    private static void AssertSamePages(List<(HashSet<string> Keys, byte[] Content)> expected, List<(HashSet<string> Keys, byte[] Content)> actual, string path, string savePath)
    {
        var name = Path.GetFileName(path);
        Assert.True(expected.Count == actual.Count, $"{name} via {savePath}: page count changed ({expected.Count} -> {actual.Count}).");

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].Keys.SetEquals(actual[i].Keys), $"{name} via {savePath}: page {i}'s key set changed ({string.Join(',', expected[i].Keys)} -> {string.Join(',', actual[i].Keys)}).");
            Assert.True(expected[i].Content.AsSpan().SequenceEqual(actual[i].Content), $"{name} via {savePath}: page {i}'s decoded content-stream bytes changed.");
        }
    }
}
