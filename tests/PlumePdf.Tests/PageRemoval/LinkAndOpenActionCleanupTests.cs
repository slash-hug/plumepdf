using PlumePdf.Documents.PageRemoval;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Save-time clean-up of kept pages' annotations and of the catalog's open action after
/// <c>doc.Pages.RemoveAt</c>: a link whose destination is a removed page is dropped, every
/// kept page's <c>/Annots</c> is written without <c>null</c> entries (direct, even when it was
/// indirect), a kept annotation's <c>/Popup</c> or <c>/IRT</c> into a removed page's annotation
/// is dropped, and an <c>/OpenAction</c> to a removed page is removed. Links to kept pages,
/// remote (<c>GoToR</c>) links and other annotations survive. Asserted on the reopened file in
/// all three layouts.
/// </summary>
public class LinkAndOpenActionCleanupTests
{
    // Page 1 carries an indirect /Annots array mixing dead and live links; page 3 links to page 2.
    private static readonly byte[] MixedLinks = CleanupFixtures.Compose(
        string.Empty,
        new Dictionary<int, string>
        {
            [45] = "[40 0 R 41 0 R 42 0 R 43 0 R 44 0 R]",
            [40] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [4 0 R /Fit] /Contents (DEAD-EXPLICIT) >>",
            [41] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoTo /D [3 0 R /Fit] /Next << /S /GoTo /D [4 0 R /Fit] >> >> /Contents (KEPT-FIRST-GOTO) >>",
            [42] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (https://example.com/) /Next 46 0 R >> /Contents (DEAD-AFTER-URI) >>",
            [46] = "<< /S /GoTo /D [4 0 R /Fit] >>",
            [43] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoToR /F (other.pdf) /D [1 /Fit] >> /Contents (KEPT-REMOTE) >>",
            [44] = "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Contents (KEPT-NOTE) >>",
            [47] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [4 0 R /Fit] /Contents (DEAD-ON-PAGE-THREE) >>",
        },
        "/Annots 45 0 R",
        string.Empty,
        "/Annots [47 0 R]");

    public static TheoryData<string, SaveLayout> RemovalsByLayout() => CleanupFixtures.RemovalsByLayout();

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void DeadLink_IsDroppedAndTheEmptiedAnnotsWithIt(string removal, SaveLayout layout)
    {
        // Shape L: page 1's only annotation links to page 2.
        using var reopened = PdfDocument.Open(SaveShape("L", removal, layout));
        Assert.False(reopened.Pages[0].Dictionary.ContainsKey(PdfName.Annots));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void MixedLinks_OnlyTheDeadOnesGo(string removal, SaveLayout layout)
    {
        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(MixedLinks, removal, layout));

        // The indirect array became a direct, compacted one.
        Assert.IsType<PdfArray>(reopened.Pages[0].Dictionary[PdfName.Annots]);
        var kept = CleanupFixtures.Annotations(reopened, 0).Select(a => CleanupFixtures.ContentsText(reopened, a));
        Assert.Equal(["KEPT-FIRST-GOTO", "KEPT-REMOTE", "KEPT-NOTE"], kept);

        if (removal == "2")
        {
            Assert.Empty(CleanupFixtures.Annotations(reopened, 1));
            Assert.False(reopened.Pages[1].Dictionary.ContainsKey(PdfName.Annots));
        }
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void SharedLinkToAKeptPage_Survives(string removal, SaveLayout layout)
    {
        using var reopened = PdfDocument.Open(SaveShape("S", removal, layout));
        var link = Assert.Single(CleanupFixtures.Annotations(reopened, 0));
        Assert.Equal("SHARED-LINK-MARK", CleanupFixtures.ContentsText(reopened, link));
        var destination = Assert.IsType<PdfArray>(CleanupFixtures.Get(reopened, link, "Dest"));
        Assert.Equal(reopened.Pages[0].Reference, Assert.IsType<PdfReference>(destination[0]).Target);
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void KeptReply_LosesItsInReplyToTheRemovedAnnotation(string removal, SaveLayout layout)
    {
        // Shape X: kept page 1's reply points (/IRT) at an annotation of the removed page.
        using var reopened = PdfDocument.Open(SaveShape("X", removal, layout));
        var reply = Assert.Single(CleanupFixtures.Annotations(reopened, 0));
        Assert.Equal("KEPT-REPLY-MARK", CleanupFixtures.ContentsText(reopened, reply));
        Assert.False(reply.ContainsKey(PdfName.Get("IRT")));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void KeptNote_LosesItsPopupOnTheRemovedPage(string removal, SaveLayout layout)
    {
        // Shape P: kept page 1's note has its /Popup on the removed page.
        using var reopened = PdfDocument.Open(SaveShape("P", removal, layout));
        var note = Assert.Single(CleanupFixtures.Annotations(reopened, 0));
        Assert.Equal("KEPT-NOTE-MARK", CleanupFixtures.ContentsText(reopened, note));
        Assert.False(note.ContainsKey(PdfName.Get("Popup")));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void LinksThroughNamedDestinations_AreDropped(string removal, SaveLayout layout)
    {
        // D links by /Dests name, M by /Names /Dests string; both land on page 2.
        foreach (var shape in new[] { "D", "M" })
        {
            using var reopened = PdfDocument.Open(SaveShape(shape, removal, layout));
            Assert.False(reopened.Pages[0].Dictionary.ContainsKey(PdfName.Annots), $"Shape {shape} kept its dead link.");
        }
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void OpenActionToARemovedPage_IsRemoved(string removal, SaveLayout layout)
    {
        using var reopened = PdfDocument.Open(SaveShape("O", removal, layout));
        Assert.False(CleanupFixtures.Catalog(reopened).ContainsKey(PdfName.Get("OpenAction")));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void OpenGoToActionToARemovedPage_IsRemoved(string removal, SaveLayout layout)
    {
        var source = CleanupFixtures.Compose("/OpenAction 70 0 R", new Dictionary<int, string> { [70] = "<< /S /GoTo /D [4 0 R /XYZ 0 200 0] >>" });
        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(source, removal, layout));
        Assert.False(CleanupFixtures.Catalog(reopened).ContainsKey(PdfName.Get("OpenAction")));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void OpenActionToAKeptPage_Stays(string removal, SaveLayout layout)
    {
        var source = CleanupFixtures.Compose("/OpenAction [3 0 R /Fit]", new Dictionary<int, string>());
        using var reopened = PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(source, removal, layout));
        var action = Assert.IsType<PdfArray>(CleanupFixtures.Get(reopened, CleanupFixtures.Catalog(reopened), "OpenAction"));
        Assert.Equal(reopened.Pages[0].Reference, Assert.IsType<PdfReference>(action[0]).Target);
    }

    [Fact]
    public void Passes_CountWhatTheyRemove_AndNeverTouchTheDocument()
    {
        var source = CleanupFixtures.Compose("/OpenAction [4 0 R /Fit]", new Dictionary<int, string>
        {
            [40] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [4 0 R /Fit] >>",
            [41] = "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [5 0 R /Fit] >>",
        }, "/Annots [40 0 R 41 0 R]");
        using var document = PdfDocument.Open(source);
        document.Pages.RemoveAt(1);
        var before = CanonicalPdf.GraphSnapshot(document);

        var context = ContextAfterRemovingPageTwo(document);
        AnnotsPass.Apply(context);
        OpenActionPass.Apply(context);

        Assert.Equal(1, context.Counts.Links);
        Assert.Equal(1, context.Counts.OpenActions);
        Assert.Contains(40, context.Excluded);
        Assert.DoesNotContain(41, context.Excluded);
        Assert.Equal(before, CanonicalPdf.GraphSnapshot(document));
    }

    // Page 2 (object 4) removed from a flat three-page tree (object 2).
    internal static SaveCleanupContext ContextAfterRemovingPageTwo(PdfDocument document) =>
        new(
            document.Objects,
            document.Catalog!.Reference,
            document.Catalog.Dictionary,
            [.. document.Pages.Select(static p => (p.Reference, p.Dictionary))],
            [2, 4],
            [4],
            PdfOptions.Default);

    private static byte[] SaveShape(string shape, string removal, SaveLayout layout) =>
        CleanupFixtures.SaveAfterRemoval(RemovedPageFixtures.Build(shape).Bytes, removal, layout);
}
