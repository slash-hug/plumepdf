using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// A behavior-identical-refactor guard for the shared CMYK/APP14 helper extracted from
/// <c>RasterImage.ConvertCmykToRgb</c>. Locks the exact ISO 32000-1 §8.6.5.3 outputs — both
/// APP14-inverted and plain — so the extraction (and any future consumer, e.g. the render-time
/// image resolver) can never drift from what
/// <see cref="RasterImage.Decode(ReadOnlyMemory{byte},PdfOptions?)"/> shipped in Phase 7.
/// The double-inversion bug class (the same shape as the earlier TIFF polarity
/// bug) is exactly what a second independently-authored path would reintroduce.
/// </summary>
public class JpegColorTransformsTests
{
    [Fact]
    public void ConvertCmykToRgb_PlainSamples_MatchesIsoFormula()
    {
        // One pixel per case: pure C, pure K, C+K saturating the min(255, C+K) clamp, and white.
        byte[] cmyk =
        [
            255, 0, 0, 0, // full cyan
            0, 0, 0, 255, // full black
            200, 0, 0, 100, // C+K > 255 clamps
            0, 0, 0, 0, // no ink = white
        ];

        var rgb = JpegColorTransforms.ConvertCmykToRgb(cmyk, width: 4, height: 1, adobeInverted: false);

        Assert.Equal(new byte[]
        {
            0, 255, 255, // R = 255-min(255,255+0)=0
            0, 0, 0,
            0, 155, 155, // R = 255-min(255,300)=0; G/B = 255-100
            255, 255, 255,
        }, rgb);
    }

    [Fact]
    public void ConvertCmykToRgb_AdobeInverted_UndoesStorageInversionFirst()
    {
        // Stored 255 on every channel un-inverts to 0 ink = white; stored 0 un-inverts to
        // full ink on every channel = black (the same convention Decode_CmykJpeg_ConvertsToRgb24
        // asserts end-to-end through the real APP14 fixture).
        byte[] cmyk =
        [
            255, 255, 255, 255,
            0, 0, 0, 0,
        ];

        var rgb = JpegColorTransforms.ConvertCmykToRgb(cmyk, width: 2, height: 1, adobeInverted: true);

        Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0 }, rgb);
    }

    /// <summary>The facade and the shared helper must be the same math — decode a real APP14 CMYK JPEG through <see cref="RasterImage"/> and reproduce its pixel output via the helper's formula.</summary>
    [Fact]
    public void RasterImageCmykDecode_StillMatchesSharedHelper()
    {
        var jpeg = Jpeg.CmykJpegFixture.BuildFlatColor(0, 0, 0, 0);
        var frame = RasterImage.Decode(jpeg).Frames[0];

        Assert.Equal(RasterPixelFormat.Rgb24, frame.Format);
        // APP14 present -> stored 0 un-inverts to full ink -> pure black, per the helper.
        var viaHelper = JpegColorTransforms.ConvertCmykToRgb([0, 0, 0, 0], 1, 1, adobeInverted: true);
        Assert.Equal(viaHelper, frame.Pixels.ToArray()[..3]);
    }
}
