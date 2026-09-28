using PlumePdf.Fonts.Outlines;

namespace PlumePdf.Fonts.Tables;

/// <summary>One component reference inside a composite glyph, per OpenType spec §5.3.1.</summary>
/// <param name="GlyphId">The referenced glyph's ID.</param>
/// <param name="GlyphIdByteOffset">The byte offset, within the owning glyph's own <c>glyf</c> span, of the 2-byte <c>glyphIndex</c> field — where <see cref="FontSubsetter"/> writes the remapped ID back when rebuilding a subset.</param>
internal readonly record struct GlyphComponent(int GlyphId, int GlyphIdByteOffset);

/// <summary>
/// The <c>glyf</c> table (OpenType spec §5.3.1) — per-glyph outline data, addressed by
/// <see cref="LocaTable"/>. Exposes each glyph's raw byte span (needed verbatim for
/// subsetting/embedding — PDF never decodes contour coordinates itself), its declared
/// bounding box (present in the 10-byte header for both simple and composite glyphs, so no
/// component walk is needed just to read it), and composite-component enumeration.
/// </summary>
internal sealed class GlyfTable
{
    private readonly byte[] _data;
    private readonly int _tableOffset;
    private readonly LocaTable _loca;

    private GlyfTable(byte[] data, int tableOffset, LocaTable loca)
    {
        _data = data;
        _tableOffset = tableOffset;
        _loca = loca;
    }

    /// <summary>
    /// Wraps a font's already-parsed <c>glyf</c> table for glyph-span and composite-component
    /// access. <paramref name="fullFontData"/> is the whole font file (glyph spans are
    /// addressed relative to it via <paramref name="glyfTableOffset"/>) — kept this way rather
    /// than copying the table out, since <see cref="FontSubsetter"/> needs to copy arbitrary
    /// byte ranges out of the original file regardless.
    /// </summary>
    public static GlyfTable Wrap(byte[] fullFontData, int glyfTableOffset, int glyfTableLength, LocaTable loca)
    {
        ArgumentNullException.ThrowIfNull(fullFontData);
        if ((long)glyfTableOffset + glyfTableLength > fullFontData.Length)
        {
            throw new PlumePdfException("PLUME8004", "'glyf' table's offset+length runs past the end of the font file.");
        }

        return new GlyfTable(fullFontData, glyfTableOffset, loca);
    }

    /// <summary>The number of glyphs (from the backing <see cref="LocaTable"/>).</summary>
    public int GlyphCount => _loca.GlyphCount;

    /// <summary>
    /// The raw, still-encoded bytes of glyph <paramref name="glyphId"/> — empty for a glyph
    /// with no outline (e.g. space). Throws <c>PLUME8012</c> if <c>loca</c> describes a span
    /// that runs past the end of the table or is negative-length (offsets must be
    /// non-decreasing per spec).
    /// </summary>
    public ReadOnlyMemory<byte> GetGlyphBytes(int glyphId)
    {
        if (glyphId < 0 || glyphId >= _loca.GlyphCount)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var start = _loca[glyphId];
        var end = _loca[glyphId + 1];
        if (end < start)
        {
            throw new PlumePdfException("PLUME8012", $"'loca' offsets for glyph {glyphId} are not non-decreasing (start {start} > end {end}).");
        }

        var length = end - start;
        if ((long)_tableOffset + start + length > _data.Length)
        {
            throw new PlumePdfException("PLUME8012", $"'glyf' span for glyph {glyphId} runs past the end of the table.");
        }

        return _data.AsMemory((int)(_tableOffset + start), (int)length);
    }

    /// <summary>Whether glyph <paramref name="glyphId"/> is a composite (its outline is built from other glyphs, per §5.3.1's <c>numberOfContours == -1</c> convention).</summary>
    public bool IsComposite(int glyphId)
    {
        var glyph = GetGlyphBytes(glyphId).Span;
        return glyph.Length >= 10 && SfntPrimitives.TryReadInt16(glyph, 0, out var numberOfContours) && numberOfContours < 0;
    }

    /// <summary>The glyph's declared bounding box (xMin, yMin, xMax, yMax), in font units — present in the header for both simple and composite glyphs. All zero for an empty (no-outline) glyph.</summary>
    public (short XMin, short YMin, short XMax, short YMax) GetBoundingBox(int glyphId)
    {
        var glyph = GetGlyphBytes(glyphId).Span;
        if (glyph.Length < 10)
        {
            return (0, 0, 0, 0);
        }

        SfntPrimitives.TryReadInt16(glyph, 2, out var xMin);
        SfntPrimitives.TryReadInt16(glyph, 4, out var yMin);
        SfntPrimitives.TryReadInt16(glyph, 6, out var xMax);
        SfntPrimitives.TryReadInt16(glyph, 8, out var yMax);
        return (xMin, yMin, xMax, yMax);
    }

    /// <summary>
    /// The immediate (one level, not transitive) component glyph references of a composite
    /// glyph, per §5.3.1's component-record chain. Empty for a simple glyph. Throws
    /// <c>PLUME8012</c> if the component chain is truncated mid-record.
    /// </summary>
    public List<GlyphComponent> GetComponents(int glyphId)
    {
        var components = new List<GlyphComponent>();
        var glyph = GetGlyphBytes(glyphId).Span;
        if (glyph.Length < 10 || !SfntPrimitives.TryReadInt16(glyph, 0, out var numberOfContours) || numberOfContours >= 0)
        {
            return components;
        }

        var offset = 10;
        while (true)
        {
            if (!SfntPrimitives.TryReadUInt16(glyph, offset, out var flags)
                || !SfntPrimitives.TryReadUInt16(glyph, offset + 2, out var componentGlyphId))
            {
                throw new PlumePdfException("PLUME8012", $"Composite glyph {glyphId}'s component chain is truncated.");
            }

            components.Add(new GlyphComponent(componentGlyphId, offset + 2));

            var argsSize = (flags & 0x0001) != 0 ? 4 : 2; // ARG_1_AND_2_ARE_WORDS
            var scaleSize = (flags & 0x0008) != 0 ? 2      // WE_HAVE_A_SCALE
                : (flags & 0x0040) != 0 ? 4                 // WE_HAVE_AN_X_AND_Y_SCALE
                : (flags & 0x0080) != 0 ? 8                 // WE_HAVE_A_TWO_BY_TWO
                : 0;

            offset += 4 + argsSize + scaleSize;

            if ((flags & 0x0020) == 0) // MORE_COMPONENTS
            {
                break;
            }
        }

        return components;
    }

    /// <summary>
    /// Extends <paramref name="glyphIds"/> in place with every glyph transitively reachable
    /// through composite-glyph component references, starting from its current members. Uses
    /// an explicit iterative stack (never C# recursion, a stack-overflow-safety
    /// rule) with per-glyph coloring so a genuine cycle (a component chain that reaches back
    /// to one of its own ancestors — not merely two composites sharing a component, which is
    /// legitimate reuse) is distinguished and rejected, rather than silently reaching a
    /// fixed point.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME8007</c>: the component chain either cycles back to an ancestor, or nests
    /// deeper than <paramref name="maxDepth"/>.
    /// </exception>
    public void ResolveCompositeClosure(HashSet<int> glyphIds, int maxDepth)
    {
        // 0 = unvisited, 1 = on the current expansion path (gray), 2 = fully expanded (black).
        var color = new Dictionary<int, byte>();
        var stack = new Stack<(int GlyphId, List<GlyphComponent> Components, int NextIndex)>();

        foreach (var root in glyphIds.ToArray())
        {
            if (color.GetValueOrDefault(root) != 0)
            {
                continue;
            }

            stack.Push((root, GetComponents(root), 0));
            color[root] = 1;

            while (stack.Count > 0)
            {
                if (stack.Count > maxDepth)
                {
                    throw new PlumePdfException("PLUME8007", $"Composite glyph nesting exceeded the configured limit of {maxDepth} while resolving glyph {root}.");
                }

                var (glyphId, components, nextIndex) = stack.Pop();

                if (nextIndex >= components.Count)
                {
                    color[glyphId] = 2;
                    continue;
                }

                stack.Push((glyphId, components, nextIndex + 1));

                var childId = components[nextIndex].GlyphId;
                glyphIds.Add(childId);
                var childColor = color.GetValueOrDefault(childId);

                if (childColor == 1)
                {
                    throw new PlumePdfException("PLUME8007", $"Composite glyph {childId} cycles back to one of its own ancestors via glyph {glyphId}.");
                }

                if (childColor == 2)
                {
                    continue; // Already fully expanded via another path — legitimate shared reuse, not a cycle.
                }

                color[childId] = 1;
                stack.Push((childId, GetComponents(childId), 0));
            }
        }
    }

    /// <summary>One decoded, still-undecomposed contour point (OpenType spec §5.3.1's point-flags convention).</summary>
    private readonly record struct RawPoint(float X, float Y, bool OnCurve);

    /// <summary>One composite-glyph component's full affine placement (args + optional scale), per §5.3.1. A superset of <see cref="GetComponents"/>'s (GlyphId, byte-offset) pair — kept as a separate decode so <see cref="FontSubsetter"/>'s existing remap path (which only ever needs the glyph id and the byte offset to patch) is untouched (C-OUTLINE-1 keeps existing subset behavior intact).</summary>
    private readonly record struct ComponentTransform(int GlyphId, double A, double B, double C, double D, double Dx, double Dy);

    /// <summary>
    /// Decodes glyph <paramref name="glyphId"/>'s outline into <see cref="GlyphOutline"/>'s
    /// normalized moveTo/lineTo/curveTo command stream, in font design units. Composite glyphs
    /// are resolved recursively (each component's sub-outline transformed by its own affine
    /// placement and appended) up to <paramref name="limits"/>'s
    /// <see cref="FontReadLimits.MaxCompositeGlyphDepth"/>. An empty (no-outline) glyph — e.g.
    /// space — returns <see cref="GlyphOutline.Empty"/>.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME8012</c>: the glyph's point/flag/coordinate data is truncated or malformed.
    /// <c>PLUME8007</c>: composite nesting exceeds <see cref="FontReadLimits.MaxCompositeGlyphDepth"/> (mirrors <see cref="ResolveCompositeClosure"/>'s own cap).
    /// </exception>
    public GlyphOutline BuildOutline(int glyphId, FontReadLimits limits)
    {
        var builder = new GlyphOutlineBuilder();
        AppendOutline(glyphId, 0, limits, builder, 1, 0, 0, 1, 0, 0);
        return builder.Build();
    }

    private void AppendOutline(int glyphId, int depth, FontReadLimits limits, GlyphOutlineBuilder sink, double a, double b, double c, double d, double dx, double dy)
    {
        if (depth > limits.MaxCompositeGlyphDepth)
        {
            throw new PlumePdfException("PLUME8007", $"Composite glyph nesting exceeded the configured limit of {limits.MaxCompositeGlyphDepth} while decoding glyph {glyphId}'s outline.");
        }

        if (IsComposite(glyphId))
        {
            foreach (var component in GetComponentTransforms(glyphId))
            {
                // Compose the parent transform with the component's own — standard 2D affine
                // composition (apply the component's transform first, then the parent's).
                var ca = (component.A * a) + (component.B * c);
                var cb = (component.A * b) + (component.B * d);
                var cc = (component.C * a) + (component.D * c);
                var cd = (component.C * b) + (component.D * d);
                var cdx = (component.Dx * a) + (component.Dy * c) + dx;
                var cdy = (component.Dx * b) + (component.Dy * d) + dy;
                AppendOutline(component.GlyphId, depth + 1, limits, sink, ca, cb, cc, cd, cdx, cdy);
            }

            return;
        }

        foreach (var contour in DecodeSimpleGlyphContours(glyphId))
        {
            EmitContour(contour, sink, a, b, c, d, dx, dy);
        }
    }

    private List<RawPoint[]> DecodeSimpleGlyphContours(int glyphId)
    {
        var glyph = GetGlyphBytes(glyphId).Span;
        if (glyph.Length == 0)
        {
            return [];
        }

        if (!SfntPrimitives.TryReadInt16(glyph, 0, out var numberOfContours) || numberOfContours < 0)
        {
            return [];
        }

        if (numberOfContours == 0)
        {
            return [];
        }

        var offset = 10;
        var endPts = new int[numberOfContours];
        for (var i = 0; i < numberOfContours; i++)
        {
            if (!SfntPrimitives.TryReadUInt16(glyph, offset, out var endPt))
            {
                throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s endPtsOfContours array is truncated.");
            }

            endPts[i] = endPt;
            offset += 2;
        }

        var numPoints = endPts[^1] + 1;

        if (!SfntPrimitives.TryReadUInt16(glyph, offset, out var instructionLength))
        {
            throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s instructionLength field is truncated.");
        }

        offset += 2 + instructionLength;

        var flags = new byte[numPoints];
        for (var i = 0; i < numPoints;)
        {
            if (!SfntPrimitives.TryReadUInt8(glyph, offset, out var flag))
            {
                throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s flags array is truncated.");
            }

            offset += 1;
            flags[i++] = flag;

            if ((flag & 0x08) != 0) // REPEAT_FLAG
            {
                if (!SfntPrimitives.TryReadUInt8(glyph, offset, out var repeatCount))
                {
                    throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s flag repeat count is truncated.");
                }

                offset += 1;
                for (var r = 0; r < repeatCount && i < numPoints; r++)
                {
                    flags[i++] = flag;
                }
            }
        }

        var xs = new float[numPoints];
        var xAccum = 0;
        for (var i = 0; i < numPoints; i++)
        {
            var flag = flags[i];
            if ((flag & 0x02) != 0) // X_SHORT_VECTOR
            {
                if (!SfntPrimitives.TryReadUInt8(glyph, offset, out var delta))
                {
                    throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s x-coordinate array is truncated.");
                }

                offset += 1;
                xAccum += (flag & 0x10) != 0 ? delta : -delta; // bit 0x10 (SAME_OR_POSITIVE) selects the sign for the 1-byte form.
            }
            else if ((flag & 0x10) == 0) // not short, not "same as previous" -> full int16 delta
            {
                if (!SfntPrimitives.TryReadInt16(glyph, offset, out var delta))
                {
                    throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s x-coordinate array is truncated.");
                }

                offset += 2;
                xAccum += delta;
            }

            // else: short-vector bit clear and same-or-positive bit set -> delta 0 (repeat previous x).
            xs[i] = xAccum;
        }

        var ys = new float[numPoints];
        var yAccum = 0;
        for (var i = 0; i < numPoints; i++)
        {
            var flag = flags[i];
            if ((flag & 0x04) != 0) // Y_SHORT_VECTOR
            {
                if (!SfntPrimitives.TryReadUInt8(glyph, offset, out var delta))
                {
                    throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s y-coordinate array is truncated.");
                }

                offset += 1;
                yAccum += (flag & 0x20) != 0 ? delta : -delta; // bit 0x20 (SAME_OR_POSITIVE) selects the sign for the 1-byte form.
            }
            else if ((flag & 0x20) == 0)
            {
                if (!SfntPrimitives.TryReadInt16(glyph, offset, out var delta))
                {
                    throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s y-coordinate array is truncated.");
                }

                offset += 2;
                yAccum += delta;
            }

            ys[i] = yAccum;
        }

        var contours = new List<RawPoint[]>(numberOfContours);
        var start = 0;
        foreach (var end in endPts)
        {
            if (end < start || end >= numPoints)
            {
                throw new PlumePdfException("PLUME8012", $"Glyph {glyphId}'s endPtsOfContours entries are not non-decreasing and in range.");
            }

            var count = end - start + 1;
            var pts = new RawPoint[count];
            for (var i = 0; i < count; i++)
            {
                var idx = start + i;
                pts[i] = new RawPoint(xs[idx], ys[idx], (flags[idx] & 0x01) != 0);
            }

            contours.Add(pts);
            start = end + 1;
        }

        return contours;
    }

    /// <summary>
    /// Reconstructs one TrueType contour's implicit on/off-curve point chain (OpenType spec
    /// §5.3.1) into moveTo/lineTo/quadTo calls on <paramref name="sink"/>. Off-curve points
    /// between two other off-curve points imply an on-curve point at their midpoint — the
    /// standard TrueType contour-decompression rule every TrueType rasterizer implements
    /// (FreeType's <c>outline decompose</c>, stb_truetype's <c>stbtt__csctx_v_line_v</c>
    /// equivalent) — expressed here independently against the public OpenType spec description,
    /// not any other library's source (the clean-room policy in AGENTS.md).
    /// </summary>
    private static void EmitContour(RawPoint[] points, GlyphOutlineBuilder sink, double a, double b, double c, double d, double dx, double dy)
    {
        if (points.Length == 0)
        {
            return;
        }

        (float X, float Y) Transform(RawPoint p) => ((float)((p.X * a) + (p.Y * c) + dx), (float)((p.X * b) + (p.Y * d) + dy));

        var n = points.Length;
        var startIndex = -1;
        for (var i = 0; i < n; i++)
        {
            if (points[i].OnCurve)
            {
                startIndex = i;
                break;
            }
        }

        (float X, float Y) startPoint;
        int firstIterated;
        int steps;
        if (startIndex < 0)
        {
            // Every point in the contour is off-curve: synthesize an on-curve start at the
            // midpoint of the last and first points (OpenType spec §5.3.1). None of the n raw
            // points has been "consumed" by this synthetic start, so all n still need walking.
            var p0 = Transform(points[n - 1]);
            var p1 = Transform(points[0]);
            startPoint = ((p0.X + p1.X) / 2f, (p0.Y + p1.Y) / 2f);
            firstIterated = 0;
            steps = n;
        }
        else
        {
            startPoint = Transform(points[startIndex]);
            firstIterated = (startIndex + 1) % n;
            steps = n - 1; // The real on-curve start point was already consumed above.
        }

        sink.MoveTo(startPoint.X, startPoint.Y);

        (float X, float Y)? pendingControl = null;
        for (var k = 0; k < steps; k++)
        {
            var idx = (firstIterated + k) % n;
            var p = points[idx];
            var tp = Transform(p);

            if (p.OnCurve)
            {
                if (pendingControl is { } ctrl)
                {
                    sink.QuadTo(ctrl.X, ctrl.Y, tp.X, tp.Y);
                    pendingControl = null;
                }
                else
                {
                    sink.LineTo(tp.X, tp.Y);
                }
            }
            else
            {
                if (pendingControl is { } ctrl)
                {
                    var mid = ((ctrl.X + tp.X) / 2f, (ctrl.Y + tp.Y) / 2f);
                    sink.QuadTo(ctrl.X, ctrl.Y, mid.Item1, mid.Item2);
                }

                pendingControl = tp;
            }
        }

        if (pendingControl is { } finalControl)
        {
            sink.QuadTo(finalControl.X, finalControl.Y, startPoint.X, startPoint.Y);
        }

        sink.ClosePath();
    }

    /// <summary>Decodes a composite glyph's per-component affine placement (args + optional scale), per §5.3.1. Point-matching composites (<c>ARGS_ARE_XY_VALUES</c> clear) are rare in practice; this decoder falls back to a zero offset for them rather than resolving the matched points, a documented simplification.</summary>
    private List<ComponentTransform> GetComponentTransforms(int glyphId)
    {
        var result = new List<ComponentTransform>();
        var glyph = GetGlyphBytes(glyphId).Span;
        if (glyph.Length < 10 || !SfntPrimitives.TryReadInt16(glyph, 0, out var numberOfContours) || numberOfContours >= 0)
        {
            return result;
        }

        var offset = 10;
        while (true)
        {
            if (!SfntPrimitives.TryReadUInt16(glyph, offset, out var flags)
                || !SfntPrimitives.TryReadUInt16(glyph, offset + 2, out var componentGlyphId))
            {
                throw new PlumePdfException("PLUME8012", $"Composite glyph {glyphId}'s component chain is truncated.");
            }

            var argOffset = offset + 4;
            double dx = 0, dy = 0;
            var argsAreWords = (flags & 0x0001) != 0; // ARG_1_AND_2_ARE_WORDS
            var argsAreXy = (flags & 0x0002) != 0; // ARGS_ARE_XY_VALUES

            if (argsAreXy)
            {
                if (argsAreWords)
                {
                    if (!SfntPrimitives.TryReadInt16(glyph, argOffset, out var argX) || !SfntPrimitives.TryReadInt16(glyph, argOffset + 2, out var argY))
                    {
                        throw new PlumePdfException("PLUME8012", $"Composite glyph {glyphId}'s component arguments are truncated.");
                    }

                    dx = argX;
                    dy = argY;
                }
                else
                {
                    // Signed bytes.
                    if (!SfntPrimitives.TryReadUInt8(glyph, argOffset, out var argXb) || !SfntPrimitives.TryReadUInt8(glyph, argOffset + 1, out var argYb))
                    {
                        throw new PlumePdfException("PLUME8012", $"Composite glyph {glyphId}'s component arguments are truncated.");
                    }

                    dx = unchecked((sbyte)argXb);
                    dy = unchecked((sbyte)argYb);
                }
            }
            // else: point-matching composite — dx/dy stay 0 (documented simplification above).

            var argsSize = argsAreWords ? 4 : 2;
            var scaleOffset = argOffset + argsSize;

            double a = 1, b = 0, c = 0, d = 1;
            if ((flags & 0x0008) != 0) // WE_HAVE_A_SCALE
            {
                if (!SfntPrimitives.TryReadF2Dot14(glyph, scaleOffset, out var scale))
                {
                    throw new PlumePdfException("PLUME8012", $"Composite glyph {glyphId}'s scale is truncated.");
                }

                a = d = scale;
            }
            else if ((flags & 0x0040) != 0) // WE_HAVE_AN_X_AND_Y_SCALE
            {
                if (!SfntPrimitives.TryReadF2Dot14(glyph, scaleOffset, out var xscale) || !SfntPrimitives.TryReadF2Dot14(glyph, scaleOffset + 2, out var yscale))
                {
                    throw new PlumePdfException("PLUME8012", $"Composite glyph {glyphId}'s x/y scale is truncated.");
                }

                a = xscale;
                d = yscale;
            }
            else if ((flags & 0x0080) != 0) // WE_HAVE_A_TWO_BY_TWO
            {
                if (!SfntPrimitives.TryReadF2Dot14(glyph, scaleOffset, out var m00) || !SfntPrimitives.TryReadF2Dot14(glyph, scaleOffset + 2, out var m01)
                    || !SfntPrimitives.TryReadF2Dot14(glyph, scaleOffset + 4, out var m10) || !SfntPrimitives.TryReadF2Dot14(glyph, scaleOffset + 6, out var m11))
                {
                    throw new PlumePdfException("PLUME8012", $"Composite glyph {glyphId}'s 2x2 matrix is truncated.");
                }

                a = m00;
                b = m01;
                c = m10;
                d = m11;
            }

            result.Add(new ComponentTransform(componentGlyphId, a, b, c, d, dx, dy));

            var scaleSize = (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            offset = scaleOffset + scaleSize;

            if ((flags & 0x0020) == 0) // MORE_COMPONENTS
            {
                break;
            }
        }

        return result;
    }
}
