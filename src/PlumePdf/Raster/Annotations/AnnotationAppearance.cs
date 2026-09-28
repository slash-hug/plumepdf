using PlumePdf.Content;
using PlumePdf.Objects;

namespace PlumePdf.Raster.Annotations;

/// <summary>
/// Maps an annotation's <c>/AP</c> normal-appearance form XObject onto the annotation's own
/// <c>/Rect</c> (ISO 32000-1 §12.5.5's algorithm): the appearance's <c>/BBox</c>, transformed by
/// its own <c>/Matrix</c>, is translated and scaled so its bounding box exactly fills
/// <c>/Rect</c> — the composed matrix is what <see cref="RasterInterpreter.BuildDisplayList"/>
/// uses as the appearance content stream's initial CTM. Also decodes the appearance stream's
/// bytes with the crash-vector caps required before any display-list allocation happens
/// (PLUME7731): the existing <see cref="PdfStream.GetDecodedBytes"/> decompression-bomb cap
/// (<c>PdfOptions.MaxDecompressedStreamBytes</c>) runs first, then the decoded length is checked
/// against <c>PdfOptions.MaxGeneratedAppearanceBytes</c> — the same budget
/// <c>Documents.Forms.Appearances.AppearanceGenerator</c> enforces on a <em>synthesized</em>
/// appearance, applied symmetrically here to one read from the document, since both feed the same
/// interpreter pipeline afterward.
/// </summary>
internal static class AnnotationAppearance
{
    private static readonly PdfName RectName = PdfName.Get("Rect");
    private static readonly PdfName BBoxName = PdfName.Get("BBox");
    private static readonly PdfName MatrixName = PdfName.Get("Matrix");
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");

    /// <summary>One resolved, ready-to-interpret appearance: the composed appearance-space-to-page-space CTM, the appearance's own <c>/Resources</c> (or <see langword="null"/>), and its decoded content-stream bytes.</summary>
    internal readonly record struct Placement(PdfMatrix Ctm, PdfDictionary? Resources, byte[] ContentBytes);

    /// <summary>
    /// Attempts to resolve <paramref name="appearanceStream"/> (already-selected, e.g. via
    /// <c>/AS</c> against an appearance-state sub-dictionary) against <paramref name="annotation"/>'s
    /// <c>/Rect</c>. Returns <see langword="false"/> — with a <c>PLUME7731</c> diagnostic recorded,
    /// never thrown (a malformed single annotation degrades, it does not crash the page) —
    /// when the <c>/Rect</c>/<c>/BBox</c> is unusable, the stream fails to decode, or the decoded
    /// content exceeds the size budget.
    /// </summary>
    public static bool TryResolve(PdfDictionary annotation, PdfStream appearanceStream, ObjectRegistry? objects, PdfOptions options, DiagnosticCollection? diagnostics, out Placement placement)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(appearanceStream);
        ArgumentNullException.ThrowIfNull(options);

        placement = default;

        if (!TryReadRect(annotation, RectName, objects, out var rectMinX, out var rectMinY, out var rectMaxX, out var rectMaxY))
        {
            Report(diagnostics, "an annotation's /Rect is missing or malformed; its appearance cannot be placed.");
            return false;
        }

        if (Resolve(appearanceStream.Dictionary, BBoxName, objects) is not PdfArray { Count: 4 } bboxArray
            || !TryReadRectArray(bboxArray, out var bMinX, out var bMinY, out var bMaxX, out var bMaxY))
        {
            Report(diagnostics, "an annotation's /AP appearance stream has a missing or malformed /BBox.");
            return false;
        }

        var matrix = Resolve(appearanceStream.Dictionary, MatrixName, objects) is PdfArray { Count: 6 } matrixArray
            ? ReadMatrixArray(matrixArray)
            : PdfMatrix.Identity;
        if (!matrix.IsFinite)
        {
            matrix = PdfMatrix.Identity;
        }

        // §12.5.5 step 1: transform the four /BBox corners by /Matrix, take their bounding box.
        var c0 = matrix.Transform(bMinX, bMinY);
        var c1 = matrix.Transform(bMaxX, bMinY);
        var c2 = matrix.Transform(bMaxX, bMaxY);
        var c3 = matrix.Transform(bMinX, bMaxY);
        if (!AllFinite(c0, c1, c2, c3))
        {
            Report(diagnostics, "an annotation's /AP /Matrix produced a non-finite transformed /BBox.");
            return false;
        }

        var tMinX = Math.Min(Math.Min(c0.X, c1.X), Math.Min(c2.X, c3.X));
        var tMaxX = Math.Max(Math.Max(c0.X, c1.X), Math.Max(c2.X, c3.X));
        var tMinY = Math.Min(Math.Min(c0.Y, c1.Y), Math.Min(c2.Y, c3.Y));
        var tMaxY = Math.Max(Math.Max(c0.Y, c1.Y), Math.Max(c2.Y, c3.Y));

        // §12.5.5 step 2: matrix A translates/scales the transformed bbox onto /Rect exactly.
        var transformedWidth = tMaxX - tMinX;
        var transformedHeight = tMaxY - tMinY;
        var scaleX = transformedWidth != 0 ? (rectMaxX - rectMinX) / transformedWidth : 1.0;
        var scaleY = transformedHeight != 0 ? (rectMaxY - rectMinY) / transformedHeight : 1.0;
        var a = new PdfMatrix(scaleX, 0, 0, scaleY, rectMinX - (tMinX * scaleX), rectMinY - (tMinY * scaleY));
        if (!a.IsFinite)
        {
            Report(diagnostics, "an annotation's /AP bbox-to-/Rect mapping produced a non-finite matrix (a degenerate /Rect or /BBox).");
            return false;
        }

        var resources = Resolve(appearanceStream.Dictionary, ResourcesName, objects) as PdfDictionary;

        byte[] decoded;
        try
        {
            decoded = appearanceStream.GetDecodedBytes(options.Filters, options, objects is null ? null : r => objects[r]);
        }
        catch (PlumePdfException)
        {
            Report(diagnostics, "an annotation's /AP appearance stream failed to decode (malformed filter chain or exceeded the decompression cap).");
            return false;
        }

        if (decoded.LongLength > options.MaxGeneratedAppearanceBytes)
        {
            Report(diagnostics, $"an annotation's decoded /AP appearance stream is {decoded.LongLength} bytes, exceeding PdfOptions.MaxGeneratedAppearanceBytes ({options.MaxGeneratedAppearanceBytes}); refusing to render it.");
            return false;
        }

        // Matrix * A (row-vector composition): appearance space -> transformed-bbox-aligned-to-Rect space.
        placement = new Placement(PdfMatrix.Multiply(matrix, a), resources, decoded);
        return true;
    }

    private static bool TryReadRect(PdfDictionary dict, PdfName key, ObjectRegistry? objects, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = maxX = maxY = 0;
        if (Resolve(dict, key, objects) is not PdfArray { Count: 4 } array || !TryReadRectArray(array, out var x1, out var y1, out var x2, out var y2))
        {
            return false;
        }

        minX = Math.Min(x1, x2);
        maxX = Math.Max(x1, x2);
        minY = Math.Min(y1, y2);
        maxY = Math.Max(y1, y2);
        return double.IsFinite(minX) && double.IsFinite(minY) && double.IsFinite(maxX) && double.IsFinite(maxY);
    }

    private static bool TryReadRectArray(PdfArray array, out double x1, out double y1, out double x2, out double y2)
    {
        x1 = y1 = x2 = y2 = 0;
        if (array[0] is not PdfNumber n0 || array[1] is not PdfNumber n1 || array[2] is not PdfNumber n2 || array[3] is not PdfNumber n3)
        {
            return false;
        }

        x1 = n0.Value;
        y1 = n1.Value;
        x2 = n2.Value;
        y2 = n3.Value;
        return double.IsFinite(x1) && double.IsFinite(y1) && double.IsFinite(x2) && double.IsFinite(y2);
    }

    private static bool AllFinite(params (double X, double Y)[] points)
    {
        foreach (var (x, y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return false;
            }
        }

        return true;
    }

    private static PdfMatrix ReadMatrixArray(PdfArray array) => new(
        NumAt(array, 0), NumAt(array, 1), NumAt(array, 2), NumAt(array, 3), NumAt(array, 4), NumAt(array, 5));

    private static double NumAt(PdfArray array, int index) =>
        index < array.Count && array[index] is PdfNumber n && double.IsFinite(n.Value) ? n.Value : (index == 0 || index == 3 ? 1 : 0);

    private static void Report(DiagnosticCollection? diagnostics, string message) =>
        diagnostics?.Add(new PdfDiagnostic("PLUME7731", DiagnosticSeverity.Warning, message));

    private static PdfObject? Resolve(PdfDictionary dict, PdfName key, ObjectRegistry? objects) =>
        dict.TryGetValue(key, out var value) ? Resolve(value, objects) : null;

    private static PdfObject? Resolve(PdfObject? value, ObjectRegistry? objects) =>
        value is PdfReference reference && objects is not null ? objects[reference.Target] : value;
}
