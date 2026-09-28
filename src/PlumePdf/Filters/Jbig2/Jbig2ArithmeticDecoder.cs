namespace PlumePdf.Filters.Jbig2;

/// <summary>
/// The MQ arithmetic decoder (ITU-T T.88 Annex E) that underlies every arithmetic-coded
/// JBIG2 structure — generic regions, refinement, symbol dictionaries, and text regions all
/// read bits through this same decoder against their own context arrays. Ported from pdf.js's
/// <c>jbig2.js</c> (pinned tag <c>v5.6.205</c> — the last plain-JS tag before pdf.js's WASM
/// rewrite removed the readable source) — the same MQ-coder as JPEG2000's Annex C
/// and JBIG's own arithmetic coder, so the 47-state <see cref="QeTable"/> and the
/// INITDEC/BYTEIN/DECODE procedure shapes are standard, publicly-published algorithm data
/// reproduced identically across every conformant implementation, not ISO spec prose. A
/// NOTICE entry credits this port.
/// </summary>
/// <remarks>
/// The <c>C</c> register is tracked as two 16-bit halves (<c>_chigh</c>/<c>_clow</c>, each
/// kept in a wider <see cref="int"/> for shift headroom) rather than one 32-bit value —
/// mirrors the porting source's own register split exactly, which keeps this port a faithful
/// transliteration rather than a re-derivation that could silently diverge on an edge case
/// (a genuine, spec-significant risk here: DECODE's carry propagation into <c>chigh</c> is
/// exactly the kind of one-bit-off error that would decode plausible-looking but wrong
/// bitmaps with no crash to reveal it).
/// </remarks>
internal sealed class Jbig2ArithmeticDecoder
{
    /// <summary>One MQ-coder probability-estimation state (ITU-T T.88 Table E.1): the interval <see cref="Qe"/>, the next-state indices on an MPS/LPS exchange, and whether an LPS exchange also flips which symbol is "more probable".</summary>
    private readonly record struct QeState(int Qe, byte NextMps, byte NextLps, bool SwitchMps);

    // The standard 47-entry Qe probability-estimation table (ITU-T T.88 Table E.1 / T.800
    // Annex C.2 / the original JBIG's own identical table) - the same numeric data in every
    // MQ-coder implementation regardless of source language.
    private static readonly QeState[] QeTable =
    [
        new(0x5601, 1, 1, true), new(0x3401, 2, 6, false), new(0x1801, 3, 9, false), new(0x0AC1, 4, 12, false),
        new(0x0521, 5, 29, false), new(0x0221, 38, 33, false), new(0x5601, 7, 6, true), new(0x5401, 8, 14, false),
        new(0x4801, 9, 14, false), new(0x3801, 10, 14, false), new(0x3001, 11, 17, false), new(0x2401, 12, 18, false),
        new(0x1C01, 13, 20, false), new(0x1601, 29, 21, false), new(0x5601, 15, 14, true), new(0x5401, 16, 14, false),
        new(0x5101, 17, 15, false), new(0x4801, 18, 16, false), new(0x3801, 19, 17, false), new(0x3401, 20, 18, false),
        new(0x3001, 21, 19, false), new(0x2801, 22, 19, false), new(0x2401, 23, 20, false), new(0x2201, 24, 21, false),
        new(0x1C01, 25, 22, false), new(0x1801, 26, 23, false), new(0x1601, 27, 24, false), new(0x1401, 28, 25, false),
        new(0x1201, 29, 26, false), new(0x1101, 30, 27, false), new(0x0AC1, 31, 28, false), new(0x09C1, 32, 29, false),
        new(0x08A1, 33, 30, false), new(0x0521, 34, 31, false), new(0x0441, 35, 32, false), new(0x02A1, 36, 33, false),
        new(0x0221, 37, 34, false), new(0x0141, 38, 35, false), new(0x0111, 39, 36, false), new(0x0085, 40, 37, false),
        new(0x0049, 41, 38, false), new(0x0025, 42, 39, false), new(0x0015, 43, 40, false), new(0x0009, 44, 41, false),
        new(0x0005, 45, 42, false), new(0x0001, 45, 43, false), new(0x5601, 46, 46, false),
    ];

    private readonly byte[] _data;
    private readonly int _start;
    private readonly int _end;
    private int _bp;
    private int _chigh;
    private int _clow;
    private int _ct;
    private int _a;

    public Jbig2ArithmeticDecoder(byte[] data, int start, int end)
    {
        _data = data;
        _start = start;
        _end = end;
        _bp = start;
        _chigh = start < end ? data[start] : 0xFF;
        ByteIn();
        _chigh = ((_chigh << 7) & 0xFFFF) | ((_clow >> 9) & 0x7F);
        _clow = (_clow << 7) & 0xFFFF;
        _ct -= 7;
        _a = 0x8000;
    }

    private void ByteIn()
    {
        var data = _data;
        var bp = _bp;

        if (bp < _end && data[bp] == 0xFF)
        {
            if (bp + 1 >= _end || data[bp + 1] > 0x8F)
            {
                _clow += 0xFF00;
                _ct = 8;
            }
            else
            {
                bp++;
                _clow += data[bp] << 9;
                _ct = 7;
                _bp = bp;
            }
        }
        else
        {
            bp++;
            _clow += bp < _end ? data[bp] << 8 : 0xFF00;
            _ct = 8;
            _bp = bp;
        }

        if (_clow > 0xFFFF)
        {
            _chigh += _clow >> 16;
            _clow &= 0xFFFF;
        }
    }

    /// <summary>
    /// Decodes one bit against the context state stored at <paramref name="contexts"/>[<paramref name="contextIndex"/>]
    /// — packed as <c>(qeStateIndex &lt;&lt; 1) | mps</c> — updating that entry in place.
    /// </summary>
    public int ReadBit(byte[] contexts, int contextIndex)
    {
        var state = contexts[contextIndex] >> 1;
        var mps = contexts[contextIndex] & 1;
        var qe = QeTable[state];
        var a = _a - qe.Qe;

        int d;
        if (_chigh < qe.Qe)
        {
            // exchangeLps
            if (a < qe.Qe)
            {
                a = qe.Qe;
                d = mps;
                state = qe.NextMps;
            }
            else
            {
                a = qe.Qe;
                d = 1 - mps;
                if (qe.SwitchMps)
                {
                    mps = d;
                }

                state = qe.NextLps;
            }
        }
        else
        {
            _chigh -= qe.Qe;
            if ((a & 0x8000) != 0)
            {
                _a = a;
                return mps;
            }

            // exchangeMps
            if (a < qe.Qe)
            {
                d = 1 - mps;
                if (qe.SwitchMps)
                {
                    mps = d;
                }

                state = qe.NextLps;
            }
            else
            {
                d = mps;
                state = qe.NextMps;
            }
        }

        // Renormalization (E.3.3).
        do
        {
            if (_ct == 0)
            {
                ByteIn();
            }

            a <<= 1;
            _chigh = ((_chigh << 1) & 0xFFFF) | ((_clow >> 15) & 1);
            _clow = (_clow << 1) & 0xFFFF;
            _ct--;
        }
        while ((a & 0x8000) == 0);

        _a = a;
        contexts[contextIndex] = (byte)((state << 1) | mps);
        return d;
    }
}

/// <summary>
/// The arithmetic integer decoding procedure (ITU-T T.88 Annex A.3) shared by every
/// arithmetic-coded integer field in a JBIG2 stream (symbol dictionary/text region deltas,
/// counts, coordinates). Each named procedure instance (<c>IADH</c>, <c>IADW</c>, ...) needs
/// its own 512-entry context array — <see cref="Jbig2IntegerContexts"/> is the per-procedure
/// cache that hands one out.
/// </summary>
internal static class Jbig2ArithmeticInteger
{
    /// <summary>
    /// Decodes one signed integer (or <see langword="null"/> for OOB — "out of band", the
    /// sentinel JBIG2 uses for e.g. "no more symbols in this strip"). The prefix-coded
    /// magnitude ranges (2/4/6/8/12/32 bits, offsets 0/4/20/84/340/4436) and the context
    /// PREV-update rule below are Annex A.3's procedure exactly, not an approximation.
    /// </summary>
    public static int? DecodeInteger(Jbig2ArithmeticDecoder decoder, byte[] contexts)
    {
        var prev = 1;

        int ReadBits(int length)
        {
            var v = 0;
            for (var i = 0; i < length; i++)
            {
                var bit = decoder.ReadBit(contexts, prev);
                prev = prev < 256 ? (prev << 1) | bit : (((prev << 1) | bit) & 511) | 256;
                v = (v << 1) | bit;
            }

            return v;
        }

        var sign = ReadBits(1);
        long value;
        if (ReadBits(1) == 0)
        {
            value = ReadBits(2);
        }
        else if (ReadBits(1) == 0)
        {
            value = ReadBits(4) + 4;
        }
        else if (ReadBits(1) == 0)
        {
            value = ReadBits(6) + 20;
        }
        else if (ReadBits(1) == 0)
        {
            value = ReadBits(8) + 84;
        }
        else if (ReadBits(1) == 0)
        {
            value = ReadBits(12) + 340;
        }
        else
        {
            value = (uint)ReadBits(32) + 4436;
        }

        // The 32-bit magnitude branch's Annex A.3 range (340+2^12 .. 4436+2^32-1) legitimately
        // exceeds Int32.MaxValue - a hostile or malformed stream can drive this decode down
        // that branch trivially. A raw `checked` cast would let a bare OverflowException
        // escape past this codec's per-segment fallback (which catches only PlumePdfException)
        // and crash the whole page decode instead of just this one segment - so this
        // throws the coded refusal that fallback expects instead.
        if (sign == 0)
        {
            if (value > int.MaxValue)
            {
                throw new PlumePdfException("PLUME3558", $"JBIG2: decoded integer magnitude {value} exceeds Int32 range - malformed or hostile arithmetic-coded data.");
            }

            return (int)value;
        }

        if (value > 0)
        {
            if (value > (long)int.MaxValue + 1)
            {
                throw new PlumePdfException("PLUME3558", $"JBIG2: decoded integer magnitude -{value} exceeds Int32 range - malformed or hostile arithmetic-coded data.");
            }

            return (int)-value;
        }

        return null; // OOB.
    }

    /// <summary>Decodes a symbol ID: exactly <paramref name="codeLength"/> bits, context-cached per bit position (Annex A.3's IAID procedure — simpler than <see cref="DecodeInteger"/>: no sign, no prefix code, PREV always just shifts).</summary>
    public static int DecodeIaid(Jbig2ArithmeticDecoder decoder, byte[] contexts, int codeLength)
    {
        var prev = 1;
        for (var i = 0; i < codeLength; i++)
        {
            var bit = decoder.ReadBit(contexts, prev);
            prev = (prev << 1) | bit;
        }

        return codeLength < 31 ? prev & ((1 << codeLength) - 1) : prev & 0x7FFFFFFF;
    }
}

/// <summary>
/// Hands out one 512-byte context array per named integer-decoding procedure instance
/// (<c>IADH</c>, <c>IADW</c>, <c>IAEX</c>, ...) plus one <c>(2^(codeLength+1))</c>-sized
/// array per distinct <c>IAID</c> code length, all scoped to one region/symbol-dictionary
/// decode (a fresh cache per decode, per ITU-T T.88 6.5.8.2.3 — contexts do not carry over
/// between independent symbol dictionaries or text regions, only within one).
/// </summary>
internal sealed class Jbig2IntegerContexts
{
    private const int IntegerContextSize = 512;
    private readonly Dictionary<string, byte[]> _named = [];
    private readonly Dictionary<int, byte[]> _iaid = [];

    public byte[] Get(string procedure) =>
        _named.TryGetValue(procedure, out var existing) ? existing : _named[procedure] = new byte[IntegerContextSize];

    public byte[] GetIaid(int codeLength) =>
        _iaid.TryGetValue(codeLength, out var existing) ? existing : _iaid[codeLength] = new byte[1 << (codeLength + 1)];
}
