using System.Text;
using PlumePdf.Objects;
using PlumePdf.Tests.PageRemoval;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <c>Pdf.Merge</c> with the same document (or page) more than once (issue #22): every occurrence
/// is a page of its own, and a repeated form's fields are independent copies, renamed like any
/// cross-document name collision.
/// </summary>
public class MergeDuplicatePagesTests
{
    [Fact]
    public void MergingADocumentWithItself_KeepsEveryPageOfBoth()
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("L").Bytes);

        using var merged = Pdf.Merge(document, document);
        var bytes = RemovedPageFixtures.SaveToBytes(merged, SaveLayout.Save);
        using var reopened = PdfDocument.Open(bytes);

        Assert.Equal(6, merged.Pages.Count);
        Assert.Equal(6, reopened.Pages.Count);
        Assert.DoesNotContain(reopened.Diagnostics, static d => d.Code == "PLUME6011");
        foreach (var marker in RemovedPageFixtures.PageMarkers)
        {
            Assert.Equal(2, Occurrences(bytes, marker));
        }

        CleanupFixtures3b.AssertQpdfClean(bytes, "document merged with itself");
    }

    [Fact]
    public void MergingAFormWithItself_GivesTheSecondCopyIndependentRenamedFields()
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("K").Bytes);
        var originalNames = document.Form.Fields.Select(static f => f.FullName).ToList();

        using var merged = Pdf.Merge(document, document);
        var bytes = RemovedPageFixtures.SaveToBytes(merged, SaveLayout.Save);
        using var reopened = PdfDocument.Open(bytes);
        var names = reopened.Form.Fields.Select(static f => f.FullName).ToList();

        Assert.Equal(2 * originalNames.Count, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(originalNames, name => Assert.Contains(name, names));
        Assert.Contains(names, static name => name.Contains('~', StringComparison.Ordinal));
        CleanupFixtures3b.AssertQpdfClean(bytes, "form merged with itself");
    }

    [Fact]
    public void RemovingAPageThenMergingWithItself_KeepsTheRemainingPagesTwice()
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("C").Bytes);
        document.Pages.RemoveAt(1);

        using var merged = Pdf.Merge(document, document);
        var bytes = RemovedPageFixtures.SaveToBytes(merged, SaveLayout.Save);

        Assert.Equal(4, merged.Pages.Count);
        Assert.Equal(0, Occurrences(bytes, "PAGE-TWO-MARK"));
        Assert.Equal(2, Occurrences(bytes, "PAGE-ONE-MARK"));
        CleanupFixtures3b.AssertQpdfClean(bytes, "reduced document merged with itself");
    }

    [Fact]
    public void EachCopy_LinksAndPlacesWidgetsWithinItself()
    {
        // Shape L: page 1 links to page 2. Shape K: a field whose widgets sit on pages 1 and 2.
        using var linked = PdfDocument.Open(RemovedPageFixtures.Build("L").Bytes);
        using var linkedTwice = PdfDocument.Open(RemovedPageFixtures.SaveToBytes(Pdf.Merge(linked, linked), SaveLayout.Save));

        Assert.Equal(linkedTwice.Pages[1].Reference, LinkTarget(linkedTwice, 0));
        Assert.Equal(linkedTwice.Pages[4].Reference, LinkTarget(linkedTwice, 3));

        using var form = PdfDocument.Open(RemovedPageFixtures.Build("K").Bytes);
        using var formTwice = PdfDocument.Open(RemovedPageFixtures.SaveToBytes(Pdf.Merge(form, form), SaveLayout.Save));
        var secondCopyPages = new HashSet<IndirectReference>(formTwice.Pages.Skip(3).Select(static p => p.Reference));
        var widgetsChecked = 0;

        foreach (var index in new[] { 3, 4, 5 })
        {
            foreach (var widget in CleanupFixtures.Annotations(formTwice, index).Where(static a => a.TryGetValue(PdfName.Subtype, out var s) && s.ToString() == "/Widget"))
            {
                var page = (PdfReference)widget[PdfName.Get("P")];
                Assert.Contains(page.Target, secondCopyPages);
                widgetsChecked++;
                if (widget.TryGetValue(PdfName.Parent, out var parentValue))
                {
                    var parent = (PdfDictionary)CleanupFixtures.Resolve(formTwice, parentValue)!;
                    var kids = (PdfArray)CleanupFixtures.Resolve(formTwice, parent[PdfName.Kids])!;
                    foreach (var kid in kids)
                    {
                        var kidPage = (PdfReference)((PdfDictionary)CleanupFixtures.Resolve(formTwice, kid)!)[PdfName.Get("P")];
                        Assert.Contains(kidPage.Target, secondCopyPages);
                    }
                }
            }
        }

        Assert.True(widgetsChecked > 0, "the second copy has no widgets to check");
    }

    [Fact]
    public void RepeatedOccurrences_ReportWhatTheyLeftOutOnce()
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("L").Bytes);
        document.Pages.RemoveAt(1);

        using var merged = Pdf.Merge(document, document);

        Assert.Single(merged.Diagnostics, static d => d.Code == "PLUME5021");
    }

    private static IndirectReference LinkTarget(PdfDocument document, int pageIndex)
    {
        var link = Assert.Single(CleanupFixtures.Annotations(document, pageIndex), static a => a.TryGetValue(PdfName.Subtype, out var s) && s.ToString() == "/Link");
        var destination = (PdfArray)CleanupFixtures.Resolve(document, link[PdfName.Get("Dest")])!;
        return ((PdfReference)destination[0]).Target;
    }

    private static int Occurrences(byte[] bytes, string needle)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
