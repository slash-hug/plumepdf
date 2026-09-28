using System.Numerics;
using PlumePdf.Filters.Jbig2;
using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxMqDecoder"/> against a set of known-answer
/// reference vectors and a differential test against
/// <c>Jbig2ArithmeticDecoder</c> over fixed-seed LCG vectors (a codec
/// table is proven against an independent implementation, never by transcription care alone).
/// A third check, <see cref="TestMqEncoder_RoundTripsThroughJpxMqDecoder"/>, proves
/// <see cref="TestMqEncoder"/> itself correct before <c>JpxBlockDecoderTests</c> relies on it to
/// build hand-assembled code-block streams.
/// </summary>
public class JpxMqDecoderTests
{
    // The reference vectors below are the same known-answer inputs/outputs already carried by
    // Jbig2ArithmeticDecoderTests (cross-checked there against an independent Python
    // transcription): T.800 Annex C's software conventions and T.88 Annex E's are the same
    // MQ-coder, so JpxMqDecoder must decode these bit-for-bit identically to
    // Jbig2ArithmeticDecoder given the same input bytes and context-index sequence. Reusing
    // them here (rather than transcribing T.800's own Annex H.2 test data, which this repo
    // does not carry — the clean-room policy in AGENTS.md keeps ISO spec text, including its
    // worked examples, out of the tree) gives an exact, independently-already-verified
    // known-answer check; the differential test below is what actually proves table equality,
    // not this fixed-vector check.
    //
    // JpxMqDecoder has no context whose Table D.7 initial state is 4 or 46 in this cycle, so
    // context indices 1..8 (the eight zero-coding contexts other than ZC0, all initial state 0
    // per Table D.7) are used in place of Jbig2ArithmeticDecoderTests' zero-initialized
    // contexts[0..7] — an exact behavioural match, not a coincidence: both are simply "8
    // contexts starting at state 0, MPS 0".
    private static int[] DecodeBits(byte[] data, int count)
    {
        var decoder = new JpxMqDecoder(data);
        var bits = new int[count];
        for (var i = 0; i < count; i++)
        {
            bits[i] = decoder.Decode(1 + (i % 8));
        }

        return bits;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Decode_MatchesKnownAnswerVectors(string hexInput, int[] expectedBits)
    {
        var data = Convert.FromHexString(hexInput);

        var actual = DecodeBits(data, expectedBits.Length);

        Assert.Equal(expectedBits, actual);
    }

    public static TheoryData<string, int[]> Vectors()
    {
        return new TheoryData<string, int[]>
        {
            {
                "0000000000000000",
                [0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0]
            },
            {
                "ffffffffffffffff",
                [1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1, 0]
            },
            {
                "84c73bfce1a1430402200000",
                [0, 0, 0, 1, 1, 1, 0, 1, 0, 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 0, 0, 1, 1, 1, 0, 1, 1, 1, 0, 1, 0, 1, 0, 1]
            },
            {
                "00ffac715500ff009933",
                [0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 1, 0, 1, 1, 0]
            },
            {
                "0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20",
                [0, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, 1, 1]
            },
        };
    }

    [Fact]
    public void Decode_DoesNotThrow_WhenInputRunsOutMidStream()
    {
        // 500 decisions over two real bytes: T.800 C.3.4's BYTEIN feeds implied 0xFF past the
        // end and decoding simply continues — there is nothing to observe or report here (the
        // block decoder's PLUME3710 keys off structural tier-2/tier-1 disagreements only).
        var decoder = new JpxMqDecoder([0x12, 0x34]);
        for (var i = 0; i < 500; i++)
        {
            var bit = decoder.Decode(1 + (i % 8));
            Assert.True(bit is 0 or 1);
        }
    }

    [Fact]
    public void Decode_EmptyInput_DoesNotThrow()
    {
        var decoder = new JpxMqDecoder([]);
        for (var i = 0; i < 16; i++)
        {
            var bit = decoder.Decode(JpxTier1Contexts.Uniform);
            Assert.True(bit is 0 or 1);
        }
    }

    /// <summary>
    /// T.800 C.3.4 pins what "past the end of the segment" means: BYTEIN behaves exactly as if
    /// the codeword were followed by an unbounded run of <c>0xFF</c> bytes — whether the segment
    /// ends on a plain byte, ends on an in-bounds <c>0xFF</c>, or is explicitly padded with
    /// <c>0xFF</c>s. A decoder over the bare bytes must therefore emit the same decisions as one
    /// over the same bytes plus explicit <c>0xFF</c> padding, for as long as either is asked.
    /// This is why over-read is not a truncation signal: a
    /// near-optimally terminated codeword (D.4.2) and a cut one look identical from here.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x12, 0x34 })]
    [InlineData(new byte[] { 0x12, 0xFF })]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x84, 0xC7, 0x3B, 0xFC, 0xE1, 0xA1, 0x43, 0x04, 0x02, 0x20, 0x00, 0x00, 0x41, 0x0D, 0xBB, 0x86, 0xF4, 0x31, 0x7F, 0xFF, 0x88, 0xFF, 0x37, 0x47, 0x1A, 0xDB, 0x6A, 0xDF, 0xFF, 0xAC })]
    public void Decode_PastEnd_IsEquivalentToExplicit0xFFPadding(byte[] bytes)
    {
        var bare = new JpxMqDecoder(bytes);
        var padded = new JpxMqDecoder([.. bytes, .. Enumerable.Repeat((byte)0xFF, 256)]);

        for (var i = 0; i < 1000; i++)
        {
            var context = i % JpxTier1Contexts.Count;
            Assert.Equal(padded.Decode(context), bare.Decode(context));
        }
    }

    [Fact]
    public void ResetContexts_RestoresTableD7InitialStates()
    {
        // Decoding the same all-zero input from a fresh construction and from a decoder that
        // ran for a while and then had ResetContexts() called must produce the same first
        // decision on JpxTier1Contexts.Uniform (state 46, the "always split half" state) —
        // proving ResetContexts actually restores Table D.7, not just zeroes everything.
        var fresh = new JpxMqDecoder(new byte[64]);
        var freshBit = fresh.Decode(JpxTier1Contexts.Uniform);

        var used = new JpxMqDecoder(new byte[64]);
        for (var i = 0; i < 40; i++)
        {
            _ = used.Decode(i % JpxTier1Contexts.Count);
        }

        used.Reinitialise(new byte[64]);
        used.ResetContexts();
        var afterReset = used.Decode(JpxTier1Contexts.Uniform);

        Assert.Equal(freshBit, afterReset);
    }

    /// <summary>
    /// <see cref="JpxMqDecoder"/> with the eight always-state-0 contexts (1..8) must
    /// decode bit-for-bit identically to <see cref="Jbig2ArithmeticDecoder"/> (contexts 0..7,
    /// also zero-initialized) over 64 fixed-seed LCG vectors of 4 KB each, given the same input
    /// bytes and the same context-index cycling sequence — proving the two independently
    /// transcribed Qe/next-state tables (and INITDEC/DECODE/BYTEIN/RENORMD structures) are
    /// equal, since a codec table is proven against an independent
    /// implementation, never by transcription care alone. Enough decode calls per vector (well
    /// beyond the 4 KB of real input) also exercises both decoders' identical past-end
    /// (implied <c>0xFF</c>, T.800 C.3.4) padding behaviour, which must agree too.
    /// </summary>
    [Fact]
    public void Decode_MatchesJbig2ArithmeticDecoder_OverFixedSeedVectors()
    {
        const int vectorCount = 64;
        const int vectorLength = 4096;
        const int decisionsPerVector = 20000;

        for (var v = 0; v < vectorCount; v++)
        {
            var data = GenerateLcgBytes(seed: 1000 + v, length: vectorLength);

            var jpx = new JpxMqDecoder(data);
            var jbig2 = new Jbig2ArithmeticDecoder(data, 0, data.Length);
            var jbig2Contexts = new byte[8];

            for (var i = 0; i < decisionsPerVector; i++)
            {
                var jpxBit = jpx.Decode(1 + (i % 8));
                var jbig2Bit = jbig2.ReadBit(jbig2Contexts, i % 8);

                Assert.True(jpxBit == jbig2Bit, $"vector {v}, decision {i}: JpxMqDecoder={jpxBit}, Jbig2ArithmeticDecoder={jbig2Bit}");
            }
        }
    }

    /// <summary>
    /// Proves <see cref="TestMqEncoder"/> (the Annex C CODEMPS/CODELPS/FLUSH encoder
    /// <c>JpxBlockDecoderTests</c> uses to hand-assemble code-block streams) actually inverts
    /// <see cref="JpxMqDecoder"/>: encode a fixed-seed random sequence of (bit, context)
    /// decisions across all 19 contexts, decode the resulting bytes back through
    /// <see cref="JpxMqDecoder"/> using the same context sequence, and require an exact match.
    /// </summary>
    [Fact]
    public void TestMqEncoder_RoundTripsThroughJpxMqDecoder()
    {
        var random = new Random(12345);
        const int count = 5000;
        var bits = new int[count];
        var contexts = new int[count];

        var encoder = new TestMqEncoder();
        for (var i = 0; i < count; i++)
        {
            contexts[i] = random.Next(JpxTier1Contexts.Count);
            bits[i] = random.Next(2);
            encoder.Encode(bits[i], contexts[i]);
        }

        var bytes = encoder.Finish();
        var decoder = new JpxMqDecoder(bytes);
        for (var i = 0; i < count; i++)
        {
            var decoded = decoder.Decode(contexts[i]);
            Assert.True(decoded == bits[i], $"decision {i} (context {contexts[i]}): expected {bits[i]}, got {decoded}");
        }
    }

    /// <summary>Deterministic, dependency-free byte generator (a classic 32-bit LCG) for the differential vectors above — same recurrence every run, no external randomness source.</summary>
    internal static byte[] GenerateLcgBytes(int seed, int length)
    {
        var state = (uint)seed;
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            state = (state * 1103515245u) + 12345u;
            bytes[i] = (byte)(state >> 16);
        }

        return bytes;
    }
}

/// <summary>
/// Test-only MQ arithmetic <em>encoder</em> (T.800 Annex C's CODEMPS/CODELPS/FLUSH software
/// conventions, derived as the exact operational inverse of <see cref="JpxMqDecoder.Decode"/>'s
/// own branch structure) — never shipped. <see cref="JpxMqDecoderTests.TestMqEncoder_RoundTripsThroughJpxMqDecoder"/>
/// proves it correct; <c>JpxBlockDecoderTests</c> uses it to hand-assemble single-block
/// codeword streams per code-block style flag, so each flag path has an exact expected
/// coefficient array to assert against.
/// </summary>
/// <remarks>
/// Internally this tracks the accumulating code register as an exact <see cref="BigInteger"/>
/// (a Qe contribution committed at renormalization step <c>k</c> is added pre-scaled by
/// <c>2^k</c>) rather than a fixed-width, carry-propagating register — mathematically the same
/// nested-interval construction real MQ hardware performs incrementally, just computed all at
/// once since a test-only encoder has no streaming constraint. <see cref="Finish"/> serializes
/// the final value with generous trailing zero padding (a valid point in the final interval)
/// and applies T.800's own <c>0xFF</c> marker-avoidance bit-stuffing — the identical rule
/// <see cref="JpxMqDecoder"/>'s <c>BYTEIN</c> and <see cref="JpxBlockDecoder"/>'s raw bypass
/// reader unstuff on the way back in.
/// </remarks>
internal sealed class TestMqEncoder
{
    private readonly record struct QeState(int Qe, byte NextMps, byte NextLps, bool SwitchMps);

    // Independently re-typed from T.800 Table C.2 (not shared code with JpxMqDecoder) — the
    // round-trip test above is what proves this copy and JpxMqDecoder's agree.
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

    private static readonly byte[] InitialStates =
    [
        4, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0,
        3,
        46,
    ];

    private readonly byte[] _contexts = new byte[JpxTier1Contexts.Count];
    private int _a = 0x8000;
    private int _shift;

    // Each MPS-side commit records the Qe committed and the total shift count AT THAT MOMENT
    // (before this call's own renormalization) rather than an immediately-accumulated value:
    // JpxMqDecoder's Chigh comparison for decision i happens at whatever shift scale was
    // reached by decision i-1's renormalization, and that scale keeps growing for every later
    // decision — so a commit made early cannot be expressed in the FINAL output's common scale
    // until the total shift count is known, which only happens once every decision has been
    // recorded. Finish() folds every commit into that common scale by shifting each one up by
    // (finalShift - shiftAtCommit) — the amount of additional precision the register gained
    // AFTER that particular commit was made.
    private readonly List<(int Qe, int ShiftAtCommit)> _commits = [];

    public TestMqEncoder() => ResetContexts();

    /// <summary>Resets all 19 contexts to their Table D.7 initial states — the encode-side twin of <see cref="JpxMqDecoder.ResetContexts"/>, for building a <c>reset</c>-style stream.</summary>
    public void ResetContexts()
    {
        // InitialStates holds raw state indices (MPS = 0 throughout); pack each into this
        // encoder's own (state << 1) | mps byte shape, matching JpxMqDecoder.ResetContexts.
        for (var i = 0; i < JpxTier1Contexts.Count; i++)
        {
            _contexts[i] = (byte)(InitialStates[i] << 1);
        }
    }

    /// <summary>Starts a fresh codeword segment (the encode-side twin of <see cref="JpxMqDecoder.Reinitialise"/>): the register resets, but contexts are left exactly as they stand — matching a <c>termall</c>-style block, where each segment is its own MQ codeword but tier-1 context adaptation is continuous across them.</summary>
    public void Reinitialise()
    {
        _a = 0x8000;
        _shift = 0;
        _commits.Clear();
    }

    /// <summary>Encodes one decision (<paramref name="bit"/>, 0 or 1) against <paramref name="contextIndex"/>.</summary>
    public void Encode(int bit, int contextIndex)
    {
        var packed = _contexts[contextIndex];
        var state = packed >> 1;
        var mps = packed & 1;
        var qe = QeTable[state];
        var aNatural = _a - qe.Qe;

        // Which sub-interval (the "MPS-side", landing where JpxMqDecoder's Chigh >= Qe branch
        // reads from, or the "LPS-side", its Chigh < Qe branch) produces the desired bit,
        // given the interval-inversion rule both exchange functions implement: derived as the
        // exact dual of JpxMqDecoder.Decode's own branch table (see that method's remarks).
        var mpsSide = (bit == mps) == (aNatural >= qe.Qe);

        if (mpsSide)
        {
            _commits.Add((qe.Qe, _shift));
            _a = aNatural;
            if ((_a & 0x8000) != 0)
            {
                // Fast path: no state change, no renormalization (mirrors JpxMqDecoder's own
                // fast-path return with the context left untouched).
                return;
            }
        }
        else
        {
            _a = qe.Qe;
        }

        if (bit == mps)
        {
            state = qe.NextMps;
        }
        else
        {
            if (qe.SwitchMps)
            {
                mps = bit;
            }

            state = qe.NextLps;
        }

        _contexts[contextIndex] = (byte)((state << 1) | mps);

        while ((_a & 0x8000) == 0)
        {
            _a <<= 1;
            _shift++;
        }
    }

    /// <summary>Serializes every encoded decision so far into a byte-stuffed codeword segment.</summary>
    public byte[] Finish()
    {
        // The 15, not 16, matches JpxMqDecoder's own INITDEC exactly: it loads two bytes (16
        // bits) but then folds in a `<<7` before the first DECODE call, which — algebraically —
        // makes the very first Chigh comparison equal to (first 16 raw bits) >> 1, i.e. a
        // 15-bit-scaled view of the stream, not a 16-bit one. Empirically confirmed by
        // TestMqEncoder_RoundTripsThroughJpxMqDecoder over thousands of decisions.
        const int frameBits = 15;
        const int safetyPadBits = 32;

        BigInteger c = 0;
        foreach (var (qe, shiftAtCommit) in _commits)
        {
            c += (BigInteger)qe << (_shift - shiftAtCommit);
        }

        var totalBits = _shift + frameBits + safetyPadBits;
        var roundedBits = ((totalBits + 7) / 8) * 8;
        var aligned = c << (roundedBits - (_shift + frameBits));

        var writer = new BitStuffWriter();
        for (var i = roundedBits - 1; i >= 0; i--)
        {
            writer.WriteBit((int)((aligned >> i) & 1));
        }

        return writer.Finish();
    }
}

/// <summary>T.800's <c>0xFF</c> marker-avoidance bit-stuffing on the output side — the exact inverse of <see cref="JpxMqDecoder"/>'s <c>BYTEIN</c> unstuffing and <see cref="JpxBlockDecoder"/>'s private raw bypass reader. Shared by <see cref="TestMqEncoder"/> and <c>JpxBlockDecoderTests</c>' own raw-bypass segment construction.</summary>
internal sealed class BitStuffWriter
{
    private readonly List<byte> _out = [];
    private int _currentByte;
    private int _bitsUsed;
    private int _capacity = 8;
    private bool _prevWasFf;

    public void WriteBit(int bit)
    {
        if (bit != 0)
        {
            _currentByte |= 1 << (_capacity - 1 - _bitsUsed);
        }

        _bitsUsed++;
        if (_bitsUsed == _capacity)
        {
            FlushByte();
        }
    }

    public byte[] Finish()
    {
        if (_bitsUsed > 0)
        {
            _out.Add((byte)_currentByte);
        }

        return [.. _out];
    }

    private void FlushByte()
    {
        _out.Add((byte)_currentByte);
        _prevWasFf = _currentByte == 0xFF;
        _currentByte = 0;
        _bitsUsed = 0;
        _capacity = _prevWasFf ? 7 : 8;
    }
}
