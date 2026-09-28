using System.Text;
using PlumePdf.Elements;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using PlumePdf.Fonts.Standard14;
// Phase 6.5: `TextDirection` exists in two namespaces this file needs both of —
// the public, Auto-capable `PlumePdf.Elements.TextDirection` (paragraph base direction, this
// file's overwhelming common case: ResolveDirection/ShapeVisualLine/MeasuredText all speak it)
// and the internal, resolved-only `PlumePdf.Fonts.TextDirection` (the ILineShaper seam's
// ShapingOptions/ShapedRun). The alias makes every bare `TextDirection` below bind to the
// Elements one; the Fonts one is named fully qualified at the few call sites that need it
// (ShapeVisualLine's per-run ShapingOptions construction).
using TextDirection = PlumePdf.Elements.TextDirection;

namespace PlumePdf.Layout;

/// <summary>
/// Breaks a <see cref="Elements.Text"/> element's content into wrapped lines that fit a given
/// width, and measures glyph advances to do it — through the real font-metrics seam
/// (<see cref="IFontMetrics"/>/<see cref="ILineShaper"/>), never an invented heuristic: the
/// same <see cref="ILineShaper"/> shaping this type uses to measure is what
/// <see cref="ManuscriptRenderer"/> uses to paint, so wrapped lines never overflow the width
/// they were measured against.
/// </summary>
internal static class TextLayouter
{
    // Phase 6.5/D-4/A-13: the swap site, in step with ManuscriptRenderer's own `Shaper` field —
    // both moved to ComplexShaper in the same commit (measure/paint symmetry). ComplexShaper's
    // Latin/Cyrillic/Greek fast path delegates straight back to SimpleShaper, so every
    // pre-Phase-6.5 document shapes through the exact same code it always did.
    private static readonly ILineShaper Shaper = new ComplexShaper();

    // The plain-path word measurer (see WrapPlainLeftToRightParagraph's remarks): used only on
    // text IsPlainLeftToRightParagraph has proven all-simple-script, where ComplexShaper.Shape
    // would delegate to exactly this shaper anyway — never a way to bypass complex-script
    // dispatch.
    private static readonly ILineShaper PlainShaper = new SimpleShaper();
    private static readonly IFontMetrics RegularMetrics = ResolveStandard14("Helvetica");
    private static readonly IFontMetrics BoldMetrics = ResolveStandard14("Helvetica-Bold");

    /// <summary>The advance width, in points, of a single character at the given font size and weight, measured against the Standard-14 Helvetica/Helvetica-Bold metrics.</summary>
    /// <exception cref="PlumePdfException"><c>PLUME8009</c> — <paramref name="c"/> has no glyph in the resolved font (Standard-14's WinAnsiEncoding, e.g. most non-Latin script).</exception>
    public static double MeasureChar(char c, double fontSize, bool bold) => MeasureText(c.ToString(), fontSize, bold);

    /// <summary>The advance width, in points, of an entire (unwrapped) string, measured against the Standard-14 Helvetica/Helvetica-Bold metrics.</summary>
    /// <exception cref="PlumePdfException"><c>PLUME8009</c> — a character in <paramref name="text"/> has no glyph in the resolved font.</exception>
    public static double MeasureText(string text, double fontSize, bool bold) => MeasureText(text, fontSize, bold ? BoldMetrics : RegularMetrics);

    /// <summary>The advance width, in points, of an entire (unwrapped) string, measured against <paramref name="metrics"/> via <see cref="ILineShaper"/> — the same shaping <see cref="ManuscriptRenderer"/> paints with.</summary>
    /// <exception cref="PlumePdfException"><c>PLUME8009</c> — a character in <paramref name="text"/> has no glyph in <paramref name="metrics"/>.</exception>
    internal static double MeasureText(string text, double fontSize, IFontMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(metrics);

        if (text.Length == 0)
        {
            return 0;
        }

        var shaped = Shaper.Shape(text, metrics, BuildShapingOptions(metrics, TextDirection.LeftToRight));
        double totalUnits = 0;
        foreach (var glyph in shaped.Glyphs)
        {
            totalUnits += glyph.AdvanceWidth;
        }

        return totalUnits * fontSize / metrics.UnitsPerEm;
    }

    /// <summary>
    /// Builds the <see cref="ShapingOptions"/> one <see cref="ILineShaper.Shape"/> call needs
    /// (Phase 6.5/D-4/D-14): <paramref name="direction"/> converted to the internal shaping-seam
    /// <see cref="PlumePdf.Fonts.TextDirection"/> (never <see cref="TextDirection.Auto"/> here —
    /// callers resolve that first, via <see cref="ResolveDirection"/>), and the lookup-application
    /// budget read from <paramref name="metrics"/>'s own <see cref="TrueTypeFontProgram.Limits"/>
    /// when it is an embedded font (the same <c>PdfOptions.MaxShapingLookupApplications</c> the
    /// font itself was parsed with — <see cref="FontReadLimits.From"/>), or
    /// <see cref="ShapingOptions.Default"/>'s budget for a Standard-14 metrics-only font, which
    /// carries no <see cref="FontReadLimits"/> of its own.
    /// </summary>
    private static ShapingOptions BuildShapingOptions(IFontMetrics metrics, TextDirection direction)
    {
        var fontsDirection = direction == TextDirection.RightToLeft ? PlumePdf.Fonts.TextDirection.RightToLeft : PlumePdf.Fonts.TextDirection.LeftToRight;
        var maxLookupApplications = metrics is TrueTypeFontProgram ttf ? ttf.Limits.MaxShapingLookupApplications : ShapingOptions.Default.MaxLookupApplications;
        return ShapingOptions.Default with { Direction = fontsDirection, MaxLookupApplications = maxLookupApplications };
    }

    /// <summary>
    /// Greedily wraps <paramref name="text"/> into lines no wider than <paramref name="availableWidth"/>,
    /// breaking at whitespace where possible, measured against the Standard-14 Helvetica/Helvetica-Bold
    /// metrics. A single word wider than <paramref name="availableWidth"/> is hard-broken character
    /// by character rather than overflowing or looping forever — a long unbroken token (a serial
    /// number, a URL) is ordinary real-world content, not a degenerate manuscript, so this does
    /// not throw for that reason (though an unencodable character within it still throws PLUME8009).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="availableWidth"/> is not positive.</exception>
    /// <exception cref="PlumePdfException"><c>PLUME8009</c> — a character in <paramref name="text"/> has no glyph in the resolved font.</exception>
    public static IReadOnlyList<string> WrapLines(string text, double fontSize, bool bold, double availableWidth) =>
        WrapLines(text, fontSize, bold ? BoldMetrics : RegularMetrics, availableWidth);

    /// <summary>Same as <see cref="WrapLines(string,double,bool,double)"/>, measured against <paramref name="metrics"/> — the overload <see cref="Layout.LayoutEngine"/> uses once a <see cref="Elements.Text.Font"/> is resolved.</summary>
    /// <param name="text">The text to wrap.</param>
    /// <param name="fontSize">The font size to measure at.</param>
    /// <param name="metrics">The font to measure against.</param>
    /// <param name="availableWidth">The maximum width, in points, a line may occupy.</param>
    /// <param name="direction">
    /// The paragraph's resolved base direction. Defaults to
    /// <see cref="TextDirection.LeftToRight"/>. A plain left-to-right simple-script paragraph
    /// (the pre-Phase-6.5 universe — see <see cref="IsPlainLeftToRightParagraph"/>) wraps by
    /// the exact pre-Phase-6.5 per-word width accumulation, each word shaped once; every other
    /// paragraph makes each wrap decision against <see cref="ShapeVisualLine"/> — the exact
    /// bidi-aware shaping call <see cref="Layout.LayoutEngine"/>'s own final-width measurement
    /// and <see cref="Layout.ManuscriptRenderer"/>'s painting both use for the finished line —
    /// so a wrapped line's measured width can never diverge from what gets painted, even for a
    /// line mixing Arabic/Hebrew and Latin/digit runs.
    /// </param>
    internal static IReadOnlyList<string> WrapLines(string text, double fontSize, IFontMetrics metrics, double availableWidth, TextDirection direction = TextDirection.LeftToRight)
    {
        var measured = WrapLinesMeasured(text, fontSize, metrics, availableWidth, direction);
        var lines = new List<string>(measured.Count);
        foreach (var line in measured)
        {
            lines.Add(line.Text);
        }

        return lines;
    }

    /// <summary>
    /// Same as <see cref="WrapLines(string,double,IFontMetrics,double,TextDirection)"/> but
    /// returns each wrapped line together with the width it was measured at during wrapping,
    /// so <see cref="Layout.LayoutEngine"/> never re-shapes a finished line just to learn a
    /// width the wrap loop already computed (the Phase 6.5 review's text-layout-throughput
    /// finding). Each returned <see cref="WrappedLine.Width"/> is in points, computed exactly
    /// as the wrap decision that emitted the line computed it: per-word accumulation on the
    /// plain left-to-right path, a <see cref="ShapeVisualLine"/> measurement of the emitted
    /// line's exact text on the bidi path.
    /// </summary>
    internal static IReadOnlyList<WrappedLine> WrapLinesMeasured(string text, double fontSize, IFontMetrics metrics, double availableWidth, TextDirection direction = TextDirection.LeftToRight)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableWidth);

        var lines = new List<WrappedLine>();
        if (text.Length == 0)
        {
            lines.Add(new WrappedLine(string.Empty, 0));
            return lines;
        }

        foreach (var paragraph in text.Split('\n'))
        {
            WrapParagraph(paragraph, fontSize, metrics, availableWidth, direction, lines);
        }

        return lines;
    }

    /// <summary>The visual (painted) width, in points, of <paramref name="text"/> shaped exactly as <see cref="ShapeVisualLine"/> would paint it — the one width computation every wrap decision below is made against, so wrap-time and paint-time measurement can never diverge.</summary>
    private static double MeasureVisualWidth(string text, double fontSize, IFontMetrics metrics, TextDirection direction) =>
        text.Length == 0 ? 0 : ShapeVisualLine(text, metrics, direction).Width * fontSize / metrics.UnitsPerEm;

    private static void WrapParagraph(string paragraph, double fontSize, IFontMetrics metrics, double availableWidth, TextDirection direction, List<WrappedLine> lines)
    {
        if (paragraph.Length == 0)
        {
            lines.Add(new WrappedLine(string.Empty, 0));
            return;
        }

        // The Phase 6.5 review's text-layout-throughput finding: the classification below runs
        // ONCE per paragraph (one bidi-class scan + one script-segmentation pass), never per
        // candidate line, and routes the pre-Phase-6.5 universe — plain left-to-right
        // simple-script text, i.e. every ordinary Latin/Cyrillic/Greek document — onto the
        // exact per-word width-accumulation wrap the pre-6.5 TextLayouter shipped (each word
        // shaped once, widths summed), instead of re-shaping the whole accumulated line (with
        // a fresh BidiAlgorithm.Analyze) for every candidate word.
        if (direction == TextDirection.LeftToRight && IsPlainLeftToRightParagraph(paragraph))
        {
            WrapPlainLeftToRightParagraph(paragraph, fontSize, metrics, availableWidth, lines);
            return;
        }

        WrapBidiParagraph(paragraph, fontSize, metrics, availableWidth, direction, lines);
    }

    /// <summary>
    /// Whether <paramref name="paragraph"/> is guaranteed to resolve to a single left-to-right
    /// level run under a left-to-right base direction AND to shape space-separably, making
    /// per-word width accumulation exact — the fast-wrap eligibility check (run once per
    /// paragraph). Two conditions, each with a precise justification:
    /// (1) every UTF-16 code unit's Bidi_Class is in {L, EN, ES, ET, CS, NSM, WS, ON} — with no
    /// R/AL/AN present and sos = L (paragraph level 0), UAX#9's W and N rules resolve every one
    /// of these to L (W5/W6/W7, N1/N2 with all-L strong context) and I1 leaves the level at 0,
    /// so <see cref="ShapeVisualLine"/> would produce exactly one left-to-right run with no L4
    /// mirroring and nothing dropped by <see cref="BuildRunText"/> (explicit
    /// directional-formatting characters and BN are all outside the set — isolate-containing
    /// text therefore still reaches <see cref="ShapeVisualLine"/>'s <see cref="RefuseIsolates"/>
    /// via the bidi path, preserving the PLUME9013 refusal);
    /// (2) every script run is one <see cref="ComplexShaper"/> routes to <see cref="SimpleShaper"/>
    /// (Common/Latin/Cyrillic/Greek/Han/Hebrew — Hebrew is moot here, its characters are class R),
    /// whose only lookups are adjacent-glyph ligature and pair kerning, so summing per-word
    /// shaped widths plus space advances equals shaping the whole line except for a font that
    /// ligates or kerns against the space glyph itself — the identical additivity assumption
    /// the pre-Phase-6.5 TextLayouter shipped for every document it could render. Arabic and
    /// Devanagari (whole-run contextual shaping) are excluded by (1)/(2) respectively and take
    /// the conservative re-shape path (<see cref="WrapBidiParagraph"/>).
    /// </summary>
    private static bool IsPlainLeftToRightParagraph(string paragraph)
    {
        if (!IsPlainLeftToRightLine(paragraph))
        {
            return false;
        }

        foreach (var run in ScriptSegmenter.Segment(paragraph))
        {
            if (run.Script is not (ScriptTag.Common or ScriptTag.Latin or ScriptTag.Cyrillic or ScriptTag.Greek or ScriptTag.Han or ScriptTag.Hebrew))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether every UTF-16 code unit of <paramref name="line"/> has a Bidi_Class in
    /// {L, EN, ES, ET, CS, NSM, WS, ON} — <see cref="IsPlainLeftToRightParagraph"/>'s condition
    /// (1), reused by <see cref="ShapeVisualLine"/>'s own fast path: with no R/AL/AN present
    /// and a level-0 paragraph (sos = L), UAX#9's W rules (W5-W7) and N rules (N1/N2, all-L
    /// strong context) resolve every one of these classes to L and I1 leaves every level at 0,
    /// so the full <see cref="BidiAlgorithm.Analyze"/> is guaranteed to produce exactly one
    /// left-to-right run with no L4 mirroring — and every explicit directional-formatting
    /// class (LRE/RLE/LRO/RLO/PDF, LRI/RLI/FSI/PDI) and BN is outside the set, so nothing
    /// <see cref="BuildRunText"/> would drop (or <see cref="RefuseIsolates"/> would refuse)
    /// can slip through this fast path.
    /// </summary>
    private static bool IsPlainLeftToRightLine(string line)
    {
        foreach (var ch in line)
        {
            if (BidiAlgorithm.GetBidiClass(ch) is not (BidiClass.L or BidiClass.EN or BidiClass.ES or BidiClass.ET or BidiClass.CS or BidiClass.NSM or BidiClass.WS or BidiClass.ON))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The pre-Phase-6.5 wrap loop, restored verbatim for the paragraphs it was always exact
    /// for (<see cref="IsPlainLeftToRightParagraph"/>): each word is shaped once
    /// (<see cref="MeasureText(string,double,IFontMetrics)"/> — for a single-run left-to-right
    /// line this is exactly what <see cref="ShapeVisualLine"/> would measure), the joining
    /// space once, and candidate-line widths are accumulated sums — O(paragraph length) total
    /// shaping work instead of re-shaping the accumulated line per candidate word. The only
    /// change from the pre-6.5 loop is the hard-break unit: grapheme clusters
    /// (<see cref="EnumerateGraphemeClusters"/>) instead of runes, so a base+combining-mark
    /// sequence is never split.
    /// </summary>
    private static void WrapPlainLeftToRightParagraph(string paragraph, double fontSize, IFontMetrics metrics, double availableWidth, List<WrappedLine> lines)
    {
        var words = paragraph.Split(' ');
        var current = new StringBuilder();
        double currentWidth = 0;

        // IsPlainLeftToRightParagraph already proved every script run simple — and any
        // substring of an all-simple-script paragraph is itself all-simple-script — so shaping
        // each word through SimpleShaper directly is exactly what ComplexShaper.Shape would do
        // for it (its all-simple case delegates the whole text to SimpleShaper), minus a
        // redundant per-word ScriptSegmenter pass the paragraph-level check has already
        // answered. ManuscriptRenderer's paint of the finished line still goes through the
        // dispatching Shaper (ShapeVisualLine) — identical glyphs by the same delegation.
        var options = BuildShapingOptions(metrics, TextDirection.LeftToRight);
        var spaceWidth = MeasurePlainWidth(" ", fontSize, metrics, options);

        foreach (var word in words)
        {
            var wordWidth = MeasurePlainWidth(word, fontSize, metrics, options);
            var gap = current.Length > 0 ? spaceWidth : 0;

            if (current.Length > 0 && currentWidth + gap + wordWidth > availableWidth)
            {
                lines.Add(new WrappedLine(current.ToString(), currentWidth));
                current.Clear();
                currentWidth = 0;
            }

            if (wordWidth > availableWidth)
            {
                // A single word too wide to ever fit a line on its own: hard-break it.
                if (current.Length > 0)
                {
                    lines.Add(new WrappedLine(current.ToString(), currentWidth));
                    current.Clear();
                    currentWidth = 0;
                }

                // Hard-break at grapheme-cluster boundaries, never inside one. See
                // EnumerateGraphemeClusters' own remarks.
                foreach (var cluster in EnumerateGraphemeClusters(word))
                {
                    var clusterWidth = MeasurePlainWidth(cluster, fontSize, metrics, options);
                    if (current.Length > 0 && currentWidth + clusterWidth > availableWidth)
                    {
                        lines.Add(new WrappedLine(current.ToString(), currentWidth));
                        current.Clear();
                        currentWidth = 0;
                    }

                    current.Append(cluster);
                    currentWidth += clusterWidth;
                }

                continue;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
                currentWidth += spaceWidth;
            }

            current.Append(word);
            currentWidth += wordWidth;
        }

        lines.Add(new WrappedLine(current.ToString(), currentWidth));
    }

    /// <summary>
    /// <see cref="WrapPlainLeftToRightParagraph"/>'s word/cluster measurement: one direct
    /// <see cref="SimpleShaper"/> shape (see the caller's remarks for why that is provably the
    /// same shaping <see cref="Shaper"/> would perform on this text), advances summed and
    /// scaled to points exactly as <see cref="MeasureText(string,double,IFontMetrics)"/> does.
    /// </summary>
    private static double MeasurePlainWidth(string text, double fontSize, IFontMetrics metrics, ShapingOptions options)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        double totalUnits = 0;
        foreach (var glyph in PlainShaper.Shape(text, metrics, options).Glyphs)
        {
            totalUnits += glyph.AdvanceWidth;
        }

        return totalUnits * fontSize / metrics.UnitsPerEm;
    }

    /// <summary>
    /// The conservative wrap loop for every paragraph <see cref="IsPlainLeftToRightParagraph"/>
    /// cannot prove single-run/space-separable — right-to-left base direction, any R/AL/AN or
    /// explicit directional-formatting content, or a complex script (Arabic/Devanagari), where
    /// a candidate line's width genuinely depends on cross-word shaping context (bidi run
    /// splits, L4 mirroring, whole-run GSUB/GPOS lookups). Each candidate is measured by
    /// <see cref="ShapeVisualLine"/> on its exact text (correctness over speed — the Phase 6.5
    /// review ruling: complex scripts re-shape conservatively; only the provably-additive plain
    /// path accumulates), and each emitted line's tracked width is the
    /// <see cref="ShapeVisualLine"/> measurement of exactly the text emitted.
    /// </summary>
    private static void WrapBidiParagraph(string paragraph, double fontSize, IFontMetrics metrics, double availableWidth, TextDirection direction, List<WrappedLine> lines)
    {
        var words = paragraph.Split(' ');
        var current = new StringBuilder();
        double currentWidth = 0;

        foreach (var word in words)
        {
            var wordWidth = MeasureVisualWidth(word, fontSize, metrics, direction);
            var candidateWidth = current.Length == 0 ? wordWidth : MeasureVisualWidth($"{current} {word}", fontSize, metrics, direction);

            if (current.Length > 0 && candidateWidth > availableWidth)
            {
                lines.Add(new WrappedLine(current.ToString(), currentWidth));
                current.Clear();
                currentWidth = 0;
                candidateWidth = wordWidth;
            }

            if (wordWidth > availableWidth)
            {
                // A single word too wide to ever fit a line on its own: hard-break it.
                if (current.Length > 0)
                {
                    lines.Add(new WrappedLine(current.ToString(), currentWidth));
                    current.Clear();
                    currentWidth = 0;
                }

                // Hard-break at grapheme-cluster boundaries, never inside one — a base
                // character followed by combining marks (Arabic harakat, Devanagari matras,
                // Hebrew points) must never be split across two lines, or shaping breaks
                // silently. See EnumerateGraphemeClusters' own remarks for how "cluster" is
                // approximated here against the generated UAX#29 tables.
                foreach (var cluster in EnumerateGraphemeClusters(word))
                {
                    var candidateClusterWidth = current.Length == 0 ? MeasureVisualWidth(cluster, fontSize, metrics, direction) : MeasureVisualWidth(current.ToString() + cluster, fontSize, metrics, direction);
                    if (current.Length > 0 && candidateClusterWidth > availableWidth)
                    {
                        lines.Add(new WrappedLine(current.ToString(), currentWidth));
                        current.Clear();
                        candidateClusterWidth = MeasureVisualWidth(cluster, fontSize, metrics, direction);
                    }

                    current.Append(cluster);
                    currentWidth = candidateClusterWidth;
                }

                continue;
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
            currentWidth = candidateWidth;
        }

        lines.Add(new WrappedLine(current.ToString(), currentWidth));
    }

    /// <summary>
    /// Groups <paramref name="word"/>'s runes into grapheme-cluster-safe units: a base rune plus
    /// every immediately-following codepoint whose Unicode <c>Grapheme_Cluster_Break</c>
    /// property continues a cluster rather than starting a new one (<c>Extend</c>/
    /// <c>SpacingMark</c>/<c>ZWJ</c> — nonspacing/spacing-combining marks and zero-width
    /// joiners), so a hard break — the only place <see cref="WrapParagraph"/> ever splits inside
    /// a word — never lands between a base character and a mark that shapes onto it. This is a
    /// documented interim approximation of full UAX#29 extended grapheme clusters (Regional
    /// Indicator pairs and Indic virama/conjunct rules are not modeled): it covers exactly the
    /// failure mode of silently splitting a base+combining-mark sequence — using the
    /// pinned, generated <c>UnicodeShapingData.g.cs</c> <c>Grapheme_Cluster_Break</c> table,
    /// not <see cref="System.Globalization.CharUnicodeInfo"/>/<see cref="System.Text.Rune.GetUnicodeCategory(System.Text.Rune)"/>
    /// — the host runtime's own UCD version, which would let this hard-break decision (and
    /// hence wrapped line content) silently shift across .NET patch levels, breaking
    /// <see cref="PlumePdf.PdfOptions.Deterministic"/> byte-identity.
    /// </summary>
    private static IEnumerable<string> EnumerateGraphemeClusters(string word)
    {
        var current = new StringBuilder();
        foreach (var rune in word.EnumerateRunes())
        {
            if (current.Length > 0 && !ContinuesCluster(rune))
            {
                yield return current.ToString();
                current.Clear();
            }

            current.Append(rune.ToString());
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool ContinuesCluster(System.Text.Rune rune)
    {
        var breakProperty = UnicodeShapingData.GetGraphemeClusterBreak(rune.Value);
        return breakProperty is GraphemeClusterBreak.Extend or GraphemeClusterBreak.SpacingMark or GraphemeClusterBreak.ZWJ;
    }

    /// <summary>
    /// Resolves a <see cref="Elements.Text"/> element's paragraph base direction once (never
    /// per wrapped line — UAX#9 P2/P3 is a paragraph-scoped decision): <paramref name="requested"/>
    /// verbatim when it is not <see cref="TextDirection.Auto"/>, otherwise the first-strong
    /// heuristic run against <paramref name="content"/> — the element's full, unwrapped
    /// content, so every wrapped line of the same paragraph resolves to the same direction.
    /// </summary>
    public static TextDirection ResolveDirection(string content, TextDirection requested)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (requested != TextDirection.Auto)
        {
            return requested;
        }

        return BidiAlgorithm.FirstStrongParagraphLevel(content) == 1 ? TextDirection.RightToLeft : TextDirection.LeftToRight;
    }

    /// <summary>
    /// Shapes one already-wrapped line into its final visual-order glyphs and total width:
    /// runs UAX#9 (<see cref="BidiAlgorithm"/>) against <paramref name="baseDirection"/>
    /// (resolved via <see cref="ResolveDirection"/> — never <see cref="TextDirection.Auto"/>
    /// here), shapes each resulting level run once in its own logical order (preserving
    /// GSUB/GPOS context exactly as a plain <see cref="ILineShaper.Shape"/> call would), then
    /// reverses a right-to-left run's glyphs for painting and assembles every run in L2 visual
    /// order. <see cref="Layout.LayoutEngine"/> and <see cref="ManuscriptRenderer"/> both call
    /// this same method for the same line — measured and painted widths are therefore always
    /// equal by construction, not by coincidence. For a purely left-to-right line (the only
    /// case any pre-Phase-6.5 document produces) this reduces to exactly one run shaped exactly
    /// once, byte-identical to the pre-Phase-6.5 single <see cref="ILineShaper.Shape"/> call —
    /// and, per the Phase 6.5 review's throughput finding, that reduction is taken literally: a
    /// line whose every code unit's Bidi_Class is in the provably-level-0 set (see
    /// <see cref="IsPlainLeftToRightLine"/>) skips the full UAX#9 analysis (which the analysis
    /// could only confirm) and issues the single <see cref="ILineShaper.Shape"/> call directly,
    /// one O(n) class scan replacing the multi-pass, multi-allocation
    /// <see cref="BidiAlgorithm.Analyze"/> on the path every ordinary Latin document paints
    /// through.
    /// </summary>
    /// <param name="line">The already-wrapped line to shape.</param>
    /// <param name="metrics">The font to shape against.</param>
    /// <param name="baseDirection">The line's resolved paragraph base direction (never <see cref="TextDirection.Auto"/>).</param>
    /// <param name="clusterMap">
    /// When supplied (cluster-driven <c>/ToUnicode</c> wiring — the <see cref="Fonts.FontObjectBuilder"/>
    /// consumer needs a real caller, not just its own unit tests), each level run's shaped
    /// glyphs are folded into it against that run's own SOURCE text (unmirrored — L4 bracket
    /// mirroring picks which glyph is painted, never which character the glyph represents, so
    /// <c>/ToUnicode</c> must round-trip the character the author wrote) — <em>before</em> L2's
    /// right-to-left reversal, since <see cref="ShapedGlyph.TextIndex"/> indexes into the run's
    /// own (per-run, not full-line) text regardless of paint order. <see langword="null"/> (the
    /// default) skips this — <see cref="LayoutEngine"/>'s measure-only call site has no font
    /// object to build a <c>/ToUnicode</c> CMap for and passes nothing.
    /// </param>
    /// <exception cref="PlumePdfException"><c>PLUME8009</c> — a character in <paramref name="line"/> has no glyph in <paramref name="metrics"/>.</exception>
    internal static VisualLine ShapeVisualLine(string line, IFontMetrics metrics, TextDirection baseDirection, GlyphClusterMap? clusterMap = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(metrics);

        if (line.Length == 0)
        {
            return new VisualLine(0, []);
        }

        if (baseDirection == TextDirection.LeftToRight && IsPlainLeftToRightLine(line))
        {
            // Provably a single level-0 run (see IsPlainLeftToRightLine's justification): the
            // full analysis below could only reproduce this exact outcome — one run, no
            // mirroring, nothing dropped, no reorder — so shape the line directly. Isolate
            // initiators/terminators are outside the plain set, so RefuseIsolates still gates
            // every line that could contain one.
            var glyphs = Shaper.Shape(line, metrics, BuildShapingOptions(metrics, TextDirection.LeftToRight)).Glyphs;
            clusterMap?.Record(glyphs, line);
            double width = 0;
            foreach (var glyph in glyphs)
            {
                width += glyph.AdvanceWidth;
            }

            return new VisualLine(width, glyphs);
        }

        RefuseIsolates(line);

        var analysis = BidiAlgorithm.Analyze(line, baseDirection);
        var runs = analysis.GetLevelRuns();
        var shapedPerRun = new List<IReadOnlyList<ShapedGlyph>>(runs.Count);

        foreach (var run in runs)
        {
            var runText = BuildRunText(line, run, out var sourceRunText);
            var runDirection = run.IsRightToLeft ? TextDirection.RightToLeft : TextDirection.LeftToRight;
            var shaped = Shaper.Shape(runText, metrics, BuildShapingOptions(metrics, runDirection)).Glyphs;

            // The Phase 6.5 review's /ToUnicode-mirroring finding: record the run's SOURCE
            // (logical, unmirrored) text into the cluster map, not the L4-mirrored text the
            // glyphs were shaped from — /ToUnicode answers "what characters does this glyph
            // REPRESENT", and a right-to-left "(" is represented by the "(" the author wrote
            // even though the ")" glyph is what gets painted. Mirroring substitutes one UTF-16
            // code unit for another (BidiAlgorithm.TryGetMirror is char→char), so every
            // ShapedGlyph.TextIndex/CodepointCount into runText indexes sourceRunText
            // identically.
            clusterMap?.Record(shaped, sourceRunText);
            shapedPerRun.Add(run.IsRightToLeft ? ReverseForPaint(shaped) : shaped);
        }

        var visualOrder = BidiAlgorithm.ReorderRunIndices(runs);
        var visualGlyphs = new List<ShapedGlyph>(line.Length);
        double totalWidth = 0;
        foreach (var runIndex in visualOrder)
        {
            foreach (var glyph in shapedPerRun[runIndex])
            {
                visualGlyphs.Add(glyph);
                totalWidth += glyph.AdvanceWidth;
            }
        }

        return new VisualLine(totalWidth, visualGlyphs);
    }

    /// <summary>
    /// Builds one bidi run's text, in its own logical order, applying L4 mirroring (bracket
    /// substitution) per character when the run resolves right-to-left. Explicit
    /// directional-formatting characters that are not isolates (LRE/RLE/LRO/RLO/PDF) and other
    /// zero-width format/control characters (BN) are dropped rather than shaped — UAX#9's
    /// "removing" implementation variant, and the only sound choice here: they carry no visible
    /// glyph in any real font, so passing them to <see cref="ILineShaper.Shape"/> would either
    /// throw <c>PLUME8009</c> (no font maps a Unicode format character) or draw a stray tofu
    /// box. <see cref="ManuscriptRenderer"/>'s own per-font codepoint map is built separately,
    /// from the unfiltered line, so this never affects it. The <em>dropping</em> is sound for
    /// embeddings/overrides because <see cref="BidiAlgorithm"/> gives them correct embedding
    /// levels via X1-X8 regardless (so <see cref="BidiAlgorithm.GetLevelRuns"/>/L2 reorder
    /// correctly around them) — but note that the weak/neutral/implicit resolution feeding
    /// those runs is itself <see cref="BidiAlgorithm"/>'s documented flat-pass approximation
    /// around explicit-embedding boundaries (no X10 isolating-run-sequence construction, no
    /// conformance-gate coverage — see that type's remarks), so
    /// embedding/override support as a whole is approximate, not merely "control characters
    /// removed". Isolate initiators/PDI (LRI/RLI/FSI/PDI) never reach here —
    /// <see cref="ShapeVisualLine"/> refuses them upfront (<see cref="RefuseIsolates"/>) instead,
    /// see that method's own remarks for why they are not simply dropped the same way.
    /// </summary>
    /// <param name="line">The full line the run indexes into.</param>
    /// <param name="run">The bidi level run to build text for.</param>
    /// <param name="sourceRunText">
    /// The run's SOURCE text — identical to the returned shaped text except that L4 mirroring
    /// is NOT applied (the same invisible-formatting characters are dropped, so indices align
    /// one-to-one with the returned text). <see cref="ShapeVisualLine"/> records this, never
    /// the mirrored form, into <see cref="GlyphClusterMap"/>: a mirrored glyph still
    /// <em>represents</em> the character the author wrote, so <c>/ToUnicode</c> must round-trip
    /// "(" back to "(" even though the ")" glyph is what an RTL run paints. When the run needs
    /// no mirroring this is the same string instance as the return value — no extra allocation
    /// on any left-to-right or mirror-free path.
    /// </param>
    private static string BuildRunText(string line, BidiRun run, out string sourceRunText)
    {
        var sb = new StringBuilder(run.Length);
        StringBuilder? source = null;
        for (var i = 0; i < run.Length; i++)
        {
            var ch = line[run.Start + i];
            if (IsInvisibleFormatting(ch))
            {
                continue;
            }

            if (run.IsRightToLeft && BidiAlgorithm.TryGetMirror(ch, out var mirrored))
            {
                // First mirrored character: fork the source builder from everything appended
                // so far (identical up to this point), then let the two diverge.
                source ??= new StringBuilder(sb.ToString(), run.Length);
                source.Append(ch);
                sb.Append(mirrored);
                continue;
            }

            source?.Append(ch);
            sb.Append(ch);
        }

        var shapedText = sb.ToString();
        sourceRunText = source?.ToString() ?? shapedText;
        return shapedText;
    }

    private static bool IsInvisibleFormatting(char ch)
    {
        var cls = BidiAlgorithm.GetBidiClass(ch);
        return cls is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;
    }

    /// <summary>
    /// Refuses (<c>PLUME9013</c>) rather than silently discards text containing an isolate
    /// initiator or terminator (LRI/RLI/FSI/PDI). UAX#9 X10 defines an "isolating run sequence"
    /// — the unit weak/neutral/implicit resolution (W1-W7/N0-N2/I1/I2) actually runs over — by
    /// threading together every level run from an isolate initiator through to its matching PDI;
    /// <see cref="BidiAlgorithm"/>'s own remarks document that it does not construct these
    /// (a documented, tested-only-for-the-no-isolates case scope cut), so W-N-I resolution
    /// inside/around an isolate is an unverified approximation with zero conformance-suite
    /// coverage (<c>BidiConformanceTests</c> excludes every case containing these characters
    /// from its pass/fail gate). A caller relying on RLI/PDI to force correct ordering around,
    /// say, an embedded Latin brand name inside Arabic text needs that machinery to actually be
    /// correct, not silently approximated — dropping the characters (as
    /// <see cref="BuildRunText"/> drops plain embeddings/overrides and other zero-width format
    /// characters) could silently produce mis-ordered output with no diagnostic, the same
    /// no-silent-wrong doctrine shaping refusals already follow. Plain embeddings/
    /// overrides (LRE/RLE/LRO/RLO/PDF) do not need this refusal — see <see cref="BuildRunText"/>'s
    /// own remarks for why a non-isolate embedding boundary does not implicate X10 the same way.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME9013</c> — <paramref name="line"/> contains an isolate initiator or terminator.</exception>
    private static void RefuseIsolates(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (BidiAlgorithm.GetBidiClass(line[i]) is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI)
            {
                throw new PlumePdfException(
                    "PLUME9013",
                    $"Text at position {i} contains U+{(int)line[i]:X4}, a bidi isolate initiator/terminator (LRI/RLI/FSI/PDI). " +
                    "PlumePDF's UAX #9 implementation does not construct isolating run sequences (X10) — the mechanism these characters require for correct weak/neutral text resolution around the isolated span — so it refuses rather than risk silently mis-ordered output. Remove them, or restructure the text to rely on implicit (Auto/LeftToRight/RightToLeft) direction resolution instead.");
            }
        }
    }

    /// <summary>
    /// Reverses one run's glyphs for right-to-left painting, correctly re-attributing each
    /// glyph's <see cref="ShapedGlyph.KernAdjustment"/> (the gap it carries is defined as
    /// "the space between this glyph and the next in painted order" — reversing the glyph
    /// order without moving the adjustment would put every kern gap next to the wrong pair).
    /// The total of every glyph's <see cref="ShapedGlyph.AdvanceWidth"/> is unaffected
    /// (a sum is invariant to reordering), so this never changes the run's measured width.
    /// Known limitation (2026-08-20 review): complex-script runs can now carry a per-glyph
    /// GPOS advance delta in <see cref="ShapedGlyph.KernAdjustment"/> (not just a pair-gap
    /// kern), and this slot-reassignment would move such a delta off its glyph on reversal —
    /// HarfBuzz reverses advances WITH their glyphs. Currently untriggerable: the pinned RTL
    /// fixture (Noto Naskh Arabic) has only mark/mkmk GPOS with natural-zero-advance marks.
    /// Revisit if an RTL font with kern/curs GPOS joins the fixture set.
    /// </summary>
    private static List<ShapedGlyph> ReverseForPaint(IReadOnlyList<ShapedGlyph> glyphs)
    {
        var n = glyphs.Count;
        var result = new List<ShapedGlyph>(n);
        for (var j = 0; j < n; j++)
        {
            var g = glyphs[n - 1 - j];
            var kern = j < n - 1 ? glyphs[n - 2 - j].KernAdjustment : 0.0;
            result.Add(g with { KernAdjustment = kern });
        }

        return result;
    }

    private static IFontMetrics ResolveStandard14(string name) =>
        Standard14Font.TryGet(name, out var font)
            ? font
            : throw new PlumePdfException("PLUME8014", $"Internal font-table invariant violated: '{name}' is not a recognized Standard-14 font name.");
}

/// <summary>One line's final shaped glyphs, in the visual (paint) order <see cref="TextLayouter.ShapeVisualLine"/> produces, plus their total width.</summary>
internal readonly record struct VisualLine(double Width, IReadOnlyList<ShapedGlyph> Glyphs);

/// <summary>One wrapped line produced by <see cref="TextLayouter.WrapLinesMeasured"/>: its text plus the width, in points, the wrap decision that emitted it measured it at — so <see cref="LayoutEngine"/> never re-shapes a finished line just to learn its width.</summary>
internal readonly record struct WrappedLine(string Text, double Width);
