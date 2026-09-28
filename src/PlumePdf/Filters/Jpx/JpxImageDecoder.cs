namespace PlumePdf.Filters.Jpx;

/// <summary>
/// The decoder entry point: accepts a JP2 file or a raw codestream, enforces
/// <see cref="PdfOptions.MaxImagePixels"/> on the <c>SIZ</c> grid before allocating, decodes tile by
/// tile into native-width planes, and returns a colour-space-ready <see cref="JpxImage"/>. Stateless;
/// safe for concurrent callers.
/// </summary>
/// <remarks>
/// Memory posture (mechanically enforced after profiling found a memory-amplification bug class): every
/// header-driven allocation is charged to a per-call <see cref="JpxDecodeBudget"/> before it is
/// made, so no header can make the decoder hold more than
/// <see cref="JpxDecodeBudget.BytesPerReferencePixel"/> bytes per permitted pixel. Within a tile,
/// components are decoded one at a time (tier-1 → dequantise/IDWT → level shift → written into
/// the whole-image plane, then released) — except the three the multiple component transform
/// couples, which are held together only until the RCT/ICT has run.
/// </remarks>
internal static class JpxImageDecoder
{
    private static readonly byte[] Jp2Signature = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    /// <summary>Decodes <paramref name="data"/> (JP2 or raw J2K).</summary>
    public static JpxImage Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(options);

        ReadOnlyMemory<byte> codestreamMemory;
        JpxColourInfo colour;
        (int NC, int BPC)? ihdr;

        if (LooksLikeJp2(data.Span))
        {
            var container = Jp2Boxes.Parse(data, options, diagnostics);
            codestreamMemory = container.Codestream;
            colour = container.Colour;
            ihdr = container.Ihdr;
        }
        else
        {
            codestreamMemory = data;
            colour = new JpxColourInfo();
            ihdr = null;
        }

        var header = JpxCodestream.Parse(codestreamMemory, options, diagnostics);
        var siz = header.Siz;

        CheckIhdrAgreement(ihdr, siz, options, diagnostics);

        var referenceWidth = siz.Xsiz - siz.XOsiz;
        var referenceHeight = siz.Ysiz - siz.YOsiz;
        var referencePixels = (long)referenceWidth * referenceHeight;
        if (referencePixels > options.MaxImagePixels)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.ImageTooLarge, $"JPEG 2000 reference grid ({referenceWidth}x{referenceHeight} = {referencePixels} pixels) exceeds PdfOptions.MaxImagePixels ({options.MaxImagePixels}).");
        }

        if (referencePixels > Array.MaxLength)
        {
            // Every plane this decoder allocates — a component's native grid, a tile-component's
            // samples, a wavelet level, an upsampled or palette-expanded plane — holds at most one
            // element per reference-grid pixel, so this one bound makes every derived element count
            // below representable as an array length. A caller who raises MaxImagePixels past
            // Array.MaxLength (public long; 6 000 000 000 in one probe) otherwise reached
            // an int-overflowing `Width * Height` and a System.OverflowException at the `new`:
            // refused here as the cap's own code, since no
            // MaxImagePixels value can make such a plane allocatable.
            throw new PlumePdfException(JpxDiagnosticCodes.ImageTooLarge, $"JPEG 2000 reference grid ({referenceWidth}x{referenceHeight} = {referencePixels} pixels) exceeds the {Array.MaxLength} samples one plane can hold, whatever PdfOptions.MaxImagePixels permits.");
        }

        var componentCount = siz.Components.Length;
        var imageBounds = new JpxGeometry.Bounds(siz.XOsiz, siz.YOsiz, siz.Xsiz, siz.Ysiz);
        var componentBounds = new JpxGeometry.Bounds[componentCount];
        var totalComponentPixels = 0L;
        var totalPlaneBytes = 0L;
        var totalUpsampledBytes = 0L;
        for (var c = 0; c < componentCount; c++)
        {
            componentBounds[c] = JpxGeometry.TileComponentBounds(imageBounds, siz.Components[c]);
            var pixels = (long)componentBounds[c].Width * componentBounds[c].Height;
            var bytesPerSample = siz.Components[c].Precision <= 8 ? 1 : 2;
            totalComponentPixels += pixels;
            totalPlaneBytes += pixels * bytesPerSample;
            if (pixels != referencePixels)
            {
                totalUpsampledBytes += referencePixels * bytesPerSample;
            }
        }

        // The per-image pixel bound applies to the actual allocation, which is one full-grid
        // plane PER COMPONENT — the single-plane reference-grid check above bounds Csiz==1 but
        // says nothing about Csiz, which SIZ's own 16-bit segment length permits up to ~21832.
        // Summing each component's own (possibly sub-sampled) grid credits sub-sampling instead
        // of over-rejecting a legitimately large sub-sampled multi-component image; the
        // UpsampleNearest pass that later restores every sub-sampled component to the full
        // grid is charged separately, in full, to the working-memory budget below.
        if (totalComponentPixels > options.MaxImagePixels)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.ImageTooLarge, $"JPEG 2000 image's {componentCount} component(s) total {totalComponentPixels} samples, exceeding PdfOptions.MaxImagePixels ({options.MaxImagePixels}).");
        }

        var budget = new JpxDecodeBudget(options.MaxImagePixels);
        budget.RequireOutputWithinLimit(componentCount, referencePixels, "its components upsampled to the reference grid");
        budget.Charge(totalPlaneBytes, $"{componentCount} native-width component plane(s)");
        budget.Charge(totalUpsampledBytes, "upsampling the sub-sampled component(s) to the reference grid");
        if (colour.Palette is { } declaredPalette)
        {
            budget.RequireOutputWithinLimit(JpxComponentTransform.PaletteOutputPlanes(declaredPalette, colour.ChannelMap), referencePixels, "its palette-expanded planes");
            budget.Charge(JpxComponentTransform.PaletteExpansionBytes(declaredPalette, colour.ChannelMap, referencePixels), "the palette-expanded output planes");
        }

        var full8 = new byte[componentCount][];
        var full16 = new ushort[componentCount][];
        for (var c = 0; c < componentCount; c++)
        {
            var count = checked((int)((long)componentBounds[c].Width * componentBounds[c].Height)); // ≤ referencePixels ≤ Array.MaxLength, checked above
            if (siz.Components[c].Precision <= 8)
            {
                full8[c] = new byte[count];
            }
            else
            {
                full16[c] = new ushort[count];
            }
        }

        var anyTileAppliedMct = false;
        var reportedMctWithoutThreeComponents = false;
        foreach (var (tileIndex, tileParts) in GroupTilePartsByTile(header))
        {
            var usedBeforeTile = budget.Used;
            var tile = JpxGeometry.BuildTile(header, tileIndex, budget);
            JpxPacketDecoder.DecodeTilePackets(tile, header, tileParts, options, diagnostics);

            var tileMct = tile.Components[0].Coding.Mct && componentCount >= 3;
            if (tileMct)
            {
                RequireMctComponentsShareOneGrid(tile, siz);
                anyTileAppliedMct = true;
            }
            else if (tile.Components[0].Coding.Mct && !reportedMctWithoutThreeComponents)
            {
                // T.800 Table A.17 defines the multiple component transform on components 0–2;
                // a COD declaring it for a one- or two-component codestream is internally
                // inconsistent. There is nothing to transform, so the samples that exist decode
                // exactly as they were coded — reported once per decode under PLUME3707's
                // recoverable arm rather than ignored silently.
                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.HeaderInvalid, $"COD declares a multiple component transform (SGcod MCT = 1), but the codestream has only {componentCount} component(s); T.800 Table A.17 defines the transform on components 0-2. Decoding without it.", options, diagnostics, null);
                reportedMctWithoutThreeComponents = true;
            }

            // Components 0-2 are reconstructed together only when the MCT couples them; every
            // other component (and every component of an MCT-less tile) is reconstructed,
            // written into its plane and released before the next one is touched.
            var groupEnd = tileMct ? 3 : 0;
            if (tileMct)
            {
                var group = new int[3][];
                for (var c = 0; c < 3; c++)
                {
                    group[c] = ReconstructTileComponent(tile, c, siz.Components[c], tileParts, budget, diagnostics);
                }

                if (tile.Components[0].Coding.Reversible53)
                {
                    JpxComponentTransform.InverseRct(group[0], group[1], group[2]);
                }
                else
                {
                    JpxComponentTransform.InverseIct(group[0], group[1], group[2]);
                }

                for (var c = 0; c < 3; c++)
                {
                    StoreTileComponent(group[c], tile.Components[c], siz.Components[c], componentBounds[c], full8[c], full16[c]);
                    budget.Release(4L * group[c].Length);
                }
            }

            for (var c = groupEnd; c < componentCount; c++)
            {
                var samples = ReconstructTileComponent(tile, c, siz.Components[c], tileParts, budget, diagnostics);
                StoreTileComponent(samples, tile.Components[c], siz.Components[c], componentBounds[c], full8[c], full16[c]);
                budget.Release(4L * samples.Length);
            }

            // The tile's partition (precincts, code-blocks, segment lists) is garbage from here.
            budget.Release(budget.Used - usedBeforeTile);
        }

        var planes = new JpxPlane[componentCount];
        for (var c = 0; c < componentCount; c++)
        {
            var info = siz.Components[c];
            var native = new JpxPlane
            {
                Width = componentBounds[c].Width,
                Height = componentBounds[c].Height,
                Precision = info.Precision,
                Signed = info.Signed,
                Samples8 = full8[c],
                Samples16 = full16[c],
            };
            planes[c] = JpxComponentTransform.UpsampleNearest(native, referenceWidth, referenceHeight, info.XRsiz, info.YRsiz, x0Offset: 0, y0Offset: 0);
        }

        // Annex I.5.3.3: sYCC (EnumCS 18) is a JP2-wrapper colour conversion, distinct from the
        // codestream's own MCT — apply it only when no tile's codestream already produced RGB
        // (a per-tile COD may switch the MCT on for some tiles only; converting the others
        // would leave the image half-converted, so any MCT tile means the wrapper rule yields).
        if (colour.EnumeratedColourSpace == 18 && componentCount >= 3 && !anyTileAppliedMct)
        {
            JpxComponentTransform.SyccToRgb(planes[0], planes[1], planes[2]);
        }

        if (colour.Palette is { } palette)
        {
            planes = JpxComponentTransform.ExpandPalette(planes[0], palette, colour.ChannelMap, options, diagnostics);
        }

        if (colour.AlphaChannelIndex is { } alpha && (alpha < 0 || alpha >= planes.Length))
        {
            // I.5.3.6's channel index names one of the decoded planes; one past them would make
            // JpxImage.SelectPlanes index out of range at the first ToInterleaved8Bit call.
            // The box is ignored, per its family's contract.
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"cdef box names channel {alpha} as the opacity channel, but the image has only {planes.Length} plane(s); ignoring the cdef box.", options, diagnostics, null);
            colour.AlphaChannelIndex = null;
            colour.AlphaPremultiplied = false;
        }

        return new JpxImage { Width = referenceWidth, Height = referenceHeight, Planes = planes, Colour = colour };
    }

    /// <summary><see langword="true"/> when <paramref name="data"/> starts with the JP2 signature box or a raw <c>SOC</c>+<c>SIZ</c> (<c>FF 4F FF 51</c>).</summary>
    public static bool IsJpx(ReadOnlySpan<byte> data) =>
        LooksLikeJp2(data) || (data.Length >= 4 && data[0] == 0xFF && data[1] == 0x4F && data[2] == 0xFF && data[3] == 0x51);

    private static bool LooksLikeJp2(ReadOnlySpan<byte> data) =>
        data.Length >= Jp2Signature.Length && data[..Jp2Signature.Length].SequenceEqual(Jp2Signature);

    /// <summary>
    /// Every tile that has at least one tile-part, in tile-index order, each with its own
    /// tile-parts in <c>TPsot</c> order (the list <see cref="JpxSegmentRef.TilePartIndex"/> indexes
    /// into, per <see cref="JpxPacketDecoder.DecodeTilePackets"/>'s and
    /// <see cref="JpxBlockDecoder.Decode"/>'s shared contract). Grouped once from the header's
    /// list rather than by scanning it per tile: a <c>SIZ</c> declaring millions of 1×1 tiles is
    /// a legal header, and iterating the declared grid (allocating a list per tile, most of them
    /// empty) cost 2 GB and ten seconds for an 84-byte input.
    /// </summary>
    private static IEnumerable<(int TileIndex, List<JpxTilePart> TileParts)> GroupTilePartsByTile(JpxCodestreamHeader header)
    {
        var groups = new SortedDictionary<int, List<JpxTilePart>>();
        foreach (var tilePart in header.TileParts)
        {
            if (!groups.TryGetValue(tilePart.TileIndex, out var list))
            {
                list = new List<JpxTilePart>();
                groups[tilePart.TileIndex] = list;
            }

            list.Add(tilePart);
        }

        foreach (var (tileIndex, list) in groups)
        {
            list.Sort(static (a, b) => a.TPsot.CompareTo(b.TPsot));
            yield return (tileIndex, list);
        }
    }

    /// <summary>
    /// T.800 G.1: the RCT/ICT operate sample-by-sample across components 0–2, which therefore
    /// must share one sample grid (identical sub-sampling and tile-component bounds) and one
    /// bit depth (the transform's integer arithmetic and the level shift that follows it are
    /// defined for three components of the same <c>Ssiz</c> precision). A stream declaring
    /// <c>MCT = 1</c> over components of different grids is non-conformant and would otherwise
    /// index the shorter components past their ends (a real fuzz-testing finding); one over
    /// components of different depths would mix, say, 8-bit and 12-bit
    /// ranges in one subtraction. Refused, never mis-decoded:
    /// applying the transform to mismatched components, or silently skipping it, both paint the
    /// wrong colours.
    /// </summary>
    private static void RequireMctComponentsShareOneGrid(JpxTile tile, JpxSiz siz)
    {
        var c0 = tile.Components[0];
        for (var c = 1; c < 3; c++)
        {
            var tc = tile.Components[c];
            if (tc.X0 != c0.X0 || tc.Y0 != c0.Y0 || tc.X1 != c0.X1 || tc.Y1 != c0.Y1)
            {
                throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"COD declares a multiple component transform, but component {c}'s sample grid ({tc.X1 - tc.X0}x{tc.Y1 - tc.Y0} in tile {tile.Index}) differs from component 0's ({c0.X1 - c0.X0}x{c0.Y1 - c0.Y0}); T.800 G.1 requires identical grids for components 0-2.");
            }

            if (siz.Components[c].Precision != siz.Components[0].Precision)
            {
                throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"COD declares a multiple component transform, but component {c} is {siz.Components[c].Precision}-bit while component 0 is {siz.Components[0].Precision}-bit; T.800 G.1 requires the same bit depth for components 0-2.");
            }
        }
    }

    /// <summary>
    /// Tier-1 decode, dequantise and inverse-transform one tile-component into a fresh sample
    /// buffer (still before the DC level shift). The buffer (4 bytes/sample) and the tier-1
    /// coefficients it is gathered from (another 4 bytes/sample, released by
    /// <see cref="JpxWavelet.Reconstruct(JpxTileComponent, JpxComponentInfo, Span{int}, JpxDecodeBudget?)"/>
    /// as it goes) are charged to <paramref name="budget"/>; the caller releases the buffer's
    /// share once the samples are stored.
    /// </summary>
    private static int[] ReconstructTileComponent(JpxTile tile, int component, JpxComponentInfo info, IReadOnlyList<JpxTilePart> tileParts, JpxDecodeBudget budget, DiagnosticCollection? diagnostics)
    {
        var tc = tile.Components[component];
        var sampleCount = (long)(tc.X1 - tc.X0) * (tc.Y1 - tc.Y0);
        budget.Charge(8L * sampleCount, $"tile {tile.Index} component {component}'s coefficients and samples");

        foreach (var resolution in tc.Resolutions)
        {
            foreach (var subband in resolution.Subbands)
            {
                foreach (var precinct in subband.Precincts)
                {
                    foreach (var block in precinct.CodeBlocks)
                    {
                        JpxBlockDecoder.Decode(block, tileParts, tc.Coding, subband.Orientation, subband.Mb, diagnostics);
                    }
                }
            }
        }

        var samples = new int[sampleCount];
        JpxWavelet.Reconstruct(tc, info, samples, budget);
        budget.Release(4L * sampleCount); // the tier-1 coefficients, dropped block by block during Reconstruct
        return samples;
    }

    /// <summary>Level-shifts (unsigned components only, G.1.2) and writes one tile-component's reconstructed samples into the whole-image native-resolution buffer at its tile offset.</summary>
    private static void StoreTileComponent(int[] samples, JpxTileComponent tc, JpxComponentInfo info, JpxGeometry.Bounds componentBounds, byte[]? full8, ushort[]? full16)
    {
        if (!info.Signed)
        {
            JpxComponentTransform.LevelShiftUnsigned(samples, info.Precision);
        }

        WriteTileComponent(samples, tc.X1 - tc.X0, tc.Y1 - tc.Y0, info, tc.X0 - componentBounds.X0, tc.Y0 - componentBounds.Y0, componentBounds.Width, full8, full16);
    }

    /// <summary>
    /// Writes one tile-component's reconstructed samples, saturated to
    /// <see cref="JpxComponentInfo.Precision"/> (<see cref="JpxComponentTransform.ClampToPrecision"/>:
    /// T.800 G.1.2 clamps, for signed and unsigned components alike — the irreversible transform's
    /// ringing overshoots the nominal range at sharp edges, and a mask would wrap that into
    /// speckles), into the whole-image native-resolution buffer at its tile offset.
    /// </summary>
    private static void WriteTileComponent(int[] samples, int width, int height, JpxComponentInfo info, int destX0, int destY0, int fullWidth, byte[]? full8, ushort[]? full16)
    {
        for (var y = 0; y < height; y++)
        {
            var destRow = ((destY0 + y) * fullWidth) + destX0;
            var srcRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var v = JpxComponentTransform.ClampToPrecision(samples[srcRow + x], info);
                if (full8 is not null)
                {
                    full8[destRow + x] = (byte)v;
                }
                else
                {
                    full16![destRow + x] = (ushort)v;
                }
            }
        }
    }

    /// <summary>PLUME3714 (Jp2BoxInvalid): flags when <c>ihdr</c>'s component count or (uniform, non-<c>bpcc</c>) bit depth disagrees with the codestream's own <c>SIZ</c>; the codestream's own facts always win.</summary>
    private static void CheckIhdrAgreement((int NC, int BPC)? ihdr, JpxSiz siz, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (ihdr is not { } value || siz.Components.Length == 0)
        {
            return;
        }

        var bpcVariesPerComponent = value.BPC == 0xFF;
        var mismatchNc = value.NC != siz.Components.Length;
        var mismatchBpc = !bpcVariesPerComponent &&
            (((value.BPC & 0x7F) + 1 != siz.Components[0].Precision) || ((value.BPC & 0x80) != 0) != siz.Components[0].Signed);

        if (mismatchNc || mismatchBpc)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, $"jp2h's ihdr box (NC={value.NC}, BPC=0x{value.BPC:X2}) disagrees with the codestream's own SIZ; ignoring ihdr.", options, diagnostics, null);
        }
    }
}
