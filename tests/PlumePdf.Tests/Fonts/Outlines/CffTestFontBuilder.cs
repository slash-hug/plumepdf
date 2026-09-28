using System.Globalization;

namespace PlumePdf.Tests.Fonts.Outlines;

/// <summary>
/// Byte-exact bare-CFF assembler used across these tests: one shared builder so
/// <c>CffParserTests</c>
/// and <c>TextRenderingTests</c> assemble their fixtures from the same reading of Adobe TN #5176
/// rather than two hand-rolled blobs that could agree with each other and disagree with the
/// spec (the same lesson a GSUB field-order mismatch taught elsewhere). Everything <see cref="CffTestBuilder"/>
/// (the original minimal builder) can express, plus: a String INDEX, a charset (formats 0/1/2
/// or a predefined id), an Encoding (predefined, or format 0/1 with supplements),
/// <c>FontMatrix</c>, <c>CharstringType</c>, a Private DICT with local Subrs, and CID-keyed
/// structure (<c>ROS</c> + FDArray + FDSelect). Callers may ADD members; existing signatures
/// are frozen.
/// </summary>
/// <remarks>
/// Layout: header · Name INDEX · Top DICT INDEX · String INDEX · Global Subr INDEX · charset ·
/// Encoding · CharStrings INDEX · Private DICT (+ local Subrs INDEX) · FDArray INDEX · FDSelect.
/// Every Top DICT offset operand uses the fixed 5-byte <c>29 int32</c> form so the Top DICT's
/// length is independent of the offsets' values; the layout is therefore computed in one pass
/// after measuring a zero-offset Top DICT.
/// </remarks>
internal static class CffTestFontBuilder
{
    /// <summary>CID-keyed structure: <c>ROS</c> strings (appended to the String INDEX automatically), one FD index per glyph (FDSelect format 0), and the FD count. Every FD DICT points at the same Private DICT.</summary>
    /// <param name="Registry">ROS registry string, e.g. <c>"Adobe"</c>.</param>
    /// <param name="Ordering">ROS ordering string, e.g. <c>"Identity"</c>.</param>
    /// <param name="Supplement">ROS supplement number.</param>
    /// <param name="FdSelect">FD index per glyph, GID 0 included (count must equal the charstring count).</param>
    /// <param name="FdCount">Number of FD DICTs to emit in the FDArray (every referenced FD index must be below this).</param>
    public sealed record CidOptions(string Registry, string Ordering, int Supplement, IReadOnlyList<byte> FdSelect, int FdCount);

    /// <summary>Assembles a bare CFF program.</summary>
    /// <param name="charstrings">Type 2 charstrings, GID order; entry 0 is <c>.notdef</c>. Use <see cref="Number"/> for operands.</param>
    /// <param name="globalSubrs">Global Subr INDEX entries (bias applies as in the spec).</param>
    /// <param name="localSubrs">Private DICT <c>Subrs</c> entries; emits a Private DICT with a <c>Subrs</c> operator when non-empty.</param>
    /// <param name="customStrings">String INDEX entries; entry <c>i</c> is SID <c>391 + i</c>.</param>
    /// <param name="charsetSids">Charset table: the SID (non-CID) or CID (CID-keyed) for every glyph from GID 1 upward (count = <c>charstrings.Count - 1</c>). Emitted in <paramref name="charsetFormat"/>. Mutually exclusive with <paramref name="charsetPredefined"/>.</param>
    /// <param name="charsetFormat">0, 1, or 2 — the charset table format used when <paramref name="charsetSids"/> is given.</param>
    /// <param name="charsetPredefined">Writes the Top DICT <c>charset</c> operand as a predefined id (0 = ISOAdobe, 1 = Expert, 2 = ExpertSubset) with no table. When both this and <paramref name="charsetSids"/> are null the operator is omitted (the ISOAdobe default).</param>
    /// <param name="encodingPredefined">Writes the Top DICT <c>Encoding</c> operand as a predefined id (0 = Standard, 1 = Expert) with no table. When every encoding parameter is null the operator is omitted (the Standard default).</param>
    /// <param name="encodingCodes">Encoding format 0: the code assigned to GID <c>i + 1</c> for each entry.</param>
    /// <param name="encodingRanges">Encoding format 1: <c>(first code, nLeft)</c> ranges assigned to consecutive GIDs starting at 1. Mutually exclusive with <paramref name="encodingCodes"/>.</param>
    /// <param name="encodingSupplements">Encoding supplements: <c>(code, SID)</c> pairs appended after the format 0/1 table (sets the format byte's high bit).</param>
    /// <param name="fontMatrix">Top DICT <c>FontMatrix</c> (six reals, nibble-encoded); omitted when null.</param>
    /// <param name="charstringType">Top DICT <c>CharstringType</c>; omitted when null.</param>
    /// <param name="cid">CID-keyed structure; when non-null the Top DICT starts with <c>ROS</c> and carries <c>FDArray</c>/<c>FDSelect</c>, and the Private DICT is referenced from every FD DICT instead of the Top DICT.</param>
    public static byte[] Build(
        IReadOnlyList<byte[]> charstrings,
        IReadOnlyList<byte[]>? globalSubrs = null,
        IReadOnlyList<byte[]>? localSubrs = null,
        IReadOnlyList<string>? customStrings = null,
        IReadOnlyList<int>? charsetSids = null,
        int charsetFormat = 0,
        int? charsetPredefined = null,
        int? encodingPredefined = null,
        IReadOnlyList<byte>? encodingCodes = null,
        IReadOnlyList<(byte First, byte NLeft)>? encodingRanges = null,
        IReadOnlyList<(byte Code, int Sid)>? encodingSupplements = null,
        double[]? fontMatrix = null,
        int? charstringType = null,
        CidOptions? cid = null)
    {
        ArgumentNullException.ThrowIfNull(charstrings);
        if (charstrings.Count == 0)
        {
            throw new ArgumentException("A CFF program needs at least the .notdef charstring.", nameof(charstrings));
        }

        if (charsetSids is not null && charsetPredefined is not null)
        {
            throw new ArgumentException("charsetSids and charsetPredefined are mutually exclusive.");
        }

        if (charsetSids is not null && charsetSids.Count != charstrings.Count - 1)
        {
            throw new ArgumentException($"charsetSids must have one entry per glyph from GID 1 ({charstrings.Count - 1}), got {charsetSids.Count}.", nameof(charsetSids));
        }

        if (encodingCodes is not null && encodingRanges is not null)
        {
            throw new ArgumentException("encodingCodes and encodingRanges are mutually exclusive.");
        }

        if (fontMatrix is not null && fontMatrix.Length != 6)
        {
            throw new ArgumentException("FontMatrix needs exactly six values.", nameof(fontMatrix));
        }

        if (cid is not null && cid.FdSelect.Count != charstrings.Count)
        {
            throw new ArgumentException("cid.FdSelect must have one FD index per glyph, GID 0 included.", nameof(cid));
        }

        globalSubrs ??= [];
        localSubrs ??= [];
        var strings = new List<string>(customStrings ?? []);
        int rosRegistrySid = 0, rosOrderingSid = 0;
        if (cid is not null)
        {
            rosRegistrySid = 391 + strings.Count;
            strings.Add(cid.Registry);
            rosOrderingSid = 391 + strings.Count;
            strings.Add(cid.Ordering);
        }

        byte[] header = [1, 0, 4, 4];
        var nameIndex = BuildIndex([System.Text.Encoding.ASCII.GetBytes("PlumeTest")]);
        var stringIndex = BuildIndex(strings.Select(static s => System.Text.Encoding.ASCII.GetBytes(s)).ToList());
        var globalSubrIndex = BuildIndex(globalSubrs);

        var charsetBlob = charsetSids is null ? [] : BuildCharset(charsetSids, charsetFormat);
        var hasEncodingTable = cid is null && (encodingCodes is not null || encodingRanges is not null || encodingSupplements is not null);
        var encodingBlob = hasEncodingTable ? BuildEncoding(encodingCodes, encodingRanges, encodingSupplements) : [];
        var charStringsIndex = BuildIndex(charstrings);

        // Private DICT: Subrs offset is relative to the Private DICT's own start, so the DICT is
        // exactly [29 int32 19] (6 bytes) when subrs exist and empty otherwise.
        byte[] privateDict = localSubrs.Count > 0 ? [29, .. Int32Be(6), 19] : [];
        var localSubrIndex = localSubrs.Count > 0 ? BuildIndex(localSubrs) : [];
        byte[] privateBlob = [.. privateDict, .. localSubrIndex];

        // One-pass layout: measure the Top DICT with zero offsets (fixed-width operands).
        var topDictIndexLength = BuildIndex([BuildTopDict(0, 0, 0, 0, 0, 0)]).Length;
        var prefixLength = header.Length + nameIndex.Length + topDictIndexLength + stringIndex.Length + globalSubrIndex.Length;
        var charsetOffset = prefixLength;
        var encodingOffset = charsetOffset + charsetBlob.Length;
        var charStringsOffset = encodingOffset + encodingBlob.Length;
        var privateOffset = charStringsOffset + charStringsIndex.Length;
        var fdArrayOffset = privateOffset + privateBlob.Length;

        byte[] fdArrayIndex = [];
        byte[] fdSelectBlob = [];
        if (cid is not null)
        {
            var fdDicts = new List<byte[]>();
            for (var i = 0; i < cid.FdCount; i++)
            {
                fdDicts.Add([29, .. Int32Be(privateDict.Length), 29, .. Int32Be(privateOffset), 18]); // Private: size offset
            }

            fdArrayIndex = BuildIndex(fdDicts);
            fdSelectBlob = [0, .. cid.FdSelect]; // FDSelect format 0: one FD byte per glyph.
        }

        var fdSelectOffset = fdArrayOffset + fdArrayIndex.Length;

        var topDictIndex = BuildIndex([BuildTopDict(charsetOffset, encodingOffset, charStringsOffset, privateOffset, fdArrayOffset, fdSelectOffset)]);
        if (topDictIndex.Length != topDictIndexLength)
        {
            throw new InvalidOperationException("Top DICT length changed with offsets — operands must stay fixed-width.");
        }

        return
        [
            .. header, .. nameIndex, .. topDictIndex, .. stringIndex, .. globalSubrIndex,
            .. charsetBlob, .. encodingBlob, .. charStringsIndex, .. privateBlob, .. fdArrayIndex, .. fdSelectBlob,
        ];

        byte[] BuildTopDict(int charsetOff, int encodingOff, int charStringsOff, int privateOff, int fdArrayOff, int fdSelectOff)
        {
            List<byte> d = [];
            if (cid is not null)
            {
                d.AddRange([29, .. Int32Be(rosRegistrySid), 29, .. Int32Be(rosOrderingSid), 29, .. Int32Be(cid.Supplement), 12, 30]); // ROS — must come first.
            }

            if (fontMatrix is not null)
            {
                foreach (var v in fontMatrix)
                {
                    d.AddRange(Real(v));
                }

                d.AddRange([12, 7]);
            }

            if (charstringType is { } cst)
            {
                d.AddRange([29, .. Int32Be(cst), 12, 6]);
            }

            if (charsetPredefined is { } cp)
            {
                d.AddRange([29, .. Int32Be(cp), 15]);
            }
            else if (charsetSids is not null)
            {
                d.AddRange([29, .. Int32Be(charsetOff), 15]);
            }

            if (cid is null)
            {
                if (encodingPredefined is { } ep)
                {
                    d.AddRange([29, .. Int32Be(ep), 16]);
                }
                else if (hasEncodingTable)
                {
                    d.AddRange([29, .. Int32Be(encodingOff), 16]);
                }
            }

            d.AddRange([29, .. Int32Be(charStringsOff), 17]);

            if (cid is null)
            {
                if (privateBlob.Length > 0)
                {
                    d.AddRange([29, .. Int32Be(privateDict.Length), 29, .. Int32Be(privateOff), 18]);
                }
            }
            else
            {
                d.AddRange([29, .. Int32Be(fdArrayOff), 12, 36]);
                d.AddRange([29, .. Int32Be(fdSelectOff), 12, 37]);
            }

            return [.. d];
        }
    }

    /// <summary>CFF/Type 2 integer operand encoding (Adobe TN #5176 Table 3 / #5177's shared 32-254 scheme) for charstrings. Values outside ±1131 use the 3-byte <c>28</c> form.</summary>
    /// <param name="v">The operand value.</param>
    public static byte[] Number(int v) => v switch
    {
        >= -107 and <= 107 => [(byte)(v + 139)],
        >= 108 and <= 1131 => [(byte)(((v - 108) / 256) + 247), (byte)((v - 108) % 256)],
        >= -1131 and <= -108 => [(byte)(((-v - 108) / 256) + 251), (byte)((-v - 108) % 256)],
        _ => [28, (byte)(v >> 8), (byte)v],
    };

    /// <summary>DICT real-number operand (TN #5176 Table 5: <c>30</c> followed by packed nibbles — digits, <c>a</c> = '.', <c>b</c> = 'E', <c>c</c> = 'E-', <c>e</c> = '-', <c>f</c> = end).</summary>
    /// <param name="value">The real value; rendered with up to 15 fractional digits, invariant culture.</param>
    public static byte[] Real(double value)
    {
        var text = value.ToString("0.###############", CultureInfo.InvariantCulture);
        List<byte> nibbles = [];
        foreach (var ch in text)
        {
            nibbles.Add(ch switch
            {
                >= '0' and <= '9' => (byte)(ch - '0'),
                '.' => 0xA,
                '-' => 0xE,
                _ => throw new ArgumentException($"Unexpected character '{ch}' in real operand text '{text}'."),
            });
        }

        nibbles.Add(0xF);
        if (nibbles.Count % 2 == 1)
        {
            nibbles.Add(0xF);
        }

        List<byte> result = [30];
        for (var i = 0; i < nibbles.Count; i += 2)
        {
            result.Add((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }

        return [.. result];
    }

    /// <summary>A CFF INDEX over <paramref name="entries"/> (count · offSize · offsets · data), choosing the smallest offSize (1, 2, or 4) that fits.</summary>
    /// <param name="entries">The INDEX entries in order.</param>
    public static byte[] BuildIndex(IReadOnlyList<byte[]> entries)
    {
        if (entries.Count == 0)
        {
            return [0, 0];
        }

        var offsets = new int[entries.Count + 1];
        offsets[0] = 1;
        for (var i = 0; i < entries.Count; i++)
        {
            offsets[i + 1] = offsets[i] + entries[i].Length;
        }

        var offSize = offsets[^1] <= 0xFF ? 1 : offsets[^1] <= 0xFFFF ? 2 : 4;
        List<byte> result = [(byte)(entries.Count >> 8), (byte)entries.Count, (byte)offSize];
        foreach (var o in offsets)
        {
            for (var b = offSize - 1; b >= 0; b--)
            {
                result.Add((byte)(o >> (8 * b)));
            }
        }

        foreach (var e in entries)
        {
            result.AddRange(e);
        }

        return [.. result];
    }

    private static byte[] BuildCharset(IReadOnlyList<int> sids, int format)
    {
        List<byte> c = [(byte)format];
        switch (format)
        {
            case 0:
                foreach (var sid in sids)
                {
                    c.AddRange(UInt16Be(sid));
                }

                break;

            case 1:
            case 2:
                {
                    var i = 0;
                    while (i < sids.Count)
                    {
                        var first = sids[i];
                        var nLeft = 0;
                        var maxLeft = format == 1 ? 0xFF : 0xFFFF;
                        while (i + nLeft + 1 < sids.Count && sids[i + nLeft + 1] == first + nLeft + 1 && nLeft < maxLeft)
                        {
                            nLeft++;
                        }

                        c.AddRange(UInt16Be(first));
                        if (format == 1)
                        {
                            c.Add((byte)nLeft);
                        }
                        else
                        {
                            c.AddRange(UInt16Be(nLeft));
                        }

                        i += nLeft + 1;
                    }

                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(format), "Charset format must be 0, 1, or 2.");
        }

        return [.. c];
    }

    private static byte[] BuildEncoding(IReadOnlyList<byte>? codes, IReadOnlyList<(byte First, byte NLeft)>? ranges, IReadOnlyList<(byte Code, int Sid)>? supplements)
    {
        List<byte> e = [];
        var format = ranges is not null ? 1 : 0;
        if (supplements is { Count: > 0 })
        {
            format |= 0x80;
        }

        e.Add((byte)format);
        if (ranges is not null)
        {
            e.Add((byte)ranges.Count);
            foreach (var (first, nLeft) in ranges)
            {
                e.Add(first);
                e.Add(nLeft);
            }
        }
        else
        {
            codes ??= [];
            e.Add((byte)codes.Count);
            e.AddRange(codes);
        }

        if (supplements is { Count: > 0 })
        {
            e.Add((byte)supplements.Count);
            foreach (var (code, sid) in supplements)
            {
                e.Add(code);
                e.AddRange(UInt16Be(sid));
            }
        }

        return [.. e];
    }

    private static byte[] Int32Be(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] UInt16Be(int value) => [(byte)(value >> 8), (byte)value];
}
