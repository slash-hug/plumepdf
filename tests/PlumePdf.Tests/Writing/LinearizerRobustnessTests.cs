using PlumePdf.Tests.PageRemoval;
using PlumePdf.Tests.TestSupport;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// Linearized saves of documents the other layouts already handle: a bookmark whose action lists
/// form fields (issue #19: the outline hint table must count the fields the outline reaches), and a
/// malformed form after a page removal (issue #20: the linearizer must never reach an object its
/// classification did not number).
/// </summary>
public class LinearizerRobustnessTests
{
    [Theory]
    [InlineData(SaveLayout.Save)]
    [InlineData(SaveLayout.Optimize)]
    [InlineData(SaveLayout.Linearize)]
    public void BookmarkActionListingFormFields_IsQpdfClean(SaveLayout layout)
    {
        var bytes = CleanupFixtures.Compose(
            "/Outlines 20 0 R /AcroForm << /Fields [30 0 R 31 0 R] >>",
            new Dictionary<int, string>
            {
                [20] = "<< /Type /Outlines /First 21 0 R /Last 21 0 R /Count 1 >>",
                [21] = "<< /Title (Submit) /Parent 20 0 R /Dest [3 0 R /Fit] /A << /S /SubmitForm /F (https://example.invalid/) /Fields [30 0 R 31 0 R] >> >>",
                [30] = "<< /FT /Tx /T (a) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>",
                [31] = "<< /FT /Tx /T (b) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R >>",
            },
            "/Annots [30 0 R]", "/Annots [31 0 R]");

        using var document = PdfDocument.Open(bytes);
        var saved = RemovedPageFixtures.SaveToBytes(document, layout);

        CleanupFixtures3b.AssertQpdfClean(saved, $"bookmark submit action, {layout}");
    }

    [Theory]
    [InlineData(SaveLayout.Save)]
    [InlineData(SaveLayout.Optimize)]
    [InlineData(SaveLayout.Linearize)]
    public void MalformedFormNamingTheRemovedPage_NeitherLeaksItNorFails(SaveLayout layout)
    {
        // /AcroForm names the page being removed; the kept page lists a widget whose /P is that
        // removed page and whose field chain loops (80 -> 81 -> 80).
        var bytes = CleanupFixtures.Compose(
            "/AcroForm 4 0 R",
            new Dictionary<int, string>
            {
                [80] = "<< /T (loop) /Kids [81 0 R] >>",
                [81] = "<< /T (loopchild) /Parent 80 0 R /Kids [80 0 R 82 0 R] >>",
                [82] = "<< /Type /Annot /Subtype /Widget /Parent 81 0 R /P 4 0 R /Rect [0 0 10 10] >>",
            },
            "/Annots [82 0 R]");

        using var document = PdfDocument.Open(bytes);
        document.Pages.RemoveAt(1);
        var saved = RemovedPageFixtures.SaveToBytes(document, layout);

        using var reopened = PdfDocument.Open(saved);
        Assert.Equal(2, reopened.Pages.Count);
        Assert.False(RecoveredBytes.AnyRecoveredObjectContains(saved, "PAGE-TWO-MARK"), $"the removed page came back through the malformed /AcroForm ({layout}).");
    }
}
