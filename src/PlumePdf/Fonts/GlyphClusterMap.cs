namespace PlumePdf.Fonts;

/// <summary>
/// The renderer→<c>FontObjectBuilder</c> contract for cluster-driven <c>/ToUnicode</c>
/// construction: accumulates, per embedded font, which Unicode codepoints each
/// emitted glyph id represents — derived from the shaper's actual <see cref="ShapedGlyph.Cluster"/>
/// grouping rather than an independent per-codepoint <c>cmap</c> lookup, which is what makes the
/// <c>fi</c>-ligature <c>/ToUnicode</c> gap (and every complex-script substitution/reordering
/// gap it would otherwise multiply) possible in the first place. <c>ManuscriptRenderer</c>'s
/// <c>DocumentBuilder</c> populates one instance per font usage via <see cref="Record"/> as
/// <c>TextLayouter.ShapeVisualLine</c> shapes each line (one call per bidi level run, against
/// that run's own shaped text — see its own remarks), then converts <see cref="GlyphIdToCodepoints"/>
/// into <see cref="GlyphCluster"/>s before calling
/// <see cref="FontObjectBuilder.BuildEmbeddedTrueType"/>.
/// </summary>
internal sealed class GlyphClusterMap
{
    private readonly Dictionary<int, string> _glyphIdToCodepoints = [];

    /// <summary>
    /// Folds one shaped run's glyphs into the map. Per the "cluster-first-glyph
    /// carries the codepoints, trailing glyphs map to nothing" rule: for every distinct
    /// <see cref="ShapedGlyph.Cluster"/> value in <paramref name="glyphs"/>, only the first
    /// glyph (in <paramref name="glyphs"/> order) belonging to it is recorded as carrying that
    /// cluster's <see cref="ShapedGlyph.TextIndex"/>/<see cref="ShapedGlyph.CodepointCount"/>
    /// slice of <paramref name="text"/> — every later glyph sharing the same cluster id (a
    /// reordered Devanagari syllable's non-representative glyphs, or a ligature's already-
    /// consumed trailing positions) is deliberately left unmapped, matching how a reader must
    /// interpret an N:M cluster's <c>/ToUnicode</c> entries. Safe to call repeatedly across
    /// every line/run that shares one font usage — a glyph id already recorded by an earlier
    /// call is never overwritten, so the map is stable regardless of call order.
    /// </summary>
    /// <param name="glyphs">One shaped run's glyphs, in the shaper's output (logical) order.</param>
    /// <param name="text">
    /// The run's SOURCE text — the characters the author wrote, which <paramref name="glyphs"/>'
    /// <see cref="ShapedGlyph.TextIndex"/>/<see cref="ShapedGlyph.CodepointCount"/> index into.
    /// When the painted text differs from the source per-character-reversibly — UAX#9 L4
    /// bracket mirroring substitutes the paired bracket's glyph on a right-to-left run —
    /// callers pass the source, never the substituted form: <c>/ToUnicode</c> answers "what
    /// characters does this glyph represent", so an RTL "(" must extract back as "(" even
    /// though the ")" glyph is what gets painted (<c>TextLayouter.ShapeVisualLine</c>'s
    /// <c>sourceRunText</c>, per the Phase 6.5 review's mirroring finding).
    /// </param>
    public void Record(IReadOnlyList<ShapedGlyph> glyphs, string text)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(text);

        var seenClustersInThisRun = new HashSet<int>();
        foreach (var glyph in glyphs)
        {
            if (!seenClustersInThisRun.Add(glyph.Cluster))
            {
                continue;
            }

            if (_glyphIdToCodepoints.ContainsKey(glyph.GlyphId))
            {
                continue;
            }

            _glyphIdToCodepoints[glyph.GlyphId] = text.Substring(glyph.TextIndex, glyph.CodepointCount);
        }
    }

    /// <summary>
    /// The glyph id → Unicode codepoints map accumulated so far, ready for
    /// <c>FontObjectBuilder</c>'s cluster-driven <c>/ToUnicode</c> construction. A glyph id with
    /// no entry here either was never used, or was a non-representative glyph inside a
    /// multi-glyph cluster — both cases correctly get no <c>bfchar</c> entry.
    /// </summary>
    public IReadOnlyDictionary<int, string> GlyphIdToCodepoints => _glyphIdToCodepoints;
}
