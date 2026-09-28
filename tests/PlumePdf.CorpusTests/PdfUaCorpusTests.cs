using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Sweeps veraPDF-corpus's PDF/UA-1 fixtures read-side, through the public
/// <see cref="PdfStructureInfo"/> facade. Every fixture opens and its structure tree (if any)
/// parses without throwing — the lenient "open anything" philosophy, extended to
/// tagged-PDF reading: a non-conformant real-world PDF/UA fixture (veraPDF-corpus's whole point
/// is a mix of conforming and deliberately-non-conforming files) degrades to diagnostics, never
/// an unhandled exception. Every fixture whose name does **not** mark it deliberately
/// non-conformant (the corpus's own <c>-pass-</c>/<c>-fail-</c> naming convention, used the
/// same way <c>PdfAValidatorCorpusTests</c>/<c>VeraPdfInteropTests</c> do) gets its per-page
/// marked-content order checked for internal consistency: no MCID appears twice within one
/// page's structure-tree order, which would mean the tree lists the same marked-content span
/// under two different structure elements (a malformed tree, not a PlumePDF bug — this is a
/// structural-soundness sweep, not a ground-truth text comparison, which the corpus's own
/// PDF/UA fixtures carry no independent oracle for). <c>-fail-</c> fixtures are read-only
/// swept (must not throw) and excluded from the no-duplicate-MCID assertion: some of them —
/// e.g. <c>7.20-t02-fail-a.pdf</c> — are deliberately authored to violate exactly this PDF/UA-1
/// rule (a structure element referencing a content item another element also references), so
/// PlumePDF's read side must faithfully reflect that duplication rather than a test asserting
/// it away.
/// </summary>
/// <remarks>
/// Hermetic-lane: self-skips (a no-op pass) when the corpus hasn't been fetched
/// (<c>scripts/fetch-corpora.sh</c>), matching the house rule <c>ExtractionCorpusTests.cs</c>
/// established — the default CI lane stays hermetic; the dedicated <c>corpus</c> job fetches
/// first. veraPDF-corpus's PDF/UA-1 fixtures live under a top-level directory whose exact name
/// this file doesn't hardcode a single spelling for (case/punctuation has shifted across
/// snapshots of the upstream repo) — any directory containing "UA" (case-insensitive) is swept.
/// </remarks>
public class PdfUaCorpusTests
{
    [Fact]
    public void PdfUaFixtures_OpenAndReadStructureTree_NeverThrows_AndReadingOrderHasNoDuplicateMcids()
    {
        if (!CorpusFixture.CorporaAvailable)
        {
            return; // Hermetic lane: corpus not fetched.
        }

        // Anti-vacuity: with the corpus fetched, finding zero PDF/UA fixtures means the
        // corpus layout changed (a renamed UA directory) — that must fail loudly, never
        // read as a green sweep of nothing.
        var fixtures = FindPdfUaFixtures().ToList();
        Assert.True(fixtures.Count > 0, "corpora/veraPDF-corpus-master is fetched but contains no PDF/UA fixture directory — the corpus layout changed and this sweep would otherwise pass having swept zero files.");

        var sweepCount = 0;
        foreach (var path in fixtures)
        {
            using var document = PdfDocument.Open(path);

            // The read must never throw for any real-world fixture, conformant or not - a
            // malformed /StructTreeRoot degrades to Root == null plus a diagnostic, exactly like
            // every other recoverable deviation the reading engine tolerates.
            var structure = PdfStructureInfo.For(document);
            _ = structure.IsTagged;
            _ = structure.Language;

            var isDeliberatelyNonConformant = Path.GetFileName(path).Contains("-fail-", StringComparison.OrdinalIgnoreCase);

            if (structure.Root is { } root && !isDeliberatelyNonConformant)
            {
                var mcidsByPage = new Dictionary<int, List<int>>();
                CollectMcids(root, mcidsByPage);

                foreach (var (_, mcids) in mcidsByPage)
                {
                    Assert.Equal(mcids.Count, mcids.Distinct().Count());
                }
            }

            sweepCount++;
        }

        Assert.True(sweepCount > 0, "Expected at least one PDF/UA-1 fixture to have been swept.");
    }

    private static void CollectMcids(PdfStructureNode node, Dictionary<int, List<int>> mcidsByPage)
    {
        switch (node)
        {
            case PdfMarkedContentReference mcr:
                if (!mcidsByPage.TryGetValue(mcr.PageIndex, out var list))
                {
                    list = [];
                    mcidsByPage[mcr.PageIndex] = list;
                }

                list.Add(mcr.Mcid);
                break;

            case PdfStructureElement element:
                foreach (var child in element.Children)
                {
                    CollectMcids(child, mcidsByPage);
                }

                break;
        }
    }

    private static IEnumerable<string> FindPdfUaFixtures()
    {
        var corpusRoot = Path.Combine(CorpusFixture.CorporaRoot, "veraPDF-corpus-master");
        if (!Directory.Exists(corpusRoot))
        {
            yield break;
        }

        var uaDirectories = Directory.EnumerateDirectories(corpusRoot, "*", SearchOption.AllDirectories)
            .Where(static d => Path.GetFileName(d).Contains("UA", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static d => d, StringComparer.Ordinal)
            .ToList();

        if (uaDirectories.Count == 0)
        {
            yield break;
        }

        foreach (var directory in uaDirectories)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.AllDirectories).OrderBy(static f => f, StringComparer.Ordinal))
            {
                yield return file;
            }
        }
    }
}
