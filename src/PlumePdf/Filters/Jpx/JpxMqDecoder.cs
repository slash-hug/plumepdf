namespace PlumePdf.Filters.Jpx;

/// <summary>
/// The tier-1 context slots the MQ decoder tracks (T.800 Table D.7): nine zero-coding
/// contexts (index 0 is the "no significant neighbour" context, ZC0), five sign-coding
/// contexts, three magnitude-refinement contexts, one run-length context, and one uniform
/// (bypass-probability) context used both for the run-mode's 2-bit position code and for the
/// segmentation-symbol check. <see cref="JpxBlockDecoder"/> is the only reader of these
/// constants — kept alongside <see cref="JpxMqDecoder"/> because Table D.7's initial-state
/// row order below follows exactly this numbering.
/// </summary>
internal static class JpxTier1Contexts
{
    /// <summary>Zero-coding context for "no significant neighbour" (h=v=d=0) — every band shares this one slot.</summary>
    public const int Zc0 = 0;

    /// <summary>Sign-coding contexts occupy 9..13 (five slots: H,V ∈ {-1,0,1} minus the four symmetric duplicates the XOR bit absorbs).</summary>
    public const int SignBase = 9;

    /// <summary>Magnitude-refinement contexts occupy 14..16: 14 = first refinement, no significant neighbour; 15 = first refinement, a significant neighbour; 16 = second-or-later refinement.</summary>
    public const int MagRefBase = 14;

    /// <summary>Run-length context: the single decision "does this whole 4-sample column stay all-insignificant".</summary>
    public const int RunLength = 17;

    /// <summary>Uniform (probability-½) context: the run mode's 2-bit position code, and the segmentation-symbol check.</summary>
    public const int Uniform = 18;

    /// <summary>Total distinct context slots (Table D.7 has exactly 19 rows).</summary>
    public const int Count = 19;
}

/// <summary>
/// MQ arithmetic decoder (T.800 Annex C, software conventions) with the 19 tier-1 contexts and
/// their Table D.7 initial states (UNIFORM 46, run-length 3, ZC0 4, others 0). Kept separate
/// from <c>Jbig2ArithmeticDecoder</c> (T.88 Annex E's same coder) so the two codecs' hot paths
/// stay decoupled; <c>JpxMqDecoderTests</c>' differential test proves the shared Qe table and
/// state machine equal by decoding the same bytes through both and comparing bits, not by
/// sharing code.
/// </summary>
internal sealed class JpxMqDecoder
{
    /// <summary>One MQ-coder probability-estimation state (T.800 Table C.2): the interval <see cref="Qe"/>, the next-state indices on an MPS/LPS exchange, and whether an LPS exchange also flips which symbol is "more probable".</summary>
    private readonly record struct QeState(int Qe, byte NextMps, byte NextLps, bool SwitchMps);

    // T.800 Table C.2 — the standard 47-entry Qe probability-estimation table. This is the same
    // published numeric data every conformant MQ-coder implementation carries (JBIG, T.88,
    // T.800 all define the identical table); transcribed here directly from the standard, not
    // from Jbig2ArithmeticDecoder.cs — JpxMqDecoderTests' differential test is what proves the
    // two transcriptions equal (a codec table is proven against an independent implementation,
    // never by transcription care alone).
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

    // T.800 Table D.7 initial states, indexed exactly per JpxTier1Contexts: ZC0 = 4, the run
    // context = 3, the uniform context = 46 (the terminal "always split half" state — Qe =
    // 0x5601 gives an even split, the closest a 47-state table gets to true 50/50), every
    // other slot (the other 8 ZC contexts, the 5 sign contexts, the 3 magnitude-refinement
    // contexts) starts at state 0.
    private static readonly byte[] InitialStates =
    [
        4, 0, 0, 0, 0, 0, 0, 0, 0, // ZC0..ZC8
        0, 0, 0, 0, 0, // 5 sign contexts
        0, 0, 0, // 3 magnitude-refinement contexts
        3, // run-length
        46, // uniform
    ];

    // Packed per-context state: (qeStateIndex << 1) | mps, one byte per slot — the same packing
    // Jbig2ArithmeticDecoder uses for its own (unrelated, differently-sized) context arrays;
    // it is simply the natural way to store "a 47-way state index plus one MPS bit" in a byte.
    private readonly byte[] _contexts = new byte[JpxTier1Contexts.Count];

    private byte[] _data;
    private int _end;
    private int _bp;

    // The code register C, tracked as a single 32-bit accumulator rather than split high/low
    // halves: Chigh is bits 16-31, Clow is bits 0-15, and RENORMD's left shift moves Clow's top
    // bit into Chigh's bottom bit for free by shifting the whole register — algebraically
    // identical to a two-register split, just a different (and, for this decoder, simpler)
    // bookkeeping choice from the same T.800 Annex C software-conventions flowchart.
    private uint _c;

    // A is tracked as a signed int, not uint: DECODE's interval subtraction (A -= Qe) is
    // compared against Qe both before and after the subtraction, and the "before" comparison
    // legitimately needs to observe a negative intermediate (the T.800 flowchart's
    // "A < Qe" test after "A = A - Qe" relies on ordinary signed arithmetic, not on A ever
    // being a valid unsigned interval width at that instant) — an unsigned field would wrap to
    // a huge positive value instead and misroute the exchange.
    private int _a;

    private int _ct;

    /// <summary>Starts decoding <paramref name="segment"/> (<c>INITDEC</c>) with every context at its Table D.7 initial state.</summary>
    public JpxMqDecoder(ReadOnlySpan<byte> segment)
    {
        _data = segment.ToArray();
        _end = _data.Length;
        ResetContexts();
        InitDec();
    }

    /// <summary>Resets all 19 contexts to their Table D.7 initial states (the <c>reset</c> code-block style, or first construction).</summary>
    public void ResetContexts()
    {
        // InitialStates holds raw Table D.7 state indices (MPS = 0 for every one of them); pack
        // each into this decoder's own (stateIndex << 1) | mps byte shape before storing it —
        // Array.Copy-ing the raw indices in directly (as this once did) silently reinterprets
        // e.g. state index 3 (the run-length context's own initial state) as packed byte 3,
        // i.e. state index 1 with MPS already flipped to 1, desynchronising decode from the
        // very first symbol.
        for (var i = 0; i < JpxTier1Contexts.Count; i++)
        {
            _contexts[i] = (byte)(InitialStates[i] << 1);
        }
    }

    /// <summary>Restarts on a new codeword segment (<c>INITDEC</c> again; contexts are kept as they stand — <see cref="ResetContexts"/> is a separate call for the <c>reset</c> style).</summary>
    public void Reinitialise(ReadOnlySpan<byte> segment)
    {
        _data = segment.ToArray();
        _end = _data.Length;
        InitDec();
    }

    /// <summary>Decodes one decision in <paramref name="contextIndex"/> (<c>DECODE</c>).</summary>
    public int Decode(int contextIndex)
    {
        var packed = _contexts[contextIndex];
        var stateIndex = packed >> 1;
        var mps = packed & 1;
        var qe = QeTable[stateIndex];
        var qeValue = qe.Qe;

        _a -= qeValue;
        int d;

        // Chigh is the top 16 bits of the 32-bit code register.
        if ((_c >> 16) < (uint)qeValue)
        {
            // LPS_EXCHANGE (T.800 Figure C.5/C.6, software-conventions form).
            if (_a < qeValue)
            {
                _a = qeValue;
                d = mps;
                stateIndex = qe.NextMps;
            }
            else
            {
                _a = qeValue;
                d = 1 - mps;
                if (qe.SwitchMps)
                {
                    mps = d;
                }

                stateIndex = qe.NextLps;
            }
        }
        else
        {
            _c -= (uint)qeValue << 16;
            if ((_a & 0x8000) != 0)
            {
                _contexts[contextIndex] = (byte)((stateIndex << 1) | mps);
                return mps;
            }

            // MPS_EXCHANGE.
            if (_a < qeValue)
            {
                d = 1 - mps;
                if (qe.SwitchMps)
                {
                    mps = d;
                }

                stateIndex = qe.NextLps;
            }
            else
            {
                d = mps;
                stateIndex = qe.NextMps;
            }
        }

        // RENORMD (T.800 Figure C.7): shift A and C left, pulling a fresh byte in via BYTEIN
        // whenever the bit counter CT runs out, until A's top (16th) bit is set again.
        do
        {
            if (_ct == 0)
            {
                ByteIn();
            }

            _a <<= 1;
            _c <<= 1;
            _ct--;
        }
        while ((_a & 0x8000) == 0);

        _contexts[contextIndex] = (byte)((stateIndex << 1) | mps);
        return d;
    }

    private void InitDec()
    {
        _bp = 0;
        var b0 = _bp < _end ? _data[_bp] : (byte)0xFF;
        _c = (uint)b0 << 16;
        ByteIn();
        _c <<= 7;
        _ct -= 7;
        _a = 0x8000;
    }

    private void ByteIn()
    {
        // BYTEIN (T.800 Figure C.8, C.3.4): the marker-avoidance stuffing rule. A 0xFF byte
        // already sitting at the read position is always followed by either a genuine stuff
        // byte (≤ 0x8F, contributing one fewer significant bit) or a marker/the end of the
        // segment, in which case the decoder feeds itself 0xFF and stops advancing. Reading past
        // the last real byte anywhere else likewise feeds an implied 0xFF. Both are the
        // standard's own definition of decoding at and beyond a codeword's end (D.4.2 lets an
        // encoder drop every trailing byte the decoder regenerates this way), so neither is
        // tracked or reported — the block decoder's PLUME3710 keys off structural facts only.
        if (_bp < _end && _data[_bp] == 0xFF)
        {
            var pastEnd = _bp + 1 >= _end;
            if (pastEnd || _data[_bp + 1] > 0x8F)
            {
                _c += 0xFF00;
                _ct = 8;
            }
            else
            {
                _bp++;
                _c += (uint)_data[_bp] << 9;
                _ct = 7;
            }
        }
        else
        {
            _bp++;
            _c += _bp < _end ? (uint)_data[_bp] << 8 : 0xFF00u;
            _ct = 8;
        }
    }
}
