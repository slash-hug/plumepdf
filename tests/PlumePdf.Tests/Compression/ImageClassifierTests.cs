using PlumePdf.Documents.Compression;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// The image classifier: every placed image, and every soft mask those images use, gets a plan
/// from its dictionary and its placements: a skip reason when compression must leave it alone,
/// otherwise the downsample trigger (strictly above 1.5 times the maximum DPI on the lower axis),
/// per-axis targets, and the re-encode intent. A soft mask's target follows its parent's.
/// </summary>
public class ImageClassifierTests
{
    private const int Image = 10;
    private const int Mask = 11;

    private static readonly CompressionSettings NoLevers = new(Lossless: true, MaxDpi: null, JpegQuality: null);
    private static readonly CompressionSettings Downsample150 = new(Lossless: true, MaxDpi: 150, JpegQuality: null);

    [Theory]
    [InlineData(300.0, 300.0, false)] // exactly 1.5x: not above it
    [InlineData(299.999, 400.0, false)] // the lower axis is just below
    [InlineData(300.001, 300.001, true)] // just above
    [InlineData(400.0, 300.0, false)] // only the higher axis is above
    [InlineData(1000.0, 301.0, true)]
    public void ShouldDownsample_TriggersStrictlyAboveOneAndAHalfTimesMaxDpi_OnTheLowerAxis(double dpiX, double dpiY, bool expected) =>
        Assert.Equal(expected, ImageClassifier.ShouldDownsample(dpiX, dpiY, maxDpi: 200));

    [Fact]
    public void Classify_UsesTheProductionTriggerAtTheBoundary()
    {
        // 450 pixels per inch on a 150 DPI limit: exactly 1.5x below the trigger, a hair above it.
        var atBoundary = Plan(RgbImage(450, 450), Measured(300, 300), new CompressionSettings(true, 200, null));
        var above = Plan(RgbImage(450, 450), Measured(300.001, 300.001), new CompressionSettings(true, 200, null));

        Assert.Equal(PlannedImageAction.Keep, atBoundary[Image].Action);
        Assert.Equal(PlannedImageAction.Downsample, above[Image].Action);
    }

    [Theory]
    [InlineData(600, 300, 600.0, 300.0, 150.0, 150, 150)] // each axis to the limit
    [InlineData(600, 300, 600.0, 100.0, 150.0, 150, 300)] // an axis already below the limit keeps its size
    [InlineData(2, 2, 10000.0, 10000.0, 1.0, 1, 1)] // never below one pixel
    [InlineData(1000, 1000, 333.0, 250.0, 100.0, 300, 400)] // rounded to the nearest pixel
    public void TargetSize_ScalesEachAxisToMaxDpi_NeverUpsampling(int width, int height, double dpiX, double dpiY, double maxDpi, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), ImageClassifier.TargetSize(width, height, dpiX, dpiY, maxDpi));

    [Fact]
    public void HighDpiImage_IsPlannedForDownsamplingPerAxis()
    {
        var plan = Plan(RgbImage(600, 600), Measured(600, 400, page: 3), Downsample150);

        var image = plan[Image];
        Assert.Equal(PlannedImageAction.Downsample, image.Action);
        Assert.Equal(PlannedSkip.None, image.Skip);
        Assert.Equal((600, 600), (image.Width, image.Height));
        Assert.Equal((150, 225), (image.TargetWidth, image.TargetHeight));
        Assert.Equal((600.0, 400.0), (image.DpiX, image.DpiY));
        Assert.Equal(3, image.PageIndex);
        Assert.False(image.IsSoftMask);
        Assert.Null(image.SoftMaskNumber);
    }

    [Fact]
    public void JpegQuality_ReencodesAtTheSameSize_WithOrWithoutDownsampling()
    {
        var reencode = Plan(RgbImage(600, 600), Measured(200, 200), new CompressionSettings(true, 150, 70))[Image];
        var both = Plan(RgbImage(600, 600), Measured(600, 600), new CompressionSettings(true, 150, 70))[Image];
        var jpegOnly = Plan(RgbImage(600, 600), Measured(600, 600), new CompressionSettings(true, null, 70))[Image];

        Assert.Equal((PlannedImageAction.Reencode, 600, 600), (reencode.Action, reencode.TargetWidth, reencode.TargetHeight));
        Assert.Equal((PlannedImageAction.DownsampleAndReencode, 150, 150), (both.Action, both.TargetWidth, both.TargetHeight));
        Assert.Equal((PlannedImageAction.Reencode, 600, 600), (jpegOnly.Action, jpegOnly.TargetWidth, jpegOnly.TargetHeight));
    }

    [Fact]
    public void NoLevers_KeepsAnEligibleImage()
    {
        var image = Plan(RgbImage(600, 600), Measured(600, 600), NoLevers)[Image];

        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.None, 600, 600), (image.Action, image.Skip, image.TargetWidth, image.TargetHeight));
    }

    // Skip reasons by name: the enum is internal, and theory parameters must be public types.
    public static TheoryData<string, string> DictionarySkips => new()
    {
        { "bilevel", nameof(PlannedSkip.Bilevel) },
        { "stencil", nameof(PlannedSkip.StencilMask) },
        { "16-bit", nameof(PlannedSkip.UnsupportedBitDepth) },
        { "4-bit", nameof(PlannedSkip.UnsupportedBitDepth) },
        { "cmyk", nameof(PlannedSkip.UnsupportedColorSpace) },
        { "jpx", nameof(PlannedSkip.UnsupportedFilter) },
        { "jbig2", nameof(PlannedSkip.UnsupportedFilter) },
        { "ccitt", nameof(PlannedSkip.UnsupportedFilter) },
        { "color-key", nameof(PlannedSkip.ColorKeyMask) },
        { "no-width", nameof(PlannedSkip.DecodeFailed) },
        { "too-many-pixels", nameof(PlannedSkip.ResourceLimit) },
        { "matte", nameof(PlannedSkip.MatteSoftMask) },
    };

    [Theory]
    [MemberData(nameof(DictionarySkips))]
    public void IneligibleDictionary_IsSkippedWithItsReason(string shape, string reason)
    {
        var expected = Enum.Parse<PlannedSkip>(reason);
        var dictionary = RgbImage(600, 600);
        var options = PdfOptions.Default;
        switch (shape)
        {
            case "bilevel":
                dictionary = ImageDictionary(600, 600, PdfName.Get("DeviceGray"), bitsPerComponent: 1);
                break;
            case "stencil":
                dictionary = ImageDictionary(600, 600, PdfName.Get("DeviceGray"), bitsPerComponent: 1);
                dictionary.Remove(PdfName.ColorSpace);
                dictionary.Set(PdfName.ImageMask, PdfBoolean.True);
                break;
            case "16-bit":
                dictionary.Set(PdfName.BitsPerComponent, PdfNumber.Get(16));
                break;
            case "4-bit":
                dictionary.Set(PdfName.BitsPerComponent, PdfNumber.Get(4));
                break;
            case "cmyk":
                dictionary.Set(PdfName.ColorSpace, PdfName.Get("DeviceCMYK"));
                break;
            case "jpx":
                dictionary.Set(PdfName.Filter, PdfName.Get("JPXDecode"));
                break;
            case "jbig2":
                dictionary.Set(PdfName.Filter, new PdfArray([PdfName.Get("FlateDecode"), PdfName.Get("JBIG2Decode")]));
                break;
            case "ccitt":
                dictionary.Set(PdfName.Filter, PdfName.Get("CCITTFaxDecode"));
                break;
            case "color-key":
                dictionary.Set(PdfName.Get("Mask"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0), PdfNumber.Get(10)]));
                break;
            case "no-width":
                dictionary.Remove(PdfName.Get("Width"));
                break;
            case "too-many-pixels":
                options = PdfOptions.Default with { MaxImagePixels = 1000 };
                break;
            case "matte":
                var mask = ImageDictionary(600, 600, PdfName.Get("DeviceGray"));
                mask.Remove(PdfName.ColorSpace);
                mask.Set(PdfName.Get("Matte"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0)]));
                dictionary.Set(PdfName.SMask, new PdfReference(new IndirectReference(Mask, 0)));
                var withMatte = Plan([(Image, dictionary), (Mask, mask)], Scan((Image, Measured(600, 600))), Downsample150, options);
                Assert.Equal((PlannedImageAction.Keep, expected), (withMatte[Image].Action, withMatte[Image].Skip));
                Assert.Equal((PlannedImageAction.Keep, expected), (withMatte[Mask].Action, withMatte[Mask].Skip));
                return;
        }

        var image = Plan(dictionary, Measured(600, 600), new CompressionSettings(true, 150, 70), options)[Image];

        Assert.Equal(expected, image.Skip);
        Assert.Equal(PlannedImageAction.Keep, image.Action);
        Assert.False(string.IsNullOrEmpty(image.SkipDetail));
    }

    [Theory]
    [InlineData(nameof(PlacementKind.TilingPatternOnly), nameof(PlannedSkip.TilingPatternOnly))]
    [InlineData(nameof(PlacementKind.Unmeasured), nameof(PlannedSkip.UnmeasuredPlacement))]
    public void UnmeasuredPlacement_IsSkippedEvenForReencoding(string placementKind, string reason)
    {
        var kind = Enum.Parse<PlacementKind>(placementKind);
        var expected = Enum.Parse<PlannedSkip>(reason);
        var image = Plan(RgbImage(600, 600), new ImagePlacementSummary(null, null, 2, kind), new CompressionSettings(true, 150, 70))[Image];

        Assert.Equal((PlannedImageAction.Keep, expected), (image.Action, image.Skip));
        Assert.Equal(2, image.PageIndex);
        Assert.Null(image.DpiX);
    }

    [Fact]
    public void DictionarySkip_TakesPrecedenceOverAnUnmeasuredPlacement()
    {
        var bilevel = ImageDictionary(600, 600, PdfName.Get("DeviceGray"), bitsPerComponent: 1);

        var image = Plan(bilevel, new ImagePlacementSummary(null, null, 0, PlacementKind.TilingPatternOnly), Downsample150)[Image];

        Assert.Equal(PlannedSkip.Bilevel, image.Skip);
    }

    [Fact]
    public void NotPlacedImage_IsKeptAsNoPlacement_EvenWhenOtherwiseIneligible()
    {
        var bilevel = ImageDictionary(600, 600, PdfName.Get("DeviceGray"), bitsPerComponent: 1);
        var plan = Plan(
            [(Image, RgbImage(600, 600)), (12, bilevel)],
            Scan((Image, NotPlaced(page: 2)), (12, NotPlaced())),
            new CompressionSettings(true, 150, 70));

        foreach (var number in new[] { Image, 12 })
        {
            Assert.Equal((PlannedImageAction.Keep, PlannedSkip.NoPlacement), (plan[number].Action, plan[number].Skip));
            Assert.Null(plan[number].PageIndex);
            Assert.Null(plan[number].DpiX);
            Assert.False(string.IsNullOrEmpty(plan[number].SkipDetail));
        }

        Assert.Equal((600, 600), (plan[Image].TargetWidth, plan[Image].TargetHeight));
    }

    [Fact]
    public void SoftMaskOfOnlyNotPlacedParents_IsKeptAsNoPlacement()
    {
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(600, 600))],
            Scan((Image, NotPlaced())),
            Downsample150);

        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.NoPlacement), (plan[Mask].Action, plan[Mask].Skip));
        Assert.True(plan[Mask].IsSoftMask);
        Assert.Null(plan[Mask].PageIndex);
    }

    [Fact]
    public void SoftMaskSharedWithANotPlacedParent_FollowsTheDrawnParent()
    {
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (12, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(600, 600))],
            Scan((Image, Measured(600, 600, page: 1)), (12, NotPlaced())),
            Downsample150);

        Assert.Equal(PlannedSkip.NoPlacement, plan[12].Skip);
        Assert.Equal((PlannedImageAction.Downsample, 150, 150), (plan[Mask].Action, plan[Mask].TargetWidth, plan[Mask].TargetHeight));
        Assert.Equal(1, plan[Mask].PageIndex);
    }

    [Fact]
    public void NotPlacedImageAlsoUsedAsASoftMask_IsPlannedAsTheMask()
    {
        // Listed in resources but never drawn: only its use as a mask counts.
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(600, 600))],
            Scan((Image, Measured(600, 600)), (Mask, NotPlaced())),
            Downsample150);

        Assert.Equal((PlannedImageAction.Downsample, PlannedSkip.None, 150, 150), (plan[Mask].Action, plan[Mask].Skip, plan[Mask].TargetWidth, plan[Mask].TargetHeight));
        Assert.True(plan[Mask].IsSoftMask);
    }

    [Fact]
    public void SoftMask_FollowsItsParentsPlannedSize()
    {
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(600, 600))],
            Scan((Image, Measured(600, 600, page: 1))),
            new CompressionSettings(true, 150, 70));

        Assert.Equal(Mask, plan[Image].SoftMaskNumber);
        Assert.Equal((PlannedImageAction.DownsampleAndReencode, 150, 150), (plan[Image].Action, plan[Image].TargetWidth, plan[Image].TargetHeight));

        var mask = plan[Mask];
        Assert.True(mask.IsSoftMask);
        Assert.Equal(PlannedSkip.None, mask.Skip);
        Assert.Equal((PlannedImageAction.Downsample, 150, 150), (mask.Action, mask.TargetWidth, mask.TargetHeight));
        Assert.Equal(1, mask.PageIndex);
    }

    [Fact]
    public void SoftMaskOfADifferentSize_ScalesByTheParentsFactor()
    {
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(300, 1200))],
            Scan((Image, Measured(600, 600))),
            Downsample150);

        Assert.Equal((75, 300), (plan[Mask].TargetWidth, plan[Mask].TargetHeight));
    }

    [Fact]
    public void SoftMask_IsKeptWhenItsParentIsNotDownsampled()
    {
        // Re-encoding alone never touches a mask: masks are never written as JPEG.
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(600, 600))],
            Scan((Image, Measured(100, 100))),
            new CompressionSettings(true, 150, 70));

        Assert.Equal(PlannedImageAction.Reencode, plan[Image].Action);
        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.None, 600, 600), (plan[Mask].Action, plan[Mask].Skip, plan[Mask].TargetWidth, plan[Mask].TargetHeight));
    }

    [Fact]
    public void SoftMaskOfASkippedParent_IsKept()
    {
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(600, 600))],
            Scan((Image, new ImagePlacementSummary(null, null, 0, PlacementKind.Unmeasured))),
            Downsample150);

        Assert.Equal(PlannedSkip.UnmeasuredPlacement, plan[Image].Skip);
        Assert.Equal(PlannedImageAction.Keep, plan[Mask].Action);
    }

    [Fact]
    public void SoftMaskSharedByTwoParents_TakesTheLargerTargetPerAxis()
    {
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (12, WithSoftMask(RgbImage(600, 600))), (Mask, GrayMask(600, 600))],
            Scan((Image, Measured(600, 300)), (12, Measured(300, 600))),
            Downsample150);

        Assert.Equal((150, 300), (plan[Image].TargetWidth, plan[Image].TargetHeight));
        Assert.Equal((300, 150), (plan[12].TargetWidth, plan[12].TargetHeight));
        Assert.Equal((PlannedImageAction.Downsample, 300, 300), (plan[Mask].Action, plan[Mask].TargetWidth, plan[Mask].TargetHeight));
    }

    [Fact]
    public void ImageUsedBothAsASoftMaskAndAPlainImage_IsSkippedAsShared()
    {
        var plan = Plan(
            [(Image, WithSoftMask(RgbImage(600, 600))), (Mask, ImageDictionary(600, 600, PdfName.Get("DeviceGray")))],
            Scan((Image, Measured(600, 600)), (Mask, Measured(600, 600))),
            Downsample150);

        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.SharedAsMask), (plan[Mask].Action, plan[Mask].Skip));
        Assert.True(plan[Mask].IsSoftMask);

        // The parent decides on its own.
        Assert.Equal(PlannedImageAction.Downsample, plan[Image].Action);
    }

    // An image that is drawn, has a soft mask, and is itself another image's soft mask is kept
    // as shared; its own mask then follows that kept size rather than the reduction it would
    // have had, whichever of the two masks has the lower object number.
    [Theory]
    [InlineData(10, 11, 12)]
    [InlineData(20, 5, 30)]
    public void SoftMaskOfAParentKeptAsShared_FollowsTheKeptSize(int sharedImage, int itsMask, int outerImage)
    {
        var shared = ImageDictionary(600, 600, PdfName.Get("DeviceGray"));
        shared.Set(PdfName.SMask, new PdfReference(new IndirectReference(itsMask, 0)));
        var outer = RgbImage(600, 600);
        outer.Set(PdfName.SMask, new PdfReference(new IndirectReference(sharedImage, 0)));

        var plan = Plan(
            [(sharedImage, shared), (itsMask, GrayMask(600, 600)), (outerImage, outer)],
            Scan((sharedImage, Measured(600, 600)), (outerImage, Measured(600, 600))),
            Downsample150);

        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.SharedAsMask), (plan[sharedImage].Action, plan[sharedImage].Skip));
        Assert.Equal((PlannedImageAction.Keep, 600, 600), (plan[itsMask].Action, plan[itsMask].TargetWidth, plan[itsMask].TargetHeight));
    }

    [Fact]
    public void IneligibleSoftMask_IsSkippedWithItsOwnReason()
    {
        var mask = GrayMask(600, 600);
        mask.Set(PdfName.BitsPerComponent, PdfNumber.Get(16));

        var plan = Plan([(Image, WithSoftMask(RgbImage(600, 600))), (Mask, mask)], Scan((Image, Measured(600, 600))), Downsample150);

        Assert.Equal(PlannedImageAction.Downsample, plan[Image].Action);
        Assert.Equal((PlannedImageAction.Keep, PlannedSkip.UnsupportedBitDepth), (plan[Mask].Action, plan[Mask].Skip));
    }

    [Fact]
    public void IccBasedImage_IsEligible()
    {
        var profile = new PdfDictionary();
        profile.Set(PdfName.N, PdfNumber.Get(3));
        var dictionary = ImageDictionary(600, 600, new PdfArray([PdfName.Get("ICCBased"), new PdfReference(new IndirectReference(20, 0))]));

        var plan = Plan([(Image, dictionary), (20, profile)], Scan((Image, Measured(600, 600))), Downsample150);

        Assert.Equal(PlannedImageAction.Downsample, plan[Image].Action);
    }

    [Fact]
    public void Classify_ChecksTheCancellationToken()
    {
        var objects = Registry((Image, new PdfStream(RgbImage(600, 600), new byte[4])));

        Assert.Throws<OperationCanceledException>(() => ImageClassifier.Classify(objects, Scan((Image, Measured(600, 600))), Downsample150, PdfOptions.Default, new CancellationToken(canceled: true)));
    }

    private static PdfDictionary RgbImage(int width, int height) => ImageDictionary(width, height, PdfName.Get("DeviceRGB"));

    private static PdfDictionary GrayMask(int width, int height)
    {
        var mask = ImageDictionary(width, height, PdfName.Get("DeviceGray"));
        mask.Remove(PdfName.ColorSpace);
        return mask;
    }

    private static PdfDictionary WithSoftMask(PdfDictionary image)
    {
        image.Set(PdfName.SMask, new PdfReference(new IndirectReference(Mask, 0)));
        return image;
    }

    private static ImagePlacementSummary Measured(double dpiX, double dpiY, int page = 0) => new(dpiX, dpiY, page, PlacementKind.Measured);

    private static ImagePlacementSummary NotPlaced(int page = 0) => new(null, null, page, PlacementKind.NotPlaced);

    private static PlacementScan Scan(params (int Number, ImagePlacementSummary Placement)[] images) =>
        new(images.ToDictionary(static i => i.Number, static i => i.Placement), [], new DiagnosticCollection());

    private static IReadOnlyDictionary<int, PlannedImage> Plan(PdfDictionary image, ImagePlacementSummary placement, CompressionSettings settings, PdfOptions? options = null) =>
        Plan([(Image, image)], Scan((Image, placement)), settings, options);

    private static IReadOnlyDictionary<int, PlannedImage> Plan((int Number, PdfDictionary Dictionary)[] objects, PlacementScan scan, CompressionSettings settings, PdfOptions? options = null)
    {
        var registry = Registry([.. objects.Select(static o => (o.Number, (PdfObject)(o.Dictionary.ContainsKey(PdfName.N) ? o.Dictionary : new PdfStream(o.Dictionary, new byte[4]))))]);
        return ImageClassifier.Classify(registry, scan, settings, options ?? PdfOptions.Default, CancellationToken.None);
    }
}
