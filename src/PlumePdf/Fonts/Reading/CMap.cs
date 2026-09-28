namespace PlumePdf.Fonts.Reading;

/// <summary>
/// One codespace range declared by a CMap's <c>begincodespacerange</c> block
/// (ISO 32000-1 §9.7.5.2). <see cref="Low"/> and <see cref="High"/> are always the same
/// length, and that shared length is the byte width of every code that falls inside the
/// range when scanning a content-stream string for this CMap (§9.7.6.2).
/// </summary>
internal readonly struct CMapCodespaceRange
{
    /// <summary>Creates a codespace range. <paramref name="low"/> and <paramref name="high"/> must be the same length.</summary>
    public CMapCodespaceRange(byte[] low, byte[] high)
    {
        Low = low;
        High = high;
    }

    /// <summary>The inclusive lower bound, one byte value per byte position.</summary>
    public byte[] Low { get; }

    /// <summary>The inclusive upper bound, one byte value per byte position.</summary>
    public byte[] High { get; }

    /// <summary>The byte width of a code in this range.</summary>
    public int Length => Low.Length;

    /// <summary>Whether <paramref name="code"/> (exactly <see cref="Length"/> bytes) falls within this range, byte by byte.</summary>
    public bool Contains(ReadOnlySpan<byte> code)
    {
        if (code.Length != Length)
        {
            return false;
        }

        for (var i = 0; i < Length; i++)
        {
            if (code[i] < Low[i] || code[i] > High[i])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A parsed PDF/PostScript-subset CMap (ISO 32000-1 §9.7.5) — the code-space/CID mapping used
/// by a Type0 font's embedded <c>/Encoding</c> stream, or the code-to-Unicode overlay used by
/// <c>/ToUnicode</c>. Built by <see cref="CMapParser"/>; a plain, immutable lookup table with
/// no parsing logic of its own. This "CMap" is always the PDF/PostScript-subset artifact —
/// never the unrelated OpenType <c>cmap</c> table (<see cref="PlumePdf.Fonts.Tables.CmapTable"/>),
/// which maps Unicode to glyph index inside a font <em>program</em>, the opposite direction
/// and a different file format entirely.
/// </summary>
internal sealed class CMap
{
    private readonly Dictionary<uint, int> _cidSingles;
    private readonly (uint Lo, uint Hi, int StartCid)[] _cidRanges;
    private readonly Dictionary<uint, string> _bfSingles;
    private readonly (uint Lo, uint Hi, byte[] DstLo)[] _bfRanges;

    internal CMap(
        IReadOnlyList<CMapCodespaceRange> codespaceRanges,
        Dictionary<uint, int> cidSingles,
        List<(uint Lo, uint Hi, int StartCid)> cidRanges,
        Dictionary<uint, string> bfSingles,
        List<(uint Lo, uint Hi, byte[] DstLo)> bfRanges,
        string? useCMapName)
    {
        CodespaceRanges = codespaceRanges;
        _cidSingles = cidSingles;
        _bfSingles = bfSingles;
        UseCMapName = useCMapName;

        // Sorted by Lo so lookups can binary-search rather than scan linearly — a CMap can
        // legitimately declare thousands of ranges (large CJK CID CMaps).
        cidRanges.Sort(static (a, b) => a.Lo.CompareTo(b.Lo));
        _cidRanges = [.. cidRanges];
        bfRanges.Sort(static (a, b) => a.Lo.CompareTo(b.Lo));
        _bfRanges = [.. bfRanges];
    }

    /// <summary>The declared codespace ranges, in declaration order.</summary>
    public IReadOnlyList<CMapCodespaceRange> CodespaceRanges { get; }

    /// <summary>
    /// The CMap name this one names via <c>usecmap</c>, if any — recorded but not resolved or
    /// merged into this CMap's own ranges (deferred for this phase; a
    /// document that relies on <c>usecmap</c> chaining for CID/Unicode resolution gets a
    /// partial map from this CMap's own declared ranges only).
    /// </summary>
    public string? UseCMapName { get; }

    /// <summary>
    /// Determines how many bytes, starting at <paramref name="remaining"/>'s first byte, make
    /// up the next code, per the declared <see cref="CodespaceRanges"/> (§9.7.6.2). Always
    /// returns at least 1 (and never more than <paramref name="remaining"/>'s length) so a
    /// caller can always make forward progress, even over non-conforming input.
    /// </summary>
    public int GetCodeLength(ReadOnlySpan<byte> remaining)
    {
        if (remaining.Length == 0)
        {
            return 0;
        }

        if (CodespaceRanges.Count == 0)
        {
            return 1;
        }

        foreach (var range in CodespaceRanges)
        {
            if (remaining.Length >= range.Length && range.Contains(remaining[..range.Length]))
            {
                return range.Length;
            }
        }

        // No exact match: fall back to the range whose first byte covers the incoming first
        // byte (matching how real-world CMap-consuming readers stay lenient here), else the
        // shortest declared range length, so decoding keeps making forward progress on
        // non-conforming input.
        foreach (var range in CodespaceRanges)
        {
            if (remaining.Length >= range.Length && remaining[0] >= range.Low[0] && remaining[0] <= range.High[0])
            {
                return range.Length;
            }
        }

        var fallbackLength = int.MaxValue;
        foreach (var range in CodespaceRanges)
        {
            fallbackLength = Math.Min(fallbackLength, range.Length);
        }

        return Math.Min(remaining.Length, fallbackLength);
    }

    /// <summary>Resolves <paramref name="code"/> to a CID via <c>cidchar</c>/<c>cidrange</c> entries. <see langword="false"/> when unmapped.</summary>
    public bool TryGetCid(uint code, out int cid)
    {
        if (_cidSingles.TryGetValue(code, out cid))
        {
            return true;
        }

        var index = FindRange(_cidRanges, code);
        if (index >= 0)
        {
            var range = _cidRanges[index];
            cid = range.StartCid + (int)(code - range.Lo);
            return true;
        }

        cid = 0;
        return false;
    }

    /// <summary>Resolves <paramref name="code"/> to Unicode text via <c>bfchar</c>/<c>bfrange</c> entries. <see langword="false"/> when unmapped.</summary>
    public bool TryGetUnicode(uint code, out string unicode)
    {
        if (_bfSingles.TryGetValue(code, out var single))
        {
            unicode = single;
            return true;
        }

        var index = FindRange(_bfRanges, code);
        if (index >= 0)
        {
            var range = _bfRanges[index];
            var dstBytes = AddDelta(range.DstLo, code - range.Lo);
            unicode = DecodeUtf16BigEndian(dstBytes);
            return true;
        }

        unicode = string.Empty;
        return false;
    }

    private static int FindRange((uint Lo, uint Hi, int StartCid)[] ranges, uint code)
    {
        var lo = 0;
        var hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var range = ranges[mid];
            if (code < range.Lo)
            {
                hi = mid - 1;
            }
            else if (code > range.Hi)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    private static int FindRange((uint Lo, uint Hi, byte[] DstLo)[] ranges, uint code)
    {
        var lo = 0;
        var hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var range = ranges[mid];
            if (code < range.Lo)
            {
                hi = mid - 1;
            }
            else if (code > range.Hi)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    /// <summary>
    /// Adds <paramref name="delta"/> to <paramref name="value"/>, treating the byte array as
    /// one big-endian integer of its own length. Used to derive a <c>bfrange</c> entry's
    /// per-code destination from its declared low destination — including walking correctly
    /// through a UTF-16BE surrogate pair for codes past the Basic Multilingual Plane, since
    /// incrementing a (high-surrogate, low-surrogate) 4-byte pair as one 32-bit integer tracks
    /// consecutive supplementary-plane codepoints for any delta that stays within one
    /// low-surrogate's 0x400-code span, which is the whole realistic case for a CMap's
    /// declared per-line span.
    /// </summary>
    private static byte[] AddDelta(byte[] value, uint delta)
    {
        var result = (byte[])value.Clone();
        var carry = (ulong)delta;
        for (var i = result.Length - 1; i >= 0 && carry != 0; i--)
        {
            var sum = result[i] + (carry & 0xFF);
            result[i] = unchecked((byte)sum);
            carry = (carry >> 8) + (uint)(sum >> 8);
        }

        return result;
    }

    /// <summary>Decodes an even-length UTF-16BE byte sequence to a string. A trailing odd byte (malformed input) is dropped.</summary>
    private static string DecodeUtf16BigEndian(ReadOnlySpan<byte> bytes)
    {
        var count = bytes.Length / 2;
        if (count == 0)
        {
            return string.Empty;
        }

        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = (char)((bytes[2 * i] << 8) | bytes[(2 * i) + 1]);
        }

        return new string(chars);
    }
}
