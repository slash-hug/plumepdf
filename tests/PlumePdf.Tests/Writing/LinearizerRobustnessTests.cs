using System.Text;
using PlumePdf.Objects;
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

    public static TheoryData<string, SaveLayout> MalformedKeys()
    {
        var data = new TheoryData<string, SaveLayout>();
        foreach (var key in new[] { "AcroForm", "Info" })
        {
            foreach (var layout in Enum.GetValues<SaveLayout>())
            {
                data.Add(key, layout);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(MalformedKeys))]
    public void MalformedDictionaryNamingTheRemovedPage_NeitherLeaksItNorFails(string key, SaveLayout layout)
    {
        var bytes = MalformedFormFixture(key);

        using var document = PdfDocument.Open(bytes);
        document.Pages.RemoveAt(1);
        var saved = RemovedPageFixtures.SaveToBytes(document, layout);

        using var reopened = PdfDocument.Open(saved);
        Assert.Equal(2, reopened.Pages.Count);
        Assert.False(RecoveredBytes.AnyRecoveredObjectContains(saved, "PAGE-TWO-MARK"), $"the removed page came back through the malformed /{key} ({layout}).");
    }

    [Fact]
    public void Linearizer_NumbersFormObjectsNoPartClaimed()
    {
        // The removed-set builder now keeps the malformed /AcroForm (the removed page) out. Had it
        // not, the form walk would claim the page's font as a page barrier and the part 9 walk
        // would never reach it; the linearizer must still number it rather than fail.
        using var document = PdfDocument.Open(MalformedFormFixture("AcroForm"));
        document.Pages.RemoveAt(1);
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();

        // What the builder produced before it stopped protecting page-tree nodes: the original
        // /Pages root excluded, the removed page (object 4, the malformed /AcroForm) put back.
        var excluded = new HashSet<int>(document.OpenTimePageTree);
        excluded.ExceptWith(pages.Select(static p => p.Reference.Number));
        excluded.Remove(4);

        using var output = new MemoryStream();
        var ex = Record.Exception(() => Linearizer.Write(output, document.Objects, document.Catalog!.Reference, document.Catalog.Dictionary, pages, PdfOptions.Default with { Deterministic = true }, excluded));

        Assert.Null(ex);
    }

    [Theory]
    [InlineData(SaveLayout.Save)]
    [InlineData(SaveLayout.Optimize)]
    [InlineData(SaveLayout.Linearize)]
    public void OpenWithOutlines_BookmarkReachingFields_IsQpdfClean(SaveLayout layout)
    {
        // /PageMode /UseOutlines puts the outline group (and the fields it reaches) in the first
        // page's section; a later page that reaches them lists them as shared objects.
        var appearance = "q BT /Helv 10 Tf (x) Tj ET Q";
        var bytes = CleanupFixtures.Compose(
            "/PageMode /UseOutlines /Outlines 20 0 R /AcroForm << /Fields [30 0 R 31 0 R] /DR << /Font << /Helv 9 0 R >> >> >>",
            new Dictionary<int, string>
            {
                [20] = "<< /Type /Outlines /First 21 0 R /Last 21 0 R /Count 1 >>",
                [21] = "<< /Title (Submit) /Parent 20 0 R /Dest [3 0 R /Fit] /A << /S /SubmitForm /F (https://example.invalid/) /Fields [30 0 R 31 0 R] >> >>",
                [30] = "<< /FT /Tx /T (a) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R /AP << /N 32 0 R >> >>",
                [31] = "<< /FT /Tx /T (b) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R /AP << /N 33 0 R >> >>",
                [32] = $"<< /Type /XObject /Subtype /Form /BBox [0 0 50 20] /Resources << /Font << /Helv 9 0 R >> >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
                [33] = $"<< /Type /XObject /Subtype /Form /BBox [0 0 50 20] /Resources << /Font << /Helv 9 0 R >> >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
            },
            "/Annots [30 0 R]", "/Annots [31 0 R]");

        using var document = PdfDocument.Open(bytes);
        var saved = RemovedPageFixtures.SaveToBytes(document, layout);

        CleanupFixtures3b.AssertQpdfClean(saved, $"open-with-outlines bookmark reaching fields, {layout}");
    }

    // /AcroForm (catalog) or /Info (trailer) names the page being removed; the kept page lists a
    // widget whose /P is that removed page and whose field chain loops (80 -> 81 -> 80).
    private static byte[] MalformedFormFixture(string key)
    {
        var bytes = CleanupFixtures.Compose(
            key == "AcroForm" ? "/AcroForm 4 0 R" : "",
            new Dictionary<int, string>
            {
                [80] = "<< /T (loop) /Kids [81 0 R] >>",
                [81] = "<< /T (loopchild) /Parent 80 0 R /Kids [80 0 R 82 0 R] >>",
                [82] = "<< /Type /Annot /Subtype /Widget /Parent 81 0 R /P 4 0 R /Rect [0 0 10 10] >>",
            },
            "/Annots [82 0 R]");
        if (key == "AcroForm")
        {
            return bytes;
        }

        // The trailer follows the cross-reference table, so adding /Info to it moves no offset.
        var text = Encoding.Latin1.GetString(bytes);
        var at = text.LastIndexOf("/Root 1 0 R", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(text.Insert(at, "/Info 4 0 R "));
    }
}
