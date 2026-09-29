using PlumePdf.Objects;
using PlumePdf.Tests.TestSupport;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// The confidentiality guarantee for <c>doc.Pages.RemoveAt</c> followed by a full-rewrite
/// <see cref="PdfDocument.Save"/> (issue #16): whatever still references a removed page — an
/// outline, a named destination, a link, a form field, an open action, a structure element —
/// nothing that belonged only to it may be written. Every fixture shape × both removal sets ×
/// all three layouts is saved, reopened and scanned with <see cref="RecoveredBytes"/>, which
/// decodes every object physically present in the file, reachable or not.
/// </summary>
/// <remarks>
/// Only removal is asserted here: a <c>null</c> left where a reference to a removed object used
/// to be is allowed, and outline titles and link text on kept pages are not checked because
/// dropping dead bookmarks and links is clean-up, not confidentiality.
/// </remarks>
public class RemovedPageConfidentialityTests
{
    // Marker kinds whose absence is the guarantee. Outline titles and kept-page link text are
    // tidiness (the item or link survives, pointing nowhere) and are not asserted absent.
    private static readonly FixtureMarkerKind[] ConfidentialKinds =
        [FixtureMarkerKind.PageContent, FixtureMarkerKind.AnnotationText, FixtureMarkerKind.FieldValue, FixtureMarkerKind.StructureText];

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

    public static TheoryData<string, SaveLayout> RemovalsByLayout()
    {
        var data = new TheoryData<string, SaveLayout>();
        foreach (var removal in RemovedPageFixtures.Removals)
        {
            foreach (var layout in Enum.GetValues<SaveLayout>())
            {
                data.Add(removal, layout);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void RemovedPage_NothingOnlyItOwnedIsRecoverable(string shape, string removal, SaveLayout layout)
    {
        var fixture = RemovedPageFixtures.Build(shape);
        var removed = RemovedPageFixtures.PageIndexes(removal);

        var saved = SaveAfterRemoval(fixture, removed, layout);

        using (var reopened = PdfDocument.Open(saved))
        {
            Assert.Equal(3 - removed.Length, reopened.Pages.Count);
        }

        var leaked = fixture.Markers
            .Where(m => ConfidentialKinds.Contains(m.Kind) && m.IsRemovedBy(removed))
            .Where(m => RecoveredBytes.AnyRecoveredObjectContains(saved, m.Text))
            .Select(static m => $"{m.Text} ({m.Kind})")
            .ToList();
        var missing = fixture.Markers
            .Where(m => !m.IsRemovedBy(removed))
            .Where(m => !RecoveredBytes.AnyRecoveredObjectContains(saved, m.Text))
            .Select(static m => $"{m.Text} ({m.Kind})")
            .ToList();

        Assert.True(leaked.Count == 0, $"Shape {shape}, removing page(s) {removal}, {layout}: removed content recoverable from the saved file: {string.Join(", ", leaked)}");
        Assert.True(missing.Count == 0, $"Shape {shape}, removing page(s) {removal}, {layout}: kept content missing from the saved file: {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void SharedAnnotation_KeptPageStillHasItsLink(string removal, SaveLayout layout)
    {
        // Shape S lists one link in the /Annots of kept page 1 and of removed page 2. Keeping
        // the page that still shows it wins: the link must survive on page 1.
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build("S"), RemovedPageFixtures.PageIndexes(removal), layout);

        using var reopened = PdfDocument.Open(saved);
        var annots = Assert.IsType<PdfArray>(Resolve(reopened, reopened.Pages[0].Dictionary[PdfName.Annots]));
        Assert.Contains(annots, a => Resolve(reopened, a) is PdfDictionary d && d.TryGetValue(PdfName.Subtype, out var subtype) && subtype == PdfName.Get("Link"));
    }

    [Theory]
    [MemberData(nameof(RemovalsByLayout))]
    public void IntegerIndexDestination_IsWrittenUnchanged_DocumentedLimitation(string removal, SaveLayout layout)
    {
        // Documented limitation: an integer page-index destination (legal only for remote
        // targets, but seen in the wild) is not a reference, so nothing ties it to the page it
        // meant. Shape I's bookmark names index 1 — page 2 before the removal — and is written
        // as-is, so afterwards it silently names whatever page now sits at index 1 (or none).
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build("I"), RemovedPageFixtures.PageIndexes(removal), layout);

        using var reopened = PdfDocument.Open(saved);
        var catalog = Assert.IsType<PdfDictionary>(Resolve(reopened, reopened.Objects.Trailer[PdfName.Root]));
        var outlines = Assert.IsType<PdfDictionary>(Resolve(reopened, catalog[PdfName.Get("Outlines")]));
        var item = Assert.IsType<PdfDictionary>(Resolve(reopened, outlines[PdfName.First]));
        var destination = Assert.IsType<PdfArray>(Resolve(reopened, item[PdfName.Get("Dest")]));
        var index = Assert.IsType<PdfNumber>(destination[0]);
        Assert.Equal(1, index.ToInt32());
    }

    private static byte[] SaveAfterRemoval(RemovedPageFixture fixture, int[] removed, SaveLayout layout)
    {
        using var document = PdfDocument.Open(fixture.Bytes);
        RemovedPageFixtures.RemovePages(document, removed);
        return RemovedPageFixtures.SaveToBytes(document, layout);
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfReference reference ? document.Objects[reference.Target] : value;
}
