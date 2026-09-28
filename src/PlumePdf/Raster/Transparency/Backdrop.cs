namespace PlumePdf.Raster.Transparency;

/// <summary>
/// Capture and removal of a non-isolated transparency group's initial backdrop (ISO 32000-1
/// §11.4.5/§11.4.7). An isolated group starts rendering from a fully transparent
/// buffer (<c>RasterInterpreter</c>'s job, unaffected by this file); a <b>non-isolated</b>
/// group instead starts from a copy of whatever is already on the destination beneath it, so the
/// group's own content can blend against real page content while it renders. That copy —
/// <see cref="Capture"/>'s job — has to be subtracted back out of the group's finished result
/// before <see cref="TransparencyGroup.CompositeNonIsolated"/> composites it onto the
/// destination for real, or the backdrop would be painted twice (once as the group's own
/// starting point, once again when the "over" compositor lays the group on the destination).
/// <see cref="RemoveBackdrop"/> is that subtraction — the standard Porter-Duff "over" operator
/// inverted algebraically, general compositing math (Porter &amp; Duff 1984), not proprietary to
/// any PDF implementation, so no NOTICE attribution applies (per the clean-room policy in AGENTS.md).
/// </summary>
internal static class Backdrop
{
    /// <summary>
    /// Copies a <paramref name="width"/>×<paramref name="height"/> region of
    /// <paramref name="destination"/>, starting at (<paramref name="originX"/>,
    /// <paramref name="originY"/>), into a new BGRA buffer — the non-isolated group's initial
    /// backdrop. Pixels outside the destination's bounds (a group whose device-space extent runs
    /// past the page edge) capture as fully transparent black, matching what compositing against
    /// "nothing there" should mean.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME7739</c> — the requested <paramref name="width"/>×<paramref name="height"/> buffer would exceed <see cref="RasterSurface.DefaultMaxSurfaceBytes"/>; refused before allocating (a document/DPI-derived dimension, the same class of risk <see cref="RasterSurface.Create"/> itself already guards).</exception>
    internal static byte[] Capture(ReadOnlySpan<byte> destination, int destWidth, int destHeight, int destStrideBytes, int originX, int originY, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        if ((long)width * height * 4 > RasterSurface.DefaultMaxSurfaceBytes)
        {
            throw new PlumePdfException("PLUME7739", $"A non-isolated transparency group's backdrop capture ({width}x{height}) would exceed the raster surface byte cap; refusing to allocate it.");
        }

        var captured = new byte[width * height * 4];

        var srcX0 = Math.Max(0, -originX);
        var srcY0 = Math.Max(0, -originY);
        var srcX1 = Math.Min(width, destWidth - originX);
        var srcY1 = Math.Min(height, destHeight - originY);
        if (srcX0 >= srcX1 || srcY0 >= srcY1)
        {
            return captured; // Entirely off-surface — transparent black throughout, as allocated.
        }

        for (var y = srcY0; y < srcY1; y++)
        {
            var destRowStart = ((originY + y) * destStrideBytes) + ((originX + srcX0) * 4);
            var capturedRowStart = ((y * width) + srcX0) * 4;
            var rowBytes = (srcX1 - srcX0) * 4;
            destination.Slice(destRowStart, rowBytes).CopyTo(captured.AsSpan(capturedRowStart, rowBytes));
        }

        return captured;
    }

    /// <summary>
    /// Un-blends a non-isolated group's rendered result in place: given the group buffer
    /// <paramref name="groupBgra"/> (the group's content already composited "over"
    /// <paramref name="backdropBgra"/>, exactly as rendering it against that starting backdrop
    /// produced) and the same <paramref name="backdropBgra"/> <see cref="Capture"/> returned,
    /// recovers the group's own content <em>as if it had been rendered in isolation</em> — so the
    /// caller can then composite that recovered content onto the real destination without the
    /// backdrop being counted twice.
    /// </summary>
    /// <remarks>
    /// Inverting the non-premultiplied "over" operator (<c>αr = αs + α0(1-αs)</c>, <c>Cr·αr =
    /// Cs·αs + C0·α0(1-αs)</c>) for <c>(Cs, αs)</c> gives <c>αs = (αr-α0)/(1-α0)</c> and
    /// <c>Cs = (Cr·αr - C0·α0·(1-αs)) / αs</c>. When the backdrop pixel was fully opaque
    /// (<c>α0 = 1</c>), <c>αr</c> is <em>always</em> 1 too regardless of <c>αs</c> — the group's
    /// own alpha is genuinely unrecoverable from the result alone (a real, documented limit of
    /// backdrop removal, not a bug here); this treats that pixel as fully opaque with the
    /// rendered color left as-is, the same "can't separate further, so don't distort the
    /// visible pixel" choice every implementation of this algorithm has to make.
    /// </remarks>
    internal static void RemoveBackdrop(Span<byte> groupBgra, ReadOnlySpan<byte> backdropBgra, int pixelCount)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            double b0 = backdropBgra[o], g0 = backdropBgra[o + 1], r0 = backdropBgra[o + 2];
            var a0 = backdropBgra[o + 3] / 255.0;
            double br = groupBgra[o], gr = groupBgra[o + 1], rr = groupBgra[o + 2];
            var ar = groupBgra[o + 3] / 255.0;

            if (a0 >= 1.0 - (1.0 / 255.0))
            {
                // Opaque backdrop: alpha is unrecoverable (see remarks) — leave the pixel as
                // rendered, fully opaque.
                groupBgra[o + 3] = 255;
                continue;
            }

            var asAlpha = (ar - a0) / (1.0 - a0);
            if (asAlpha <= 0)
            {
                // Fully transparent once the backdrop's own contribution is removed — the color
                // is then irrelevant; zero it for a clean, deterministic result.
                groupBgra[o] = 0;
                groupBgra[o + 1] = 0;
                groupBgra[o + 2] = 0;
                groupBgra[o + 3] = 0;
                continue;
            }

            var clampedAlpha = Math.Min(asAlpha, 1.0);
            var oneMinusAs = 1.0 - clampedAlpha;
            var sb = (br * ar) - (b0 * a0 * oneMinusAs);
            var sg = (gr * ar) - (g0 * a0 * oneMinusAs);
            var sr = (rr * ar) - (r0 * a0 * oneMinusAs);

            groupBgra[o] = ClampByte(sb / clampedAlpha);
            groupBgra[o + 1] = ClampByte(sg / clampedAlpha);
            groupBgra[o + 2] = ClampByte(sr / clampedAlpha);
            groupBgra[o + 3] = ClampByte(clampedAlpha * 255.0);
        }
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);
}
