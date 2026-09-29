using System.Text;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Self-test for <see cref="RemovedPageFixtures"/>: every shape is a well-formed three-page
/// document, and its markers are unique, so a marker found in saved output can only have come
/// from the object it was planted in.
/// </summary>
public class RemovedPageFixturesTests
{
    public static TheoryData<string> Shapes() => [.. RemovedPageFixtures.AllShapes];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Fixture_OpensWithThreePagesAndNoErrors(string shape)
    {
        var fixture = RemovedPageFixtures.Build(shape);

        using var document = PdfDocument.Open(fixture.Bytes);

        Assert.Equal(3, document.Pages.Count);
        Assert.DoesNotContain(document.Diagnostics, static d => d.Severity == DiagnosticSeverity.Error);
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains(RemovedPageFixtures.PageMarkers[i], document.Pages[i].ExtractText().Text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Fixture_MarkersAreUniqueAndEachPlantedOnce(string shape)
    {
        var fixture = RemovedPageFixtures.Build(shape);
        var text = Encoding.Latin1.GetString(fixture.Bytes);

        foreach (var marker in fixture.Markers)
        {
            var first = text.IndexOf(marker.Text, StringComparison.Ordinal);
            Assert.True(first >= 0, $"Marker {marker.Text} is not in the {shape} fixture.");
            Assert.True(text.IndexOf(marker.Text, first + 1, StringComparison.Ordinal) < 0, $"Marker {marker.Text} occurs more than once in the {shape} fixture.");
            Assert.DoesNotContain(fixture.Markers, other => other != marker && other.Text.Contains(marker.Text, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Shapes_AreTheDocumentedMatrix()
    {
        Assert.Equal(23, RemovedPageFixtures.AllShapes.Count);
        Assert.Equal(RemovedPageFixtures.AllShapes.Count, RemovedPageFixtures.AllShapes.Distinct().Count());
        Assert.Equal(
            RemovedPageFixtures.AllShapes.Order(),
            RemovedPageFixtures.FormShapes.Concat(RemovedPageFixtures.NonFormShapes).Order());
    }
}
