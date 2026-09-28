namespace PlumePdf.Fonts.Shaping;

/// <summary>Devanagari (Indic) syllable-structure category for one codepoint (Unicode <c>Indic_Syllabic_Category</c>, narrowed to what Devanagari syllable segmentation and reordering need).</summary>
internal enum DevanagariCategory : byte
{
    /// <summary>Not part of Devanagari syllable structure (digits, danda, unclassified) — a syllable boundary.</summary>
    Other,
    Consonant,
    IndependentVowel,
    Nukta,
    Virama,

    /// <summary>The one Devanagari dependent vowel sign (matra) that is logically encoded after its consonant but rendered visually before it — U+093F only (A-7's pre-base reorder target).</summary>
    MatraPreBase,

    /// <summary>Every other dependent vowel sign (above/below/post-base) — these render in logical glyph order; their visual placement comes from the glyph's own design/GPOS anchors, not buffer reordering.</summary>
    MatraOther,

    /// <summary>Anusvara/visarga/candrabindu/stress signs — trailing syllable modifiers.</summary>
    Modifier,
}

/// <summary>
/// Devanagari shaping: syllable identification, reph and pre-base-matra
/// reordering, and the Microsoft <c>dev2</c>/<c>deva</c> feature plan, driven directly against
/// the font's raw <c>GSUB</c>/<c>GPOS</c> bytes via <see cref="OpenTypeLayoutEngine"/>.
/// <see cref="DevanagariCategory"/> per codepoint is derived from the pinned, generated
/// <c>UnicodeShapingData.g.cs</c> <c>Indic_Syllabic_Category</c> table — covering
/// the full Devanagari block including the additional consonants used for Marathi/Sindhi/
/// Kashmiri (U+0972-U+097F), not just the hand-picked subset this file used to carry — so
/// <c>PdfOptions.Deterministic</c> byte-identity cannot shift with the runtime's own UCD version
/// either.
/// </summary>
/// <remarks>
/// Documented simplification versus a full HarfBuzz-equivalent Indic shaper: reph and pre-base
/// matra reordering happen in one pass, after GSUB substitution runs and keyed by
/// <see cref="ShapingGlyph.Cluster"/> (not the two-pass reorder-to-shaping-order-then-back
/// real Indic shapers use to give GSUB features a canonical linear context to match against).
/// Below-base and above-base consonant/matra forms are expected to come from the font's own
/// <c>half</c>/<c>blwf</c>/<c>pstf</c> glyph substitution and GPOS anchor placement, not manual
/// buffer repositioning — only reph and the one pre-base matra move.
/// </remarks>
internal static class IndicShaper
{
    private const int Ra = 0x0930;
    private const int PreBaseMatraI = 0x093F;

    /// <summary>
    /// Shapes <paramref name="text"/> (assumed a single Devanagari-script run, one or more
    /// syllables) against <paramref name="font"/>.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME8009</c> a codepoint has no glyph in the font; <c>PLUME8024</c> the shaping
    /// work budget was exhausted; <c>PLUME8025</c> the font has neither a <c>dev2</c> nor a
    /// <c>deva</c> GSUB script record.
    /// </exception>
    public static GlyphBuffer Shape(ReadOnlySpan<char> text, TrueTypeFontProgram font, ShapingBudget budget)
    {
        var codepoints = EnumerateCodepoints(text);
        var glyphIds = new ushort[codepoints.Count];
        var advances = new double[codepoints.Count];
        var clusters = new int[codepoints.Count];

        for (var i = 0; i < codepoints.Count; i++)
        {
            var (codepoint, textIndex, _) = codepoints[i];
            if (!font.TryGetGlyphId(codepoint, out var glyphId))
            {
                throw new PlumePdfException(
                    "PLUME8009",
                    $"Codepoint U+{codepoint:X4} at text position {textIndex} has no glyph in font '{font.BaseFontName}'. " +
                    "PlumePDF does not silently substitute a fallback font or a .notdef box — embed a font that covers this character, or remove/replace it.");
            }

            glyphIds[i] = checked((ushort)glyphId);
            advances[i] = font.GetAdvanceWidth(glyphId);
            clusters[i] = textIndex;
        }

        var buffer = GlyphBuffer.FromGlyphs(glyphIds, clusters, advances, font.Gdef);

        if (!font.Sfnt.TryGetTable("GSUB", out var gsubBytes))
        {
            throw MissingCapability(font, "no GSUB table at all — Devanagari shaping requires conjunct/half-form substitution");
        }

        var gsubEngine = OpenTypeLayoutEngine.TryCreate(gsubBytes, font.Gdef, isGsub: true);
        var scriptTag = gsubEngine is not null && gsubEngine.HasScript("dev2") ? "dev2" : gsubEngine is not null && gsubEngine.HasScript("deva") ? "deva" : null;
        if (gsubEngine is null || scriptTag is null)
        {
            throw MissingCapability(font, "no 'dev2' or 'deva' GSUB script record (missing conjunct/half-form substitution)");
        }

        double GetAdvance(ushort gid) => font.GetAdvanceWidth(gid);

        var categories = new DevanagariCategory[codepoints.Count];
        for (var i = 0; i < codepoints.Count; i++)
        {
            categories[i] = ClassifyCategory(codepoints[i].Codepoint);
        }

        var syllables = SegmentSyllables(categories);
        var analyses = syllables.Select(s => AnalyzeSyllable(codepoints, categories, s.Start, s.Length)).ToList();

        foreach (var feature in new[] { "nukt", "akhn", "rphf", "pref", "blwf", "half", "pstf", "vatu", "cjct" })
        {
            ApplyBlanket(gsubEngine, buffer, budget, GetAdvance, font.BaseFontName, scriptTag, feature);
        }

        foreach (var analysis in analyses)
        {
            if (analysis.RephRaIndex is int raIdx && analysis.RephHalantIndex is int halantIdx)
            {
                ReorderReph(buffer, codepoints[raIdx].TextIndex, codepoints[halantIdx].TextIndex, codepoints[analysis.BaseConsonantIndex].TextIndex);
            }

            if (analysis.PreBaseMatraIndex is int matraIdx)
            {
                ReorderPreBaseMatra(buffer, codepoints[matraIdx].TextIndex, codepoints[analysis.BaseConsonantIndex].TextIndex);
            }
        }

        foreach (var feature in new[] { "pres", "abvs", "blws", "psts", "haln", "calt" })
        {
            ApplyBlanket(gsubEngine, buffer, budget, GetAdvance, font.BaseFontName, scriptTag, feature);
        }

        if (font.Sfnt.TryGetTable("GPOS", out var gposBytes))
        {
            var gposEngine = OpenTypeLayoutEngine.TryCreate(gposBytes, font.Gdef, isGsub: false);
            if (gposEngine is not null)
            {
                foreach (var feature in new[] { "abvm", "blwm", "dist", "kern" })
                {
                    var lookups = gposEngine.ResolveLookupIndices(scriptTag, null, feature);
                    if (lookups.Count > 0)
                    {
                        // Devanagari paints left-to-right: a mark's pen-relative offset must
                        // subtract the advances between base and mark (see
                        // OpenTypeLayoutEngine.PenRelativeAttachmentDelta's remarks — this is
                        // the root cause of the long-standing "reph/anusvara one base-advance
                        // too far right" divergence from hb-shape).
                        gposEngine.ApplyPositioning(buffer, lookups, budget, font.BaseFontName, TextDirection.LeftToRight);
                    }
                }
            }
        }

        return buffer;
    }

    private static PlumePdfException MissingCapability(TrueTypeFontProgram font, string reason) =>
        new(
            "PLUME8025",
            $"Cannot shape Devanagari text with font '{font.BaseFontName}': {reason}. " +
            "PlumePDF refuses rather than falling back to unreordered isolated forms (the no-silent-tofu policy) — embed a font with real Devanagari OpenType Layout support.");

    private static void ApplyBlanket(OpenTypeLayoutEngine engine, GlyphBuffer buffer, ShapingBudget budget, Func<ushort, double> getAdvance, string fontName, string scriptTag, string featureTag)
    {
        var lookups = engine.ResolveLookupIndices(scriptTag, null, featureTag);
        if (lookups.Count > 0)
        {
            engine.ApplySubstitution(buffer, lookups, budget, getAdvance, fontName);
        }
    }

    private readonly record struct Syllable(int Start, int Length);

    private static List<Syllable> SegmentSyllables(IReadOnlyList<DevanagariCategory> categories)
    {
        var result = new List<Syllable>();
        var i = 0;
        while (i < categories.Count)
        {
            if (categories[i] is not (DevanagariCategory.Consonant or DevanagariCategory.IndependentVowel))
            {
                i++;
                continue;
            }

            var start = i;
            i++;

            while (i < categories.Count)
            {
                if (categories[i] == DevanagariCategory.Nukta)
                {
                    i++;
                    continue;
                }

                if (categories[i] == DevanagariCategory.Virama && i + 1 < categories.Count && categories[i + 1] == DevanagariCategory.Consonant)
                {
                    i += 2;
                    continue;
                }

                break;
            }

            while (i < categories.Count && categories[i] is DevanagariCategory.MatraPreBase or DevanagariCategory.MatraOther)
            {
                i++;
            }

            while (i < categories.Count && categories[i] == DevanagariCategory.Modifier)
            {
                i++;
            }

            if (i < categories.Count && categories[i] == DevanagariCategory.Virama)
            {
                i++; // Word-final halant with no following consonant (e.g. a standalone half-form).
            }

            result.Add(new Syllable(start, i - start));
        }

        return result;
    }

    private readonly record struct SyllableAnalysis(int? RephRaIndex, int? RephHalantIndex, int BaseConsonantIndex, int? PreBaseMatraIndex);

    /// <summary>
    /// Finds this syllable's reph (a leading Ra+Virama followed by at least one more
    /// consonant), base consonant (the syllable's last consonant — the standard "last is base"
    /// rule for Devanagari), and pre-base matra (U+093F), all as indices into
    /// <paramref name="codepoints"/>.
    /// </summary>
    private static SyllableAnalysis AnalyzeSyllable(IReadOnlyList<(int Codepoint, int TextIndex, int CharLength)> codepoints, IReadOnlyList<DevanagariCategory> categories, int start, int length)
    {
        var end = start + length;
        var consonantIndices = new List<int>();
        for (var i = start; i < end; i++)
        {
            if (categories[i] == DevanagariCategory.Consonant)
            {
                consonantIndices.Add(i);
            }
        }

        int? rephRa = null;
        int? rephHalant = null;
        if (consonantIndices.Count >= 2 && codepoints[start].Codepoint == Ra && start + 1 < end && categories[start + 1] == DevanagariCategory.Virama)
        {
            rephRa = start;
            rephHalant = start + 1;
        }

        var baseIndex = consonantIndices[^1];

        int? preBaseMatra = null;
        for (var i = start; i < end; i++)
        {
            if (codepoints[i].Codepoint == PreBaseMatraI)
            {
                preBaseMatra = i;
                break;
            }
        }

        return new SyllableAnalysis(rephRa, rephHalant, baseIndex, preBaseMatra);
    }

    private static int FindBufferIndexByCluster(GlyphBuffer buffer, int cluster)
    {
        for (var i = 0; i < buffer.Count; i++)
        {
            if (buffer[i].Cluster == cluster)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Moves the reph glyph(s) (the Ra+Virama pair, or the single glyph they ligated into via
    /// <c>rphf</c>) to immediately after the base consonant's current glyph, preserving
    /// Ra-then-Virama order. Moves the Virama first (to right after base) and then Ra (to
    /// right after base, ahead of the just-moved Virama) — moving in the other order, or
    /// moving each cluster independently to a stale base-relative position, leaves the pair
    /// non-adjacent.
    /// </summary>
    private static void ReorderReph(GlyphBuffer buffer, int raCluster, int halantCluster, int baseCluster)
    {
        var baseIdx = FindBufferIndexByCluster(buffer, baseCluster);
        if (baseIdx < 0)
        {
            return;
        }

        var halantIdx = FindBufferIndexByCluster(buffer, halantCluster);
        if (halantIdx >= 0 && halantIdx != baseIdx + 1)
        {
            buffer.MoveGlyph(halantIdx, baseIdx + 1);
        }

        var raIdx = FindBufferIndexByCluster(buffer, raCluster);
        var freshBaseIdx = FindBufferIndexByCluster(buffer, baseCluster);
        if (raIdx >= 0 && freshBaseIdx >= 0 && raIdx != freshBaseIdx + 1)
        {
            buffer.MoveGlyph(raIdx, freshBaseIdx + 1);
        }
    }

    /// <summary>Moves the pre-base matra's glyph to immediately before the base consonant's current glyph.</summary>
    private static void ReorderPreBaseMatra(GlyphBuffer buffer, int matraCluster, int baseCluster)
    {
        var matraIdx = FindBufferIndexByCluster(buffer, matraCluster);
        var baseIdx = FindBufferIndexByCluster(buffer, baseCluster);
        if (matraIdx < 0 || baseIdx < 0 || matraIdx <= baseIdx)
        {
            return;
        }

        buffer.MoveGlyph(matraIdx, baseIdx);
    }

    private static List<(int Codepoint, int TextIndex, int CharLength)> EnumerateCodepoints(ReadOnlySpan<char> text)
    {
        var result = new List<(int, int, int)>(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Add((char.ConvertToUtf32(text[i], text[i + 1]), i, 2));
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

    /// <summary>
    /// Maps the generated table's Unicode <c>Indic_Syllabic_Category</c> value onto this
    /// shaper's narrower <see cref="DevanagariCategory"/>: every <c>Vowel_Dependent</c> (matra)
    /// is <see cref="DevanagariCategory.MatraOther"/> except U+093F, Devanagari's one pre-base
    /// matra (the reorder target), and every syllable-modifier-family category (bindu,
    /// visarga, cantillation marks, generic syllable modifiers) collapses to this shaper's one
    /// <see cref="DevanagariCategory.Modifier"/> bucket — none of them affect syllable
    /// segmentation or reordering differently from one another at this shaper's granularity.
    /// </summary>
    private static DevanagariCategory ClassifyCategory(int codepoint)
    {
        if (codepoint == PreBaseMatraI)
        {
            return DevanagariCategory.MatraPreBase;
        }

        return UnicodeShapingData.GetIndicSyllabicCategory(codepoint) switch
        {
            IndicSyllabicCategory.Consonant or IndicSyllabicCategory.Consonant_Dead => DevanagariCategory.Consonant,
            IndicSyllabicCategory.Vowel_Independent => DevanagariCategory.IndependentVowel,
            IndicSyllabicCategory.Nukta => DevanagariCategory.Nukta,
            IndicSyllabicCategory.Virama => DevanagariCategory.Virama,
            IndicSyllabicCategory.Vowel_Dependent => DevanagariCategory.MatraOther,
            IndicSyllabicCategory.Bindu or IndicSyllabicCategory.Visarga
                or IndicSyllabicCategory.Cantillation_Mark or IndicSyllabicCategory.Syllable_Modifier => DevanagariCategory.Modifier,
            _ => DevanagariCategory.Other,
        };
    }
}
