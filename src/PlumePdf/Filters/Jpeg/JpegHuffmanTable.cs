namespace PlumePdf.Filters;

/// <summary>
/// One JPEG Huffman table (DC or AC, ISO/IEC 10918-1 §B.2.4.2/§C): the code-length counts
/// (<c>BITS</c>, index 1-16) and the symbols in code order (<c>HUFFVAL</c>) that a <c>DHT</c>
/// marker (or, for encoding, the fixed Annex K "typical" tables) carries. Builds both a
/// decode side (canonical min-code/max-code/val-ptr per §F.2.2.3) and an encode side
/// (code + length per symbol, §C.2's generate_codes/order_codes) eagerly at construction,
/// since a table is small (at most 16 lengths x 256 symbols) and reused across every block
/// in a scan. All fields are set once in the constructor and never mutated afterward
/// (the mutable-static ban): the static <see cref="StandardLuminanceDc"/>
/// family below are safe to share across every encode call for exactly that reason.
/// </summary>
internal sealed class JpegHuffmanTable
{
    // Decode side: minCode[len]/maxCode[len]/valPtr[len] for len 1-16 (index 0 unused).
    // maxCode[len] == -1 means no code of that length exists.
    private readonly int[] _minCode = new int[17];
    private readonly int[] _maxCode = new int[17];
    private readonly int[] _valPtr = new int[17];
    private readonly byte[] _values;

    // Encode side: for symbol byte b, EncodeCode[b]/EncodeLength[b] - only entries for
    // symbols actually present in HUFFVAL are meaningful; length 0 marks "not in this table".
    private readonly ushort[] _encodeCode = new ushort[256];
    private readonly byte[] _encodeLength = new byte[256];

    // Decode-side 8-bit lookahead: _lookahead[peek8] packs
    // (symbol << 4) | codeLength for every 8-bit window whose prefix is a code of length <= 8
    // (real codes never have length 0, so 0 is the "no short code matches" sentinel). Short
    // codes dominate real scans, so most symbols resolve in one probe instead of a
    // bit-at-a-time canonical walk.
    private readonly ushort[] _lookahead = new ushort[256];

    /// <summary>Builds a table from its <c>BITS</c> counts (index 1-16, index 0 ignored) and <c>HUFFVAL</c> symbol list.</summary>
    public JpegHuffmanTable(ReadOnlySpan<byte> bits, byte[] values)
    {
        _values = values;
        Array.Fill(_maxCode, -1);

        // §C.2 generate_size_table + generate_code_table: lay out one HUFFSIZE/HUFFCODE
        // entry per symbol, in the order HUFFVAL lists them (shortest codes first).
        var huffSize = new byte[values.Length];
        var huffCode = new ushort[values.Length];
        var k = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var n = 0; n < bits[length]; n++)
            {
                huffSize[k++] = (byte)length;
            }
        }

        ushort code = 0;
        var sizeIndex = 0;
        for (var length = 1; length <= 16; length++)
        {
            while (sizeIndex < huffSize.Length && huffSize[sizeIndex] == length)
            {
                huffCode[sizeIndex] = code;
                code++;
                sizeIndex++;
            }

            code <<= 1;
        }

        // Decode-side min/max/valptr per §F.2.2.3, built directly from the per-symbol codes
        // above rather than re-deriving them: valPtr[length] is the HUFFVAL index of the
        // first symbol at that length.
        var valueIndex = 0;
        for (var length = 1; length <= 16; length++)
        {
            if (bits[length] == 0)
            {
                continue;
            }

            _valPtr[length] = valueIndex;
            _minCode[length] = huffCode[valueIndex];
            _maxCode[length] = huffCode[valueIndex + bits[length] - 1];
            valueIndex += bits[length];
        }

        for (var i = 0; i < values.Length; i++)
        {
            var symbol = values[i];
            _encodeCode[symbol] = huffCode[i];
            _encodeLength[symbol] = huffSize[i];
        }

        for (var i = 0; i < values.Length; i++)
        {
            int length = huffSize[i];
            if (length > 8)
            {
                continue;
            }

            var baseEntry = huffCode[i] << (8 - length);
            var span = 1 << (8 - length);
            var packed = (ushort)((values[i] << 4) | length);
            for (var j = 0; j < span; j++)
            {
                _lookahead[baseEntry + j] = packed;
            }
        }
    }

    /// <summary>
    /// One-probe decode for the next symbol when the leading code is at most 8 bits long:
    /// <paramref name="peek8"/> is the next 8 bits of the stream (MSB-first), and a hit
    /// returns the symbol plus how many of those bits the code actually consumed. A miss
    /// means the code is longer than 8 bits — the caller continues the canonical walk.
    /// </summary>
    public bool TryLookupFast(int peek8, out int symbol, out int length)
    {
        var entry = _lookahead[peek8];
        symbol = entry >> 4;
        length = entry & 0xF;
        return entry != 0;
    }

    /// <summary>Looks up the symbol for a <paramref name="length"/>-bit <paramref name="code"/>, per the decode-side tables built at construction.</summary>
    public bool TryLookup(int length, int code, out int symbol)
    {
        if (_maxCode[length] >= 0 && code <= _maxCode[length] && code >= _minCode[length])
        {
            symbol = _values[_valPtr[length] + code - _minCode[length]];
            return true;
        }

        symbol = 0;
        return false;
    }

    /// <summary>The code and bit length to emit for <paramref name="symbol"/>, for encoding. Length 0 means the symbol has no code in this table.</summary>
    public (ushort Code, byte Length) GetCode(byte symbol) => (_encodeCode[symbol], _encodeLength[symbol]);

    /// <summary>
    /// The zigzag-to-natural index map (ISO/IEC 10918-1 Figure A.6): <c>ZigzagToNatural[z]</c>
    /// is the row-major <c>(row * 8 + col)</c> index the <c>z</c>'th coefficient
    /// in a <c>DQT</c>/entropy-coded zigzag scan belongs at. Shared by dequantization, the
    /// IDCT/FDCT block layout, and quantization-table (de)serialization - every place a JPEG
    /// block's 64 coefficients cross between "as stored in the file" and "as indexed by
    /// (row, column)".
    /// </summary>
    public static readonly int[] ZigzagToNatural =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    // The Annex K.3 "typical" Huffman tables - fixed constants reproduced identically by
    // essentially every baseline JPEG encoder (libjpeg's jcparam.c/jpeg_std_huff_tables,
    // stb_image_write.h, ...): this is functional numeric data (code-length counts and
    // symbol values), not spec prose, and no code was copied to produce it (the clean-room policy in AGENTS.md).
    private static readonly byte[] LuminanceDcBits = [0, 0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] LuminanceDcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] ChrominanceDcBits = [0, 0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] ChrominanceDcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] LuminanceAcBits = [0, 0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    private static readonly byte[] LuminanceAcValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12,
        0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08,
        0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16,
        0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
        0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6,
        0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4,
        0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA,
        0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private static readonly byte[] ChrominanceAcBits = [0, 0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] ChrominanceAcValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21,
        0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34,
        0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38,
        0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96,
        0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4,
        0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2,
        0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9,
        0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    /// <summary>The Annex K.3 standard luminance DC table, shared read-only across every baseline encode.</summary>
    public static readonly JpegHuffmanTable StandardLuminanceDc = new(LuminanceDcBits, LuminanceDcValues);

    /// <summary>The Annex K.3 standard chrominance DC table.</summary>
    public static readonly JpegHuffmanTable StandardChrominanceDc = new(ChrominanceDcBits, ChrominanceDcValues);

    /// <summary>The Annex K.3 standard luminance AC table.</summary>
    public static readonly JpegHuffmanTable StandardLuminanceAc = new(LuminanceAcBits, LuminanceAcValues);

    /// <summary>The Annex K.3 standard chrominance AC table.</summary>
    public static readonly JpegHuffmanTable StandardChrominanceAc = new(ChrominanceAcBits, ChrominanceAcValues);

    /// <summary>The raw <c>BITS</c> counts (index 0-16, index 0 always 0) for writing a <c>DHT</c> segment for the Annex K standard luminance DC table.</summary>
    public static ReadOnlySpan<byte> StandardLuminanceDcBits => LuminanceDcBits;

    /// <summary>The raw <c>HUFFVAL</c> symbol list for the Annex K standard luminance DC table.</summary>
    public static ReadOnlySpan<byte> StandardLuminanceDcValues => LuminanceDcValues;

    /// <summary>The raw <c>BITS</c> counts for the Annex K standard chrominance DC table.</summary>
    public static ReadOnlySpan<byte> StandardChrominanceDcBits => ChrominanceDcBits;

    /// <summary>The raw <c>HUFFVAL</c> symbol list for the Annex K standard chrominance DC table.</summary>
    public static ReadOnlySpan<byte> StandardChrominanceDcValues => ChrominanceDcValues;

    /// <summary>The raw <c>BITS</c> counts for the Annex K standard luminance AC table.</summary>
    public static ReadOnlySpan<byte> StandardLuminanceAcBits => LuminanceAcBits;

    /// <summary>The raw <c>HUFFVAL</c> symbol list for the Annex K standard luminance AC table.</summary>
    public static ReadOnlySpan<byte> StandardLuminanceAcValues => LuminanceAcValues;

    /// <summary>The raw <c>BITS</c> counts for the Annex K standard chrominance AC table.</summary>
    public static ReadOnlySpan<byte> StandardChrominanceAcBits => ChrominanceAcBits;

    /// <summary>The raw <c>HUFFVAL</c> symbol list for the Annex K standard chrominance AC table.</summary>
    public static ReadOnlySpan<byte> StandardChrominanceAcValues => ChrominanceAcValues;
}
