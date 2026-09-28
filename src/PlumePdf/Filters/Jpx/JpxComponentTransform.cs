namespace PlumePdf.Filters.Jpx;

/// <summary>
/// Component-level post-processing (T.800 Annex G and the JP2 colour rules of Annex I): inverse RCT
/// and ICT, the DC level shift for unsigned components, packing to native-width planes, nearest-
/// neighbour upsampling to the reference grid, palette expansion, and the sYCC → RGB conversion.
/// </summary>
internal static class JpxComponentTransform
{
    // ITU-R BT.601 (T.800 Annex G.3.2) YCbCr → RGB inverse matrix — the same constants the
    // irreversible component transform and the JP2 sYCC colour space both use.
    private const double CrToR = 1.402;
    private const double CbToG = -0.344136;
    private const double CrToG = -0.714136;
    private const double CbToB = 1.772;

    /// <summary>Inverse reversible component transform (G.2), in place on components 0–2: exact integer <c>G = Y − ⌊(Cb+Cr)/4⌋</c>, <c>R = Cr+G</c>, <c>B = Cb+G</c>. The three spans must be the same length (G.1: the transformed components share one sample grid).</summary>
    public static void InverseRct(Span<int> c0, Span<int> c1, Span<int> c2)
    {
        RequireSameLength(c0, c1, c2);
        for (var i = 0; i < c0.Length; i++)
        {
            var y = c0[i];
            var cb = c1[i]; // I1 = B − G
            var cr = c2[i]; // I2 = R − G
            var g = y - ((cb + cr) >> 2); // arithmetic shift = exact floor for two's-complement ints
            var r = cr + g;
            var b = cb + g;
            c0[i] = r;
            c1[i] = g;
            c2[i] = b;
        }
    }

    /// <summary>Inverse irreversible component transform (G.3), in place on components 0–2: the BT.601 YCbCr → RGB matrix, rounded to nearest integer. The three spans must be the same length (G.1: the transformed components share one sample grid).</summary>
    public static void InverseIct(Span<int> c0, Span<int> c1, Span<int> c2)
    {
        RequireSameLength(c0, c1, c2);
        for (var i = 0; i < c0.Length; i++)
        {
            double y = c0[i];
            double cb = c1[i];
            double cr = c2[i];
            var r = y + (CrToR * cr);
            var g = y + (CbToG * cb) + (CrToG * cr);
            var b = y + (CbToB * cb);
            c0[i] = (int)Math.Round(r, MidpointRounding.AwayFromZero);
            c1[i] = (int)Math.Round(g, MidpointRounding.AwayFromZero);
            c2[i] = (int)Math.Round(b, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// T.800 G.1 applies the component transforms to three components of identical sample grid;
    /// <see cref="JpxImageDecoder"/> refuses a stream whose components 0–2 differ
    /// (<c>PLUME3712</c>) before reaching here, so this is the internal contract check that
    /// keeps a mismatched call from indexing <paramref name="c1"/>/<paramref name="c2"/> past
    /// their ends.
    /// </summary>
    private static void RequireSameLength(Span<int> c0, Span<int> c1, Span<int> c2)
    {
        if (c0.Length != c1.Length || c0.Length != c2.Length)
        {
            throw new ArgumentException($"The multiple component transform needs three equally sized components; got {c0.Length}, {c1.Length} and {c2.Length} samples.", nameof(c1));
        }
    }

    /// <summary>Adds <c>2^(precision−1)</c> (G.1.2) — unsigned components only.</summary>
    public static void LevelShiftUnsigned(Span<int> samples, int precision)
    {
        var half = 1 << (precision - 1);
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] += half;
        }
    }

    /// <summary>
    /// G.1.2 saturation to <paramref name="info"/>'s range, returned as the two's-complement bit
    /// pattern of <c>info.Precision</c> bits (identity for an in-range unsigned value). Every
    /// sample is CLAMPED (<c>opj_int_clamp</c>) — <c>[0, 2^precision − 1]</c> for an unsigned
    /// component after its level shift, <c>[−2^(precision−1), 2^(precision−1) − 1]</c> for a
    /// signed one — never masked: the irreversible transform's quantisation ringing routinely
    /// overshoots the range at sharp edges, and a mask would wrap that overshoot (258 → 2,
    /// −3 → 253; for a signed 8-bit sample 130 → −126) into isolated speckles instead of
    /// saturating it. The clamped signed value's bit pattern, stored in a plane's native width,
    /// is what <see cref="JpxPlane.Sample"/> sign-extends back. This is the one packing path:
    /// <see cref="JpxImageDecoder"/> writes every tile-component through it.
    /// </summary>
    public static int ClampToPrecision(int sample, JpxComponentInfo info)
    {
        var mask = (1 << info.Precision) - 1;
        if (!info.Signed)
        {
            return Math.Clamp(sample, 0, mask);
        }

        var half = 1 << (info.Precision - 1);
        return Math.Clamp(sample, -half, half - 1) & mask;
    }

    /// <summary>Nearest-neighbour upsampling of a sub-sampled component plane to the reference grid: reference-grid point <c>(x,y)</c> maps to component sample <c>⌊(x−x0Offset)/xrsiz⌋, ⌊(y−y0Offset)/yrsiz⌋</c>, clamped to the plane's own bounds.</summary>
    public static JpxPlane UpsampleNearest(JpxPlane plane, int gridWidth, int gridHeight, int xrsiz, int yrsiz, int x0Offset, int y0Offset)
    {
        if (xrsiz == 1 && yrsiz == 1 && plane.Width == gridWidth && plane.Height == gridHeight && x0Offset == 0 && y0Offset == 0)
        {
            return plane;
        }

        if (plane.Width <= 0 || plane.Height <= 0 || xrsiz <= 0 || yrsiz <= 0)
        {
            // There is no nearest sample to pick; JpxCodestream refuses a component whose grid
            // is empty (PLUME3706) so this is unreachable from a parsed stream, but the clamp
            // below must never be handed an inverted [0, −1] range.
            throw new ArgumentException($"Cannot upsample a {plane.Width}x{plane.Height} plane by {xrsiz}x{yrsiz}.", nameof(plane));
        }

        var wide = plane.Precision > 8;
        var out8 = wide ? null : new byte[(long)gridWidth * gridHeight];
        var out16 = wide ? new ushort[(long)gridWidth * gridHeight] : null;

        for (var y = 0; y < gridHeight; y++)
        {
            var sy = Math.Clamp(FloorDiv(y - y0Offset, yrsiz), 0, plane.Height - 1);
            for (var x = 0; x < gridWidth; x++)
            {
                var sx = Math.Clamp(FloorDiv(x - x0Offset, xrsiz), 0, plane.Width - 1);
                var srcIndex = (sy * plane.Width) + sx;
                var dstIndex = (y * gridWidth) + x;
                if (wide)
                {
                    out16![dstIndex] = plane.Samples16![srcIndex];
                }
                else
                {
                    out8![dstIndex] = plane.Samples8![srcIndex];
                }
            }
        }

        return new JpxPlane { Width = gridWidth, Height = gridHeight, Precision = plane.Precision, Signed = plane.Signed, Samples8 = out8, Samples16 = out16 };
    }

    /// <summary>Expands a single index plane through a <c>pclr</c> palette (and optional <c>cmap</c>) into its output colour planes (I.5.3.4, I.5.3.5).</summary>
    /// <remarks>
    /// <para>A <c>cmap</c> box's <c>PCOL</c> is trusted user/producer input naming a palette column, and
    /// nothing at parse time (<see cref="Jp2Boxes"/> parses <c>cmap</c> and <c>pclr</c>
    /// independently, in whichever box order the file happens to use) cross-checks it against the
    /// palette's own column count. A <c>PCOL</c> at or past <paramref name="palette"/>'s column
    /// count is out-of-range input <see cref="ReadPaletteEntry"/> must never index with — caught
    /// here, once, before the per-pixel loop runs: the whole (inconsistent) <c>cmap</c> is
    /// reported (<c>PLUME3715</c>) and ignored in favour of identity column order, per this box
    /// family's documented "pclr/cmap/cdef inconsistent — box ignored" contract.</para>
    /// <para><paramref name="channelMap"/> uses <see cref="Jp2Boxes"/>'s encoding: a non-negative
    /// entry is a palette column (<c>MTYP</c> 1); a negative entry <c>~cmp</c> is a direct-use
    /// channel (<c>MTYP</c> 0) that copies codestream component <c>cmp</c> — here always the index
    /// plane itself, since a palette applies to a single-component codestream (I.5.3.4); any other
    /// component is the same inconsistency as an out-of-range column.</para>
    /// <para>The output planes' size is known from the header alone —
    /// <see cref="PaletteExpansionBytes"/> — and <see cref="JpxImageDecoder"/> charges it to the
    /// decode budget before the first tile is touched: a <c>cmap</c> naming hundreds of channels
    /// over a large index plane is the amplification <see cref="JpxDecodeBudget"/> exists to
    /// refuse, and refusing it after decoding the whole index plane would waste the decode.</para>
    /// </remarks>
    public static JpxPlane[] ExpandPalette(JpxPlane index, (int Depth, int Entries, byte[] Table) palette, int[]? channelMap, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var bytesPerChannel = palette.Depth <= 8 ? 1 : 2;
        var channelCount = PaletteChannelCount(palette);
        var order = ResolveChannelOrder(palette, channelMap, out var channelMapInconsistent);
        if (channelMapInconsistent)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"cmap box names a palette column outside the pclr box's own {channelCount} column(s) or a direct-use component other than 0; ignoring cmap and using identity column order.", options, diagnostics, null);
        }

        var pixelCount = index.Width * index.Height;
        var outputs = new JpxPlane[order.Length];

        for (var c = 0; c < order.Length; c++)
        {
            var column = order[c];
            var wide = palette.Depth > 8;
            var out8 = wide ? null : new byte[pixelCount];
            var out16 = wide ? new ushort[pixelCount] : null;
            var indexMax = index.MaxValue;
            for (var i = 0; i < pixelCount; i++)
            {
                var entry = (int)ReadIndex(index, i);
                long value;
                if (column < 0)
                {
                    // Direct use of the index component, rescaled to the palette's depth so every
                    // output plane shares one precision (the JpxPlane contract: one depth per image).
                    value = JpxImage.Rescale(entry, indexMax, wide ? 0xFFFF : 0xFF);
                }
                else
                {
                    if (entry < 0 || entry >= palette.Entries)
                    {
                        entry = 0;
                    }

                    value = ReadPaletteEntry(palette.Table, entry, column, channelCount, bytesPerChannel);
                }

                if (wide)
                {
                    out16![i] = (ushort)value;
                }
                else
                {
                    out8![i] = (byte)value;
                }
            }

            outputs[c] = new JpxPlane { Width = index.Width, Height = index.Height, Precision = palette.Depth, Signed = false, Samples8 = out8, Samples16 = out16 };
        }

        return outputs;
    }

    /// <summary>Bytes <see cref="ExpandPalette"/> will allocate for <paramref name="pixelCount"/> index samples: <see cref="PaletteOutputPlanes"/> planes at the palette's byte width.</summary>
    public static long PaletteExpansionBytes((int Depth, int Entries, byte[] Table) palette, int[]? channelMap, long pixelCount) =>
        PaletteOutputPlanes(palette, channelMap) * pixelCount * (palette.Depth <= 8 ? 1 : 2);

    /// <summary>
    /// How many planes <see cref="ExpandPalette"/> produces — computed by the same
    /// <see cref="ResolveChannelOrder"/> decision the expansion itself makes, so the charge and
    /// the allocation cannot disagree: one per <paramref name="channelMap"/> entry when a
    /// consistent <c>cmap</c> was given, else one per palette column. The first version of this
    /// charged <c>cmap.Length</c> while the expansion fell back to every palette column when the
    /// <c>cmap</c> was inconsistent — two planes charged, 255 allocated: 17 GB from a 957-byte
    /// file.
    /// </summary>
    public static int PaletteOutputPlanes((int Depth, int Entries, byte[] Table) palette, int[]? channelMap) =>
        ResolveChannelOrder(palette, channelMap, out _).Length;

    /// <summary>
    /// The output-channel order <see cref="ExpandPalette"/> will use: <paramref name="channelMap"/>
    /// itself when every entry names one of the palette's own columns (or direct-use component 0),
    /// else — <paramref name="inconsistent"/> set, for the caller to report as <c>PLUME3715</c> — the
    /// identity order over all of the palette's columns (this box family's documented
    /// "pclr/cmap/cdef inconsistent — box ignored" contract; a channel-order fallback under a loud
    /// deviation, not a mis-decode of any sample, so the refuse-rather-than-mis-decode rule is not engaged).
    /// </summary>
    private static int[] ResolveChannelOrder((int Depth, int Entries, byte[] Table) palette, int[]? channelMap, out bool inconsistent)
    {
        var channelCount = PaletteChannelCount(palette);
        inconsistent = channelMap is not null && Array.Exists(channelMap, entry => entry >= 0 ? entry >= channelCount : ~entry != 0);
        return channelMap is null || inconsistent ? CreateIdentity(channelCount) : channelMap;
    }

    private static int PaletteChannelCount((int Depth, int Entries, byte[] Table) palette) =>
        palette.Entries == 0 ? 0 : palette.Table.Length / palette.Entries / (palette.Depth <= 8 ? 1 : 2);

    /// <summary>sYCC → RGB in place (EnumCS 18): re-centres Cb/Cr around the half-range point (the level shift already applied) then applies the fixed BT.601 matrix. The caller skips this when the MCT already produced RGB.</summary>
    public static void SyccToRgb(JpxPlane y, JpxPlane cb, JpxPlane cr)
    {
        var half = 1 << (y.Precision - 1);
        var max = y.MaxValue;
        var pixelCount = y.Width * y.Height;
        for (var i = 0; i < pixelCount; i++)
        {
            double yy = y.Sample(i);
            double cbc = cb.Sample(i) - half;
            double crc = cr.Sample(i) - half;
            var r = yy + (CrToR * crc);
            var g = yy + (CbToG * cbc) + (CrToG * crc);
            var b = yy + (CbToB * cbc);
            WriteClamped(y, i, r, max);
            WriteClamped(cb, i, g, max);
            WriteClamped(cr, i, b, max);
        }
    }

    private static void WriteClamped(JpxPlane plane, int index, double value, int max)
    {
        var v = Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, max);
        if (plane.Samples16 is not null)
        {
            plane.Samples16[index] = (ushort)v;
        }
        else
        {
            plane.Samples8![index] = (byte)v;
        }
    }

    private static long ReadIndex(JpxPlane plane, int i) => plane.Samples16 is not null ? plane.Samples16[i] : plane.Samples8![i];

    private static long ReadPaletteEntry(byte[] table, int entry, int column, int channelCount, int bytesPerChannel)
    {
        var offset = ((entry * channelCount) + column) * bytesPerChannel;
        if (bytesPerChannel == 1)
        {
            return table[offset];
        }

        return (table[offset] << 8) | table[offset + 1];
    }

    private static int[] CreateIdentity(int count)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = i;
        }

        return result;
    }

    /// <summary>Floor division for a non-negative divisor, correct for a negative dividend (unlike C#'s truncating <c>/</c>).</summary>
    private static int FloorDiv(int a, int b) => (a >= 0) ? a / b : -(((-a) + b - 1) / b);
}
