namespace PlumePdf.Fonts.Shaping;

/// <summary>
/// The Unicode script tags this shaping tier distinguishes (v1.0 script
/// tier — Arabic and Devanagari get real shapers; everything else that isn't script-neutral is
/// <see cref="Unsupported"/>, refused by <see cref="ComplexShaper"/> rather than silently
/// passed through the simple cmap-only path — the no-silent-tofu doctrine extended to
/// out-of-tier scripts).
/// </summary>
internal enum ScriptTag
{
    /// <summary>Script-neutral codepoints (spaces, ASCII/common punctuation, digits, combining marks) that attach to whichever script run surrounds them (UAX#24's Common/Inherited).</summary>
    Common,
    Latin,
    Cyrillic,
    Greek,
    Arabic,
    Devanagari,

    /// <summary>CJK Unified Ideographs (Han) — no contextual joining/reordering needed; shaped through the plain cmap+GSUB-liga+GPOS-kern path exactly like <see cref="Common"/>/<see cref="Latin"/>.</summary>
    Han,

    /// <summary>Hebrew — an abjad with combining points/cantillation marks but no cursive joining; shaped through the plain cmap+GSUB-liga+GPOS-kern path exactly like <see cref="Common"/>/<see cref="Latin"/> (bidi reordering happens separately, at the <c>TextLayouter</c>/<c>BidiAlgorithm</c> layer, not here).</summary>
    Hebrew,

    /// <summary>Any script this tier does not shape — 1.x. Carries no further identity; <see cref="ScriptRun"/> callers report the run's own text for a useful refusal message.</summary>
    Unsupported,
}

/// <summary>One maximal run of text sharing a single non-<see cref="ScriptTag.Common"/> script (the script-run itemization) — <see cref="ScriptSegmenter.Segment"/>'s unit of output.</summary>
internal readonly record struct ScriptRun(int Start, int Length, ScriptTag Script);

/// <summary>
/// Itemizes a text run into maximal script runs, a minimal single-pass adaptation of
/// UAX#24 §5.1's "script runs" algorithm: Common/Inherited codepoints (punctuation, digits,
/// combining marks, spaces) attach to whichever explicit-script run they sit inside rather than
/// splitting it, and a run of only Common codepoints at the very start of the text stays Common
/// until an explicit script appears (or the text ends). Classification is driven entirely by
/// the pinned, generated <c>UnicodeShapingData.g.cs</c> <c>Script</c> table (the
/// authoritative source, so <c>PdfOptions.Deterministic</c> byte-identity cannot shift with the
/// runtime's own UCD version); <see cref="ScriptPolicy"/> is the one remaining hand-authored
/// piece, a deliberately narrow product decision (not a Unicode fact) about which of the
/// resulting scripts this shaping tier refuses outright versus renders through the plain
/// cmap+GSUB-liga+GPOS-kern path — see its own remarks.
/// </summary>
internal static class ScriptSegmenter
{
    public static List<ScriptRun> Segment(ReadOnlySpan<char> text)
    {
        var runs = new List<ScriptRun>();
        var i = 0;
        while (i < text.Length)
        {
            var runStart = i;
            var (cp0, len0) = ReadCodepoint(text, i);
            var runScript = Classify(cp0);
            i += len0;

            while (i < text.Length)
            {
                var (cp, len) = ReadCodepoint(text, i);
                var script = Classify(cp);
                if (script == ScriptTag.Common || script == runScript)
                {
                    if (runScript == ScriptTag.Common && script != ScriptTag.Common)
                    {
                        runScript = script;
                    }

                    i += len;
                    continue;
                }

                break;
            }

            runs.Add(new ScriptRun(runStart, i - runStart, runScript));
        }

        return runs;
    }

    private static (int Codepoint, int Length) ReadCodepoint(ReadOnlySpan<char> text, int index)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return (char.ConvertToUtf32(text[index], text[index + 1]), 2);
        }

        return (text[index], 1);
    }

    private static ScriptTag Classify(int cp)
    {
        var script = UnicodeShapingData.GetScript(cp);
        return script switch
        {
            UnicodeScript.Common or UnicodeScript.Inherited => ScriptTag.Common,
            UnicodeScript.Latin => ScriptTag.Latin,
            UnicodeScript.Greek => ScriptTag.Greek,
            UnicodeScript.Cyrillic => ScriptTag.Cyrillic,
            UnicodeScript.Arabic => ScriptTag.Arabic,
            UnicodeScript.Devanagari => ScriptTag.Devanagari,
            UnicodeScript.Han => ScriptTag.Han,
            UnicodeScript.Hebrew => ScriptTag.Hebrew,
            _ => ScriptPolicy.RequiresComplexShaping(script) ? ScriptTag.Unsupported : ScriptTag.Common,
        };
    }
}

/// <summary>
/// The one authored (non-Unicode-derived) piece of <see cref="ScriptSegmenter"/>'s
/// classification: which scripts outside the shipped shaping tier (Arabic +
/// Devanagari) still need a coded <c>PLUME8026</c> refusal versus which render
/// correctly through the plain cmap+GSUB-liga+GPOS-kern path with no contextual shaping at all.
/// This tier's scope sanctioned refusing scripts that <em>need dedicated shaping logic</em> this tier does not
/// implement — complex joining (other Arabic-family cursive scripts) or syllable
/// reordering/conjunct formation (other Brahmic/Indic scripts) — not every script this tier
/// simply hasn't named. A script reaching here that needs neither (Hangul, Armenian, Georgian,
/// Ethiopic, Hiragana/Katakana, …) is deliberately left off this list and defaults to
/// <see cref="ScriptTag.Common"/> in <see cref="ScriptSegmenter.Classify"/> — same policy CJK
/// Han and Hebrew now get explicitly (see their own <see cref="ScriptTag"/> members) rather than
/// the blanket refusal this file used to apply to them.
/// </summary>
internal static class ScriptPolicy
{
    private static readonly HashSet<UnicodeScript> ComplexScripts =
    [
        // Other Brahmic/Indic scripts needing syllable segmentation, reordering, and/or
        // conjunct (below-base/half-form) glyph formation the Devanagari-only
        // Indic shaper does not generalize to.
        UnicodeScript.Thai, UnicodeScript.Lao, UnicodeScript.Myanmar, UnicodeScript.Khmer,
        UnicodeScript.Bengali, UnicodeScript.Gujarati, UnicodeScript.Gurmukhi, UnicodeScript.Oriya,
        UnicodeScript.Tamil, UnicodeScript.Telugu, UnicodeScript.Kannada, UnicodeScript.Malayalam,
        UnicodeScript.Sinhala, UnicodeScript.Tibetan,

        // Other cursive-joining scripts the Arabic-only joining shaper does not
        // generalize to.
        UnicodeScript.Syriac, UnicodeScript.Mongolian, UnicodeScript.Nko, UnicodeScript.Thaana,
    ];

    /// <summary>Whether <paramref name="script"/> needs contextual shaping (joining, reordering, or conjunct formation) the shipped tier does not implement, and must therefore refuse rather than silently render unjoined/unreordered.</summary>
    public static bool RequiresComplexShaping(UnicodeScript script) => ComplexScripts.Contains(script);
}
