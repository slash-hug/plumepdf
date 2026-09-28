namespace PlumePdf.Fonts;

/// <summary>
/// The writing direction a <see cref="ShapedRun"/>'s glyphs are already in visual (painting)
/// order for. This replaced the Phase 2 seam's undocumented left-to-right-only
/// assumption. Both shipped shapers (<see cref="SimpleShaper"/> and the complex-script
/// <c>ComplexShaper</c>) keep their glyph output in logical (input) order and therefore always
/// report <see cref="LeftToRight"/> — RTL visual reordering is the layout pass's job
/// (<c>TextLayouter.ShapeVisualLine</c>'s per-bidi-run reversal), not the shaper's. A shaper
/// that DID pre-reverse an RTL run into visual order would report <see cref="RightToLeft"/>;
/// the caller paints the glyphs in the order given either way, never reversing a run this
/// field already declares visual.
/// </summary>
internal enum TextDirection
{
    /// <summary>Glyphs paint left to right (Latin, Cyrillic, Greek, and Devanagari's base storage order before any bidi reordering).</summary>
    LeftToRight,

    /// <summary>Glyphs paint right to left (Arabic, Hebrew) — already reordered into visual order by the shaper.</summary>
    RightToLeft,
}

/// <summary>
/// Per-call shaping inputs beyond the text and font: a script hint for a shaper
/// that dispatches by script (the Arabic/Devanagari v1.0 tier), a base-direction hint,
/// and a lookup-application budget that guards a hostile caller-supplied font's contextual or
/// chaining lookup graph from driving unbounded work. This corrects an earlier
/// pinned-but-never-shipped <c>ShapingOptions</c> signature on the record.
/// </summary>
/// <param name="ScriptTag">
/// An OpenType script tag (e.g. <c>"arab"</c>, <c>"deva"</c>) to shape the run against, or
/// <see langword="null"/> to let the shaper infer one from the run's own script data.
/// <see cref="SimpleShaper"/> ignores this — it has exactly one code path.
/// </param>
/// <param name="Direction">The run's base writing direction. Defaults to <see cref="TextDirection.LeftToRight"/>; <see cref="SimpleShaper"/> ignores this and always produces <see cref="TextDirection.LeftToRight"/> output.</param>
/// <param name="MaxLookupApplications">
/// The maximum number of GSUB/GPOS lookup applications one <see cref="ILineShaper.Shape"/> call
/// may perform before refusing to continue with a coded error — a hostile font's
/// contextual/chaining lookup graph could otherwise drive unbounded work against attacker-chosen
/// input. Defaults to <see cref="PlumePdf.PdfOptions.MaxShapingLookupApplications"/>'s own
/// default (see <see cref="ShapingOptions.Default"/>).
/// </param>
internal readonly record struct ShapingOptions(
    string? ScriptTag = null,
    TextDirection Direction = TextDirection.LeftToRight,
    int MaxLookupApplications = 100_000)
{
    /// <summary>The options an unqualified shape call implies: no script hint, left-to-right, the default lookup budget — <see cref="PlumePdf.PdfOptions.Default"/>'s <see cref="PlumePdf.PdfOptions.MaxShapingLookupApplications"/> value.</summary>
    /// <remarks>
    /// Deliberately constructed with every argument named explicitly, not <c>new()</c>: for a
    /// <see langword="struct"/>, a bare <c>new ShapingOptions()</c> binds to the compiler's
    /// implicit public parameterless constructor (which zero-initializes every field) rather
    /// than to this primary constructor's declared defaults — <c>MaxLookupApplications</c>
    /// would silently come out <c>0</c>, not <c>100_000</c>, refusing every shape call.
    /// </remarks>
    public static ShapingOptions Default { get; } = new(ScriptTag: null, Direction: TextDirection.LeftToRight, MaxLookupApplications: 100_000);
}

/// <summary>One shaped glyph: the glyph to draw, its advance width (font units), its GPOS placement offset, which cluster of the input it belongs to, and which UTF-16 code unit of the input run it came from (for cursor/selection mapping and <c>/ToUnicode</c> construction).</summary>
/// <param name="GlyphId">The glyph-ID key (see <see cref="IFontMetrics"/> remarks) to draw.</param>
/// <param name="AdvanceWidth">The advance width to apply after this glyph, in font units (already includes any GPOS kerning adjustment).</param>
/// <param name="TextIndex">The index into the original input <see cref="ReadOnlySpan{Char}"/> this glyph's cluster starts at.</param>
/// <param name="CodepointCount">How many UTF-16 code units of the input this glyph's cluster consumed (2+ for a multi-character ligature; 1 for an ordinary BMP character; 2 for a surrogate pair mapping to one glyph).</param>
/// <param name="KernAdjustment">The GPOS kerning delta already folded into <paramref name="AdvanceWidth"/>, in font units — the paint pass must reproduce it as a <c>TJ</c> adjustment, or measured and painted widths diverge.</param>
/// <param name="XOffset">The glyph's horizontal placement offset from the pen position, in font units — nonzero only for GPOS mark-attachment/cursive positioning. <see cref="SimpleShaper"/> always produces 0.</param>
/// <param name="YOffset">The glyph's vertical placement offset from the pen position, in font units — nonzero only for GPOS mark-attachment/cursive positioning (mark-to-base, mark-to-ligature, mark-to-mark, cursive). <see cref="SimpleShaper"/> always produces 0.</param>
/// <param name="Cluster">
/// The cluster id this glyph belongs to. Glyphs sharing a <see cref="Cluster"/> value trace back
/// to the same source-text cluster; the mapping is N:M-capable — a complex shaper may fold
/// several codepoints into one glyph (an Arabic ligature) or spread one N:M syllable across
/// several reordered glyphs (a Devanagari conjunct).
/// <see cref="SimpleShaper"/> sets it to <see cref="TextIndex"/>, already a stable per-cluster id
/// for its strictly 1:N (ligature) output.
/// </param>
internal readonly record struct ShapedGlyph(int GlyphId, double AdvanceWidth, int TextIndex, int CodepointCount, double KernAdjustment = 0, double XOffset = 0, double YOffset = 0, int Cluster = 0);

/// <summary>The result of shaping one run of text against one font: its glyphs in visual (painting) order, plus the direction that order is in.</summary>
/// <param name="glyphs">The shaped glyphs, already in the order they should be drawn.</param>
/// <param name="direction">The direction <paramref name="glyphs"/> is already ordered for. Defaults to <see cref="TextDirection.LeftToRight"/>.</param>
internal sealed class ShapedRun(IReadOnlyList<ShapedGlyph> glyphs, TextDirection direction = TextDirection.LeftToRight)
{
    /// <summary>The shaped glyphs, in the order they should be drawn.</summary>
    public IReadOnlyList<ShapedGlyph> Glyphs { get; } = glyphs;

    /// <summary>
    /// The visual (painting) order the <see cref="Glyphs"/> array is already in
    /// — a statement about the array's ACTUAL order, never an echo of the requested
    /// <see cref="ShapingOptions.Direction"/>. Both shipped shapers emit logical-order glyphs
    /// and report <see cref="TextDirection.LeftToRight"/> (an RTL Hebrew or Arabic run included
    /// — its visual reversal happens downstream, in <c>TextLayouter.ShapeVisualLine</c>'s
    /// per-bidi-run paint reversal). The paint pass draws the glyphs as given regardless of
    /// value; it never reverses a run this field already declares
    /// <see cref="TextDirection.RightToLeft"/> itself.
    /// </summary>
    public TextDirection Direction { get; } = direction;
}

/// <summary>
/// Internal shaping seam (a staged-shaping decision): converts a
/// run of text plus a font into glyphs, applying whatever substitution/positioning that font's
/// shaper implementation supports. <see cref="SimpleShaper"/> is the Phase 2 implementation
/// (cmap mapping + GSUB ligatures + GPOS pair kerning for Latin/Cyrillic/Greek); a complex-script
/// shaper (Arabic/Indic) plugs into the same seam without touching callers
/// beyond the values it passes in <see cref="ShapingOptions"/>. Kept internal by that same
/// decision's continuation — only <see cref="PdfFont"/> is public.
/// </summary>
internal interface ILineShaper
{
    /// <summary>
    /// Shapes <paramref name="text"/> against <paramref name="font"/> per <paramref name="options"/>.
    /// Throws a coded <see cref="PlumePdfException"/> (<c>PLUME8009</c>) naming the offending
    /// codepoint and font when a character has no glyph in the font at all — Phase 2's fail-fast
    /// glyph-coverage policy, restated for complex-script shapers as
    /// post-shaping emitted-glyph validation rather than a per-input-codepoint check.
    /// </summary>
    ShapedRun Shape(ReadOnlySpan<char> text, IFontMetrics font, ShapingOptions options);
}
