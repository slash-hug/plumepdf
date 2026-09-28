namespace PlumePdf.Filters;

/// <summary>
/// Baseline JPEG encode (ISO/IEC 10918-1 SOF0, Huffman-coded, single scan): IJG-style
/// quality-to-quantization scaling (<c>jpeg_quality_scaling</c>'s well-known formula,
/// Annex K.1's base tables), the Annex K.3 "typical" Huffman tables (no custom table
/// optimization - matches <c>stb_image_write.h</c>'s approach, the cited encoder reference),
/// and 4:2:0 or 4:4:4 chroma subsampling for 3-component
/// input. Not an <see cref="IPdfEncodingFilter"/> - unlike every other registered PDF
/// filter, producing a JPEG bitstream needs image shape (width, height, component count)
/// that generic byte-buffer signature has no room for; <c>RasterImage.EncodeJpeg</c>
/// is this type's intended caller, plumbing the shape through directly.
/// </summary>
internal static class JpegEncoder
{
    // Annex K.1's base ("quality 50") luminance/chrominance quantization tables, natural
    // (row-major) order - functional numeric constants reproduced identically by every
    // baseline JPEG encoder, not spec prose (the clean-room policy in AGENTS.md).
    private static readonly ushort[] BaseLuminanceQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly ushort[] BaseChrominanceQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    /// <summary>
    /// Encodes <paramref name="pixels"/> (interleaved, <paramref name="componentCount"/>
    /// bytes per pixel, row-major) as a baseline JPEG bitstream.
    /// </summary>
    /// <param name="pixels">Interleaved pixel samples: 1 byte/pixel for grayscale, 3 (RGB) for color.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="componentCount">1 (grayscale) or 3 (RGB, converted to YCbCr).</param>
    /// <param name="quality">IJG quality 1-100 (higher is better/larger).</param>
    /// <param name="subsampleChroma">When <see langword="true"/> (the default) and <paramref name="componentCount"/> is 3, encodes 4:2:0; otherwise 4:4:4. Ignored for grayscale.</param>
    /// <param name="xDpi">Horizontal resolution to record in a JFIF <c>APP0</c> marker, or <see langword="null"/> to omit an absolute density (aspect-ratio-only JFIF).</param>
    /// <param name="yDpi">Vertical resolution to record in a JFIF <c>APP0</c> marker.</param>
    public static byte[] Encode(ReadOnlySpan<byte> pixels, int width, int height, int componentCount, int quality, bool subsampleChroma = true, double? xDpi = null, double? yDpi = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (componentCount is not (1 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(componentCount), componentCount, "JpegEncoder supports only 1 (grayscale) or 3 (RGB) components.");
        }

        var clampedQuality = Math.Clamp(quality, 1, 100);
        var subsample = componentCount == 3 && subsampleChroma;
        var hMax = subsample ? 2 : 1;
        var vMax = subsample ? 2 : 1;

        var luminanceQuant = ScaleQuantTable(BaseLuminanceQuant, clampedQuality);
        var chrominanceQuant = componentCount == 3 ? ScaleQuantTable(BaseChrominanceQuant, clampedQuality) : luminanceQuant;

        var planes = BuildPlanes(pixels, width, height, componentCount, hMax, vMax, subsample);

        var writer = new MarkerWriter();
        writer.WriteMarkerOnly(0xD8); // SOI
        WriteJfif(writer, xDpi, yDpi);
        WriteDqt(writer, 0, luminanceQuant);
        if (componentCount == 3)
        {
            WriteDqt(writer, 1, chrominanceQuant);
        }

        WriteSof0(writer, width, height, componentCount, subsample);
        WriteStandardHuffmanTables(writer, componentCount);
        WriteSos(writer, componentCount);

        var bits = new EntropyBitWriter();
        EncodeScan(bits, planes, componentCount, hMax, vMax, luminanceQuant, chrominanceQuant);
        bits.FlushWithPadding();
        writer.WriteRawBytes(bits.ToArray());

        writer.WriteMarkerOnly(0xD9); // EOI
        return writer.ToArray();
    }

    private static ushort[] ScaleQuantTable(ushort[] baseTable, int quality)
    {
        // The standard IJG quality->scale-factor formula (jcparam.c jpeg_quality_scaling),
        // widely reproduced (a numeric formula, not copyrighted prose).
        var scaleFactor = quality < 50 ? 5000 / quality : 200 - (quality * 2);
        var scaled = new ushort[64];
        for (var i = 0; i < 64; i++)
        {
            var value = ((baseTable[i] * scaleFactor) + 50) / 100;
            scaled[i] = (ushort)Math.Clamp(value, 1, 255);
        }

        return scaled;
    }

    private readonly record struct ComponentPlane(byte[] Samples, int Width, int Height, int HorizontalSampling, int VerticalSampling);

    private static ComponentPlane[] BuildPlanes(ReadOnlySpan<byte> pixels, int width, int height, int componentCount, int hMax, int vMax, bool subsample)
    {
        var mcuWidth = 8 * hMax;
        var mcuHeight = 8 * vMax;
        var paddedWidth = ((width + mcuWidth - 1) / mcuWidth) * mcuWidth;
        var paddedHeight = ((height + mcuHeight - 1) / mcuHeight) * mcuHeight;

        if (componentCount == 1)
        {
            var plane = new byte[paddedWidth * paddedHeight];
            for (var y = 0; y < paddedHeight; y++)
            {
                var srcY = Math.Min(y, height - 1);
                for (var x = 0; x < paddedWidth; x++)
                {
                    var srcX = Math.Min(x, width - 1);
                    plane[(y * paddedWidth) + x] = pixels[(srcY * width) + srcX];
                }
            }

            return [new ComponentPlane(plane, paddedWidth, paddedHeight, 1, 1)];
        }

        var yPlane = new byte[paddedWidth * paddedHeight];
        var cbFull = new float[paddedWidth * paddedHeight];
        var crFull = new float[paddedWidth * paddedHeight];

        for (var y = 0; y < paddedHeight; y++)
        {
            var srcY = Math.Min(y, height - 1);
            for (var x = 0; x < paddedWidth; x++)
            {
                var srcX = Math.Min(x, width - 1);
                var offset = ((srcY * width) + srcX) * 3;
                var r = pixels[offset];
                var g = pixels[offset + 1];
                var b = pixels[offset + 2];

                var yy = (0.299f * r) + (0.587f * g) + (0.114f * b);
                var cb = 128f - (0.168736f * r) - (0.331264f * g) + (0.5f * b);
                var cr = 128f + (0.5f * r) - (0.418688f * g) - (0.081312f * b);

                var idx = (y * paddedWidth) + x;
                yPlane[idx] = ClampToByte(yy);
                cbFull[idx] = cb;
                crFull[idx] = cr;
            }
        }

        if (!subsample)
        {
            var cbPlane = new byte[paddedWidth * paddedHeight];
            var crPlane = new byte[paddedWidth * paddedHeight];
            for (var i = 0; i < cbPlane.Length; i++)
            {
                cbPlane[i] = ClampToByte(cbFull[i]);
                crPlane[i] = ClampToByte(crFull[i]);
            }

            return
            [
                new ComponentPlane(yPlane, paddedWidth, paddedHeight, 1, 1),
                new ComponentPlane(cbPlane, paddedWidth, paddedHeight, 1, 1),
                new ComponentPlane(crPlane, paddedWidth, paddedHeight, 1, 1),
            ];
        }

        var chromaWidth = paddedWidth / 2;
        var chromaHeight = paddedHeight / 2;
        var cbSub = new byte[chromaWidth * chromaHeight];
        var crSub = new byte[chromaWidth * chromaHeight];
        for (var y = 0; y < chromaHeight; y++)
        {
            for (var x = 0; x < chromaWidth; x++)
            {
                var i00 = ((2 * y * paddedWidth) + (2 * x));
                var i01 = i00 + 1;
                var i10 = i00 + paddedWidth;
                var i11 = i10 + 1;
                var cbAvg = (cbFull[i00] + cbFull[i01] + cbFull[i10] + cbFull[i11]) / 4f;
                var crAvg = (crFull[i00] + crFull[i01] + crFull[i10] + crFull[i11]) / 4f;
                var subIdx = (y * chromaWidth) + x;
                cbSub[subIdx] = ClampToByte(cbAvg);
                crSub[subIdx] = ClampToByte(crAvg);
            }
        }

        return
        [
            new ComponentPlane(yPlane, paddedWidth, paddedHeight, 2, 2),
            new ComponentPlane(cbSub, chromaWidth, chromaHeight, 1, 1),
            new ComponentPlane(crSub, chromaWidth, chromaHeight, 1, 1),
        ];
    }

    private static void EncodeScan(EntropyBitWriter bits, ComponentPlane[] planes, int componentCount, int hMax, int vMax, ushort[] luminanceQuant, ushort[] chrominanceQuant)
    {
        var mcusPerLine = planes[0].Width / (8 * hMax);
        var mcusPerColumn = planes[0].Height / (8 * vMax);
        var dcPredictors = new int[componentCount];
        Span<float> block = stackalloc float[64];
        Span<float> transformed = stackalloc float[64];
        Span<int> quantized = stackalloc int[64];

        for (var my = 0; my < mcusPerColumn; my++)
        {
            for (var mx = 0; mx < mcusPerLine; mx++)
            {
                for (var c = 0; c < componentCount; c++)
                {
                    var plane = planes[c];
                    var quant = c == 0 ? luminanceQuant : chrominanceQuant;
                    var dcTable = c == 0 ? JpegHuffmanTable.StandardLuminanceDc : JpegHuffmanTable.StandardChrominanceDc;
                    var acTable = c == 0 ? JpegHuffmanTable.StandardLuminanceAc : JpegHuffmanTable.StandardChrominanceAc;

                    for (var v = 0; v < plane.VerticalSampling; v++)
                    {
                        for (var h = 0; h < plane.HorizontalSampling; h++)
                        {
                            var originX = ((mx * plane.HorizontalSampling) + h) * 8;
                            var originY = ((my * plane.VerticalSampling) + v) * 8;
                            ExtractBlock(plane, originX, originY, block);
                            JpegFdct.Transform(block, transformed);
                            Quantize(transformed, quant, quantized);
                            EncodeBlock(bits, quantized, dcTable, acTable, ref dcPredictors[c]);
                        }
                    }
                }
            }
        }
    }

    private static void ExtractBlock(ComponentPlane plane, int originX, int originY, Span<float> block)
    {
        for (var y = 0; y < 8; y++)
        {
            var rowOffset = ((originY + y) * plane.Width) + originX;
            for (var x = 0; x < 8; x++)
            {
                block[(y * 8) + x] = plane.Samples[rowOffset + x] - 128f;
            }
        }
    }

    private static void Quantize(ReadOnlySpan<float> coefficients, ushort[] quant, Span<int> quantized)
    {
        for (var i = 0; i < 64; i++)
        {
            quantized[i] = (int)MathF.Round(coefficients[i] / quant[i]);
        }
    }

    private static void EncodeBlock(EntropyBitWriter bits, ReadOnlySpan<int> naturalOrder, JpegHuffmanTable dcTable, JpegHuffmanTable acTable, ref int dcPredictor)
    {
        var dc = naturalOrder[0];
        var diff = dc - dcPredictor;
        dcPredictor = dc;

        var (dcSize, dcBits) = MagnitudeCategory(diff);
        var (dcCode, dcLength) = dcTable.GetCode((byte)dcSize);
        bits.WriteBits(dcCode, dcLength);
        bits.WriteBits(dcBits, dcSize);

        var run = 0;
        for (var z = 1; z < 64; z++)
        {
            var value = naturalOrder[JpegHuffmanTable.ZigzagToNatural[z]];
            if (value == 0)
            {
                run++;
                continue;
            }

            while (run > 15)
            {
                var (zrlCode, zrlLength) = acTable.GetCode(0xF0);
                bits.WriteBits(zrlCode, zrlLength);
                run -= 16;
            }

            var (size, magnitudeBits) = MagnitudeCategory(value);
            var symbol = (byte)((run << 4) | size);
            var (code, length) = acTable.GetCode(symbol);
            bits.WriteBits(code, length);
            bits.WriteBits(magnitudeBits, size);
            run = 0;
        }

        if (run > 0)
        {
            var (eobCode, eobLength) = acTable.GetCode(0x00);
            bits.WriteBits(eobCode, eobLength);
        }
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

    private static byte ClampToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    private static void WriteJfif(MarkerWriter writer, double? xDpi, double? yDpi)
    {
        Span<byte> payload = stackalloc byte[14];
        "JFIF\0"u8.CopyTo(payload);
        payload[5] = 1; // version 1.
        payload[6] = 2; // .02
        if (xDpi is { } x && yDpi is { } y)
        {
            payload[7] = 1; // units: dots per inch.
            WriteUInt16(payload[8..], (ushort)Math.Clamp((int)Math.Round(x), 1, ushort.MaxValue));
            WriteUInt16(payload[10..], (ushort)Math.Clamp((int)Math.Round(y), 1, ushort.MaxValue));
        }
        else
        {
            payload[7] = 0; // units: aspect ratio only.
            WriteUInt16(payload[8..], 1);
            WriteUInt16(payload[10..], 1);
        }

        payload[12] = 0; // no thumbnail.
        payload[13] = 0;
        writer.WriteSegment(0xE0, payload);
    }

    private static void WriteDqt(MarkerWriter writer, byte id, ushort[] naturalOrderTable)
    {
        Span<byte> payload = stackalloc byte[65];
        payload[0] = id; // precision 0 (8-bit) in the high nibble, id in the low nibble.
        for (var z = 0; z < 64; z++)
        {
            payload[1 + z] = (byte)naturalOrderTable[JpegHuffmanTable.ZigzagToNatural[z]];
        }

        writer.WriteSegment(0xDB, payload);
    }

    private static void WriteSof0(MarkerWriter writer, int width, int height, int componentCount, bool subsample)
    {
        var payload = new byte[6 + (componentCount * 3)];
        payload[0] = 8; // 8-bit precision.
        WriteUInt16(payload.AsSpan(1), (ushort)height);
        WriteUInt16(payload.AsSpan(3), (ushort)width);
        payload[5] = (byte)componentCount;

        for (var c = 0; c < componentCount; c++)
        {
            var offset = 6 + (c * 3);
            payload[offset] = (byte)(c + 1); // component ids 1, 2, 3 (Y, Cb, Cr) - matches libjpeg convention (no Adobe marker needed for the default YCbCr assumption).
            var h = c == 0 && subsample ? 2 : 1;
            var v = c == 0 && subsample ? 2 : 1;
            payload[offset + 1] = (byte)((h << 4) | v);
            payload[offset + 2] = (byte)(c == 0 ? 0 : 1); // luminance uses quant table 0, chrominance table 1.
        }

        writer.WriteSegment(0xC0, payload);
    }

    private static void WriteStandardHuffmanTables(MarkerWriter writer, int componentCount)
    {
        WriteDht(writer, 0, 0, JpegHuffmanTable.StandardLuminanceDcBits, JpegHuffmanTable.StandardLuminanceDcValues);
        WriteDht(writer, 1, 0, JpegHuffmanTable.StandardLuminanceAcBits, JpegHuffmanTable.StandardLuminanceAcValues);
        if (componentCount == 3)
        {
            WriteDht(writer, 0, 1, JpegHuffmanTable.StandardChrominanceDcBits, JpegHuffmanTable.StandardChrominanceDcValues);
            WriteDht(writer, 1, 1, JpegHuffmanTable.StandardChrominanceAcBits, JpegHuffmanTable.StandardChrominanceAcValues);
        }
    }

    private static void WriteDht(MarkerWriter writer, byte tableClass, byte id, ReadOnlySpan<byte> bits, ReadOnlySpan<byte> values)
    {
        var payload = new byte[17 + values.Length];
        payload[0] = (byte)((tableClass << 4) | id);
        bits[1..17].CopyTo(payload.AsSpan(1));
        values.CopyTo(payload.AsSpan(17));
        writer.WriteSegment(0xC4, payload);
    }

    private static void WriteSos(MarkerWriter writer, int componentCount)
    {
        var payload = new byte[4 + (componentCount * 2)];
        payload[0] = (byte)componentCount;
        for (var c = 0; c < componentCount; c++)
        {
            payload[1 + (c * 2)] = (byte)(c + 1);
            payload[2 + (c * 2)] = c == 0 ? (byte)0x00 : (byte)0x11; // DC/AC table selectors: luminance tables (id 0) for Y, chrominance tables (id 1) for Cb/Cr.
        }

        var tail = 1 + (componentCount * 2);
        payload[tail] = 0; // spectral selection start.
        payload[tail + 1] = 63; // spectral selection end.
        payload[tail + 2] = 0; // successive approximation.
        writer.WriteSegment(0xDA, payload);
    }

    private static void WriteUInt16(Span<byte> destination, ushort value)
    {
        destination[0] = (byte)(value >> 8);
        destination[1] = (byte)value;
    }

    /// <summary>Assembles marker segments (SOI/EOI/APPn/DQT/SOF0/DHT/SOS) and the raw entropy-coded bytes that follow SOS, in order.</summary>
    private sealed class MarkerWriter
    {
        private readonly List<byte> _bytes = [];

        public void WriteMarkerOnly(byte marker)
        {
            _bytes.Add(0xFF);
            _bytes.Add(marker);
        }

        public void WriteSegment(byte marker, ReadOnlySpan<byte> payload)
        {
            _bytes.Add(0xFF);
            _bytes.Add(marker);
            var length = payload.Length + 2;
            _bytes.Add((byte)(length >> 8));
            _bytes.Add((byte)length);
            _bytes.AddRange(payload.ToArray());
        }

        public void WriteRawBytes(byte[] raw) => _bytes.AddRange(raw);

        public byte[] ToArray() => [.. _bytes];
    }

    /// <summary>MSB-first bit writer for entropy-coded scan data, byte-stuffing every literal <c>0xFF</c> output byte with a trailing <c>0x00</c> per §B.1.1.5.</summary>
    private sealed class EntropyBitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bitBuffer;
        private int _bitCount;

        public void WriteBits(int value, int length)
        {
            if (length == 0)
            {
                return;
            }

            _bitBuffer = (_bitBuffer << length) | (value & ((1 << length) - 1));
            _bitCount += length;

            while (_bitCount >= 8)
            {
                _bitCount -= 8;
                var b = (byte)(_bitBuffer >> _bitCount);
                _bytes.Add(b);
                if (b == 0xFF)
                {
                    _bytes.Add(0x00);
                }
            }
        }

        /// <summary>Pads the final partial byte with 1-bits (§B.1.1.5's defined padding) and flushes it.</summary>
        public void FlushWithPadding()
        {
            if (_bitCount > 0)
            {
                var padded = (byte)((_bitBuffer << (8 - _bitCount)) | ((1 << (8 - _bitCount)) - 1));
                _bytes.Add(padded);
                if (padded == 0xFF)
                {
                    _bytes.Add(0x00);
                }

                _bitCount = 0;
                _bitBuffer = 0;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
