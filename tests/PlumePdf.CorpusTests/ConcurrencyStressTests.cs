using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The corpus-lane concurrency proof for the rasterizer's thread-safety guarantee
/// ("N threads rasterizing one open document produce identical, non-corrupted output") and its
/// mechanism: the per-call <c>ScratchObjectRegistry.CreateFor(...)</c> seam gives
/// every <c>Rasterize</c> call its own private, discarded-on-return write map over a shared
/// read-through resolver, so N concurrent calls — on the same open <see cref="PdfDocument"/> or
/// across many independently-opened documents — must never observe or produce anything other
/// than what a purely serial run would. This class proves that observably, from the public API,
/// across every always-present hand-crafted fixture (no <c>fetch-corpora.sh</c> prerequisite —
/// unlike <see cref="RasterOracleTests"/>'s SSIM comparison, pixel-identity needs no external
/// oracle, so this lane runs unconditionally in every CI job, not just the armed <c>corpus</c>
/// one).
/// </summary>
/// <remarks>
/// "Corpus-lane" here means "lives in the
/// <c>PlumePdf.CorpusTests</c> project," not "requires fetched corpora" — every fixture this
/// class rasterizes is one of the always-committed <see cref="CorpusFixture.FixtureFiles"/>.
/// Encrypted (<c>AES-*</c>/<c>RC4-*</c>/<c>qpdf-aes*</c>) and deliberately-malformed
/// (<c>broken-xref.pdf</c>/<c>truncated.pdf</c>) fixtures are excluded — this suite proves
/// concurrency safety of a successful render, not decryption or repair-path behavior, which
/// other suites already cover.
/// </remarks>
public class ConcurrencyStressTests
{
    /// <summary>
    /// Every always-present, cleanly-renderable fixture this suite stresses — deliberately more
    /// than <see cref="RasterOracleTests"/>'s six-fixture curated SSIM list (adds
    /// <c>hybrid-freemarked.pdf</c> and <c>xref-stream.pdf</c>) since "many corpus documents" is
    /// this suite's own mandate, not an SSIM-calibration concern.
    /// </summary>
    private static readonly string[] CuratedFixtureNames =
    [
        "classic-xref.pdf",
        "three-pages.pdf",
        "object-stream.pdf",
        "hybrid.pdf",
        "hybrid-freemarked.pdf",
        "linearized-ish.pdf",
        "simple-form.pdf",
        "xref-stream.pdf",
    ];

    private const int ParallelRepeatsPerFixture = 12;

    private static string FixturePath(string fixtureName) => Path.Combine(CorpusFixture.FixturesRoot, fixtureName);

    /// <summary>
    /// The corpus-driven-enumeration discipline: a suite that iterates a
    /// fixture list must assert the list is actually non-empty and every named file actually
    /// exists, so a future rename/deletion fails loudly here rather than letting every other
    /// test in this class vacuously pass over zero fixtures.
    /// </summary>
    [Fact]
    public void CuratedFixtureList_IsNonEmptyAndEveryFileExists()
    {
        Assert.NotEmpty(CuratedFixtureNames);
        foreach (var name in CuratedFixtureNames)
        {
            Assert.True(File.Exists(FixturePath(name)), $"Curated fixture missing: {name}");
        }
    }

    /// <summary>
    /// Baseline self-consistency: rasterizing the very same fixture repeatedly, purely serially,
    /// must always produce byte-identical pixel output — the reference every parallel comparison
    /// below is measured against. If this fails, the rasterizer itself is non-deterministic and
    /// the parallel tests below would not be measuring what they claim to.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void SerialRasters_AreSelfConsistent(string fixtureName)
    {
        var path = FixturePath(fixtureName);
        var reference = Pdf.Rasterize(path).Frames[0];

        for (var i = 0; i < 5; i++)
        {
            var repeat = Pdf.Rasterize(path).Frames[0];
            AssertFramesIdentical(reference, repeat, fixtureName, $"serial repeat #{i}");
        }
    }

    /// <summary>
    /// Thread-safety, direct form: <see cref="ParallelRepeatsPerFixture"/> threads all call
    /// <c>document.Pages[0].Rasterize(...)</c> on the SAME already-open <see cref="PdfDocument"/>
    /// instance concurrently. Every thread's result must be byte-identical to a serial reference
    /// rendered before any concurrency starts — proving concurrent <c>Rasterize</c> calls on one
    /// shared document never observe or produce cross-call corruption (the exact hazard a
    /// document.Objects-mutating widget-appearance-synthesis implementation, the "easy wrong"
    /// alternative to the scratch registry it actually uses, would introduce).
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void ParallelRasters_OnOneSharedDocument_MatchSerialReference(string fixtureName)
    {
        using var document = PdfDocument.Open(FixturePath(fixtureName));
        var reference = document.Pages[0].Rasterize().Frames[0];

        var results = new RasterImageFrame[ParallelRepeatsPerFixture];
        Parallel.For(0, ParallelRepeatsPerFixture, i =>
        {
            results[i] = document.Pages[0].Rasterize().Frames[0];
        });

        for (var i = 0; i < results.Length; i++)
        {
            AssertFramesIdentical(reference, results[i], fixtureName, $"parallel thread #{i} (shared document)");
        }
    }

    /// <summary>
    /// Parallel-vs-serial pixel-identity across many corpus documents, literally: every
    /// curated fixture gets its own independently-opened
    /// <see cref="PdfDocument"/>, a serial reference render is captured for each up front, and
    /// then every (document, repeat) pair races against every other one on the thread pool —
    /// proving isolation holds not just within one shared document (the test above) but across
    /// many concurrently-open documents at once, the broader real-world shape (a server
    /// rasterizing pages from many different requests' documents simultaneously).
    /// </summary>
    [Fact]
    public void ParallelRasters_AcrossManyOpenDocuments_MatchSerialReferences()
    {
        var documents = new PdfDocument[CuratedFixtureNames.Length];
        var references = new RasterImageFrame[CuratedFixtureNames.Length];
        try
        {
            for (var i = 0; i < CuratedFixtureNames.Length; i++)
            {
                documents[i] = PdfDocument.Open(FixturePath(CuratedFixtureNames[i]));
                references[i] = documents[i].Pages[0].Rasterize().Frames[0];
            }

            var workItems = new (int DocumentIndex, int Repeat)[CuratedFixtureNames.Length * ParallelRepeatsPerFixture];
            var w = 0;
            for (var d = 0; d < CuratedFixtureNames.Length; d++)
            {
                for (var r = 0; r < ParallelRepeatsPerFixture; r++)
                {
                    workItems[w++] = (d, r);
                }
            }

            var results = new RasterImageFrame[workItems.Length];
            Parallel.For(0, workItems.Length, i =>
            {
                var (documentIndex, _) = workItems[i];
                results[i] = documents[documentIndex].Pages[0].Rasterize().Frames[0];
            });

            for (var i = 0; i < workItems.Length; i++)
            {
                var (documentIndex, repeat) = workItems[i];
                AssertFramesIdentical(
                    references[documentIndex], results[i],
                    CuratedFixtureNames[documentIndex], $"parallel work item (repeat #{repeat}, multi-document)");
            }
        }
        finally
        {
            foreach (var document in documents)
            {
                document?.Dispose();
            }
        }
    }

    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in CuratedFixtureNames)
        {
            data.Add(name);
        }

        return data;
    }

    private static void AssertFramesIdentical(RasterImageFrame reference, RasterImageFrame actual, string fixtureName, string context)
    {
        Assert.True(reference.Width == actual.Width && reference.Height == actual.Height && reference.Format == actual.Format,
            $"{fixtureName} ({context}): dimensions/format diverged from the serial reference " +
            $"({reference.Width}x{reference.Height} {reference.Format} vs {actual.Width}x{actual.Height} {actual.Format}).");

        Assert.True(reference.Pixels.Span.SequenceEqual(actual.Pixels.Span),
            $"{fixtureName} ({context}): pixel bytes diverged from the serial reference — concurrent Rasterize calls are not producing byte-identical output.");
    }
}
