using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

/// <summary>
/// Exercises <see cref="DctFilterAdapter"/> via a caller-constructed <see cref="PdfFilterRegistry"/>
/// (its own verify step) rather than <see cref="PdfFilterRegistry.Default"/> - default
/// registration of <c>DCTDecode</c> is covered separately.
/// </summary>
public class DctFilterAdapterTests
{
    private static PdfFilterRegistry BuildRegistry()
    {
        var registry = new PdfFilterRegistry();
        registry.Register("DCTDecode", new DctFilterAdapter());
        return registry;
    }

    [Fact]
    public void Decode_GrayscaleJpeg_ReturnsRawGraySamples()
    {
        var pixels = new byte[16 * 16];
        Array.Fill(pixels, (byte)90);
        var jpegBytes = JpegEncoder.Encode(pixels, 16, 16, componentCount: 1, quality: 95);

        var registry = BuildRegistry();
        var streamDict = new PdfDictionary { [PdfName.Filter] = PdfName.Get("DCTDecode") };
        var decoded = registry.Decode(streamDict, jpegBytes, PdfOptions.Default);

        Assert.Equal(16 * 16, decoded.Length);
        foreach (var sample in decoded)
        {
            Assert.InRange(sample, 88, 92);
        }
    }

    [Fact]
    public void Decode_RgbJpeg_ReturnsInterleavedRgbSamples()
    {
        var pixels = new byte[8 * 8 * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 200;
            pixels[i + 1] = 50;
            pixels[i + 2] = 10;
        }

        var jpegBytes = JpegEncoder.Encode(pixels, 8, 8, componentCount: 3, quality: 95, subsampleChroma: false);

        var registry = BuildRegistry();
        var streamDict = new PdfDictionary { [PdfName.Filter] = PdfName.Get("DCTDecode") };
        var decoded = registry.Decode(streamDict, jpegBytes, PdfOptions.Default);

        Assert.Equal(8 * 8 * 3, decoded.Length);
        Assert.InRange(decoded[0], 190, 210);
        Assert.InRange(decoded[1], 40, 60);
        Assert.InRange(decoded[2], 0, 20);
    }

    [Fact]
    public void Decode_ColorTransformDecodeParms_IsReadAndThreadedThrough()
    {
        // /ColorTransform (ISO 32000-1 Table 13) lives in the stream's own /DecodeParms
        // dictionary, exactly like LZWDecode's /EarlyChange - this pins that the adapter
        // reads it via IPdfFilterWithDecodeParms rather than ignoring it. A flat color
        // survives either transform interpretation identically (Y=128 with zero chroma maps
        // to R=G=B=128 either way), so this is a plumbing proof, not a color-math one.
        var pixels = new byte[8 * 8 * 3];
        Array.Fill(pixels, (byte)128);
        var jpegBytes = JpegEncoder.Encode(pixels, 8, 8, componentCount: 3, quality: 95, subsampleChroma: false);

        var registry = BuildRegistry();
        var streamDict = new PdfDictionary
        {
            [PdfName.Filter] = PdfName.Get("DCTDecode"),
            [PdfName.DecodeParms] = new PdfDictionary { [PdfName.Get("ColorTransform")] = PdfNumber.Get(0) },
        };
        var decoded = registry.Decode(streamDict, jpegBytes, PdfOptions.Default);

        Assert.Equal(8 * 8 * 3, decoded.Length);
        Assert.InRange(decoded[0], 120, 136);
    }

    [Fact]
    public void Decode_CmykJpeg_ReturnsFourComponentSamples()
    {
        var jpegBytes = CmykJpegFixture.BuildFlatColor(200, 120, 60, 40);

        var registry = BuildRegistry();
        var streamDict = new PdfDictionary { [PdfName.Filter] = PdfName.Get("DCTDecode") };
        var decoded = registry.Decode(streamDict, jpegBytes, PdfOptions.Default);

        Assert.Equal(8 * 8 * 4, decoded.Length);
        Assert.Equal(200, decoded[0]);
        Assert.Equal(120, decoded[1]);
        Assert.Equal(60, decoded[2]);
        Assert.Equal(40, decoded[3]);
    }

    [Fact]
    public void UnregisteredFilterName_StillThrowsPlume3010()
    {
        var registry = new PdfFilterRegistry(); // DCTDecode deliberately not registered.
        var streamDict = new PdfDictionary { [PdfName.Filter] = PdfName.Get("DCTDecode") };
        var ex = Assert.Throws<PlumePdfException>(() => registry.Decode(streamDict, [0xFF, 0xD8], PdfOptions.Default));
        Assert.Equal("PLUME3010", ex.Code);
    }
}
