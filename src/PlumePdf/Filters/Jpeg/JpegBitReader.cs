namespace PlumePdf.Filters;

/// <summary>
/// MSB-first bit reader over a JPEG entropy-coded segment (ISO/IEC 10918-1 §F.2.2.5), one
/// raw byte at a time (never a multi-byte lookahead buffer) so a restart marker or the
/// scan's terminating marker can be recognized at exactly the byte where it starts. Handles
/// byte stuffing per §B.1.1.5: a <c>0xFF</c> data byte is followed by a stuffed <c>0x00</c>
/// in the encoded stream, transparently removed here; any other byte following
/// <c>0xFF</c> is a marker — this reader stops supplying bits at that point (without
/// consuming the marker) rather than guessing, so the caller decides what to do (resync on
/// a restart marker, or hand control back to the marker-driven parse loop).
/// </summary>
internal sealed class JpegBitReader
{
    private readonly byte[] _data;
    private int _position;
    private int _bitBuffer;
    private int _bitCount;

    /// <summary>Creates a reader starting at <paramref name="startPosition"/> in <paramref name="data"/>.</summary>
    public JpegBitReader(byte[] data, int startPosition)
    {
        _data = data;
        _position = startPosition;
    }

    /// <summary>
    /// The current byte offset into the source buffer. While bits remain buffered from an
    /// already-consumed byte this still reflects "one past the last raw byte pulled in" —
    /// callers that need a marker-aligned position call <see cref="TryConsumeRestartMarker"/>
    /// or rely on the fact that a stopped decode (see the type summary) always leaves this
    /// pointing exactly at the unconsumed marker's leading <c>0xFF</c>.
    /// </summary>
    public int Position => _position;

    /// <summary>Reads a single bit. Returns <see langword="false"/> (no bit produced) at a marker boundary or end of data.</summary>
    public bool TryReadBit(out int bit)
    {
        if (!EnsureBits(1))
        {
            bit = 0;
            return false;
        }

        _bitCount--;
        bit = (_bitBuffer >> _bitCount) & 1;
        return true;
    }

    /// <summary>Reads <paramref name="count"/> bits (0-16) as an unsigned value, MSB first. Returns <see langword="false"/> at a marker boundary or end of data.</summary>
    public bool TryReadBits(int count, out int value)
    {
        if (count == 0)
        {
            value = 0;
            return true;
        }

        if (!EnsureBits(count))
        {
            value = 0;
            return false;
        }

        _bitCount -= count;
        value = (_bitBuffer >> _bitCount) & ((1 << count) - 1);
        return true;
    }

    /// <summary>
    /// Decodes one symbol using <paramref name="table"/> (§F.2.2.3's canonical Huffman walk).
    /// Returns <see langword="false"/> at a marker boundary/end of data, or when 16 bits are
    /// consumed without matching any code (a corrupt table or misaligned stream).
    /// </summary>
    /// <remarks>
    /// A performance pass found that when 8 bits can be buffered, the leading code resolves through
    /// the table's one-probe lookahead (short codes dominate real scans — the old shape paid a
    /// bit-at-a-time walk of up to three nested calls per bit). Consumed bits and results are
    /// identical to the walk: a lookahead miss proves no code of length ≤ 8 matches, so the
    /// canonical walk resumes at length 9 over the same bits. Near a marker or end of data,
    /// buffering 8 bits fails (the raw-byte puller never consumes marker bytes) and the
    /// original bit-at-a-time walk runs instead, preserving the exact stop-at-marker geometry.
    /// </remarks>
    public bool TryDecodeHuffman(JpegHuffmanTable table, out int symbol)
    {
        if (EnsureBits(8))
        {
            var peek = (_bitBuffer >> (_bitCount - 8)) & 0xFF;
            if (table.TryLookupFast(peek, out symbol, out var fastLength))
            {
                _bitCount -= fastLength;
                return true;
            }

            // No code of length <= 8 prefixes these bits: consume them and continue the
            // canonical walk from length 9, exactly where the bit-at-a-time walk would be.
            _bitCount -= 8;
            var longCode = peek;
            for (var length = 9; length <= 16; length++)
            {
                if (!TryReadBit(out var bit))
                {
                    symbol = 0;
                    return false;
                }

                longCode = (longCode << 1) | bit;
                if (table.TryLookup(length, longCode, out symbol))
                {
                    return true;
                }
            }

            symbol = 0;
            return false;
        }

        var code = 0;
        for (var length = 1; length <= 16; length++)
        {
            if (!TryReadBit(out var bit))
            {
                symbol = 0;
                return false;
            }

            code = (code << 1) | bit;
            if (table.TryLookup(length, code, out symbol))
            {
                return true;
            }
        }

        symbol = 0;
        return false;
    }

    /// <summary>
    /// Decodes a JPEG magnitude-category value (§F.2.2.1's RECEIVE + EXTEND, used for both DC
    /// diffs and AC coefficient magnitudes): reads <paramref name="numBits"/> raw bits, then
    /// sign-extends a value whose top bit was 0 into the negative half of the category's
    /// range. <paramref name="numBits"/> of 0 always yields 0 without reading any bits.
    /// </summary>
    public bool TryReceiveExtend(int numBits, out int value)
    {
        if (numBits == 0)
        {
            value = 0;
            return true;
        }

        if (!TryReadBits(numBits, out var raw))
        {
            value = 0;
            return false;
        }

        var half = 1 << (numBits - 1);
        value = raw < half ? raw - (1 << numBits) + 1 : raw;
        return true;
    }

    /// <summary>
    /// Discards any bits buffered from an already-consumed byte (the padding bits before a
    /// restart marker are defined to be 1-bits with no semantic content, §B.1.1.5) and, if the
    /// next two source bytes are <c>0xFF</c> followed by an RST0-RST7 marker (<c>0xD0</c>-<c>0xD7</c>),
    /// consumes them and returns <see langword="true"/>. Leaves the position unchanged
    /// (padding bits aside) when no restart marker is present.
    /// </summary>
    public bool TryConsumeRestartMarker()
    {
        _bitBuffer = 0;
        _bitCount = 0;

        if (_position + 1 < _data.Length && _data[_position] == 0xFF)
        {
            var next = _data[_position + 1];
            if (next is >= 0xD0 and <= 0xD7)
            {
                _position += 2;
                return true;
            }
        }

        return false;
    }

    private bool EnsureBits(int count)
    {
        while (_bitCount < count)
        {
            if (!TryReadRawByte(out var raw))
            {
                return false;
            }

            _bitBuffer = (_bitBuffer << 8) | raw;
            _bitCount += 8;
        }

        return true;
    }

    private bool TryReadRawByte(out byte value)
    {
        if (_position >= _data.Length)
        {
            value = 0;
            return false;
        }

        var b = _data[_position];
        if (b != 0xFF)
        {
            _position++;
            value = b;
            return true;
        }

        // 0xFF in entropy-coded data is either a stuffed literal (followed by 0x00) or the
        // start of a marker - either way we need to see the next byte before deciding.
        if (_position + 1 >= _data.Length)
        {
            value = 0;
            return false;
        }

        var next = _data[_position + 1];
        if (next == 0x00)
        {
            _position += 2;
            value = 0xFF;
            return true;
        }

        // A real marker: stop here without consuming it (leave _position at the leading
        // 0xFF) so the caller - restart resync or the outer marker-driven parse loop - can
        // see it.
        value = 0;
        return false;
    }
}
