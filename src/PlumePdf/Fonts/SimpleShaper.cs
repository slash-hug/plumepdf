namespace PlumePdf.Fonts;

/// <summary>
/// Phase 2's managed shaping implementation (a staged-shaping decision): resolves each
/// Unicode codepoint to a glyph via <see cref="IFontMetrics.TryGetGlyphId"/>, then — for an
/// embedded <see cref="TrueTypeFontProgram"/> that carries them — applies <c>GSUB</c>
/// ligature substitution and <c>GPOS</c> pair kerning. Scoped to left-to-right scripts with
/// no reordering or reshaping needs (Latin, Cyrillic, Greek); complex-script shaping
/// (Arabic/Indic) is <see cref="Shaping.ComplexShaper"/>, behind this same
/// <see cref="ILineShaper"/> seam. A font with no <c>GSUB</c>/<c>GPOS</c> (or a
/// <see cref="Standard14.Standard14Font"/>, which has neither) shapes as a plain
/// one-codepoint-to-one-glyph run with no ligatures or kerning. Ignores
/// <see cref="ShapingOptions.ScriptTag"/>/<see cref="ShapingOptions.Direction"/> — it has
/// exactly one code path — but enforces <see cref="ShapingOptions.MaxLookupApplications"/>
/// as a real, if narrow, consumption site: every attempted GSUB ligature match
/// and GPOS pair-adjustment lookup counts against the budget.
/// </summary>
internal sealed class SimpleShaper : ILineShaper
{
    /// <inheritdoc/>
    public ShapedRun Shape(ReadOnlySpan<char> text, IFontMetrics font, ShapingOptions options)
    {
        var lookupApplications = 0;
        var codepoints = EnumerateCodepoints(text);
        var glyphIds = new ushort[codepoints.Count];

        for (var i = 0; i < codepoints.Count; i++)
        {
            var (codepoint, textIndex, _) = codepoints[i];
            if (!font.TryGetGlyphId(codepoint, out var glyphId))
            {
                // char.ConvertFromUtf32 throws ArgumentOutOfRangeException for a lone surrogate
                // (0xD800-0xDFFF) — EnumerateCodepoints below emits exactly that for an unpaired
                // surrogate in the input, and this is a document-data path: a bare BCL exception
                // here would escape instead of the coded PLUME8009 the exception policy requires.
                var display = codepoint <= 0xFFFF && char.IsSurrogate((char)codepoint) ? $"<unpaired surrogate U+{codepoint:X4}>" : char.ConvertFromUtf32(codepoint);
                throw new PlumePdfException(
                    "PLUME8009",
                    $"Codepoint U+{codepoint:X4} ('{display}') at text position {textIndex} has no glyph in font '{font.BaseFontName}'. " +
                    "PlumePDF does not silently substitute a fallback font or a .notdef box (Phase 2 fail-fast glyph-coverage policy) — " +
                    "embed a font that covers this character, or remove/replace it.");
            }

            glyphIds[i] = checked((ushort)glyphId);
        }

        var gsub = (font as TrueTypeFontProgram)?.Gsub;
        var gpos = (font as TrueTypeFontProgram)?.Gpos;

        var shaped = new List<ShapedGlyph>(codepoints.Count);
        var i2 = 0;
        while (i2 < codepoints.Count)
        {
            var (_, textIndex, charLength) = codepoints[i2];
            var glyphId = glyphIds[i2];
            var consumedCodepoints = 1;
            var consumedChars = charLength;

            if (gsub is not null)
            {
                CountLookupApplication(ref lookupApplications, options, font);
                if (gsub.TryFindLigature(glyphIds, i2, out var componentCount, out var ligatureGlyphId))
                {
                    glyphId = ligatureGlyphId;
                    consumedCodepoints = componentCount;
                    consumedChars = 0;
                    for (var c = 0; c < componentCount; c++)
                    {
                        consumedChars += codepoints[i2 + c].CharLength;
                    }
                }
            }

            shaped.Add(new ShapedGlyph(glyphId, font.GetAdvanceWidth(glyphId), textIndex, consumedChars, Cluster: textIndex));
            i2 += consumedCodepoints;
        }

        if (gpos is not null)
        {
            for (var i = 0; i < shaped.Count - 1; i++)
            {
                CountLookupApplication(ref lookupApplications, options, font);
                var adjustment = gpos.GetPairAdjustment(checked((ushort)shaped[i].GlyphId), checked((ushort)shaped[i + 1].GlyphId));
                if (adjustment != 0)
                {
                    shaped[i] = shaped[i] with { AdvanceWidth = shaped[i].AdvanceWidth + adjustment, KernAdjustment = adjustment };
                }
            }
        }

        // Always LeftToRight, deliberately NOT echoing options.Direction back: ShapedRun.Direction
        // names the visual order the Glyphs array is actually in, and this shaper never reorders —
        // its output stays in logical order, which paints left-to-right. Echoing the caller's
        // requested base direction (as this once did) claimed an RTL Hebrew run's glyphs were
        // already visually reversed when they were not (the caller's own downstream bidi pass —
        // TextLayouter.ShapeVisualLine's ReverseForPaint — does that reversal).
        return new ShapedRun(shaped, TextDirection.LeftToRight);
    }

    /// <summary>
    /// The real-if-narrow consumption site for
    /// <see cref="ShapingOptions.MaxLookupApplications"/>: every attempted GSUB/GPOS lookup
    /// (whether or not it matches) counts against the budget, so a hostile caller-supplied font
    /// paired with a pathologically long input cannot drive unbounded lookup work even through
    /// this simple shaper's two lookup types. The full lookup executor (the complex-script
    /// shaping engine) applies the same budget to a much larger lookup surface (contextual/chaining
    /// substitution and positioning), where it matters far more.
    /// </summary>
    private static void CountLookupApplication(ref int lookupApplications, ShapingOptions options, IFontMetrics font)
    {
        lookupApplications++;
        if (lookupApplications > options.MaxLookupApplications)
        {
            throw new PlumePdfException(
                "PLUME8024",
                $"Shaping font '{font.BaseFontName}' exceeded the configured lookup-application budget of " +
                $"{options.MaxLookupApplications} (ShapingOptions.MaxLookupApplications / PdfOptions.MaxShapingLookupApplications). " +
                "This guards against a hostile or pathological font/input combination driving unbounded shaping work; " +
                "raise the budget if this is a genuine large document, not a crafted input.");
        }
    }

    private static List<(int Codepoint, int TextIndex, int CharLength)> EnumerateCodepoints(ReadOnlySpan<char> text)
    {
        var result = new List<(int, int, int)>(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var codepoint = char.ConvertToUtf32(text[i], text[i + 1]);
                result.Add((codepoint, i, 2));
                i += 2;
            }
            else
            {
                result.Add((text[i], i, 1));
                i += 1;
            }
        }

        return result;
    }
}
