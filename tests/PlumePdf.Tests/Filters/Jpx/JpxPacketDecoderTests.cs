using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxPacketDecoder"/>'s pure tier-2 decode tables —
/// the B.10.6 pass-count code, <c>Lblock</c> growth, the B.10.7 segment-count rule across every
/// code-block style combination that affects it, and the five B.12 progression-order iterators
/// against a hand-listed toy tile — all runnable without <see cref="JpxBitReader"/>/
/// <see cref="JpxTagTree"/>. The one integration-shaped test that needs them
/// (<see cref="DecodeTilePackets_LrcpFixture_TotalSegmentBytesMatchesTilePartLength"/>) runs
/// normally now that they are merged.
/// </summary>
public class JpxPacketDecoderTests
{
    // ------------------------------------------------------------------
    // B.10.6 pass-count table.
    // ------------------------------------------------------------------

    [Fact]
    public void PassCountFromFields_EveryReachableCombination_YieldsEachValue1Through164Once()
    {
        var seen = new List<int>
        {
            JpxPacketDecoder.PassCountFromFields(moreThanOne: false, moreThanTwo: false, 0, 0, 0),
            JpxPacketDecoder.PassCountFromFields(moreThanOne: true, moreThanTwo: false, 0, 0, 0),
        };

        for (var two = 0; two < 3; two++)
        {
            seen.Add(JpxPacketDecoder.PassCountFromFields(moreThanOne: true, moreThanTwo: true, two, 0, 0));
        }

        for (var five = 0; five < 31; five++)
        {
            seen.Add(JpxPacketDecoder.PassCountFromFields(moreThanOne: true, moreThanTwo: true, 3, five, 0));
        }

        for (var seven = 0; seven < 128; seven++)
        {
            seen.Add(JpxPacketDecoder.PassCountFromFields(moreThanOne: true, moreThanTwo: true, 3, 31, seven));
        }

        // 2 one-bit codes + 3 two-bit-field values + 31 five-bit-field values + 128 seven-bit-field
        // values = 164 reachable pass counts, contiguous 1..164, each exactly once.
        Assert.Equal(164, seen.Count);
        Assert.Equal(Enumerable.Range(1, 164), seen);
    }

    [Theory]
    [InlineData(false, false, 0, 0, 0, 1)]
    [InlineData(true, false, 0, 0, 0, 2)]
    [InlineData(true, true, 0, 0, 0, 3)]
    [InlineData(true, true, 2, 0, 0, 5)]
    [InlineData(true, true, 3, 0, 0, 6)]
    [InlineData(true, true, 3, 30, 0, 36)]
    [InlineData(true, true, 3, 31, 0, 37)]
    [InlineData(true, true, 3, 31, 127, 164)]
    public void PassCountFromFields_SpotValues(bool moreThanOne, bool moreThanTwo, int twoBitField, int fiveBitField, int sevenBitField, int expected) =>
        Assert.Equal(expected, JpxPacketDecoder.PassCountFromFields(moreThanOne, moreThanTwo, twoBitField, fiveBitField, sevenBitField));

    // ------------------------------------------------------------------
    // B.10.7.1 Lblock growth and segment-length field width.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(3, 0, 3)]
    [InlineData(3, 1, 4)]
    [InlineData(3, 5, 8)]
    [InlineData(8, 0, 8)]
    public void GrowLblock_AddsUnaryOnesCount(int currentLblock, int unaryOnesRead, int expected) =>
        Assert.Equal(expected, JpxPacketDecoder.GrowLblock(currentLblock, unaryOnesRead));

    [Theory]
    [InlineData(3, 1, 3)]
    [InlineData(3, 2, 4)]
    [InlineData(3, 3, 4)]
    [InlineData(3, 4, 5)]
    [InlineData(5, 7, 7)]
    [InlineData(5, 8, 8)]
    [InlineData(3, 10, 6)]
    public void SegmentLengthFieldBits_IsLblockPlusFloorLog2Passes(int lblock, int passesInSegment, int expected) =>
        Assert.Equal(expected, JpxPacketDecoder.SegmentLengthFieldBits(lblock, passesInSegment));

    // ------------------------------------------------------------------
    // B.10.7 segment-count rule — all six CodeBlockStyle bits, plus a cross-packet straddle case.
    // ------------------------------------------------------------------

    [Fact]
    public void PlanSegments_NoStyleFlags_OneMqSegmentForAllNewPasses()
    {
        var plans = Plan(0x00, priorPasses: 0, newPasses: 13);
        Assert.Equal(new[] { (13, JpxSegmentKind.Mq) }, plans);
    }

    [Fact]
    public void PlanSegments_ResetAlone_BehavesLikeNoStyleFlags()
    {
        // Bit 0x02 (reset context probabilities on each pass) does not change segment boundaries.
        var plans = Plan(0x02, priorPasses: 0, newPasses: 13);
        Assert.Equal(new[] { (13, JpxSegmentKind.Mq) }, plans);
    }

    [Fact]
    public void PlanSegments_TermAllAlone_OneMqSegmentPerPass()
    {
        var plans = Plan(0x04, priorPasses: 0, newPasses: 13);
        Assert.Equal(Enumerable.Repeat((1, JpxSegmentKind.Mq), 13), plans);
    }

    [Fact]
    public void PlanSegments_BypassAlone_OneMqRunThenRawPairPlusCleanupPerBitPlane()
    {
        var plans = Plan(0x01, priorPasses: 0, newPasses: 13);
        Assert.Equal(new[] { (10, JpxSegmentKind.Mq), (2, JpxSegmentKind.Raw), (1, JpxSegmentKind.Mq) }, plans);
    }

    [Fact]
    public void PlanSegments_BypassAndTermAll_OneSegmentPerPassRawPastPassTen()
    {
        var expected = Enumerable.Range(1, 10).Select(_ => (1, JpxSegmentKind.Mq))
            .Append((1, JpxSegmentKind.Raw))   // pass 11: sig-prop
            .Append((1, JpxSegmentKind.Raw))   // pass 12: mag-ref
            .Append((1, JpxSegmentKind.Mq));   // pass 13: cleanup

        var plans = Plan(0x05, priorPasses: 0, newPasses: 13);
        Assert.Equal(expected, plans);
    }

    [Fact]
    public void PlanSegments_AllSixStyleBitsSet_BehavesLikeBypassAndTermAllAlone()
    {
        // The other four bits (reset 0x02, vertically causal 0x08, predictable termination 0x10,
        // segmentation symbols 0x20) must not change the segment plan.
        const byte allSixBits = 0x01 | 0x02 | 0x04 | 0x08 | 0x10 | 0x20;
        Assert.Equal(Plan(0x05, priorPasses: 0, newPasses: 13), Plan(allSixBits, priorPasses: 0, newPasses: 13));
    }

    [Fact]
    public void PlanSegments_Bypass_PriorPassesShiftTheRawBoundary()
    {
        // 9 passes already signalled (still inside the pass<=10 MQ run); this packet's 4 new passes
        // are global passes 10 (MQ), 11 (sig-prop, raw), 12 (mag-ref, raw), 13 (cleanup, MQ).
        var plans = Plan(0x01, priorPasses: 9, newPasses: 4);
        Assert.Equal(new[] { (1, JpxSegmentKind.Mq), (2, JpxSegmentKind.Raw), (1, JpxSegmentKind.Mq) }, plans);
    }

    [Fact]
    public void PlanSegments_Bypass_RawPairCanBeSplitAcrossPackets()
    {
        // Pass 11 (sig-prop) was already signalled in an earlier packet; this packet contributes
        // only pass 12 (mag-ref), the second half of the raw pair, with no cleanup pass yet.
        var plans = Plan(0x01, priorPasses: 11, newPasses: 1);
        Assert.Equal(new[] { (1, JpxSegmentKind.Raw) }, plans);
    }

    [Fact]
    public void PlanSegments_ZeroNewPasses_NoSegments() =>
        Assert.Empty(JpxPacketDecoder.PlanSegments(0x01, priorPasses: 5, newPasses: 0));

    private static IEnumerable<(int Passes, JpxSegmentKind Kind)> Plan(byte codeBlockStyle, int priorPasses, int newPasses) =>
        JpxPacketDecoder.PlanSegments(codeBlockStyle, priorPasses, newPasses).Select(p => (p.Passes, p.Kind));

    // ------------------------------------------------------------------
    // B.12 progression-order iterators — a 2-layer/3-resolution/2-component toy tile whose finest
    // resolution (r=2) has 2 precincts and whose coarser levels (r=0,1) have 1, derived from a
    // constant PPx=PPy=2 at every level (T.800's reference-grid step shrinks at finer levels even
    // with a constant precinct exponent) over an 8x4 tile, XRsiz=YRsiz=1, N_L=2.
    // ------------------------------------------------------------------

    [Fact]
    public void EnumerateLrcp_EmitsLayerResolutionComponentPositionOrder()
    {
        var tile = BuildToyTile(JpxProgression.Lrcp);
        var actual = JpxPacketDecoder.EnumerateLrcp(tile, resStart: 0, resEnd: 3, compStart: 0, compEnd: 2, layers: 2).ToArray();

        JpxPacketKey[] expected =
        [
            new(0, 0, 0, 0), new(0, 0, 1, 0),
            new(0, 1, 0, 0), new(0, 1, 1, 0),
            new(0, 2, 0, 0), new(0, 2, 0, 1), new(0, 2, 1, 0), new(0, 2, 1, 1),
            new(1, 0, 0, 0), new(1, 0, 1, 0),
            new(1, 1, 0, 0), new(1, 1, 1, 0),
            new(1, 2, 0, 0), new(1, 2, 0, 1), new(1, 2, 1, 0), new(1, 2, 1, 1),
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnumerateRlcp_EmitsResolutionLayerComponentPositionOrder()
    {
        var tile = BuildToyTile(JpxProgression.Rlcp);
        var actual = JpxPacketDecoder.EnumerateRlcp(tile, resStart: 0, resEnd: 3, compStart: 0, compEnd: 2, layers: 2).ToArray();

        JpxPacketKey[] expected =
        [
            new(0, 0, 0, 0), new(0, 0, 1, 0),
            new(1, 0, 0, 0), new(1, 0, 1, 0),
            new(0, 1, 0, 0), new(0, 1, 1, 0),
            new(1, 1, 0, 0), new(1, 1, 1, 0),
            new(0, 2, 0, 0), new(0, 2, 0, 1), new(0, 2, 1, 0), new(0, 2, 1, 1),
            new(1, 2, 0, 0), new(1, 2, 0, 1), new(1, 2, 1, 0), new(1, 2, 1, 1),
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnumerateRpcl_StepsPositionPerResolutionBeforeComponentAndLayer()
    {
        var tile = BuildToyTile(JpxProgression.Rpcl);
        var header = BuildToyHeader(tile);
        var actual = JpxPacketDecoder.EnumerateRpcl(header, tile, resStart: 0, resEnd: 3, compStart: 0, compEnd: 2, layers: 2).ToArray();

        JpxPacketKey[] expected =
        [
            new(0, 0, 0, 0), new(1, 0, 0, 0), new(0, 0, 1, 0), new(1, 0, 1, 0),
            new(0, 1, 0, 0), new(1, 1, 0, 0), new(0, 1, 1, 0), new(1, 1, 1, 0),
            new(0, 2, 0, 0), new(1, 2, 0, 0), new(0, 2, 1, 0), new(1, 2, 1, 0),
            new(0, 2, 0, 1), new(1, 2, 0, 1), new(0, 2, 1, 1), new(1, 2, 1, 1),
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnumeratePcrl_StepsPositionOnceBeforeComponentResolutionAndLayer()
    {
        var tile = BuildToyTile(JpxProgression.Pcrl);
        var header = BuildToyHeader(tile);
        var actual = JpxPacketDecoder.EnumeratePcrl(header, tile, resStart: 0, resEnd: 3, compStart: 0, compEnd: 2, layers: 2).ToArray();

        JpxPacketKey[] expected =
        [
            new(0, 0, 0, 0), new(1, 0, 0, 0), new(0, 1, 0, 0), new(1, 1, 0, 0), new(0, 2, 0, 0), new(1, 2, 0, 0),
            new(0, 0, 1, 0), new(1, 0, 1, 0), new(0, 1, 1, 0), new(1, 1, 1, 0), new(0, 2, 1, 0), new(1, 2, 1, 0),
            new(0, 2, 0, 1), new(1, 2, 0, 1), new(0, 2, 1, 1), new(1, 2, 1, 1),
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnumerateCprl_StepsPositionPerComponentBeforeResolutionAndLayer()
    {
        var tile = BuildToyTile(JpxProgression.Cprl);
        var header = BuildToyHeader(tile);
        var actual = JpxPacketDecoder.EnumerateCprl(header, tile, resStart: 0, resEnd: 3, compStart: 0, compEnd: 2, layers: 2).ToArray();

        JpxPacketKey[] expected =
        [
            new(0, 0, 0, 0), new(1, 0, 0, 0), new(0, 1, 0, 0), new(1, 1, 0, 0), new(0, 2, 0, 0), new(1, 2, 0, 0), new(0, 2, 0, 1), new(1, 2, 0, 1),
            new(0, 0, 1, 0), new(1, 0, 1, 0), new(0, 1, 1, 0), new(1, 1, 1, 0), new(0, 2, 1, 0), new(1, 2, 1, 0), new(0, 2, 1, 1), new(1, 2, 1, 1),
        ];

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnumerateProgression_DispatchesToTheMatchingIteratorForEveryOrder()
    {
        // JpxProgression is internal, so this can't be a public [Theory] parameter — one Fact
        // exercises all five orders instead.
        foreach (var order in new[] { JpxProgression.Lrcp, JpxProgression.Rlcp, JpxProgression.Rpcl, JpxProgression.Pcrl, JpxProgression.Cprl })
        {
            var tile = BuildToyTile(order);
            var header = BuildToyHeader(tile);
            var direct = order switch
            {
                JpxProgression.Lrcp => JpxPacketDecoder.EnumerateLrcp(tile, 0, 3, 0, 2, 2),
                JpxProgression.Rlcp => JpxPacketDecoder.EnumerateRlcp(tile, 0, 3, 0, 2, 2),
                JpxProgression.Rpcl => JpxPacketDecoder.EnumerateRpcl(header, tile, 0, 3, 0, 2, 2),
                JpxProgression.Pcrl => JpxPacketDecoder.EnumeratePcrl(header, tile, 0, 3, 0, 2, 2),
                JpxProgression.Cprl => JpxPacketDecoder.EnumerateCprl(header, tile, 0, 3, 0, 2, 2),
                _ => throw new ArgumentOutOfRangeException(nameof(order)),
            };

            var dispatched = JpxPacketDecoder.EnumerateProgression(header, tile, order, layers: 2, resStart: 0, resEnd: 3, compStart: 0, compEnd: 2);
            Assert.Equal(direct, dispatched);
        }
    }

    private static JpxTile BuildToyTile(JpxProgression order) => new()
    {
        Index = 0,
        X0 = 0,
        Y0 = 0,
        X1 = 8,
        Y1 = 4,
        Components = [ToyComponent(0, order), ToyComponent(1, order)],
        ProgressionVolumes = [],
    };

    private static JpxCodestreamHeader BuildToyHeader(JpxTile tile) => new()
    {
        Siz = new JpxSiz
        {
            Xsiz = 8,
            Ysiz = 4,
            XOsiz = 0,
            YOsiz = 0,
            XTsiz = 8,
            YTsiz = 4,
            XTOsiz = 0,
            YTOsiz = 0,
            Rsiz = 0,
            Components = [new JpxComponentInfo(8, false, 1, 1), new JpxComponentInfo(8, false, 1, 1)],
        },
        Coding = [tile.Components[0].Coding, tile.Components[1].Coding],
        Quant = [tile.Components[0].Quant, tile.Components[1].Quant],
        MainPoc = [],
        TileParts = [],
    };

    private static JpxTileComponent ToyComponent(int index, JpxProgression order) => new()
    {
        Component = index,
        X0 = 0,
        Y0 = 0,
        X1 = 8,
        Y1 = 4,
        Coding = new JpxCodingStyle
        {
            DecompositionLevels = 2,
            Xcb = 6,
            Ycb = 6,
            CodeBlockStyle = 0,
            Reversible53 = true,
            PrecinctExponentsX = [2, 2, 2],
            PrecinctExponentsY = [2, 2, 2],
            Progression = order,
            Layers = 2,
            Mct = false,
            Sop = false,
            Eph = false,
        },
        Quant = new JpxQuantization { Style = 0, GuardBits = 2, Steps = [(0, 0)] },
        Resolutions =
        [
            ToyResolution(level: 0, precinctsWide: 1, precinctsHigh: 1),
            ToyResolution(level: 1, precinctsWide: 1, precinctsHigh: 1),
            ToyResolution(level: 2, precinctsWide: 2, precinctsHigh: 1),
        ],
    };

    // PPx = PPy = 2 at every level, N_L = 2: the reference-grid step XRsiz*2^(PPx+N_L-r) is
    // 16, 8, 4 for r = 0, 1, 2 — over the 8x4 tile that gives 1, 1, 2 precincts wide and 1 high
    // at every level, matching PrecinctsWide/High below.
    private static JpxResolution ToyResolution(int level, int precinctsWide, int precinctsHigh) => new()
    {
        Level = level,
        X0 = 0,
        Y0 = 0,
        X1 = 1,
        Y1 = 1,
        PPx = 2,
        PPy = 2,
        PrecinctsWide = precinctsWide,
        PrecinctsHigh = precinctsHigh,
        Subbands = [],
    };

    // ------------------------------------------------------------------
    // Integration-shaped (needed JpxBitReader/JpxTagTree, now merged).
    // ------------------------------------------------------------------

    [Fact]
    public void DecodeTilePackets_LrcpFixture_TotalSegmentBytesMatchesTilePartLength()
    {
        var fixturesDir = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "jpx");
        var fileBytes = File.ReadAllBytes(Path.Combine(fixturesDir, "prog-lrcp.jp2"));
        var codestream = Jp2Boxes.Parse(fileBytes, PdfOptions.Default, null).Codestream;

        var header = JpxCodestream.Parse(codestream, PdfOptions.Default, null);
        var tileParts = header.TileParts.Where(static tp => tp.TileIndex == 0).OrderBy(static tp => tp.TPsot).ToList();
        Assert.NotEmpty(tileParts);

        var tile = JpxGeometry.BuildTile(header, tileIndex: 0);
        JpxPacketDecoder.DecodeTilePackets(tile, header, tileParts, PdfOptions.Default, null);

        // Every codeword segment's body byte range must land inside its own tile-part's data
        // (tier-2 can never point tier-1 past the bytes it was handed), and the segment bodies
        // together account for most, but not all, of the tile-part: packet headers (inclusion,
        // zero-bit-plane, pass-count bits, byte-aligned per packet) occupy the rest. The oracle
        // gate (JpxOracleTests, corpus-wide, this exact fixture included) is what proves the
        // decode is actually correct end-to-end; this test's job is the narrower tier-2 shape
        // invariant pure-function tests can't reach without a live bit reader.
        var totalSegmentBytes = 0;
        foreach (var component in tile.Components)
        {
            foreach (var resolution in component.Resolutions)
            {
                foreach (var subband in resolution.Subbands)
                {
                    foreach (var precinct in subband.Precincts)
                    {
                        foreach (var block in precinct.CodeBlocks)
                        {
                            foreach (var segment in block.Segments)
                            {
                                Assert.InRange(segment.TilePartIndex, 0, tileParts.Count - 1);
                                Assert.InRange(segment.Offset + segment.Length, 0, tileParts[segment.TilePartIndex].Data.Length);
                                totalSegmentBytes += segment.Length;
                            }
                        }
                    }
                }
            }
        }

        var tilePartBytes = tileParts.Sum(static tp => tp.Data.Length);
        Assert.True(totalSegmentBytes > 0, "Expected at least one non-empty codeword segment.");
        Assert.True(totalSegmentBytes <= tilePartBytes, $"Segment body bytes ({totalSegmentBytes}) exceed the tile-part's own data length ({tilePartBytes}).");
    }

    [Fact]
    public void DecodeTilePackets_TwoPrecinctsInOneResolution_DecodesBothNotJustTheFirst()
    {
        // A regression test for the bug that DecodeKeys' visited-triple de-duplication
        // was keyed on (layer, resolution, component) alone, dropping the precinct — so once the
        // first precinct of a triple was visited, EVERY OTHER precinct of that same triple (all
        // yielded consecutively by PrecinctsOf, within the SAME enumeration, not a later POC
        // volume) was silently `continue`d over rather than decoded. No committed fixture catches
        // this (every one has a 1x1 precinct grid at every resolution level) — this hand-built
        // tile is the minimal repro: a single resolution with a 2x1 precinct grid, one code-block
        // per precinct. Each packet's header is hand-encoded to exactly one byte (see the bit
        // breakdown below) followed by one arbitrary body byte, so the whole tile-part is 4 bytes:
        // [packet0-header, packet0-body, packet1-header, packet1-body].
        //
        // Packet header bits (MSB first), 8 bits = 1 byte, for a precinct with ONE never-yet-
        // included code-block (a 1x1 tag tree, so a single '1' bit finalizes each tree
        // immediately at value 0) and Lblock starting at its default of 3:
        //   1 (packet has data) 1 (inclusion: included) 1 (zero-bit-planes: finalizes at 0)
        //   0 (number of coding passes: 1 pass)  0 (Lblock unary growth terminator: +0)
        //   0 0 1 (3-bit segment length field, Lblock(3)+floor(log2(1))=3 bits: length = 1)
        // = 0b1110_0001 = 0xE1. The body byte's actual value is irrelevant to this test.
        var codeBlockA = new JpxCodeBlock { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1 };
        var codeBlockB = new JpxCodeBlock { X0 = 1, Y0 = 0, X1 = 2, Y1 = 1 };
        var precinct0 = new JpxPrecinct { X0 = 0, Y0 = 0, X1 = 1, Y1 = 1, CodeBlocks = [codeBlockA], CodeBlocksWide = 1 };
        var precinct1 = new JpxPrecinct { X0 = 1, Y0 = 0, X1 = 2, Y1 = 1, CodeBlocks = [codeBlockB], CodeBlocksWide = 1 };
        var subband = new JpxSubband { Orientation = 0, X0 = 0, Y0 = 0, X1 = 2, Y1 = 1, Epsilon = 8, Mu = 0, Mb = 8, Gain = 0, Precincts = [precinct0, precinct1] };
        var resolution = new JpxResolution { Level = 0, X0 = 0, Y0 = 0, X1 = 2, Y1 = 1, PPx = 2, PPy = 2, PrecinctsWide = 2, PrecinctsHigh = 1, Subbands = [subband] };
        var coding = new JpxCodingStyle
        {
            DecompositionLevels = 0,
            Xcb = 6,
            Ycb = 6,
            CodeBlockStyle = 0,
            Reversible53 = true,
            PrecinctExponentsX = [2],
            PrecinctExponentsY = [2],
            Progression = JpxProgression.Lrcp,
            Layers = 1,
            Mct = false,
            Sop = false,
            Eph = false,
        };
        var quant = new JpxQuantization { Style = 0, GuardBits = 2, Steps = [(8, 0)] };
        var tileComponent = new JpxTileComponent { Component = 0, X0 = 0, Y0 = 0, X1 = 2, Y1 = 1, Coding = coding, Quant = quant, Resolutions = [resolution] };
        var tile = new JpxTile { Index = 0, X0 = 0, Y0 = 0, X1 = 2, Y1 = 1, Components = [tileComponent], ProgressionVolumes = [] };
        var header = new JpxCodestreamHeader
        {
            Siz = new JpxSiz { Xsiz = 2, Ysiz = 1, XOsiz = 0, YOsiz = 0, XTsiz = 2, YTsiz = 1, XTOsiz = 0, YTOsiz = 0, Rsiz = 0, Components = [new JpxComponentInfo(8, false, 1, 1)] },
            Coding = [coding],
            Quant = [quant],
            MainPoc = [],
            TileParts = [],
        };
        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = new byte[] { 0xE1, 0xAB, 0xE1, 0xAB } };

        JpxPacketDecoder.DecodeTilePackets(tile, header, [tilePart], PdfOptions.Default, null);

        Assert.True(codeBlockA.Included, "precinct 0's code-block should be included.");
        Assert.Single(codeBlockA.Segments);
        Assert.True(codeBlockB.Included, "precinct 1's code-block should ALSO be included — it must not be skipped just because precinct 0's (layer, resolution, component) triple was already visited.");
        Assert.Single(codeBlockB.Segments);
    }

    [Fact]
    public void DecodeTilePackets_RpclTwoPrecinctsTwoLayers_DecodesEveryPacketNotJustTheFirstPrecinct()
    {
        // Regression: DecodeKeys de-duplicated on the
        // (layer, resolution, component) TRIPLE, which is only safe when a triple's precincts are
        // yielded contiguously (LRCP/RLCP). Under RPCL/PCRL/CPRL the precinct position is the OUTER
        // loop and the layer the inner one, so the sequence here is
        //   (l0,r0,c0,p0) (l1,r0,c0,p0) (l0,r0,c0,p1) (l1,r0,c0,p1)
        // and the triple-keyed set saw p1's packets as repeats and skipped them. A single 8x1
        // tile-component with N_L = 0, PPx = 2 (4-sample precincts -> a 2x1 precinct grid, one
        // 4x1 code-block each) and 2 layers. Packet headers (MSB first, one byte each):
        //   first layer of a precinct (block never yet included; 1x1 tag trees finalise on one bit):
        //     1 (non-empty) 1 (included) 1 (zero bit-planes = 0) 0 (1 pass) 0 (Lblock +0)
        //     001 (3-bit length = 1)                                        = 0b1110_0001 = 0xE1
        //   second layer (block already included -> one inclusion bit):
        //     1 (non-empty) 1 (included) 0 (1 pass) 0 (Lblock +0) 001 (length 1) 0 (pad) = 0xC2
        // each followed by one body byte, so the tile-part is 8 bytes in RPCL order.
        var codeBlockA = new JpxCodeBlock { X0 = 0, Y0 = 0, X1 = 4, Y1 = 1 };
        var codeBlockB = new JpxCodeBlock { X0 = 4, Y0 = 0, X1 = 8, Y1 = 1 };
        var precinct0 = new JpxPrecinct { X0 = 0, Y0 = 0, X1 = 4, Y1 = 1, CodeBlocks = [codeBlockA], CodeBlocksWide = 1 };
        var precinct1 = new JpxPrecinct { X0 = 4, Y0 = 0, X1 = 8, Y1 = 1, CodeBlocks = [codeBlockB], CodeBlocksWide = 1 };
        var subband = new JpxSubband { Orientation = 0, X0 = 0, Y0 = 0, X1 = 8, Y1 = 1, Epsilon = 8, Mu = 0, Mb = 8, Gain = 0, Precincts = [precinct0, precinct1] };
        var resolution = new JpxResolution { Level = 0, X0 = 0, Y0 = 0, X1 = 8, Y1 = 1, PPx = 2, PPy = 2, PrecinctsWide = 2, PrecinctsHigh = 1, Subbands = [subband] };
        var coding = new JpxCodingStyle
        {
            DecompositionLevels = 0,
            Xcb = 6,
            Ycb = 6,
            CodeBlockStyle = 0,
            Reversible53 = true,
            PrecinctExponentsX = [2],
            PrecinctExponentsY = [2],
            Progression = JpxProgression.Rpcl,
            Layers = 2,
            Mct = false,
            Sop = false,
            Eph = false,
        };
        var quant = new JpxQuantization { Style = 0, GuardBits = 2, Steps = [(8, 0)] };
        var tileComponent = new JpxTileComponent { Component = 0, X0 = 0, Y0 = 0, X1 = 8, Y1 = 1, Coding = coding, Quant = quant, Resolutions = [resolution] };
        var tile = new JpxTile { Index = 0, X0 = 0, Y0 = 0, X1 = 8, Y1 = 1, Components = [tileComponent], ProgressionVolumes = [] };
        var header = new JpxCodestreamHeader
        {
            Siz = new JpxSiz { Xsiz = 8, Ysiz = 1, XOsiz = 0, YOsiz = 0, XTsiz = 8, YTsiz = 1, XTOsiz = 0, YTOsiz = 0, Rsiz = 0, Components = [new JpxComponentInfo(8, false, 1, 1)] },
            Coding = [coding],
            Quant = [quant],
            MainPoc = [],
            TileParts = [],
        };
        var tilePart = new JpxTilePart { TileIndex = 0, TPsot = 0, TNsot = 1, Data = new byte[] { 0xE1, 0xAB, 0xC2, 0xCD, 0xE1, 0xEF, 0xC2, 0x12 } };

        JpxPacketDecoder.DecodeTilePackets(tile, header, [tilePart], PdfOptions.Default, null);

        Assert.True(codeBlockA.Included);
        Assert.Equal(2, codeBlockA.Segments.Count);
        Assert.Equal([1, 3], codeBlockA.Segments.Select(static s => s.Offset));
        Assert.True(codeBlockB.Included, "precinct 1's code-block must be decoded even though its (layer, resolution, component) triples were all seen at precinct 0 first.");
        Assert.Equal(2, codeBlockB.Segments.Count);
        Assert.Equal([5, 7], codeBlockB.Segments.Select(static s => s.Offset));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}
