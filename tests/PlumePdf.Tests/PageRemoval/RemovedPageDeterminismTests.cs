using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// <see cref="PdfOptions.Deterministic"/> and non-mutation after <c>doc.Pages.RemoveAt</c>:
/// the same removal on two separately opened copies saves to identical bytes, a second
/// <see cref="PdfDocument.Save"/> of the same document repeats the first byte for byte, and
/// saving leaves the <c>doc.Objects</c> graph exactly as it was. Whatever <c>Save</c> computes
/// for the removed pages is computed on the side, never written back into the opened document.
/// </summary>
public class RemovedPageDeterminismTests
{
    public static TheoryData<string, string, SaveLayout> Matrix()
    {
        var data = new TheoryData<string, string, SaveLayout>();
        foreach (var shape in RemovedPageFixtures.AllShapes)
        {
            foreach (var removal in RemovedPageFixtures.Removals)
            {
                foreach (var layout in Enum.GetValues<SaveLayout>())
                {
                    data.Add(shape, removal, layout);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void SaveAfterRemoval_IsDeterministicRepeatableAndLeavesTheGraphUnchanged(string shape, string removal, SaveLayout layout)
    {
        var fixture = RemovedPageFixtures.Build(shape);
        var removed = RemovedPageFixtures.PageIndexes(removal);

        using var document = PdfDocument.Open(fixture.Bytes);
        RemovedPageFixtures.RemovePages(document, removed);
        var pagesBefore = document.Pages.Select(static p => p.Reference).ToList();
        var graphBefore = CanonicalPdf.GraphSnapshot(document);

        var first = RemovedPageFixtures.SaveToBytes(document, layout);
        var graphAfterFirst = CanonicalPdf.GraphSnapshot(document);
        var second = RemovedPageFixtures.SaveToBytes(document, layout);

        Assert.Equal(graphBefore, graphAfterFirst);
        Assert.Equal(graphBefore, CanonicalPdf.GraphSnapshot(document));
        Assert.Equal(pagesBefore, document.Pages.Select(static p => p.Reference));
        Assert.True(first.AsSpan().SequenceEqual(second), $"Shape {shape}, removing page(s) {removal}, {layout}: a second Save of the same document wrote different bytes.");

        using var twin = PdfDocument.Open(fixture.Bytes);
        RemovedPageFixtures.RemovePages(twin, removed);
        var fromTwin = RemovedPageFixtures.SaveToBytes(twin, layout);
        Assert.True(first.AsSpan().SequenceEqual(fromTwin), $"Shape {shape}, removing page(s) {removal}, {layout}: two Deterministic saves of the same removal wrote different bytes.");
    }
}
