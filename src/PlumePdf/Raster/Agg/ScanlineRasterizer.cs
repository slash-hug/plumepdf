namespace PlumePdf.Raster.Agg;

/// <summary>The polygon fill rule (ISO 32000-1 §8.5.3) — selects how <see cref="ScanlineRasterizer"/> turns a signed winding total into inside/outside.</summary>
internal enum FillRule
{
    /// <summary><c>f</c>/<c>F</c>/<c>W</c> operators — a point is inside when the accumulated signed winding is nonzero.</summary>
    NonZero,

    /// <summary><c>f*</c>/<c>W*</c> operators — a point is inside when the accumulated winding is odd.</summary>
    EvenOdd,
}

/// <summary>
/// Sweeps an <see cref="OutlineRasterizer"/>'s accumulated <see cref="Cell"/>s into per-pixel
/// antialiased coverage spans — the AGG 2.3 <c>rasterizer_scanline_aa</c> sweep/<c>calculate_alpha</c>
/// step (field-by-field port from PDFium's <c>third_party/agg23/agg_rasterizer_scanline_aa.h</c>,
/// permitted under the clean-room policy in AGENTS.md). Two things happen per scanline row, left to right: (1) each cell's own
/// <c>Cover</c>/<c>Area</c> pair gives that one pixel's exact partial coverage; (2) the running
/// sum of <c>Cover</c> values ("winding total so far") gives every pixel in a gap between cells
/// a single constant coverage — the fill rule (<see cref="FillRule"/>) turns that signed winding
/// total into a 0-255 alpha, which <c>Sweep</c>'s <c>antiAlias</c> flag then either keeps as-is or
/// collapses to 0/255 at the aa_mask midpoint (<see cref="PlumePdf.PdfRasterizeOptions.AntiAlias"/>)
/// — PDFium's own <c>calculate_alpha(area, no_smooth)</c> shape. Entirely integer
/// arithmetic (shifts, add, and one division-free clamp) — deterministic same-platform, no libm.
/// </summary>
internal static class ScanlineRasterizer
{
    /// <summary>Invoked once per contiguous, constant-coverage horizontal run of pixels on row <paramref name="y"/>: <paramref name="xStart"/>..<paramref name="xStart"/>+<paramref name="length"/>-1, each at <paramref name="coverage"/> (1-255; zero-coverage runs are never reported).</summary>
    public delegate void SpanAction(int y, int xStart, int length, byte coverage);

    /// <summary>
    /// Sweeps every accumulated cell in <paramref name="outline"/>, clipped to
    /// <c>[clipMinX, clipMaxX) × [clipMinY, clipMaxY)</c>, invoking <paramref name="onSpan"/>
    /// for each nonzero-coverage run. Cells are sorted once by (Y, X) — an <see cref="System.Array"/>
    /// sort, not a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/> iteration
    /// order, and every grouping step below sums integers, whose result never depends on
    /// summation order — so two sweeps of the same outline always produce byte-identical spans
    /// (no Dictionary-iteration-order dependence in the deterministic path).
    /// <paramref name="antiAlias"/> <see langword="false"/> thresholds every span's coverage to
    /// 0 or 255 instead of leaving the exact fractional value; cell accumulation
    /// above is identical either way — only the alpha each span reports changes.
    /// </summary>
    public static void Sweep(OutlineRasterizer outline, FillRule fillRule, int clipMinX, int clipMinY, int clipMaxX, int clipMaxY, bool antiAlias, SpanAction onSpan)
    {
        var cellSpan = outline.CellSpan;
        var count = cellSpan.Length;
        if (count == 0 || clipMaxX <= clipMinX || clipMaxY <= clipMinY)
        {
            return;
        }

        // The sort used to run a Comparison<Cell> delegate per comparison, per
        // sweep, per glyph — 29% of a text-heavy page's entire render. Sorting a packed primitive
        // key array alongside the cells uses the runtime's specialized unmanaged-key introsort
        // instead, and pooled buffers replace the per-sweep Cell[] allocation. The key preserves
        // (Y, X) ordering exactly: Y in the high 32 bits keeps its signed order; X in the low 32
        // bits is bias-mapped (x XOR int.MinValue) so signed X order survives the unsigned
        // comparison the low half of a long gets. Equal-(Y,X) cells' relative order is irrelevant
        // — the row sweep sums their Cover/Area, and integer addition is order-independent.
        var sorted = System.Buffers.ArrayPool<Cell>.Shared.Rent(count);
        var keys = System.Buffers.ArrayPool<long>.Shared.Rent(count);
        try
        {
            cellSpan.CopyTo(sorted);
            for (var i = 0; i < count; i++)
            {
                keys[i] = ((long)sorted[i].Y << 32) | (uint)(sorted[i].X ^ int.MinValue);
            }

            Array.Sort(keys, sorted, 0, count);

            var rowStart = 0;
            while (rowStart < count)
            {
                var y = sorted[rowStart].Y;
                var rowEnd = rowStart;
                while (rowEnd < count && sorted[rowEnd].Y == y)
                {
                    rowEnd++;
                }

                if (y >= clipMinY && y < clipMaxY)
                {
                    SweepRow(sorted, rowStart, rowEnd, y, fillRule, clipMinX, clipMaxX, antiAlias, onSpan);
                }

                rowStart = rowEnd;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<Cell>.Shared.Return(sorted);
            System.Buffers.ArrayPool<long>.Shared.Return(keys);
        }
    }

    private static void SweepRow(Cell[] sorted, int start, int end, int y, FillRule fillRule, int clipMinX, int clipMaxX, bool antiAlias, SpanAction onSpan)
    {
        var cover = 0;
        var i = start;
        var lastEmittedX = clipMinX;

        while (i < end)
        {
            var x = sorted[i].X;
            var area = 0;
            var cellCover = 0;
            while (i < end && sorted[i].X == x)
            {
                area += sorted[i].Area;
                cellCover += sorted[i].Cover;
                i++;
            }

            // Fill the gap between the previous cell (or row start) and this cell with the
            // constant coverage implied by the running winding total — no local area term
            // applies there (no edge crosses those pixels).
            if (x > lastEmittedX)
            {
                EmitSpan(y, lastEmittedX, x, cover, fillRule, clipMinX, clipMaxX, antiAlias, onSpan);
            }

            // This cell's own pixel: partial coverage from its area term, using the winding
            // total *after* adding this cell's cover (AGG's calculate_alpha convention).
            cover += cellCover;
            var alpha = CalculateAlpha((cover << (FixedMath.SubpixelShift + 1)) - area, fillRule, antiAlias);
            if (alpha > 0 && x >= clipMinX && x < clipMaxX)
            {
                onSpan(y, x, 1, (byte)alpha);
            }

            lastEmittedX = x + 1;
        }

        // Trailing gap to the right of the last cell, out to the clip bound, at the final
        // running winding total (a polygon's rightmost edge always closes the winding back to
        // zero, so a well-formed path emits nothing here — but an unclosed/degenerate path must
        // not paint a runaway span, hence the explicit fill-rule clamp same as everywhere else).
        if (lastEmittedX < clipMaxX)
        {
            EmitSpan(y, lastEmittedX, clipMaxX, cover, fillRule, clipMinX, clipMaxX, antiAlias, onSpan);
        }
    }

    private static void EmitSpan(int y, int from, int to, int cover, FillRule fillRule, int clipMinX, int clipMaxX, bool antiAlias, SpanAction onSpan)
    {
        var alpha = CalculateAlpha(cover << (FixedMath.SubpixelShift + 1), fillRule, antiAlias);
        if (alpha <= 0)
        {
            return;
        }

        var spanStart = Math.Max(from, clipMinX);
        var spanEndExclusive = Math.Min(to, clipMaxX);
        if (spanEndExclusive > spanStart)
        {
            onSpan(y, spanStart, spanEndExclusive - spanStart, (byte)alpha);
        }
    }

    // AGG calculate_alpha: maps a raw (cover*2*SubpixelScale - area) quantity down to a 0-255
    // coverage value, applying the fill rule, then — field-by-field port of PDFium's own
    // calculate_alpha(int area, bool no_smooth) at the pinned chromium/8009 tag
    // (third_party/agg23/agg_rasterizer_scanline_aa.h, permitted under the clean-room policy in
    // AGENTS.md) — collapses that
    // value to 0/255 at the aa_mask midpoint when the caller has asked for aliased coverage
    // (PdfRasterizeOptions.AntiAlias = false). aa_mask is SubpixelScale - 1 (255); PDFium's
    // rule is `cover > (aa_mask >> 1) ? aa_mask : 0`, i.e. strictly-greater-than-127.
    private static int CalculateAlpha(int rawCover, FillRule fillRule, bool antiAlias)
    {
        var cover = rawCover >> (FixedMath.SubpixelShift + 1);
        if (cover < 0)
        {
            cover = -cover;
        }

        if (fillRule == FillRule.EvenOdd)
        {
            cover &= (2 * FixedMath.SubpixelScale) - 1;
            if (cover > FixedMath.SubpixelScale)
            {
                cover = (2 * FixedMath.SubpixelScale) - cover;
            }
        }

        cover = Math.Min(cover, FixedMath.SubpixelScale - 1);
        return antiAlias ? cover : (cover > ((FixedMath.SubpixelScale - 1) >> 1) ? FixedMath.SubpixelScale - 1 : 0);
    }
}
