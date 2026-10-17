using System.Security.Cryptography;
using PlumePdf.Documents.Compression;
using Xunit;
using Xunit.Abstractions;
using static PlumePdf.Tests.Compression.CompressEstimateFixtures;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <c>Pdf.EstimateCompression</c>, the dry run: the same refusals as <c>Compress</c> in the
/// same order with nothing written, a plan that matches what <c>Compress</c> then does image by
/// image, real trial encodes for a capped, deterministic sample, extrapolation for the rest,
/// the output-size formula, progress, the stream overloads, and the source left untouched.
/// </summary>
public class CompressEstimateTests(ITestOutputHelper output)
{
    private static readonly PdfOptions Deterministic = PdfOptions.Default with { Deterministic = true };

    // ---- Refusals and argument errors ----

    public static TheoryData<string> RefusalCases() => new() { "quality", "dpi", "linearize", "linearize+quality", "encrypted", "encrypted+linearize", "signed" };

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public void Refusals_MatchCompress_CodeForCode(string name)
    {
        var (source, compressOptions, options, cleanup) = RefusalCase(name);
        try
        {
            var before = File.ReadAllBytes(source);
            var compress = Record(() => Pdf.Compress(source, TempPath(), compressOptions, options));
            var estimate = Record(() => Pdf.EstimateCompression(source, compressOptions, options));

            Assert.NotNull(compress);
            Assert.Equal(compress.GetType(), estimate?.GetType());
            Assert.Equal((compress as PlumePdfException)?.Code, (estimate as PlumePdfException)?.Code);
            Assert.Equal(before, File.ReadAllBytes(source));
        }
        finally
        {
            if (cleanup)
            {
                Delete(source);
            }
        }
    }

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public void Refusals_ThroughANonSeekableStream_MatchCompress_AndLeaveNoSpool(string name)
    {
        var (source, compressOptions, options, cleanup) = RefusalCase(name);
        var spoolDirectory = Directory.CreateTempSubdirectory("plumepdf-estimate-spool-").FullName;
        try
        {
            var bytes = File.ReadAllBytes(source);
            var compress = Record(() =>
            {
                using var input = new ForwardOnlyStream(new MemoryStream(bytes), writable: false);
                Pdf.Compress(input, new MemoryStream(), compressOptions, options);
            });
            var estimate = Record(() =>
            {
                using var input = new ForwardOnlyStream(new MemoryStream(bytes), writable: false);
                CompressionEngine.Estimate(input, compressOptions, options, CancellationToken.None, new CompressionHooks { SpoolDirectory = spoolDirectory });
            });

            Assert.NotNull(compress);
            Assert.Equal(compress.GetType(), estimate?.GetType());
            Assert.Equal((compress as PlumePdfException)?.Code, (estimate as PlumePdfException)?.Code);
            Assert.Empty(Directory.GetFileSystemEntries(spoolDirectory));
        }
        finally
        {
            Directory.Delete(spoolDirectory, recursive: true);
            if (cleanup)
            {
                Delete(source);
            }
        }
    }

    [Fact]
    public void ArgumentErrors_AreTheBclExceptions()
    {
        Assert.Throws<ArgumentNullException>(() => Pdf.EstimateCompression((string)null!));
        Assert.Throws<ArgumentException>(() => Pdf.EstimateCompression(string.Empty));
        Assert.Throws<ArgumentNullException>(() => Pdf.EstimateCompression((Stream)null!));
        using var writeOnly = new ForwardOnlyStream(new MemoryStream(), readable: false);
        Assert.Throws<ArgumentException>(() => Pdf.EstimateCompression(writeOnly));
    }

    [Fact]
    public void ALinearizeRefusal_ComesBeforeTheInputIsRead()
    {
        var inner = new MemoryStream(Mixed().Build());
        using var input = new ForwardOnlyStream(inner, writable: false);

        var ex = Assert.Throws<PlumePdfException>(() => Pdf.EstimateCompression(input, options: PdfOptions.Default with { Linearize = true }));

        Assert.Equal("PLUME5018", ex.Code);
        Assert.Equal(0, inner.Position);
    }

    [Fact]
    public void SamePathAndOutputRefusals_DoNotApply()
    {
        var source = Write(Mixed());
        try
        {
            // PLUME1006 is about saving over the source: an estimate writes nothing.
            var hooks = new CompressionHooks { SamePathSourceLimit = 100 };
            var estimate = CompressionEngine.Estimate(source, AllLevers, null, CancellationToken.None, hooks);

            Assert.NotEmpty(estimate.Images);
        }
        finally
        {
            Delete(source);
        }
    }

    // ---- The plan matches what Compress does ----

    public static TheoryData<string, string> PlanCases() => new()
    {
        { "mixed", "all" },
        { "mixed", "lossless" },
        { "mixed", "dpi" },
        { "mixed", "quality" },
        { "mixed", "dpi-nolossless" },
        { "scan", "all" },
        { "many", "all" },
        { "many", "lossless" },
        { "multi", "all" },
    };

    [Theory]
    [MemberData(nameof(PlanCases))]
    public void EveryPlannedDecision_MatchesWhatCompressDoes(string fixture, string levers)
    {
        var pdf = Fixture(fixture);
        var options = Levers(levers);
        var estimate = Estimate(pdf, options);
        var (actual, _) = Compress(pdf, options, Deterministic);

        Assert.Equal(actual.Images.Count, estimate.Images.Count);
        for (var i = 0; i < actual.Images.Count; i++)
        {
            var (planned, done) = (estimate.Images[i], actual.Images[i]);
            var context = $"{fixture}/{levers} image {i} (object {done.SourceReference?.Number})";
            Assert.True(Equals(done.SourceReference, planned.SourceReference), context);
            Assert.True(done.PageIndex == planned.PageIndex, context);
            Assert.True(Equals(done.DuplicateOf, planned.DuplicateOf), context);
            Assert.True(done.BytesBefore == planned.BytesBefore, $"{context}: BytesBefore {planned.BytesBefore} vs {done.BytesBefore}");
            Assert.True(done.WidthBefore == planned.WidthBefore && done.HeightBefore == planned.HeightBefore, context);
            Assert.True(done.EffectiveDpiBefore == planned.EffectiveDpiBefore, context);
            Assert.True(done.EncodingBefore == planned.EncodingBefore, context);

            // An unsampled image's fate is the plan's; Compress may still keep its original
            // under the never-larger rule, which only a trial encode can tell.
            if (!planned.Sampled && done.Action == ImageCompressionAction.KeptOriginal)
            {
                Assert.True(planned.PlannedAction is ImageCompressionAction.Downsampled or ImageCompressionAction.Reencoded or ImageCompressionAction.DownsampledAndReencoded, context);
                continue;
            }

            Assert.True(done.Action == planned.PlannedAction, $"{context}: {planned.PlannedAction} planned, {done.Action} done");
            Assert.True(done.SkipReason == planned.SkipReason, context);
            Assert.True(done.WidthAfter == planned.WidthAfter && done.HeightAfter == planned.HeightAfter, $"{context}: {planned.WidthAfter}x{planned.HeightAfter} vs {done.WidthAfter}x{done.HeightAfter}");
            Assert.True(done.EffectiveDpiAfter == planned.EffectiveDpiAfter, context);

            // An unsampled unfiltered image is assumed to stay as it is (conservatively), so
            // its written encoding is only known when it was sampled.
            if (planned.Sampled || planned.EncodingBefore != ImageEncoding.Uncompressed || done.Action == ImageCompressionAction.Deduplicated)
            {
                Assert.True(done.EncodingAfter == planned.EncodingAfter, $"{context}: {planned.EncodingAfter} vs {done.EncodingAfter}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(PlanCases))]
    public void ASampledImage_IsEstimatedAtExactlyTheBytesCompressWrites(string fixture, string levers)
    {
        var pdf = Fixture(fixture);
        var options = Levers(levers);
        var estimate = Estimate(pdf, options);
        var (actual, _) = Compress(pdf, options, Deterministic);

        var sampled = estimate.Images.Where(static i => i.Sampled).ToList();
        Assert.Equal(estimate.SampledImages, sampled.Count);
        foreach (var image in sampled)
        {
            var done = Decision(actual, image.SourceReference!.Value.Number);
            Assert.Equal(done.BytesAfter, image.EstimatedBytesAfter);
            Assert.Equal(done.Action, image.PlannedAction);
        }
    }

    [Fact]
    public void KeptOriginal_IsPlannedOnlyForASampledImage()
    {
        // The text-like image (16) loses to JPEG; with everything sampled, the estimate knows.
        var estimate = Estimate(Mixed(), AllLevers);

        Assert.All(estimate.Images.Where(static i => i.PlannedAction == ImageCompressionAction.KeptOriginal), static i => Assert.True(i.Sampled));
        Assert.Contains(estimate.Images, static i => i.PlannedAction == ImageCompressionAction.KeptOriginal);
    }

    [Fact]
    public void Coverage_IsCompressResults_DuplicatesInlineImagesAndSkipsIncluded()
    {
        var estimate = Estimate(Mixed(), AllLevers);

        var duplicate = Single(estimate, 18);
        Assert.Equal(ImageCompressionAction.Deduplicated, duplicate.PlannedAction);
        Assert.Equal(new IndirectReference(10, 0), duplicate.DuplicateOf);
        Assert.Equal(0, duplicate.EstimatedBytesAfter);
        Assert.False(duplicate.Sampled);

        var bilevel = Single(estimate, 20);
        Assert.Equal(ImageCompressionAction.Skipped, bilevel.PlannedAction);
        Assert.Equal(ImageSkipReason.Bilevel, bilevel.SkipReason);
        Assert.Equal(bilevel.BytesBefore, bilevel.EstimatedBytesAfter);

        var inline = estimate.Images[^1];
        Assert.Null(inline.SourceReference);
        Assert.Equal(ImageSkipReason.InlineImage, inline.SkipReason);
        Assert.Equal(0, inline.BytesBefore);
        Assert.Equal(0, inline.EstimatedBytesAfter);
        Assert.Equal(1, inline.PageIndex);

        var mask = Single(estimate, 12);
        Assert.Equal(ImageCompressionAction.Downsampled, mask.PlannedAction);
        Assert.Equal(ImageEncoding.Flate, mask.EncodingAfter);
    }

    // ---- Sampling ----

    [Fact]
    public void Sampling_TakesAtMostThreePerGroup_AndSixteenPerCall_BiggestGroupsFirst()
    {
        var estimate = Estimate(ManyGroups(), AllLevers);

        Assert.Equal(16, estimate.SampledImages);
        var groups = estimate.Images
            .Where(static i => i.SourceReference is not null && IsLever(i.PlannedAction))
            .GroupBy(static i => (i.EncodingBefore, Action: i.Sampled && i.PlannedAction == ImageCompressionAction.KeptOriginal ? ImageCompressionAction.Reencoded : i.PlannedAction, Mask: i.EncodingAfter == ImageEncoding.Flate && i.PlannedAction == ImageCompressionAction.Downsampled))
            .ToList();
        Assert.All(groups, static g => Assert.InRange(g.Count(static i => i.Sampled), 0, 3));

        // Round-robin over groups by total size: every group gets one before any gets two.
        Assert.All(groups, static g => Assert.True(g.Any(static i => i.Sampled), $"{g.Key} unsampled"));
    }

    [Fact]
    public void SampleCounts_AreRoundRobin_InPriorityOrder_CappedPerGroupAndPerCall()
    {
        Assert.Equal([3, 3, 3], CompressionEstimator.SampleCounts([10, 5, 3]));
        Assert.Equal([3, 1, 2], CompressionEstimator.SampleCounts([10, 1, 2]));
        Assert.Equal([3, 3, 3, 3, 2, 2], CompressionEstimator.SampleCounts([9, 9, 9, 9, 9, 9]));
        Assert.Equal([2, 2, 2, 2, 2, 2, 2, 1, 1], CompressionEstimator.SampleCounts([5, 5, 5, 5, 5, 5, 5, 5, 5]));
        var many = CompressionEstimator.SampleCounts([.. Enumerable.Repeat(4, 20)]);
        Assert.Equal(16, many.Sum());
        Assert.Equal([.. Enumerable.Repeat(1, 16), 0, 0, 0, 0], many);
        Assert.Empty(CompressionEstimator.SampleCounts([]));
    }

    [Fact]
    public void SpreadIndices_AreEvenlySpaced_AndCentred()
    {
        Assert.Equal([0], CompressionEstimator.SpreadIndices(1, 1));
        Assert.Equal([2], CompressionEstimator.SpreadIndices(5, 1));
        Assert.Equal([0, 1, 2], CompressionEstimator.SpreadIndices(3, 3));
        Assert.Equal([1, 5, 8], CompressionEstimator.SpreadIndices(10, 3));
        Assert.Equal([2, 7], CompressionEstimator.SpreadIndices(10, 2));
    }

    [Fact]
    public void AGroupWithNoUsableSample_BorrowsTheMostSimilarSameEncodingGroupsRatio()
    {
        var flateMask = new CompressionEstimator.GroupKey(ImageEncoding.Flate, ImageReencodeOutcome.Downsampled, SoftMask: true);
        var jpegDownsample = new CompressionEstimator.GroupKey(ImageEncoding.Jpeg, ImageReencodeOutcome.Downsampled, SoftMask: false);
        var flateReencode = new CompressionEstimator.GroupKey(ImageEncoding.Flate, ImageReencodeOutcome.Reencoded, SoftMask: false);
        var flateBoth = new CompressionEstimator.GroupKey(ImageEncoding.Flate, ImageReencodeOutcome.DownsampledAndReencoded, SoftMask: false);
        CompressionEstimator.GroupRatio[] ratios =
        [
            new(flateMask, ImageEncoding.Flate, 0.5),
            new(jpegDownsample, ImageEncoding.Jpeg, 0.2),
            new(flateReencode, ImageEncoding.Jpeg, 0.3),
        ];

        // Its own group's ratio when it has one.
        Assert.Equal(0.3, CompressionEstimator.Ratio(ratios, flateReencode, ImageEncoding.Jpeg));

        // Same written encoding only (the Flate mask group is out); then soft-mask flag, then
        // encoding read, then outcome: a Flate source re-encoded matches on encoding read.
        Assert.Equal(0.3, CompressionEstimator.Ratio(ratios, flateBoth, ImageEncoding.Jpeg));

        // An unfiltered source downsampled to JPEG? No: writing Flate, only the mask group writes it.
        var unfilteredDownsample = new CompressionEstimator.GroupKey(ImageEncoding.Uncompressed, ImageReencodeOutcome.Downsampled, SoftMask: false);
        Assert.Equal(0.5, CompressionEstimator.Ratio(ratios, unfilteredDownsample, ImageEncoding.Flate));

        // Nothing writes the same encoding: no ratio, so the image is assumed unchanged.
        Assert.Null(CompressionEstimator.Ratio(ratios[..1], flateBoth, ImageEncoding.Jpeg));

        // A tie goes to the earlier group (the larger one by bytes).
        CompressionEstimator.GroupRatio[] tied = [new(jpegDownsample, ImageEncoding.Jpeg, 0.2), new(new(ImageEncoding.Jpeg, ImageReencodeOutcome.DownsampledAndReencoded, false), ImageEncoding.Jpeg, 0.4)];
        Assert.Equal(0.2, CompressionEstimator.Ratio(tied, new(ImageEncoding.Jpeg, ImageReencodeOutcome.Reencoded, false), ImageEncoding.Jpeg));
    }

    [Fact]
    public void TheEstimate_IsDeterministic()
    {
        var first = Describe(Estimate(ManyGroups(), AllLevers));
        var second = Describe(Estimate(ManyGroups(), AllLevers));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AnUnsampledImage_IsExtrapolatedFromItsGroup_ByTargetPixels_AndNeverLarger()
    {
        var estimate = Estimate(JpegScan(), ScanLevers);

        var sampled = estimate.Images.Where(static i => i.Sampled).ToList();
        Assert.Equal(3, sampled.Count);
        var ratio = (double)sampled.Sum(static i => i.EstimatedBytesAfter) / sampled.Sum(static i => (long)i.WidthAfter * i.HeightAfter);
        foreach (var image in estimate.Images.Where(static i => !i.Sampled))
        {
            var expected = Math.Min(image.BytesBefore, (long)Math.Round(ratio * image.WidthAfter * image.HeightAfter));
            Assert.Equal(expected, image.EstimatedBytesAfter);
        }
    }

    // ---- The output-size formula and accuracy ----

    [Theory]
    [MemberData(nameof(PlanCases))]
    public void EstimatedOutputBytes_IsInputLessTheImagesAndTheNonImageSavings(string fixture, string levers)
    {
        var pdf = Fixture(fixture);
        var options = Levers(levers);
        var estimate = Estimate(pdf, options);

        var saved = estimate.Images.Where(static i => i.SourceReference is not null).Sum(static i => i.BytesBefore - i.EstimatedBytesAfter);
        Assert.Equal(estimate.InputBytes - saved - estimate.EstimatedNonImageSavedBytes + estimate.EstimatedStructureGrowthBytes, estimate.EstimatedOutputBytes);
        Assert.Equal(estimate.InputBytes - estimate.EstimatedOutputBytes, estimate.EstimatedSavedBytes);

        // The fixtures repeat no stream but images and page content: with the lossless levers
        // on, every repeat of a content stream is written once, and the copy kept may be Flated.
        if (options?.Lossless == false)
        {
            Assert.Equal(0, estimate.EstimatedNonImageSavedBytes);
            Assert.Equal(0, estimate.SampledNonImageStreams);
        }
        else
        {
            Assert.True(estimate.EstimatedNonImageSavedBytes >= RepeatedContentBytes(pdf.Build()));
        }
    }

    [Fact]
    public void ADuplicateNonImageStream_CountsAsRemoved_AndTheCopyKeptAsFlated()
    {
        // Two pages with byte-identical, unfiltered content streams: the lossless levers write
        // one, Flate-compressed (it's sampled, so its saving is the real one).
        var content = "0 0 10 10 re f ".PadRight(4000, ' ');
        var pdf = new ScanTestPdf().Page(3, content).Page(5, content);

        var estimate = Estimate(pdf, null);
        var withoutLossless = Estimate(pdf, new PdfCompressOptions { Lossless = false });

        var flated = CompressionTestImages.Flate(System.Text.Encoding.Latin1.GetBytes(content)).Length + CompressionEstimator.FilterEntryBytes;
        Assert.Empty(estimate.Images);
        Assert.Equal(1, estimate.SampledNonImageStreams);
        Assert.Equal(4000 + (4000 - flated), estimate.EstimatedNonImageSavedBytes);
        Assert.Equal(estimate.InputBytes - estimate.EstimatedNonImageSavedBytes, estimate.EstimatedOutputBytes);
        Assert.Equal(withoutLossless.InputBytes, withoutLossless.EstimatedOutputBytes);
    }

    [Fact]
    public void OnlyTheStreamsTheWriteWouldFlate_AreCounted()
    {
        var text = string.Concat(Enumerable.Repeat("BT /F1 12 Tf (Hello) Tj ET ", 400));
        var pdf = new ScanTestPdf()
            .Stream(20, "/Type /Metadata /Subtype /XML", text) // XMP stays unfiltered
            .Stream(22, "/F (external.dat)", text) // external file data
            .Stream(24, "/Filter /ASCIIHexDecode", text) // already filtered
            .Stream(26, string.Empty, text) // reachable from nothing: never written
            .Page(3, "0 0 10 10 re f", extra: "/Metadata 20 0 R /X1 22 0 R /X2 24 0 R");

        var estimate = Estimate(pdf, null);

        // Only the page's content stream is eligible, and it is too small to gain from Flate.
        Assert.Equal(1, estimate.SampledNonImageStreams);
        Assert.Equal(0, estimate.EstimatedNonImageSavedBytes);
    }

    [Fact]
    public void ManyEligibleStreams_TheLargest32AreSampled_TheRestExtrapolated()
    {
        var pdf = new ScanTestPdf();
        for (var page = 0; page < 40; page++)
        {
            pdf.Page(3 + (2 * page), ContentOperators(page, 200 + (page * 10)));
        }

        var estimate = Estimate(pdf, null);
        var (actual, _) = Compress(pdf, null, Deterministic);

        Assert.Equal(32, estimate.SampledNonImageStreams);
        var error = (double)(estimate.EstimatedOutputBytes - actual.OutputBytes) / actual.OutputBytes;
        output.WriteLine($"input {estimate.InputBytes}, estimated {estimate.EstimatedOutputBytes} (non-image saving {estimate.EstimatedNonImageSavedBytes}), actual {actual.OutputBytes}, error {error:P2}");

        // Measured: 4.3% over (both TFMs), where counting no non-image saving was 543% over.
        Assert.InRange(error, 0.02, 0.07);
    }

    [Fact]
    public void TheStreamSampleBudget_SkipsAStreamThatDoesNotFit()
    {
        var (large, medium, small) = (ContentOperators(1, 2000), ContentOperators(2, 600), ContentOperators(3, 300));
        var pdf = new ScanTestPdf().Page(3, large).Page(5, medium).Page(7, small);
        var source = Write(pdf);
        try
        {
            // Room for the two smaller streams, not the largest: it is passed over, they are read.
            var budget = new CompressionHooks { NonImageSampleBudget = medium.Length + small.Length };
            var estimate = CompressionEngine.Estimate(source, null, Deterministic, CancellationToken.None, budget);

            Assert.Equal(2, estimate.SampledNonImageStreams);
            Assert.True(estimate.EstimatedNonImageSavedBytes > large.Length / 2, "the largest is extrapolated from the others' ratio");
        }
        finally
        {
            Delete(source);
        }
    }

    [Fact]
    public void StreamSamples_AreLargestFirst_WithinTheCountAndByteBudget()
    {
        Assert.Equal([0, 1, 2], CompressionEstimator.StreamSamples([30, 20, 10], budget: 100, maximum: 32));
        Assert.Equal([1, 2], CompressionEstimator.StreamSamples([300, 20, 10], budget: 100, maximum: 32));
        Assert.Equal([0, 2], CompressionEstimator.StreamSamples([60, 50, 30], budget: 100, maximum: 32));
        Assert.Equal([0, 1], CompressionEstimator.StreamSamples([5, 5, 5], budget: 100, maximum: 2));
        Assert.Empty(CompressionEstimator.StreamSamples([500], budget: 100, maximum: 32));
    }

    [Fact]
    public void AnUnsampledStream_SavesWhatTheRatioPredicts_LessTheFilterEntry_NeverBelowZero()
    {
        Assert.Equal(1000 - (250 + CompressionEstimator.FilterEntryBytes), CompressionEstimator.ExtrapolatedFlateSaving(1000, 0.25));
        Assert.Equal(0, CompressionEstimator.ExtrapolatedFlateSaving(20, 0.25));
        Assert.Equal(0, CompressionEstimator.ExtrapolatedFlateSaving(1000, 1.0));
    }

    // Measured (both TFMs, macOS arm64): the scan's estimate is 6.5% over the written size (the
    // images alone 5.9% over: three samples from 24 pages whose noise differs page to page);
    // the mixed document's 1.3% over (every rewritten image sampled, so the images are exact
    // and the gap is the unestimated object-stream packing); many groups with the cap hit, 3.7% over (the
    // images 1.4% under). The bands allow for another zlib build's Flate output.
    [Fact]
    public void Accuracy_OnAHomogeneousJpegScan_IsWithinTheMeasuredBand()
    {
        var error = RelativeError(JpegScan(), ScanLevers, out var estimate);

        Assert.Equal(3, estimate.SampledImages);
        Assert.InRange(error, 0.045, 0.085);
    }

    [Fact]
    public void Accuracy_OnAMixedDocument_IsWithinTheMeasuredBand()
    {
        var error = RelativeError(Mixed(), AllLevers, out _);

        Assert.InRange(error, 0.0, 0.03);
    }

    [Fact]
    public void Accuracy_OnManyGroups_WithTheCapHit_IsWithinTheMeasuredBand()
    {
        var error = RelativeError(ManyGroups(), AllLevers, out _);

        Assert.InRange(error, 0.02, 0.055);
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("many")]
    [InlineData("scan")]
    [InlineData("multi")]
    public void LosslessOnly_TheEstimateIsNeverBelowTheActualSize(string fixture)
    {
        var estimate = Estimate(Fixture(fixture), null);
        var (actual, _) = Compress(Fixture(fixture), null, Deterministic);

        output.WriteLine($"{fixture}: estimated {estimate.EstimatedOutputBytes}, actual {actual.OutputBytes}");
        Assert.True(estimate.EstimatedOutputBytes >= actual.OutputBytes, $"estimated {estimate.EstimatedOutputBytes} < actual {actual.OutputBytes}");
    }

    // ---- Regressions from the corpus review ----

    [Fact]
    public void ASampleKeptUnderNeverLarger_FeedsItsTrialSize_NotItsOriginal_IntoTheRatio()
    {
        // Every image is kept (JPEG is larger for all of them), and the samples are the
        // smallest originals: estimating the rest from those originals would shrink them.
        var options = new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 80 } };
        var estimate = Estimate(KeptTextGroup(), options);
        var (actual, _) = Compress(KeptTextGroup(), options, Deterministic);

        Assert.All(actual.Images, static i => Assert.Equal(ImageCompressionAction.KeptOriginal, i.Action));
        Assert.Equal(3, estimate.SampledImages);
        Assert.Equal(actual.Images.Sum(static i => i.BytesAfter), estimate.Images.Sum(static i => i.EstimatedBytesAfter));
    }

    [Fact]
    public void AnUnrepresentativeSample_LeavesThePointEstimateUnder_ButTheCautiousFigureCoversTheOutput()
    {
        var options = ScanLevers with { Lossless = false };
        var estimate = Estimate(BlankSamplesAmongNoisyPages(), options);
        var (actual, _) = Compress(BlankSamplesAmongNoisyPages(), options, Deterministic);

        output.WriteLine($"estimated {estimate.EstimatedOutputBytes}, extrapolated saving {estimate.ExtrapolatedSavedBytes}, actual {actual.OutputBytes}");
        Assert.True(estimate.EstimatedOutputBytes < actual.OutputBytes, "the blank samples make the rest look smaller than they are");
        Assert.True(estimate.EstimatedOutputBytes + estimate.ExtrapolatedSavedBytes >= actual.OutputBytes);
        Assert.Equal(
            estimate.Images.Where(static i => i.SourceReference is not null && !i.Sampled && i.PlannedAction != ImageCompressionAction.Deduplicated).Sum(static i => i.BytesBefore - i.EstimatedBytesAfter),
            estimate.ExtrapolatedSavedBytes);
    }

    [Fact]
    public void WithEverythingSampled_NothingIsExtrapolated()
    {
        var estimate = Estimate(Mixed(), AllLevers);

        Assert.Equal(0, estimate.ExtrapolatedSavedBytes);
    }

    [Fact]
    public void AnUnsampledStreamsSaving_CountsAsExtrapolated()
    {
        var pdf = new ScanTestPdf();
        for (var page = 0; page < 40; page++)
        {
            pdf.Page(3 + (2 * page), ContentOperators(page, 200 + (page * 10)));
        }

        var estimate = Estimate(pdf, null);

        Assert.InRange(estimate.ExtrapolatedSavedBytes, 1, estimate.EstimatedNonImageSavedBytes - 1);
    }

    [Fact]
    public void WithoutPacking_AnObjectStreamSource_IsEstimatedToGrow()
    {
        var source = ObjectStreamPackedSource();
        var options = new PdfCompressOptions { Lossless = false };
        var input = Write(source);
        var outputPath = TempPath();
        try
        {
            var estimate = Pdf.EstimateCompression(input, options, Deterministic);
            var actual = Pdf.Compress(input, outputPath, options, Deterministic);

            var error = (double)(estimate.EstimatedOutputBytes - actual.OutputBytes) / actual.OutputBytes;
            output.WriteLine($"input {estimate.InputBytes}, growth {estimate.EstimatedStructureGrowthBytes}, estimated {estimate.EstimatedOutputBytes}, actual {actual.OutputBytes}, error {error:P2}");
            Assert.True(actual.OutputBytes > estimate.InputBytes, "unpacking grows the file");
            Assert.True(estimate.EstimatedStructureGrowthBytes > 0);
            Assert.InRange(error, -0.03, 0.05);
        }
        finally
        {
            Delete(input, outputPath);
        }
    }

    [Fact]
    public void PackedOutput_EstimatesNoStructureGrowth()
    {
        var estimate = Estimate(Mixed(), AllLevers);

        Assert.Equal(0, estimate.EstimatedStructureGrowthBytes);
    }

    [Fact]
    public void SignaturesToStrip_IsWhatCompressWouldStrip()
    {
        var signed = CompressSignatureTests.SignedFormSource();
        var output = TempPath();
        try
        {
            var allow = new PdfCompressOptions { AllowInvalidatingSignatures = true };
            var estimate = Pdf.EstimateCompression(signed, allow);
            var actual = Pdf.Compress(signed, output, allow);

            Assert.Equal(actual.SignaturesStripped, estimate.SignaturesToStrip);
            Assert.Equal(1, estimate.SignaturesToStrip);
            Assert.Equal(0, Estimate(Mixed(), AllLevers).SignaturesToStrip);
        }
        finally
        {
            Delete(signed, output);
        }
    }

    [Theory]
    [InlineData("scan", "all")]
    [InlineData("scan", "dpi")]
    [InlineData("mixed", "all")]
    [InlineData("many", "all")]
    public void EstimatedSourceQuality_IsCompresssForEveryImage(string fixture, string levers)
    {
        var estimate = Estimate(Fixture(fixture), Levers(levers));
        var (actual, _) = Compress(Fixture(fixture), Levers(levers), Deterministic);

        Assert.Equal(actual.Images.Select(static i => i.EstimatedSourceQuality), estimate.Images.Select(static i => i.EstimatedSourceQuality));
        if (fixture == "scan")
        {
            Assert.Contains(estimate.Images, static i => i.EstimatedSourceQuality is not null && !i.Sampled);
        }
    }

    [Fact]
    public void CompressionStage_ValuesFollowTheirDeclaredOrder()
    {
        Assert.Equal([0, 1, 2, 3], new[] { CompressionStage.Spooling, CompressionStage.Planning, CompressionStage.Sampling, CompressionStage.Writing }.Select(static s => (int)s));
        Assert.Equal([CompressionStage.Spooling, CompressionStage.Planning, CompressionStage.Sampling, CompressionStage.Writing], Enum.GetValues<CompressionStage>());
    }

    // ---- Diagnostics ----

    [Fact]
    public void Diagnostics_CarryCompresssPlanTimeSummaries_AndNothingGoesOnTheDocument()
    {
        var estimate = Estimate(Mixed(), AllLevers, PdfOptions.Default with { PdfVersion = "1.4" });

        Assert.Contains(estimate.Diagnostics, static d => d.Code == "PLUME6088");
        Assert.Contains(estimate.Diagnostics, static d => d.Code == "PLUME6085" && d.Message.Contains("Bilevel", StringComparison.Ordinal));
        Assert.Contains(estimate.Diagnostics, static d => d.Code == "PLUME6087");
        Assert.DoesNotContain(estimate.Diagnostics, static d => d.Code == "PLUME6090");

        var lossless = Estimate(Mixed(), null);
        Assert.Contains(lossless.Diagnostics, static d => d.Code == "PLUME6090");
    }

    [Fact]
    public void Diagnostics_HaveTheSameCodesCompressRaises_WhenEverythingIsSampled()
    {
        var estimate = Estimate(Mixed(), AllLevers);
        var (actual, _) = Compress(Mixed(), AllLevers);

        Assert.Equal(actual.Diagnostics.Select(static d => d.Code), estimate.Diagnostics.Select(static d => d.Code));
    }

    // ---- Progress ----

    [Fact]
    public void Progress_ReportsPlanningThenSampling_EachFramedFromZeroToTotal()
    {
        var progress = new Recording();
        var estimate = Estimate(Mixed(), AllLevers with { Progress = progress });

        Assert.Equal([CompressionStage.Planning, CompressionStage.Sampling], progress.Reports.Select(static r => r.Stage).Distinct());
        var sampling = progress.Reports.Where(static r => r.Stage == CompressionStage.Sampling).ToList();
        var total = estimate.SampledImages + estimate.SampledNonImageStreams;
        Assert.Equal(Enumerable.Range(0, total + 1).Select(i => new CompressionProgress(CompressionStage.Sampling, i, total)), sampling);
        Assert.Equal(new CompressionProgress(CompressionStage.Planning, 3, 3), progress.Reports.Last(static r => r.Stage == CompressionStage.Planning));
    }

    [Fact]
    public void Progress_WithNothingToSample_ReportsZeroOfZero()
    {
        var progress = new Recording();
        Estimate(new ScanTestPdf().Page(3, "0 0 10 10 re f"), new PdfCompressOptions { Progress = progress, Lossless = false });

        Assert.Equal([new CompressionProgress(CompressionStage.Sampling, 0, 0)], progress.Reports.Where(static r => r.Stage == CompressionStage.Sampling));
    }

    [Fact]
    public void Progress_FromANonSeekableInput_StartsWithSpooling()
    {
        var progress = new Recording();
        using var input = new ForwardOnlyStream(new MemoryStream(Mixed().Build()), writable: false);

        Pdf.EstimateCompression(input, AllLevers with { Progress = progress });

        Assert.Equal([CompressionStage.Spooling, CompressionStage.Planning, CompressionStage.Sampling], progress.Reports.Select(static r => r.Stage).Distinct());
        Assert.All(progress.Reports.Where(static r => r.Stage == CompressionStage.Spooling), static r => Assert.Null(r.Total));
    }

    // ---- Overloads, side effects ----

    [Fact]
    public async Task EveryOverload_GivesTheSameEstimate()
    {
        var bytes = Mixed().Build();
        var source = Write(bytes);
        try
        {
            var path = Describe(Pdf.EstimateCompression(source, AllLevers));
            var pathAsync = Describe(await Pdf.EstimateCompressionAsync(source, AllLevers));
            using var seekable = new MemoryStream(bytes);
            var stream = Describe(Pdf.EstimateCompression(seekable, AllLevers));
            using var forward = new ForwardOnlyStream(new MemoryStream(bytes), writable: false);
            var nonSeekable = Describe(Pdf.EstimateCompression(forward, AllLevers));
            using var forwardAsync = new ForwardOnlyStream(new MemoryStream(bytes), writable: false);
            var nonSeekableAsync = Describe(await Pdf.EstimateCompressionAsync(forwardAsync, AllLevers));

            Assert.Equal(path, pathAsync);
            Assert.Equal(path, stream);
            Assert.Equal(path, nonSeekable);
            Assert.Equal(path, nonSeekableAsync);
            Assert.Equal(bytes, seekable.ToArray());
        }
        finally
        {
            Delete(source);
        }
    }

    [Fact]
    public async Task ANonSeekableInput_IsSpooled_AndTheSpoolIsDeleted()
    {
        foreach (var asynchronous in new[] { false, true })
        {
            string? spool = null;
            var hooks = new CompressionHooks { Spooled = path => spool = path };
            using var input = new ForwardOnlyStream(new MemoryStream(Mixed().Build()), writable: false);

            var estimate = asynchronous
                ? await CompressionEngine.EstimateAsync(input, AllLevers, null, CancellationToken.None, hooks)
                : CompressionEngine.Estimate(input, AllLevers, null, CancellationToken.None, hooks);

            Assert.NotNull(spool);
            Assert.False(File.Exists(spool));
            Assert.Equal(Mixed().Build().Length, estimate.InputBytes);
        }
    }

    [Fact]
    public async Task Cancellation_StopsTheEstimate_AndDeletesTheSpool()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var source = Write(Mixed());
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pdf.EstimateCompressionAsync(source, AllLevers, cancellationToken: cancelled.Token));
        }
        finally
        {
            Delete(source);
        }

        using var sampling = new CancellationTokenSource();
        var progress = new Recording { OnReport = r => { if (r is { Stage: CompressionStage.Sampling, Completed: 1 }) { sampling.Cancel(); } } };
        string? spool = null;
        using var input = new ForwardOnlyStream(new MemoryStream(Mixed().Build()), writable: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompressionEngine.EstimateAsync(input, AllLevers with { Progress = progress }, null, sampling.Token, new CompressionHooks { Spooled = p => spool = p }));
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public void TheSourceFile_IsUntouched_AndNothingIsWrittenBesideIt()
    {
        var source = Write(Mixed());
        try
        {
            var hash = SHA256.HashData(File.ReadAllBytes(source));
            var siblings = Directory.GetFiles(Path.GetDirectoryName(source)!, $"*{Path.GetFileNameWithoutExtension(source)}*");

            Pdf.EstimateCompression(source, AllLevers);

            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
            Assert.Equal(siblings, Directory.GetFiles(Path.GetDirectoryName(source)!, $"*{Path.GetFileNameWithoutExtension(source)}*"));
        }
        finally
        {
            Delete(source);
        }
    }

    [Fact]
    public void ASignedSource_WithAllowInvalidatingSignatures_IsEstimated_WithoutTouchingTheFile()
    {
        var signed = CompressSignatureTests.SignedFormSource();
        var output = TempPath();
        try
        {
            var bytes = File.ReadAllBytes(signed);
            var allow = new PdfCompressOptions { AllowInvalidatingSignatures = true };

            var estimate = Pdf.EstimateCompression(signed, allow);

            Assert.Equal(bytes, File.ReadAllBytes(signed));
            using (var reopened = PdfDocument.Open(signed))
            {
                Assert.NotEmpty(reopened.Signatures);
            }

            var actual = Pdf.Compress(signed, output, allow);
            Assert.Equal(actual.Images.Select(static i => (i.SourceReference, i.Action)), estimate.Images.Select(static i => (i.SourceReference, i.PlannedAction)));
        }
        finally
        {
            Delete(signed, output);
        }
    }

    [Fact]
    public void Compress_WritesTheSameBytes_WithOrWithoutAnEstimateFirst()
    {
        var source = Write(Mixed());
        var first = TempPath();
        var second = TempPath();
        try
        {
            Pdf.Compress(source, first, AllLevers, Deterministic);
            Pdf.EstimateCompression(source, AllLevers, Deterministic);
            Pdf.Compress(source, second, AllLevers, Deterministic);

            Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        }
        finally
        {
            Delete(source, first, second);
        }
    }

    // ---- Helpers ----

    // Distinct, compressible page content: 'lines' lines of text-showing operators.
    private static string ContentOperators(int seed, int lines) =>
        string.Concat(Enumerable.Range(0, lines).Select(i => $"BT /F1 {9 + ((i + seed) % 5)} Tf {(i * 7) % 500} {(i * 13) % 700} Td (Line {i} of page {seed}) Tj ET\n"));

    // The bytes of every page content stream identical to an earlier page's.
    private static long RepeatedContentBytes(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var seen = new HashSet<string>();
        long repeated = 0;
        for (var i = 0; i < document.Pages.Count; i++)
        {
            var content = PageContent(document, i);
            if (!seen.Add(Convert.ToHexString(content.RawBytes.Span)))
            {
                repeated += content.RawBytes.Length;
            }
        }

        return repeated;
    }

    private static bool IsLever(ImageCompressionAction action) =>
        action is ImageCompressionAction.Downsampled or ImageCompressionAction.Reencoded or ImageCompressionAction.DownsampledAndReencoded or ImageCompressionAction.KeptOriginal;

    private double RelativeError(ScanTestPdf pdf, PdfCompressOptions? options, out CompressionEstimate estimate)
    {
        estimate = Estimate(pdf, options);
        var (actual, _) = Compress(pdf, options, Deterministic);
        var error = (double)(estimate.EstimatedOutputBytes - actual.OutputBytes) / actual.OutputBytes;
        var imagesEstimated = estimate.Images.Sum(static i => i.EstimatedBytesAfter);
        var imagesActual = actual.Images.Sum(static i => i.BytesAfter);
        output.WriteLine($"input {estimate.InputBytes}, estimated {estimate.EstimatedOutputBytes}, actual {actual.OutputBytes}, error {error:P2}, sampled {estimate.SampledImages}; images estimated {imagesEstimated}, written {imagesActual} ({(double)(imagesEstimated - imagesActual) / imagesActual:P2})");
        return error;
    }

    private static CompressionEstimate Estimate(ScanTestPdf pdf, PdfCompressOptions? compressOptions, PdfOptions? options = null)
    {
        var source = Write(pdf);
        try
        {
            return Pdf.EstimateCompression(source, compressOptions, options ?? Deterministic);
        }
        finally
        {
            Delete(source);
        }
    }

    private static PlannedImageCompression Single(CompressionEstimate estimate, int number) =>
        estimate.Images.Single(i => i.SourceReference?.Number == number);

    private static string Describe(CompressionEstimate estimate) =>
        string.Join('\n', [
            $"{estimate.InputBytes} {estimate.EstimatedOutputBytes} {estimate.SampledImages} {estimate.EstimatedNonImageSavedBytes} {estimate.SampledNonImageStreams} {estimate.ExtrapolatedSavedBytes} {estimate.EstimatedStructureGrowthBytes} {estimate.SignaturesToStrip}",
            .. estimate.Images.Select(static i => $"{i.SourceReference} {i.PageIndex} {i.PlannedAction} {i.SkipReason} {i.DuplicateOf} {i.BytesBefore} {i.EstimatedBytesAfter} {i.Sampled} {i.WidthAfter}x{i.HeightAfter} {i.EncodingBefore}>{i.EncodingAfter} {i.EffectiveDpiAfter} {i.EstimatedSourceQuality}"),
            .. estimate.Diagnostics.Select(static d => $"{d.Code} {d.Message}"),
        ]);

    private static ScanTestPdf Fixture(string name) => name switch
    {
        "mixed" => Mixed(),
        "scan" => JpegScan(),
        "many" => ManyGroups(),
        _ => new ScanTestPdf()
            .Stream(10, Image(), FlateSamples())
            .Stream(12, Image(), FlateSamples())
            .Stream(14, Image(colorSpace: "/DeviceGray", bits: 1), new byte[Pixels * Pixels / 8])
            .Stream(18, Image(extra: "/SMask 20 0 R"), FlateSamples(seed: 2))
            .Stream(20, Image(colorSpace: "/DeviceGray"), FlateSamples(channels: 1, seed: 3))
            .Stream(22, Image(), FlateSamples(seed: 4))
            .Page(3, $"{Draw("Im0")} {Draw("Im1")}", xobjects: "/Im0 10 0 R /Im1 12 0 R")
            .Page(5, $"{Draw("Im0")} q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI Q", xobjects: "/Im0 14 0 R")
            .Page(7, Draw("Im0"), xobjects: "/Im0 18 0 R"),
    };

    private static PdfCompressOptions? Levers(string name) => name switch
    {
        "all" => AllLevers,
        "dpi" => new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 100 } },
        "quality" => new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 60 } },
        "dpi-nolossless" => new PdfCompressOptions { Lossless = false, Images = new PdfImageCompression { MaxDpi = 100 } },
        _ => null,
    };

    private static (string Source, PdfCompressOptions? CompressOptions, PdfOptions? Options, bool Cleanup) RefusalCase(string name)
    {
        var encrypted = Path.Combine(RepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "AES-128.pdf");
        var linearize = PdfOptions.Default with { Linearize = true };
        var badQuality = new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 0 } };
        return name switch
        {
            "quality" => (Write(Mixed()), badQuality, null, true),
            "dpi" => (Write(Mixed()), new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = double.NaN } }, null, true),
            "linearize" => (Write(Mixed()), null, linearize, true),
            "linearize+quality" => (Write(Mixed()), badQuality, linearize, true),
            "encrypted" => (encrypted, null, null, false),
            "encrypted+linearize" => (encrypted, null, linearize, false),
            _ => (CompressSignatureTests.SignedFormSource(), null, null, true),
        };
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class Recording : IProgress<CompressionProgress>
    {
        private readonly List<CompressionProgress> _reports = [];

        public Action<CompressionProgress>? OnReport { get; init; }

        public IReadOnlyList<CompressionProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(CompressionProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }

            OnReport?.Invoke(value);
        }
    }
}
