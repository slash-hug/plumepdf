using PlumePdf.Content;
using PlumePdf.Raster.Color;
using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Shading;

/// <summary>
/// Mesh shadings — <c>/ShadingType</c> 4 (free-form Gouraud-shaded triangle mesh), 5
/// (lattice-form Gouraud-shaded triangle mesh), 6 (Coons patch mesh), and 7 (tensor-product
/// patch mesh), ISO 32000-1 §8.7.4.5.5-§8.7.4.5.8. Types 6/7 flatten their patches
/// into a fixed-resolution grid of Gouraud-shaded triangles (the same public technique every
/// mesh rasterizer uses for a patch surface — evaluate a bicubic Bézier surface at a subdivision
/// grid, then triangulate the grid) so all four types share one <see cref="IRasterShading"/>
/// (<see cref="TriangleMeshShading"/>) and one per-pixel barycentric-interpolated fill.
/// </summary>
/// <remarks>
/// Every vertex/patch coordinate is transformed into device space (via <see cref="PdfMatrix.Transform"/>
/// with the shading's own <c>Ctm</c>) once, at parse time — a cubic Bézier curve/surface is
/// affine-invariant, so flattening device-space-transformed control points produces exactly the
/// same result as flattening in shading space and transforming each sample, without
/// <see cref="IRasterShading.Paint"/> ever needing to invert a matrix per pixel. All arithmetic
/// is <c>+</c>/<c>-</c>/<c>*</c>/<c>/</c> (De Casteljau lerp-chains for the Bézier evaluation,
/// barycentric weights for the triangle fill) — no <see cref="System.Math"/>/<see cref="System.MathF"/>
/// transcendental, so <c>RasterDeterministicMathBanTests</c> passes. The Coons/tensor
/// boundary-blend and tensor-product surface formulas are standard, published CAGD
/// (computer-aided geometric design) math — not ported from any single implementation's source,
/// so no NOTICE attribution applies (per the clean-room policy in AGENTS.md).
///
/// Vertex/patch counts are capped <em>before</em> the triangle list backing a shading grows past
/// them (<see cref="MaxTriangles"/>/<see cref="MaxPatches"/>/<see cref="MaxVerticesPerRow"/>,
/// <c>PLUME7735</c>) — the classic "a small dictionary integer (<c>/VerticesPerRow</c>) drives an
/// allocation uncorrelated with the actual decoded stream size" decompression-bomb-adjacent
/// pattern (Phase 7/8's precedent). A structurally malformed stream (truncated mid-record, an
/// invalid flag value, a `/Decode` array of the wrong length) throws <c>PLUME7736</c>. Both are
/// caught by <see cref="ShadingFactory"/>'s caller and degrade to the flat mid-gray placeholder,
/// mirroring how <see cref="AxialShading"/>/<see cref="RadialShading"/>'s own cap/malformed
/// exceptions already degrade there — lenient-by-default, consistent with every other shading
/// failure mode in this namespace.
/// </remarks>
internal static class MeshShading
{
    /// <summary>Hard ceiling on the number of triangles one mesh shading may flatten to — enforced before each triangle is appended, so a pathological input can allocate at most this many triangle records.</summary>
    internal const int MaxTriangles = 1_000_000;

    /// <summary>Hard ceiling on the number of Coons/tensor patches one shading may read.</summary>
    internal const int MaxPatches = 200_000;

    /// <summary>Hard ceiling on <c>/VerticesPerRow</c> (type 5) — validated before a single row array is allocated, since it is a document-supplied integer uncorrelated with the actual stream length.</summary>
    internal const int MaxVerticesPerRow = 65_536;

    /// <summary>Per-edge subdivision count used to flatten a Coons/tensor patch into a triangle grid — 8×8 cells (128 triangles) per patch, a fixed, bounded cost independent of document input.</summary>
    private const int PatchSubdivisions = 8;

    private static readonly int[] ValidComponentBitWidths = [1, 2, 4, 8, 12, 16, 24, 32];
    private static readonly int[] ValidFlagBitWidths = [2, 4, 8];

    /// <summary>
    /// Parses a mesh shading stream (<c>/ShadingType</c> 4-7) and builds its paintable
    /// <see cref="IRasterShading"/>.
    /// </summary>
    /// <param name="shadingStream">The shading's stream object (mesh shadings are always streams — the vertex/patch data is the stream payload).</param>
    /// <param name="colorSpace">The shading's already-parsed <c>/ColorSpace</c>.</param>
    /// <param name="function">The shading's already-parsed <c>/Function</c> (optional for mesh shadings — when present, each vertex/corner carries one parametric value instead of a full set of color components), or <see langword="null"/>.</param>
    /// <param name="ctm">The transform from the shading's own coordinate space into device space (ISO 32000-1 §8.7.4.5).</param>
    /// <param name="alpha">The paint-time fill alpha (0-255) every triangle blends with.</param>
    /// <param name="resolve">Resolves an indirect reference to its value.</param>
    /// <param name="options">Supplies the filter registry used to decode the stream.</param>
    /// <param name="diagnostics">Unused directly (mesh parse failures throw rather than degrade internally — see remarks); threaded through for the sub-parsers this method calls (<see cref="ColorSpace.Parse"/>-family already-parsed inputs don't need it again here) for signature symmetry with the rest of this namespace.</param>
    /// <exception cref="PlumePdfException"><c>PLUME7735</c> — a cap (triangle/patch/vertices-per-row) would be exceeded; <c>PLUME7736</c> — the stream/dictionary is structurally malformed.</exception>
    internal static IRasterShading Parse(
        PdfStream shadingStream,
        RasterColorSpace colorSpace,
        PdfFunction? function,
        PdfMatrix ctm,
        byte alpha,
        Func<IndirectReference, PdfObject> resolve,
        PdfOptions options,
        DiagnosticCollection? diagnostics)
    {
        var dict = shadingStream.Dictionary;
        var shadingType = RequireInt(dict, "ShadingType", resolve);

        var bitsPerCoordinate = RequireBits(dict, "BitsPerCoordinate", resolve, ValidComponentBitWidths);
        var bitsPerComponent = RequireBits(dict, "BitsPerComponent", resolve, ValidComponentBitWidths);
        var nComp = function is not null ? 1 : colorSpace.ComponentCount;

        var decode = FunctionEvaluator.RequireNumberArray(dict, "Decode", resolve);
        if (decode.Length != 4 + (2 * nComp))
        {
            throw new PlumePdfException("PLUME7736", $"Mesh shading /Decode must have {4 + (2 * nComp)} entries for this color configuration ([xmin xmax ymin ymax c1min c1max ...]), found {decode.Length}.");
        }

        var decodedBytes = shadingStream.GetDecodedBytes(options.Filters, options, r => resolve(r));
        var reader = new BitReader(decodedBytes);
        var vertexReader = new VertexReader(reader, bitsPerCoordinate, bitsPerComponent, nComp, decode, colorSpace, function, ctm);

        var triangles = new List<(MeshVertex A, MeshVertex B, MeshVertex C)>();

        switch (shadingType)
        {
            case 4:
                ParseFreeFormTriangles(vertexReader, RequireBits(dict, "BitsPerFlag", resolve, ValidFlagBitWidths), triangles);
                break;
            case 5:
                ParseLatticeTriangles(vertexReader, RequireVerticesPerRow(dict, resolve), triangles);
                break;
            case 6:
                ParsePatches(vertexReader, RequireBits(dict, "BitsPerFlag", resolve, ValidFlagBitWidths), controlPointCount: 12, triangles);
                break;
            case 7:
                ParsePatches(vertexReader, RequireBits(dict, "BitsPerFlag", resolve, ValidFlagBitWidths), controlPointCount: 16, triangles);
                break;
            default:
                throw new PlumePdfException("PLUME7736", $"/ShadingType {shadingType} is not a mesh shading type (expected 4, 5, 6, or 7).");
        }

        return new TriangleMeshShading(triangles, alpha);
    }

    // ------------------------------------------------------------------
    // Type 4 — free-form Gouraud-shaded triangle mesh (§8.7.4.5.5).
    // ------------------------------------------------------------------

    private static void ParseFreeFormTriangles(VertexReader vr, int bitsPerFlag, List<(MeshVertex A, MeshVertex B, MeshVertex C)> triangles)
    {
        MeshVertex va = default, vb = default, vc = default;
        var havePreviousTriangle = false;

        while (vr.TryReadFlag(bitsPerFlag, out var flag))
        {
            if (!vr.TryReadPositionAndColor(out var v))
            {
                throw new PlumePdfException("PLUME7736", "Free-form Gouraud triangle mesh (/ShadingType 4) stream is truncated mid-vertex.");
            }

            vr.AlignToByte();

            switch (flag)
            {
                case 0:
                    va = v;
                    vb = ReadPlainVertex(vr, bitsPerFlag, "Free-form Gouraud triangle mesh (/ShadingType 4)");
                    vc = ReadPlainVertex(vr, bitsPerFlag, "Free-form Gouraud triangle mesh (/ShadingType 4)");
                    AddTriangle(triangles, va, vb, vc);
                    break;
                case 1:
                    if (!havePreviousTriangle)
                    {
                        throw new PlumePdfException("PLUME7736", "Free-form Gouraud triangle mesh vertex flag 1 appeared with no preceding triangle to share an edge from.");
                    }

                    (va, vb, vc) = (vb, vc, v);
                    AddTriangle(triangles, va, vb, vc);
                    break;
                case 2:
                    if (!havePreviousTriangle)
                    {
                        throw new PlumePdfException("PLUME7736", "Free-form Gouraud triangle mesh vertex flag 2 appeared with no preceding triangle to share an edge from.");
                    }

                    (vb, vc) = (vc, v);
                    AddTriangle(triangles, va, vb, vc);
                    break;
                default:
                    throw new PlumePdfException("PLUME7736", $"Free-form Gouraud triangle mesh vertex flag must be 0, 1, or 2 — found {flag}.");
            }

            havePreviousTriangle = true;
        }
    }

    private static MeshVertex ReadPlainVertex(VertexReader vr, int bitsPerFlag, string context)
    {
        // Every vertex record — including the 2nd/3rd of a flag-0 triangle — starts with its own
        // flag field; its value is meaningless for these two (a new-triangle's va already decided
        // the triangle boundary), but the bits must still be consumed to stay aligned with the
        // stream.
        if (!vr.TryReadFlag(bitsPerFlag, out _) || !vr.TryReadPositionAndColor(out var v))
        {
            throw new PlumePdfException("PLUME7736", $"{context} stream is truncated mid-vertex.");
        }

        vr.AlignToByte();
        return v;
    }

    // ------------------------------------------------------------------
    // Type 5 — lattice-form Gouraud-shaded triangle mesh (§8.7.4.5.6).
    // ------------------------------------------------------------------

    private static void ParseLatticeTriangles(VertexReader vr, int verticesPerRow, List<(MeshVertex A, MeshVertex B, MeshVertex C)> triangles)
    {
        var currentRow = new MeshVertex[verticesPerRow];
        var currentCount = 0;
        MeshVertex[]? previousRow = null;

        while (vr.TryReadPositionAndColor(out var v))
        {
            vr.AlignToByte();
            currentRow[currentCount] = v;
            currentCount++;

            if (currentCount != verticesPerRow)
            {
                continue;
            }

            if (previousRow is not null)
            {
                for (var i = 0; i < verticesPerRow - 1; i++)
                {
                    AddTriangle(triangles, previousRow[i], previousRow[i + 1], currentRow[i]);
                    AddTriangle(triangles, previousRow[i + 1], currentRow[i + 1], currentRow[i]);
                }
            }

            previousRow = currentRow;
            currentRow = new MeshVertex[verticesPerRow];
            currentCount = 0;
        }
    }

    private static int RequireVerticesPerRow(PdfDictionary dict, Func<IndirectReference, PdfObject> resolve)
    {
        if (!dict.TryGetValue(PdfName.Get("VerticesPerRow"), out var value)
            || FunctionEvaluator.Resolve(value, resolve) is not PdfNumber { IsInteger: true } number
            || !number.TryToInt32(out var count))
        {
            throw new PlumePdfException("PLUME7736", "Lattice-form Gouraud triangle mesh (/ShadingType 5) requires an integer /VerticesPerRow.");
        }

        if (count < 2)
        {
            throw new PlumePdfException("PLUME7736", $"/VerticesPerRow must be at least 2, found {count}.");
        }

        if (count > MaxVerticesPerRow)
        {
            throw new PlumePdfException("PLUME7735", $"/VerticesPerRow ({count}) exceeds the {MaxVerticesPerRow} cap; refusing to allocate a row that wide.");
        }

        return count;
    }

    // ------------------------------------------------------------------
    // Types 6/7 — Coons patch mesh (§8.7.4.5.7) / tensor-product patch mesh (§8.7.4.5.8).
    // ------------------------------------------------------------------

    private static void ParsePatches(VertexReader vr, int bitsPerFlag, int controlPointCount, List<(MeshVertex A, MeshVertex B, MeshVertex C)> triangles)
    {
        (double X, double Y)[]? previousPoints = null;
        (byte R, byte G, byte B)[]? previousColors = null;
        var patchCount = 0;

        while (vr.TryReadFlag(bitsPerFlag, out var flag))
        {
            (double X, double Y)[] points;
            (byte R, byte G, byte B)[] colors;

            if (flag == 0)
            {
                points = new (double X, double Y)[controlPointCount];
                for (var i = 0; i < controlPointCount; i++)
                {
                    if (!vr.TryReadPoint(out points[i]))
                    {
                        throw new PlumePdfException("PLUME7736", "Patch mesh stream is truncated reading a new patch's control points.");
                    }
                }

                colors = new (byte R, byte G, byte B)[4];
                for (var i = 0; i < 4; i++)
                {
                    if (!vr.TryReadColor(out colors[i]))
                    {
                        throw new PlumePdfException("PLUME7736", "Patch mesh stream is truncated reading a new patch's corner colors.");
                    }
                }
            }
            else
            {
                if (flag > 3)
                {
                    throw new PlumePdfException("PLUME7736", $"Patch mesh flag must be 0-3, found {flag}.");
                }

                if (previousPoints is null || previousColors is null)
                {
                    throw new PlumePdfException("PLUME7736", $"Patch mesh flag {flag} appeared with no preceding patch to share an edge from.");
                }

                points = new (double X, double Y)[controlPointCount];
                colors = new (byte R, byte G, byte B)[4];
                CopySharedEdge(previousPoints, previousColors, (int)flag, points, colors);

                for (var i = 4; i < controlPointCount; i++)
                {
                    if (!vr.TryReadPoint(out points[i]))
                    {
                        throw new PlumePdfException("PLUME7736", "Patch mesh stream is truncated reading a continuation patch's new control points.");
                    }
                }

                for (var i = 2; i < 4; i++)
                {
                    if (!vr.TryReadColor(out colors[i]))
                    {
                        throw new PlumePdfException("PLUME7736", "Patch mesh stream is truncated reading a continuation patch's new corner colors.");
                    }
                }
            }

            vr.AlignToByte();

            if (patchCount >= MaxPatches)
            {
                throw new PlumePdfException("PLUME7735", $"Mesh shading exceeds the {MaxPatches}-patch cap; refusing to read more.");
            }

            patchCount++;
            FlattenPatch(points, colors, controlPointCount == 16, triangles);

            previousPoints = points;
            previousColors = colors;
        }
    }

    /// <summary>
    /// Copies the 4 control points and 2 colors a continuation patch (flag 1-3) reuses from the
    /// previous patch's shared boundary edge into the start of the new patch's arrays — the
    /// remaining control points/colors are read fresh by the caller. Edge indices are the
    /// 0-based form of the spec's 1-based p1..p12 numbering: flag 1 shares p4-p7/c2-c3, flag 2
    /// shares p7-p10/c3-c4, flag 3 shares p10-p1/c4-c1.
    /// </summary>
    private static void CopySharedEdge((double X, double Y)[] previousPoints, (byte R, byte G, byte B)[] previousColors, int flag, (double X, double Y)[] points, (byte R, byte G, byte B)[] colors)
    {
        int[] pointIndices;
        int[] colorIndices;
        switch (flag)
        {
            case 1:
                pointIndices = [3, 4, 5, 6];
                colorIndices = [1, 2];
                break;
            case 2:
                pointIndices = [6, 7, 8, 9];
                colorIndices = [2, 3];
                break;
            default: // 3
                pointIndices = [9, 10, 11, 0];
                colorIndices = [3, 0];
                break;
        }

        for (var i = 0; i < 4; i++)
        {
            points[i] = previousPoints[pointIndices[i]];
        }

        colors[0] = previousColors[colorIndices[0]];
        colors[1] = previousColors[colorIndices[1]];
    }

    /// <summary>
    /// Flattens one Coons (<paramref name="isTensor"/> <see langword="false"/>, 12 control
    /// points) or tensor-product (<see langword="true"/>, 16 control points) patch into a
    /// <see cref="PatchSubdivisions"/>×<see cref="PatchSubdivisions"/> grid of Gouraud-shaded
    /// triangles, evaluating the surface at each grid point and bilinearly interpolating the 4
    /// corner colors across the same <c>(u, v)</c> parameter space.
    /// </summary>
    private static void FlattenPatch((double X, double Y)[] points, (byte R, byte G, byte B)[] colors, bool isTensor, List<(MeshVertex A, MeshVertex B, MeshVertex C)> triangles)
    {
        const int n = PatchSubdivisions;
        var grid = new MeshVertex[n + 1, n + 1];

        for (var iv = 0; iv <= n; iv++)
        {
            var v = (double)iv / n;
            for (var iu = 0; iu <= n; iu++)
            {
                var u = (double)iu / n;
                var (x, y) = isTensor ? EvaluateTensor(points, u, v) : EvaluateCoons(points, u, v);
                var (r, g, b) = BilinearColor(colors, u, v);
                grid[iv, iu] = new MeshVertex(x, y, r, g, b);
            }
        }

        for (var iv = 0; iv < n; iv++)
        {
            for (var iu = 0; iu < n; iu++)
            {
                var a = grid[iv, iu];
                var b = grid[iv, iu + 1];
                var c = grid[iv + 1, iu];
                var d = grid[iv + 1, iu + 1];
                AddTriangle(triangles, a, b, c);
                AddTriangle(triangles, b, d, c);
            }
        }
    }

    /// <summary>
    /// The bilinearly-blended Coons patch surface (standard CAGD construction): four cubic
    /// Bézier boundary curves, ruled-surface-blended, minus the bilinear interpolant of the four
    /// corners (which the two ruled-surface terms would otherwise double-count). <c>points</c>
    /// holds the spec's p1..p12 (0-based p[0]..p[11]) in their documented read order; corners are
    /// p1=(u0,v0), p4=(u1,v0), p7=(u1,v1), p10=(u0,v1).
    /// </summary>
    private static (double X, double Y) EvaluateCoons((double X, double Y)[] p, double u, double v)
    {
        var edgeD1 = CubicBezier(p[0], p[1], p[2], p[3], u); // v=0 boundary: corner(0,0) -> corner(1,0)
        var edgeD2 = CubicBezier(p[9], p[8], p[7], p[6], u); // v=1 boundary: corner(0,1) -> corner(1,1)
        var edgeC1 = CubicBezier(p[0], p[11], p[10], p[9], v); // u=0 boundary: corner(0,0) -> corner(0,1)
        var edgeC2 = CubicBezier(p[3], p[4], p[5], p[6], v); // u=1 boundary: corner(1,0) -> corner(1,1)

        var corner00 = p[0];
        var corner10 = p[3];
        var corner01 = p[9];
        var corner11 = p[6];
        var w00 = (1 - u) * (1 - v);
        var w10 = u * (1 - v);
        var w01 = (1 - u) * v;
        var w11 = u * v;

        var bilinearX = (w00 * corner00.X) + (w10 * corner10.X) + (w01 * corner01.X) + (w11 * corner11.X);
        var bilinearY = (w00 * corner00.Y) + (w10 * corner10.Y) + (w01 * corner01.Y) + (w11 * corner11.Y);

        var x = ((1 - v) * edgeD1.X) + (v * edgeD2.X) + ((1 - u) * edgeC1.X) + (u * edgeC2.X) - bilinearX;
        var y = ((1 - v) * edgeD1.Y) + (v * edgeD2.Y) + ((1 - u) * edgeC1.Y) + (u * edgeC2.Y) - bilinearY;
        return (x, y);
    }

    /// <summary>
    /// The full tensor-product bicubic Bézier surface (standard CAGD construction, De Casteljau
    /// nested in both parameters) over the patch's 4×4 control-point grid — the same 12 boundary
    /// points as <see cref="EvaluateCoons"/> plus <c>p13..p16</c> (0-based <c>p[12]..p[15]</c>),
    /// the interior control points a tensor patch adds. Grid row order (each a 4-point Bézier
    /// curve in <c>u</c>): <c>[p1,p2,p3,p4]</c>, <c>[p12,p13,p14,p5]</c>, <c>[p11,p16,p15,p6]</c>,
    /// <c>[p10,p9,p8,p7]</c> — the standard 4×4 layout the format's <c>p1..p16</c> read order
    /// resolves to.
    /// </summary>
    private static (double X, double Y) EvaluateTensor((double X, double Y)[] p, double u, double v)
    {
        var row0 = CubicBezier(p[0], p[1], p[2], p[3], u);
        var row1 = CubicBezier(p[11], p[12], p[13], p[4], u);
        var row2 = CubicBezier(p[10], p[15], p[14], p[5], u);
        var row3 = CubicBezier(p[9], p[8], p[7], p[6], u);
        return CubicBezier(row0, row1, row2, row3, v);
    }

    private static (double X, double Y) CubicBezier((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3, double t)
    {
        var a = Lerp(p0, p1, t);
        var b = Lerp(p1, p2, t);
        var c = Lerp(p2, p3, t);
        var d = Lerp(a, b, t);
        var e = Lerp(b, c, t);
        return Lerp(d, e, t);
    }

    private static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t) =>
        (a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));

    private static (byte R, byte G, byte B) BilinearColor((byte R, byte G, byte B)[] c, double u, double v)
    {
        var w00 = (1 - u) * (1 - v);
        var w10 = u * (1 - v);
        var w01 = (1 - u) * v;
        var w11 = u * v;

        var r = (w00 * c[0].R) + (w10 * c[1].R) + (w11 * c[2].R) + (w01 * c[3].R);
        var g = (w00 * c[0].G) + (w10 * c[1].G) + (w11 * c[2].G) + (w01 * c[3].G);
        var b = (w00 * c[0].B) + (w10 * c[1].B) + (w11 * c[2].B) + (w01 * c[3].B);
        return (ClampByte(r), ClampByte(g), ClampByte(b));
    }

    // ------------------------------------------------------------------
    // Shared helpers.
    // ------------------------------------------------------------------

    private static void AddTriangle(List<(MeshVertex A, MeshVertex B, MeshVertex C)> triangles, MeshVertex a, MeshVertex b, MeshVertex c)
    {
        if (triangles.Count >= MaxTriangles)
        {
            throw new PlumePdfException("PLUME7735", $"Mesh shading exceeds the {MaxTriangles}-triangle cap; refusing to flatten more.");
        }

        triangles.Add((a, b, c));
    }

    private static int RequireInt(PdfDictionary dict, string key, Func<IndirectReference, PdfObject> resolve)
    {
        if (!dict.TryGetValue(PdfName.Get(key), out var value)
            || FunctionEvaluator.Resolve(value, resolve) is not PdfNumber { IsInteger: true } number
            || !number.TryToInt32(out var result))
        {
            throw new PlumePdfException("PLUME7736", $"Mesh shading dictionary is missing required integer key /{key}.");
        }

        return result;
    }

    private static int RequireBits(PdfDictionary dict, string key, Func<IndirectReference, PdfObject> resolve, int[] validValues)
    {
        if (!dict.TryGetValue(PdfName.Get(key), out var value)
            || FunctionEvaluator.Resolve(value, resolve) is not PdfNumber { IsInteger: true } number
            || !number.TryToInt32(out var bits)
            || Array.IndexOf(validValues, bits) < 0)
        {
            throw new PlumePdfException("PLUME7736", $"Mesh shading /{key} must be one of [{string.Join(", ", validValues)}].");
        }

        return bits;
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>One flattened mesh vertex, in device-space pixel coordinates (post-CTM) with its resolved sRGB color.</summary>
    private readonly record struct MeshVertex(double X, double Y, byte R, byte G, byte B);

    /// <summary>
    /// Reads position/color records out of a mesh shading's bit-packed stream (ISO 32000-1
    /// §8.7.4.5.5: flag then a coordinate pair then color components, each field
    /// <c>BitsPerFlag</c>/<c>BitsPerCoordinate</c>/<c>BitsPerComponent</c> wide, MSB-first,
    /// denormalized through <c>/Decode</c>), transforming each coordinate to device space via
    /// the shading's <see cref="PdfMatrix"/> and each color through <c>/Function</c> (when
    /// present) then <see cref="RasterColorSpace.ToRgb"/> as it goes.
    /// </summary>
    private sealed class VertexReader
    {
        private readonly BitReader _reader;
        private readonly int _bitsPerCoordinate;
        private readonly int _bitsPerComponent;
        private readonly int _nComp;
        private readonly double[] _decode;
        private readonly RasterColorSpace _colorSpace;
        private readonly PdfFunction? _function;
        private readonly PdfMatrix _ctm;
        private readonly double[] _rawColor;
        private readonly double[] _functionOutput;

        internal VertexReader(BitReader reader, int bitsPerCoordinate, int bitsPerComponent, int nComp, double[] decode, RasterColorSpace colorSpace, PdfFunction? function, PdfMatrix ctm)
        {
            _reader = reader;
            _bitsPerCoordinate = bitsPerCoordinate;
            _bitsPerComponent = bitsPerComponent;
            _nComp = nComp;
            _decode = decode;
            _colorSpace = colorSpace;
            _function = function;
            _ctm = ctm;
            _rawColor = new double[nComp];
            _functionOutput = function is not null ? new double[colorSpace.ComponentCount] : [];
        }

        internal bool TryReadFlag(int bitsPerFlag, out uint flag)
        {
            if (!_reader.HasBits(bitsPerFlag))
            {
                flag = 0;
                return false;
            }

            flag = _reader.ReadBits(bitsPerFlag);
            return true;
        }

        internal bool TryReadPoint(out (double X, double Y) point)
        {
            if (!_reader.HasBits(_bitsPerCoordinate * 2))
            {
                point = default;
                return false;
            }

            var rawX = _reader.ReadBits(_bitsPerCoordinate);
            var rawY = _reader.ReadBits(_bitsPerCoordinate);
            var x = Denormalize(rawX, _bitsPerCoordinate, _decode[0], _decode[1]);
            var y = Denormalize(rawY, _bitsPerCoordinate, _decode[2], _decode[3]);
            point = _ctm.Transform(x, y);
            return true;
        }

        internal bool TryReadColor(out (byte R, byte G, byte B) color)
        {
            if (!_reader.HasBits(_bitsPerComponent * _nComp))
            {
                color = default;
                return false;
            }

            for (var i = 0; i < _nComp; i++)
            {
                var raw = _reader.ReadBits(_bitsPerComponent);
                _rawColor[i] = Denormalize(raw, _bitsPerComponent, _decode[4 + (2 * i)], _decode[5 + (2 * i)]);
            }

            if (_function is not null)
            {
                _function.Evaluate(_rawColor, _functionOutput);
                color = _colorSpace.ToRgb(_functionOutput);
            }
            else
            {
                color = _colorSpace.ToRgb(_rawColor);
            }

            return true;
        }

        internal bool TryReadPositionAndColor(out MeshVertex vertex)
        {
            if (!TryReadPoint(out var p) || !TryReadColor(out var c))
            {
                vertex = default;
                return false;
            }

            vertex = new MeshVertex(p.X, p.Y, c.R, c.G, c.B);
            return true;
        }

        internal void AlignToByte() => _reader.AlignToByte();

        private static double Denormalize(uint raw, int bits, double lo, double hi)
        {
            var maxRaw = bits >= 32 ? 4294967295.0 : (double)((1u << bits) - 1u);
            if (maxRaw <= 0 || !double.IsFinite(lo) || !double.IsFinite(hi))
            {
                return lo;
            }

            return lo + ((raw / maxRaw) * (hi - lo));
        }
    }

    /// <summary>A MSB-first bit reader over an already-decoded (filter-applied) byte buffer.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private long _bitPosition;

        internal BitReader(byte[] data) => _data = data;

        private long TotalBits => (long)_data.Length * 8;

        internal bool HasBits(int count) => count >= 0 && _bitPosition + count <= TotalBits;

        internal uint ReadBits(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
            {
                var byteIndex = (int)(_bitPosition >> 3);
                var bitIndex = (int)(_bitPosition & 7);
                var bit = byteIndex < _data.Length ? (_data[byteIndex] >> (7 - bitIndex)) & 1 : 0;
                value = (value << 1) | (uint)bit;
                _bitPosition++;
            }

            return value;
        }

        internal void AlignToByte()
        {
            var remainder = _bitPosition & 7;
            if (remainder != 0)
            {
                _bitPosition += 8 - remainder;
            }
        }
    }

    /// <summary>
    /// The shared <see cref="IRasterShading"/> for every mesh type: a flat list of device-space,
    /// per-vertex-colored triangles, filled with standard barycentric Gouraud interpolation.
    /// </summary>
    private sealed class TriangleMeshShading : IRasterShading
    {
        private readonly List<(MeshVertex A, MeshVertex B, MeshVertex C)> _triangles;
        private readonly byte _alpha;

        internal TriangleMeshShading(List<(MeshVertex A, MeshVertex B, MeshVertex C)> triangles, byte alpha)
        {
            _triangles = triangles;
            _alpha = alpha;
        }

        public void Paint(RasterSurface surface, ClipWindow clip)
        {
            foreach (var (a, b, c) in _triangles)
            {
                FillTriangle(surface, a, b, c, clip, _alpha);
            }
        }

        private static void FillTriangle(RasterSurface surface, MeshVertex a, MeshVertex b, MeshVertex c, ClipWindow clip, byte alpha)
        {
            var minX = Math.Max(clip.MinX, (int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
            var maxX = Math.Min(clip.MaxX, (int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
            var minY = Math.Max(clip.MinY, (int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
            var maxY = Math.Min(clip.MaxY, (int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
            if (minX >= maxX || minY >= maxY)
            {
                return;
            }

            var denom = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
            if (Math.Abs(denom) < 1e-9)
            {
                return; // Degenerate (zero-area) triangle — nothing to fill.
            }

            const double edgeTolerance = -1e-6;

            for (var y = minY; y < maxY; y++)
            {
                var py = y + 0.5;
                for (var x = minX; x < maxX; x++)
                {
                    var px = x + 0.5;
                    var w0 = (((b.Y - c.Y) * (px - c.X)) + ((c.X - b.X) * (py - c.Y))) / denom;
                    var w1 = (((c.Y - a.Y) * (px - c.X)) + ((a.X - c.X) * (py - c.Y))) / denom;
                    var w2 = 1 - w0 - w1;
                    if (w0 < edgeTolerance || w1 < edgeTolerance || w2 < edgeTolerance)
                    {
                        continue;
                    }

                    var coverage = clip.CoverageAt(x, y);
                    if (coverage == 0)
                    {
                        continue;
                    }

                    var r = ClampByte((w0 * a.R) + (w1 * b.R) + (w2 * c.R));
                    var g = ClampByte((w0 * a.G) + (w1 * b.G) + (w2 * c.G));
                    var bChannel = ClampByte((w0 * a.B) + (w1 * b.B) + (w2 * c.B));
                    surface.BlendPixel(x, y, bChannel, g, r, alpha, coverage);
                }
            }
        }
    }
}
