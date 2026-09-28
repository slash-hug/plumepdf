using System.Buffers.Binary;
using System.Text;

namespace PlumePdf.Filters.Png;

/// <summary>
/// A from-scratch PNG encoder (ISO/IEC 15948) for the three normalized shapes
/// <see cref="RasterPixelFormat"/> defines: 8-bit grayscale (color type 0), 24-bit RGB
/// (color type 2), and 32-bit RGBA (color type 6) — never palette output, since a decoded
/// <see cref="RasterImageFrame"/> carries no palette to re-derive one from. Every row is
/// written with PNG filter type 0 ("None") — the simplest correct choice; adaptive
/// per-row filter selection (typically a modest size win) is a 1.x polish item, not a
/// correctness concern (C8's hermetic tests compare decoded pixels and chunk structure,
/// never encoded bytes, precisely because encoded PNG bytes are not meant to be a stable
/// cross-platform contract). Compression is <see cref="FlateEncodingFilter"/>, the same
/// zlib-wrapped DEFLATE encoder every other PlumePDF stream writer uses.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Encodes <paramref name="frame"/> as a complete PNG file.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frame"/>'s <see cref="RasterImageFrame.Format"/> is not one of the three supported shapes.</exception>
    public static byte[] Encode(RasterImageFrame frame, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        var (colorType, samplesPerPixel) = frame.Format switch
        {
            RasterPixelFormat.Gray8 => (0, 1),
            RasterPixelFormat.Rgb24 => (2, 3),
            RasterPixelFormat.Rgba32 => (6, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(frame), frame.Format, "PngEncoder supports Gray8, Rgb24, and Rgba32 only."),
        };

        var output = new MemoryStream();
        output.Write(Signature);

        WriteChunk(output, "IHDR", BuildIhdr(frame.Width, frame.Height, colorType));

        if (frame.XDpi is { } xDpi && frame.YDpi is { } yDpi)
        {
            WriteChunk(output, "pHYs", BuildPhys(xDpi, yDpi));
        }

        var filtered = ApplyNoneFilter(frame.Pixels.Span, frame.Width, frame.Height, samplesPerPixel);
        var encoder = options.Filters.TryGetEncoder("FlateDecode", out var flateEncoder) && flateEncoder is not null
            ? flateEncoder
            : new FlateEncodingFilter();
        var compressed = encoder.Encode(filtered, options);
        WriteChunk(output, "IDAT", compressed);

        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height, int colorType)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8; // bit depth: always 8, matching RasterPixelFormat's normalized shapes
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0; // compression method
        ihdr[11] = 0; // filter method
        ihdr[12] = 0; // interlace method: none (Adam7 output is a decode-only concern here)
        return ihdr;
    }

    private static byte[] BuildPhys(double xDpi, double yDpi)
    {
        var phys = new byte[9];
        var ppuX = (uint)Math.Round(xDpi / 0.0254);
        var ppuY = (uint)Math.Round(yDpi / 0.0254);
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(0, 4), ppuX);
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4, 4), ppuY);
        phys[8] = 1; // unit specifier: 1 = meter
        return phys;
    }

    private static byte[] ApplyNoneFilter(ReadOnlySpan<byte> pixels, int width, int height, int samplesPerPixel)
    {
        var rowBytes = width * samplesPerPixel;
        var stride = rowBytes + 1;
        var output = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            output[y * stride] = 0; // filter type 0: None
            pixels.Slice(y * rowBytes, rowBytes).CopyTo(output.AsSpan((y * stride) + 1, rowBytes));
        }

        return output;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        output.Write(lengthBytes);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32.Compute(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }
}

/// <summary>The standard CRC-32 (ISO 3309 / ITU-T V.42, the same polynomial zlib and Ethernet use) PNG requires on every chunk.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in first)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in second)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
