using PlumePdf.Documents.PageRemoval;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Save-time outline clean-up after <c>doc.Pages.RemoveAt</c>: a bookmark whose destination
/// (<c>/Dest</c>, or an <c>/A</c> <c>GoTo</c>, by explicit array or by name) is a removed page is
/// deleted, its children move up to its parent in its place, in order, and every
/// <c>/First</c>/<c>/Last</c>/<c>/Prev</c>/<c>/Next</c>/<c>/Parent</c>/<c>/Count</c> is rebuilt
/// (signed <c>/Count</c>: positive for an open item with that many visible descendants, negative
/// for a closed one). An outline left empty is dropped from the catalog. Asserted on the
/// reopened file in all three layouts; <see cref="CleanupFixtures.OutlineShape"/> validates
/// every link it walks.
/// </summary>
public class OutlineCleanupTests
{
    // Intro → p1; Chapter → p3, open: [Removed Section → p2, closed: [Sub A → p1, Sub B → p3],
    // Kept Section → p1]; Closed Part → p1, closed: [Dead Child (GoTo p2), open: [Grandchild →
    // p1], Live Child → p1]; Dead Top → p2.
    private static readonly byte[] Nested = CleanupFixtures.Compose(
        "/Outlines 20 0 R",
        new Dictionary<int, string>
        {
            [20] = "<< /Type /Outlines /First 21 0 R /Last 24 0 R /Count 6 >>",
            [21] = "<< /Title (Intro) /Parent 20 0 R /Next 22 0 R /Dest [3 0 R /Fit] >>",
            [22] = "<< /Title (Chapter) /Parent 20 0 R /Prev 21 0 R /Next 23 0 R /First 25 0 R /Last 26 0 R /Count 2 /Dest [5 0 R /Fit] >>",
            [25] = "<< /Title (Removed Section) /Parent 22 0 R /Next 26 0 R /First 27 0 R /Last 28 0 R /Count -2 /Dest [4 0 R /Fit] >>",
            [27] = "<< /Title (Sub A) /Parent 25 0 R /Next 28 0 R /Dest [3 0 R /Fit] >>",
            [28] = "<< /Title (Sub B) /Parent 25 0 R /Prev 27 0 R /Dest [5 0 R /Fit] >>",
            [26] = "<< /Title (Kept Section) /Parent 22 0 R /Prev 25 0 R /Dest [3 0 R /Fit] >>",
            [23] = "<< /Title (Closed Part) /Parent 20 0 R /Prev 22 0 R /Next 24 0 R /First 29 0 R /Last 31 0 R /Count -3 /Dest [3 0 R /Fit] >>",
            [29] = "<< /Title (Dead Child) /Parent 23 0 R /Next 31 0 R /First 30 0 R /Last 30 0 R /Count 1 /A << /S /GoTo /D [4 0 R /Fit] >> >>",
            [30] = "<< /Title (Grandchild) /Parent 29 0 R /Dest [3 0 R /Fit] >>",
            [31] = "<< /Title (Live Child) /Parent 23 0 R /Prev 29 0 R /Dest [3 0 R /Fit] >>",
            [24] = "<< /Title (Dead Top) /Parent 20 0 R /Prev 23 0 R /Dest [4 0 R /Fit] >>",
        });

    public static TheoryData<string, SaveLayout> RemovalsByLayout() => CleanupFixtures.RemovalsByLayout();

    public static TheoryData<SaveLayout> Layouts() => CleanupFixtures.Layouts();

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void DeadBookmark_IsDeleted(string removal, SaveLayout layout)
    {
        // A: /Dest arrays; G: /A GoTo actions. The second item targets page 1 and survives.
        using (var reopened = PdfDocument.Open(SaveShape("A", removal, layout)))
        {
            Assert.Equal("Count=1: OUTLINE-TO-ONE-MARK", CleanupFixtures.OutlineShape(reopened));
        }

        using (var reopened = PdfDocument.Open(SaveShape("G", removal, layout)))
        {
            Assert.Equal("Count=1: GOTO-OUTLINE-ONE-MARK", CleanupFixtures.OutlineShape(reopened));
        }
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void EmptiedOutline_IsDroppedFromTheCatalog(string removal, SaveLayout layout)
    {
        // E1: every item targets page 2. D: the only item targets page 2 through a /Dests name.
        foreach (var shape in new[] { "E1", "D" })
        {
            using var reopened = PdfDocument.Open(SaveShape(shape, removal, layout));
            Assert.Null(CleanupFixtures.OutlineShape(reopened));
        }
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Nested_ChildrenMoveUpInPlace_AndCountsAreRecomputed(SaveLayout layout)
    {
        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(Nested, "2", layout));
        Assert.Equal(
            "Count=6: Intro, Chapter(+3)[Sub A, Sub B, Kept Section], Closed Part(-2)[Grandchild, Live Child]",
            CleanupFixtures.OutlineShape(reopened));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Nested_DeletedOpenParentAndDeletedChild_PromoteTheirSurvivors(SaveLayout layout)
    {
        // Removing pages 2 and 3 also deletes Chapter (→ page 3) and Sub B: Chapter's surviving
        // descendants (Sub A, through the deleted Removed Section, then Kept Section) take its place.
        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(Nested, "2+3", layout));
        Assert.Equal(
            "Count=4: Intro, Sub A, Kept Section, Closed Part(-2)[Grandchild, Live Child]",
            CleanupFixtures.OutlineShape(reopened));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void CyclicSiblingChain_IsRebuiltWithoutTheCycle(SaveLayout layout)
    {
        var source = CleanupFixtures.Compose("/Outlines 20 0 R", new Dictionary<int, string>
        {
            [20] = "<< /Type /Outlines /First 21 0 R /Last 22 0 R /Count 2 >>",
            [21] = "<< /Title (One) /Parent 20 0 R /Next 22 0 R /Dest [3 0 R /Fit] >>",
            [22] = "<< /Title (Two) /Parent 20 0 R /Prev 21 0 R /Next 21 0 R /Dest [4 0 R /Fit] >>",
        });

        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(source, "2", layout));
        Assert.Equal("Count=1: One", CleanupFixtures.OutlineShape(reopened));
    }

    [Fact]
    public void Pass_CountsAndExcludesDeletedItems_AndNeverTouchesTheDocument()
    {
        using var document = PdfDocument.Open(Nested);
        document.Pages.RemoveAt(1);
        var before = CanonicalPdf.GraphSnapshot(document);

        var context = LinkAndOpenActionCleanupTests.ContextAfterRemovingPageTwo(document);
        OutlinePass.Apply(context);

        Assert.Equal(3, context.Counts.Bookmarks);
        Assert.Superset(new HashSet<int> { 25, 29, 24 }, context.Excluded);
        Assert.False(context.Replacements.ContainsKey(21), "An item whose links did not change was replaced.");
        Assert.Equal(before, CanonicalPdf.GraphSnapshot(document));
    }

    private static byte[] SaveShape(string shape, string removal, SaveLayout layout) =>
        CleanupFixtures.SaveAfterRemoval(RemovedPageFixtures.Build(shape).Bytes, removal, layout);
}
