using PlumePdf.Documents.Compression;
using PlumePdf.Filters;
using Xunit;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary><see cref="NativeImageSamples"/>: decoding an image XObject to its own 8-bit samples, or saying why compression leaves it alone.</summary>
public class NativeImageSamplesTests
{
    private static (bool Ok, NativeSamples? Samples, PlannedSkip Skip, string? Detail) Decode(PdfStream image, ObjectRegistry? objects = null, PdfOptions? options = null, bool softMask = false)
    {
        var ok = NativeImageSamples.TryDecode(image, objects ?? Registry(), options ?? PdfOptions.Default, out var samples, out var skip, out var detail, softMask);
        return (ok, samples, skip, detail);
    }

    private static void AssertSkip(PdfStream image, PlannedSkip expected, ObjectRegistry? objects = null, PdfOptions? options = null)
    {
        var (ok, samples, skip, detail) = Decode(image, objects, options);
        Assert.False(ok);
        Assert.Null(samples);
        Assert.Equal(expected, skip);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Flate_DecodesToTheOriginalSamples(int channels)
    {
        var original = Samples(21, 13, channels);
        var (ok, samples, skip, detail) = Decode(FlateImage(original, 21, 13, channels));

        Assert.True(ok, detail);
        Assert.Equal(PlannedSkip.None, skip);
        Assert.Equal((21, 13, channels), (samples!.Width, samples.Height, samples.Channels));
        Assert.Equal(original, samples.Samples);
    }

    // A soft mask with /Matte must keep its parent's /Width and /Height; separate
    // never-larger decisions for the two could resize one and not the other. The image's
    // own /Matte marks it as such a mask however the caller offered it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SoftMaskWithMatte_IsSkipped(bool offeredAsSoftMask)
    {
        var dictionary = ImageDictionary(9, 9, ColorSpaceFor(1));
        dictionary.Set(PdfName.Get("Matte"), new PdfArray([PdfNumber.Get(1), PdfNumber.Get(1), PdfNumber.Get(1)]));
        var (ok, samples, skip, detail) = Decode(new PdfStream(dictionary, Samples(9, 9, 1)), softMask: offeredAsSoftMask);

        Assert.False(ok);
        Assert.Null(samples);
        Assert.Equal(PlannedSkip.MatteSoftMask, skip);
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImageWhoseSoftMaskHasMatte_IsSkipped(bool matte)
    {
        var original = Samples(9, 9, 3);
        var mask = ImageDictionary(9, 9, ColorSpaceFor(1));
        if (matte)
        {
            mask.Set(PdfName.Get("Matte"), new PdfArray([PdfNumber.Get(1), PdfNumber.Get(1), PdfNumber.Get(1)]));
        }

        var dictionary = ImageDictionary(9, 9, ColorSpaceFor(3));
        dictionary.Set(PdfName.SMask, new PdfReference(new IndirectReference(20, 0)));
        var objects = Registry((20, new PdfStream(mask, Samples(9, 9, 1))));

        var (ok, samples, skip, detail) = Decode(new PdfStream(dictionary, original), objects);

        if (matte)
        {
            Assert.False(ok);
            Assert.Equal(PlannedSkip.MatteSoftMask, skip);
        }
        else
        {
            Assert.True(ok, detail);
            Assert.Equal(original, samples!.Samples);
        }
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(3, 15)]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    public void FlateWithPredictor_UndoesThePrediction(int channels, int predictor)
    {
        var original = Samples(19, 11, channels);
        var predicted = predictor == 15 ? PngPredict(original, 19, 11, channels) : TiffPredict(original, 19, 11, channels);
        var dictionary = ImageDictionary(19, 11, ColorSpaceFor(channels));
        dictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        dictionary.Set(PdfName.DecodeParms, PredictorParms(predictor, 19, channels));

        var (ok, samples, _, detail) = Decode(new PdfStream(dictionary, Flate(predicted)));

        Assert.True(ok, detail);
        Assert.Equal(original, samples!.Samples);
    }

    [Fact]
    public void Unfiltered_IsReadAsIs()
    {
        var original = Samples(8, 8, 3);
        var (ok, samples, _, detail) = Decode(new PdfStream(ImageDictionary(8, 8, ColorSpaceFor(3)), original));
        Assert.True(ok, detail);
        Assert.Equal(original, samples!.Samples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Dct_DecodesToTheJpegDecodersSamples(int channels)
    {
        var image = DctImage(Samples(40, 24, channels), 40, 24, channels);
        var expected = JpegDecoder.Decode(image.RawBytes, PdfOptions.Default, null, null).Pixels;

        var (ok, samples, _, detail) = Decode(image);

        Assert.True(ok, detail);
        Assert.Equal((40, 24, channels), (samples!.Width, samples.Height, samples.Channels));
        Assert.Equal(expected, samples.Samples);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void IccBased_DecodesNativeSamplesAndLeavesTheProfileAlone(int components)
    {
        var profileDictionary = new PdfDictionary();
        profileDictionary.Set(PdfName.N, PdfNumber.Get(components));
        var profile = new PdfStream(profileDictionary, new byte[] { 1, 2, 3, 4 });
        var objects = Registry((5, profile));

        var original = Samples(10, 6, components);
        var colorSpace = new PdfArray([PdfName.Get("ICCBased"), new PdfReference(new IndirectReference(5, 0))]);
        var dictionary = ImageDictionary(10, 6, colorSpace);
        dictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        var image = new PdfStream(dictionary, Flate(original));

        var (ok, samples, _, detail) = Decode(image, objects);

        Assert.True(ok, detail);
        Assert.Equal(components, samples!.Channels);
        Assert.Equal(original, samples.Samples);
        Assert.Same(colorSpace, image.Dictionary[PdfName.ColorSpace]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, profile.RawBytes.ToArray());
    }

    [Fact]
    public void IndirectEntries_AreResolvedBeforeTheyAreRead()
    {
        // Width, Height, BitsPerComponent, ColorSpace and Filter all behind references.
        var objects = Registry(
            (10, PdfNumber.Get(12)),
            (11, PdfNumber.Get(7)),
            (12, PdfNumber.Get(8)),
            (13, PdfName.Get("DeviceRGB")),
            (14, PdfName.Get("FlateDecode")));
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Subtype, PdfName.Image);
        dictionary.Set(PdfName.Get("Width"), new PdfReference(new IndirectReference(10, 0)));
        dictionary.Set(PdfName.Get("Height"), new PdfReference(new IndirectReference(11, 0)));
        dictionary.Set(PdfName.BitsPerComponent, new PdfReference(new IndirectReference(12, 0)));
        dictionary.Set(PdfName.ColorSpace, new PdfReference(new IndirectReference(13, 0)));
        dictionary.Set(PdfName.Filter, new PdfReference(new IndirectReference(14, 0)));
        var original = Samples(12, 7, 3);

        var (ok, samples, _, detail) = Decode(new PdfStream(dictionary, Flate(original)), objects);

        Assert.True(ok, detail);
        Assert.Equal(original, samples!.Samples);
    }

    [Fact]
    public void OneBit_IsBilevel() =>
        AssertSkip(new PdfStream(ImageDictionary(8, 8, ColorSpaceFor(1), bitsPerComponent: 1), new byte[8]), PlannedSkip.Bilevel);

    [Fact]
    public void ImageMask_IsAStencil()
    {
        var dictionary = ImageDictionary(8, 8, ColorSpaceFor(1), bitsPerComponent: 1);
        dictionary.Remove(PdfName.ColorSpace);
        dictionary.Set(PdfName.ImageMask, PdfBoolean.True);
        AssertSkip(new PdfStream(dictionary, new byte[8]), PlannedSkip.StencilMask);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(16)]
    public void OtherBitDepths_AreUnsupported(int bitsPerComponent) =>
        AssertSkip(new PdfStream(ImageDictionary(8, 8, ColorSpaceFor(3), bitsPerComponent), new byte[8 * 8 * 3 * 2]), PlannedSkip.UnsupportedBitDepth);

    [Theory]
    [InlineData("DeviceCMYK")]
    [InlineData("CalRGB")]
    [InlineData("Indexed")]
    [InlineData("Separation")]
    [InlineData("Lab")]
    [InlineData("ICC4")]
    [InlineData("Missing")]
    public void OtherColorSpaces_AreUnsupported(string family)
    {
        var profileDictionary = new PdfDictionary();
        profileDictionary.Set(PdfName.N, PdfNumber.Get(4));
        var objects = Registry((5, new PdfStream(profileDictionary, new byte[4])));
        PdfObject colorSpace = family switch
        {
            "DeviceCMYK" => PdfName.Get("DeviceCMYK"),
            "CalRGB" => new PdfArray([PdfName.Get("CalRGB"), new PdfDictionary()]),
            "Indexed" => new PdfArray([PdfName.Get("Indexed"), PdfName.Get("DeviceRGB"), PdfNumber.Get(1), PdfString.FromHex(new byte[6])]),
            "Separation" => new PdfArray([PdfName.Get("Separation"), PdfName.Get("Spot"), PdfName.Get("DeviceGray"), new PdfDictionary()]),
            "Lab" => new PdfArray([PdfName.Get("Lab"), new PdfDictionary()]),
            "ICC4" => new PdfArray([PdfName.Get("ICCBased"), new PdfReference(new IndirectReference(5, 0))]),
            _ => PdfNull.Instance,
        };
        var dictionary = ImageDictionary(4, 4, colorSpace);
        if (family == "Missing")
        {
            dictionary.Remove(PdfName.ColorSpace);
        }

        AssertSkip(new PdfStream(dictionary, new byte[4 * 4 * 4]), PlannedSkip.UnsupportedColorSpace, objects);
    }

    [Fact]
    public void SoftMask_WithoutAColorSpace_IsReadAsGray()
    {
        var original = Samples(9, 9, 1);
        var dictionary = ImageDictionary(9, 9, ColorSpaceFor(1));
        dictionary.Remove(PdfName.ColorSpace);
        var image = new PdfStream(dictionary, original);

        Assert.False(Decode(image).Ok);
        var (ok, samples, _, detail) = Decode(image, softMask: true);
        Assert.True(ok, detail);
        Assert.Equal(1, samples!.Channels);
        Assert.Equal(original, samples.Samples);
    }

    [Theory]
    [InlineData("JPXDecode")]
    [InlineData("JBIG2Decode")]
    [InlineData("CCITTFaxDecode")]
    public void CodecsCompressionDoesNotReencode_AreUnsupportedFilters(string filter)
    {
        var dictionary = ImageDictionary(8, 8, ColorSpaceFor(1));
        dictionary.Set(PdfName.Filter, new PdfArray([PdfName.Get("FlateDecode"), PdfName.Get(filter)]));
        AssertSkip(new PdfStream(dictionary, new byte[16]), PlannedSkip.UnsupportedFilter);
    }

    [Fact]
    public void ColorKeyMask_IsSkipped()
    {
        var dictionary = ImageDictionary(4, 4, ColorSpaceFor(3));
        dictionary.Set(PdfName.Get("Mask"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0), PdfNumber.Get(10)]));
        AssertSkip(new PdfStream(dictionary, new byte[4 * 4 * 3]), PlannedSkip.ColorKeyMask);
    }

    [Fact]
    public void IndirectColorKeyMask_IsSkipped()
    {
        var dictionary = ImageDictionary(4, 4, ColorSpaceFor(3));
        dictionary.Set(PdfName.Get("Mask"), new PdfReference(new IndirectReference(20, 0)));
        var objects = Registry((20, new PdfArray([PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0), PdfNumber.Get(10), PdfNumber.Get(0), PdfNumber.Get(10)])));
        AssertSkip(new PdfStream(dictionary, new byte[4 * 4 * 3]), PlannedSkip.ColorKeyMask, objects);
    }

    [Fact]
    public void StencilMaskStream_IsNotAColorKeyMask()
    {
        var original = Samples(4, 4, 3);
        var mask = new PdfDictionary();
        mask.Set(PdfName.ImageMask, PdfBoolean.True);
        var dictionary = ImageDictionary(4, 4, ColorSpaceFor(3));
        dictionary.Set(PdfName.Get("Mask"), new PdfReference(new IndirectReference(20, 0)));

        var (ok, samples, _, detail) = Decode(new PdfStream(dictionary, original), Registry((20, new PdfStream(mask, new byte[4]))));

        Assert.True(ok, detail);
        Assert.Equal(original, samples!.Samples);
    }

    [Fact]
    public void TruncatedJpeg_UnderStrict_IsADecodeFailureNotAThrow()
    {
        var image = DctImage(Samples(64, 64, 3), 64, 64, 3);
        var truncated = new PdfStream(image.Dictionary, image.RawBytes[..(image.RawBytes.Length / 2)]);
        var strict = PdfOptions.Default with { Strict = true };

        var (ok, samples, skip, detail) = Decode(truncated, options: strict);

        Assert.False(ok);
        Assert.Null(samples);
        Assert.Equal(PlannedSkip.DecodeFailed, skip);
        Assert.Contains("PLUME32", detail);
    }

    [Fact]
    public void CorruptFlate_IsADecodeFailure()
    {
        var dictionary = ImageDictionary(8, 8, ColorSpaceFor(3));
        dictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        AssertSkip(new PdfStream(dictionary, new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0x00, 0x01 }), PlannedSkip.DecodeFailed, options: PdfOptions.Default with { Strict = true });
    }

    [Fact]
    public void ShortSampleData_IsADecodeFailure() =>
        AssertSkip(new PdfStream(ImageDictionary(8, 8, ColorSpaceFor(3)), new byte[10]), PlannedSkip.DecodeFailed);

    [Fact]
    public void JpegWhoseComponentsDisagreeWithTheColorSpace_IsADecodeFailure()
    {
        var image = DctImage(Samples(16, 16, 3), 16, 16, 3);
        image.Dictionary.Set(PdfName.ColorSpace, ColorSpaceFor(1));
        AssertSkip(image, PlannedSkip.DecodeFailed);
    }

    [Fact]
    public void MissingWidth_IsADecodeFailure()
    {
        var dictionary = ImageDictionary(8, 8, ColorSpaceFor(1));
        dictionary.Remove(PdfName.Get("Width"));
        AssertSkip(new PdfStream(dictionary, new byte[64]), PlannedSkip.DecodeFailed);
    }

    [Fact]
    public void MorePixelsThanTheCap_IsAResourceLimit() =>
        AssertSkip(FlateImage(Samples(32, 32, 1), 32, 32, 1), PlannedSkip.ResourceLimit, options: PdfOptions.Default with { MaxImagePixels = 1000 });

    [Fact]
    public void DecompressionPastTheCap_IsAResourceLimit() =>
        AssertSkip(FlateImage(Samples(64, 64, 3), 64, 64, 3), PlannedSkip.ResourceLimit, options: PdfOptions.Default with { MaxDecompressedStreamBytes = 4096 });

    [Fact]
    public void JpegPastThePixelCap_IsAResourceLimit()
    {
        // The dictionary understates the size; the JPEG frame itself trips the decoder's cap.
        var image = DctImage(Samples(64, 64, 1), 64, 64, 1);
        image.Dictionary.Set(PdfName.Get("Width"), PdfNumber.Get(8));
        image.Dictionary.Set(PdfName.Get("Height"), PdfNumber.Get(8));
        AssertSkip(image, PlannedSkip.ResourceLimit, options: PdfOptions.Default with { MaxImagePixels = 100 });
    }
}
