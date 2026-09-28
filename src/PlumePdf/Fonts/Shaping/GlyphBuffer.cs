using PlumePdf.Fonts.Tables;

namespace PlumePdf.Fonts.Shaping;

/// <summary>
/// One glyph inside a <see cref="GlyphBuffer"/> mid-shaping: its glyph ID, the source
/// <see cref="Cluster"/> it traces back to (N:M cluster tracking — several glyphs
/// can share a cluster, e.g. an Arabic ligature's remaining components, or a reordered Indic
/// matra keeps its base's cluster), its advance, and any GPOS placement offset accumulated so
/// far. <see cref="GdefClass"/>/<see cref="MarkAttachClass"/> are cached from <see cref="Tables.GdefTable"/>
/// at insertion time so <c>LookupFlag</c> skip checks (OpenType spec §5.2) don't re-look-up
/// GDEF on every scan step.
/// </summary>
internal struct ShapingGlyph
{
    public ushort GlyphId;
    public int Cluster;
    public double XAdvance;
    public double YAdvance;
    public double XOffset;
    public double YOffset;
    public GlyphClass GdefClass;
    public ushort MarkAttachClass;
}

/// <summary>
/// A mutable glyph string that <see cref="OpenTypeLayoutEngine"/> substitutes and positions
/// in place. Distinct from the public/near-public <see cref="ShapedGlyph"/> the
/// <see cref="ILineShaper"/> seam returns: <see cref="ShapedGlyph"/> is a flat per-glyph record
/// with no in-flight substitution bookkeeping, so <see cref="ComplexShaper"/> shapes into this
/// richer, mutable buffer first and then narrows the result down to <see cref="ShapedRun"/>'s
/// shape (<see cref="ComplexShaper"/>'s own <c>AppendComplex</c> carries every field — cluster,
/// <c>XOffset</c>/<c>YOffset</c> placement, and codepoint span — straight through), while
/// <see cref="ArabicShaper"/>/<see cref="IndicShaper"/> unit tests assert directly against the
/// buffer's full glyph ID + cluster + offset fidelity.
/// </summary>
internal sealed class GlyphBuffer
{
    private readonly List<ShapingGlyph> _glyphs;
    private readonly GdefTable _gdef;

    public GlyphBuffer(GdefTable gdef)
    {
        _gdef = gdef;
        _glyphs = [];
    }

    public int Count => _glyphs.Count;

    public ShapingGlyph this[int index]
    {
        get => _glyphs[index];
        set => _glyphs[index] = value;
    }

    /// <summary>A read-only view of the current glyph sequence, in buffer (visual-shaping) order.</summary>
    public IReadOnlyList<ShapingGlyph> Glyphs => _glyphs;

    /// <summary>Appends one glyph, classifying it against GDEF immediately.</summary>
    public void Add(ushort glyphId, int cluster, double xAdvance)
    {
        _glyphs.Add(Classify(glyphId, cluster, xAdvance));
    }

    /// <summary>Builds a buffer from a 1:1 glyph-ID/cluster sequence (the shaper's starting point before any GSUB substitution runs).</summary>
    public static GlyphBuffer FromGlyphs(IReadOnlyList<ushort> glyphIds, IReadOnlyList<int> clusters, IReadOnlyList<double> advances, GdefTable gdef)
    {
        var buffer = new GlyphBuffer(gdef);
        for (var i = 0; i < glyphIds.Count; i++)
        {
            buffer.Add(glyphIds[i], clusters[i], advances[i]);
        }

        return buffer;
    }

    /// <summary>
    /// Replaces the <paramref name="length"/> glyphs starting at <paramref name="start"/> with
    /// <paramref name="replacementGlyphIds"/> (GSUB single/multiple/alternate/ligature
    /// substitution — lengths 1:1, 1:N, 1:1, and N:1 respectively). Every inserted glyph's
    /// advance comes from <paramref name="getAdvance"/> (the font's own <c>hmtx</c>, via
    /// <see cref="IFontMetrics.GetAdvanceWidth"/>); its cluster is <paramref name="clusters"/>[i]
    /// if given, else the replaced range's leftmost original cluster (correct for ligature
    /// merge and single/alternate substitution; multiple substitution passes explicit clusters
    /// when a feature legitimately needs to split one cluster into several, which Phase 6.5's
    /// tier — decomposition is not a feature either shaper uses — never does).
    /// </summary>
    public void Replace(int start, int length, ReadOnlySpan<ushort> replacementGlyphIds, Func<ushort, double> getAdvance, IReadOnlyList<int>? clusters = null)
    {
        var baseCluster = _glyphs[start].Cluster;
        var items = new ShapingGlyph[replacementGlyphIds.Length];
        for (var i = 0; i < replacementGlyphIds.Length; i++)
        {
            var gid = replacementGlyphIds[i];
            items[i] = Classify(gid, clusters is not null ? clusters[i] : baseCluster, getAdvance(gid));
        }

        _glyphs.RemoveRange(start, length);
        _glyphs.InsertRange(start, items);
    }

    /// <summary>GPOS placement (XPlacement/YPlacement, ValueRecord or anchor-to-anchor math): adds to whatever offset the glyph already carries, since multiple GPOS lookups (e.g. mark-to-base then mark-to-mark for a stacked diacritic) can each contribute.</summary>
    public void AddPosition(int index, double xOffset, double yOffset)
    {
        var g = _glyphs[index];
        g.XOffset += xOffset;
        g.YOffset += yOffset;
        _glyphs[index] = g;
    }

    /// <summary>GPOS advance adjustment (XAdvance/YAdvance ValueRecord fields, or a mark's advance zeroed out for zero-advance mark attachment).</summary>
    public void AdjustAdvance(int index, double dxAdvance, double dyAdvance)
    {
        var g = _glyphs[index];
        g.XAdvance += dxAdvance;
        g.YAdvance += dyAdvance;
        _glyphs[index] = g;
    }

    public void SetAdvance(int index, double xAdvance)
    {
        var g = _glyphs[index];
        g.XAdvance = xAdvance;
        _glyphs[index] = g;
    }

    /// <summary>
    /// Moves the glyph at <paramref name="fromIndex"/> to just before <paramref name="toIndex"/>
    /// (pre-removal indexing), preserving its cluster — the Indic reordering primitive
    /// (reph placement, pre-base matra reorder) that keeps N:M cluster maintenance correct: the
    /// glyph's visual position changes but its <see cref="ShapingGlyph.Cluster"/> still names
    /// the syllable it belongs to.
    /// </summary>
    public void MoveGlyph(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex)
        {
            return;
        }

        var g = _glyphs[fromIndex];
        _glyphs.RemoveAt(fromIndex);
        var insertAt = toIndex > fromIndex ? toIndex - 1 : toIndex;
        _glyphs.Insert(insertAt, g);
    }

    public ushort[] GlyphIds()
    {
        var result = new ushort[_glyphs.Count];
        for (var i = 0; i < _glyphs.Count; i++)
        {
            result[i] = _glyphs[i].GlyphId;
        }

        return result;
    }

    /// <summary>Re-derives every glyph's cached GDEF classification — call after directly mutating <see cref="this[int]"/> with a different <see cref="ShapingGlyph.GlyphId"/> (rare; substitution should normally go through <see cref="Replace"/>, which classifies inline).</summary>
    public void Reclassify(int index)
    {
        var g = _glyphs[index];
        g.GdefClass = _gdef.GetGlyphClass(g.GlyphId);
        g.MarkAttachClass = _gdef.GetMarkAttachClass(g.GlyphId);
        _glyphs[index] = g;
    }

    private ShapingGlyph Classify(ushort glyphId, int cluster, double xAdvance) => new()
    {
        GlyphId = glyphId,
        Cluster = cluster,
        XAdvance = xAdvance,
        GdefClass = _gdef.GetGlyphClass(glyphId),
        MarkAttachClass = _gdef.GetMarkAttachClass(glyphId),
    };
}
