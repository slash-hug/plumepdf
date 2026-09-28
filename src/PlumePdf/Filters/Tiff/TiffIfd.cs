namespace PlumePdf.Filters.Tiff;

/// <summary>The TIFF field type codes (TIFF 6.0 §2, "Field Types") this reader recognizes when decoding an IFD entry's value.</summary>
internal enum TiffFieldType
{
    Byte = 1,
    Ascii = 2,
    Short = 3,
    Long = 4,
    Rational = 5,
    SByte = 6,
    Undefined = 7,
    SShort = 8,
    SLong = 9,
    SRational = 10,
    Float = 11,
    Double = 12,
}

/// <summary>
/// One parsed IFD ("Image File Directory") entry: a TIFF tag's type and its values, already
/// widened to <see langword="long"/> (integer types) or <see langword="double"/> (rational
/// and float types) regardless of the on-disk width - a caller never needs to know whether
/// <c>/ImageWidth</c> was stored as a SHORT or a LONG.
/// </summary>
internal sealed class TiffEntry(ushort tag, TiffFieldType type, long[] integers, double[] reals, byte[] raw)
{
    public ushort Tag { get; } = tag;

    public TiffFieldType Type { get; } = type;

    /// <summary>The entry's values as integers - populated for BYTE/SHORT/LONG/SBYTE/SSHORT/SLONG; empty otherwise.</summary>
    public long[] Integers { get; } = integers;

    /// <summary>The entry's values as doubles - populated for RATIONAL/SRATIONAL/FLOAT/DOUBLE; empty otherwise.</summary>
    public double[] Reals { get; } = reals;

    /// <summary>The entry's raw bytes exactly as stored (ASCII/UNDEFINED, and every other type too, for callers that want the original encoding).</summary>
    public byte[] Raw { get; } = raw;

    public int Count => Type is TiffFieldType.Rational or TiffFieldType.SRational or TiffFieldType.Float or TiffFieldType.Double ? Reals.Length : Integers.Length;
}

/// <summary>
/// One parsed TIFF Image File Directory: a page/frame's tag entries plus the file offset of
/// the next IFD (0 = last frame). <see cref="TiffFrameDecoder"/> reads pixel data using
/// these entries and the original file bytes (strip/tile offsets point back into the file).
/// </summary>
internal sealed class TiffIfd(long offset, IReadOnlyDictionary<ushort, TiffEntry> entries, long nextIfdOffset)
{
    /// <summary>The byte offset this IFD itself was read from (used for the cycle guard in <see cref="TiffReader"/>).</summary>
    public long Offset { get; } = offset;

    public IReadOnlyDictionary<ushort, TiffEntry> Entries { get; } = entries;

    /// <summary>The file offset of the next IFD in the chain, or 0 if this is the last one.</summary>
    public long NextIfdOffset { get; } = nextIfdOffset;

    public bool TryGet(ushort tag, out TiffEntry entry) => Entries.TryGetValue(tag, out entry!);

    /// <summary>Reads a single-valued integer tag, or <paramref name="fallback"/> if absent/wrong type.</summary>
    public long GetInt(ushort tag, long fallback) =>
        TryGet(tag, out var entry) && entry.Integers.Length > 0 ? entry.Integers[0] : fallback;

    /// <summary>Reads an integer-array tag (e.g. <c>StripOffsets</c>), or an empty array if absent.</summary>
    public long[] GetInts(ushort tag) => TryGet(tag, out var entry) ? entry.Integers : [];

    public double GetRational(ushort tag, double fallback) =>
        TryGet(tag, out var entry) && entry.Reals.Length > 0 ? entry.Reals[0] : fallback;

    public bool Has(ushort tag) => Entries.ContainsKey(tag);
}

/// <summary>The well-known baseline/extension TIFF tag numbers this reader consumes (TIFF 6.0 §3 and the CCITT/Predictor/PackBits/LZW extension notes).</summary>
internal static class TiffTag
{
    public const ushort NewSubfileType = 254;
    public const ushort ImageWidth = 256;
    public const ushort ImageLength = 257;
    public const ushort BitsPerSample = 258;
    public const ushort Compression = 259;
    public const ushort PhotometricInterpretation = 262;
    public const ushort FillOrder = 266;
    public const ushort StripOffsets = 273;
    public const ushort SamplesPerPixel = 277;
    public const ushort RowsPerStrip = 278;
    public const ushort StripByteCounts = 279;
    public const ushort XResolution = 282;
    public const ushort YResolution = 283;
    public const ushort PlanarConfiguration = 284;
    public const ushort ResolutionUnit = 296;
    public const ushort T4Options = 292;
    public const ushort T6Options = 293;
    public const ushort Predictor = 317;
    public const ushort ColorMap = 320;
    public const ushort TileWidth = 322;
    public const ushort TileLength = 323;
    public const ushort TileOffsets = 324;
    public const ushort TileByteCounts = 325;
    public const ushort ExtraSamples = 338;
}

/// <summary>TIFF's Compression tag values (TIFF 6.0 §3 "Baseline Fields" + extension notes) this reader dispatches on.</summary>
internal static class TiffCompression
{
    public const long None = 1;
    public const long CcittMh = 2; // "CCITT Group 3, 1-Dimensional Modified Huffman" - always K=0.
    public const long CcittG3 = 3;
    public const long CcittG4 = 4;
    public const long Lzw = 5;
    public const long OldJpeg = 6;
    public const long NewJpeg = 7;
    public const long Deflate = 8; // Adobe's registered value; 32946 is the older PKZIP-era alias also seen in the wild.
    public const long DeflateAdobeAlias = 32946;
    public const long PackBits = 32773;
}

/// <summary>TIFF's PhotometricInterpretation tag values (TIFF 6.0 §3) this reader recognizes.</summary>
internal static class TiffPhotometric
{
    public const long WhiteIsZero = 0;
    public const long BlackIsZero = 1;
    public const long Rgb = 2;
    public const long Palette = 3;
    public const long TransparencyMask = 4;
    public const long Cmyk = 5;
    public const long YCbCr = 6;
}
