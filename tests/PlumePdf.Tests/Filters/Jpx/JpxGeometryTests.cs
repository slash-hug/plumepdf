using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxGeometry"/>'s Annex B arithmetic against
/// hand-computed tables — odd origins, sub-sampling, per-component decomposition levels, and the
/// B.7 code-block clamp with <c>PPx = 2</c> at <c>r = 0</c>.
/// </summary>
public class JpxGeometryTests
{
    // Style 0 (reversible/"none") still transmits one explicit SPqcd exponent per subband
    // (T.800 Table A.28) — JpxCodestream.ParseQuantization already reads it into Steps, and
    // JpxGeometry.SubbandQuantisation reads it back out via the same per-subband index style 2
    // uses. A generously-sized placeholder (more entries than any test below's subband count
    // needs) stands in for a real QCD/QCC's Steps array; the exponent value itself (8) is
    // arbitrary — these tests assert Lblock/resolution-count facts, not epsilon/Mb values.
    private static readonly (int Exponent, int Mantissa)[] PlaceholderSteps =
        Enumerable.Repeat((8, 0), 32).ToArray();

    private static JpxSiz Siz(int xsiz, int ysiz, int xosiz, int yosiz, int xtsiz, int ytsiz, int xtosiz, int ytosiz, params (int XRsiz, int YRsiz)[] components) => new()
    {
        Xsiz = xsiz,
        Ysiz = ysiz,
        XOsiz = xosiz,
        YOsiz = yosiz,
        XTsiz = xtsiz,
        YTsiz = ytsiz,
        XTOsiz = xtosiz,
        YTOsiz = ytosiz,
        Rsiz = 0,
        Components = Array.ConvertAll(components, c => new JpxComponentInfo(8, false, c.XRsiz, c.YRsiz)),
    };

    // ---- B-5..B-9: image/tile grid ----

    [Fact]
    public void TileBounds_SingleTile64x48_CoversWholeImage()
    {
        var siz = Siz(64, 48, 0, 0, 64, 48, 0, 0, (1, 1));
        Assert.Equal(1, siz.NumXTiles);
        Assert.Equal(1, siz.NumYTiles);

        var bounds = JpxGeometry.TileBounds(siz, 0);

        Assert.Equal(new JpxGeometry.Bounds(0, 0, 64, 48), bounds);
    }

    [Fact]
    public void TileBounds_3x2GridOn97x61_ComputesCorrectTileCount()
    {
        // 97x61 image, 33x31 nominal tiles: NumXTiles = ceil(97/33) = 3, NumYTiles = ceil(61/31) = 2
        // (a tiles-3x2-partial fixture shape, hand-computed here without any codestream bytes).
        var siz = Siz(97, 61, 0, 0, 33, 31, 0, 0, (1, 1), (1, 1), (1, 1));

        Assert.Equal(3, siz.NumXTiles);
        Assert.Equal(2, siz.NumYTiles);
    }

    [Theory]
    [InlineData(0, 0, 0, 33, 31)] // top-left: full 33x31 nominal cell
    [InlineData(2, 0, 66, 97, 31)] // top-right: partial width (97 - 66 = 31, not 33)
    [InlineData(0, 1, 0, 33, 61)] // bottom-left: partial height (61 - 31 = 30, not 31)
    [InlineData(2, 1, 66, 97, 61)] // bottom-right: partial on both axes
    public void TileBounds_3x2GridOn97x61_EdgeTilesAreClippedToTheImage(int p, int q, int expectedX0, int expectedX1, int expectedYEdgeReference)
    {
        var siz = Siz(97, 61, 0, 0, 33, 31, 0, 0, (1, 1));
        var tileIndex = (q * siz.NumXTiles) + p;

        var bounds = JpxGeometry.TileBounds(siz, tileIndex);

        Assert.Equal(expectedX0, bounds.X0);
        Assert.Equal(expectedX1, bounds.X1);

        // Hand-computed per B-8/B-9: y0 = max(YTOsiz + q*YTsiz, YOsiz); y1 = min(YTOsiz + (q+1)*YTsiz, Ysiz).
        var expectedY0 = q * 31;
        var expectedY1 = Math.Min((q + 1) * 31, 61);
        Assert.Equal(expectedY0, bounds.Y0);
        Assert.Equal(expectedY1, bounds.Y1);
        _ = expectedYEdgeReference; // documents which axis is the interesting one per row above
    }

    [Fact]
    public void TileBounds_LastTileOfPartialGrid_IsNarrowerAndShorterThanNominal()
    {
        var siz = Siz(97, 61, 0, 0, 33, 31, 0, 0, (1, 1));

        var lastTile = JpxGeometry.TileBounds(siz, 5); // p=2, q=1: the doubly-partial corner

        Assert.Equal(new JpxGeometry.Bounds(66, 31, 97, 61), lastTile);
        Assert.Equal(31, lastTile.Width); // nominal 33, clipped by the image edge
        Assert.Equal(30, lastTile.Height); // nominal 31, clipped by the image edge
    }

    [Fact]
    public void TileBounds_OddOrigin_ClampsToXOsiz()
    {
        // XOsiz = 3 on a single 67-wide tile: the tile's nominal [0,67) is clamped at the low end
        // to the image's own origin.
        var siz = Siz(67, 48, 3, 0, 67, 48, 0, 0, (1, 1));

        var bounds = JpxGeometry.TileBounds(siz, 0);

        Assert.Equal(new JpxGeometry.Bounds(3, 0, 67, 48), bounds);
    }

    // ---- B-12: tile-component bounds with sub-sampling ----

    [Fact]
    public void TileComponentBounds_OddOrigin_UnitySubsampling_PassesThroughUnchanged()
    {
        var tileBounds = new JpxGeometry.Bounds(3, 0, 67, 48);
        var component = new JpxComponentInfo(8, false, 1, 1);

        var tc = JpxGeometry.TileComponentBounds(tileBounds, component);

        Assert.Equal(new JpxGeometry.Bounds(3, 0, 67, 48), tc);
    }

    [Fact]
    public void TileComponentBounds_Uniform2x2Subsampling_HalvesEachAxisByCeiling()
    {
        var tileBounds = new JpxGeometry.Bounds(0, 0, 64, 48);
        var component = new JpxComponentInfo(8, false, 2, 2);

        var tc = JpxGeometry.TileComponentBounds(tileBounds, component);

        Assert.Equal(new JpxGeometry.Bounds(0, 0, 32, 24), tc);
    }

    [Fact]
    public void TileComponentBounds_Mixed1x1_2x2_2x2_Subsampling_LumaFullChromaHalved()
    {
        // The 4:2:0-shaped case: component 0 unsampled, components 1/2 subsampled 2x2.
        var tileBounds = new JpxGeometry.Bounds(0, 0, 64, 48);
        var luma = new JpxComponentInfo(8, false, 1, 1);
        var chroma = new JpxComponentInfo(8, false, 2, 2);

        var lumaBounds = JpxGeometry.TileComponentBounds(tileBounds, luma);
        var chroma1Bounds = JpxGeometry.TileComponentBounds(tileBounds, chroma);
        var chroma2Bounds = JpxGeometry.TileComponentBounds(tileBounds, chroma);

        Assert.Equal(new JpxGeometry.Bounds(0, 0, 64, 48), lumaBounds);
        Assert.Equal(new JpxGeometry.Bounds(0, 0, 32, 24), chroma1Bounds);
        Assert.Equal(chroma1Bounds, chroma2Bounds);
    }

    [Fact]
    public void TileComponentBounds_OddTileEdgeWithSubsampling_UsesCeilingAtBothEnds()
    {
        // A partial tile [66,97) x [31,61) under 2x2 subsampling: ceil(66/2)=33, ceil(97/2)=49.
        var tileBounds = new JpxGeometry.Bounds(66, 31, 97, 61);
        var component = new JpxComponentInfo(8, false, 2, 2);

        var tc = JpxGeometry.TileComponentBounds(tileBounds, component);

        Assert.Equal(new JpxGeometry.Bounds(33, 16, 49, 31), tc);
    }

    // ---- B-14: resolution bounds, hand-computed table for 64x48, N_L = 5 ----

    [Theory]
    [InlineData(5, 0, 0, 64, 48)] // r = N_L: full tile-component resolution
    [InlineData(4, 0, 0, 32, 24)]
    [InlineData(3, 0, 0, 16, 12)]
    [InlineData(2, 0, 0, 8, 6)]
    [InlineData(1, 0, 0, 4, 3)]
    [InlineData(0, 0, 0, 2, 2)] // r = 0: ceil(48/32) = 2, not 1 -- the coarsest LL is not always square
    public void ResolutionBounds_64x48_NL5_MatchesHandComputedTable(int r, int x0, int y0, int x1, int y1)
    {
        var tcBounds = new JpxGeometry.Bounds(0, 0, 64, 48);

        var bounds = JpxGeometry.ResolutionBounds(tcBounds, decompositionLevels: 5, r);

        Assert.Equal(new JpxGeometry.Bounds(x0, y0, x1, y1), bounds);
    }

    // ---- B-15: subband coordinates, r > 0 halving ----

    [Fact]
    public void SubbandBounds_Resolution1Of64x48NL5_SplitsIntoConsistentQuadrants()
    {
        // Resolution 1's own bounds are (0,0,4,3) (from the table above). Its LL (=resolution 0)
        // is (0,0,2,2); HL/LH/HH must partition the remaining 12 - 4 = 8 samples exactly, and
        // every subband's area must sum back to the resolution's own 4*3 = 12.
        var resolution1Bounds = new JpxGeometry.Bounds(0, 0, 4, 3);

        var hl = JpxGeometry.SubbandBounds(resolution1Bounds, orientation: 1);
        var lh = JpxGeometry.SubbandBounds(resolution1Bounds, orientation: 2);
        var hh = JpxGeometry.SubbandBounds(resolution1Bounds, orientation: 3);
        var ll = JpxGeometry.ResolutionBounds(new JpxGeometry.Bounds(0, 0, 64, 48), 5, 0);

        Assert.Equal(new JpxGeometry.Bounds(0, 0, 2, 2), hl);
        Assert.Equal(new JpxGeometry.Bounds(0, 0, 2, 1), lh);
        Assert.Equal(new JpxGeometry.Bounds(0, 0, 2, 1), hh);

        var totalArea = (ll.Width * ll.Height) + (hl.Width * hl.Height) + (lh.Width * lh.Height) + (hh.Width * hh.Height);
        Assert.Equal(resolution1Bounds.Width * resolution1Bounds.Height, totalArea);
    }

    [Fact]
    public void SubbandBounds_OrientationZero_IsResolutionBoundsUnchanged()
    {
        var resolutionBounds = new JpxGeometry.Bounds(0, 0, 2, 2);

        var ll = JpxGeometry.SubbandBounds(resolutionBounds, orientation: 0);

        Assert.Equal(resolutionBounds, ll);
    }

    // ---- B.7: code-block clamp ----

    [Fact]
    public void ClampCodeBlockExponents_PPx2AtR0_ClampsXcb6DownTo2()
    {
        var (xcb, ycb) = JpxGeometry.ClampCodeBlockExponents(xcb: 6, ycb: 6, ppx: 2, ppy: 2, r: 0);

        Assert.Equal(2, xcb);
        Assert.Equal(2, ycb);
    }

    [Fact]
    public void ClampCodeBlockExponents_AtRGreaterThanZero_UsesPPxMinusOne()
    {
        var (xcb, ycb) = JpxGeometry.ClampCodeBlockExponents(xcb: 6, ycb: 6, ppx: 2, ppy: 2, r: 1);

        Assert.Equal(1, xcb); // min(6, ppx-1=1)
        Assert.Equal(1, ycb);
    }

    [Fact]
    public void ClampCodeBlockExponents_SmallXcbBelowPrecinct_IsUnaffected()
    {
        var (xcb, ycb) = JpxGeometry.ClampCodeBlockExponents(xcb: 4, ycb: 4, ppx: 15, ppy: 15, r: 0);

        Assert.Equal(4, xcb);
        Assert.Equal(4, ycb);
    }

    // ---- B-16: precinct partition, including r = 0 ----

    [Fact]
    public void PrecinctGrid_DefaultHugePrecinct_ProducesExactlyOnePrecinct()
    {
        var bounds = new JpxGeometry.Bounds(0, 0, 64, 48);

        var (pxStart, pyStart, wide, high) = JpxGeometry.PrecinctGrid(bounds, ppxExponent: 15, ppyExponent: 15);

        Assert.Equal(0, pxStart);
        Assert.Equal(0, pyStart);
        Assert.Equal(1, wide);
        Assert.Equal(1, high);
    }

    [Fact]
    public void PrecinctGrid_SmallExplicitPrecinct_PartitionsIntoMultipleCells()
    {
        // 64x48 bounds, PPx=PPy=2 (cell = 4x4): 16 columns, 12 rows.
        var bounds = new JpxGeometry.Bounds(0, 0, 64, 48);

        var (pxStart, pyStart, wide, high) = JpxGeometry.PrecinctGrid(bounds, ppxExponent: 2, ppyExponent: 2);

        Assert.Equal(0, pxStart);
        Assert.Equal(0, pyStart);
        Assert.Equal(16, wide);
        Assert.Equal(12, high);
    }

    [Fact]
    public void PrecinctGrid_EmptyBounds_ProducesZeroPrecincts()
    {
        var (pxStart, pyStart, wide, high) = JpxGeometry.PrecinctGrid(new JpxGeometry.Bounds(5, 5, 5, 5), 4, 4);

        Assert.Equal(0, pxStart);
        Assert.Equal(0, pyStart);
        Assert.Equal(0, wide);
        Assert.Equal(0, high);
    }

    [Fact]
    public void GridPartition_UnalignedStart_ComputesFloorStartAndCeilingCount()
    {
        // [3,10) with exponent 2 (cell size 4): cells 0[0,4), 1[4,8), 2[8,12) -- start=0, count=3.
        var (start, count) = JpxGeometry.GridPartition(3, 10, exponent: 2);

        Assert.Equal(0, start);
        Assert.Equal(3, count);
    }

    // ---- BuildTile: end-to-end assembly for a full tile ----

    [Fact]
    public void BuildTile_SingleTile_LblockDefaultsToThreeOnEveryCodeBlock()
    {
        var siz = Siz(64, 48, 0, 0, 64, 48, 0, 0, (1, 1));
        var coding = new JpxCodingStyle
        {
            DecompositionLevels = 2,
            Xcb = 6,
            Ycb = 6,
            CodeBlockStyle = 0,
            Reversible53 = true,
            PrecinctExponentsX = [15, 15, 15],
            PrecinctExponentsY = [15, 15, 15],
            Progression = JpxProgression.Lrcp,
            Layers = 1,
            Mct = false,
            Sop = false,
            Eph = false,
        };
        var quant = new JpxQuantization { Style = 0, GuardBits = 2, Steps = PlaceholderSteps };
        var header = new JpxCodestreamHeader
        {
            Siz = siz,
            Coding = [coding],
            Quant = [quant],
            MainPoc = [],
            TileParts = [],
        };

        var tile = JpxGeometry.BuildTile(header, 0);

        Assert.Equal(0, tile.Index);
        Assert.Equal(new JpxGeometry.Bounds(0, 0, 64, 48), new JpxGeometry.Bounds(tile.X0, tile.Y0, tile.X1, tile.Y1));
        var component = Assert.Single(tile.Components);
        Assert.Equal(3, component.Resolutions.Length); // N_L + 1
        Assert.All(component.Resolutions, resolution => Assert.All(resolution.Subbands, subband => Assert.All(subband.Precincts, precinct => Assert.All(precinct.CodeBlocks, block => Assert.Equal(3, block.Lblock)))));

        // Resolution 0 has one LL subband; resolutions 1..N_L each have three (HL, LH, HH).
        Assert.Single(component.Resolutions[0].Subbands);
        Assert.Equal(3, component.Resolutions[1].Subbands.Length);
        Assert.Equal(3, component.Resolutions[2].Subbands.Length);
    }

    [Fact]
    public void BuildTile_TileOverride_UsesTileScopedCodingAndQuant()
    {
        var siz = Siz(64, 48, 0, 0, 64, 48, 0, 0, (1, 1));
        var mainCoding = new JpxCodingStyle
        {
            DecompositionLevels = 1,
            Xcb = 6,
            Ycb = 6,
            CodeBlockStyle = 0,
            Reversible53 = true,
            PrecinctExponentsX = [15, 15],
            PrecinctExponentsY = [15, 15],
            Progression = JpxProgression.Lrcp,
            Layers = 1,
            Mct = false,
            Sop = false,
            Eph = false,
        };
        var tileCoding = new JpxCodingStyle
        {
            DecompositionLevels = 3,
            Xcb = mainCoding.Xcb,
            Ycb = mainCoding.Ycb,
            CodeBlockStyle = mainCoding.CodeBlockStyle,
            Reversible53 = mainCoding.Reversible53,
            PrecinctExponentsX = [15, 15, 15, 15],
            PrecinctExponentsY = [15, 15, 15, 15],
            Progression = mainCoding.Progression,
            Layers = mainCoding.Layers,
            Mct = mainCoding.Mct,
            Sop = mainCoding.Sop,
            Eph = mainCoding.Eph,
        };
        var quant = new JpxQuantization { Style = 0, GuardBits = 2, Steps = PlaceholderSteps };
        var header = new JpxCodestreamHeader
        {
            Siz = siz,
            Coding = [mainCoding],
            Quant = [quant],
            MainPoc = [],
            TileParts = [],
        };
        header.TileOverrides[0] = ([tileCoding], [quant]);

        var tile = JpxGeometry.BuildTile(header, 0);

        Assert.Equal(4, tile.Components[0].Resolutions.Length); // tile override's N_L=3 -> 4 resolutions, not main's 2
    }
}
