using PlumePdf.Documents.PageRemoval;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Save-time named-destination clean-up after <c>doc.Pages.RemoveAt</c>: entries of the
/// catalog's <c>/Dests</c> dictionary and leaves of the <c>/Names /Dests</c> name tree that
/// resolve to a removed page are dropped, every surviving intermediate and leaf node's
/// <c>/Limits</c> is recomputed, an emptied leaf leaves its parent's <c>/Kids</c>, an emptied
/// <c>/Dests</c> or name tree is dropped, and an emptied <c>/Names</c> goes with it. Asserted on
/// the reopened file in all three layouts.
/// </summary>
public class NamedDestinationCleanupTests
{
    private static readonly byte[] Mixed = CleanupFixtures.Compose(
        "/Names << /Dests 61 0 R /JavaScript 66 0 R >> /Dests << /gone [4 0 R /Fit] >>",
        new Dictionary<int, string>
        {
            [61] = "<< /Kids [62 0 R 63 0 R] >>",
            [62] = "<< /Names [(a) [3 0 R /Fit] (b) [4 0 R /Fit] (c) [5 0 R /Fit]] /Limits [(a) (c)] >>",
            [63] = "<< /Names [(d) [4 0 R /Fit] (e) 67 0 R] /Limits [(d) (e)] >>",
            [67] = "[3 0 R /XYZ 0 0 0]",
            [66] = "<< /Names [(js) 68 0 R] >>",
            [68] = "<< /S /JavaScript /JS (1) >>",
        });

    public static TheoryData<string, SaveLayout> RemovalsByLayout() => CleanupFixtures.RemovalsByLayout();

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void Dests_DropsOnlyTheDeadName(string removal, SaveLayout layout)
    {
        // D: /Dests << /two → page 2, /one → page 1 >>.
        using var reopened = PdfDocument.Open(SaveShape("D", removal, layout));
        var dests = Assert.IsType<PdfDictionary>(CleanupFixtures.Get(reopened, CleanupFixtures.Catalog(reopened), "Dests"));
        Assert.Equal(["one"], dests.Keys.Select(static k => k.Value));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void NameTree_EmptiedLeafLeavesKids(string removal, SaveLayout layout)
    {
        // M: /Kids [(one) → page 1, (two) → page 2].
        using var reopened = PdfDocument.Open(SaveShape("M", removal, layout));
        Assert.Equal(["one"], TreeKeys(reopened, out var kidCount));
        Assert.Equal(1, kidCount);
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void NameTree_EmptiedTreeDropsNames(string removal, SaveLayout layout)
    {
        // E2: every entry targets page 2; /Names held nothing else.
        using var reopened = PdfDocument.Open(SaveShape("E2", removal, layout));
        Assert.False(CleanupFixtures.Catalog(reopened).ContainsKey(PdfName.Get("Names")));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void Mixed_LimitsAreRecomputed_OtherTreesKept_EmptiedDestsDropped(string removal, SaveLayout layout)
    {
        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(Mixed, removal, layout));
        var catalog = CleanupFixtures.Catalog(reopened);

        Assert.Equal(removal == "2" ? ["a", "c", "e"] : ["a", "e"], TreeKeys(reopened, out var kidCount));
        Assert.Equal(2, kidCount);
        var names = Assert.IsType<PdfDictionary>(CleanupFixtures.Get(reopened, catalog, "Names"));
        Assert.True(names.ContainsKey(PdfName.Get("JavaScript")));
        Assert.False(catalog.ContainsKey(PdfName.Get("Dests")));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void DirectLeafRoot_IsRewrittenInPlace(string removal, SaveLayout layout)
    {
        var source = CleanupFixtures.Compose(
            "/Names << /Dests << /Names [(x) [4 0 R /Fit] (y) [3 0 R /Fit]] >> >>",
            new Dictionary<int, string>());
        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(source, removal, layout));
        Assert.Equal(["y"], TreeKeys(reopened, out _));
    }

    [Fact]
    public void Pass_CountsAndExcludes_AndNeverTouchesTheDocument()
    {
        using var document = PdfDocument.Open(Mixed);
        document.Pages.RemoveAt(1);
        var before = CanonicalPdf.GraphSnapshot(document);

        var context = LinkAndOpenActionCleanupTests.ContextAfterRemovingPageTwo(document);
        NamedDestinationPass.Apply(context);

        // (b) and (d) in the tree, /gone in /Dests.
        Assert.Equal(3, context.Counts.NamedDestinations);
        Assert.NotNull(context.Catalog);
        Assert.False(context.Catalog.ContainsKey(PdfName.Get("Dests")));
        Assert.Equal(before, CanonicalPdf.GraphSnapshot(document));
    }

    // The /Names /Dests tree's keys in order, validating every non-root node's /Limits against
    // its subtree and the keys' ascending order; `kidCount` is the root's /Kids length (0 for a
    // leaf root).
    private static List<string> TreeKeys(PdfDocument document, out int kidCount)
    {
        var names = Assert.IsType<PdfDictionary>(CleanupFixtures.Get(document, CleanupFixtures.Catalog(document), "Names"));
        var root = Assert.IsType<PdfDictionary>(CleanupFixtures.Get(document, names, "Dests"));
        kidCount = CleanupFixtures.Get(document, root, "Kids") is PdfArray kids ? kids.Count : 0;
        var keys = new List<string>();
        Walk(document, root, isRoot: true, keys, depth: 0);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        return keys;
    }

    private static void Walk(PdfDocument document, PdfDictionary node, bool isRoot, List<string> keys, int depth)
    {
        Assert.True(depth < 16, "Name tree deeper than any fixture builds - a cycle?");
        var start = keys.Count;
        if (CleanupFixtures.Get(document, node, "Names") is PdfArray pairs)
        {
            Assert.True(pairs.Count > 0 && pairs.Count % 2 == 0, "A leaf with no or unpaired entries.");
            for (var i = 0; i < pairs.Count; i += 2)
            {
                keys.Add(Assert.IsType<PdfString>(CleanupFixtures.Resolve(document, pairs[i])).GetText());
                Assert.IsNotType<PdfNull>(CleanupFixtures.Resolve(document, pairs[i + 1]));
            }
        }

        if (CleanupFixtures.Get(document, node, "Kids") is PdfArray kids)
        {
            Assert.NotEmpty(kids);
            foreach (var kid in kids)
            {
                Walk(document, Assert.IsType<PdfDictionary>(CleanupFixtures.Resolve(document, kid)), isRoot: false, keys, depth + 1);
            }
        }

        if (!isRoot)
        {
            var limits = Assert.IsType<PdfArray>(CleanupFixtures.Get(document, node, "Limits"));
            Assert.Equal(keys[start], Assert.IsType<PdfString>(CleanupFixtures.Resolve(document, limits[0])).GetText());
            Assert.Equal(keys[^1], Assert.IsType<PdfString>(CleanupFixtures.Resolve(document, limits[1])).GetText());
        }
    }

    private static byte[] SaveShape(string shape, string removal, SaveLayout layout) =>
        CleanupFixtures.SaveAfterRemoval(RemovedPageFixtures.Build(shape).Bytes, removal, layout);
}
