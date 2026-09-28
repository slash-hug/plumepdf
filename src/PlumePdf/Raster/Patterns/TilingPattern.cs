using PlumePdf.Raster.Functions;
using PlumePdf.Raster.Transparency;

namespace PlumePdf.Raster.Patterns;

/// <summary>Whether a tiling pattern's cell content stream carries its own color (colored, <c>/PaintType 1</c>) or paints only shapes/glyphs whose color the fill operator supplies each time (uncolored, <c>/PaintType 2</c>), ISO 32000-1 §8.7.3.3.</summary>
internal enum PatternPaintType
{
    /// <summary><c>/PaintType 1</c> — the pattern cell's content stream sets its own colors.</summary>
    Colored = 1,

    /// <summary><c>/PaintType 2</c> — the pattern cell paints only marks; the fill operator's own <c>/Pattern</c> underlying-space color operand supplies the color for every mark.</summary>
    Uncolored = 2,
}

/// <summary>
/// A parsed <c>/PatternType 1</c> (tiling) pattern dictionary (ISO 32000-1 §8.7.3.1 Table 75).
/// Rendering the cell's own content stream into pixels is the <c>RasterInterpreter</c>'s
/// job (it alone can execute arbitrary content-stream operators); this type owns the other
/// half — parsing the tiling geometry and, given an
/// already-rendered single-cell buffer, repeating and compositing it across a fill region
/// (<see cref="TilingPattern.CompositeAxisAligned"/>).
/// </summary>
internal sealed record TilingPatternDefinition(
    double BBoxLlx, double BBoxLly, double BBoxUrx, double BBoxUry,
    double XStep, double YStep,
    double MatrixA, double MatrixB, double MatrixC, double MatrixD, double MatrixE, double MatrixF,
    PatternPaintType PaintType,
    int TilingType)
{
    /// <summary>Parses a tiling pattern's dictionary (a plain <see cref="PdfDictionary"/>, or a <see cref="PdfStream"/>'s dictionary — the stream body is the cell's content, read separately by the interpreter).</summary>
    /// <exception cref="PlumePdfException"><c>PLUME7726</c> — missing/malformed required entries, or a non-positive <c>/XStep</c>/<c>/YStep</c> (ISO 32000-1 §8.7.3.1 requires both nonzero; PlumePDF additionally requires positive, matching every real-world producer).</exception>
    internal static TilingPatternDefinition Parse(PdfDictionary dict, Func<IndirectReference, PdfObject> resolve)
    {
        var bbox = FunctionEvaluator.TryNumberArray(dict, "BBox", resolve);
        if (bbox is not { Length: 4 })
        {
            throw new PlumePdfException("PLUME7726", $"Tiling pattern needs a 4-element /BBox, found {(bbox?.Length.ToString() ?? "none")}.");
        }

        var xStep = ReadNumber(dict, "XStep", resolve);
        var yStep = ReadNumber(dict, "YStep", resolve);
        if (xStep is not > 0 || yStep is not > 0)
        {
            throw new PlumePdfException("PLUME7726", $"Tiling pattern's /XStep ({xStep}) and /YStep ({yStep}) must both be positive.");
        }

        var matrix = FunctionEvaluator.TryNumberArray(dict, "Matrix", resolve) ?? [1, 0, 0, 1, 0, 0];
        if (matrix.Length != 6)
        {
            throw new PlumePdfException("PLUME7726", $"Tiling pattern's /Matrix has {matrix.Length} entries; expected 6.");
        }

        var paintType = (int)ReadNumber(dict, "PaintType", resolve) == 2 ? PatternPaintType.Uncolored : PatternPaintType.Colored;
        var tilingType = dict.TryGetValue(PdfName.Get("TilingType"), out var ttValue) && FunctionEvaluator.Resolve(ttValue, resolve) is PdfNumber ttNumber ? ttNumber.ToInt32() : 1;

        return new TilingPatternDefinition(bbox[0], bbox[1], bbox[2], bbox[3], xStep, yStep, matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5], paintType, tilingType);
    }

    private static double ReadNumber(PdfDictionary dict, string key, Func<IndirectReference, PdfObject> resolve) =>
        dict.TryGetValue(PdfName.Get(key), out var value) && FunctionEvaluator.Resolve(value, resolve) is PdfNumber number ? number.Value : 0;

    /// <summary>
    /// Whether <see cref="MatrixB"/>/<see cref="MatrixC"/> (the matrix's rotation/skew terms)
    /// are zero — the fast axis-aligned tiling path (<see cref="TilingPattern.CompositeAxisAligned"/>)
    /// only handles a pure scale+translate pattern-to-device matrix. A rotated/skewed pattern
    /// matrix (rare in real-world content — every tiling pattern PlumePDF's corpus fixtures
    /// exercise is axis-aligned) needs the interpreter to re-invoke the cell's content stream
    /// per tile instance instead of blitting a pre-rendered raster; that path is the
    /// <c>RasterInterpreter</c>'s own integration, not this file.
    /// </summary>
    internal bool IsAxisAligned => MatrixB == 0 && MatrixC == 0;
}

/// <summary>A rendered pattern cell: BGRA pixels, top-down, no row padding — the buffer the interpreter produces by rendering <see cref="TilingPatternDefinition.BBoxLlx"/>..<see cref="TilingPatternDefinition.BBoxUry"/>'s content stream once, at whatever device-space scale the caller chose.</summary>
internal readonly record struct TilingCellBuffer(ReadOnlyMemory<byte> Bgra, int Width, int Height)
{
    /// <summary>Validates that <see cref="Bgra"/>'s length matches <see cref="Width"/> × <see cref="Height"/> × 4.</summary>
    public bool IsValid => Bgra.Length == (long)Width * Height * 4;
}

/// <summary>
/// Repeats a pre-rendered <see cref="TilingCellBuffer"/> across a device-space region and
/// composites each instance onto a destination BGRA surface — the fast path for an
/// axis-aligned tiling pattern. Compositing goes through
/// <see cref="BlendModes.Composite"/> in <see cref="BlendMode.Normal"/> mode (plain
/// "source over"), so cell transparency (an uncolored pattern's unpainted background, or a
/// colored cell with its own <c>ca</c>) composites correctly against whatever is already in
/// the destination.
/// </summary>
internal static class TilingPattern
{
    // Defensive upper bound on tile instances enumerated for one fill — no PdfOptions cap
    // exists for pattern tile counts yet, so this local constant stands in, following the
    // "validate a document/output-controlled dimension before allocating" discipline (a fill
    // region's area divided by a tiny XStep/YStep is exactly such a dimension) until a
    // dedicated PdfOptions knob is added.
    internal const int DefaultMaxTileInstances = 1_000_000;

    /// <summary>
    /// Tiles <paramref name="cell"/> across <paramref name="destination"/>, clipped to
    /// <paramref name="clipX0"/>..<paramref name="clipY1"/> (already intersected with both the
    /// destination bounds and the fill path's own device-space bounding box by the caller —
    /// this method has no path/clip knowledge of its own; the caller masks by the actual fill
    /// shape separately through the normal scan-converter clip machinery).
    /// </summary>
    /// <param name="destination">The destination BGRA surface, top-down, <paramref name="destStrideBytes"/> bytes per row.</param>
    /// <param name="destWidth">The destination surface's width in pixels.</param>
    /// <param name="destHeight">The destination surface's height in pixels.</param>
    /// <param name="destStrideBytes">The destination surface's row stride in bytes.</param>
    /// <param name="cell">The pre-rendered pattern cell to repeat.</param>
    /// <param name="originX">Device-space X of pattern-space (0, 0), mapped through the pattern-to-device matrix.</param>
    /// <param name="originY">Device-space Y of pattern-space (0, 0), mapped through the pattern-to-device matrix.</param>
    /// <param name="stepXPixels">Device-space pixel width of one <c>/XStep</c> repeat (requires a pure scale+translate pattern matrix — see <see cref="TilingPatternDefinition.IsAxisAligned"/>).</param>
    /// <param name="stepYPixels">Device-space pixel height of one <c>/YStep</c> repeat.</param>
    /// <param name="clipX0">The left edge (inclusive) of the device-space region to fill.</param>
    /// <param name="clipY0">The top edge (inclusive) of the device-space region to fill.</param>
    /// <param name="clipX1">The right edge (exclusive) of the device-space region to fill.</param>
    /// <param name="clipY1">The bottom edge (exclusive) of the device-space region to fill.</param>
    /// <param name="maxTileInstances">The instance-count cap, enforced before iterating — pass <see cref="DefaultMaxTileInstances"/> absent a caller-specific policy.</param>
    /// <param name="blendMode">The active <c>/BM</c> blend mode to composite each tile instance with. Defaults to <see cref="BlendMode.Normal"/>.</param>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME7727</c> — <paramref name="stepXPixels"/> or <paramref name="stepYPixels"/> is not positive;
    /// <c>PLUME7725</c> — the fill region would require more than <paramref name="maxTileInstances"/> tile instances.
    /// </exception>
    internal static void CompositeAxisAligned(
        Span<byte> destination, int destWidth, int destHeight, int destStrideBytes,
        TilingCellBuffer cell,
        double originX, double originY,
        double stepXPixels, double stepYPixels,
        int clipX0, int clipY0, int clipX1, int clipY1,
        int maxTileInstances = DefaultMaxTileInstances,
        BlendMode blendMode = BlendMode.Normal)
    {
        if (stepXPixels <= 0 || stepYPixels <= 0)
        {
            throw new PlumePdfException("PLUME7727", $"Tiling pattern's device-space step ({stepXPixels}x{stepYPixels} px) is not positive; cannot tile.");
        }

        if (!cell.IsValid || cell.Width <= 0 || cell.Height <= 0)
        {
            return;
        }

        clipX0 = Math.Max(clipX0, 0);
        clipY0 = Math.Max(clipY0, 0);
        clipX1 = Math.Min(clipX1, destWidth);
        clipY1 = Math.Min(clipY1, destHeight);
        if (clipX0 >= clipX1 || clipY0 >= clipY1)
        {
            return;
        }

        var iMin = (int)Math.Floor((clipX0 - originX - cell.Width) / stepXPixels);
        var iMax = (int)Math.Ceiling((clipX1 - originX) / stepXPixels);
        var jMin = (int)Math.Floor((clipY0 - originY - cell.Height) / stepYPixels);
        var jMax = (int)Math.Ceiling((clipY1 - originY) / stepYPixels);

        var instanceCount = (long)(iMax - iMin + 1) * (jMax - jMin + 1);
        if (instanceCount > maxTileInstances)
        {
            throw new PlumePdfException("PLUME7725", $"Tiling pattern would require {instanceCount} tile instances to cover the fill region, exceeding the {maxTileInstances} cap; refusing to composite.");
        }

        var cellSpan = cell.Bgra.Span;
        for (var j = jMin; j <= jMax; j++)
        {
            var tileTop = (int)Math.Round(originY + (j * stepYPixels));
            for (var i = iMin; i <= iMax; i++)
            {
                var tileLeft = (int)Math.Round(originX + (i * stepXPixels));
                BlitCell(destination, destWidth, destHeight, destStrideBytes, cellSpan, cell.Width, cell.Height, tileLeft, tileTop, clipX0, clipY0, clipX1, clipY1, blendMode);
            }
        }
    }

    private static void BlitCell(Span<byte> dest, int destWidth, int destHeight, int destStride, ReadOnlySpan<byte> cell, int cellWidth, int cellHeight, int left, int top, int clipX0, int clipY0, int clipX1, int clipY1, BlendMode blendMode)
    {
        var srcX0 = Math.Max(Math.Max(0, clipX0 - left), -left);
        var srcY0 = Math.Max(Math.Max(0, clipY0 - top), -top);
        var srcX1 = Math.Min(cellWidth, Math.Min(clipX1 - left, destWidth - left));
        var srcY1 = Math.Min(cellHeight, Math.Min(clipY1 - top, destHeight - top));
        if (srcX0 >= srcX1 || srcY0 >= srcY1)
        {
            return;
        }

        for (var y = srcY0; y < srcY1; y++)
        {
            var destY = top + y;
            var srcRowOffset = ((y * cellWidth) + srcX0) * 4;
            var destRowOffset = (destY * destStride) + ((left + srcX0) * 4);
            for (var x = srcX0; x < srcX1; x++)
            {
                // Buffers are BGRA byte order; BlendModes.Composite's tuple positions are
                // (R, G, B, A) — the non-separable modes weight channels asymmetrically
                // (Lum = 0.30R + 0.59G + 0.11B), so getting this reorder backwards would
                // silently swap the red/blue weights instead of merely swapping which byte
                // holds which value.
                var backdrop = (dest[destRowOffset + 2], dest[destRowOffset + 1], dest[destRowOffset], dest[destRowOffset + 3]);
                var source = (cell[srcRowOffset + 2], cell[srcRowOffset + 1], cell[srcRowOffset], cell[srcRowOffset + 3]);
                var result = BlendModes.Composite(blendMode, backdrop, source);
                dest[destRowOffset] = result.B;
                dest[destRowOffset + 1] = result.G;
                dest[destRowOffset + 2] = result.R;
                dest[destRowOffset + 3] = result.A;

                srcRowOffset += 4;
                destRowOffset += 4;
            }
        }
    }
}
