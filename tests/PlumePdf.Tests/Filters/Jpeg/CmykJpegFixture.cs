using PlumePdf.Filters;

namespace PlumePdf.Tests.Filters.Jpeg;

/// <summary>
/// Hand-assembles a tiny, self-authored 4-component (CMYK) baseline JPEG with an Adobe
/// <c>APP14</c> marker (<c>transform=0</c>, i.e. "no color transform" - the samples are
/// stored as direct C/M/Y/K values, not YCCK) - test-only support for a committed
/// Adobe-CMYK fixture requirement. No CLI tool available in this environment can
/// author a genuine CMYK JPEG (cjpeg accepts only grayscale/RGB PPM input), so this builds
/// one directly from <see cref="JpegFdct"/> and <see cref="JpegHuffmanTable"/> - the same
/// primitives <see cref="JpegEncoder"/> uses for its own (RGB/grayscale-only scope)
/// baseline encoder - rather than duplicating an untested byte layout by
/// hand. A single 8x8-pixel, single-MCU, unquantized (quant table all-ones) image with a
/// flat color per channel: every AC coefficient is exactly zero for a constant block (DCT
/// basis orthogonality), so only each component's DC coefficient is nonzero, keeping the
/// entropy-coded payload to one Huffman-coded DC symbol + one EOB per component.
/// </summary>
public static class CmykJpegFixture
{
    /// <summary>Builds an 8x8 CMYK JPEG whose single pixel block is the flat color (<paramref name="c"/>, <paramref name="m"/>, <paramref name="y"/>, <paramref name="k"/>).</summary>
    public static byte[] BuildFlatColor(byte c, byte m, byte y, byte k)
    {
        var bytes = new List<byte>();
        WriteMarkerOnly(bytes, 0xD8); // SOI

        WriteAdobeApp14(bytes);
        WriteDqt(bytes);
        WriteSof0(bytes);
        WriteDht(bytes, 0, 0, JpegHuffmanTable.StandardLuminanceDcBits, JpegHuffmanTable.StandardLuminanceDcValues);
        WriteDht(bytes, 1, 0, JpegHuffmanTable.StandardLuminanceAcBits, JpegHuffmanTable.StandardLuminanceAcValues);
        WriteSos(bytes);

        var entropy = new List<byte>();
        var bitBuffer = 0;
        var bitCount = 0;
        var values = new[] { c, m, y, k };
        var predictors = new int[4]; // One independent DC predictor per component (ISO/IEC 10918-1 §F.1.1.5.1) - a single shared predictor across C/M/Y/K would corrupt every component after the first.
        for (var i = 0; i < values.Length; i++)
        {
            EncodeFlatBlock(values[i], JpegHuffmanTable.StandardLuminanceDc, JpegHuffmanTable.StandardLuminanceAc, ref predictors[i], entropy, ref bitBuffer, ref bitCount);
        }

        FlushWithPadding(entropy, ref bitBuffer, ref bitCount);
        bytes.AddRange(entropy);

        WriteMarkerOnly(bytes, 0xD9); // EOI
        return [.. bytes];
    }

    private static void EncodeFlatBlock(byte value, JpegHuffmanTable dcTable, JpegHuffmanTable acTable, ref int predictor, List<byte> entropy, ref int bitBuffer, ref int bitCount)
    {
        Span<float> block = stackalloc float[64];
        block.Fill(value - 128f);
        Span<float> coefficients = stackalloc float[64];
        JpegFdct.Transform(block, coefficients);

        var dc = (int)MathF.Round(coefficients[0]); // quant table is all-ones (see BuildFlatColor's remarks).
        var diff = dc - predictor;
        predictor = dc;

        var (dcSize, dcBits) = MagnitudeCategory(diff);
        var (dcCode, dcLength) = dcTable.GetCode((byte)dcSize);
        WriteBits(entropy, ref bitBuffer, ref bitCount, dcCode, dcLength);
        WriteBits(entropy, ref bitBuffer, ref bitCount, dcBits, dcSize);

        // Every AC coefficient of a constant block is exactly zero - one EOB (symbol 0x00) covers all 63.
        var (eobCode, eobLength) = acTable.GetCode(0x00);
        WriteBits(entropy, ref bitBuffer, ref bitCount, eobCode, eobLength);
    }

    private static (int Size, int Bits) MagnitudeCategory(int value)
    {
        if (value == 0)
        {
            return (0, 0);
        }

        var abs = Math.Abs(value);
        var size = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)abs);
        var bits = value > 0 ? value : value + (1 << size) - 1;
        return (size, bits);
    }

    private static void WriteBits(List<byte> entropy, ref int bitBuffer, ref int bitCount, int value, int length)
    {
        if (length == 0)
        {
            return;
        }

        bitBuffer = (bitBuffer << length) | (value & ((1 << length) - 1));
        bitCount += length;
        while (bitCount >= 8)
        {
            bitCount -= 8;
            var b = (byte)(bitBuffer >> bitCount);
            entropy.Add(b);
            if (b == 0xFF)
            {
                entropy.Add(0x00);
            }
        }
    }

    private static void FlushWithPadding(List<byte> entropy, ref int bitBuffer, ref int bitCount)
    {
        if (bitCount > 0)
        {
            var padded = (byte)((bitBuffer << (8 - bitCount)) | ((1 << (8 - bitCount)) - 1));
            entropy.Add(padded);
            if (padded == 0xFF)
            {
                entropy.Add(0x00);
            }

            bitCount = 0;
        }
    }

    private static void WriteMarkerOnly(List<byte> bytes, byte marker)
    {
        bytes.Add(0xFF);
        bytes.Add(marker);
    }

    private static void WriteSegment(List<byte> bytes, byte marker, byte[] payload)
    {
        bytes.Add(0xFF);
        bytes.Add(marker);
        var length = payload.Length + 2;
        bytes.Add((byte)(length >> 8));
        bytes.Add((byte)length);
        bytes.AddRange(payload);
    }

    private static void WriteAdobeApp14(List<byte> bytes)
    {
        var payload = new byte[12];
        "Adobe"u8.CopyTo(payload);
        payload[5] = 100; // version
        payload[6] = 0; payload[7] = 0; // flags0
        payload[8] = 0; payload[9] = 0; // flags1
        payload[10] = 0; // padding (Adobe's marker is 6-byte version/flags then 1-byte transform at offset 11; offset 10 is the low byte of flags1, kept 0)
        payload[11] = 0; // transform = 0: no color transform (direct CMYK).
        WriteSegment(bytes, 0xEE, payload);
    }

    private static void WriteDqt(List<byte> bytes)
    {
        var payload = new byte[65];
        payload[0] = 0; // precision 0, id 0.
        for (var i = 1; i < 65; i++)
        {
            payload[i] = 1; // all-ones: JpegFdct's raw output is used as the quantized level directly.
        }

        WriteSegment(bytes, 0xDB, payload);
    }

    private static void WriteSof0(List<byte> bytes)
    {
        var payload = new byte[6 + (4 * 3)];
        payload[0] = 8; // precision.
        payload[1] = 0; payload[2] = 8; // height = 8.
        payload[3] = 0; payload[4] = 8; // width = 8.
        payload[5] = 4; // component count.
        for (var i = 0; i < 4; i++)
        {
            var offset = 6 + (i * 3);
            payload[offset] = (byte)(i + 1); // component ids 1-4 (C, M, Y, K).
            payload[offset + 1] = 0x11; // 1x1 sampling - no subsampling.
            payload[offset + 2] = 0; // quant table id 0 for every component.
        }

        WriteSegment(bytes, 0xC0, payload);
    }

    private static void WriteDht(List<byte> bytes, byte tableClass, byte id, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        var payload = new byte[17 + values.Length];
        payload[0] = (byte)((tableClass << 4) | id);
        bits[1..17].CopyTo(payload.AsSpan(1));
        values.CopyTo(payload.AsSpan(17));
        WriteSegment(bytes, 0xC4, payload);
    }

    private static void WriteSos(List<byte> bytes)
    {
        var payload = new byte[4 + (4 * 2)];
        payload[0] = 4;
        for (var i = 0; i < 4; i++)
        {
            payload[1 + (i * 2)] = (byte)(i + 1);
            payload[2 + (i * 2)] = 0x00; // DC table 0, AC table 0 for every component.
        }

        var tail = 1 + (4 * 2);
        payload[tail] = 0;
        payload[tail + 1] = 63;
        payload[tail + 2] = 0;
        WriteSegment(bytes, 0xDA, payload);
    }
}
