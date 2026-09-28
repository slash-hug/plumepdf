using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxImage"/>'s sample conversions and <see cref="JpxPlane"/>'s
/// sign extension. These pins are the contract every consumer (the filter adapter, the resolver's
/// JPX branch, <c>RasterImage.Decode</c>) builds on, including the recorded divergence from PNG/TIFF
/// at 16-bit: JPX rounds at every depth where PNG/TIFF's <c>&gt;&gt; 8</c> truncates, so
/// raw 255 → 1 (shift: 0) and 511 → 2 (shift: 1); mid-range values such as 32768 → 128 agree.
/// </summary>
public class JpxTypesTests
{
    [Theory]
    [InlineData(12, 4095, 255)]
    [InlineData(12, 2048, 128)]
    [InlineData(12, 0, 0)]
    [InlineData(16, 65535, 255)]
    [InlineData(16, 32768, 128)] // agrees with >> 8
    [InlineData(16, 255, 1)]     // >> 8 gives 0 — the recorded divergence
    [InlineData(16, 511, 2)]     // >> 8 gives 1
    [InlineData(16, 32767, 127)]
    [InlineData(8, 255, 255)]
    [InlineData(8, 128, 128)]
    [InlineData(4, 15, 255)]
    [InlineData(1, 1, 255)]
    public void Rescale_FullScale_RoundsHalfUp(int precision, int value, int expected)
    {
        var max = (1 << precision) - 1;
        Assert.Equal(expected, JpxImage.Rescale(value, max, 255));
    }

    [Fact]
    public void Plane_Sample_SignExtendsSignedComponents()
    {
        var plane8 = new JpxPlane { Width = 2, Height = 1, Precision = 8, Signed = true, Samples8 = [0x80, 0x7F] };
        Assert.Equal(-128, plane8.Sample(0));
        Assert.Equal(127, plane8.Sample(1));

        var plane12 = new JpxPlane { Width = 2, Height = 1, Precision = 12, Signed = true, Samples16 = [0x800, 0x7FF] };
        Assert.Equal(-2048, plane12.Sample(0));
        Assert.Equal(2047, plane12.Sample(1));

        var unsigned16 = new JpxPlane { Width = 1, Height = 1, Precision = 16, Signed = false, Samples16 = [0x8000] };
        Assert.Equal(32768, unsigned16.Sample(0));
    }

    [Fact]
    public void SampleUnsigned_ShiftsSignedByHalfRange()
    {
        var image = new JpxImage
        {
            Width = 2,
            Height = 1,
            Planes = [new JpxPlane { Width = 2, Height = 1, Precision = 8, Signed = true, Samples8 = [0x80, 0x7F] }],
            Colour = new JpxColourInfo(),
        };

        Assert.Equal(0, image.SampleUnsigned(0, 0));   // −128 + 128
        Assert.Equal(255, image.SampleUnsigned(0, 1)); // 127 + 128
    }

    [Fact]
    public void ToInterleaved8Bit_InterleavesPlanesAndDropsAlphaOnRequest()
    {
        var image = new JpxImage
        {
            Width = 2,
            Height = 1,
            Planes =
            [
                new JpxPlane { Width = 2, Height = 1, Precision = 8, Signed = false, Samples8 = [10, 20] },
                new JpxPlane { Width = 2, Height = 1, Precision = 8, Signed = false, Samples8 = [30, 40] },
                new JpxPlane { Width = 2, Height = 1, Precision = 8, Signed = false, Samples8 = [255, 0] },
            ],
            Colour = new JpxColourInfo { AlphaChannelIndex = 2 },
        };

        Assert.Equal(new byte[] { 10, 30, 20, 40 }, image.ToInterleaved8Bit(dropAlpha: true));
        Assert.Equal(new byte[] { 10, 30, 255, 20, 40, 0 }, image.ToInterleaved8Bit(dropAlpha: false));
        Assert.Equal(2, image.ColourChannelCount);
    }

    [Fact]
    public void ToInterleaved8Bit_RescalesHighPrecisionAndSignedPlanes()
    {
        var image = new JpxImage
        {
            Width = 2,
            Height = 1,
            Planes =
            [
                new JpxPlane { Width = 2, Height = 1, Precision = 16, Signed = false, Samples16 = [65535, 255] },
                new JpxPlane { Width = 2, Height = 1, Precision = 12, Signed = true, Samples16 = [0x800, 0x7FF] },
            ],
            Colour = new JpxColourInfo(),
        };

        Assert.Equal(new byte[] { 255, 0, 1, 255 }, image.ToInterleaved8Bit(dropAlpha: true)); // 255/65535 → 1 (PNG/TIFF's >> 8 would give 0)
    }

    [Fact]
    public void ToInterleaved16BitBigEndian_RescalesToFullScale()
    {
        var image = new JpxImage
        {
            Width = 2,
            Height = 1,
            Planes = [new JpxPlane { Width = 2, Height = 1, Precision = 12, Signed = false, Samples16 = [4095, 0] }],
            Colour = new JpxColourInfo(),
        };

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0x00, 0x00 }, image.ToInterleaved16BitBigEndian(dropAlpha: true));
    }

    [Fact]
    public void Siz_TileCounts_FollowB5()
    {
        var siz = new JpxSiz
        {
            Xsiz = 640,
            Ysiz = 480,
            XOsiz = 0,
            YOsiz = 0,
            XTsiz = 320,
            YTsiz = 240,
            XTOsiz = 0,
            YTOsiz = 0,
            Rsiz = 0,
            Components = [new JpxComponentInfo(8, false, 1, 1)],
        };
        Assert.Equal(2, siz.NumXTiles);
        Assert.Equal(2, siz.NumYTiles);

        var odd = new JpxSiz
        {
            Xsiz = 97,
            Ysiz = 61,
            XOsiz = 3,
            YOsiz = 0,
            XTsiz = 32,
            YTsiz = 24,
            XTOsiz = 0,
            YTOsiz = 0,
            Rsiz = 0,
            Components = [new JpxComponentInfo(8, false, 1, 1)],
        };
        Assert.Equal(4, odd.NumXTiles); // ceil(97/32)
        Assert.Equal(3, odd.NumYTiles); // ceil(61/24)
    }

    [Fact]
    public void DiagnosticCodes_AreLiteralsInTheJpxBand()
    {
        var codes = typeof(JpxDiagnosticCodes).GetFields().Select(f => (string)f.GetValue(null)!).ToArray();
        // 19 real codes (3700-3718); PLUME3749 (NotImplemented) was retired once
        // the codec was implemented (docs/errors/PLUME3749.md's retirement note).
        Assert.Equal(19, codes.Length);
        Assert.All(codes, c => Assert.Matches("^PLUME37[01][0-9]$", c));
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
