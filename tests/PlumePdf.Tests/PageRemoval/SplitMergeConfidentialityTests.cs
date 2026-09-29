using PlumePdf.Tests.TestSupport;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// <c>Pdf.Split</c> and <c>Pdf.Merge</c> (issue #17): a part or merged document contains only
/// the pages it was given. Pages not imported must not come along through a link, a pop-up or
/// reply, a widget's <c>/P</c>, a radio group spanning pages or any other reference — with or
/// without a <c>RemoveAt</c> first — and the result is qpdf-clean.
/// </summary>
public class SplitMergeConfidentialityTests
{
    public static TheoryData<string> Shapes() => [.. RemovedPageFixtures.AllShapes];

    public static TheoryData<string, string> ShapesByRemoval()
    {
        var data = new TheoryData<string, string>();
        foreach (var shape in RemovedPageFixtures.AllShapes)
        {
            foreach (var removal in RemovedPageFixtures.Removals)
            {
                data.Add(shape, removal);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Split_EachPartHoldsOnlyItsOwnPage(string shape)
    {
        var fixture = RemovedPageFixtures.Build(shape);
        using var document = PdfDocument.Open(fixture.Bytes);
        using var parts = Pdf.Split(document);

        for (var index = 0; index < parts.Documents.Count; index++)
        {
            var bytes = RemovedPageFixtures.SaveToBytes(parts.Documents[index], SaveLayout.Save);
            var otherPages = Enumerable.Range(0, 3).Where(i => i != index).ToArray();

            foreach (var marker in fixture.Markers.Where(m => m.IsRemovedBy(otherPages)))
            {
                Assert.False(RecoveredBytes.AnyRecoveredObjectContains(bytes, marker.Text), $"{shape} part {index + 1}: {marker.Text} came along.");
            }

            Assert.True(RecoveredBytes.AnyRecoveredObjectContains(bytes, RemovedPageFixtures.PageMarkers[index]), $"{shape} part {index + 1}: its own page is missing.");
            CleanupFixtures3b.AssertQpdfClean(bytes, $"{shape} part {index + 1}");
        }
    }

    [Theory]
    [MemberData(nameof(ShapesByRemoval))]
    public void RemoveThenMerge_LeavesTheRemovedPagesOut(string shape, string removal)
    {
        var fixture = RemovedPageFixtures.Build(shape);
        var removed = RemovedPageFixtures.PageIndexes(removal);
        using var document = PdfDocument.Open(fixture.Bytes);
        RemovedPageFixtures.RemovePages(document, removed);

        using var merged = Pdf.Merge(document);
        var bytes = RemovedPageFixtures.SaveToBytes(merged, SaveLayout.Save);

        foreach (var marker in fixture.Markers.Where(m => m.IsRemovedBy(removed)))
        {
            Assert.False(RecoveredBytes.AnyRecoveredObjectContains(bytes, marker.Text), $"{shape} {removal}: {marker.Text} came along.");
        }

        foreach (var kept in Enumerable.Range(0, 3).Except(removed))
        {
            Assert.True(RecoveredBytes.AnyRecoveredObjectContains(bytes, RemovedPageFixtures.PageMarkers[kept]), $"{shape} {removal}: kept page {kept + 1} is missing.");
        }

        CleanupFixtures3b.AssertQpdfClean(bytes, $"{shape} {removal} merge");
    }

    [Fact]
    public void MergingDifferentDocuments_AfterRemovingPagesFromOne_LeavesThemOut()
    {
        // Page 1 links to pages 2 and 3 (a /Dest and a GoTo action); both are removed before the merge.
        var withLinks = CleanupFixtures.Compose("", new Dictionary<int, string>
        {
            [20] = "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] /Dest [4 0 R /Fit] >>",
            [21] = "<< /Type /Annot /Subtype /Link /Rect [20 0 40 20] /A << /S /GoTo /D [5 0 R /Fit] >> >>",
        }, "/Annots [20 0 R 21 0 R]");
        using var first = PdfDocument.Open(withLinks);
        first.Pages.RemoveAt(2);
        first.Pages.RemoveAt(1);
        using var second = PdfDocument.Open(RemovedPageFixtures.Build("none").Bytes);

        using var merged = Pdf.Merge(first, second);
        var bytes = RemovedPageFixtures.SaveToBytes(merged, SaveLayout.Save);

        Assert.Equal(4, merged.Pages.Count);
        Assert.Equal(1, CountOccurrences(bytes, "PAGE-TWO-MARK"));   // the second document's page 2 only
        Assert.Equal(1, CountOccurrences(bytes, "PAGE-THREE-MARK")); // the second document's page 3 only
        Assert.Contains(merged.Diagnostics, static d => d.Code == "PLUME5021" && d.Message.Contains("2 links", StringComparison.Ordinal));
        CleanupFixtures3b.AssertQpdfClean(bytes, "cross-document merge");
    }

    [Fact]
    public void WidgetListedOnlyOnAPageNotImported_WithNoP_StaysOut()
    {
        // Widget 30 has no /P; page 2's /Annots is the only thing that places it. Its field
        // must not come along into the part holding page 1.
        var source = CleanupFixtures.Compose("/AcroForm << /Fields [30 0 R] >>", new Dictionary<int, string>
        {
            [30] = "<< /FT /Tx /T (onlytwo) /V (ONLY-ON-PAGE-TWO) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] >>",
        }, "", "/Annots [30 0 R]");
        using var document = PdfDocument.Open(source);
        using var parts = Pdf.Split(document);

        var first = RemovedPageFixtures.SaveToBytes(parts.Documents[0], SaveLayout.Save);
        var second = RemovedPageFixtures.SaveToBytes(parts.Documents[1], SaveLayout.Save);

        Assert.False(RecoveredBytes.AnyRecoveredObjectContains(first, "ONLY-ON-PAGE-TWO"));
        Assert.True(RecoveredBytes.AnyRecoveredObjectContains(second, "ONLY-ON-PAGE-TWO"));
    }

    [Fact]
    public void Split_RecordsWhatEachPartLeftOut()
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("L").Bytes);
        using var parts = Pdf.Split(document);

        // Page 1's link targets page 2, which the first part does not hold.
        Assert.Contains(parts.Documents[0].Diagnostics, static d => d.Code == "PLUME5021" && d.Message.Contains("1 link", StringComparison.Ordinal));
    }

    private static int CountOccurrences(byte[] bytes, string needle)
    {
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
