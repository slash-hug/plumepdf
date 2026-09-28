using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PlumePdf.Tests.Filters.Png;

/// <summary>
/// A minimal, from-scratch, spec-independent PNG chunk writer used only by this test
/// project — deliberately NOT <see cref="PlumePdf.Filters.Png.PngEncoder"/>: every
/// <c>PngDecoderTests</c> fixture is built here byte-by-byte against ISO/IEC 15948's chunk
/// layout, so decode tests are checked against an independent implementation rather than
/// round-tripping through PlumePDF's own encoder (which would make a decoder bug and its
/// matching encoder bug invisible to each other). Fixtures are generated in-process, at test
/// run time, from small hand-picked, provenance-recorded pixel grids, with the provenance
/// being "authored directly in this file against the PNG spec" rather than a binary blob
/// copied from PngSuite.
/// </summary>
internal static class RawPngBuilder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Build(int width, int height, int bitDepth, int colorType, byte[] scanlines, byte[]? palette = null, byte[]? transparency = null, (double X, double Y)? dpi = null, int interlace = 0, byte[]? rawIdatOverride = null)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = (byte)bitDepth;
        ihdr[9] = (byte)colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = (byte)interlace;
        WriteChunk(output, "IHDR", ihdr);

        if (palette is not null)
        {
            WriteChunk(output, "PLTE", palette);
        }

        if (transparency is not null)
        {
            WriteChunk(output, "tRNS", transparency);
        }

        if (dpi is { } d)
        {
            var phys = new byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(0, 4), (uint)Math.Round(d.X / 0.0254));
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4, 4), (uint)Math.Round(d.Y / 0.0254));
            phys[8] = 1;
            WriteChunk(output, "pHYs", phys);
        }

        // rawIdatOverride lets a test hand-craft the IDAT payload's raw bytes directly
        // (e.g. a zlib header that trips a corrupt-stream shape) instead of a real,
        // honestly-deflated stream from DeflateZlib(scanlines).
        WriteChunk(output, "IDAT", rawIdatOverride ?? DeflateZlib(scanlines));
        WriteChunk(output, "IEND", []);

        return output.ToArray();
    }

    /// <summary>Prefixes every row in <paramref name="rows"/> with PNG filter-type byte 0 ("None") and returns the concatenated scanline stream <see cref="Build"/> expects.</summary>
    public static byte[] NoneFilterScanlines(byte[][] rows)
    {
        using var output = new MemoryStream();
        foreach (var row in rows)
        {
            output.WriteByte(0);
            output.Write(row);
        }

        return output.ToArray();
    }

    private static byte[] DeflateZlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)data.Length);
        output.Write(lengthBytes);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
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
}
