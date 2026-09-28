using System.Buffers.Binary;
using PlumePdf.Fonts.Outlines;
using PlumePdf.Fonts.Tables;

namespace PlumePdf.Fonts;

/// <summary>
/// Resource limits for parsing an untrusted SFNT (TrueType/OpenType) font file — the Fonts
/// layer's own analogue of <see cref="PdfOptions"/>'s reading limits (threaded from
/// the first commit, never retrofitted). <see cref="PdfOptions"/> is the single source of
/// truth for the values (<see cref="PdfOptions.MaxFontFileBytes"/>,
/// <see cref="PdfOptions.MaxFontGlyphCount"/>, <see cref="PdfOptions.MaxCompositeGlyphDepth"/>,
/// <see cref="PdfOptions.MaxSubsetGlyphs"/>, <see cref="PdfOptions.MaxShapingLookupApplications"/>);
/// <see cref="From"/> derives one of these from a <see cref="PdfOptions"/>
/// instance at the <see cref="PdfFont"/> boundary, and every downstream Try-form in this
/// namespace threads this narrower record rather than <see cref="PdfOptions"/> itself, since
/// font-table parsing has no use for the read/write options a document-level call carries.
/// </summary>
internal readonly record struct FontReadLimits(
    long MaxFontFileBytes,
    int MaxFontGlyphCount,
    int MaxCompositeGlyphDepth,
    int MaxSubsetGlyphs,
    int MaxShapingLookupApplications)
{
    /// <summary>Derives a <see cref="FontReadLimits"/> from a <see cref="PdfOptions"/> instance's five font-parsing/shaping limits.</summary>
    public static FontReadLimits From(PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new FontReadLimits(
            MaxFontFileBytes: options.MaxFontFileBytes,
            MaxFontGlyphCount: options.MaxFontGlyphCount,
            MaxCompositeGlyphDepth: options.MaxCompositeGlyphDepth,
            MaxSubsetGlyphs: options.MaxSubsetGlyphs,
            MaxShapingLookupApplications: options.MaxShapingLookupApplications);
    }

    /// <summary>The limits <see cref="PdfOptions.Default"/> implies — the fallback used when a caller (or an internal-only test) parses a font without threading options explicitly.</summary>
    public static FontReadLimits Default { get; } = From(PdfOptions.Default);
}

/// <summary>
/// Bounds-checked big-endian primitive readers over a font byte span. Every method returns
/// <see langword="false"/> instead of throwing when the requested field would run past the
/// end of the source span — the SFNT analogue of <see cref="PdfNumber"/>'s Try-forms:
/// table offsets, lengths, and counts inside a font file are entirely attacker-controlled in
/// a server scenario, so no bare checked read may run on them.
/// </summary>
internal static class SfntPrimitives
{
    public static bool TryReadUInt8(ReadOnlySpan<byte> data, int offset, out byte value)
    {
        if (offset < 0 || (long)offset + 1 > data.Length)
        {
            value = 0;
            return false;
        }

        value = data[offset];
        return true;
    }

    public static bool TryReadUInt16(ReadOnlySpan<byte> data, int offset, out ushort value)
    {
        if (offset < 0 || (long)offset + 2 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        return true;
    }

    public static bool TryReadInt16(ReadOnlySpan<byte> data, int offset, out short value)
    {
        if (offset < 0 || (long)offset + 2 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2));
        return true;
    }

    public static bool TryReadUInt32(ReadOnlySpan<byte> data, int offset, out uint value)
    {
        if (offset < 0 || (long)offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        return true;
    }

    public static bool TryReadInt32(ReadOnlySpan<byte> data, int offset, out int value)
    {
        if (offset < 0 || (long)offset + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
        return true;
    }

    /// <summary>Reads a 4-byte ASCII table/script/feature tag. Non-printable bytes are kept verbatim (tags are opaque identifiers, not validated text).</summary>
    public static bool TryReadTag(ReadOnlySpan<byte> data, int offset, out string tag)
    {
        if (offset < 0 || (long)offset + 4 > data.Length)
        {
            tag = string.Empty;
            return false;
        }

        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < 4; i++)
        {
            chars[i] = (char)data[offset + i];
        }

        tag = new string(chars);
        return true;
    }

    /// <summary>Reads a 16.16 fixed-point value (OpenType <c>Fixed</c>) as a <see cref="double"/>.</summary>
    public static bool TryReadFixed(ReadOnlySpan<byte> data, int offset, out double value)
    {
        if (!TryReadInt32(data, offset, out var raw))
        {
            value = 0;
            return false;
        }

        value = raw / 65536.0;
        return true;
    }

    /// <summary>Reads a 2.14 fixed-point value (OpenType <c>F2Dot14</c>, used by composite-glyph scales) as a <see cref="double"/>.</summary>
    public static bool TryReadF2Dot14(ReadOnlySpan<byte> data, int offset, out double value)
    {
        if (!TryReadInt16(data, offset, out var raw))
        {
            value = 0;
            return false;
        }

        value = raw / 16384.0;
        return true;
    }
}

/// <summary>One entry in an SFNT table directory: a table's tag and its byte extent within the font file, already validated to lie inside the file.</summary>
internal readonly record struct SfntTableRecord(string Tag, int Offset, int Length);

/// <summary>
/// A parsed SFNT (TrueType/OpenType) font file's table directory — the shared entry point
/// every table parser in <c>PlumePdf.Fonts.Tables</c> starts from. Ported from the OpenType
/// specification's own public description of the table-directory layout (ISO/IEC
/// 14496-22 / the freely published OpenType spec), not from any other library's source
/// (the clean-room policy in AGENTS.md).
/// </summary>
internal sealed class SfntFont
{
    private readonly Dictionary<string, SfntTableRecord> _tables;
    private GlyfTable? _lazyRenderGlyf;
    private LocaTable? _lazyRenderLoca;
    private CffParser? _lazyRenderCff;

    private SfntFont(byte[] data, string version, Dictionary<string, SfntTableRecord> tables)
    {
        Data = data;
        Version = version;
        _tables = tables;
    }

    /// <summary>The font file's full bytes.</summary>
    public byte[] Data { get; }

    /// <summary>The 4-byte sfntVersion tag: decodes to bytes 0x00 0x01 0x00 0x00 for TrueType, or the ASCII text <c>"OTTO"</c> for CFF-flavored OpenType.</summary>
    public string Version { get; }

    /// <summary>Every table present, by tag.</summary>
    public IReadOnlyDictionary<string, SfntTableRecord> Tables => _tables;

    /// <summary>Returns the bytes of table <paramref name="tag"/>, or <see langword="false"/> if it isn't present.</summary>
    public bool TryGetTable(string tag, out ReadOnlyMemory<byte> table)
    {
        if (_tables.TryGetValue(tag, out var record))
        {
            table = Data.AsMemory(record.Offset, record.Length);
            return true;
        }

        table = default;
        return false;
    }

    /// <summary>Returns the bytes of table <paramref name="tag"/>, throwing <c>PLUME8003</c> if it isn't present.</summary>
    public ReadOnlyMemory<byte> GetRequiredTable(string tag)
    {
        if (TryGetTable(tag, out var table))
        {
            return table;
        }

        throw new PlumePdfException("PLUME8003", $"Font is missing the required '{tag}' table.");
    }

    /// <summary>
    /// Parses an SFNT font file's table directory, bounds-checking every offset and length
    /// against <paramref name="limits"/> and the file's own size before trusting it.
    /// </summary>
    /// <param name="data">The raw font file bytes.</param>
    /// <param name="limits">Resource limits guarding against a hostile or corrupt file.</param>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME8001</c> the file exceeds <see cref="FontReadLimits.MaxFontFileBytes"/>;
    /// <c>PLUME8002</c> the file is too short to hold a table directory of the size it declares;
    /// <c>PLUME8004</c> a table's offset+length runs past the end of the file;
    /// <c>PLUME8006</c> the file is a CFF-flavored ('OTTO') font with no 'glyf' table (unsupported — Phase 2 embeds TrueType outlines only).
    /// </exception>
    public static SfntFont Parse(byte[] data, FontReadLimits limits) => ParseCore(data, limits, allowCffOnly: false);

    /// <summary>
    /// Parses <paramref name="data"/> the same way as <see cref="Parse"/>, except a CFF-flavored
    /// ('OTTO') font with no 'glyf' table is accepted instead of throwing <c>PLUME8006</c>. This
    /// is the render-path entry point for Phase 8: outline rasterization reads the
    /// font's <c>'CFF '</c> table directly via <see cref="GetGlyphOutline"/>, so it has no need
    /// for the TrueType-only embed/subset write path's refusal — which stays exactly as-is on
    /// <see cref="Parse"/> for every other caller (<see cref="TrueTypeFontProgram"/> included).
    /// </summary>
    /// <exception cref="PlumePdfException">Same codes as <see cref="Parse"/>, minus <c>PLUME8006</c>.</exception>
    public static SfntFont ParseForRender(byte[] data, FontReadLimits limits) => ParseCore(data, limits, allowCffOnly: true);

    private static SfntFont ParseCore(byte[] data, FontReadLimits limits, bool allowCffOnly)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.LongLength > limits.MaxFontFileBytes)
        {
            throw new PlumePdfException("PLUME8001", $"Font file is {data.LongLength} bytes, exceeding the configured limit of {limits.MaxFontFileBytes} bytes.");
        }

        var span = data.AsSpan();
        if (!SfntPrimitives.TryReadTag(span, 0, out var version) || !SfntPrimitives.TryReadUInt16(span, 4, out var numTables))
        {
            throw new PlumePdfException("PLUME8002", "Font file is too short to contain an SFNT header.");
        }

        // Table directory: 12-byte header + numTables * 16-byte records. numTables is
        // attacker-controlled (up to 65535) — compute the required size in a wider type so a
        // hostile count can't wrap a 32-bit multiply before the bounds check below catches it.
        var directorySize = 12L + (16L * numTables);
        if (directorySize > data.LongLength)
        {
            throw new PlumePdfException("PLUME8002", $"Font declares {numTables} tables, but the table directory ({directorySize} bytes) runs past the end of the {data.LongLength}-byte file.");
        }

        var tables = new Dictionary<string, SfntTableRecord>(numTables, StringComparer.Ordinal);
        for (var i = 0; i < numTables; i++)
        {
            var recordOffset = 12 + (i * 16);
            if (!SfntPrimitives.TryReadTag(span, recordOffset, out var tag)
                || !SfntPrimitives.TryReadUInt32(span, recordOffset + 8, out var tableOffset)
                || !SfntPrimitives.TryReadUInt32(span, recordOffset + 12, out var tableLength))
            {
                throw new PlumePdfException("PLUME8002", "Font's table directory is truncated mid-record.");
            }

            // Widen to long before adding: tableOffset/tableLength are each already full
            // uint32 range, and their sum must not wrap a 32-bit accumulator.
            var end = (long)tableOffset + tableLength;
            if (end > data.LongLength)
            {
                throw new PlumePdfException("PLUME8004", $"Font table '{tag}' (offset {tableOffset}, length {tableLength}) runs past the end of the {data.LongLength}-byte file.");
            }

            // Last directory entry for a repeated tag wins — matches how real rasterizers
            // resolve a (spec-forbidden but seen in the wild) duplicate table tag.
            tables[tag] = new SfntTableRecord(tag, (int)tableOffset, (int)tableLength);
        }

        if (version == "OTTO" && !tables.ContainsKey("glyf") && !allowCffOnly)
        {
            throw new PlumePdfException("PLUME8006", "Font is a CFF-flavored OpenType font ('OTTO', no 'glyf' table present). Phase 2 embeds TrueType outlines only — CFF/Type1 outline support is out of scope for this phase.");
        }

        return new SfntFont(data, version, tables);
    }

    /// <summary>
    /// Decodes glyph <paramref name="glyphId"/>'s render-path outline — dispatching to the
    /// <c>'CFF '</c> table (via <see cref="CffParser"/>) for a CFF-flavored ('OTTO') font, or to
    /// <c>'glyf'</c>/<c>'loca'</c> (via <c>GlyfTable</c>) for a TrueType one — per the Fonts-
    /// layer parsing / Raster-layer rasterization split. Built lazily and cached on first call
    /// (the underlying table parse is amortized across every glyph the caller decodes from this
    /// font). Only <see cref="SfntFont"/> instances obtained from <see cref="ParseForRender"/>
    /// can reach a CFF dispatch without ever having thrown <c>PLUME8006</c> first; an instance
    /// from the ordinary <see cref="Parse"/> reaching this method is, by construction, always
    /// TrueType.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME8003</c>: the font has neither a <c>'CFF '</c> nor a <c>'glyf'</c>/<c>'loca'</c>/<c>'head'</c>/<c>'maxp'</c> table set to decode from.</exception>
    public GlyphOutline GetGlyphOutline(int glyphId, FontReadLimits limits)
    {
        if (Version == "OTTO" && TryGetTable("CFF ", out var cffTable))
        {
            _lazyRenderCff ??= CffParser.Parse(cffTable, limits);
            return _lazyRenderCff.GetGlyphOutline(glyphId);
        }

        if (_lazyRenderGlyf is null)
        {
            if (!_tables.TryGetValue("glyf", out var glyfRecord)
                || !TryGetTable("loca", out var locaTable) || !TryGetTable("head", out var headTable) || !TryGetTable("maxp", out var maxpTable))
            {
                throw new PlumePdfException("PLUME8003", "Font has neither a 'CFF ' table nor the 'glyf'/'loca'/'head'/'maxp' set needed to decode outlines.");
            }

            var head = HeadTable.Parse(headTable);
            var maxp = MaxpTable.Parse(maxpTable, limits);
            _lazyRenderLoca = LocaTable.Parse(locaTable, maxp.NumGlyphs, head.IndexToLocFormat != 0);
            _lazyRenderGlyf = GlyfTable.Wrap(Data, glyfRecord.Offset, glyfRecord.Length, _lazyRenderLoca);
        }

        return _lazyRenderGlyf.BuildOutline(glyphId, limits);
    }
}
