using PlumePdf.Documents.Compression;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// The planning pass: the placement scan, the classifier and the duplicate finder combined into
/// one plan. Duplicates are only looked for when the lossless levers are on; a duplicate image's
/// placements count towards the copy that is written; and an alias only stands when both copies
/// would be written the same way.
/// </summary>
public class CompressionPlannerTests
{
    private const string Rgb = "/Type /XObject /Subtype /Image /Width 300 /Height 300 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode";
    private const string Gray = "/Type /XObject /Subtype /Image /Width 300 /Height 300 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode";

    private static readonly CompressionSettings LosslessOnly = new(Lossless: true, MaxDpi: null, JpegQuality: null);

    [Fact]
    public void PlanImages_AreTheClassifierOutputOverTheScan()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, $"{Rgb} /SMask 20 0 R", Payload(1))
            .Stream(11, Rgb, Payload(2))
            .Stream(20, Gray, Payload(3))
            .Page(3, "q 72 0 0 72 0 0 cm /A Do Q q 288 0 0 288 0 0 cm /B Do Q BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI", xobjects: "/A 10 0 R /B 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());
        var settings = new CompressionSettings(Lossless: true, MaxDpi: 100, JpegQuality: 80);

        var plan = CompressionPlanner.Plan(document, settings, PdfOptions.Default, CancellationToken.None);

        var scan = PlacementScanner.Scan(document, PdfOptions.Default, CancellationToken.None);
        var expected = ImageClassifier.Classify(document.Objects, scan, settings, PdfOptions.Default, CancellationToken.None);
        Assert.Equal(expected.OrderBy(static i => i.Key), plan.Images.OrderBy(static i => i.Key));
        Assert.Equal([10, 11, 20], plan.Images.Keys.Order());
        Assert.Equal(scan.InlineImagePages, plan.InlineImagePages);
        Assert.Empty(plan.Aliases);
    }

    [Fact]
    public void ScanDiagnostics_AreThePlans()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Rgb, Payload(1))
            .Stream(11, "/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 10 0 R /Fm0 11 0 R >> >>", "/Im0 Do /Fm0 Do")
            .Page(3, "q 72 0 0 72 0 0 cm /Fm0 Do Q", xobjects: "/Fm0 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        var plan = CompressionPlanner.Plan(document, LosslessOnly, PdfOptions.Default, CancellationToken.None);

        Assert.Contains(plan.Diagnostics, static d => d.Code == "PLUME6025");
        Assert.Equal(PlannedSkip.UnmeasuredPlacement, plan.Images[10].Skip);
    }

    [Fact]
    public void LosslessOff_LooksForNoDuplicates()
    {
        using var document = PdfDocument.Open(TwoPagesOfTheSameImage(firstEdge: 72, secondEdge: 72).Build());

        var lossless = CompressionPlanner.Plan(document, LosslessOnly, PdfOptions.Default, CancellationToken.None);
        var lossy = CompressionPlanner.Plan(document, new CompressionSettings(Lossless: false, MaxDpi: 100, JpegQuality: null), PdfOptions.Default, CancellationToken.None);

        Assert.Equal(10, lossless.Aliases[12]);
        Assert.Empty(lossy.Aliases);
        Assert.Equal([10, 12], lossy.Images.Keys.Order());
    }

    [Fact]
    public void Plan_ChecksTheCancellationToken()
    {
        using var document = PdfDocument.Open(TwoPagesOfTheSameImage(firstEdge: 72, secondEdge: 72).Build());

        Assert.Throws<OperationCanceledException>(() => CompressionPlanner.Plan(document, LosslessOnly, PdfOptions.Default, new CancellationToken(canceled: true)));
    }

    // Only the canonical copy is written, so it has to be sized for every place either copy is
    // drawn: on its own page the image is at 300 DPI (well over the trigger), but its duplicate
    // is drawn at 100 DPI, so the written copy is kept.
    [Fact]
    public void DuplicateImage_FoldsItsPlacementsIntoTheCanonical()
    {
        using var document = PdfDocument.Open(TwoPagesOfTheSameImage(firstEdge: 72, secondEdge: 216).Build());
        var settings = new CompressionSettings(Lossless: true, MaxDpi: 100, JpegQuality: null);

        var plan = CompressionPlanner.Plan(document, settings, PdfOptions.Default, CancellationToken.None);

        Assert.Equal(10, plan.Aliases[12]);
        var image = Assert.Single(plan.Images).Value;
        Assert.Equal(10, image.SourceNumber);
        Assert.Equal(PlannedImageAction.Keep, image.Action);
        Assert.Equal((100.0, 100.0), (image.DpiX, image.DpiY));
        Assert.Equal(1, image.PageIndex);
    }

    [Fact]
    public void DuplicateImage_NotLossless_IsPlannedOnItsOwnPlacements()
    {
        using var document = PdfDocument.Open(TwoPagesOfTheSameImage(firstEdge: 72, secondEdge: 216).Build());
        var settings = new CompressionSettings(Lossless: false, MaxDpi: 100, JpegQuality: null);

        var plan = CompressionPlanner.Plan(document, settings, PdfOptions.Default, CancellationToken.None);

        Assert.Equal(PlannedImageAction.Downsample, plan.Images[10].Action);
        Assert.Equal(PlannedImageAction.Keep, plan.Images[12].Action);
    }

    [Fact]
    public void DuplicateImage_DrawnInATilingPattern_LeavesTheCanonicalUnmeasured()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Rgb, Payload(1))
            .Stream(12, Rgb, Payload(1))
            .Stream(13, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /XObject << /Im0 12 0 R >> >>", "q 10 0 0 10 0 0 cm /Im0 Do Q")
            .Page(3, "q 72 0 0 72 0 0 cm /Im0 Do Q /Pattern cs /P0 scn 0 0 100 100 re f", xobjects: "/Im0 10 0 R", resources: "/Pattern << /P0 13 0 R >>");
        using var document = PdfDocument.Open(pdf.Build());

        var plan = CompressionPlanner.Plan(document, new CompressionSettings(true, 100, null), PdfOptions.Default, CancellationToken.None);

        Assert.Equal(10, plan.Aliases[12]);
        Assert.Equal(PlannedSkip.UnmeasuredPlacement, Assert.Single(plan.Images).Value.Skip);
    }

    [Fact]
    public void UnplacedImage_IsPlannedAsNoPlacement()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Rgb, Payload(1))
            .Stream(11, Rgb, Payload(2))
            .Page(3, "q 72 0 0 72 0 0 cm /A Do Q", xobjects: "/A 10 0 R /B 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        var plan = CompressionPlanner.Plan(document, new CompressionSettings(true, 100, 80), PdfOptions.Default, CancellationToken.None);

        Assert.Equal(PlannedImageAction.DownsampleAndReencode, plan.Images[10].Action);
        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.NoPlacement), (plan.Images[11].Action, plan.Images[11].Skip));
    }

    // The canonical copy is listed on page 0 but never drawn; its duplicate is drawn at 300 DPI.
    // The unplaced listing doesn't constrain the written copy, so the drawn placement sizes it.
    [Fact]
    public void UnplacedCanonical_TakesItsDrawnDuplicatesPlacement()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Rgb, Payload(1))
            .Stream(12, Rgb, Payload(1))
            .Page(3, "0 0 10 10 re f", xobjects: "/Im0 10 0 R")
            .Page(5, "q 72 0 0 72 0 0 cm /Im0 Do Q", xobjects: "/Im0 12 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        var plan = CompressionPlanner.Plan(document, new CompressionSettings(true, 100, null), PdfOptions.Default, CancellationToken.None);

        Assert.Equal(10, plan.Aliases[12]);
        var image = Assert.Single(plan.Images).Value;
        Assert.Equal((PlannedImageAction.Downsample, PlannedSkip.None, 100), (image.Action, image.Skip, image.TargetWidth));
        Assert.Equal(1, image.PageIndex);
    }

    // Two different images whose soft masks are byte-identical: one is drawn at 300 DPI and
    // downsampled, so its mask is too; the other, at 75 DPI, keeps its size and so does its mask.
    // Writing one mask for both would size one of them wrongly, so the masks stay apart.
    [Fact]
    public void IdenticalSoftMasks_OfDifferentlyPlannedImages_AreNotMerged()
    {
        var plan = PlanMaskedPair(secondEdge: 288);

        Assert.False(plan.Aliases.ContainsKey(21));
        Assert.Equal((PlannedImageAction.Downsample, 100), (plan.Images[20].Action, plan.Images[20].TargetWidth));
        Assert.Equal((PlannedImageAction.Keep, 300), (plan.Images[21].Action, plan.Images[21].TargetWidth));
    }

    [Fact]
    public void IdenticalSoftMasks_OfAlikePlannedImages_StillMerge()
    {
        var plan = PlanMaskedPair(secondEdge: 72);

        Assert.Equal(20, plan.Aliases[21]);
        Assert.Equal((PlannedImageAction.Downsample, 100), (plan.Images[20].Action, plan.Images[20].TargetWidth));
    }

    // Only the canonical copy is written, so a merged-away mask carries no plan of its own and
    // its parent names the copy that is written.
    [Fact]
    public void MergedSoftMask_IsAbsentFromThePlan_AndItsParentNamesTheCanonical()
    {
        var plan = PlanMaskedPair(secondEdge: 72);

        Assert.Equal([10, 11, 20], plan.Images.Keys.Order());
        Assert.Equal(20, plan.Images[10].SoftMaskNumber);
        Assert.Equal(20, plan.Images[11].SoftMaskNumber);
    }

    // A plain image and a soft mask with the same bytes, both downsampled to the same size: the
    // plans agree on everything but being a mask, and a mask is written as one (never as a
    // plain image), so they stay apart.
    [Fact]
    public void PlainImage_IdenticalToASoftMask_PlannedTheSame_IsStillNotMerged()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Gray, Payload(3))
            .Stream(11, $"{Rgb} /SMask 20 0 R", Payload(1))
            .Stream(20, Gray, Payload(3))
            .Page(3, "q 72 0 0 72 0 0 cm /A Do Q q 72 0 0 72 0 0 cm /B Do Q", xobjects: "/A 10 0 R /B 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        var plan = CompressionPlanner.Plan(document, new CompressionSettings(true, 100, null), PdfOptions.Default, CancellationToken.None);

        Assert.False(plan.Aliases.ContainsKey(20));
        Assert.Equal((PlannedImageAction.Downsample, 100, 100, false), (plan.Images[10].Action, plan.Images[10].TargetWidth, plan.Images[10].TargetHeight, plan.Images[10].IsSoftMask));
        Assert.Equal((PlannedImageAction.Downsample, 100, 100, true), (plan.Images[20].Action, plan.Images[20].TargetWidth, plan.Images[20].TargetHeight, plan.Images[20].IsSoftMask));
    }

    // A plain image that is byte-identical to another image's soft mask: the plain copy is
    // re-encoded as JPEG, which the mask must never be, so they are written separately.
    [Fact]
    public void PlainImage_IdenticalToAnotherImagesSoftMask_IsNotMergedWithIt()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Gray, Payload(3))
            .Stream(11, $"{Rgb} /SMask 20 0 R", Payload(1))
            .Stream(20, Gray, Payload(3))
            .Page(3, "q 72 0 0 72 0 0 cm /A Do Q q 72 0 0 72 0 0 cm /B Do Q", xobjects: "/A 10 0 R /B 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        var plan = CompressionPlanner.Plan(document, new CompressionSettings(true, 100, 80), PdfOptions.Default, CancellationToken.None);

        Assert.False(plan.Aliases.ContainsKey(20));
        Assert.Equal(PlannedImageAction.DownsampleAndReencode, plan.Images[10].Action);
        Assert.True(plan.Images[20].IsSoftMask);
        Assert.Equal(PlannedImageAction.Downsample, plan.Images[20].Action);
    }

    // With no image levers nothing is resized, so the same copy merge as before.
    [Fact]
    public void PlainImage_IdenticalToASoftMask_MergesWhenNeitherIsChanged()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Gray, Payload(3))
            .Stream(11, $"{Rgb} /SMask 20 0 R", Payload(1))
            .Stream(20, Gray, Payload(3))
            .Page(3, "q 72 0 0 72 0 0 cm /A Do Q q 72 0 0 72 0 0 cm /B Do Q", xobjects: "/A 10 0 R /B 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());

        var plan = CompressionPlanner.Plan(document, LosslessOnly, PdfOptions.Default, CancellationToken.None);

        Assert.Equal(10, plan.Aliases[20]);
        Assert.False(plan.Images.ContainsKey(20));
        Assert.Equal(10, plan.Images[11].SoftMaskNumber);
    }

    // Under Strict a dangling /SMask is this image failing to classify, not the run failing.
    // (The duplicate finder walks the same references the save writes, so with the lossless
    // levers on it refuses the document as the save itself would; this isolates the classifier.)
    [Fact]
    public void DanglingSoftMaskUnderStrict_IsADecodeFailureNotAThrow()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, $"{Rgb} /SMask 99 0 R", Payload(1))
            .Page(3, "q 72 0 0 72 0 0 cm /A Do Q", xobjects: "/A 10 0 R");
        var strict = PdfOptions.Default with { Strict = true };
        using var document = PdfDocument.Open(pdf.Build(), strict);

        var plan = CompressionPlanner.Plan(document, new CompressionSettings(Lossless: false, MaxDpi: 100, JpegQuality: null), strict, CancellationToken.None);

        var image = Assert.Single(plan.Images).Value;
        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.DecodeFailed), (image.Action, image.Skip));
        Assert.Null(image.SoftMaskNumber);
    }

    private static CompressionPlan PlanMaskedPair(int secondEdge)
    {
        var pdf = new ScanTestPdf()
            .Stream(10, $"{Rgb} /SMask 20 0 R", Payload(1))
            .Stream(11, $"{Rgb} /SMask 21 0 R", Payload(2))
            .Stream(20, Gray, Payload(3))
            .Stream(21, Gray, Payload(3))
            .Page(3, $"q 72 0 0 72 0 0 cm /A Do Q q {secondEdge} 0 0 {secondEdge} 0 0 cm /B Do Q", xobjects: "/A 10 0 R /B 11 0 R");
        using var document = PdfDocument.Open(pdf.Build());
        return CompressionPlanner.Plan(document, new CompressionSettings(true, 100, null), PdfOptions.Default, CancellationToken.None);
    }

    // A 300x300 image (object 10) drawn on page 0 and its byte-identical copy (object 12) on
    // page 1, each on a square of the given edge in points.
    private static ScanTestPdf TwoPagesOfTheSameImage(int firstEdge, int secondEdge) =>
        new ScanTestPdf()
            .Stream(10, Rgb, Payload(1))
            .Stream(12, Rgb, Payload(1))
            .Page(3, $"q {firstEdge} 0 0 {firstEdge} 0 0 cm /Im0 Do Q", xobjects: "/Im0 10 0 R")
            .Page(5, $"q {secondEdge} 0 0 {secondEdge} 0 0 cm /Im0 Do Q", xobjects: "/Im0 12 0 R");

    // A placeholder payload (nothing here decodes it); equal seeds give byte-identical payloads.
    private static byte[] Payload(byte seed) => Enumerable.Repeat(seed, 64).ToArray();
}
