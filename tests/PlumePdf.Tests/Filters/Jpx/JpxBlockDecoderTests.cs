using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxBlockDecoder"/> on hand-assembled
/// single-block streams, one per code-block style flag, built by <see cref="TestBlockEncoder"/>
/// (an independent test-only tier-1 <em>encoder</em> mirroring T.800 Annex D's context-formation
/// rules, driving <see cref="TestMqEncoder"/>) — each flag path is checked against an exact
/// expected coefficient array recovered by the production decoder, plus the
/// segmentation-symbol mismatch case, which must yield <c>PLUME3711</c> while still keeping the
/// decoded coefficients.
/// </summary>
public class JpxBlockDecoderTests
{
    private static JpxCodingStyle Coding(byte styleFlags, bool reversible53 = true) => new()
    {
        DecompositionLevels = 1,
        Xcb = 6,
        Ycb = 6,
        CodeBlockStyle = styleFlags,
        Reversible53 = reversible53,
        PrecinctExponentsX = [15, 15],
        PrecinctExponentsY = [15, 15],
        Progression = JpxProgression.Lrcp,
        Layers = 1,
        Mct = false,
        Sop = false,
        Eph = false,
    };

    private static JpxCodeBlock Block(int width, int height) => new()
    {
        X0 = 0,
        Y0 = 0,
        X1 = width,
        Y1 = height,
    };

    /// <summary>4x4 block, 3 coded planes, a mix of magnitudes/signs that exercises significance propagation (via a pre-seeded significant neighbour), magnitude refinement, and plain cleanup — every plane touches at least one sample.</summary>
    private static readonly int[] Target4X4 =
    [
        5, 0, -3, 0,
        7, -1, 0, 2,
        0, 0, -6, 0,
        1, 0, 0, -4,
    ];

    private static void AssertDecodesTo(int[] expected, int width, int height, int mb, byte styleFlags, DiagnosticCollection? diagnostics = null, bool corruptSegmentationSymbol = false)
    {
        var coding = Coding(styleFlags);
        var (tileData, segments) = TestBlockEncoder.Encode(width, height, coding, orientation: 0, mb: mb, zeroBitPlanes: 0, targetCoefficients: expected, corruptSegmentationSymbol: corruptSegmentationSymbol);

        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = tileData };
        var block = Block(width, height);
        foreach (var segment in segments)
        {
            block.Segments.Add(segment);
        }

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: mb, diagnostics);

        Assert.Equal(expected, block.Coefficients);
    }

    [Fact]
    public void Decode_DefaultStyle_RecoversExactCoefficients()
    {
        AssertDecodesTo(Target4X4, width: 4, height: 4, mb: 3, styleFlags: 0x00);
    }

    [Fact]
    public void Decode_ResetStyle_RecoversExactCoefficients()
    {
        // 0x02 = reset: contexts return to their Table D.7 initial states after every pass.
        // Correctness here does not depend on compression efficiency, only on the encoder and
        // decoder resetting at exactly the same points.
        AssertDecodesTo(Target4X4, width: 4, height: 4, mb: 3, styleFlags: 0x02);
    }

    [Fact]
    public void Decode_TermAllStyle_RecoversExactCoefficients_AcrossMultipleSegments()
    {
        // 0x04 = termall: every coding pass is its own MQ codeword segment. For a 4x4 block
        // with 3 coded planes that is 1 (plane 0 cleanup) + 2*3 (planes 1-2: sig-prop/mag-ref/
        // cleanup) = 7 segments.
        var coding = Coding(0x04);
        var (tileData, segments) = TestBlockEncoder.Encode(4, 4, coding, orientation: 0, mb: 3, zeroBitPlanes: 0, targetCoefficients: Target4X4);

        Assert.Equal(7, segments.Length);
        Assert.All(segments, s => Assert.Equal(1, s.Passes));

        AssertDecodesTo(Target4X4, width: 4, height: 4, mb: 3, styleFlags: 0x04);
    }

    [Fact]
    public void Decode_BypassStyle_RecoversExactCoefficients_UsingRawCodedPasses()
    {
        // 0x01 = bypass. Needs at least 5 coded planes (1 + 3*4 = 13 passes) so pass numbering
        // actually reaches 11, the point at which sig-prop/mag-ref switch to raw coding.
        var width = 4;
        var height = 4;
        var target = new[]
        {
            21, 0, -13, 0,
            30, -5, 0, 9,
            0, 0, -27, 0,
            3, 0, 0, -17,
        };

        var coding = Coding(0x01);
        var (tileData, segments) = TestBlockEncoder.Encode(width, height, coding, orientation: 0, mb: 5, zeroBitPlanes: 0, targetCoefficients: target);

        // At least one raw (bypass) segment must have been produced once pass 11 is reached.
        Assert.True(segments.Length >= 3, $"expected at least 3 segments (arithmetic + raw + arithmetic), got {segments.Length}");

        AssertDecodesTo(target, width, height, mb: 5, styleFlags: 0x01);
    }

    [Fact]
    public void Decode_BypassAndTermAll_RecoversExactCoefficients()
    {
        var target = new[]
        {
            21, 0, -13, 0,
            30, -5, 0, 9,
            0, 0, -27, 0,
            3, 0, 0, -17,
        };

        AssertDecodesTo(target, width: 4, height: 4, mb: 5, styleFlags: 0x01 | 0x04);
    }

    [Fact]
    public void Decode_VerticallyCausalStyle_RecoversExactCoefficients()
    {
        // 8x4 (two 4-row stripes is impossible at height 4 - use 4x8 so there IS a stripe
        // boundary the 0x08 flag actually cuts context contributions across.
        var target = new[]
        {
            5, 0, -3, 0,
            7, -1, 0, 2,
            0, 0, -6, 0,
            1, 0, 0, -4,
            -2, 0, 0, 3,
            0, 5, -1, 0,
            0, 0, 0, 6,
            -3, 0, 2, 0,
        };

        AssertDecodesTo(target, width: 4, height: 8, mb: 3, styleFlags: 0x08);
    }

    [Fact]
    public void Decode_SegmentationSymbolStyle_CorrectSymbol_RecoversExactCoefficients_NoDiagnostic()
    {
        var diagnostics = new DiagnosticCollection();

        AssertDecodesTo(Target4X4, width: 4, height: 4, mb: 3, styleFlags: 0x20, diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Code == JpxDiagnosticCodes.SegmentationSymbolMismatch);
    }

    [Fact]
    public void Decode_SegmentationSymbolMismatch_KeepsCoefficients_AndReports3711()
    {
        var diagnostics = new DiagnosticCollection();

        AssertDecodesTo(Target4X4, width: 4, height: 4, mb: 3, styleFlags: 0x20, diagnostics, corruptSegmentationSymbol: true);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == JpxDiagnosticCodes.SegmentationSymbolMismatch);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void Decode_AllZeroBlock_ProducesZeroCoefficients_NoPasses()
    {
        var coding = Coding(0x00);
        var block = Block(4, 4);
        block.ZeroBitPlanes = 3; // Mb - P = 0 -> nothing coded, block is entirely insignificant.
        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = ReadOnlyMemory<byte>.Empty };

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: 3, diagnostics: null);

        Assert.Equal(new int[16], block.Coefficients);
        Assert.Equal(0, block.DecodedPlanes);
    }

    [Fact]
    public void Decode_DefaultStyle_SegmentSplitAcrossTwoTier2Contributions_StillRecoversExactCoefficients()
    {
        // A regression test for the bug that, outside the termAll style, a
        // code-block's arithmetically-coded passes form ONE continuous MQ codeword (T.800
        // B.10.7) even when tier-2 hands its bytes over piecemeal across more than one layer's
        // packet contribution — which JpxPacketDecoder.PlanSegments does for the DEFAULT style,
        // emitting one JpxSegmentRef PER PACKET rather than one per code-block. The bug: the
        // block decoder previously called mq.Reinitialise (a fresh INITDEC) at every
        // JpxSegmentRef boundary, destroying the arithmetic-coder's in-flight state whenever a
        // continuous codeword happened to be split into more than one JpxSegmentRef.
        //
        // TestBlockEncoder (an independent, non-C#-shared tier-1 encoder) always emits exactly
        // ONE combined segment for the default style, since it encodes a code-block's passes in
        // one shot; this test manually re-splits that ONE continuous byte range into TWO
        // JpxSegmentRefs at an arbitrary interior point — reproducing exactly what tier-2 does
        // when a later layer contributes additional passes to an already-included block — and
        // proves the production decoder still recovers the exact original coefficients.
        var coding = Coding(0x00);
        var (tileData, segments) = TestBlockEncoder.Encode(4, 4, coding, orientation: 0, mb: 3, zeroBitPlanes: 0, targetCoefficients: Target4X4);
        Assert.Single(segments); // sanity: the default style really does produce one continuous codeword here.

        var whole = segments[0];
        Assert.True(whole.Length >= 2, "need at least 2 bytes to split.");
        Assert.True(whole.Passes >= 2, "need at least 2 passes to split.");
        var byteSplit = whole.Length / 2;
        var passSplit = whole.Passes / 2;
        JpxSegmentRef[] split =
        [
            whole with { Length = byteSplit, Passes = passSplit },
            whole with { Offset = whole.Offset + byteSplit, Length = whole.Length - byteSplit, Passes = whole.Passes - passSplit },
        ];

        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = tileData };
        var block = Block(4, 4);
        foreach (var segment in split)
        {
            block.Segments.Add(segment);
        }

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: 3, diagnostics: null);

        Assert.Equal(Target4X4, block.Coefficients);
    }

    [Fact]
    public void Decode_PassesExceedSchedule_WithNoDataBehindThem_DecodesTheScheduleAndReportsNothing()
    {
        var diagnostics = new DiagnosticCollection();
        var coding = Coding(0x00);
        var (tileData, segments) = TestBlockEncoder.Encode(4, 4, coding, orientation: 0, mb: 3, zeroBitPlanes: 0, targetCoefficients: Target4X4);

        // Claim far more passes than a 3-coded-plane block's 7-pass schedule holds. The decoder
        // must survive it (never index past the schedule) AND stay silent about it: a signalled
        // surplus is what OpenJPEG's own encoder emits when a block outgrows M_b, and OpenJPEG's
        // decoder skips the surplus passes without a word (t1.c opj_t1_decode_cblk,
        // `bpno_plus_one >= 1`) — see Decode_SurplusPassesBeyondMb_DecodesTopPlanesLikeOpenJpeg.
        var inflated = segments[0] with { Passes = segments[0].Passes + 50 };

        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = tileData };
        var block = Block(4, 4);
        block.Segments.Add(inflated);

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: 3, diagnostics);

        Assert.Equal(Target4X4, block.Coefficients);
        Assert.Equal(3, block.DecodedPlanes);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// Regression test (the <c>depth-12u.j2k</c> false
    /// positive): an encoder whose block magnitudes need <paramref name="surplusPlanes"/> more
    /// bit-planes than the subband's <c>M_b</c> allows still signals <c>P = 0</c> zero bit-planes
    /// (OpenJPEG codes the negative <c>band->numbps − cblk->numbps</c> as 0 in the tag tree) and
    /// the FULL pass count for its actual plane count — <c>3·(M_b + k) − 2</c> passes where the
    /// decoder's schedule holds <c>3·M_b − 2</c>. OpenJPEG's decoder (t1.c
    /// <c>opj_t1_decode_cblk</c>: the pass loop runs while <c>bpno_plus_one &gt;= 1</c>) decodes
    /// exactly the top <c>M_b</c> planes at the schedule's own weights and skips the surplus
    /// passes silently, so its coefficients come out as <c>|v| &gt;&gt; k</c> with the sign kept. This
    /// decoder must produce the identical coefficients (the oracle gate is bit-exact against
    /// <c>opj_decompress</c> on such a stream) and, like OpenJPEG, record NO diagnostic — the old
    /// <c>exceededMb</c> PLUME3710 fired ~10 times per 12-bit fixture and escalated every such
    /// page to a spurious PLUME7744. Encoded here by <see cref="TestBlockEncoder"/> with
    /// <c>mb: 3 + k</c> (its real plane count) and decoded with <c>mb: 3</c> (what the
    /// under-provisioned subband tells tier-1); the target's low <c>k</c> bits are made to vary so
    /// the truncation is observable. Default and termall styles: termall puts every surplus pass
    /// in its own segment, so the unread segments must be tolerated as well.
    /// </summary>
    [Theory]
    [InlineData(1, 0x00)]
    [InlineData(2, 0x00)]
    [InlineData(1, 0x04)]
    [InlineData(3, 0x04)]
    public void Decode_SurplusPassesBeyondMb_DecodesTopPlanesLikeOpenJpeg(int surplusPlanes, byte styleFlags)
    {
        var wide = new int[Target4X4.Length];
        for (var i = 0; i < wide.Length; i++)
        {
            var magnitude = (Math.Abs(Target4X4[i]) << surplusPlanes) | (i % (1 << surplusPlanes));
            wide[i] = Target4X4[i] < 0 ? -magnitude : magnitude;
        }

        var diagnostics = new DiagnosticCollection();
        var coding = Coding(styleFlags);
        var (tileData, segments) = TestBlockEncoder.Encode(4, 4, coding, orientation: 0, mb: 3 + surplusPlanes, zeroBitPlanes: 0, targetCoefficients: wide);
        var signalled = segments.Sum(s => s.Passes);
        Assert.Equal((3 * (3 + surplusPlanes)) - 2, signalled); // sanity: the encoder really signalled its own, larger schedule.

        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = tileData };
        var block = Block(4, 4);
        foreach (var segment in segments)
        {
            block.Segments.Add(segment);
        }

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: 3, diagnostics);

        // The top three planes of `wide` ARE Target4X4 (|v| >> k, sign kept) — the coefficient
        // OpenJPEG reconstructs when it decodes M_b planes of an (M_b + k)-plane block.
        Assert.Equal(Target4X4, block.Coefficients);
        Assert.Equal(3, block.DecodedPlanes);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// The structural rule for <c>PLUME3710</c>:
    /// tier-1 over-read is never a diagnostic — an MQ/raw decoder reading implied 0xFF bytes
    /// past a segment's real end is conformant (T.800 C.3.4 BYTEIN, D.4.2 near-optimal
    /// termination), and no byte-count threshold can separate that from a cut file. What IS
    /// detectable is a <see cref="JpxSegmentRef"/> whose declared <c>Length</c> runs past the
    /// bytes its tile-part actually holds. Keep this 4x4 block's single 14-byte, 7-pass
    /// segment reference intact but cut the tile-part's own <c>Data</c> down to 1/2/3/4 real
    /// bytes: the declared segment no longer fits, and PLUME3710 must fire in every case.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode_SegmentDeclaresMoreBytesThanTilePartHolds_Reports3710(int keepBytes)
    {
        var diagnostics = new DiagnosticCollection();
        var coding = Coding(0x00);
        var (tileData, segments) = TestBlockEncoder.Encode(4, 4, coding, orientation: 0, mb: 3, zeroBitPlanes: 0, targetCoefficients: Target4X4);
        var whole = segments[0];
        Assert.Equal(14, whole.Length); // sanity: the declared length below really does overshoot the cut tile-part.

        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = tileData.AsMemory(0, keepBytes) };
        var block = Block(4, 4);
        block.Segments.Add(whole);

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: 3, diagnostics);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == JpxDiagnosticCodes.CodeBlockTruncated);
        Assert.Contains($"declares 14 bytes but only {keepBytes} remain in tile-part 0", diagnostic.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of that structural rule: when the tile-part's <c>Data</c> AND the segment's
    /// declared <c>Length</c> are cut consistently (the shape a real truncated file presents after tier-2
    /// has already reconciled the two — <c>JpxPacketDecoder</c> rejects a packet whose declared
    /// lengths overshoot the tile-part with <c>PLUME3709</c> before any segment is recorded),
    /// the block decoder has no structural fact left to report: a 1-byte codeword is
    /// indistinguishable from an optimally terminated one (T.800 D.4.2), and the decoder must
    /// decode what it can off implied 0xFF bytes (C.3.4) with NO diagnostic — matching OpenJPEG
    /// and PDFium, which never diagnose over-read.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Decode_SegmentAndTilePartCutConsistently_NoDiagnostic(int keepBytes)
    {
        var diagnostics = new DiagnosticCollection();
        var coding = Coding(0x00);
        var (tileData, segments) = TestBlockEncoder.Encode(4, 4, coding, orientation: 0, mb: 3, zeroBitPlanes: 0, targetCoefficients: Target4X4);
        var whole = segments[0];
        Assert.Equal(14, whole.Length); // sanity: keepBytes below is a severe cut, well past any "one or two implied bytes" reading of Annex C.
        var cut = whole with { Length = keepBytes };

        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = tileData.AsMemory(0, keepBytes) };
        var block = Block(4, 4);
        block.Segments.Add(cut);

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: 3, diagnostics);

        Assert.Empty(diagnostics);
        Assert.Equal(16, block.Coefficients!.Length); // decoded what it could — every promised pass ran, off implied 0xFF once the real bytes were gone.
        Assert.Equal(3, block.DecodedPlanes);
    }

    /// <summary>
    /// Regression test (veraPDF 6.2.8.3-t02-fail-a): a single
    /// quality layer legitimately stopping short of mb-ZeroBitPlanes' theoretical ceiling — an
    /// ordinary lossy rate-distortion truncation point, not corruption — must NOT report
    /// <c>PLUME3710</c>. <c>maxPasses: 6</c> stops the encoder one pass short of this 3-coded-
    /// plane block's full 7-pass schedule (dropping only plane 2's own cleanup pass) and
    /// properly FLUSHes right there, so the decoder's trailing RENORMD legitimately reads an
    /// implied 0xFF past this shorter codeword's real end (T.800 C.3.4 BYTEIN) exactly as it
    /// would for a genuinely truncated stream — the shape the old <c>DecodedPlanes &lt;
    /// codedPlanes</c> comparison could not tell apart from real truncation, firing a false
    /// positive on every one of veraPDF-corpus's real-world, non-synthetic t02-fail-a
    /// code-blocks. Under the structural rule (<see cref="Decode_SegmentDeclaresMoreBytesThanTilePartHolds_Reports3710"/>)
    /// nothing here is reportable: the passes signalled fit M_b, one segment carries them all,
    /// and the segment fits its tile-part.
    /// </summary>
    [Fact]
    public void Decode_LayerStopsShortOfCeiling_NoTruncationDiagnostic()
    {
        var diagnostics = new DiagnosticCollection();
        var coding = Coding(0x00);
        var (tileData, segments) = TestBlockEncoder.Encode(4, 4, coding, orientation: 0, mb: 3, zeroBitPlanes: 0, targetCoefficients: Target4X4, maxPasses: 6);
        Assert.Single(segments); // sanity: the default style still yields one continuous codeword.
        Assert.Equal(6, segments[0].Passes); // sanity: the layer really did stop one pass short of the full 7-pass schedule.

        // TestMqEncoder pads its FLUSH with 32 safety bits (its own Finish() remarks) so its own
        // round-trip tests never brush the buffer's real end — the opposite of a tight, real
        // packet-header-sized segment. Trim that slack back off here so the MQ decoder's
        // trailing RENORMD genuinely reads implied 0xFF past this segment's end, exactly the
        // T.800 C.3.4 "conformant over-read near a real termination" shape a Kakadu- or
        // OpenJPEG-encoded file presents (this decoder still executes every one of the 6
        // promised passes regardless). The segment still fits the untrimmed tile-part, so no
        // structural fact is violated.
        var whole = segments[0];
        var trimmed = whole with { Length = whole.Length - 6 };

        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = tileData };
        var block = Block(4, 4);
        block.Segments.Add(trimmed);

        JpxBlockDecoder.Decode(block, [tilePart], coding, orientation: 0, mb: 3, diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Code == JpxDiagnosticCodes.CodeBlockTruncated);
        Assert.Equal(2, block.DecodedPlanes); // plane 1's cleanup (index 3) is the last pass completed; plane 2's own cleanup (index 6) was never promised.
    }
}

/// <summary>
/// Test-only tier-1 EBCOT <em>encoder</em> (T.800 Annex D, mirrored independently of
/// <see cref="JpxBlockDecoder"/>'s own private implementation — no shared code, no internals
/// exposed) driving <see cref="TestMqEncoder"/>/<see cref="BitStuffWriter"/> to hand-assemble a
/// single code-block's codeword segments for a chosen target coefficient array and style. Never
/// shipped.
/// </summary>
internal static class TestBlockEncoder
{
    private enum PassType
    {
        SigProp,
        MagRef,
        Cleanup,
    }

    public static (byte[] TileData, JpxSegmentRef[] Segments) Encode(
        int width,
        int height,
        JpxCodingStyle coding,
        int orientation,
        int mb,
        int zeroBitPlanes,
        int[] targetCoefficients,
        bool corruptSegmentationSymbol = false,
        // Stops encoding after this many of the schedule's passes and flushes right there — a
        // real, properly-FLUSHed codeword covering fewer passes than mb/zeroBitPlanes' own
        // theoretical ceiling permits, simulating a quality layer whose rate-distortion
        // truncation point legitimately stopped short of that ceiling (default: every pass,
        // i.e. the file's own layer covers the full ceiling exactly, as every other Fact here
        // assumes).
        int maxPasses = int.MaxValue)
    {
        var codedPlanes = mb - zeroBitPlanes;
        var style = coding.CodeBlockStyle;
        var bypass = (style & 0x01) != 0;
        var resetPerPass = (style & 0x02) != 0;
        var termAll = (style & 0x04) != 0;
        var vCausal = (style & 0x08) != 0;
        var segSymbol = (style & 0x20) != 0;

        var schedule = BuildSchedule(codedPlanes);

        var count = width * height;
        var significant = new bool[count];
        var refined = new bool[count];
        var sign = new bool[count];
        var attemptedThisPlane = new bool[count];
        var newThisPlane = new bool[count];
        var stripeStart = 0;

        bool IsSig(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                return false;
            }

            if (vCausal && y >= stripeStart + 4)
            {
                return false;
            }

            return significant[(y * width) + x];
        }

        int ZeroCodingContext(int x, int y)
        {
            var h = (IsSig(x - 1, y) ? 1 : 0) + (IsSig(x + 1, y) ? 1 : 0);
            var v = (IsSig(x, y - 1) ? 1 : 0) + (IsSig(x, y + 1) ? 1 : 0);
            var d = (IsSig(x - 1, y - 1) ? 1 : 0) + (IsSig(x + 1, y - 1) ? 1 : 0)
                  + (IsSig(x - 1, y + 1) ? 1 : 0) + (IsSig(x + 1, y + 1) ? 1 : 0);
            return orientation switch
            {
                1 => HorizontalVerticalTable(v, h, d),
                3 => DiagonalTable(h, v, d),
                _ => HorizontalVerticalTable(h, v, d),
            };
        }

        (int Context, int Xor) SignContext(int x, int y)
        {
            int Contribution(int nx, int ny) => !IsSig(nx, ny) ? 0 : sign[(ny * width) + nx] ? -1 : 1;
            var h = Math.Clamp(Contribution(x - 1, y) + Contribution(x + 1, y), -1, 1);
            var v = Math.Clamp(Contribution(x, y - 1) + Contribution(x, y + 1), -1, 1);
            if (h > 0)
            {
                return v switch { > 0 => (13, 0), 0 => (12, 0), _ => (11, 0) };
            }

            if (h == 0)
            {
                return v switch { > 0 => (10, 0), 0 => (9, 0), _ => (10, 1) };
            }

            return v switch { > 0 => (11, 1), 0 => (12, 1), _ => (13, 1) };
        }

        int MagRefContext(int x, int y, bool firstRefinement)
        {
            if (!firstRefinement)
            {
                return JpxTier1Contexts.MagRefBase + 2;
            }

            var any =
                IsSig(x - 1, y - 1) || IsSig(x, y - 1) || IsSig(x + 1, y - 1) ||
                IsSig(x - 1, y) || IsSig(x + 1, y) ||
                IsSig(x - 1, y + 1) || IsSig(x, y + 1) || IsSig(x + 1, y + 1);
            return any ? JpxTier1Contexts.MagRefBase + 1 : JpxTier1Contexts.MagRefBase;
        }

        int TargetBit(int idx, int plane) => (Math.Abs(targetCoefficients[idx]) >> (codedPlanes - 1 - plane)) & 1;

        bool TargetSign(int idx) => targetCoefficients[idx] < 0;

        var segments = new List<JpxSegmentRef>();
        var tileData = new List<byte>();
        var mq = new TestMqEncoder();
        BitStuffWriter? raw = null;
        var currentIsRaw = false;
        var passesInSegment = 0;

        void FlushSegment()
        {
            if (passesInSegment == 0)
            {
                return;
            }

            var bytes = currentIsRaw ? raw!.Finish() : mq.Finish();
            var offset = tileData.Count;
            tileData.AddRange(bytes);
            segments.Add(new JpxSegmentRef(0, offset, bytes.Length, passesInSegment));
            passesInSegment = 0;
        }

        void StartSegment(bool wantRaw)
        {
            FlushSegment();
            if (wantRaw)
            {
                raw = new BitStuffWriter();
            }
            else
            {
                mq.Reinitialise();
            }

            currentIsRaw = wantRaw;
        }

        void EncodeBit(int bit, int context)
        {
            if (currentIsRaw)
            {
                raw!.WriteBit(bit);
            }
            else
            {
                mq.Encode(bit, context);
            }
        }

        StartSegment(wantRaw: false); // pass 1 is always plane 0's cleanup pass - always arithmetic.
        var previousWasRaw = false;

        var passLimit = Math.Min(schedule.Length, maxPasses);
        for (var i = 0; i < passLimit; i++)
        {
            var (plane, type) = schedule[i];
            var passNumber = i + 1;
            var wantRaw = type != PassType.Cleanup && bypass && passNumber >= 11;

            if (i > 0 && (termAll || wantRaw != previousWasRaw))
            {
                StartSegment(wantRaw);
            }

            previousWasRaw = wantRaw;

            if (type == PassType.SigProp || (type == PassType.Cleanup && plane == 0))
            {
                Array.Clear(attemptedThisPlane);
                Array.Clear(newThisPlane);
            }

            switch (type)
            {
                case PassType.SigProp:
                    for (var s = 0; s < height; s += 4)
                    {
                        stripeStart = s;
                        var rows = Math.Min(4, height - s);
                        for (var x = 0; x < width; x++)
                        {
                            for (var ry = 0; ry < rows; ry++)
                            {
                                var y = s + ry;
                                var idx = (y * width) + x;
                                if (significant[idx])
                                {
                                    continue;
                                }

                                var ctx = ZeroCodingContext(x, y);
                                if (ctx == 0)
                                {
                                    continue;
                                }

                                var bit = TargetBit(idx, plane);
                                attemptedThisPlane[idx] = true;
                                EncodeBit(bit, ctx);
                                if (bit == 1)
                                {
                                    significant[idx] = true;
                                    newThisPlane[idx] = true;
                                    sign[idx] = TargetSign(idx);
                                    var signBit = sign[idx] ? 1 : 0;
                                    if (currentIsRaw)
                                    {
                                        raw!.WriteBit(signBit);
                                    }
                                    else
                                    {
                                        var (sctx, sxor) = SignContext(x, y);
                                        mq.Encode(signBit ^ sxor, sctx);
                                    }
                                }
                            }
                        }
                    }

                    break;

                case PassType.MagRef:
                    for (var s = 0; s < height; s += 4)
                    {
                        stripeStart = s;
                        var rows = Math.Min(4, height - s);
                        for (var x = 0; x < width; x++)
                        {
                            for (var ry = 0; ry < rows; ry++)
                            {
                                var y = s + ry;
                                var idx = (y * width) + x;
                                if (!significant[idx] || newThisPlane[idx])
                                {
                                    continue;
                                }

                                var firstRefinement = !refined[idx];
                                var ctx = MagRefContext(x, y, firstRefinement);
                                var bit = TargetBit(idx, plane);
                                refined[idx] = true;
                                EncodeBit(bit, ctx);
                            }
                        }
                    }

                    break;

                default: // Cleanup - always arithmetic.
                    for (var s = 0; s < height; s += 4)
                    {
                        stripeStart = s;
                        var rows = Math.Min(4, height - s);
                        for (var x = 0; x < width; x++)
                        {
                            var startRow = 0;
                            if (rows == 4)
                            {
                                var eligible = true;
                                for (var ry = 0; ry < 4 && eligible; ry++)
                                {
                                    var y = s + ry;
                                    var idx = (y * width) + x;
                                    if (significant[idx] || attemptedThisPlane[idx] || ZeroCodingContext(x, y) != 0)
                                    {
                                        eligible = false;
                                    }
                                }

                                if (eligible)
                                {
                                    var firstRow = -1;
                                    for (var ry = 0; ry < 4; ry++)
                                    {
                                        if (TargetBit(((s + ry) * width) + x, plane) == 1)
                                        {
                                            firstRow = ry;
                                            break;
                                        }
                                    }

                                    var runBit = firstRow >= 0 ? 1 : 0;
                                    mq.Encode(runBit, JpxTier1Contexts.RunLength);
                                    if (runBit == 0)
                                    {
                                        for (var ry = 0; ry < 4; ry++)
                                        {
                                            attemptedThisPlane[((s + ry) * width) + x] = true;
                                        }

                                        continue;
                                    }

                                    for (var ry = 0; ry <= firstRow; ry++)
                                    {
                                        attemptedThisPlane[((s + ry) * width) + x] = true;
                                    }

                                    mq.Encode((firstRow >> 1) & 1, JpxTier1Contexts.Uniform);
                                    mq.Encode(firstRow & 1, JpxTier1Contexts.Uniform);
                                    var fy = s + firstRow;
                                    var fidx = (fy * width) + x;
                                    significant[fidx] = true;
                                    newThisPlane[fidx] = true;
                                    sign[fidx] = TargetSign(fidx);
                                    var (sctx0, sxor0) = SignContext(x, fy);
                                    mq.Encode((sign[fidx] ? 1 : 0) ^ sxor0, sctx0);
                                    startRow = firstRow + 1;
                                }
                            }

                            for (var ry = startRow; ry < rows; ry++)
                            {
                                var y = s + ry;
                                var idx = (y * width) + x;
                                if (significant[idx] || attemptedThisPlane[idx])
                                {
                                    continue;
                                }

                                attemptedThisPlane[idx] = true;
                                var ctx = ZeroCodingContext(x, y);
                                var bit = TargetBit(idx, plane);
                                mq.Encode(bit, ctx);
                                if (bit == 1)
                                {
                                    significant[idx] = true;
                                    newThisPlane[idx] = true;
                                    sign[idx] = TargetSign(idx);
                                    var (sctx1, sxor1) = SignContext(x, y);
                                    mq.Encode((sign[idx] ? 1 : 0) ^ sxor1, sctx1);
                                }
                            }
                        }
                    }

                    if (segSymbol)
                    {
                        int[] symbol = corruptSegmentationSymbol ? [1, 1, 1, 1] : [1, 0, 1, 0];
                        foreach (var symbolBit in symbol)
                        {
                            mq.Encode(symbolBit, JpxTier1Contexts.Uniform);
                        }
                    }

                    break;
            }

            passesInSegment++;
            if (resetPerPass)
            {
                mq.ResetContexts();
            }
        }

        FlushSegment();
        return ([.. tileData], [.. segments]);
    }

    private static (int Plane, PassType Type)[] BuildSchedule(int codedPlanes)
    {
        var schedule = new (int, PassType)[1 + (3 * (codedPlanes - 1))];
        schedule[0] = (0, PassType.Cleanup);
        var next = 1;
        for (var p = 1; p < codedPlanes; p++)
        {
            schedule[next++] = (p, PassType.SigProp);
            schedule[next++] = (p, PassType.MagRef);
            schedule[next++] = (p, PassType.Cleanup);
        }

        return schedule;
    }

    private static int HorizontalVerticalTable(int h, int v, int d)
    {
        if (h == 2)
        {
            return 8;
        }

        if (h == 1)
        {
            return v >= 1 ? 7 : d >= 1 ? 6 : 5;
        }

        if (v == 2)
        {
            return 4;
        }

        if (v == 1)
        {
            return 3;
        }

        return d >= 2 ? 2 : d == 1 ? 1 : 0;
    }

    private static int DiagonalTable(int h, int v, int d)
    {
        var hv = h + v;
        if (d >= 3)
        {
            return 8;
        }

        if (d == 2)
        {
            return hv >= 1 ? 7 : 6;
        }

        if (d == 1)
        {
            return hv >= 2 ? 5 : hv == 1 ? 4 : 3;
        }

        return hv >= 2 ? 2 : hv == 1 ? 1 : 0;
    }
}
