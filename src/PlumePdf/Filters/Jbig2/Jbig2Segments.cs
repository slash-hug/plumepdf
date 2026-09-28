namespace PlumePdf.Filters.Jbig2;

/// <summary>The JBIG2 segment types (ITU-T T.88 §7.3 Table 34) this decoder recognizes by number.</summary>
internal static class Jbig2SegmentType
{
    public const byte SymbolDictionary = 0;
    public const byte IntermediateTextRegion = 4;
    public const byte ImmediateTextRegion = 6;
    public const byte ImmediateLosslessTextRegion = 7;
    public const byte PatternDictionary = 16;
    public const byte IntermediateHalftoneRegion = 20;
    public const byte ImmediateHalftoneRegion = 22;
    public const byte ImmediateLosslessHalftoneRegion = 23;
    public const byte IntermediateGenericRegion = 36;
    public const byte ImmediateGenericRegion = 38;
    public const byte ImmediateLosslessGenericRegion = 39;
    public const byte IntermediateGenericRefinementRegion = 40;
    public const byte ImmediateGenericRefinementRegion = 42;
    public const byte ImmediateLosslessGenericRefinementRegion = 43;
    public const byte PageInfo = 48;
    public const byte EndOfPage = 49;
    public const byte EndOfStripe = 50;
    public const byte EndOfFile = 51;
    public const byte Profiles = 52;
    public const byte Tables = 53;
    public const byte Extension = 62;
}

/// <summary>
/// One parsed JBIG2 segment header (ITU-T T.88 §7.2) plus the byte range of its data within
/// the buffer it was parsed from — <see cref="Jbig2Decoder"/> reads segment data directly out
/// of that buffer using <see cref="DataStart"/>/<see cref="DataLength"/> rather than copying.
/// </summary>
internal sealed class Jbig2Segment(uint number, byte type, uint pageAssociation, uint[] referredTo, int dataStart, int dataLength, int? unknownLengthRowCount = null)
{
    public uint Number { get; } = number;

    public byte Type { get; } = type;

    public uint PageAssociation { get; } = pageAssociation;

    public uint[] ReferredTo { get; } = referredTo;

    public int DataStart { get; } = dataStart;

    public int DataLength { get; } = dataLength;

    /// <summary>
    /// For an immediate generic region whose header declared an unknown data length
    /// (0xFFFFFFFF, T.88 §7.2.7): the four-byte row count that followed the terminator
    /// sequence — "the actual number of rows contained in this segment", never more than the
    /// region-info height (§7.4.6.4). <see langword="null"/> for every ordinary, explicitly-sized
    /// segment. (When the region-info height is itself 0xFFFFFFFF — a shape T.88 defines only
    /// for the PAGE height and that PDFium and pdf.js refuse — PlumePDF tolerantly takes this
    /// row count as the height; that is a leniency, not a spec rule.)
    /// </summary>
    public int? UnknownLengthRowCount { get; } = unknownLengthRowCount;
}

/// <summary>
/// Parses a JBIG2 segment stream's headers (ITU-T T.88 §7.2) — the "embedded organization"
/// PDF's <c>/JBIG2Decode</c> and <c>/JBIG2Globals</c> use (segment headers and data packed
/// back-to-back, no file header), plus tolerance for the standalone-file organization (an
/// 8-byte magic, T.88 Annex D) in case a caller feeds a raw <c>.jb2</c> file. Ported from the
/// structure of pdf.js's <c>jbig2.js</c> segment-header reader (pinned tag <c>v5.6.205</c>)
/// — a mechanical byte-layout parse, not algorithm-bearing, so the risk profile here
/// is far lower than the arithmetic decoder's.
/// </summary>
internal static class Jbig2SegmentReader
{
    // ITU-T T.88 Annex D.4.1: raw byte values, not a UTF-8 string - a "\x97..."u8 literal
    // would UTF-8-encode U+0097 as the two bytes 0xC2 0x97 instead of the single byte 0x97
    // the format actually specifies, so this is spelled as explicit byte values.
    private static ReadOnlySpan<byte> FileMagic => [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The smallest possible segment header (T.88 §7.2): 4-byte number, flags, referred-to byte, 1-byte page association, 4-byte length.</summary>
    public const int MinimumHeaderSize = 11;

    /// <summary>
    /// Parses every segment header in <paramref name="data"/>, skipping a leading file
    /// header if present. Stops (without throwing) at the first segment whose header or
    /// declared data length runs past the end of <paramref name="data"/> — the caller
    /// decides whether that's fatal (a decode-as-far-as-possible policy: whatever segments
    /// parsed cleanly are still usable).
    /// </summary>
    public static List<Jbig2Segment> ParseSegments(ReadOnlySpan<byte> data, int maxSegments) => ParseSegments(data, maxSegments, out _);

    /// <summary>
    /// As <see cref="ParseSegments(ReadOnlySpan{byte}, int)"/>, also reporting in
    /// <paramref name="stoppedAt"/> the offset at which the header walk stopped: equal to
    /// <c>data.Length</c> when every byte was consumed, smaller when a header could not be
    /// parsed (the caller decides whether unconsumed bytes deserve a diagnostic —
    /// a silently abandoned walk once painted a scanned page as blank with no diagnostic at all).
    /// </summary>
    public static List<Jbig2Segment> ParseSegments(ReadOnlySpan<byte> data, int maxSegments, out int stoppedAt)
    {
        var offset = 0;
        if (data.Length >= 13 && data[..8].SequenceEqual(FileMagic))
        {
            var flags = data[8];
            offset = 9;
            if ((flags & 0x02) == 0 && offset + 4 <= data.Length)
            {
                offset += 4; // "number of pages" field, present when the file declares a known page count.
            }
        }

        var segments = new List<Jbig2Segment>();
        while (offset < data.Length && segments.Count < maxSegments)
        {
            if (!TryParseHeader(data, offset, out var segment, out var nextOffset))
            {
                break;
            }

            segments.Add(segment);
            offset = nextOffset;
        }

        stoppedAt = offset;
        return segments;
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> data, int offset, out Jbig2Segment segment, out int nextOffset)
    {
        segment = null!;
        nextOffset = offset;

        if (offset + 11 > data.Length)
        {
            return false;
        }

        var number = Jbig2SegmentReaderInternal.ReadUInt32(data, offset);
        var flagsByte = data[offset + 4];
        var type = (byte)(flagsByte & 0x3F);
        var pageAssociationIs4Bytes = (flagsByte & 0x40) != 0;
        var pos = offset + 5;

        if (pos >= data.Length)
        {
            return false;
        }

        var refFlagsByte = data[pos];
        uint referredCount;
        int retentionBytes;
        if ((refFlagsByte >> 5) == 7)
        {
            if (pos + 4 > data.Length)
            {
                return false;
            }

            referredCount = Jbig2SegmentReaderInternal.ReadUInt32(data, pos) & 0x1FFFFFFF;
            retentionBytes = (int)((referredCount + 1 + 7) / 8);
            pos += 4 + retentionBytes;
        }
        else
        {
            referredCount = (uint)(refFlagsByte >> 5);
            pos += 1;
        }

        if (referredCount > 1_000_000 || pos < 0)
        {
            return false; // Sanity guard against a corrupt count driving an absurd allocation.
        }

        var refSize = number <= 256 ? 1 : number <= 65536 ? 2 : 4;
        var referredTo = new uint[referredCount];
        for (var i = 0; i < referredCount; i++)
        {
            if (pos + refSize > data.Length)
            {
                return false;
            }

            referredTo[i] = refSize switch
            {
                1 => data[pos],
                2 => Jbig2SegmentReaderInternal.ReadUInt16(data, pos),
                _ => Jbig2SegmentReaderInternal.ReadUInt32(data, pos),
            };
            pos += refSize;
        }

        uint pageAssociation;
        if (pageAssociationIs4Bytes)
        {
            if (pos + 4 > data.Length)
            {
                return false;
            }

            pageAssociation = Jbig2SegmentReaderInternal.ReadUInt32(data, pos);
            pos += 4;
        }
        else
        {
            if (pos + 1 > data.Length)
            {
                return false;
            }

            pageAssociation = data[pos];
            pos += 1;
        }

        if (pos + 4 > data.Length)
        {
            return false;
        }

        var dataLength = Jbig2SegmentReaderInternal.ReadUInt32(data, pos);
        pos += 4;

        if (dataLength == 0xFFFFFFFF)
        {
            // Unknown-length segment data (T.88 §7.2.7): permitted only for an immediate
            // generic region, and exactly what streaming encoders emit — Hewlett-Packard MFP
            // scans carry their whole page as one such region (the previous
            // "real-world scanned PDFs essentially never produce this" comment was wrong, and
            // the silent `return false` painted every one of those pages blank). The data runs
            // to a terminator that MQ-coded data cannot contain (0xFF 0xAC — a marker code the
            // MQ coder never emits inside a codestream; 0x00 0x00 for MMR), followed by a
            // four-byte row count giving the region's actual height when the region-info
            // height was itself unknown.
            return TryParseUnknownLengthRegion(data, number, type, pageAssociation, referredTo, pos, out segment, out nextOffset);
        }

        if (pos + dataLength > data.Length || dataLength > int.MaxValue)
        {
            return false;
        }

        segment = new Jbig2Segment(number, type, pageAssociation, referredTo, pos, (int)dataLength);
        nextOffset = pos + (int)dataLength;
        return true;
    }

    private static bool TryParseUnknownLengthRegion(ReadOnlySpan<byte> data, uint number, byte type, uint pageAssociation, uint[] referredTo, int dataStart, out Jbig2Segment segment, out int nextOffset)
    {
        segment = null!;
        nextOffset = dataStart;

        if (type != Jbig2SegmentType.ImmediateGenericRegion)
        {
            return false; // §7.2.7: only an "immediate generic region" (type 38) may leave its length unknown — pdf.js and jbig2dec agree.
        }

        // Region info (17 bytes) + generic-region flags; skip the AT bytes too so a
        // pathological AT pair (x = -1, y = -84 -> 0xFF 0xAC) cannot masquerade as the terminator.
        var flagsIndex = dataStart + Jbig2RegionInfo.ByteSize;
        if (flagsIndex >= data.Length)
        {
            return false;
        }

        var mmr = (data[flagsIndex] & 0x01) != 0;
        var template = (data[flagsIndex] >> 1) & 0x03;
        var declaredHeight = Jbig2SegmentReaderInternal.ReadUInt32(data, dataStart + 4);
        var heightKnown = declaredHeight != 0xFFFFFFFF;
        var searchFrom = flagsIndex + 1 + (mmr ? 0 : (template == 0 ? 8 : 2));
        var terminator0 = mmr ? (byte)0x00 : (byte)0xFF;
        var terminator1 = mmr ? (byte)0x00 : (byte)0xAC;

        for (var i = searchFrom; i + 6 <= data.Length; i++)
        {
            if (data[i] != terminator0 || data[i + 1] != terminator1)
            {
                continue;
            }

            // §7.4.6.4: the row count "must be no greater than the region segment bitmap
            // height". T.88 notes that 0x00 0x00 cannot occur inside MMR data and 0xFF 0xAC only
            // at the end of MQ data, so in a well-formed stream the first candidate IS the
            // terminator; the height check is defence against a corrupt stream, where taking a
            // false candidate would decode a blank region (pdf.js goes further and requires the
            // row count to equal the declared height; jbig2dec treats a larger one as fatal).
            var rowCount = Jbig2SegmentReaderInternal.ReadUInt32(data, i + 2);
            if (rowCount > int.MaxValue || (heightKnown && rowCount > declaredHeight))
            {
                continue;
            }

            // The region's coded data is everything up to the terminator; the MQ decoder treats
            // the 0xFF 0xAC marker (and end of data) as "feed 1s", so excluding it is exact.
            segment = new Jbig2Segment(number, type, pageAssociation, referredTo, dataStart, i - dataStart, (int)rowCount);
            nextOffset = i + 6;
            return true;
        }

        return false;
    }
}

/// <summary>The common 17-byte "region segment information field" (ITU-T T.88 §7.4.1) every region-type segment (generic, refinement, text, halftone) starts with.</summary>
internal readonly record struct Jbig2RegionInfo(int Width, int Height, int X, int Y, byte CombinationOperator)
{
    public const int ByteSize = 17;

    public static Jbig2RegionInfo Parse(ReadOnlySpan<byte> data, int offset)
    {
        var width = (int)Jbig2SegmentReaderInternal.ReadUInt32(data, offset);
        var height = (int)Jbig2SegmentReaderInternal.ReadUInt32(data, offset + 4);
        var x = (int)Jbig2SegmentReaderInternal.ReadUInt32(data, offset + 8);
        var y = (int)Jbig2SegmentReaderInternal.ReadUInt32(data, offset + 12);
        var combOp = (byte)(data[offset + 16] & 0x07);
        return new Jbig2RegionInfo(width, height, x, y, combOp);
    }
}

/// <summary>Byte-reading helpers shared by the region-field types above (kept separate from <see cref="Jbig2SegmentReader"/>'s private ones only because record structs can't see private members of a static class in a different partial - trivial duplication, not a design seam.</summary>
internal static class Jbig2SegmentReaderInternal
{
    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    public static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
}
