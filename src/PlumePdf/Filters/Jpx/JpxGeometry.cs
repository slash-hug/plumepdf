namespace PlumePdf.Filters.Jpx;

/// <summary>
/// Integer partition arithmetic (T.800 Annex B): image/tile grids, tile-component bounds with
/// sub-sampling, resolution and subband coordinates, precinct partitions, and the B.7 code-block
/// clamp (<c>xcb' = min(xcb, PPx)</c> at <c>r = 0</c>, <c>min(xcb, PPx − 1)</c> above). Pure functions.
/// </summary>
/// <remarks>
/// Every partition here (tiles, resolutions/subbands, precincts, code-blocks) is anchored at
/// absolute grid coordinate 0 — never at the containing region's own origin — so a resolution's
/// precinct grid, a subband's precinct grid, and a precinct's code-block grid all address the
/// same cells by the same (px, py)/(cbx, cby) index regardless of which subband or component is
/// being partitioned. <see cref="GridPartition"/> and <see cref="GridCellBounds"/> are the two
/// primitives every higher-level partition (<see cref="PrecinctGrid"/>, <see cref="CodeBlockGrid"/>)
/// is built from.
/// </remarks>
internal static class JpxGeometry
{
    /// <summary>A half-open integer rectangle on some coordinate grid: <c>[X0, X1) × [Y0, Y1)</c>.</summary>
    internal readonly record struct Bounds(int X0, int Y0, int X1, int Y1)
    {
        public int Width => Math.Max(0, X1 - X0);

        public int Height => Math.Max(0, Y1 - Y0);

        public bool IsEmpty => X1 <= X0 || Y1 <= Y0;
    }

    /// <summary>Ceiling division for non-negative <paramref name="a"/> and positive <paramref name="b"/>: <c>⌈a/b⌉</c>.</summary>
    public static int CeilDiv(int a, int b) => (a + b - 1) / b;

    /// <summary>
    /// Tile (<paramref name="tileIndex"/>, raster order) bounds on the reference grid (B-5..B-9):
    /// the tile grid's cell, clipped to the image area <c>[XOsiz, Xsiz) × [YOsiz, Ysiz)</c>
    /// (producing a narrower/shorter cell for an edge tile whose nominal size does not divide the
    /// image evenly).
    /// </summary>
    public static Bounds TileBounds(JpxSiz siz, int tileIndex)
    {
        var p = tileIndex % siz.NumXTiles;
        var q = tileIndex / siz.NumXTiles;

        // Tile-grid arithmetic in long: XTOsiz + (p + 1)·XTsiz can pass int.MaxValue for the last
        // tile column of a grid whose origin or tile size sits near the 32-bit limit, and every
        // result is clamped to the image area (itself int-representable) before narrowing.
        var x0 = (int)Math.Max(siz.XTOsiz + ((long)p * siz.XTsiz), siz.XOsiz);
        var y0 = (int)Math.Max(siz.YTOsiz + ((long)q * siz.YTsiz), siz.YOsiz);
        var x1 = (int)Math.Min(siz.XTOsiz + ((long)(p + 1) * siz.XTsiz), siz.Xsiz);
        var y1 = (int)Math.Min(siz.YTOsiz + ((long)(q + 1) * siz.YTsiz), siz.Ysiz);

        return new Bounds(x0, y0, Math.Max(x0, x1), Math.Max(y0, y1));
    }

    /// <summary>
    /// Tile-component bounds (B-12): the tile's reference-grid bounds mapped onto component
    /// <paramref name="component"/>'s own (sub-sampled) grid via <c>⌈x / XRsiz⌉</c>/<c>⌈y / YRsiz⌉</c>
    /// at both edges — reference-grid sample <c>x</c> belongs to component sample
    /// <c>⌊x / XRsiz⌋</c>, so the half-open range <c>[tx0, tx1)</c> maps to
    /// <c>[⌈tx0/XRsiz⌉, ⌈tx1/XRsiz⌉)</c>.
    /// </summary>
    public static Bounds TileComponentBounds(Bounds tileBounds, JpxComponentInfo component) => new(
        CeilDiv(tileBounds.X0, component.XRsiz),
        CeilDiv(tileBounds.Y0, component.YRsiz),
        CeilDiv(tileBounds.X1, component.XRsiz),
        CeilDiv(tileBounds.Y1, component.YRsiz));

    /// <summary>
    /// Resolution <paramref name="r"/>'s bounds (B-14) on the tile-component's own grid:
    /// <paramref name="tileComponentBounds"/> divided by <c>2^(N_L − r)</c> (ceiling), i.e. the
    /// support of the low-pass output after <c>N_L − r</c> decomposition levels. <c>r = N_L</c> is
    /// full component resolution (no division); <c>r = 0</c> is the single coarsest LL.
    /// </summary>
    public static Bounds ResolutionBounds(Bounds tileComponentBounds, int decompositionLevels, int r)
    {
        // N_L may legitimately be 32 (Table A.15), so 2^(N_L − r) needs a long: an int shift by
        // 31 produced int.MinValue and by 32 was masked to 1, turning the coarsest resolutions
        // of any stream with N_L ≥ 31 into negative or full-size bounds and the inverse wavelet
        // into an OverflowException/IndexOutOfRangeException. A
        // divisor beyond the coordinate range simply yields a 0- or 1-sample resolution.
        var levelsRemaining = decompositionLevels - r;
        var divisor = 1L << levelsRemaining;
        return new Bounds(
            (int)CeilDiv(tileComponentBounds.X0, divisor),
            (int)CeilDiv(tileComponentBounds.Y0, divisor),
            (int)CeilDiv(tileComponentBounds.X1, divisor),
            (int)CeilDiv(tileComponentBounds.Y1, divisor));
    }

    private static long CeilDiv(long a, long b) => (a + b - 1) / b;

    /// <summary>
    /// Subband coordinates (B-15) for orientation <paramref name="orientation"/> (0 LL, 1 HL, 2 LH,
    /// 3 HH) derived from a resolution's own bounds (<paramref name="resolutionBounds"/>) by one level of the 1-D
    /// low/high split applied per axis (low: <c>⌈x0/2⌉..⌈x1/2⌉</c>; high: <c>⌊x0/2⌋..⌊x1/2⌋</c> — the
    /// "odd samples" half, since <c>⌈(x−1)/2⌉ ≡ ⌊x/2⌋</c> for <c>x ≥ 0</c>). Orientation 0 (LL) is
    /// only meaningful at <c>r = 0</c> and is <paramref name="resolutionBounds"/> unchanged (there is
    /// no coarser sibling to split away from).
    /// </summary>
    public static Bounds SubbandBounds(Bounds resolutionBounds, int orientation)
    {
        if (orientation == 0)
        {
            return resolutionBounds;
        }

        var lowX = (CeilDiv(resolutionBounds.X0, 2), CeilDiv(resolutionBounds.X1, 2));
        var highX = (resolutionBounds.X0 / 2, resolutionBounds.X1 / 2);
        var lowY = (CeilDiv(resolutionBounds.Y0, 2), CeilDiv(resolutionBounds.Y1, 2));
        var highY = (resolutionBounds.Y0 / 2, resolutionBounds.Y1 / 2);

        // 1 = HL (horizontal high, vertical low), 2 = LH (horizontal low, vertical high), 3 = HH.
        var (x0, x1) = orientation == 2 ? lowX : highX;
        var (y0, y1) = orientation == 1 ? lowY : highY;
        return new Bounds(x0, y0, x1, y1);
    }

    /// <summary>
    /// B.7 code-block exponent clamp: <c>xcb' = min(xcb, PPx)</c> at <c>r = 0</c>,
    /// <c>min(xcb, PPx − 1)</c> at <c>r &gt; 0</c> (a detail subband's own coordinate space is one
    /// halving finer than its resolution's precinct exponent).
    /// </summary>
    public static (int Xcb, int Ycb) ClampCodeBlockExponents(int xcb, int ycb, int ppx, int ppy, int r)
    {
        var xLimit = r == 0 ? ppx : ppx - 1;
        var yLimit = r == 0 ? ppy : ppy - 1;
        return (Math.Min(xcb, xLimit), Math.Min(ycb, yLimit));
    }

    /// <summary>
    /// The precinct-size exponent a subband at resolution <paramref name="r"/> partitions itself
    /// with, given the resolution's own precinct exponent <paramref name="resolutionExponent"/>
    /// (B.7): unchanged at <c>r = 0</c> (the LL subband occupies the whole precinct), one smaller
    /// at <c>r &gt; 0</c> (a detail subband's coordinate space is one halving finer).
    /// </summary>
    public static int SubbandPrecinctExponent(int resolutionExponent, int r) => r == 0 ? resolutionExponent : resolutionExponent - 1;

    /// <summary>
    /// Partitions the 1-D range <c>[b0, b1)</c> into cells of size <c>2^exponent</c>, anchored at
    /// absolute coordinate 0 (B-16 / B.7): the first cell index intersecting the range and how many
    /// cells there are. Returns <c>(0, 0)</c> for an empty range.
    /// </summary>
    public static (int Start, int Count) GridPartition(int b0, int b1, int exponent)
    {
        if (b1 <= b0)
        {
            return (0, 0);
        }

        var cellSize = 1 << exponent;
        var start = b0 / cellSize;
        var endExclusive = CeilDiv(b1, cellSize);
        return (start, Math.Max(0, endExclusive - start));
    }

    /// <summary>Bounds of grid cell <paramref name="index"/> (size <c>2^exponent</c>, anchored at 0) clipped to <c>[clampLo, clampHi)</c>.</summary>
    public static (int Lo, int Hi) GridCellBounds(int index, int exponent, int clampLo, int clampHi)
    {
        // (index + 1)·2^exponent can exceed int.MaxValue for the last cell of a range that ends
        // near the 32-bit limit; clamp in long, then narrow (the clamps are int-representable).
        var cellSize = 1L << exponent;
        var lo = (int)Math.Max(index * cellSize, clampLo);
        var hi = (int)Math.Min((index + 1) * cellSize, clampHi);
        return (lo, Math.Max(lo, hi));
    }

    /// <summary>The precinct grid over <paramref name="bandBounds"/> (a resolution's own bounds for the LL band, or a detail subband's bounds), using per-axis exponents already adjusted by <see cref="SubbandPrecinctExponent"/> for the band's own coordinate space.</summary>
    public static (int PxStart, int PyStart, int Wide, int High) PrecinctGrid(Bounds bandBounds, int ppxExponent, int ppyExponent)
    {
        var (pxStart, wide) = GridPartition(bandBounds.X0, bandBounds.X1, ppxExponent);
        var (pyStart, high) = GridPartition(bandBounds.Y0, bandBounds.Y1, ppyExponent);
        return (pxStart, pyStart, wide, high);
    }

    /// <summary>Bounds of precinct (<paramref name="px"/>, <paramref name="py"/>) within <paramref name="bandBounds"/>.</summary>
    public static Bounds PrecinctBounds(Bounds bandBounds, int px, int py, int ppxExponent, int ppyExponent)
    {
        var (x0, x1) = GridCellBounds(px, ppxExponent, bandBounds.X0, bandBounds.X1);
        var (y0, y1) = GridCellBounds(py, ppyExponent, bandBounds.Y0, bandBounds.Y1);
        return new Bounds(x0, y0, x1, y1);
    }

    /// <summary>The code-block grid over <paramref name="precinctBounds"/> (already clipped to precinct ∩ subband), using the B.7-clamped exponents.</summary>
    public static (int CbxStart, int CbyStart, int Wide, int High) CodeBlockGrid(Bounds precinctBounds, int xcbExponent, int ycbExponent)
    {
        var (cbxStart, wide) = GridPartition(precinctBounds.X0, precinctBounds.X1, xcbExponent);
        var (cbyStart, high) = GridPartition(precinctBounds.Y0, precinctBounds.Y1, ycbExponent);
        return (cbxStart, cbyStart, wide, high);
    }

    /// <summary>Bounds of code-block (<paramref name="cbx"/>, <paramref name="cby"/>) within <paramref name="precinctBounds"/>.</summary>
    public static Bounds CodeBlockBounds(Bounds precinctBounds, int cbx, int cby, int xcbExponent, int ycbExponent)
    {
        var (x0, x1) = GridCellBounds(cbx, xcbExponent, precinctBounds.X0, precinctBounds.X1);
        var (y0, y1) = GridCellBounds(cby, ycbExponent, precinctBounds.Y0, precinctBounds.Y1);
        return new Bounds(x0, y0, x1, y1);
    }

    /// <summary><c>gain_b</c> exponent of <c>R_b</c> (E-4): 0 for LL, 1 for HL/LH, 2 for HH.</summary>
    public static int SubbandGain(int orientation) => orientation switch
    {
        0 => 0,
        3 => 2,
        _ => 1,
    };

    /// <summary>
    /// <c>n_b</c>, the decomposition-step index a subband was produced by, used by the style-1
    /// (scalar derived) quantisation formula <c>ε_b = ε_0 − N_L + n_b</c>: <c>N_L</c> for the LL
    /// band and for resolution 1's detail bands (both are products of the last, <c>N_L</c>-th,
    /// decomposition step); <c>N_L − r + 1</c> for resolution <c>r ≥ 1</c>'s detail bands in
    /// general (down to 1 for the finest, resolution <c>N_L</c>).
    /// </summary>
    public static int DecompositionStepIndex(int decompositionLevels, int r) => r == 0 ? decompositionLevels : decompositionLevels - r + 1;

    /// <summary>Per-subband quantisation facts (ε_b, μ_b, M_b) after QCD/QCC-style derivation (E.1, E-2, E-5).</summary>
    public static (int Epsilon, int Mu, int Mb) SubbandQuantisation(JpxQuantization quant, int componentPrecision, int decompositionLevels, int r, int orientation)
    {
        int epsilon, mu;
        switch (quant.Style)
        {
            case 1: // Scalar derived: one reference (ε_0, μ_0) for the N_L (LL) subband, others derived.
                {
                    var (refExponent, refMantissa) = quant.Steps[0];
                    var nb = DecompositionStepIndex(decompositionLevels, r);
                    epsilon = refExponent - decompositionLevels + nb;
                    mu = refMantissa;
                    break;
                }

            default: // None (reversible, style 0) and scalar expounded (style 2) both transmit one
                     // explicit SPqcd entry per subband, in LL-then-(HL,LH,HH)-per-resolution order
                     // (T.800 Table A.28: style 0's entry is the 5-bit exponent alone, no mantissa —
                     // ParseQuantization already leaves Mantissa at 0 for it, so reading it through
                     // this same per-subband index is correct for both styles).
                {
                    var index = orientation == 0 ? 0 : 1 + ((r - 1) * 3) + (orientation - 1);
                    index = Math.Clamp(index, 0, quant.Steps.Length - 1);
                    (epsilon, mu) = quant.Steps[index];
                    break;
                }
        }

        var mb = quant.GuardBits + epsilon - 1;
        return (epsilon, mu, mb);
    }

    /// <summary>
    /// Builds the full tile → component → resolution → subband → precinct → code-block structure
    /// for tile <paramref name="tileIndex"/>: every code-block starts with
    /// <c>Lblock = 3</c> (B.10.7.1) and no segments/coefficients yet — tier-2 and tier-1
    /// fill those in afterward.
    /// </summary>
    public static JpxTile BuildTile(JpxCodestreamHeader header, int tileIndex) => BuildTile(header, tileIndex, budget: null);

    /// <summary>
    /// <see cref="BuildTile(JpxCodestreamHeader, int)"/> with the precinct and code-block object
    /// graph charged to <paramref name="budget"/> before it is allocated
    /// (<see cref="JpxDecodeBudget.BytesPerPrecinct"/>/<see cref="JpxDecodeBudget.BytesPerCodeBlock"/>
    /// each, per tile-component, before any of that component's partition is built): a
    /// <c>PPx = PPy = 0</c> partition at <c>r = 0</c> over a large tile declares one precinct and
    /// one code-block per sample — tens of millions of objects from a 100-byte header —
    /// and is refused as <c>PLUME3718</c> here, at the count, rather than
    /// discovered at the allocation. The caller releases what it charged once the tile is decoded.
    /// </summary>
    public static JpxTile BuildTile(JpxCodestreamHeader header, int tileIndex, JpxDecodeBudget? budget)
    {
        var siz = header.Siz;
        var tileBounds = TileBounds(siz, tileIndex);
        var hasOverride = header.TileOverrides.TryGetValue(tileIndex, out var overrides);
        var codingPerComponent = hasOverride ? overrides.Coding : header.Coding;
        var quantPerComponent = hasOverride ? overrides.Quant : header.Quant;

        var components = new JpxTileComponent[siz.Components.Length];
        for (var c = 0; c < components.Length; c++)
        {
            components[c] = BuildTileComponent(c, siz.Components[c], codingPerComponent[c], quantPerComponent[c], tileBounds, budget);
        }

        return new JpxTile
        {
            Index = tileIndex,
            X0 = tileBounds.X0,
            Y0 = tileBounds.Y0,
            X1 = tileBounds.X1,
            Y1 = tileBounds.Y1,
            Components = components,
            ProgressionVolumes = ResolveProgressionVolumes(header, tileIndex),
        };
    }

    private static JpxTileComponent BuildTileComponent(int componentIndex, JpxComponentInfo component, JpxCodingStyle coding, JpxQuantization quant, Bounds tileBounds, JpxDecodeBudget? budget)
    {
        var tcBounds = TileComponentBounds(tileBounds, component);
        var nl = coding.DecompositionLevels;
        var resolutions = new JpxResolution[nl + 1];

        if (budget is not null)
        {
            // Charge the whole tile-component's partition from its counts before building any
            // resolution of it: a PPx = 1 partition at r = 0 over an 8000² tile is 4 million
            // precincts, and building those first only to be refused at r = 1 is a gigabyte and
            // most of a second the refusal exists to avoid.
            var bytes = 0L;
            for (var r = 0; r <= nl; r++)
            {
                bytes += PartitionBytes(r, nl, tcBounds, coding);
            }

            budget.Charge(bytes, $"component {componentIndex}'s precinct/code-block partition");
        }

        for (var r = 0; r <= nl; r++)
        {
            resolutions[r] = BuildResolution(r, nl, tcBounds, coding, quant, component.Precision);
        }

        return new JpxTileComponent
        {
            Component = componentIndex,
            X0 = tcBounds.X0,
            Y0 = tcBounds.Y0,
            X1 = tcBounds.X1,
            Y1 = tcBounds.Y1,
            Coding = coding,
            Quant = quant,
            Resolutions = resolutions,
        };
    }

    /// <summary>
    /// Bytes the partition objects of resolution <paramref name="r"/> will occupy
    /// (<see cref="JpxDecodeBudget.BytesPerPrecinct"/>/<see cref="JpxDecodeBudget.BytesPerCodeBlock"/>
    /// each): precincts from the resolution's grid, code-blocks bounded above by each subband's
    /// own cell count at the clamped exponents plus one cell per precinct boundary (every
    /// precinct's blocks tile a disjoint part of the band, so that is an upper bound on their sum).
    /// </summary>
    private static long PartitionBytes(int r, int nl, Bounds tcBounds, JpxCodingStyle coding)
    {
        var resolutionBounds = ResolutionBounds(tcBounds, nl, r);
        var ppx = coding.PrecinctExponentsX[r];
        var ppy = coding.PrecinctExponentsY[r];
        var (_, _, precinctsWide, precinctsHigh) = PrecinctGrid(resolutionBounds, ppx, ppy);
        var (xcbAdj, ycbAdj) = ClampCodeBlockExponents(coding.Xcb, coding.Ycb, ppx, ppy, r);
        var subbandCount = r == 0 ? 1 : 3;

        var bytes = 0L;
        for (var s = 0; s < subbandCount; s++)
        {
            var bandBounds = SubbandBounds(resolutionBounds, r == 0 ? 0 : s + 1);
            var (_, cbWideBand) = GridPartition(bandBounds.X0, bandBounds.X1, xcbAdj);
            var (_, cbHighBand) = GridPartition(bandBounds.Y0, bandBounds.Y1, ycbAdj);
            var codeBlockBound = ((long)cbWideBand + precinctsWide) * ((long)cbHighBand + precinctsHigh);
            bytes += ((long)precinctsWide * precinctsHigh * JpxDecodeBudget.BytesPerPrecinct) + (codeBlockBound * JpxDecodeBudget.BytesPerCodeBlock);
        }

        return bytes;
    }

    private static JpxResolution BuildResolution(int r, int nl, Bounds tcBounds, JpxCodingStyle coding, JpxQuantization quant, int componentPrecision)
    {
        var resolutionBounds = ResolutionBounds(tcBounds, nl, r);
        var ppx = coding.PrecinctExponentsX[r];
        var ppy = coding.PrecinctExponentsY[r];
        var (resPxStart, resPyStart, precinctsWide, precinctsHigh) = PrecinctGrid(resolutionBounds, ppx, ppy);

        var subbandCount = r == 0 ? 1 : 3;
        var subbands = new JpxSubband[subbandCount];
        for (var s = 0; s < subbandCount; s++)
        {
            var orientation = r == 0 ? 0 : s + 1;
            subbands[s] = BuildSubband(orientation, r, nl, resolutionBounds, ppx, ppy, resPxStart, resPyStart, precinctsWide, precinctsHigh, coding, quant, componentPrecision);
        }

        return new JpxResolution
        {
            Level = r,
            X0 = resolutionBounds.X0,
            Y0 = resolutionBounds.Y0,
            X1 = resolutionBounds.X1,
            Y1 = resolutionBounds.Y1,
            PPx = ppx,
            PPy = ppy,
            PrecinctsWide = precinctsWide,
            PrecinctsHigh = precinctsHigh,
            Subbands = subbands,
        };
    }

    private static JpxSubband BuildSubband(int orientation, int r, int nl, Bounds resolutionBounds, int ppx, int ppy, int resPxStart, int resPyStart, int precinctsWide, int precinctsHigh, JpxCodingStyle coding, JpxQuantization quant, int componentPrecision)
    {
        var bandBounds = SubbandBounds(resolutionBounds, orientation);
        var ppxAdj = SubbandPrecinctExponent(ppx, r);
        var ppyAdj = SubbandPrecinctExponent(ppy, r);
        var (xcbAdj, ycbAdj) = ClampCodeBlockExponents(coding.Xcb, coding.Ycb, ppx, ppy, r);

        var precincts = new JpxPrecinct[precinctsWide * precinctsHigh];
        for (var py = 0; py < precinctsHigh; py++)
        {
            for (var px = 0; px < precinctsWide; px++)
            {
                var precinctBounds = PrecinctBounds(bandBounds, resPxStart + px, resPyStart + py, ppxAdj, ppyAdj);
                var (cbxStart, cbyStart, cbWide, cbHigh) = CodeBlockGrid(precinctBounds, xcbAdj, ycbAdj);
                var codeBlocks = new JpxCodeBlock[cbWide * cbHigh];
                for (var cby = 0; cby < cbHigh; cby++)
                {
                    for (var cbx = 0; cbx < cbWide; cbx++)
                    {
                        var cbBounds = CodeBlockBounds(precinctBounds, cbxStart + cbx, cbyStart + cby, xcbAdj, ycbAdj);
                        codeBlocks[(cby * cbWide) + cbx] = new JpxCodeBlock
                        {
                            X0 = cbBounds.X0,
                            Y0 = cbBounds.Y0,
                            X1 = cbBounds.X1,
                            Y1 = cbBounds.Y1,
                        };
                    }
                }

                precincts[(py * precinctsWide) + px] = new JpxPrecinct
                {
                    X0 = precinctBounds.X0,
                    Y0 = precinctBounds.Y0,
                    X1 = precinctBounds.X1,
                    Y1 = precinctBounds.Y1,
                    CodeBlocks = codeBlocks,
                    CodeBlocksWide = cbWide,
                };
            }
        }

        var (epsilon, mu, mb) = SubbandQuantisation(quant, componentPrecision, nl, r, orientation);
        return new JpxSubband
        {
            Orientation = orientation,
            X0 = bandBounds.X0,
            Y0 = bandBounds.Y0,
            X1 = bandBounds.X1,
            Y1 = bandBounds.Y1,
            Epsilon = epsilon,
            Mu = mu,
            Mb = mb,
            Gain = SubbandGain(orientation),
            Precincts = precincts,
        };
    }

    private static JpxProgressionVolume[] ResolveProgressionVolumes(JpxCodestreamHeader header, int tileIndex)
    {
        foreach (var tilePart in header.TileParts)
        {
            if (tilePart.TileIndex == tileIndex && tilePart.Poc is { Length: > 0 } tilePoc)
            {
                return tilePoc;
            }
        }

        return header.MainPoc;
    }
}
