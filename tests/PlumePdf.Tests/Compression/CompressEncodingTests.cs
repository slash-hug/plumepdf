using System.Text;
using PlumePdf.Documents.Compression;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <see cref="ImageCompressionDecision.EncodingBefore"/> and <see cref="ImageCompressionDecision.EncodingAfter"/>
/// end to end: for every <see cref="ImageCompressionAction"/>, the families reported match the
/// <c>/Filter</c> of the stream in the input and of the one actually written to the output.
/// </summary>
public class CompressEncodingTests
{
    public static TheoryData<string> Actions()
    {
        var data = new TheoryData<string>();
        foreach (var action in Enum.GetNames<ImageCompressionAction>())
        {
            data.Add(action);
        }

        return data;
    }

    // One fixture per action; the subject is the image drawn as /Im0 (object 10 unless noted).
    [Theory]
    [MemberData(nameof(Actions))]
    public void EveryAction_ReportsTheFamiliesReadAndWritten(string actionName)
    {
        var action = Enum.Parse<ImageCompressionAction>(actionName);
        PdfCompressOptions? options = AllLevers;
        var subject = 10;
        ScanTestPdf pdf;
        ImageEncoding before;
        ImageEncoding after;
        string? writtenFilter;
        switch (action)
        {
            case ImageCompressionAction.Unchanged:
                pdf = OneImage(Image(), FlateSamples());
                options = null;
                (before, after, writtenFilter) = (ImageEncoding.Flate, ImageEncoding.Flate, "FlateDecode");
                break;
            case ImageCompressionAction.Skipped:
                pdf = OneImage(Image(filter: "/JPXDecode"), new byte[64]);
                (before, after, writtenFilter) = (ImageEncoding.Jpeg2000, ImageEncoding.Jpeg2000, "JPXDecode");
                break;
            case ImageCompressionAction.Downsampled:
                pdf = OneImage(Image(filter: "/DCTDecode"), JpegSamples());
                options = new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 100 } };
                (before, after, writtenFilter) = (ImageEncoding.Jpeg, ImageEncoding.Jpeg, "DCTDecode");
                break;
            case ImageCompressionAction.Reencoded:
                pdf = OneImage(Image(), FlateSamples());
                options = new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 60 } };
                (before, after, writtenFilter) = (ImageEncoding.Flate, ImageEncoding.Jpeg, "DCTDecode");
                break;
            case ImageCompressionAction.DownsampledAndReencoded:
                pdf = OneImage(Image(), FlateSamples());
                (before, after, writtenFilter) = (ImageEncoding.Flate, ImageEncoding.Jpeg, "DCTDecode");
                break;
            case ImageCompressionAction.KeptOriginal:
                pdf = OneImage(Image(), CompressionTestImages.Flate(Flat()));
                options = new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 95 } };
                (before, after, writtenFilter) = (ImageEncoding.Flate, ImageEncoding.Flate, "FlateDecode");
                break;
            case ImageCompressionAction.Deduplicated:
                // The canonical copy (10) is re-encoded; the duplicate (12) is written as it.
                pdf = new ScanTestPdf()
                    .Stream(10, Image(), FlateSamples())
                    .Stream(12, Image(), FlateSamples())
                    .Page(3, Draw(), xobjects: "/Im0 12 0 R")
                    .Page(5, Draw(), xobjects: "/Im0 10 0 R");
                options = new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 60 } };
                subject = 12;
                (before, after, writtenFilter) = (ImageEncoding.Flate, ImageEncoding.Jpeg, "DCTDecode");
                break;
            default:
                throw new InvalidOperationException($"No fixture for {action}: add one.");
        }

        var (result, output) = Compress(pdf, options);

        var decision = Decision(result, subject);
        Assert.Equal(action, decision.Action);
        Assert.Equal((before, after), (decision.EncodingBefore, decision.EncodingAfter));
        using var document = PdfDocument.Open(output);
        var written = PageImage(document, 0).Stream;
        Assert.Equal(writtenFilter, FilterOf(written));
        Assert.Equal(after, ImageEncodings.OfStream(written.Dictionary, (IObjectSource)document.Objects));
        if (action == ImageCompressionAction.Deduplicated)
        {
            Assert.Equal(decision.EncodingAfter, Decision(result, 10).EncodingAfter);
        }
    }

    [Fact]
    public void UnfilteredImage_LeftUnchanged_IsReportedFlatedByTheLosslessLever()
    {
        var (result, output) = Compress(OneImage(Image(filter: null), Flat()));

        var decision = Decision(result, 10);
        Assert.Equal(ImageCompressionAction.Unchanged, decision.Action);
        Assert.Equal((ImageEncoding.Uncompressed, ImageEncoding.Flate), (decision.EncodingBefore, decision.EncodingAfter));
        using var document = PdfDocument.Open(output);
        Assert.Equal("FlateDecode", FilterOf(PageImage(document, 0).Stream));
    }

    [Fact]
    public void UnfilteredImage_WithTheLosslessLeverOff_StaysUncompressed()
    {
        var (result, output) = Compress(OneImage(Image(filter: null), Flat()), new PdfCompressOptions { Lossless = false });

        var decision = Decision(result, 10);
        Assert.Equal((ImageEncoding.Uncompressed, ImageEncoding.Uncompressed), (decision.EncodingBefore, decision.EncodingAfter));
        using var document = PdfDocument.Open(output);
        Assert.Null(FilterOf(PageImage(document, 0).Stream));
    }

    [Fact]
    public void SkippedUnfilteredImage_IsReportedFlatedByTheLosslessLever()
    {
        var (result, output) = Compress(OneImage(Image(colorSpace: "/DeviceGray", bits: 1, filter: null), new byte[Pixels * Pixels / 8]), AllLevers);

        var decision = Decision(result, 10);
        Assert.Equal((ImageCompressionAction.Skipped, ImageSkipReason.Bilevel), (decision.Action, decision.SkipReason));
        Assert.Equal((ImageEncoding.Uncompressed, ImageEncoding.Flate), (decision.EncodingBefore, decision.EncodingAfter));
        using var document = PdfDocument.Open(output);
        Assert.Equal("FlateDecode", FilterOf(PageImage(document, 0).Stream));
    }

    [Fact]
    public void UnfilteredImage_Reencoded_IsReportedJpeg()
    {
        var (result, output) = Compress(OneImage(Image(filter: null), CompressionTestImages.Samples(Pixels, Pixels, 3)), new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 60 } });

        var decision = Decision(result, 10);
        Assert.Equal(ImageCompressionAction.Reencoded, decision.Action);
        Assert.Equal((ImageEncoding.Uncompressed, ImageEncoding.Jpeg), (decision.EncodingBefore, decision.EncodingAfter));
        using var document = PdfDocument.Open(output);
        Assert.Equal("DCTDecode", FilterOf(PageImage(document, 0).Stream));
    }

    [Fact]
    public void AsciiWrappedJpeg_IsReportedJpeg_BeforeAndAfter()
    {
        var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(JpegSamples()) + ">");
        var (result, output) = Compress(OneImage(Image(filter: "[/ASCIIHexDecode /DCTDecode]"), hex));

        var decision = Decision(result, 10);
        Assert.Equal(ImageCompressionAction.Unchanged, decision.Action);
        Assert.Equal((ImageEncoding.Jpeg, ImageEncoding.Jpeg), (decision.EncodingBefore, decision.EncodingAfter));
        using var document = PdfDocument.Open(output);
        var written = PageImage(document, 0).Stream;
        Assert.Equal(["ASCIIHexDecode", "DCTDecode"], ((PdfArray)written.Dictionary[PdfName.Filter]!).Select(static f => ((PdfName)f).Value));
    }

    [Fact]
    public void IndirectFilter_IsFollowed()
    {
        var pdf = new ScanTestPdf()
            .Object(20, "/DCTDecode")
            .Stream(10, Image(filter: "20 0 R"), JpegSamples())
            .Page(3, Draw(), xobjects: "/Im0 10 0 R");

        var (result, _) = Compress(pdf);

        var decision = Decision(result, 10);
        Assert.Equal((ImageEncoding.Jpeg, ImageEncoding.Jpeg), (decision.EncodingBefore, decision.EncodingAfter));
    }

    [Fact]
    public void SoftMask_ReportsItsOwnFamilies()
    {
        var pdf = new ScanTestPdf()
            .Stream(10, Image(extra: "/SMask 11 0 R"), FlateSamples())
            .Stream(11, Image(colorSpace: "/DeviceGray", filter: null), CompressionTestImages.Samples(Pixels, Pixels, 1))
            .Page(3, Draw(), xobjects: "/Im0 10 0 R");

        var (result, output) = Compress(pdf, AllLevers);

        var parent = Decision(result, 10);
        Assert.Equal((ImageEncoding.Flate, ImageEncoding.Jpeg), (parent.EncodingBefore, parent.EncodingAfter));
        var mask = Decision(result, 11);
        Assert.Equal(ImageEncoding.Uncompressed, mask.EncodingBefore);
        using var document = PdfDocument.Open(output);
        var image = PageImage(document, 0).Stream;
        var writtenMask = (PdfStream)document.Objects[((PdfReference)image.Dictionary[PdfName.Get("SMask")]!).Target];
        Assert.Equal(ImageEncodings.OfStream(writtenMask.Dictionary, source: null), mask.EncodingAfter);
        Assert.Equal("FlateDecode", FilterOf(writtenMask));
    }

    [Fact]
    public void InlineImages_ReportTheirOwnFamily_BeforeAndAfter()
    {
        var pdf = new ScanTestPdf()
            .Page(3, "BI /W 1 /H 1 /CS /G /BPC 8 /F [/A85 /DCT] ID \u0001 EI BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI BI /W 1 /H 1 /CS /G /BPC 8 /Filter /Fl ID \u0001 EI");

        var (result, _) = Compress(pdf, AllLevers);

        Assert.Equal(
            [(ImageEncoding.Jpeg, ImageEncoding.Jpeg), (ImageEncoding.Uncompressed, ImageEncoding.Uncompressed), (ImageEncoding.Flate, ImageEncoding.Flate)],
            result.Images.Select(static i => (i.EncodingBefore, i.EncodingAfter)));
    }

    [Fact]
    public void InlineImagesInATilingPattern_KeepTheirFamily_OnEveryPageThatUsesIt()
    {
        var pdf = new ScanTestPdf()
            .Stream(11, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10", "q 10 0 0 10 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 /F /CCF ID \u0001 EI Q")
            .Page(3, "/Pattern cs /P0 scn 0 0 100 100 re f", resources: "/Pattern << /P0 11 0 R >>")
            .Page(5, "/Pattern cs /P0 scn 0 0 100 100 re f", resources: "/Pattern << /P0 11 0 R >>");

        var (result, _) = Compress(pdf, AllLevers);

        Assert.Equal([(0, ImageEncoding.Ccitt), (1, ImageEncoding.Ccitt)], result.Images.Select(static i => (i.PageIndex!.Value, i.EncodingBefore)));
    }

    // A flat colour: Flates to almost nothing, and no JPEG of it is smaller.
    private static byte[] Flat()
    {
        var flat = new byte[Pixels * Pixels * 3];
        Array.Fill(flat, (byte)128);
        return flat;
    }
}
