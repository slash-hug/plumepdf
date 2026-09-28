using PlumePdf.Filters.Png;
using Xunit;

namespace PlumePdf.Tests.Filters.Png;

/// <summary>
/// D2 coverage for <see cref="PngEncoder"/>. Per the C8 ruling, hermetic PNG tests compare
/// <em>decoded pixels and chunk structure</em>, never encoded bytes — <c>ZLibStream</c>'s
/// output is a property of the runtime's zlib, not of PlumePDF, so an encoded-byte golden
/// would drift across .NET SDK versions/platforms for reasons that have nothing to do with a
/// real regression.
/// </summary>
public class PngEncoderTests
{
    [Theory]
    [InlineData(RasterPixelFormat.Gray8)]
    [InlineData(RasterPixelFormat.Rgb24)]
    [InlineData(RasterPixelFormat.Rgba32)]
    public void EncodeThenDecode_IsPixelIdentical(RasterPixelFormat format)
    {
        const int width = 5;
        const int height = 3;
        var bytesPerPixel = RasterImageFrame.BytesPerPixel(format);
        var pixels = new byte[width * height * bytesPerPixel];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 37) % 256);
        }

        var frame = new RasterImageFrame(pixels, width, height, format, xDpi: 150, yDpi: 200);

        var encoded = PngEncoder.Encode(frame, PdfOptions.Default);
        var decoded = PngDecoder.Decode(encoded, long.MaxValue);

        Assert.Equal(format, decoded.Format);
        Assert.Equal(width, decoded.Width);
        Assert.Equal(height, decoded.Height);
        Assert.Equal(pixels, decoded.Pixels.ToArray());
        Assert.Equal(150, decoded.XDpi!.Value, precision: 0);
        Assert.Equal(200, decoded.YDpi!.Value, precision: 0);
    }

    [Fact]
    public void Encode_WithoutDpi_OmitsPhysChunk()
    {
        var frame = new RasterImageFrame(new byte[] { 1, 2, 3, 4 }, 2, 2, RasterPixelFormat.Gray8);

        var encoded = PngEncoder.Encode(frame, PdfOptions.Default);
        var chunkTypes = EnumerateChunkTypes(encoded);

        Assert.DoesNotContain("pHYs", chunkTypes);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunkTypes);

        var decoded = PngDecoder.Decode(encoded, long.MaxValue);
        Assert.Null(decoded.XDpi);
        Assert.Null(decoded.YDpi);
    }

    [Fact]
    public void Encode_WithDpi_WritesChunksInStandardOrder()
    {
        var frame = new RasterImageFrame(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, 2, 2, RasterPixelFormat.Rgb24, xDpi: 72, yDpi: 72);

        var encoded = PngEncoder.Encode(frame, PdfOptions.Default);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, encoded[..8]);
        Assert.Equal(["IHDR", "pHYs", "IDAT", "IEND"], EnumerateChunkTypes(encoded));
    }

    [Fact]
    public void Ihdr_DeclaresEightBitDepthAndMatchingColorType()
    {
        var frame = new RasterImageFrame(new byte[] { 1 }, 1, 1, RasterPixelFormat.Gray8);
        var encoded = PngEncoder.Encode(frame, PdfOptions.Default);

        var ihdrData = ReadChunkData(encoded, "IHDR");
        Assert.Equal(8, ihdrData[8]); // bit depth
        Assert.Equal(0, ihdrData[9]); // color type: grayscale
    }

    private static List<string> EnumerateChunkTypes(byte[] png)
    {
        var types = new List<string>();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            types.Add(type);
            offset += 8 + length + 4;
        }

        return types;
    }

    private static byte[] ReadChunkData(byte[] png, string wantedType)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == wantedType)
            {
                return png[(offset + 8)..(offset + 8 + length)];
            }

            offset += 8 + length + 4;
        }

        throw new InvalidOperationException($"Chunk '{wantedType}' not found.");
    }
}
