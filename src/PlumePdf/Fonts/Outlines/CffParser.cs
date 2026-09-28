using System.Buffers.Binary;
using PlumePdf.Fonts.Reading;
using PlumePdf.Fonts.Tables;

namespace PlumePdf.Fonts.Outlines;

/// <summary>
/// A render-path CFF (Compact Font Format, Adobe TN #5176) / Type 2 charstring (TN #5177)
/// outline interpreter — the CFF-flavored ('OTTO') counterpart to <c>GlyfTable</c>'s TrueType
/// outline decode: this adds render-path outline support and does
/// <b>not</b> retire <c>PLUME8006</c>, which still guards CFF-OTF embedding/subsetting in the
/// Fonts write path (<c>SfntFont.Parse</c>). Structurally ported from Mozilla pdf.js's
/// <c>cff_parser.js</c> / <c>font_renderer.js</c> (Apache License 2.0) — the DICT/INDEX layout
/// and the Type2 charstring operator semantics are Adobe's published technical notes, expressed
/// here independently against pdf.js's field ordering ("field-by-field vs. pinned source"
/// discipline), not copied text. Glyph names, built-in encoding, <c>FontMatrix</c>,
/// <c>CharstringType</c>, and seac-composition support are computed
/// eagerly in <see cref="Parse"/> — including the built-in <c>Encoding</c> table and, for a
/// CID-keyed program, the inverted CID→GID map — so the parser stays immutable after
/// construction. Deferring either of those two to first access was tried and reverted: an
/// unsynchronized lazy cache is only safe while no <see cref="CffParser"/> instance is ever
/// shared across threads, and this class sits behind a process-wide, multi-thread
/// <c>RenderFontFactory</c> cache keyed by face — a precondition this class cannot itself
/// enforce or verify. Eager construction keeps the safety argument independent of how the
/// caller chooses to cache instances.
/// </summary>
internal sealed class CffParser
{
    private const int MaxOperandStack = 48; // Type 2 Charstring Format §2, "the Type 2 charstring operand stack has a max of 48".
    private const int MaxSubrDepth = 60; // No spec-mandated cap; bounds runaway/hostile subroutine recursion (a caps-before-work discipline).
    private const int MaxSeacDepth = 1; // Adobe TN #5177 Appendix C: a seac component may not itself be a composite — recurse once, never twice.

    private readonly ReadOnlyMemory<byte> _data;
    private readonly List<(int Start, int Length)> _charStrings;
    private readonly List<(int Start, int Length)> _globalSubrs;
    private readonly List<(int Start, int Length)> _defaultLocalSubrs;
    private readonly int[]? _fdSelect; // gid -> FD index, CID-keyed fonts only.
    private readonly List<List<(int Start, int Length)>>? _fdLocalSubrs; // one local-subr INDEX per FD, CID-keyed fonts only.
    private readonly bool _isCidKeyed; // Top DICT carries ROS (12 30).
    private readonly FontReadLimits _limits;
    private readonly Dictionary<string, int>? _nameToGid; // non-null only for a non-CID, non-Expert charset.
    private readonly (string GlyphName, int Unicode)[]? _builtInEncoding; // null for CID-keyed/Expert/nameless programs — computed eagerly in Parse.
    private readonly int[]? _cidToGid; // CID -> GID, CID-keyed fonts only — computed eagerly in Parse.
    private readonly double _unitsPerEm;
    private readonly bool _isSupportedForNameKeyedRendering;

    private CffParser(
        ReadOnlyMemory<byte> data,
        List<(int Start, int Length)> charStrings,
        List<(int Start, int Length)> globalSubrs,
        List<(int Start, int Length)> defaultLocalSubrs,
        int[]? fdSelect,
        List<List<(int Start, int Length)>>? fdLocalSubrs,
        bool isCidKeyed,
        FontReadLimits limits,
        Dictionary<string, int>? nameToGid,
        (string GlyphName, int Unicode)[]? builtInEncoding,
        int[]? cidToGid,
        double unitsPerEm,
        bool isSupportedForNameKeyedRendering)
    {
        _data = data;
        _charStrings = charStrings;
        _globalSubrs = globalSubrs;
        _defaultLocalSubrs = defaultLocalSubrs;
        _fdSelect = fdSelect;
        _fdLocalSubrs = fdLocalSubrs;
        _isCidKeyed = isCidKeyed;
        _limits = limits;
        _nameToGid = nameToGid;
        _builtInEncoding = builtInEncoding;
        _cidToGid = cidToGid;
        _unitsPerEm = unitsPerEm;
        _isSupportedForNameKeyedRendering = isSupportedForNameKeyedRendering;
    }

    /// <summary>The number of glyphs (CharStrings INDEX entries) this CFF program defines.</summary>
    public int GlyphCount => _charStrings.Count;

    // ---- Glyph-name / encoding / metrics contract ("Interface contracts"). ----

    /// <summary>Whether the program is CID-keyed (its Top DICT carries a <c>ROS</c> operator) — its charset then maps GID → CID rather than GID → glyph name.</summary>
    public bool IsCidKeyed => _isCidKeyed;

    /// <summary>Whether a readable non-CID charset produced a glyph-name → GID table (<see langword="false"/> for CID-keyed programs and Expert/ExpertSubset charsets).</summary>
    public bool HasGlyphNames => _nameToGid is not null;

    /// <summary>Looks up a glyph by its charset name (e.g. <c>"alpha"</c>, <c>"a28"</c>); <see langword="false"/> when the program has no glyph names or the name is absent.</summary>
    /// <param name="glyphName">The PostScript glyph name to resolve.</param>
    /// <param name="gid">Receives the glyph index on success.</param>
    public bool TryGetGlyphId(string glyphName, out int gid)
    {
        if (_nameToGid is not null && _nameToGid.TryGetValue(glyphName, out gid))
        {
            return true;
        }

        gid = 0;
        return false;
    }

    /// <summary>
    /// The program's built-in encoding (Top DICT <c>Encoding</c>, predefined Standard or a custom
    /// format 0/1 table with supplements) as a 256-entry table shaped like an
    /// <see cref="Reading.EncodingResolver"/> table; <see langword="null"/> for CID-keyed
    /// programs, Expert encodings, and programs without glyph names. Computed eagerly in
    /// <see cref="Parse"/> (see class remarks).
    /// </summary>
    public (string GlyphName, int Unicode)[]? BuiltInEncoding => _builtInEncoding;

    /// <summary>Glyph-space units per em derived from the Top DICT <c>FontMatrix</c> (<c>1/s</c> for a uniform diagonal <c>[s 0 0 s 0 0]</c>); 1000 when absent or non-uniform.</summary>
    public double UnitsPerEm => _unitsPerEm;

    /// <summary>
    /// For a CID-keyed program, the charset inverted into CID → GID (unmapped CIDs → 0);
    /// <see langword="null"/> for non-CID programs. Computed eagerly in <see cref="Parse"/>
    /// (see class remarks).
    /// </summary>
    public int[]? CidToGid => _cidToGid;

    /// <summary>Whether a simple (non-CID) program can be rendered by glyph name: it has glyph names, its charstrings are Type 2, and its <c>FontMatrix</c> is uniform (or absent).</summary>
    public bool IsSupportedForNameKeyedRendering => _isSupportedForNameKeyedRendering;

    /// <summary>
    /// Parses a bare CFF table's bytes (the <c>'CFF '</c> table of an OTTO-flavored SFNT font,
    /// or a standalone <c>FontFile3</c>/Type1C program). Reads the CharStrings INDEX, the Global
    /// Subr INDEX, the Top DICT's Private DICT/Local Subrs (flat, or per-FD for CID-keyed CFF via
    /// FDArray/FDSelect — Adobe TN #5176 §18/19), the charset/String INDEX (glyph names for a
    /// non-CID program), <c>FontMatrix</c>, <c>CharstringType</c>, the built-in <c>Encoding</c>
    /// table (<see cref="BuiltInEncoding"/>), and, for a CID-keyed program, the CID→GID inversion
    /// (<see cref="CidToGid"/>) — all eagerly, so the parser is immutable afterward and safe to
    /// share across threads (a lazy variant was tried and reverted; see class remarks).
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME8014</c>: the CFF table is truncated or structurally malformed.</exception>
    public static CffParser Parse(ReadOnlyMemory<byte> cffTable, FontReadLimits limits)
    {
        var span = cffTable.Span;
        if (span.Length < 4)
        {
            throw new PlumePdfException("PLUME8014", "CFF table is too short to contain a header.");
        }

        var hdrSize = span[2];
        var pos = (int)hdrSize;

        pos = ParseIndex(span, pos, out _); // Name INDEX — unused for render-path outline extraction.
        pos = ParseIndex(span, pos, out var topDictEntries);
        pos = ParseIndex(span, pos, out var stringIndex);
        ParseIndex(span, pos, out var globalSubrs);

        if (topDictEntries.Count == 0)
        {
            throw new PlumePdfException("PLUME8014", "CFF table's Top DICT INDEX is empty.");
        }

        var topDict = ParseDict(span, topDictEntries[0]);

        if (!topDict.TryGetValue(17, out var charStringsOp) || charStringsOp.Length == 0)
        {
            throw new PlumePdfException("PLUME8014", "CFF Top DICT has no CharStrings offset (operator 17).");
        }

        ParseIndex(span, (int)charStringsOp[0], out var charStrings);

        if (charStrings.Count > limits.MaxFontGlyphCount)
        {
            throw new PlumePdfException("PLUME8005", $"CFF CharStrings INDEX declares {charStrings.Count} glyphs, exceeding the configured limit of {limits.MaxFontGlyphCount}.");
        }

        List<(int Start, int Length)> defaultLocalSubrs = [];
        if (topDict.TryGetValue(18, out var privateOp) && privateOp.Length == 2)
        {
            var privSize = (int)privateOp[0];
            var privOffset = (int)privateOp[1];
            defaultLocalSubrs = ParseLocalSubrs(span, privOffset, privSize);
        }

        int[]? fdSelect = null;
        List<List<(int Start, int Length)>>? fdLocalSubrs = null;
        var isCidKeyed = topDict.ContainsKey(1230); // operator 12 30 = ROS.

        if (isCidKeyed && topDict.TryGetValue(1236, out var fdArrayOp) && fdArrayOp.Length == 1) // 12 36 = FDArray.
        {
            ParseIndex(span, (int)fdArrayOp[0], out var fdEntries);
            fdLocalSubrs = [];
            foreach (var fdEntry in fdEntries)
            {
                var fdDict = ParseDict(span, fdEntry);
                List<(int Start, int Length)> subrs = [];
                if (fdDict.TryGetValue(18, out var fdPrivateOp) && fdPrivateOp.Length == 2)
                {
                    subrs = ParseLocalSubrs(span, (int)fdPrivateOp[1], (int)fdPrivateOp[0]);
                }

                fdLocalSubrs.Add(subrs);
            }
        }

        if (isCidKeyed && topDict.TryGetValue(1237, out var fdSelectOp) && fdSelectOp.Length == 1) // 12 37 = FDSelect.
        {
            fdSelect = ParseFdSelect(span, (int)fdSelectOp[0], charStrings.Count);
        }

        var charsetValues = ParseCharset(span, topDict, charStrings.Count, isCidKeyed, out var isExpertCharset);

        Dictionary<string, int>? nameToGid = null;
        string?[]? gidNames = null;

        if (!isCidKeyed && !isExpertCharset)
        {
            gidNames = new string?[charStrings.Count];
            nameToGid = new Dictionary<string, int>(charStrings.Count, StringComparer.Ordinal);
            for (var gid = 0; gid < charsetValues.Length; gid++)
            {
                var sid = charsetValues[gid];
                if (sid < 0)
                {
                    continue;
                }

                if (!TryGetString(span, stringIndex, sid, out var name) || name.Length == 0)
                {
                    continue;
                }

                gidNames[gid] = name;
                nameToGid.TryAdd(name, gid); // The first charset entry to claim a name wins.
            }
        }

        // isExpertCharset (non-CID): no glyph names, and therefore no built-in-encoding table
        // either (Encoding format 0/1 resolves codes to glyph names via the charset, which this
        // program does not expose) — nameToGid/gidNames both stay null.
        var builtInEncoding = gidNames is not null ? ParseEncoding(span, topDict, stringIndex, gidNames) : null;
        var cidToGid = isCidKeyed ? InvertCidCharset(charsetValues) : null;
        var (unitsPerEm, uniformMatrix) = ParseFontMatrix(topDict);
        var charstringType2 = IsCharstringType2(topDict);
        var isSupportedForNameKeyedRendering = !isCidKeyed && nameToGid is not null && uniformMatrix && charstringType2;

        return new CffParser(
            cffTable,
            charStrings,
            globalSubrs,
            defaultLocalSubrs,
            fdSelect,
            fdLocalSubrs,
            isCidKeyed,
            limits,
            nameToGid,
            builtInEncoding,
            cidToGid,
            unitsPerEm,
            isSupportedForNameKeyedRendering);
    }

    /// <summary>
    /// Decodes glyph <paramref name="gid"/>'s outline by interpreting its Type 2 charstring.
    /// Returns <see cref="GlyphOutline.Empty"/> for an out-of-range or empty (e.g. space/.notdef)
    /// glyph. When the program has glyph names and the charstring ends in a 4- or 5-operand
    /// (width-bearing) <c>endchar</c> — the deprecated seac-like accent composition, Adobe TN
    /// #5177 Appendix C — the base and accent glyphs (resolved via <c>StandardEncoding</c> codes)
    /// are decoded in fresh interpreters and appended, the accent translated by <c>(adx, ady)</c>;
    /// a component may not itself be composite, and an unresolvable base/accent name emits
    /// nothing beyond the composite glyph's own (typically empty) outline.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME8014</c>: the charstring is malformed, overflows the operand stack, or nests subroutine calls too deeply.</exception>
    public GlyphOutline GetGlyphOutline(int gid) => GetGlyphOutlineCore(gid, seacDepth: 0);

    private GlyphOutline GetGlyphOutlineCore(int gid, int seacDepth)
    {
        if (gid < 0 || gid >= _charStrings.Count || _charStrings[gid].Length == 0)
        {
            return GlyphOutline.Empty;
        }

        var localSubrs = _defaultLocalSubrs;
        if (_fdSelect is not null && _fdLocalSubrs is not null && gid < _fdSelect.Length)
        {
            var fd = _fdSelect[gid];
            if (fd >= 0 && fd < _fdLocalSubrs.Count)
            {
                localSubrs = _fdLocalSubrs[fd];
            }
        }

        var interpreter = new Type2Interpreter(_globalSubrs, localSubrs, this, seacDepth);
        interpreter.Run(_data.Span, _charStrings[gid]);
        return interpreter.Builder.Build();
    }

    // ---- CFF INDEX / DICT structures (Adobe TN #5176 §5/§4) -----------------------------

    private static int ParseIndex(ReadOnlySpan<byte> span, int pos, out List<(int Start, int Length)> entries)
    {
        entries = [];
        if (!SfntPrimitives.TryReadUInt16(span, pos, out var count))
        {
            throw new PlumePdfException("PLUME8014", "CFF INDEX count is truncated.");
        }

        pos += 2;
        if (count == 0)
        {
            return pos;
        }

        if (!SfntPrimitives.TryReadUInt8(span, pos, out var offSize) || offSize is < 1 or > 4)
        {
            throw new PlumePdfException("PLUME8014", "CFF INDEX offSize is truncated or out of range.");
        }

        pos += 1;

        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++)
        {
            if (!TryReadOffset(span, pos, offSize, out var offset))
            {
                throw new PlumePdfException("PLUME8014", "CFF INDEX offset array is truncated.");
            }

            offsets[i] = offset;
            pos += offSize;
        }

        var dataStart = pos - 1; // Offsets are 1-based, relative to the byte before the data area.
        for (var i = 0; i < count; i++)
        {
            var start = dataStart + offsets[i];
            var length = offsets[i + 1] - offsets[i];
            if (length < 0 || start < 0 || (long)start + length > span.Length)
            {
                throw new PlumePdfException("PLUME8014", "CFF INDEX entry runs past the end of the table.");
            }

            entries.Add((start, length));
        }

        return dataStart + offsets[count];
    }

    private static bool TryReadOffset(ReadOnlySpan<byte> span, int pos, int offSize, out int value)
    {
        value = 0;
        if (pos < 0 || pos + offSize > span.Length)
        {
            return false;
        }

        for (var i = 0; i < offSize; i++)
        {
            value = (value << 8) | span[pos + i];
        }

        return true;
    }

    /// <summary>Parses a CFF DICT (Top DICT, Private DICT, or an FDArray entry) into operator-code → operand-array pairs. Two-byte operators (<c>12 xx</c>) are keyed as <c>1200 + xx</c> so they share the same dictionary as one-byte operators without colliding.</summary>
    private static Dictionary<int, double[]> ParseDict(ReadOnlySpan<byte> span, (int Start, int Length) range)
    {
        var result = new Dictionary<int, double[]>();
        var operands = new List<double>();
        var pos = range.Start;
        var end = range.Start + range.Length;

        while (pos < end)
        {
            var b0 = span[pos];
            if (b0 <= 21)
            {
                int op;
                if (b0 == 12)
                {
                    if (pos + 1 >= end)
                    {
                        throw new PlumePdfException("PLUME8014", "CFF DICT escape operator is truncated.");
                    }

                    op = 1200 + span[pos + 1];
                    pos += 2;
                }
                else
                {
                    op = b0;
                    pos += 1;
                }

                result[op] = [.. operands];
                operands.Clear();
            }
            else if (b0 == 28)
            {
                if (pos + 3 > end)
                {
                    throw new PlumePdfException("PLUME8014", "CFF DICT int16 operand is truncated.");
                }

                operands.Add(BinaryPrimitives.ReadInt16BigEndian(span.Slice(pos + 1, 2)));
                pos += 3;
            }
            else if (b0 == 29)
            {
                if (pos + 5 > end)
                {
                    throw new PlumePdfException("PLUME8014", "CFF DICT int32 operand is truncated.");
                }

                operands.Add(BinaryPrimitives.ReadInt32BigEndian(span.Slice(pos + 1, 4)));
                pos += 5;
            }
            else if (b0 == 30)
            {
                pos = ParseDictReal(span, pos + 1, end, out var real);
                operands.Add(real);
            }
            else if (b0 is >= 32 and <= 246)
            {
                operands.Add(b0 - 139);
                pos += 1;
            }
            else if (b0 is >= 247 and <= 250)
            {
                if (pos + 2 > end)
                {
                    throw new PlumePdfException("PLUME8014", "CFF DICT operand is truncated.");
                }

                operands.Add(((b0 - 247) * 256) + span[pos + 1] + 108);
                pos += 2;
            }
            else if (b0 is >= 251 and <= 254)
            {
                if (pos + 2 > end)
                {
                    throw new PlumePdfException("PLUME8014", "CFF DICT operand is truncated.");
                }

                operands.Add((-(b0 - 251) * 256) - span[pos + 1] - 108);
                pos += 2;
            }
            else
            {
                throw new PlumePdfException("PLUME8014", $"CFF DICT contains reserved byte {b0}.");
            }
        }

        return result;
    }

    /// <summary>Decodes a CFF DICT real-number operand's nibble-packed BCD encoding (Adobe TN #5176 Table 5).</summary>
    private static int ParseDictReal(ReadOnlySpan<byte> span, int pos, int end, out double value)
    {
        System.Text.StringBuilder text = new();
        var done = false;
        while (!done)
        {
            if (pos >= end)
            {
                throw new PlumePdfException("PLUME8014", "CFF DICT real-number operand is truncated.");
            }

            var b = span[pos++];
            if (AppendNibble(text, (byte)(b >> 4)))
            {
                done = true;
            }
            else if (AppendNibble(text, (byte)(b & 0x0F)))
            {
                done = true;
            }
        }

        value = double.TryParse(text.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return pos;
    }

    /// <summary>Appends one nibble of a CFF DICT real-number's BCD encoding to <paramref name="text"/> (Adobe TN #5176 Table 5). Returns <see langword="true"/> for the end-of-number nibble (0xf).</summary>
    private static bool AppendNibble(System.Text.StringBuilder text, byte nibble)
    {
        switch (nibble)
        {
            case <= 9: text.Append((char)('0' + nibble)); return false;
            case 0xa: text.Append('.'); return false;
            case 0xb: text.Append('E'); return false;
            case 0xc: text.Append("E-"); return false;
            case 0xe: text.Append('-'); return false;
            case 0xf: return true;
            default: return false; // 0xd is reserved — ignored.
        }
    }

    private static List<(int Start, int Length)> ParseLocalSubrs(ReadOnlySpan<byte> span, int privateOffset, int privateSize)
    {
        if (privateOffset < 0 || privateSize < 0 || (long)privateOffset + privateSize > span.Length)
        {
            throw new PlumePdfException("PLUME8014", "CFF Private DICT offset/size runs past the end of the table.");
        }

        var privDict = ParseDict(span, (privateOffset, privateSize));
        if (!privDict.TryGetValue(19, out var subrsOp) || subrsOp.Length == 0) // operator 19 = Subrs, offset relative to the Private DICT's own start.
        {
            return [];
        }

        ParseIndex(span, privateOffset + (int)subrsOp[0], out var subrs);
        return subrs;
    }

    /// <summary>Parses an FDSelect table (Adobe TN #5176 §19) mapping each GID to its FDArray index. Supports both wire formats (0: flat array; 3: ranges).</summary>
    private static int[] ParseFdSelect(ReadOnlySpan<byte> span, int pos, int glyphCount)
    {
        var result = new int[glyphCount];
        if (!SfntPrimitives.TryReadUInt8(span, pos, out var format))
        {
            throw new PlumePdfException("PLUME8014", "CFF FDSelect is truncated.");
        }

        if (format == 0)
        {
            for (var gid = 0; gid < glyphCount; gid++)
            {
                if (!SfntPrimitives.TryReadUInt8(span, pos + 1 + gid, out var fd))
                {
                    throw new PlumePdfException("PLUME8014", "CFF FDSelect format 0 array is truncated.");
                }

                result[gid] = fd;
            }
        }
        else if (format == 3)
        {
            if (!SfntPrimitives.TryReadUInt16(span, pos + 1, out var rangeCount))
            {
                throw new PlumePdfException("PLUME8014", "CFF FDSelect format 3 is truncated.");
            }

            var rangePos = pos + 3;
            var prevFirst = 0;
            var prevFd = -1;
            for (var i = 0; i <= rangeCount; i++)
            {
                var isSentinel = i == rangeCount;
                int first;
                if (isSentinel)
                {
                    if (!SfntPrimitives.TryReadUInt16(span, rangePos, out var sentinel))
                    {
                        throw new PlumePdfException("PLUME8014", "CFF FDSelect format 3 sentinel is truncated.");
                    }

                    first = sentinel;
                }
                else
                {
                    if (!SfntPrimitives.TryReadUInt16(span, rangePos, out var f) || !SfntPrimitives.TryReadUInt8(span, rangePos + 2, out _))
                    {
                        throw new PlumePdfException("PLUME8014", "CFF FDSelect format 3 range is truncated.");
                    }

                    first = f;
                }

                if (prevFd >= 0)
                {
                    for (var gid = prevFirst; gid < first && gid < glyphCount; gid++)
                    {
                        result[gid] = prevFd;
                    }
                }

                if (!isSentinel)
                {
                    SfntPrimitives.TryReadUInt8(span, rangePos + 2, out var fd);
                    prevFd = fd;
                    prevFirst = first;
                    rangePos += 3;
                }
            }
        }
        else
        {
            throw new PlumePdfException("PLUME8014", $"CFF FDSelect format {format} is not recognized.");
        }

        return result;
    }

    // ---- Glyph names / built-in encoding / metrics (Adobe TN #5176 §12/§13/§18) ----

    /// <summary>
    /// Resolves a charset/Encoding SID to a name: SIDs below <see cref="CffStandardStrings.Count"/>
    /// are predefined (Appendix A); the rest index the font's own String INDEX at <c>sid - Count</c>.
    /// Glyph names are advisory (the render path resolves outlines by GID) — an out-of-range SID
    /// is a recoverable font deviation, not a parse failure, so this returns <see langword="false"/>
    /// rather than throwing; callers skip the entry, same as an empty name.
    /// </summary>
    private static bool TryGetString(ReadOnlySpan<byte> span, List<(int Start, int Length)> stringIndex, int sid, out string name)
    {
        if (sid >= 0 && sid < CffStandardStrings.Count)
        {
            name = CffStandardStrings.Names[sid];
            return true;
        }

        var index = sid - CffStandardStrings.Count;
        if (index < 0 || index >= stringIndex.Count)
        {
            name = "";
            return false;
        }

        var entry = stringIndex[index];
        name = System.Text.Encoding.ASCII.GetString(span.Slice(entry.Start, entry.Length));
        return true;
    }

    /// <summary>
    /// Parses the Top DICT <c>charset</c> operator (15) into a GID → value array (GID 0 is
    /// always value 0, i.e. CID 0 / SID 0 <c>.notdef</c>, per Adobe TN #5176 §13/§18): the value
    /// is a name SID for a non-CID program or a CID for a CID-keyed one. Absent or predefined id
    /// 0 (ISOAdobe) assigns GID i → SID i for GID &lt; 229 (ISOAdobe's 229 standard glyphs) — and,
    /// since no ISOAdobe-equivalent table exists in CID space, GID i → CID i (identity) for a
    /// CID-keyed program with no charset. Predefined ids 1/2 (Expert/ExpertSubset) leave every
    /// non-CID GID unnamed (<paramref name="isExpertCharset"/> = <see langword="true"/>); a
    /// CID-keyed program never legitimately declares these, so they degrade to identity there
    /// too rather than producing an unmapped font. Any other operand value is a byte offset to an
    /// explicit format 0/1/2 table (CFF overloads the single operand for both predefined ids and
    /// offsets — offsets are always well past byte 2, so there is no ambiguity). The charset is
    /// advisory data (names for a non-CID program, the CID map for a CID-keyed one — the render
    /// path walks CharStrings by GID regardless): a truncated table or an unrecognized explicit
    /// format is a recoverable font deviation, not a parse failure, so it degrades to whatever
    /// prefix was read plus <c>-1</c> (unmapped) for the rest — via <paramref name="isExpertCharset"/>
    /// for the non-CID caller — rather than throwing and blanking the whole font.
    /// </summary>
    private static int[] ParseCharset(ReadOnlySpan<byte> span, Dictionary<int, double[]> topDict, int glyphCount, bool isCidKeyed, out bool isExpertCharset)
    {
        isExpertCharset = false;
        var values = new int[glyphCount];
        if (glyphCount == 0)
        {
            return values;
        }

        if (!topDict.TryGetValue(15, out var charsetOp) || charsetOp.Length == 0 || (int)charsetOp[0] == 0)
        {
            for (var g = 1; g < glyphCount; g++)
            {
                values[g] = isCidKeyed || g < 229 ? g : -1;
            }

            return values;
        }

        var predefined = (int)charsetOp[0];
        if (predefined is 1 or 2)
        {
            if (isCidKeyed)
            {
                for (var g = 1; g < glyphCount; g++)
                {
                    values[g] = g;
                }
            }
            else
            {
                isExpertCharset = true;
                for (var g = 1; g < glyphCount; g++)
                {
                    values[g] = -1;
                }
            }

            return values;
        }

        // The explicit table is advisory: pre-fill every remaining GID unmapped so a truncated
        // read (or an unrecognized format below) leaves a safe partial result instead of needing
        // to throw.
        for (var g = 1; g < glyphCount; g++)
        {
            values[g] = -1;
        }

        var pos = predefined; // Not a predefined id (only 0/1/2 are) — an absolute byte offset.
        if (!SfntPrimitives.TryReadUInt8(span, pos, out var format))
        {
            isExpertCharset = true;
            return values;
        }

        pos += 1;
        var gid = 1;
        switch (format)
        {
            case 0:
                for (; gid < glyphCount; gid++)
                {
                    if (!SfntPrimitives.TryReadUInt16(span, pos, out var sid))
                    {
                        isExpertCharset = true;
                        return values;
                    }

                    values[gid] = sid;
                    pos += 2;
                }

                break;

            case 1:
            case 2:
                while (gid < glyphCount)
                {
                    if (!SfntPrimitives.TryReadUInt16(span, pos, out var first))
                    {
                        isExpertCharset = true;
                        return values;
                    }

                    pos += 2;

                    int nLeft;
                    if (format == 1)
                    {
                        if (!SfntPrimitives.TryReadUInt8(span, pos, out var nLeftByte))
                        {
                            isExpertCharset = true;
                            return values;
                        }

                        nLeft = nLeftByte;
                        pos += 1;
                    }
                    else
                    {
                        if (!SfntPrimitives.TryReadUInt16(span, pos, out var nLeftWord))
                        {
                            isExpertCharset = true;
                            return values;
                        }

                        nLeft = nLeftWord;
                        pos += 2;
                    }

                    for (var i = 0; i <= nLeft && gid < glyphCount; i++, gid++)
                    {
                        values[gid] = first + i;
                    }
                }

                break;

            default:
                isExpertCharset = true;
                return values;
        }

        return values;
    }

    /// <summary>
    /// Inverts a CID-keyed charset's GID → CID array into CID → GID (Adobe TN #5176 §18, ISO
    /// 32000-1 §9.7.4.2); unmapped CIDs resolve to GID 0. A declared CID above the 65535 maximum
    /// is a recoverable font deviation (the render path still walks CharStrings by GID) — that
    /// single entry is skipped rather than rejecting the whole CID → GID map.
    /// </summary>
    private static int[] InvertCidCharset(int[] cidsByGid)
    {
        const int maxValidCid = 65535;
        var maxCid = 0;
        foreach (var cid in cidsByGid)
        {
            if (cid > maxCid && cid <= maxValidCid)
            {
                maxCid = cid;
            }
        }

        // Lowest GID wins when several glyphs claim one CID (FreeType's cff_charset_compute_cids
        // — "matches Acroread" — and therefore PDFium), and CID 0 stays .notdef (GID 0) no matter
        // what a later glyph declares; this mirrors the first-wins name → GID table above.
        var cidToGid = new int[maxCid + 1];
        for (var gid = 1; gid < cidsByGid.Length; gid++)
        {
            var cid = cidsByGid[gid];
            if (cid is > 0 and <= maxValidCid && cidToGid[cid] == 0)
            {
                cidToGid[cid] = gid;
            }
        }

        return cidToGid;
    }

    /// <summary>
    /// Parses the Top DICT <c>Encoding</c> operator (16) into a 256-entry (glyph name, Unicode)
    /// table (Adobe TN #5176 §12) — only called for a non-CID program with a readable (non-
    /// Expert) charset. Absent or predefined id 0 (Standard) clones
    /// <see cref="SimpleFontEncodings.StandardEncoding"/> (never the shared array itself).
    /// Predefined id 1 (Expert) returns <see langword="null"/> — no readable table. Any other
    /// value is a byte offset to a custom format 0 (code[i] → GID i+1) or format 1 (code ranges)
    /// table, optionally followed by supplements (high bit of the format byte) that map a code
    /// directly to a name by SID, overriding whatever the format 0/1 table assigned that code.
    /// The built-in encoding is advisory (glyphs still resolve by GID) — a truncated table or an
    /// unrecognized explicit format is a recoverable font deviation, not a parse failure, so it
    /// degrades to <see langword="null"/> (a truncated/malformed supplements tail instead keeps
    /// the already-parsed base table) rather than throwing and blanking the whole font.
    /// </summary>
    private static (string GlyphName, int Unicode)[]? ParseEncoding(ReadOnlySpan<byte> span, Dictionary<int, double[]> topDict, List<(int Start, int Length)> stringIndex, string?[] gidNames)
    {
        if (!topDict.TryGetValue(16, out var encodingOp) || encodingOp.Length == 0 || (int)encodingOp[0] == 0)
        {
            return CloneStandardEncoding();
        }

        var predefined = (int)encodingOp[0];
        if (predefined == 1)
        {
            return null;
        }

        var pos = predefined; // Not a predefined id (only 0/1 are) — an absolute byte offset.
        if (!SfntPrimitives.TryReadUInt8(span, pos, out var formatByte))
        {
            return null;
        }

        var format = formatByte & 0x7F;
        var hasSupplements = (formatByte & 0x80) != 0;
        pos += 1;

        var table = new (string, int)[256];
        for (var i = 0; i < 256; i++)
        {
            table[i] = ("", -1);
        }

        if (format == 0)
        {
            if (!SfntPrimitives.TryReadUInt8(span, pos, out var nCodes))
            {
                return null;
            }

            pos += 1;
            for (var i = 0; i < nCodes; i++)
            {
                if (!SfntPrimitives.TryReadUInt8(span, pos, out var code))
                {
                    return null;
                }

                pos += 1;
                SetEncodingEntry(table, code, GidName(gidNames, i + 1));
            }
        }
        else if (format == 1)
        {
            if (!SfntPrimitives.TryReadUInt8(span, pos, out var nRanges))
            {
                return null;
            }

            pos += 1;
            var gid = 1;
            for (var r = 0; r < nRanges; r++)
            {
                if (!SfntPrimitives.TryReadUInt8(span, pos, out var first) || !SfntPrimitives.TryReadUInt8(span, pos + 1, out var nLeft))
                {
                    return null;
                }

                pos += 2;
                for (var c = 0; c <= nLeft; c++)
                {
                    SetEncodingEntry(table, first + c, GidName(gidNames, gid));
                    gid++;
                }
            }
        }
        else
        {
            return null;
        }

        // Supplements are a trailing refinement over an already-valid base table — a truncated
        // supplements tail keeps the base table rather than discarding it too.
        if (hasSupplements && SfntPrimitives.TryReadUInt8(span, pos, out var nSups))
        {
            pos += 1;
            for (var i = 0; i < nSups; i++)
            {
                if (!SfntPrimitives.TryReadUInt8(span, pos, out var code) || !SfntPrimitives.TryReadUInt16(span, pos + 1, out var sid))
                {
                    break;
                }

                pos += 3;
                if (TryGetString(span, stringIndex, sid, out var name))
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        SetEncodingEntry(table, code, name, overrideEmpty: true); // A real name overrides the base table; an empty String-INDEX entry never clears one.
                    }
                }
            }
        }

        return table;

        static string? GidName(string?[] names, int gid) => gid >= 0 && gid < names.Length ? names[gid] : null;

        static void SetEncodingEntry((string, int)[] t, int code, string? name, bool overrideEmpty = false)
        {
            if (code is < 0 or > 255)
            {
                return;
            }

            if (string.IsNullOrEmpty(name) && !overrideEmpty)
            {
                return;
            }

            name ??= "";
            var unicode = name.Length > 0 && SimpleFontEncodings.TryGlyphNameToUnicode(name, out var u) ? u : -1;
            t[code] = (name, unicode);
        }
    }

    private static (string GlyphName, int Unicode)[] CloneStandardEncoding()
    {
        var source = SimpleFontEncodings.StandardEncoding;
        var clone = new (string, int)[source.Length];
        Array.Copy(source, clone, source.Length);
        return clone;
    }

    /// <summary>Reads the Top DICT <c>FontMatrix</c> operator (12 7) and derives <c>UnitsPerEm</c>: <c>1/a</c> for a uniform diagonal matrix (<c>b == c == 0</c>, <c>a == d</c> within 1e-9, <c>a &gt; 0</c>); 1000/non-uniform otherwise. Absent defaults to CFF's own default matrix <c>[0.001 0 0 0.001 0 0]</c>, which is uniform (1000).</summary>
    private static (double UnitsPerEm, bool Uniform) ParseFontMatrix(Dictionary<int, double[]> topDict)
    {
        if (!topDict.TryGetValue(1207, out var m) || m.Length != 6)
        {
            return (1000.0, true);
        }

        var a = m[0];
        var b = m[1];
        var c = m[2];
        var d = m[3];
        if (b != 0 || c != 0 || Math.Abs(a - d) >= 1e-9 || !(a > 0))
        {
            return (1000.0, false);
        }

        // Sanity range: real fonts sit in [16, 16384] units/em (1000 and 2048 in practice). A
        // hostile or corrupt matrix such as [1E300 …] would otherwise yield an infinite glyph
        // scale downstream — treat it as unsupported so the font falls to substitution.
        var unitsPerEm = 1.0 / a;
        return unitsPerEm is >= 16 and <= 16384 ? (unitsPerEm, true) : (1000.0, false);
    }

    /// <summary>Reads the Top DICT <c>CharstringType</c> operator (12 6); defaults to (and this parser only interprets) Type 2.</summary>
    private static bool IsCharstringType2(Dictionary<int, double[]> topDict) =>
        !topDict.TryGetValue(1206, out var t) || t.Length == 0 || (int)t[0] == 2;

    /// <summary>
    /// The Type 2 charstring interpreter (Adobe TN #5177) — walks one glyph's charstring bytes,
    /// tracking the operand stack, current point, and stem-hint count, emitting path commands
    /// into a <see cref="GlyphOutlineBuilder"/>. A fresh instance is used per glyph (the
    /// interpreter's stack/point state does not carry over between glyphs, including seac
    /// component glyphs — <see cref="_owner"/> re-enters <see cref="GetGlyphOutlineCore"/>,
    /// which always constructs a new interpreter).
    /// </summary>
    private sealed class Type2Interpreter(List<(int Start, int Length)> globalSubrs, List<(int Start, int Length)> localSubrs, CffParser? owner, int seacDepth)
    {
        private readonly List<(int Start, int Length)> _globalSubrs = globalSubrs;
        private readonly List<(int Start, int Length)> _localSubrs = localSubrs;
        private readonly CffParser? _owner = owner;
        private readonly int _seacDepth = seacDepth;
        private readonly int _globalBias = Bias(globalSubrs.Count);
        private readonly int _localBias = Bias(localSubrs.Count);
        private readonly List<double> _stack = [];
        private double _x;
        private double _y;
        private int _stemCount;
        private bool _widthParsed;
        private int _depth;

        public GlyphOutlineBuilder Builder { get; } = new();

        /// <summary><paramref name="data"/> is threaded through every recursive <see cref="Execute"/> call as a parameter, never stored as a field — <c>ReadOnlySpan&lt;byte&gt;</c> is a ref struct and this class's other state (operand stack, current point) must survive across the whole glyph decode, so only the byte source is passed by-value each call rather than copied.</summary>
        public void Run(ReadOnlySpan<byte> data, (int Start, int Length) charstring) => Execute(data, charstring);

        private static int Bias(int subrCount) => subrCount < 1240 ? 107 : subrCount < 33900 ? 1131 : 32768;

        private void Execute(ReadOnlySpan<byte> bytes, (int Start, int Length) range)
        {
            if (++_depth > MaxSubrDepth)
            {
                throw new PlumePdfException("PLUME8014", "CFF Type 2 charstring subroutine nesting exceeded the safety limit.");
            }

            var pos = range.Start;
            var end = range.Start + range.Length;

            while (pos < end)
            {
                var b0 = bytes[pos];

                if (b0 >= 32 || b0 == 28)
                {
                    pos = ReadOperand(bytes, pos, out var value);
                    PushOperand(value);
                    continue;
                }

                pos++;
                switch (b0)
                {
                    case 1: // hstem
                    case 3: // vstem
                    case 18: // hstemhm
                    case 23: // vstemhm
                        CountStems();
                        break;

                    case 19: // hintmask
                    case 20: // cntrmask
                        CountStems();
                        pos += (_stemCount + 7) / 8;
                        if (pos > bytes.Length)
                        {
                            throw new PlumePdfException("PLUME8014", "CFF charstring hintmask/cntrmask runs past the end of the table.");
                        }

                        _stack.Clear();
                        break;

                    case 21: // rmoveto
                        StripWidth(2);
                        RequireOperands(2);
                        MoveTo(_stack[0], _stack[1]);
                        _stack.Clear();
                        break;

                    case 22: // hmoveto
                        StripWidth(1);
                        RequireOperands(1);
                        MoveTo(_stack[0], 0);
                        _stack.Clear();
                        break;

                    case 4: // vmoveto
                        StripWidth(1);
                        RequireOperands(1);
                        MoveTo(0, _stack[0]);
                        _stack.Clear();
                        break;

                    case 5: // rlineto
                        for (var i = 0; i + 1 < _stack.Count; i += 2)
                        {
                            LineTo(_stack[i], _stack[i + 1]);
                        }

                        _stack.Clear();
                        break;

                    case 6: // hlineto
                        RunAlternatingLines(startHorizontal: true);
                        _stack.Clear();
                        break;

                    case 7: // vlineto
                        RunAlternatingLines(startHorizontal: false);
                        _stack.Clear();
                        break;

                    case 8: // rrcurveto
                        for (var i = 0; i + 5 < _stack.Count; i += 6)
                        {
                            CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                        }

                        _stack.Clear();
                        break;

                    case 24: // rcurveline
                        {
                            var i = 0;
                            for (; i + 5 < _stack.Count - 2; i += 6)
                            {
                                CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            }

                            if (i + 1 < _stack.Count)
                            {
                                LineTo(_stack[i], _stack[i + 1]);
                            }

                            _stack.Clear();
                            break;
                        }

                    case 25: // rlinecurve
                        {
                            var i = 0;
                            for (; i + 1 < _stack.Count - 6; i += 2)
                            {
                                LineTo(_stack[i], _stack[i + 1]);
                            }

                            if (i + 5 < _stack.Count)
                            {
                                CurveTo(_stack[i], _stack[i + 1], _stack[i + 2], _stack[i + 3], _stack[i + 4], _stack[i + 5]);
                            }

                            _stack.Clear();
                            break;
                        }

                    case 26: // vvcurveto
                        RunVvCurveTo();
                        _stack.Clear();
                        break;

                    case 27: // hhcurveto
                        RunHhCurveTo();
                        _stack.Clear();
                        break;

                    case 30: // vhcurveto
                        RunAlternatingCurves(startHorizontal: false);
                        _stack.Clear();
                        break;

                    case 31: // hvcurveto
                        RunAlternatingCurves(startHorizontal: true);
                        _stack.Clear();
                        break;

                    case 10: // callsubr
                        {
                            var index = (int)PopLast() + _localBias;
                            if (index < 0 || index >= _localSubrs.Count)
                            {
                                throw new PlumePdfException("PLUME8014", "CFF callsubr index is out of range.");
                            }

                            Execute(bytes, _localSubrs[index]);
                            break;
                        }

                    case 29: // callgsubr
                        {
                            var index = (int)PopLast() + _globalBias;
                            if (index < 0 || index >= _globalSubrs.Count)
                            {
                                throw new PlumePdfException("PLUME8014", "CFF callgsubr index is out of range.");
                            }

                            Execute(bytes, _globalSubrs[index]);
                            break;
                        }

                    case 11: // return
                        _depth--;
                        return;

                    case 14: // endchar
                        // Type 2 endchar takes 0 args, or 4 (the deprecated seac-like accent
                        // composition, Adobe TN #5177 Appendix C) — a leading width operand is
                        // only present when the total is one more than one of those two counts
                        // (1 or 5), never when it's exactly 4 (a width-less seac) or 0.
                        if (!_widthParsed)
                        {
                            if (_stack.Count is 1 or 5)
                            {
                                _stack.RemoveAt(0);
                            }

                            _widthParsed = true;
                        }

                        if (_stack.Count == 4 && _owner is not null && _owner.HasGlyphNames && _seacDepth < MaxSeacDepth)
                        {
                            RunSeac(_stack[0], _stack[1], (int)_stack[2], (int)_stack[3]);
                        }

                        _stack.Clear();
                        _depth--;
                        return;

                    case 12: // escape (two-byte operator) — arithmetic/flex ops.
                        {
                            if (pos >= end)
                            {
                                throw new PlumePdfException("PLUME8014", "CFF charstring escape operator is truncated.");
                            }

                            var b1 = bytes[pos++];
                            RunEscape(b1);
                            break;
                        }

                    default:
                        _stack.Clear();
                        break;
                }
            }

            _depth--;
        }

        /// <summary>seac (deprecated 4-operand <c>endchar</c>, Adobe TN #5177 Appendix C): composes the current glyph from a base and an accent glyph, each resolved by looking up its <c>StandardEncoding</c> code's glyph name in the owning font's charset. The accent is translated by <c>(adx, ady)</c> (Type 2 seac has no <c>asb</c> — unlike Type 1's). A component is decoded via <see cref="_owner"/>, which always builds a fresh interpreter, so none of this interpreter's own hint/width state leaks into it; either component missing from this font's charset leaves the composite as just its own (typically empty) outline.</summary>
        private void RunSeac(double adx, double ady, int bcharCode, int acharCode)
        {
            if (_owner is null)
            {
                return;
            }

            if (!TryResolveStandardGlyph(bcharCode, out var baseGid) || !TryResolveStandardGlyph(acharCode, out var accentGid))
            {
                return;
            }

            var baseOutline = _owner.GetGlyphOutlineCore(baseGid, _seacDepth + 1);
            foreach (var cmd in baseOutline.Commands)
            {
                AppendCommand(Builder, cmd, 0, 0);
            }

            var accentOutline = _owner.GetGlyphOutlineCore(accentGid, _seacDepth + 1);
            var dx = (float)adx;
            var dy = (float)ady;
            foreach (var cmd in accentOutline.Commands)
            {
                AppendCommand(Builder, cmd, dx, dy);
            }
        }

        private bool TryResolveStandardGlyph(int code, out int gid)
        {
            gid = 0;
            if (_owner is null || code < 0 || code >= SimpleFontEncodings.StandardEncoding.Length)
            {
                return false;
            }

            var name = SimpleFontEncodings.StandardEncoding[code].GlyphName;
            return !string.IsNullOrEmpty(name) && _owner.TryGetGlyphId(name, out gid);
        }

        private static void AppendCommand(GlyphOutlineBuilder sink, GlyphPathCommand cmd, float dx, float dy)
        {
            switch (cmd.Kind)
            {
                case GlyphPathCommandKind.MoveTo: sink.MoveTo(cmd.X + dx, cmd.Y + dy); break;
                case GlyphPathCommandKind.LineTo: sink.LineTo(cmd.X + dx, cmd.Y + dy); break;
                case GlyphPathCommandKind.CurveTo: sink.CurveTo(cmd.X1 + dx, cmd.Y1 + dy, cmd.X2 + dx, cmd.Y2 + dy, cmd.X + dx, cmd.Y + dy); break;
                case GlyphPathCommandKind.ClosePath: sink.ClosePath(); break;
            }
        }

        private void RunEscape(byte op)
        {
            switch (op)
            {
                case 35: // flex: dx1 dy1 dx2 dy2 dx3 dy3 dx4 dy4 dx5 dy5 dx6 dy6 fd
                    if (_stack.Count >= 12)
                    {
                        CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                        CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], _stack[11]);
                    }

                    _stack.Clear();
                    break;

                case 34: // hflex: dx1 dx2 dy2 dx3 dx4 dx5 dx6
                    if (_stack.Count >= 7)
                    {
                        var y0 = _y;
                        CurveTo(_stack[0], 0, _stack[1], _stack[2], _stack[3], 0);
                        CurveTo(_stack[4], 0, _stack[5], y0 - _y, _stack[6], 0);
                    }

                    _stack.Clear();
                    break;

                case 36: // hflex1: dx1 dy1 dx2 dy2 dx3 dx4 dx5 dy5 dx6
                    if (_stack.Count >= 9)
                    {
                        var y0 = _y;
                        CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], 0);
                        CurveTo(_stack[5], 0, _stack[6], _stack[7], _stack[8], y0 - _y - _stack[1] - _stack[3] - _stack[7]);
                    }

                    _stack.Clear();
                    break;

                case 37: // flex1: dx1 dy1 dx2 dy2 dx3 dy3 dx4 dy4 dx5 dy5 d6
                    if (_stack.Count >= 11)
                    {
                        var x0 = _x;
                        var y0 = _y;
                        CurveTo(_stack[0], _stack[1], _stack[2], _stack[3], _stack[4], _stack[5]);
                        var dxSum = _stack[0] + _stack[2] + _stack[4] + _stack[6] + _stack[8];
                        var dySum = _stack[1] + _stack[3] + _stack[5] + _stack[7] + _stack[9];
                        if (Math.Abs(dxSum) > Math.Abs(dySum))
                        {
                            CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], _stack[10], y0 - _y - _stack[7] - _stack[9]);
                        }
                        else
                        {
                            CurveTo(_stack[6], _stack[7], _stack[8], _stack[9], x0 - _x - _stack[6] - _stack[8], _stack[10]);
                        }
                    }

                    _stack.Clear();
                    break;

                default:
                    // Arithmetic/storage escape operators (and/or/not/abs/add/.../random) are
                    // Type 1-legacy carry-overs rarely emitted by modern CFF producers and have
                    // no effect on outline geometry either way — drop the operands and continue.
                    _stack.Clear();
                    break;
            }
        }

        private void RunAlternatingLines(bool startHorizontal)
        {
            var horiz = startHorizontal;
            foreach (var delta in _stack)
            {
                if (horiz)
                {
                    LineTo(delta, 0);
                }
                else
                {
                    LineTo(0, delta);
                }

                horiz = !horiz;
            }
        }

        private void RunAlternatingCurves(bool startHorizontal)
        {
            var i = 0;
            var horiz = startHorizontal;
            var n = _stack.Count;
            while (n - i >= 4)
            {
                double x1, y1, x2, y2, x3, y3;
                var remaining = n - i;
                if (horiz)
                {
                    x1 = _x + _stack[i];
                    y1 = _y;
                    x2 = x1 + _stack[i + 1];
                    y2 = y1 + _stack[i + 2];
                    if (remaining == 5)
                    {
                        x3 = x2 + _stack[i + 4];
                        y3 = y2 + _stack[i + 3];
                    }
                    else
                    {
                        x3 = x2;
                        y3 = y2 + _stack[i + 3];
                    }
                }
                else
                {
                    x1 = _x;
                    y1 = _y + _stack[i];
                    x2 = x1 + _stack[i + 1];
                    y2 = y1 + _stack[i + 2];
                    if (remaining == 5)
                    {
                        x3 = x2 + _stack[i + 3];
                        y3 = y2 + _stack[i + 4];
                    }
                    else
                    {
                        x3 = x2 + _stack[i + 3];
                        y3 = y2;
                    }
                }

                EmitCurveAbsolute(x1, y1, x2, y2, x3, y3);
                i += 4;
                horiz = !horiz;
            }
        }

        private void RunVvCurveTo()
        {
            var i = 0;
            double dx1 = 0;
            if (_stack.Count % 4 == 1)
            {
                dx1 = _stack[0];
                i = 1;
            }

            var first = true;
            while (i + 4 <= _stack.Count)
            {
                var x1 = _x + (first ? dx1 : 0);
                var y1 = _y + _stack[i];
                var x2 = x1 + _stack[i + 1];
                var y2 = y1 + _stack[i + 2];
                var x3 = x2;
                var y3 = y2 + _stack[i + 3];
                EmitCurveAbsolute(x1, y1, x2, y2, x3, y3);
                i += 4;
                first = false;
            }
        }

        private void RunHhCurveTo()
        {
            var i = 0;
            double dy1 = 0;
            if (_stack.Count % 4 == 1)
            {
                dy1 = _stack[0];
                i = 1;
            }

            var first = true;
            while (i + 4 <= _stack.Count)
            {
                var x1 = _x + _stack[i];
                var y1 = _y + (first ? dy1 : 0);
                var x2 = x1 + _stack[i + 1];
                var y2 = y1 + _stack[i + 2];
                var x3 = x2 + _stack[i + 3];
                var y3 = y2;
                EmitCurveAbsolute(x1, y1, x2, y2, x3, y3);
                i += 4;
                first = false;
            }
        }

        private void CountStems()
        {
            // An odd leading operand on the first stem-hint operator is the glyph's width, per
            // the same width convention StripWidth handles for moveto/endchar — hstem/vstem
            // pairs args as (y, dy), so an odd total means a leading width value.
            if (!_widthParsed && _stack.Count % 2 == 1)
            {
                _stack.RemoveAt(0);
            }

            _widthParsed = true;
            _stemCount += _stack.Count / 2;
            _stack.Clear();
        }

        private void StripWidth(int expectedArgs)
        {
            if (!_widthParsed)
            {
                if (_stack.Count > expectedArgs)
                {
                    _stack.RemoveAt(0);
                }

                _widthParsed = true;
            }
        }

        private void RequireOperands(int count)
        {
            if (_stack.Count < count)
            {
                throw new PlumePdfException("PLUME8014", $"CFF charstring operator needs {count} operand(s) but the stack has {_stack.Count}.");
            }
        }

        private double PopLast()
        {
            if (_stack.Count == 0)
            {
                throw new PlumePdfException("PLUME8014", "CFF charstring operand stack underflow.");
            }

            var value = _stack[^1];
            _stack.RemoveAt(_stack.Count - 1);
            return value;
        }

        private void PushOperand(double value)
        {
            if (_stack.Count >= MaxOperandStack)
            {
                throw new PlumePdfException("PLUME8014", "CFF charstring operand stack overflow.");
            }

            _stack.Add(value);
        }

        private void MoveTo(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            Builder.MoveTo((float)_x, (float)_y);
        }

        private void LineTo(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            Builder.LineTo((float)_x, (float)_y);
        }

        private void CurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            var x1 = _x + dx1;
            var y1 = _y + dy1;
            var x2 = x1 + dx2;
            var y2 = y1 + dy2;
            _x = x2 + dx3;
            _y = y2 + dy3;
            Builder.CurveTo((float)x1, (float)y1, (float)x2, (float)y2, (float)_x, (float)_y);
        }

        private void EmitCurveAbsolute(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            _x = x3;
            _y = y3;
            Builder.CurveTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3);
        }

        private static int ReadOperand(ReadOnlySpan<byte> bytes, int pos, out double value)
        {
            var b0 = bytes[pos];
            var needed = b0 switch { 28 => 3, 255 => 5, >= 247 and <= 254 => 2, _ => 1 };
            if (pos + needed > bytes.Length)
            {
                throw new PlumePdfException("PLUME8014", "CFF charstring operand is truncated.");
            }

            switch (b0)
            {
                case 28:
                    value = (short)((bytes[pos + 1] << 8) | bytes[pos + 2]);
                    return pos + 3;

                case 255:
                    var raw = (bytes[pos + 1] << 24) | (bytes[pos + 2] << 16) | (bytes[pos + 3] << 8) | bytes[pos + 4];
                    value = raw / 65536.0;
                    return pos + 5;

                case >= 32 and <= 246:
                    value = b0 - 139;
                    return pos + 1;

                case >= 247 and <= 250:
                    value = ((b0 - 247) * 256) + bytes[pos + 1] + 108;
                    return pos + 2;

                default: // 251-254
                    value = (-(b0 - 251) * 256) - bytes[pos + 1] - 108;
                    return pos + 2;
            }
        }
    }
}
