using System.Security.Cryptography;
using PlumePdf.Filters;
using PlumePdf.Tests.Compression;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

/// <summary>
/// Pins the encoder's exact output. Compress writes these bytes and promises deterministic output, so a
/// change to how the encoder builds its planes or buffers its output must not move a single byte.
/// The sizes cover the padding edges (smaller than one MCU, odd, not a multiple of 16), the
/// qualities cover the quantization extremes, and the saturated image forces 0xFF byte stuffing.
/// </summary>
public class JpegEncoderGoldenTests
{
    public static TheoryData<int, int, int, int, bool, string> Cases => new()
    {
        { 1, 1, 1, 75, true, "8CA1460D3DA518DDE3B06D5ACA11A4BDED57174AAC790F11C4D4B81BB94F9571" },
        { 17, 9, 1, 50, true, "29264EBEEDED92E8E5B2A3A0794728D070052421232C244070786F50EEDC2E3C" },
        { 250, 130, 1, 100, true, "DB684706E64CD418E804A724F561A12B0062B85884E1E997C84DC344652D88B8" },
        { 1, 1, 3, 75, true, "804D8A3E74290307673F40ECBF0436172B97FD252605ACE94D77BB36A20D5291" },
        { 17, 9, 3, 10, true, "77791563E897E95B7972857A72B20960092C20F3B0CE7AFC2E5F3C3FDC28D95C" },
        { 33, 31, 3, 75, true, "BA48E2C447C9A8D56E9CF7724925640871A5A2A9960A76AB01143DE5B5EA06F1" },
        { 250, 130, 3, 90, true, "EE59CEDF6C87DD89672CE504292E8813CAD0B5DCDDF57502171A3D62BCF01F30" },
        { 17, 9, 3, 75, false, "CDF1B8A3976904C60E78F401E8A14FFF66FA9CCB1F72D89DBAB132B63FA537C3" },
        { 250, 130, 3, 100, false, "19E42C53DD881D129A482C9265A454513D8501F78CD67521E7B91AFA8239A209" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Encode_IsByteIdenticalToTheRecordedOutput(int width, int height, int components, int quality, bool subsample, string expected)
    {
        var pixels = CompressionTestImages.Samples(width, height, components, seed: width + height);

        var jpeg = JpegEncoder.Encode(pixels, width, height, components, quality, subsample);

        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(jpeg)));
    }

    [Fact]
    public void Encode_SaturatedImageWithDensity_IsByteIdenticalToTheRecordedOutput()
    {
        var pixels = new byte[40 * 24 * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i / 3) % 7 == 0 ? 0 : 255);
        }

        var jpeg = JpegEncoder.Encode(pixels, 40, 24, 3, 100, subsampleChroma: true, xDpi: 300, yDpi: 150);

        Assert.Equal("8EE5DA5CAB1FE89B9FAE526A8E90D8BD907711880A8931EEB73B83730FCDBC14", Convert.ToHexString(SHA256.HashData(jpeg)));
    }
}
