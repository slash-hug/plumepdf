using System.Text;
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
