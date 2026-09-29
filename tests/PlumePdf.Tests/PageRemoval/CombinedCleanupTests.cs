using PlumePdf.Objects;
using PlumePdf.Tests.TestSupport;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Every save-time clean-up at once, on the combined fixture (a field with widgets on a kept and
/// a removed page, a split radio group, a bookmark and a link to the removed page, and tagged
/// content on it): nothing from the removed page is recoverable, the saved file is qpdf-clean,
/// no form, annotation or structure array still holds a null, and <c>PLUME5021</c> reports what
/// was left out.
/// </summary>
public class CombinedCleanupTests
{
    public static TheoryData<string, SaveLayout> Cases() => CleanupFixtures.RemovalsByLayout();

    [Theory]
    [MemberData(nameof(Cases))]
    public void CombinedShape_IsCleanAfterEveryPass(string removal, SaveLayout layout)
    {
        var fixture = RemovedPageFixtures.Build("C");
        var pageIndexes = RemovedPageFixtures.PageIndexes(removal);
        var saved = CleanupFixtures.SaveAfterRemoval(fixture.Bytes, removal, layout);

        foreach (var marker in fixture.Markers.Where(m => m.IsRemovedBy(pageIndexes)))
        {
            Assert.False(RecoveredBytes.AnyRecoveredObjectContains(saved, marker.Text), $"{marker.Text} survived ({removal}, {layout}).");
        }

        foreach (var marker in fixture.Markers.Where(m => m.Kind == FixtureMarkerKind.PageContent && !m.IsRemovedBy(pageIndexes)))
        {
            Assert.True(RecoveredBytes.AnyRecoveredObjectContains(saved, marker.Text), $"kept {marker.Text} missing ({removal}, {layout}).");
        }

        CleanupFixtures3b.AssertQpdfClean(saved, $"C {removal} {layout}");

        using var reopened = PdfDocument.Open(saved);
        Assert.Equal(3 - pageIndexes.Length, reopened.Pages.Count);
        for (var i = 0; i < reopened.Pages.Count; i++)
        {
            Assert.DoesNotContain(CleanupFixtures.Annotations(reopened, i), static a => a is null);
        }

        var acroForm = CleanupFixtures3b.AcroForm(reopened);
        CleanupFixtures3b.NullFreeArray(reopened, acroForm[PdfName.Get("Fields")], "/Fields");
        foreach (var field in CleanupFixtures3b.FieldTreeNodes(reopened))
        {
            if (field.TryGetValue(PdfName.Kids, out var kids))
            {
                CleanupFixtures3b.NullFreeArray(reopened, kids, "/Kids");
            }
        }

        foreach (var element in CleanupFixtures3b.StructureElements(reopened))
        {
            if (element.TryGetValue(PdfName.Get("K"), out var k) && CleanupFixtures3b.Resolve(reopened, k) is PdfArray)
            {
                CleanupFixtures3b.NullFreeArray(reopened, k, "/K");
            }
        }
    }

    [Fact]
    public void Save_ReportsWhatItLeftOut_OncePerSave()
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("C").Bytes);
        document.Pages.RemoveAt(1);

        RemovedPageFixtures.SaveToBytes(document, SaveLayout.Save);
        var entry = Assert.Single(document.Diagnostics, static d => d.Code == "PLUME5021");
        Assert.Equal(DiagnosticSeverity.Info, entry.Severity);
        foreach (var kind in new[] { "bookmark", "widget", "link", "structure element" })
        {
            Assert.Contains(kind, entry.Message);
        }

        RemovedPageFixtures.SaveToBytes(document, SaveLayout.Optimize);
        Assert.Equal(2, document.Diagnostics.Count(static d => d.Code == "PLUME5021"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("N")]
    public void Save_ReportsNothing_WhenNothingPointedAtARemovedPage(string shape)
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build(shape).Bytes);
        document.Pages.RemoveAt(1);

        RemovedPageFixtures.SaveToBytes(document, SaveLayout.Save);

        Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5021");
    }

    [Fact]
    public void Save_ReportsNothing_WithoutARemoval()
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("C").Bytes);

        RemovedPageFixtures.SaveToBytes(document, SaveLayout.Save);

        Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5021");
    }
}
