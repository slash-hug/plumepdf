using System.Text;
using PlumePdf.Documents.Compression;
using PlumePdf.Filters;
using PlumePdf.Filters.Imaging;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary><see cref="ImageReencoder"/>: resizing and re-encoding one image, with the family rule and the never-larger rule.</summary>
public class ImageReencoderTests
{
    private static ImageReencodeResult Reencode(PdfStream image, int targetWidth, int targetHeight, int? jpegQuality = null, bool softMask = false, ObjectRegistry? objects = null, PdfOptions? options = null) =>
        ImageReencoder.Reencode(image, objects ?? Registry(), new ImageReencodeRequest(targetWidth, targetHeight, jpegQuality, softMask), options ?? PdfOptions.Default);

    private static string FilterOf(PdfStream stream) => ((PdfName)stream.Dictionary[PdfName.Filter]).Value;

    private static int IntOf(PdfStream stream, string key) => ((PdfNumber)stream.Dictionary[PdfName.Get(key)]).ToInt32();

    private static string Serialize(PdfObject value)
    {
        using var output = new MemoryStream();
        ObjectSerializer.WriteValue(output, value);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Flate_Downsampled_StaysFlateWithTheResamplersSamples(int channels)
    {
        var original = Samples(120, 80, channels);
        var result = Reencode(FlateImage(original, 120, 80, channels), 60, 40);

        Assert.Equal(ImageReencodeOutcome.Downsampled, result.Outcome);
        Assert.Equal("FlateDecode", FilterOf(result.Stream));
        Assert.Equal((60, 40), (IntOf(result.Stream, "Width"), IntOf(result.Stream, "Height")));
        Assert.Equal((60, 40), (result.Width, result.Height));
        Assert.Equal(BoxResampler.Resample(original, 120, 80, channels, 60, 40), result.Stream.GetDecodedBytes(PdfFilterRegistry.Default));
        Assert.Null(result.EstimatedSourceQuality);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Flate_WithJpegQuality_BecomesJpegAtThatQuality(int channels)
    {
        var result = Reencode(FlateImage(Samples(120, 80, channels), 120, 80, channels), 60, 40, jpegQuality: 70);

        Assert.Equal(ImageReencodeOutcome.DownsampledAndReencoded, result.Outcome);
        Assert.Equal("DCTDecode", FilterOf(result.Stream));
        var decoded = JpegDecoder.Decode(result.Stream.RawBytes, PdfOptions.Default, null, null);
        Assert.Equal((60, 40, channels), (decoded.Width, decoded.Height, decoded.ComponentCount));
        Assert.True(JpegQualityEstimator.TryEstimate(result.Stream.RawBytes.Span, out var quality));
        Assert.Equal(70, quality);
    }

    [Fact]
    public void Flate_WithJpegQualityOnly_IsReencodedAtItsOwnSize()
    {
        var result = Reencode(FlateImage(Samples(64, 48, 3), 64, 48, 3), 64, 48, jpegQuality: 60);

        Assert.Equal(ImageReencodeOutcome.Reencoded, result.Outcome);
        Assert.Equal("DCTDecode", FilterOf(result.Stream));
        Assert.Equal((64, 48), (IntOf(result.Stream, "Width"), IntOf(result.Stream, "Height")));
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(30, 50)]
    [InlineData(98, 95)]
    public void Jpeg_Downsampled_StaysJpegAtItsEstimatedQualityClampedTo50Through95(int sourceQuality, int expectedQuality)
    {
        var result = Reencode(DctImage(Samples(128, 96, 3), 128, 96, 3, sourceQuality), 64, 48);

        Assert.Equal(ImageReencodeOutcome.Downsampled, result.Outcome);
        Assert.Equal("DCTDecode", FilterOf(result.Stream));
        Assert.Equal(expectedQuality, result.EstimatedSourceQuality);
        Assert.False(result.UsedFallbackQuality);
        Assert.True(JpegQualityEstimator.TryEstimate(result.Stream.RawBytes.Span, out var quality));
        Assert.Equal(expectedQuality, quality);
    }

    [Fact]
    public void Jpeg_WithNonIjgTables_FallsBackTo85()
    {
        var image = DctImage(Samples(128, 96, 1), 128, 96, 1, 75);
        var jpeg = image.RawBytes.ToArray();
        var dqt = IndexOfMarker(jpeg, 0xDB);
        for (var i = 0; i < 64; i++)
        {
            jpeg[dqt + 5 + i] = 7; // A flat table no IJG quality produces; still a valid JPEG.
        }

        var result = Reencode(new PdfStream(image.Dictionary, jpeg), 64, 48);

        Assert.Equal(ImageReencodeOutcome.Downsampled, result.Outcome);
        Assert.Null(result.EstimatedSourceQuality);
        Assert.True(result.UsedFallbackQuality);
        Assert.True(JpegQualityEstimator.TryEstimate(result.Stream.RawBytes.Span, out var quality));
        Assert.Equal(ImageReencoder.FallbackJpegQuality, quality);
    }

    [Fact]
    public void Jpeg_WithJpegQualityOnly_KeepsItsDimensions()
    {
        var result = Reencode(DctImage(Samples(96, 64, 3), 96, 64, 3, 95), 96, 64, jpegQuality: 40);

        Assert.Equal(ImageReencodeOutcome.Reencoded, result.Outcome);
        Assert.Equal(95, result.EstimatedSourceQuality);
        var decoded = JpegDecoder.Decode(result.Stream.RawBytes, PdfOptions.Default, null, null);
        Assert.Equal((96, 64), (decoded.Width, decoded.Height));
        Assert.True(JpegQualityEstimator.TryEstimate(result.Stream.RawBytes.Span, out var quality));
        Assert.Equal(40, quality);
    }

    [Fact]
    public void NeverLarger_AnAlreadyTinyJpeg_IsKeptAsIs()
    {
        var image = DctImage(Samples(64, 64, 3), 64, 64, 3, quality: 5);
        var result = Reencode(image, 64, 64, jpegQuality: 95);

        Assert.Equal(ImageReencodeOutcome.KeptOriginal, result.Outcome);
        Assert.Same(image, result.Stream);
        Assert.Equal((64, 64), (result.Width, result.Height));
    }

    [Fact]
    public void NothingToDo_IsUnchangedWithoutDecoding()
    {
        // Undecodable payload: an untouched image must not even be decoded.
        var dictionary = ImageDictionary(32, 32, ColorSpaceFor(3));
        dictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        var image = new PdfStream(dictionary, new byte[] { 1, 2, 3 });

        var result = Reencode(image, 32, 32);

        Assert.Equal(ImageReencodeOutcome.Unchanged, result.Outcome);
        Assert.Same(image, result.Stream);
    }

    [Fact]
    public void LargerTarget_IsNeverUpsampled()
    {
        var image = FlateImage(Samples(40, 30, 1), 40, 30, 1);
        Assert.Equal(ImageReencodeOutcome.Unchanged, Reencode(image, 80, 60).Outcome);

        var oneAxis = Reencode(image, 20, 60);
        Assert.Equal(ImageReencodeOutcome.Downsampled, oneAxis.Outcome);
        Assert.Equal((20, 30), (oneAxis.Width, oneAxis.Height));
    }

    [Fact]
    public void ReencodedDictionary_KeepsEveryInterpretationEntry()
    {
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, PdfName.Get("XObject"));
        dictionary.Set(PdfName.Subtype, PdfName.Image);
        dictionary.Set(PdfName.Get("Width"), PdfNumber.Get(64));
        dictionary.Set(PdfName.Get("Height"), PdfNumber.Get(32));
        dictionary.Set(PdfName.ColorSpace, new PdfArray([PdfName.Get("ICCBased"), new PdfReference(new IndirectReference(5, 0))]));
        dictionary.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));
        dictionary.Set(PdfName.Get("Intent"), PdfName.Get("Perceptual"));
        dictionary.Set(PdfName.Interpolate, PdfBoolean.True);
        dictionary.Set(PdfName.SMask, new PdfReference(new IndirectReference(6, 0)));
        dictionary.Set(PdfName.Get("StructParent"), PdfNumber.Get(3));
        dictionary.Set(PdfName.Get("OC"), new PdfReference(new IndirectReference(7, 0)));
        dictionary.Set(PdfName.Length, new PdfReference(new IndirectReference(8, 0)));
        dictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        dictionary.Set(PdfName.DecodeParms, PredictorParms(15, 64, 3));
        var original = Samples(64, 32, 3);
        var image = new PdfStream(dictionary, Flate(PngPredict(original, 64, 32, 3)));
        var profileDictionary = new PdfDictionary();
        profileDictionary.Set(PdfName.N, PdfNumber.Get(3));
        var objects = Registry((5, new PdfStream(profileDictionary, new byte[16])));

        var result = Reencode(image, 32, 16, jpegQuality: 80, objects: objects);

        Assert.Equal(ImageReencodeOutcome.DownsampledAndReencoded, result.Outcome);
        Assert.Equal(
            "<< /Type /XObject /Subtype /Image /Width 32 /Height 16 /ColorSpace [/ICCBased 5 0 R] /BitsPerComponent 8 "
            + "/Intent /Perceptual /Interpolate true /SMask 6 0 R /StructParent 3 /OC 7 0 R /Filter /DCTDecode >>",
            Serialize(result.Stream.Dictionary));

        // The JFIF header records aspect ratio only (units 0 at byte 13: SOI, APP0 marker and
        // length, "JFIF\0", version), so it can't contradict the page's placement.
        var jpeg = result.Stream.RawBytes.Span;
        Assert.True(jpeg[6..11].SequenceEqual("JFIF\0"u8));
        Assert.Equal(0, jpeg[13]);
    }

    private static PdfArray DecodeArray(params int[] values) => new([.. values.Select(static v => (PdfObject)PdfNumber.Get(v))]);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void FlateWithDecodeArray_ReencodedAsJpeg_BakesItIntoTheSamples(int channels)
    {
        // A JPEG's /Decode is not applied by every renderer, so a non-JPEG source's array is
        // applied to the samples before encoding and dropped from the dictionary.
        var original = Samples(64, 48, channels);
        var image = FlateImage(original, 64, 48, channels);
        image.Dictionary.Set(PdfName.Decode, DecodeArray([.. Enumerable.Repeat(new[] { 1, 0 }, channels).SelectMany(static p => p)]));

        var result = Reencode(image, 64, 48, jpegQuality: 95);

        Assert.Equal(ImageReencodeOutcome.Reencoded, result.Outcome);
        Assert.False(result.Stream.Dictionary.ContainsKey(PdfName.Decode));
        var inverted = original.Select(static v => (byte)(255 - v)).ToArray();
        Assert.Equal(JpegEncoder.Encode(inverted, 64, 48, channels, 95), result.Stream.RawBytes.ToArray());
    }

    [Fact]
    public void FlateWithIndirectDecodeArray_ReencodedAsJpeg_BakesIt()
    {
        var original = Samples(32, 32, 1);
        var image = FlateImage(original, 32, 32, 1);
        image.Dictionary.Set(PdfName.Decode, new PdfReference(new IndirectReference(5, 0)));

        var result = Reencode(image, 32, 32, jpegQuality: 95, objects: Registry((5, DecodeArray(1, 0))));

        Assert.False(result.Stream.Dictionary.ContainsKey(PdfName.Decode));
        var inverted = original.Select(static v => (byte)(255 - v)).ToArray();
        Assert.Equal(JpegEncoder.Encode(inverted, 32, 32, 1, 95), result.Stream.RawBytes.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeArray_StaysOnTheDictionary_WhenTheFamilyIsKept(bool jpegSource)
    {
        // Flate stays Flate and JPEG stays JPEG: the array means what it meant before.
        var samples = Samples(64, 48, 3);
        var image = jpegSource ? DctImage(samples, 64, 48, 3, 80) : FlateImage(samples, 64, 48, 3);
        image.Dictionary.Set(PdfName.Decode, DecodeArray(1, 0, 1, 0, 1, 0));

        var result = Reencode(image, 32, 24, jpegQuality: jpegSource ? 70 : null);

        Assert.Equal(jpegSource ? ImageReencodeOutcome.DownsampledAndReencoded : ImageReencodeOutcome.Downsampled, result.Outcome);
        Assert.Equal("[1 0 1 0 1 0]", Serialize(result.Stream.Dictionary[PdfName.Decode]));
        if (!jpegSource)
        {
            Assert.Equal(BoxResampler.Resample(samples, 64, 48, 3, 32, 24), result.Stream.GetDecodedBytes(PdfFilterRegistry.Default));
        }
    }

    [Fact]
    public void UnfilteredSource_GainsAFilterEntry()
    {
        var image = new PdfStream(ImageDictionary(40, 40, ColorSpaceFor(1)), Samples(40, 40, 1));
        var result = Reencode(image, 20, 20);

        Assert.Equal(ImageReencodeOutcome.Downsampled, result.Outcome);
        Assert.Equal("FlateDecode", FilterOf(result.Stream));
        Assert.False(result.Stream.Dictionary.ContainsKey(PdfName.DecodeParms));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoftMask_IsResizedAndFlatedEvenWhenJpegIsRequested(bool jpegSource)
    {
        var original = Samples(80, 60, 1);
        var mask = jpegSource ? DctImage(original, 80, 60, 1, 90) : FlateImage(original, 80, 60, 1);
        mask.Dictionary.Remove(PdfName.ColorSpace);
        if (!jpegSource)
        {
            mask.Dictionary.Set(PdfName.ColorSpace, PdfName.Get("DeviceGray"));
        }

        var result = Reencode(mask, 40, 30, jpegQuality: 50, softMask: true);

        Assert.Equal(ImageReencodeOutcome.Downsampled, result.Outcome);
        Assert.Equal("FlateDecode", FilterOf(result.Stream));
        Assert.Equal((40, 30), (IntOf(result.Stream, "Width"), IntOf(result.Stream, "Height")));
        Assert.Equal(40 * 30, result.Stream.GetDecodedBytes(PdfFilterRegistry.Default).Length);
    }

    [Fact]
    public void SoftMask_AtItsOwnSize_IsUnchangedEvenWithJpegQuality()
    {
        var mask = FlateImage(Samples(20, 20, 1), 20, 20, 1);
        Assert.Equal(ImageReencodeOutcome.Unchanged, Reencode(mask, 20, 20, jpegQuality: 50, softMask: true).Outcome);
    }

    [Fact]
    public void MattePair_KeepsBothOriginalsSoTheirSizesStayEqual()
    {
        // A /Matte soft mask must match its parent's size; neither side is resized alone.
        var mask = FlateImage(Samples(64, 64, 1), 64, 64, 1);
        mask.Dictionary.Set(PdfName.Get("Matte"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(0)]));
        var parent = FlateImage(Samples(64, 64, 3), 64, 64, 3);
        parent.Dictionary.Set(PdfName.SMask, new PdfReference(new IndirectReference(20, 0)));
        var objects = Registry((20, mask));

        var parentResult = Reencode(parent, 32, 32, objects: objects);
        var maskResult = Reencode(mask, 32, 32, softMask: true, objects: objects);

        Assert.Equal((ImageReencodeOutcome.Skipped, PlannedSkip.MatteSoftMask), (parentResult.Outcome, parentResult.Skip));
        Assert.Equal((ImageReencodeOutcome.Skipped, PlannedSkip.MatteSoftMask), (maskResult.Outcome, maskResult.Skip));
        Assert.Same(parent, parentResult.Stream);
        Assert.Same(mask, maskResult.Stream);
    }

    [Fact]
    public void IneligibleImage_IsSkippedWithItsReason()
    {
        var image = new PdfStream(ImageDictionary(64, 64, ColorSpaceFor(1), bitsPerComponent: 1), new byte[64 * 8]);
        var result = Reencode(image, 32, 32);

        Assert.Equal(ImageReencodeOutcome.Skipped, result.Outcome);
        Assert.Equal(PlannedSkip.Bilevel, result.Skip);
        Assert.Same(image, result.Stream);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void DecodeFailureUnderStrict_IsSkippedNotThrown()
    {
        var image = DctImage(Samples(64, 64, 3), 64, 64, 3);
        var truncated = new PdfStream(image.Dictionary, image.RawBytes[..(image.RawBytes.Length / 2)]);

        var result = Reencode(truncated, 32, 32, options: PdfOptions.Default with { Strict = true });

        Assert.Equal(ImageReencodeOutcome.Skipped, result.Outcome);
        Assert.Equal(PlannedSkip.DecodeFailed, result.Skip);
        Assert.Same(truncated, result.Stream);
    }

    [Fact]
    public void SameInput_ProducesIdenticalBytes()
    {
        var image = FlateImage(Samples(90, 70, 3), 90, 70, 3);
        var first = Reencode(image, 45, 35, jpegQuality: 75);
        var second = Reencode(image, 45, 35, jpegQuality: 75);
        Assert.Equal(first.Stream.RawBytes.ToArray(), second.Stream.RawBytes.ToArray());
    }

    private static int IndexOfMarker(byte[] jpeg, byte marker)
    {
        for (var i = 2; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == marker)
            {
                return i;
            }
        }

        throw new InvalidOperationException("marker not found");
    }
}
