using PlumePdf.Documents.Compression;
using PlumePdf.Documents.Metadata;
using PlumePdf.Filters;
using PlumePdf.Tests.PageRemoval;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <c>Pdf.Compress</c> end to end: the lossless levers by default, each image lever on its own
/// and together, the never-larger rule, every skip reason surfacing in the result, soft masks,
/// per-image decode failures under Strict, determinism, and result-scoped diagnostics.
/// </summary>
public class CompressTests
{
    [Fact]
    public void Default_FlatesUnfilteredStreams_PacksObjects_AndWritesDuplicatesOnce()
    {
        var pdf = new ScanTestPdf { MinimumStreamBytes = 4096 }
            .Stream(10, Image(), FlateSamples())
            .Stream(12, Image(), FlateSamples())
            .Page(3, Draw(), xobjects: "/Im0 10 0 R")
            .Page(5, Draw(), xobjects: "/Im0 12 0 R");

        var (result, output) = Compress(pdf);

        using var document = PdfDocument.Open(output);
        Assert.Equal("FlateDecode", FilterOf(PageContent(document, 0)));
        Assert.True(HasObjectStreams(output));
        Assert.Equal(PageImage(document, 0).Reference, PageImage(document, 1).Reference);

        var canonical = Decision(result, 10);
        Assert.Equal(ImageCompressionAction.Unchanged, canonical.Action);
        var duplicate = Decision(result, 12);
        Assert.Equal(ImageCompressionAction.Deduplicated, duplicate.Action);
        Assert.Equal(new IndirectReference(10, 0), duplicate.DuplicateOf);
        Assert.Equal(0, duplicate.BytesAfter);
        Assert.Equal(canonical.BytesBefore, duplicate.BytesBefore);
        Assert.Equal((Pixels, Pixels), (duplicate.WidthBefore, duplicate.HeightBefore));

        Assert.Contains(result.Diagnostics, static d => d.Code == "PLUME6090" && d.Severity == DiagnosticSeverity.Info);
        Assert.True(result.OutputBytes < result.InputBytes);
        Assert.Equal(result.InputBytes - result.OutputBytes, result.SavedBytes);
        CleanupFixtures3b.AssertQpdfClean(output, "default lossless compress");
    }

    [Fact]
    public void LosslessOff_WithNoImageLevers_NeitherPacksNorFlates()
    {
        var pdf = new ScanTestPdf { MinimumStreamBytes = 4096 }
            .Stream(10, Image(), FlateSamples())
            .Page(3, Draw(), xobjects: "/Im0 10 0 R");

        var (result, output) = Compress(pdf, new PdfCompressOptions { Lossless = false }, PdfOptions.Default with { Optimize = true });

        using var document = PdfDocument.Open(output);
        Assert.Null(FilterOf(PageContent(document, 0)));
        Assert.False(HasObjectStreams(output));
        Assert.Equal(ImageCompressionAction.Unchanged, Decision(result, 10).Action);
        Assert.Contains(result.Diagnostics, static d => d.Code == "PLUME6090");
    }

    [Fact]
    public void MaxDpiOnly_FlateStaysFlate_AndJpegKeepsItsEstimatedQuality()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Image(), FlateSamples())
            .Stream(11, Image(filter: "/DCTDecode"), JpegSamples(quality: 75))
            .Page(3, $"{Draw("A")} {Draw("B")}", xobjects: "/A 10 0 R /B 11 0 R");

        var (result, output) = Compress(pdf, new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 100 } });

        using var document = PdfDocument.Open(output);
        var flate = PageImage(document, 0, "A").Stream;
        Assert.Equal(("FlateDecode", 100, 100), (FilterOf(flate), IntOf(flate, "Width"), IntOf(flate, "Height")));
        var jpeg = PageImage(document, 0, "B").Stream;
        Assert.Equal(("DCTDecode", 100), (FilterOf(jpeg), IntOf(jpeg, "Width")));
        Assert.True(JpegQualityEstimator.TryEstimate(jpeg.RawBytes.Span, out var quality));
        Assert.Equal(75, quality);

        var flateDecision = Decision(result, 10);
        Assert.Equal(ImageCompressionAction.Downsampled, flateDecision.Action);
        Assert.Equal((Pixels, Pixels, 100, 100), (flateDecision.WidthBefore, flateDecision.HeightBefore, flateDecision.WidthAfter, flateDecision.HeightAfter));
        Assert.Equal(300, flateDecision.EffectiveDpiBefore!.Value, precision: 6);
        Assert.Equal(100, flateDecision.EffectiveDpiAfter!.Value, precision: 6);
        Assert.Equal(0, flateDecision.PageIndex);
        Assert.Null(flateDecision.EstimatedSourceQuality);
        Assert.True(flateDecision.BytesAfter < flateDecision.BytesBefore);
        Assert.Equal(flate.RawBytes.Length, flateDecision.BytesAfter);

        var jpegDecision = Decision(result, 11);
        Assert.Equal(ImageCompressionAction.Downsampled, jpegDecision.Action);
        Assert.Equal(75, jpegDecision.EstimatedSourceQuality);
        Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "PLUME6090");
    }

    [Fact]
    public void JpegQualityOnly_ReencodesAtTheSameSize()
    {
        var (result, output) = Compress(OneImage(Image(), FlateSamples()), new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 60 } });

        using var document = PdfDocument.Open(output);
        var image = PageImage(document, 0).Stream;
        Assert.Equal(("DCTDecode", Pixels, Pixels), (FilterOf(image), IntOf(image, "Width"), IntOf(image, "Height")));
        Assert.True(JpegQualityEstimator.TryEstimate(image.RawBytes.Span, out var quality));
        Assert.Equal(60, quality);
        var decision = Decision(result, 10);
        Assert.Equal(ImageCompressionAction.Reencoded, decision.Action);
        Assert.Equal(decision.EffectiveDpiBefore, decision.EffectiveDpiAfter);
    }

    [Fact]
    public void BothLevers_DownsampleAndReencode()
    {
        var (result, output) = Compress(OneImage(Image(), FlateSamples()), AllLevers);

        using var document = PdfDocument.Open(output);
        var image = PageImage(document, 0).Stream;
        Assert.Equal(("DCTDecode", 100, 100), (FilterOf(image), IntOf(image, "Width"), IntOf(image, "Height")));
        Assert.Equal(ImageCompressionAction.DownsampledAndReencoded, Decision(result, 10).Action);
        CleanupFixtures3b.AssertQpdfClean(output, "all-levers compress");
    }

    [Fact]
    public void NotSmaller_KeepsTheOriginal()
    {
        // A flat colour Flates to almost nothing; no JPEG of it is smaller.
        var flat = new byte[Pixels * Pixels * 3];
        Array.Fill(flat, (byte)128);
        var payload = CompressionTestImages.Flate(flat);

        var (result, output) = Compress(OneImage(Image(), payload), new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 95 } });

        using var document = PdfDocument.Open(output);
        var image = PageImage(document, 0).Stream;
        Assert.Equal("FlateDecode", FilterOf(image));
        Assert.Equal(payload, image.RawBytes.ToArray());
        var decision = Decision(result, 10);
        Assert.Equal(ImageCompressionAction.KeptOriginal, decision.Action);
        Assert.Equal(decision.BytesBefore, decision.BytesAfter);
        var note = Assert.Single(result.Diagnostics, static d => d.Code == "PLUME6087");
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
    }

    public static TheoryData<string> SkipReasons()
    {
        var data = new TheoryData<string>();
        foreach (var reason in Enum.GetNames<ImageSkipReason>())
        {
            data.Add(reason);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SkipReasons))]
    public void EverySkipReason_SurfacesInTheResult(string reasonName)
    {
        var reason = Enum.Parse<ImageSkipReason>(reasonName);
        var options = PdfOptions.Default;
        var pdf = new ScanTestPdf();
        var subject = 10;
        switch (reason)
        {
            case ImageSkipReason.Bilevel:
                pdf = OneImage(Image(colorSpace: "/DeviceGray", bits: 1), new byte[Pixels * Pixels / 8]);
                break;
            case ImageSkipReason.StencilMask:
                pdf = OneImage("/Type /XObject /Subtype /Image /Width 300 /Height 300 /ImageMask true /BitsPerComponent 1", new byte[Pixels * Pixels / 8]);
                break;
            case ImageSkipReason.TilingPatternOnly:
                pdf = pdf.Stream(10, Image(), FlateSamples())
                    .Stream(11, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /XObject << /Im0 10 0 R >> >>", "q 10 0 0 10 0 0 cm /Im0 Do Q")
                    .Page(3, "/Pattern cs /P0 scn 0 0 100 100 re f", resources: "/Pattern << /P0 11 0 R >>");
                break;
            case ImageSkipReason.UnmeasuredPlacement:
                pdf = pdf.Stream(10, Image(), FlateSamples())
                    .Stream(11, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /XObject << /Im0 10 0 R >> >>", "q 10 0 0 10 0 0 cm /Im0 Do Q")
                    .Page(3, $"{Draw()} /Pattern cs /P0 scn 0 0 100 100 re f", xobjects: "/Im0 10 0 R", resources: "/Pattern << /P0 11 0 R >>");
                break;
            case ImageSkipReason.UnsupportedBitDepth:
                pdf = OneImage(Image(bits: 16), FlateSamples(channels: 6));
                break;
            case ImageSkipReason.UnsupportedColorSpace:
                pdf = OneImage(Image(colorSpace: "/DeviceCMYK"), FlateSamples(channels: 4));
                break;
            case ImageSkipReason.UnsupportedFilter:
                pdf = OneImage(Image(filter: "/JPXDecode"), new byte[64]);
                break;
            case ImageSkipReason.InlineImage:
                pdf = pdf.Page(3, "q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI Q");
                break;
            case ImageSkipReason.ColorKeyMask:
                pdf = OneImage(Image(extra: "/Mask [0 10 0 10 0 10]"), FlateSamples());
                break;
            case ImageSkipReason.SharedAsMask:
                pdf = pdf.Stream(10, Image(extra: "/SMask 11 0 R"), FlateSamples())
                    .Stream(11, Image(colorSpace: "/DeviceGray"), FlateSamples(channels: 1))
                    .Page(3, $"{Draw("A")} {Draw("M")}", xobjects: "/A 10 0 R /M 11 0 R");
                subject = 11;
                break;
            case ImageSkipReason.DecodeFailed:
                pdf = OneImage(Image(), [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4, 5, 6, 7, 8]);
                break;
            case ImageSkipReason.ResourceLimit:
                pdf = OneImage(Image(), FlateSamples());
                options = PdfOptions.Default with { MaxImagePixels = 1000 };
                break;
            case ImageSkipReason.NoPlacement:
                pdf = pdf.Stream(10, Image(), FlateSamples())
                    .Stream(11, Image(), FlateSamples(seed: 2))
                    .Page(3, Draw("A"), xobjects: "/A 11 0 R /B 10 0 R");
                break;
            case ImageSkipReason.MatteSoftMask:
                pdf = pdf.Stream(10, Image(extra: "/SMask 11 0 R"), FlateSamples())
                    .Stream(11, "/Type /XObject /Subtype /Image /Width 300 /Height 300 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Matte [0 0 0]", FlateSamples(channels: 1))
                    .Page(3, Draw(), xobjects: "/Im0 10 0 R");
                break;
            default:
                throw new InvalidOperationException($"No fixture for {reason}: add one.");
        }

        var (result, _) = Compress(pdf, AllLevers, options);

        var decision = reason == ImageSkipReason.InlineImage
            ? Assert.Single(result.Images)
            : Decision(result, subject);
        Assert.Equal(ImageCompressionAction.Skipped, decision.Action);
        Assert.Equal(reason, decision.SkipReason);
        if (reason == ImageSkipReason.InlineImage)
        {
            Assert.Null(decision.SourceReference);
            Assert.Equal(0, decision.PageIndex);
        }

        var code = reason == ImageSkipReason.DecodeFailed ? "PLUME6086" : "PLUME6085";
        var summary = Assert.Single(result.Diagnostics, d => d.Code == code && d.Message.Contains(reason.ToString(), StringComparison.Ordinal));
        Assert.Equal(reason == ImageSkipReason.DecodeFailed ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, summary.Severity);
    }

    [Fact]
    public void SkipSummary_CountsEveryImageWithTheReason_Once()
    {
        var bilevel = Image(colorSpace: "/DeviceGray", bits: 1);
        var pdf = new ScanTestPdf()
            .Stream(10, bilevel, new byte[Pixels * Pixels / 8])
            .Stream(11, bilevel, Enumerable.Repeat((byte)0x55, Pixels * Pixels / 8).ToArray())
            .Page(3, $"{Draw("A")} {Draw("B")}", xobjects: "/A 10 0 R /B 11 0 R");

        var (result, _) = Compress(pdf, AllLevers);

        var summary = Assert.Single(result.Diagnostics, static d => d.Code == "PLUME6085");
        Assert.Contains("2 image", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SoftMask_IsResizedWithItsParent_AndStaysFlate()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Image(extra: "/SMask 11 0 R"), FlateSamples())
            .Stream(11, Image(colorSpace: "/DeviceGray"), FlateSamples(channels: 1, seed: 3))
            .Page(3, Draw(), xobjects: "/Im0 10 0 R");

        var (result, output) = Compress(pdf, AllLevers);

        using var document = PdfDocument.Open(output);
        var image = PageImage(document, 0).Stream;
        var mask = (PdfStream)document.Objects[((PdfReference)image.Dictionary[PdfName.SMask]).Target];
        Assert.Equal(("FlateDecode", 100, 100), (FilterOf(mask), IntOf(mask, "Width"), IntOf(mask, "Height")));
        var maskDecision = Decision(result, 11);
        Assert.Equal(ImageCompressionAction.Downsampled, maskDecision.Action);
        Assert.Equal((100, 100), (maskDecision.WidthAfter, maskDecision.HeightAfter));
        Assert.Equal(ImageCompressionAction.DownsampledAndReencoded, Decision(result, 10).Action);
    }

    [Fact]
    public void UndecodableImage_UnderStrict_IsSkippedNotThrown()
    {
        var strict = PdfOptions.Default with { Strict = true };
        var payload = new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0x00, 0x01, 0x02, 0x03 };

        var (result, output) = Compress(OneImage(Image(), payload), AllLevers, strict);

        var decision = Decision(result, 10);
        Assert.Equal((ImageCompressionAction.Skipped, ImageSkipReason.DecodeFailed), (decision.Action, decision.SkipReason));
        Assert.Contains(result.Diagnostics, static d => d.Code == "PLUME6086" && d.Severity == DiagnosticSeverity.Warning);
        using var document = PdfDocument.Open(output);
        Assert.Equal(payload, PageImage(document, 0).Stream.RawBytes.ToArray());
    }

    [Fact]
    public void Deterministic_GivesByteIdenticalOutputAcrossRuns()
    {
        var pdf = new ScanTestPdf { MinimumStreamBytes = 2048 }
            .Stream(10, Image(extra: "/SMask 11 0 R"), FlateSamples())
            .Stream(11, Image(colorSpace: "/DeviceGray"), FlateSamples(channels: 1))
            .Stream(12, Image(filter: "/DCTDecode"), JpegSamples())
            .Stream(13, Image(filter: "/DCTDecode"), JpegSamples())
            .Page(3, $"{Draw("A")} {Draw("B")}", xobjects: "/A 10 0 R /B 12 0 R")
            .Page(5, Draw("B"), xobjects: "/B 13 0 R");
        var deterministic = PdfOptions.Default with { Deterministic = true };

        var (_, first) = Compress(pdf, AllLevers, deterministic);
        var (_, second) = Compress(pdf, AllLevers, deterministic);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Diagnostics_GoOnTheResult_NotOnTheDocument()
    {
        var source = Write(OneImage(Image(colorSpace: "/DeviceGray", bits: 1), new byte[Pixels * Pixels / 8]));
        var output = TempPath();
        try
        {
            using var document = PdfDocument.Open(source);

            var result = CompressionEngine.Run(document, source, output, AllLevers, PdfOptions.Default, CancellationToken.None);

            Assert.Contains(result.Diagnostics, static d => d.Code == "PLUME6085");
            Assert.DoesNotContain(document.Diagnostics, static d => d.Code.StartsWith("PLUME608", StringComparison.Ordinal) || d.Code == "PLUME6090");
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void Decisions_AreOrderedBySourceNumber_ThenInlineImages()
    {
        var pdf = new ScanTestPdf()
            .Stream(20, Image(), FlateSamples(seed: 2))
            .Stream(10, Image(), FlateSamples())
            .Page(3, $"{Draw("B")} BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI {Draw("A")}", xobjects: "/A 10 0 R /B 20 0 R");

        var (result, _) = Compress(pdf, AllLevers);

        Assert.Equal([10, 20, null], result.Images.Select(static i => i.SourceReference?.Number));
    }

    [Fact]
    public void ScanDiagnostics_AreOnTheResult()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Image(), FlateSamples())
            .Stream(11, "/Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /XObject << /Im0 10 0 R /Fm0 11 0 R >> >>", "/Im0 Do /Fm0 Do")
            .Page(3, "q 72 0 0 72 0 0 cm /Fm0 Do Q", xobjects: "/Fm0 11 0 R");

        var (result, _) = Compress(pdf, AllLevers);

        Assert.Contains(result.Diagnostics, static d => d.Code == "PLUME6025");
        Assert.Equal(ImageSkipReason.UnmeasuredPlacement, Decision(result, 10).SkipReason);
    }

    [Fact]
    public void FlateImageWithDecodeArray_ReencodedAsJpeg_RendersTheSame()
    {
        // An inverting /Decode on a Flate image: re-encoded as JPEG, the output must still
        // render as the source did, here and in any other conforming renderer.
        var pdf = OneImage(Image(extra: "/Decode [1 0 1 0 1 0]"), FlateSamples());
        var source = pdf.Build();

        var (result, output) = Compress(pdf, new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 90 } });

        Assert.Equal(ImageCompressionAction.Reencoded, Decision(result, 10).Action);
        Assert.True(MeanImageDifference(source, output) < 4, $"Mean difference {MeanImageDifference(source, output)} over the image.");
    }

    [Fact]
    public void SmallStream_WhoseFlateSavesLessThanTheFilterEntryAdds_StaysUnfiltered()
    {
        var content = $"{Draw()} q 1 0 0 1 9 9 cm /Im0 Do Q";
        var raw = System.Text.Encoding.Latin1.GetBytes(content);
        var flated = CompressionTestImages.Flate(raw);
        Assert.InRange(raw.Length - flated.Length, 1, " /Filter /FlateDecode".Length);

        var (_, output) = Compress(new ScanTestPdf().Stream(10, Image(), FlateSamples()).Page(3, content, xobjects: "/Im0 10 0 R"));

        using var document = PdfDocument.Open(output);
        var written = PageContent(document, 0);
        Assert.Null(FilterOf(written));
        Assert.Equal(raw, written.RawBytes.ToArray());
    }

    [Fact]
    public void JpegWrittenAtTheFallbackQuality_IsSummarizedWithACount()
    {
        // A JPEG behind Flate: its own quality can't be read from the raw payload, so a
        // downsample keeps it JPEG at the fallback quality.
        var pdf = new ScanTestPdf()
            .Stream(10, Image(filter: "[/FlateDecode /DCTDecode]"), CompressionTestImages.Flate(JpegSamples(quality: 75)))
            .Stream(11, Image(filter: "[/FlateDecode /DCTDecode]"), CompressionTestImages.Flate(JpegSamples(quality: 70)))
            .Stream(12, Image(filter: "/DCTDecode"), JpegSamples(quality: 75))
            .Page(3, $"{Draw("A")} {Draw("B")} {Draw("C")}", xobjects: "/A 10 0 R /B 11 0 R /C 12 0 R");

        var (result, _) = Compress(pdf, new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 100 } });

        Assert.Equal(ImageCompressionAction.Downsampled, Decision(result, 10).Action);
        Assert.Null(Decision(result, 10).EstimatedSourceQuality);
        var note = Assert.Single(result.Diagnostics, static d => d.Code == "PLUME6091");
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Contains("2 JPEG image", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JpegQualityRequested_NeverReportsTheFallback()
    {
        var pdf = OneImage(Image(filter: "[/FlateDecode /DCTDecode]"), CompressionTestImages.Flate(JpegSamples(quality: 75)));

        var (result, _) = Compress(pdf, AllLevers);

        Assert.Equal(ImageCompressionAction.DownsampledAndReencoded, Decision(result, 10).Action);
        Assert.DoesNotContain(result.Diagnostics, static d => d.Code == "PLUME6091");
    }

    // The mean absolute per-byte difference between two one-page files' renderings, over the
    // 72-point square the image is drawn on (the rest of the page is white in both).
    private static double MeanImageDifference(byte[] first, byte[] second)
    {
        static RasterImageFrame Render(byte[] pdf)
        {
            using var document = PdfDocument.Open(pdf);
            return document.Pages[0].Rasterize(new PdfRasterizeOptions { Dpi = 144 }).Frames[0];
        }

        var a = Render(first);
        var b = Render(second);
        var x = a.Pixels.Span;
        var y = b.Pixels.Span;
        long total = 0;
        for (var i = 0; i < x.Length; i++)
        {
            total += Math.Abs(x[i] - y[i]);
        }

        var side = Edge * 2;
        return (double)total / (side * side * RasterImageFrame.BytesPerPixel(a.Format));
    }

    [Fact]
    public void XmpMetadataStream_IsNeverFlated()
    {
        var source = TempPath("source");
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(OneImage(Image(), FlateSamples()).Build()))
            {
                document.SetXmpMetadata(new XmpPacket { Title = "Report" });
                document.Save(source);
            }

            Pdf.Compress(source, output);

            using var compressed = PdfDocument.Open(output);
            var metadata = (PdfStream)compressed.Objects[((PdfReference)compressed.Catalog!.Dictionary[PdfName.Get("Metadata")]).Target];
            Assert.Null(FilterOf(metadata));
        }
        finally
        {
            Delete(source, output);
        }
    }
}
