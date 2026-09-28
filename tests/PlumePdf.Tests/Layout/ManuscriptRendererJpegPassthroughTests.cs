using PlumePdf.Compose;
using PlumePdf.Documents;
using PlumePdf.Elements;
using PlumePdf.Filters;
using PlumePdf.Tests.Filters.Jpeg;
using Xunit;

namespace PlumePdf.Tests.Layout;

/// <summary>
/// <c>ManuscriptRenderer.GetOrCreateImage</c>'s DCT pass-through
/// path — a JPEG-sourced <see cref="Image"/> embeds the original encoder's bytes unchanged
/// behind <c>/DCTDecode</c> rather than being decoded to pixels and re-encoded, and a
/// 4-component (CMYK) source whose file carried an Adobe <c>APP14</c> marker gets
/// <c>/Decode [1 0 1 0 1 0 1 0]</c> to undo Adobe's storage inversion (ISO 32000-1 §8.9.5.2
/// Table 90). This deliberately uses a named fixture and an explicit assertion here, not a
/// round-trip test (a round-trip through PlumePDF's own encoder/decoder could be
/// self-consistently wrong in a way a decode-then-encode comparison would never catch) — every
/// assertion below compares the extracted XObject's raw bytes/dictionary entries against the
/// known-in-advance source bytes, never against a re-decoded pixel buffer.
/// </summary>
public class ManuscriptRendererJpegPassthroughTests
{
    [Fact]
    public void GrayscaleJpeg_EmbedsAsDctPassthrough_ByteIdenticalToSource()
    {
        var originalJpeg = JpegEncoder.Encode(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120 }, width: 4, height: 3, componentCount: 1, quality: 90);
        var frame = RasterImage.Decode(originalJpeg).Frames[0];

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(frame));
        });

        var extracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.True(extracted.IsJpeg, "A JPEG-sourced Image must pass through as /DCTDecode, not decode-then-re-encode.");
        Assert.Contains("DCTDecode", extracted.Filters);
        Assert.Equal("DeviceGray", extracted.ColorSpaceName);
        Assert.Equal(originalJpeg, extracted.Data.ToArray()); // byte-identical, not merely visually equivalent.
    }

    [Fact]
    public void RgbJpeg_EmbedsAsDctPassthrough_ByteIdenticalToSource()
    {
        byte[] rgbPixels = new byte[4 * 3 * 3];
        for (var i = 0; i < rgbPixels.Length; i++)
        {
            rgbPixels[i] = (byte)(i * 7);
        }

        var originalJpeg = JpegEncoder.Encode(rgbPixels, width: 4, height: 3, componentCount: 3, quality: 90, subsampleChroma: false);
        var frame = RasterImage.Decode(originalJpeg).Frames[0];

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(frame));
        });

        var extracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.True(extracted.IsJpeg);
        Assert.Equal("DeviceRGB", extracted.ColorSpaceName);
        Assert.Equal(originalJpeg, extracted.Data.ToArray());
    }

    [Fact]
    public void AdobeInvertedCmykJpeg_EmitsDeviceCmykWithInversionDecodeArray()
    {
        // The committed Adobe-CMYK named fixture (tests/PlumePdf.Tests/Filters/Jpeg/CmykJpegFixture.cs):
        // a 4-component JPEG carrying an Adobe APP14 marker, whose stored samples are — per
        // Adobe's own convention — the inverse of the actual ink amounts.
        var originalJpeg = CmykJpegFixture.BuildFlatColor(200, 120, 60, 40);
        var frame = RasterImage.Decode(originalJpeg).Frames[0];

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(frame));
        });

        var extracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.True(extracted.IsJpeg);
        Assert.Equal("DeviceCMYK", extracted.ColorSpaceName);
        Assert.Equal(originalJpeg, extracted.Data.ToArray());

        // The named, explicit assertion this pins: the inversion Decode array is present
        // and exactly [1 0 1 0 1 0 1 0], not merely "some Decode array exists".
        Assert.NotNull(extracted.Decode);
        Assert.Equal(new double[] { 1, 0, 1, 0, 1, 0, 1, 0 }, extracted.Decode);
    }

    [Fact]
    public void CmykJpeg_UnderPdfAConformance_RefusesWithPlume9014()
    {
        // Follow-up finding: PLUME3610's message was made honest, but the deeper
        // defect was that a CMYK JPEG composed through Manuscript under PdfAConformance would
        // silently produce a non-conformant PDF/A (DeviceCMYK with only an sRGB output intent).
        // The DCT pass-through now refuses it outright.
        var originalJpeg = CmykJpegFixture.BuildFlatColor(200, 120, 60, 40);
        var frame = RasterImage.Decode(originalJpeg).Frames[0];

        var manuscript = new Manuscript
        {
            Sections = [new Section { Body = new Image(frame) }],
        };

        var ex = Assert.Throws<PlumePdfException>(
            () => manuscript.Render(new PdfOptions { PdfAConformance = PdfAConformance.A2b }));
        Assert.Equal("PLUME9014", ex.Code);
    }

    [Fact]
    public void CmykJpeg_WithoutPdfAConformance_StillEmbedsAsDctPassthrough()
    {
        // The refusal is scoped to PDF/A only — a plain document still passes the CMYK JPEG
        // through unchanged behind /DCTDecode (guarding against an over-broad guard).
        var originalJpeg = CmykJpegFixture.BuildFlatColor(200, 120, 60, 40);
        var frame = RasterImage.Decode(originalJpeg).Frames[0];

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(frame));
        });

        var extracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.Equal("DeviceCMYK", extracted.ColorSpaceName);
    }

    [Fact]
    public void NonAdobeCmykJpeg_HasNoInversionDecodeArray()
    {
        // A 4-component JPEG with no APP14 marker at all carries no Adobe inversion signal -
        // /Decode must be absent (the default [0 1 0 1 0 1 0 1] applies), never emitted
        // speculatively just because the component count is 4.
        var data = BuildCmykWithoutAdobeMarker();
        var frame = RasterImage.Decode(data).Frames[0];

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(frame));
        });

        var extracted = Assert.Single(document.Pages[0].ExtractImages());
        Assert.Equal("DeviceCMYK", extracted.ColorSpaceName);
        Assert.Null(extracted.Decode);
    }

    private static byte[] BuildCmykWithoutAdobeMarker()
    {
        // CmykJpegFixture always writes an Adobe APP14 marker (that's the point of that
        // fixture); this test needs the negative case, so it strips the marker segment
        // (0xFF 0xEE ...) out of the otherwise-identical byte stream rather than duplicating
        // CmykJpegFixture's whole encode path for one flag.
        var withMarker = CmykJpegFixture.BuildFlatColor(200, 120, 60, 40);
        var bytes = new List<byte>(withMarker.Length);
        var i = 0;
        while (i < withMarker.Length)
        {
            if (withMarker[i] == 0xFF && i + 1 < withMarker.Length && withMarker[i + 1] == 0xEE)
            {
                var length = (withMarker[i + 2] << 8) | withMarker[i + 3];
                i += 2 + length;
                continue;
            }

            bytes.Add(withMarker[i]);
            i++;
        }

        return [.. bytes];
    }
}
