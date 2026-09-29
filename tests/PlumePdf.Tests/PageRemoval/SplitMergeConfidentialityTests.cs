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
}
