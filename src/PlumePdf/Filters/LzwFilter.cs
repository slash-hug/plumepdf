namespace PlumePdf.Filters;

/// <summary>
/// The <c>LZWDecode</c> filter (ISO 32000-1 §7.4.4): a variable-width (9-12 bit) LZW
/// decompressor. The dictionary starts with 256 single-byte entries (codes 0-255), a clear
/// code (256, resets the dictionary and code width mid-stream) and an end-of-data code
/// (257); new entries are appended one per decoded code, growing the dictionary toward its
/// 4096-entry ceiling. Code width grows from 9 to 12 bits as the dictionary fills;
/// <c>/EarlyChange</c> (default 1, ISO 32000-1 §7.4.4.2 Table 9) is Adobe's own
/// implementations' quirk of widening the code one dictionary entry sooner than the
/// dictionary size alone would require - <see cref="LzwFilterAdapter"/> reads it from
/// <c>/DecodeParms</c> and threads it through here. This is an independent implementation
/// written directly from the algorithm's public specification (ISO 32000-1 §7.4.4), not a
/// port or adaptation of any third-party codebase - no NOTICE attribution applies to this
/// file.
/// <see cref="PdfOptions.MaxDecompressedStreamBytes"/> caps decoded output exactly as it
/// does for <c>FlateDecode</c> - checked incrementally, so a decompression bomb is refused
/// before it can exhaust memory rather than after. Malformed input (a code the dictionary
/// has no entry for, a missing EOD code) is lenient-by-default: recorded as a
/// <c>PLUME3xxx</c> diagnostic and decoded as far as the input allows, or thrown under
/// <see cref="PdfOptions.Strict"/>.
/// </summary>
internal static class LzwFilter
{
    private const int ClearCode = 256;
    private const int EodCode = 257;
    private const int FirstAvailableCode = 258;
    private const int MinCodeBits = 9;
    private const int MaxCodeBits = 12;
    private const int MaxTableSize = 1 << MaxCodeBits; // 4096: the largest table a 12-bit code can address.

    /// <summary>Decodes LZW-encoded <paramref name="data"/> to raw bytes.</summary>
    /// <param name="data">The still-encoded bytes to decode.</param>
    /// <param name="earlyChange">The stream's effective <c>/EarlyChange</c> value (default <see langword="true"/>, i.e. 1).</param>
    /// <param name="options">The active options, including resource limits.</param>
    /// <param name="diagnostics">The collection to append recoverable-deviation entries to, if any.</param>
    /// <param name="subject">The indirect object this data belongs to, for diagnostic context.</param>
    public static byte[] Decode(ReadOnlySpan<byte> data, bool earlyChange, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        // data.Length * 3 overflows a positive int (and List<byte>'s constructor throws a bare
        // ArgumentOutOfRangeException on a negative capacity) once data exceeds ~716 MiB; widen
        // to long for the multiply. Also clamp to MaxDecompressedStreamBytes: the cap refuses
        // output past that size anyway, so reserving more up front (600 MB for a 200 MB input)
        // is pure waste an attacker controls.
        var initialCapacity = (int)Math.Min(Math.Min((long)data.Length * 3, options.MaxDecompressedStreamBytes), int.MaxValue);
        var output = new List<byte>(initialCapacity);
        var table = BuildInitialTable();
        var codeBits = MinCodeBits;
        var earlyChangeDelta = earlyChange ? 1 : 0;
        var reader = new BitReader(data);
        byte[]? previous = null;

        while (true)
        {
            if (!reader.TryReadBits(codeBits, out var code))
            {
                FilterDiagnostics.ReportDeviation("PLUME3051", "LZWDecode: input ended before an EOD (257) code was read; decoding the bytes seen so far.", options, diagnostics, subject);
                return [.. output];
            }

            if (code == EodCode)
            {
                return [.. output];
            }

            if (code == ClearCode)
            {
                table.RemoveRange(FirstAvailableCode, table.Count - FirstAvailableCode);
                codeBits = MinCodeBits;
                previous = null;
                continue;
            }

            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
            }
            else if (code == table.Count && previous is not null)
            {
                // The classic LZW "KwKwK" case: the encoder emitted the code for the very
                // entry it's about to add, because the pattern (the previous entry followed
                // by its own first byte) had just been observed for the first time.
                entry = Append(previous, previous[0]);
            }
            else
            {
                FilterDiagnostics.ReportDeviation("PLUME3050", $"LZWDecode: code {code} does not reference a populated dictionary entry (table has {table.Count} entries); decoding the bytes seen so far.", options, diagnostics, subject);
                return [.. output];
            }

            AppendWithCap(output, entry, options);

            if (previous is not null && table.Count < MaxTableSize)
            {
                table.Add(Append(previous, entry[0]));

                // Bump the code width once the table has grown far enough that the very
                // next code read (which may legitimately be the KwKwK case referencing the
                // entry at index table.Count, not yet added) might not fit in the current
                // width - one dictionary entry sooner than strictly necessary when
                // /EarlyChange is in effect (the default).
                if (table.Count + earlyChangeDelta >= (1 << codeBits) && codeBits < MaxCodeBits)
                {
                    codeBits++;
                }
            }

            previous = entry;
        }
    }

    private static void AppendWithCap(List<byte> output, byte[] entry, PdfOptions options)
    {
        if (output.Count + entry.Length > options.MaxDecompressedStreamBytes)
        {
            throw new PlumePdfException("PLUME3052", $"LZWDecode: decompressed output exceeds the {options.MaxDecompressedStreamBytes}-byte cap (PdfOptions.MaxDecompressedStreamBytes) - refusing to continue (possible decompression bomb).");
        }

        output.AddRange(entry);
    }

    private static byte[] Append(byte[] prefix, byte last)
    {
        var result = new byte[prefix.Length + 1];
        prefix.CopyTo(result, 0);
        result[^1] = last;
        return result;
    }

    private static List<byte[]> BuildInitialTable()
    {
        var table = new List<byte[]>(MaxTableSize);
        for (var i = 0; i < 256; i++)
        {
            table.Add([(byte)i]);
        }

        table.Add([]); // 256: clear code - never looked up as data.
        table.Add([]); // 257: EOD code - never looked up as data.
        return table;
    }

    /// <summary>A minimal MSB-first bit reader over a byte span, sized exactly for LZW's 9-12 bit codes.</summary>
    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bytePosition;
        private uint _bitBuffer;
        private int _bitCount;

        public bool TryReadBits(int bits, out int value)
        {
            while (_bitCount < bits)
            {
                if (_bytePosition >= _data.Length)
                {
                    value = 0;
                    return false;
                }

                _bitBuffer = (_bitBuffer << 8) | _data[_bytePosition++];
                _bitCount += 8;
            }

            _bitCount -= bits;
            value = (int)((_bitBuffer >> _bitCount) & ((1u << bits) - 1));
            return true;
        }
    }
}

/// <summary>Bridges the internal, object-model-free <see cref="LzwFilter"/> to the public <see cref="IPdfFilter"/> seam, reading <c>/EarlyChange</c> from <c>/DecodeParms</c>.</summary>
internal sealed class LzwFilterAdapter : IPdfFilterWithDecodeParms
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        LzwFilter.Decode(data.Span, earlyChange: true, options, diagnostics, subject);

    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfDictionary? decodeParms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, Func<IndirectReference, object?>? resolver = null) =>
        LzwFilter.Decode(data.Span, ReadEarlyChange(decodeParms, options, diagnostics, subject), options, diagnostics, subject);

    private static bool ReadEarlyChange(PdfDictionary? decodeParms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (decodeParms is null || !decodeParms.TryGetValue(PdfName.EarlyChange, out var value))
        {
            return true; // ISO 32000-1 §7.4.4.2 Table 9: /EarlyChange defaults to 1.
        }

        if (value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var result) && result is 0 or 1)
        {
            return result == 1;
        }

        FilterDiagnostics.ReportDeviation("PLUME3053", "LZWDecode: /EarlyChange must be the integer 0 or 1; defaulting to 1.", options, diagnostics, subject);
        return true;
    }
}
