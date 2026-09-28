namespace PlumePdf.Fonts.Shaping;

/// <summary>
/// The dispatching complex-script <see cref="ILineShaper"/>: itemizes
/// <c>text</c> into script runs (<see cref="ScriptSegmenter"/>), keeps <see cref="SimpleShaper"/>'s
/// cmap+GSUB-liga+GPOS-kern path as the fast path for Latin/Cyrillic/Greek/script-neutral text,
/// and dispatches Arabic runs to <see cref="ArabicShaper"/> and Devanagari runs to
/// <see cref="IndicShaper"/> — both driven against the font's own raw <c>GSUB</c>/<c>GPOS</c>
/// tables via <see cref="OpenTypeLayoutEngine"/>. A script outside the shipped
/// tier (v1.0 = Arabic + Devanagari) is a coded <c>PLUME8026</c> refusal — never
/// silently rendered as unjoined isolated forms, closing the standing gap identified in
/// the pre-6.5 <see cref="SimpleShaper"/> (which has no script awareness at all and would
/// otherwise render Arabic as reversed, unjoined tofu).
/// </summary>
/// <remarks>
/// This is the type that is swapped in at <c>TextLayouter</c>/<c>ManuscriptRenderer</c>'s
/// hardcoded <see cref="SimpleShaper"/> call sites, done together with the
/// bidi pass those call sites also need. It reports each glyph's real N:M
/// <see cref="ShapedGlyph.Cluster"/> and GPOS <see cref="ShapedGlyph.XOffset"/>/<see cref="ShapedGlyph.YOffset"/>
/// placement via the seam's amendment, narrowed down from the richer <see cref="GlyphBuffer"/>
/// each script shaper builds internally.
/// </remarks>
internal sealed class ComplexShaper : ILineShaper
{
    private readonly SimpleShaper _simpleShaper = new();

    /// <inheritdoc/>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME8009</c> a codepoint has no glyph in the font; <c>PLUME8024</c> the shaping
    /// work budget was exhausted; <c>PLUME8025</c> the font is missing the Arabic/Devanagari
    /// shaping capability the text needs (not a <see cref="TrueTypeFontProgram"/> at all, or
    /// missing the required GSUB script record); <c>PLUME8026</c> the text contains a script
    /// outside the shipped Arabic/Devanagari shaping tier.
    /// </exception>
    public ShapedRun Shape(ReadOnlySpan<char> text, IFontMetrics font, ShapingOptions options)
    {
        var runs = ScriptSegmenter.Segment(text);
        if (runs.TrueForAll(IsSimpleScript))
        {
            return _simpleShaper.Shape(text, font, options);
        }

        var budget = new ShapingBudget(options.MaxLookupApplications);
        var glyphs = new List<ShapedGlyph>();

        foreach (var run in runs)
        {
            var runText = text.Slice(run.Start, run.Length);
            switch (run.Script)
            {
                case ScriptTag.Common or ScriptTag.Latin or ScriptTag.Cyrillic or ScriptTag.Greek or ScriptTag.Han or ScriptTag.Hebrew:
                    AppendSimple(glyphs, _simpleShaper.Shape(runText, font, options), run.Start);
                    break;

                case ScriptTag.Arabic:
                    AppendComplex(glyphs, run, ArabicShaper.Shape(runText, RequireTrueType(font, "Arabic"), budget), font);
                    break;

                case ScriptTag.Devanagari:
                    AppendComplex(glyphs, run, IndicShaper.Shape(runText, RequireTrueType(font, "Devanagari"), budget), font);
                    break;

                default:
                    throw new PlumePdfException(
                        "PLUME8026",
                        $"Text at position {run.Start} uses a script outside PlumePDF's current shaping tier (v1.0: Arabic and Devanagari only; broader script coverage is a 1.x follow-on). " +
                        "PlumePDF refuses rather than rendering it as unjoined isolated glyphs — remove or replace this text.");
            }
        }

        // ShapedRun.Direction documents "the direction Glyphs is already ordered for" — neither
        // this method nor ArabicShaper/IndicShaper ever reverses the buffer's storage order
        // (RTL visual reordering is TextLayouter.ShapeVisualLine's job, via its own L2/
        // ReverseForPaint pass over a bidi level run); echoing options.Direction back here would
        // claim an Arabic run's glyphs are already right-to-left-ordered when they are still in
        // logical (left-to-right buffer) order, contradicting the field's own contract.
        return new ShapedRun(glyphs, PlumePdf.Fonts.TextDirection.LeftToRight);
    }

    private static bool IsSimpleScript(ScriptRun run) => run.Script is ScriptTag.Common or ScriptTag.Latin or ScriptTag.Cyrillic or ScriptTag.Greek or ScriptTag.Han or ScriptTag.Hebrew;

    private static TrueTypeFontProgram RequireTrueType(IFontMetrics font, string scriptName)
    {
        if (font is TrueTypeFontProgram ttf)
        {
            return ttf;
        }

        throw new PlumePdfException(
            "PLUME8025",
            $"Cannot shape {scriptName} text with font '{font.BaseFontName}': not an embedded TrueType/OpenType font (e.g. a Standard-14 metrics-only font has no glyph outlines or GSUB/GPOS tables at all) — {scriptName} shaping requires the font's own OpenType Layout tables.");
    }

    private static void AppendSimple(List<ShapedGlyph> glyphs, ShapedRun run, int runStart)
    {
        foreach (var g in run.Glyphs)
        {
            glyphs.Add(g with { TextIndex = g.TextIndex + runStart, Cluster = g.Cluster + runStart });
        }
    }

    /// <summary>
    /// Narrows one script run's richer <see cref="GlyphBuffer"/> shaping result down to the
    /// <see cref="ILineShaper"/> seam's <see cref="ShapedGlyph"/> list: each
    /// glyph's <see cref="ShapingGlyph.Cluster"/> becomes its real, run-offset
    /// <see cref="ShapedGlyph.Cluster"/> id (N:M-capable — several glyphs of a ligature or a
    /// reordered syllable legitimately share one), <see cref="ShapedGlyph.TextIndex"/>/<see cref="ShapedGlyph.CodepointCount"/>
    /// stay the cursor-adjacent first-glyph-carries-the-count projection <c>FontObjectBuilder</c>'s
    /// cluster-driven <c>/ToUnicode</c> builds from <see cref="ShapedGlyph.Cluster"/>
    /// instead, and GPOS <see cref="ShapingGlyph.XOffset"/>/<see cref="ShapingGlyph.YOffset"/>
    /// placement carries straight through for mark/cursive attachment. Every GPOS
    /// XAdvance delta the engine folded into the buffer's <see cref="ShapingGlyph.XAdvance"/>
    /// (Devanagari <c>dist</c>/<c>kern</c> pair adjustments, a mark's advance zeroed on
    /// attachment) is also surfaced as <see cref="ShapedGlyph.KernAdjustment"/> — the seam's
    /// contract (see that parameter's own doc) that lets the paint pass reproduce the delta as
    /// a <c>TJ</c> adjustment; leaving it 0 (as this method once did) made measured widths
    /// include the adjustment while the painted content stream silently dropped it.
    /// </summary>
    private static void AppendComplex(List<ShapedGlyph> glyphs, ScriptRun run, GlyphBuffer buffer, IFontMetrics font)
    {
        // The generated glyph's Cluster value is the run-local (0-based) UTF-16 code-unit
        // index of the first original codepoint it traces back to: a ligature or
        // conjunct merge collapses several original clusters into whichever glyph GSUB
        // substitution keeps — always the leftmost, per GlyphBuffer.Replace's own contract —
        // so every cluster value no longer present after shaping was absorbed into the nearest
        // surviving one before it. The gap between one surviving cluster and the next is
        // therefore exactly how many original UTF-16 code units that glyph's cluster consumed:
        // the real ShapedGlyph.CodepointCount a multi-codepoint ligature/conjunct (Arabic لا,
        // a Devanagari conjunct) needs for a correct — not silently 1-codepoint-truncated —
        // /ToUnicode entry.
        var boundaries = new SortedSet<int>();
        for (var i = 0; i < buffer.Count; i++)
        {
            boundaries.Add(buffer[i].Cluster);
        }

        var sortedBoundaries = boundaries.ToList();

        int SpanFor(int cluster)
        {
            var index = sortedBoundaries.BinarySearch(cluster);
            var nextBoundary = index + 1 < sortedBoundaries.Count ? sortedBoundaries[index + 1] : run.Length;
            return nextBoundary - cluster;
        }

        var seenClusters = new HashSet<int>();
        for (var i = 0; i < buffer.Count; i++)
        {
            var g = buffer[i];
            var cluster = run.Start + g.Cluster;
            var codepointCount = seenClusters.Add(g.Cluster) ? SpanFor(g.Cluster) : 0;
            var kernAdjustment = g.XAdvance - font.GetAdvanceWidth(g.GlyphId);
            glyphs.Add(new ShapedGlyph((int)g.GlyphId, g.XAdvance, cluster, codepointCount, KernAdjustment: kernAdjustment, XOffset: g.XOffset, YOffset: g.YOffset, Cluster: cluster));
        }
    }
}
