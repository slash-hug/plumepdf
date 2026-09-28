namespace PlumePdf.Filters.Tiff;

/// <summary>
/// Reads a TIFF 6.0 container's Image File Directory chain: byte order (<c>II</c>/<c>MM</c>),
/// the classic 42 magic number, and each IFD's tag entries — independent of any PDF type
/// (the same "container-agnostic core" shape and reasoning as <see cref="CcittFaxEngine"/>).
/// This file owns only the container/directory parse; <see cref="TiffFrameDecoder"/> (a
/// separate file, same folder) turns one <see cref="TiffIfd"/> plus the source bytes into
/// decoded pixels.
/// </summary>
/// <remarks>
/// <b>IFD-chain safety (R1, PLUME33xx).</b> A TIFF's IFDs form a singly-linked chain via each
/// directory's trailing "next IFD offset" field; nothing in the format prevents that offset
/// from pointing backward at an already-visited IFD (accidentally, or as a crafted
/// decompression-bomb-adjacent DoS: an unbounded reader would loop forever). This reader
/// tracks every visited offset and refuses to revisit one, and separately caps the total
/// frame count against <see cref="PdfOptions.MaxImageFrames"/>
/// (<see cref="RasterImage.Decode(ReadOnlyMemory{byte},PdfOptions?)"/> passes it through
/// directly; see <see cref="DefaultMaxFrames"/> for the fallback standalone callers get).
/// </remarks>
internal static class TiffReader
{
    /// <summary>
    /// The frame-count cap fallback for callers that don't have a <see cref="PdfOptions"/>
    /// instance's <see cref="PdfOptions.MaxImageFrames"/> in hand already.
    /// </summary>
    internal const int DefaultMaxFrames = 1024;

    /// <summary>Whether <paramref name="data"/> begins with a recognized TIFF byte-order marker and the classic magic number (42).</summary>
    public static bool LooksLikeTiff(ReadOnlySpan<byte> data) =>
        data.Length >= 4 &&
        ((data[0] == 'I' && data[1] == 'I' && data[2] == 42 && data[3] == 0) ||
         (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && data[3] == 42));

    /// <summary>
    /// Parses every IFD in the chain starting at the header's first-IFD offset. Never
    /// decodes pixel data — see <see cref="TiffFrameDecoder"/> for that. A cycle or the
    /// <paramref name="maxFrames"/> cap stops the chain walk and returns whatever IFDs were
    /// found before it, with a <c>PLUME33xx</c> diagnostic; a fundamentally unreadable
    /// header (bad magic, truncated first IFD) throws.
    /// </summary>
    public static List<TiffIfd> ReadIfdChain(ReadOnlyMemory<byte> data, int maxFrames, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var span = data.Span;
        if (!LooksLikeTiff(span))
        {
            throw new PlumePdfException("PLUME3300", "The data does not begin with a recognized TIFF byte-order marker (\"II*\\0\" or \"MM\\0*\").");
        }

        var bigEndian = span[0] == 'M';
        var firstIfdOffset = ReadUInt32(span, 4, bigEndian);

        var result = new List<TiffIfd>();
        var visited = new HashSet<long>();
        long offset = firstIfdOffset;

        while (offset != 0)
        {
            if (!visited.Add(offset))
            {
                FilterDiagnostics.ReportDeviation("PLUME3302", $"TIFF: IFD chain revisits offset {offset} - a cycle, refusing to loop forever; returning the {result.Count} frame(s) found before it.", options, diagnostics, subject);
                break;
            }

            if (result.Count >= maxFrames)
            {
                throw new PlumePdfException("PLUME3303", $"TIFF: IFD chain exceeds the {maxFrames}-frame cap (PdfOptions.MaxImageFrames) - refusing to continue (possible decompression bomb).");
            }

            if (offset < 0 || offset + 2 > span.Length)
            {
                var code = result.Count == 0 ? null : "PLUME3301";
                if (code is null)
                {
                    throw new PlumePdfException("PLUME3301", $"TIFF: IFD offset {offset} is out of range for a {span.Length}-byte file.");
                }

                FilterDiagnostics.ReportDeviation(code, $"TIFF: IFD offset {offset} is out of range; returning the {result.Count} frame(s) found before it.", options, diagnostics, subject);
                break;
            }

            TiffIfd ifd;
            try
            {
                ifd = ReadOneIfd(span, offset, bigEndian);
            }
            catch (PlumePdfException) when (result.Count > 0)
            {
                FilterDiagnostics.ReportDeviation("PLUME3301", $"TIFF: IFD at offset {offset} is malformed; returning the {result.Count} frame(s) found before it.", options, diagnostics, subject);
                break;
            }

            result.Add(ifd);
            offset = ifd.NextIfdOffset;
        }

        if (result.Count == 0)
        {
            throw new PlumePdfException("PLUME3301", "TIFF: the first IFD could not be read - nothing to decode.");
        }

        return result;
    }

    private static TiffIfd ReadOneIfd(ReadOnlySpan<byte> data, long offset, bool bigEndian)
    {
        var pos = checked((int)offset);
        var entryCount = ReadUInt16(data, pos, bigEndian);
        pos += 2;

        var entries = new Dictionary<ushort, TiffEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = pos + (i * 12);
            if (entryOffset + 12 > data.Length)
            {
                throw new PlumePdfException("PLUME3301", $"TIFF: IFD at offset {offset} is truncated (entry {i} of {entryCount} runs past end of file).");
            }

            var tag = ReadUInt16(data, entryOffset, bigEndian);
            var typeCode = ReadUInt16(data, entryOffset + 2, bigEndian);
            var count = ReadUInt32(data, entryOffset + 4, bigEndian);
            var valueFieldOffset = entryOffset + 8;

            if (typeCode is < 1 or > 12)
            {
                // Unknown field type (a private/vendor extension, or a corrupt entry) -
                // skip, not fatal. A range check rather than Enum.IsDefined (AOT-safety:
                // src/ avoids System.Reflection's member-enumeration surface entirely,
                // ReflectionBanTests) - TiffFieldType's values are exactly 1-12, contiguous.
                continue;
            }

            var type = (TiffFieldType)typeCode;
            var entry = ReadEntryValue(data, tag, type, count, valueFieldOffset, bigEndian);
            entries[tag] = entry;
        }

        var nextIfdPos = pos + (entryCount * 12);
        var next = nextIfdPos + 4 <= data.Length ? ReadUInt32(data, nextIfdPos, bigEndian) : 0;
        return new TiffIfd(offset, entries, next);
    }

    private static int TypeSize(TiffFieldType type) => type switch
    {
        TiffFieldType.Byte or TiffFieldType.Ascii or TiffFieldType.SByte or TiffFieldType.Undefined => 1,
        TiffFieldType.Short or TiffFieldType.SShort => 2,
        TiffFieldType.Long or TiffFieldType.SLong or TiffFieldType.Float => 4,
        TiffFieldType.Rational or TiffFieldType.SRational or TiffFieldType.Double => 8,
        _ => 1,
    };

    private static TiffEntry ReadEntryValue(ReadOnlySpan<byte> data, ushort tag, TiffFieldType type, long count, int valueFieldOffset, bool bigEndian)
    {
        var elementSize = TypeSize(type);
        var totalSize = checked(elementSize * (int)Math.Min(count, int.MaxValue / Math.Max(1, elementSize)));

        int dataOffset;
        if (totalSize <= 4)
        {
            dataOffset = valueFieldOffset;
        }
        else
        {
            var indirect = (long)ReadUInt32(data, valueFieldOffset, bigEndian);
            if (indirect < 0 || indirect + totalSize > data.Length)
            {
                // Out-of-range indirect value: treat as an empty/zero entry rather than
                // throwing - a single malformed tag shouldn't sink the whole IFD.
                return new TiffEntry(tag, type, [], [], []);
            }

            dataOffset = checked((int)indirect);
        }

        if (dataOffset < 0 || dataOffset + totalSize > data.Length || totalSize < 0)
        {
            return new TiffEntry(tag, type, [], [], []);
        }

        var raw = data.Slice(dataOffset, totalSize).ToArray();
        var n = (int)Math.Min(count, int.MaxValue);

        var integers = Array.Empty<long>();
        var reals = Array.Empty<double>();

        switch (type)
        {
            case TiffFieldType.Byte:
            case TiffFieldType.Ascii:
            case TiffFieldType.Undefined:
                integers = new long[n];
                for (var i = 0; i < n; i++)
                {
                    integers[i] = raw[i];
                }

                break;
            case TiffFieldType.SByte:
                integers = new long[n];
                for (var i = 0; i < n; i++)
                {
                    integers[i] = unchecked((sbyte)raw[i]);
                }

                break;
            case TiffFieldType.Short:
                integers = new long[n];
                for (var i = 0; i < n; i++)
                {
                    integers[i] = ReadUInt16(raw, i * 2, bigEndian);
                }

                break;
            case TiffFieldType.SShort:
                integers = new long[n];
                for (var i = 0; i < n; i++)
                {
                    integers[i] = unchecked((short)ReadUInt16(raw, i * 2, bigEndian));
                }

                break;
            case TiffFieldType.Long:
                integers = new long[n];
                for (var i = 0; i < n; i++)
                {
                    integers[i] = ReadUInt32(raw, i * 4, bigEndian);
                }

                break;
            case TiffFieldType.SLong:
                integers = new long[n];
                for (var i = 0; i < n; i++)
                {
                    integers[i] = unchecked((int)ReadUInt32(raw, i * 4, bigEndian));
                }

                break;
            case TiffFieldType.Rational:
                reals = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var num = ReadUInt32(raw, i * 8, bigEndian);
                    var den = ReadUInt32(raw, (i * 8) + 4, bigEndian);
                    reals[i] = den == 0 ? 0 : (double)num / den;
                }

                break;
            case TiffFieldType.SRational:
                reals = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var num = unchecked((int)ReadUInt32(raw, i * 8, bigEndian));
                    var den = unchecked((int)ReadUInt32(raw, (i * 8) + 4, bigEndian));
                    reals[i] = den == 0 ? 0 : (double)num / den;
                }

                break;
            case TiffFieldType.Float:
                reals = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var bits = ReadUInt32(raw, i * 4, bigEndian);
                    reals[i] = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                }

                break;
            case TiffFieldType.Double:
                reals = new double[n];
                for (var i = 0; i < n; i++)
                {
                    var bits = ReadUInt64(raw, i * 8, bigEndian);
                    reals[i] = BitConverter.Int64BitsToDouble(unchecked((long)bits));
                }

                break;
        }

        return new TiffEntry(tag, type, integers, reals, raw);
    }

    internal static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian
            ? (ushort)((data[offset] << 8) | data[offset + 1])
            : (ushort)(data[offset] | (data[offset + 1] << 8));

    internal static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian) =>
        bigEndian
            ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]
            : ((uint)data[offset + 3] << 24) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 1] << 8) | data[offset];

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        var lo = ReadUInt32(data, offset, bigEndian);
        var hi = ReadUInt32(data, offset + 4, bigEndian);
        return bigEndian ? ((ulong)lo << 32) | hi : ((ulong)hi << 32) | lo;
    }
}
