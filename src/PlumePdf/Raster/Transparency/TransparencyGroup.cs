namespace PlumePdf.Raster.Transparency;

/// <summary>
/// Composites an already-rendered isolated transparency group (ISO 32000-1 §11.4.5/§11.6.4)
/// onto a destination surface, applying constant alpha (<c>ca</c>/<c>CA</c>), an optional soft
/// mask coverage map, and a blend mode. "Isolated" describes how the
/// <c>RasterInterpreter</c> rendered the group in the first place — starting from a fully
/// transparent buffer rather than a copy of the destination, so the group's own content never
/// blends against anything outside itself while it is being painted (§11.4.5's definition).
/// This type is the other half: taking that finished, self-contained buffer and painting it
/// into the destination the normal way. <see cref="CompositeNonIsolated"/> and
/// <see cref="KnockoutLayer"/> extend this to the two remaining group flavors —
/// non-isolated groups (§11.4.7) and knockout groups (§11.4.6).
/// </summary>
internal static class TransparencyGroup
{
    /// <summary>
    /// Composites a rendered <b>non-isolated</b> group (ISO 32000-1 §11.4.7) onto
    /// <paramref name="destination"/>: first removes <paramref name="capturedBackdrop"/> — the
    /// same destination-region copy the group started rendering from (<see cref="Backdrop.Capture"/>)
    /// — from <paramref name="groupBgra"/> in place (<see cref="Backdrop.RemoveBackdrop"/>), then
    /// composites the now backdrop-free result exactly as <see cref="Composite"/> would for an
    /// isolated group. <paramref name="capturedBackdrop"/> must be the exact same
    /// <paramref name="width"/>×<paramref name="height"/> region <see cref="Backdrop.Capture"/>
    /// produced for this group.
    /// </summary>
    internal static void CompositeNonIsolated(
        Span<byte> destination, int destWidth, int destHeight, int destStrideBytes,
        Span<byte> groupBgra, ReadOnlySpan<byte> capturedBackdrop, int width, int height,
        int destOriginX, int destOriginY,
        double constantAlpha, BlendMode blendMode,
        ReadOnlySpan<byte> softMaskCoverage = default)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Backdrop.RemoveBackdrop(groupBgra, capturedBackdrop, width * height);
        Composite(destination, destWidth, destHeight, destStrideBytes, groupBgra, width, height, destOriginX, destOriginY, constantAlpha, blendMode, softMaskCoverage);
    }

    /// <summary>
    /// Folds one more element of a <b>knockout</b> group (<c>/K true</c>, ISO 32000-1 §11.4.6)
    /// into the group's running result buffer <paramref name="groupResult"/>, in place. A
    /// knockout group's defining trait: each element's contribution <b>replaces</b> — rather
    /// than compositing/accumulating with — whatever earlier same-group elements already left at
    /// that pixel, so overlapping semi-transparent siblings never stack into higher combined
    /// opacity or show through each other the way they would in an ordinary (non-knockout)
    /// group. Concretely, per pixel where <paramref name="elementBgra"/> has non-zero alpha, the
    /// running result is overwritten with the element composited (via
    /// <paramref name="elementBlendMode"/>) against the group's fixed
    /// <paramref name="initialBackdrop"/> — never against <paramref name="groupResult"/> itself.
    /// Pixels where the element painted nothing are left untouched, preserving whatever earlier
    /// elements in the same group already contributed there.
    /// </summary>
    /// <param name="groupResult">The knockout group's running result, initialized to a copy of <paramref name="initialBackdrop"/> before the first element folds in.</param>
    /// <param name="initialBackdrop">The group's fixed starting point — fully transparent for an isolated knockout group, or <see cref="Backdrop.Capture"/>'s result for a non-isolated one. Every element composites against this same buffer, never against <paramref name="groupResult"/>.</param>
    /// <param name="elementBgra">One group element's own rendered BGRA buffer, the same dimensions as the other two.</param>
    /// <param name="elementBlendMode">The element's own <c>/BM</c> blend mode.</param>
    /// <param name="pixelCount">The pixel count all three buffers share (<c>width × height</c>).</param>
    internal static void KnockoutLayer(Span<byte> groupResult, ReadOnlySpan<byte> initialBackdrop, ReadOnlySpan<byte> elementBgra, BlendMode elementBlendMode, int pixelCount)
    {
        for (var i = 0; i < pixelCount; i++)
        {
            var o = i * 4;
            if (elementBgra[o + 3] == 0)
            {
                continue; // This element painted nothing here — leave the running result untouched.
            }

            var backdropPixel = (initialBackdrop[o + 2], initialBackdrop[o + 1], initialBackdrop[o], initialBackdrop[o + 3]);
            var elementPixel = (elementBgra[o + 2], elementBgra[o + 1], elementBgra[o], elementBgra[o + 3]);
            var replaced = BlendModes.Composite(elementBlendMode, backdropPixel, elementPixel);

            groupResult[o] = replaced.B;
            groupResult[o + 1] = replaced.G;
            groupResult[o + 2] = replaced.R;
            groupResult[o + 3] = replaced.A;
        }
    }

    /// <summary>
    /// Composites <paramref name="groupBgra"/> onto <paramref name="destination"/>, pixel by
    /// pixel: each source pixel's effective alpha is its own alpha times
    /// <paramref name="constantAlpha"/> times the matching <paramref name="softMaskCoverage"/>
    /// entry (when supplied), then combined with the destination pixel through
    /// <see cref="BlendModes.Composite"/>.
    /// </summary>
    /// <param name="destination">The destination BGRA surface, top-down, <paramref name="destStrideBytes"/> bytes per row.</param>
    /// <param name="destWidth">The destination surface's width in pixels.</param>
    /// <param name="destHeight">The destination surface's height in pixels.</param>
    /// <param name="destStrideBytes">The destination surface's row stride in bytes.</param>
    /// <param name="groupBgra">The rendered group's BGRA pixels, top-down, unpadded (exactly <paramref name="width"/> × <paramref name="height"/> × 4 bytes).</param>
    /// <param name="width">The rendered group's width in pixels.</param>
    /// <param name="height">The rendered group's height in pixels.</param>
    /// <param name="destOriginX">Device-space X (in <paramref name="destination"/> pixels) where <paramref name="groupBgra"/>'s pixel (0, 0) lands.</param>
    /// <param name="destOriginY">Device-space Y where <paramref name="groupBgra"/>'s pixel (0, 0) lands.</param>
    /// <param name="constantAlpha">The group's <c>ca</c> (non-stroking) or <c>CA</c> (stroking) constant alpha, 0.0–1.0 (ISO 32000-1 §11.6.4.3). Pass 1.0 when the ExtGState set none.</param>
    /// <param name="blendMode">The active <c>/BM</c> blend mode.</param>
    /// <param name="softMaskCoverage">An optional coverage map from <see cref="SoftMask.ComputeCoverage(ReadOnlySpan{byte}, int, int, int, int, int)"/>, exactly <paramref name="width"/> × <paramref name="height"/> bytes, row-major, aligned 1:1 with <paramref name="groupBgra"/>. <see langword="null"/> when no soft mask is active.</param>
    internal static void Composite(
        Span<byte> destination, int destWidth, int destHeight, int destStrideBytes,
        ReadOnlySpan<byte> groupBgra, int width, int height,
        int destOriginX, int destOriginY,
        double constantAlpha, BlendMode blendMode,
        ReadOnlySpan<byte> softMaskCoverage = default)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var clampedAlpha = Math.Clamp(constantAlpha, 0.0, 1.0);
        var hasSoftMask = !softMaskCoverage.IsEmpty;
        if (hasSoftMask && softMaskCoverage.Length != width * height)
        {
            throw new ArgumentException($"Soft mask coverage has {softMaskCoverage.Length} entries; expected {width * height} ({width}x{height}).", nameof(softMaskCoverage));
        }

        var srcX0 = Math.Max(0, -destOriginX);
        var srcY0 = Math.Max(0, -destOriginY);
        var srcX1 = Math.Min(width, destWidth - destOriginX);
        var srcY1 = Math.Min(height, destHeight - destOriginY);
        if (srcX0 >= srcX1 || srcY0 >= srcY1)
        {
            return;
        }

        for (var y = srcY0; y < srcY1; y++)
        {
            var destY = destOriginY + y;
            var srcRowOffset = (y * width) + srcX0;
            var srcByteOffset = srcRowOffset * 4;
            var destByteOffset = (destY * destStrideBytes) + ((destOriginX + srcX0) * 4);

            for (var x = srcX0; x < srcX1; x++)
            {
                var srcAlpha = groupBgra[srcByteOffset + 3] / 255.0;
                var maskAlpha = hasSoftMask ? softMaskCoverage[srcRowOffset] / 255.0 : 1.0;
                var effectiveAlpha = srcAlpha * clampedAlpha * maskAlpha;

                if (effectiveAlpha > 0)
                {
                    var backdrop = (destination[destByteOffset + 2], destination[destByteOffset + 1], destination[destByteOffset], destination[destByteOffset + 3]);
                    var source = (
                        groupBgra[srcByteOffset + 2],
                        groupBgra[srcByteOffset + 1],
                        groupBgra[srcByteOffset],
                        (byte)Math.Clamp((int)((effectiveAlpha * 255.0) + 0.5), 0, 255));

                    var result = BlendModes.Composite(blendMode, backdrop, source);
                    destination[destByteOffset] = result.B;
                    destination[destByteOffset + 1] = result.G;
                    destination[destByteOffset + 2] = result.R;
                    destination[destByteOffset + 3] = result.A;
                }

                srcRowOffset++;
                srcByteOffset += 4;
                destByteOffset += 4;
            }
        }
    }
}
