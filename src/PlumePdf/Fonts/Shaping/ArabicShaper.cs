namespace PlumePdf.Fonts.Shaping;

/// <summary>Unicode <c>Joining_Type</c> (the Arabic cursive-joining property, Unicode §9.2 / <c>ArabicShaping.txt</c>) for one codepoint — <c>L</c> (Left_Joining) is omitted: it names no standard Arabic letter and is folded into <see cref="NonJoining"/> if ever encountered.</summary>
internal enum ArabicJoiningType : byte
{
    NonJoining,
    Right,
    Dual,
    JoinCausing,
    Transparent,
}

/// <summary>The joining form a codepoint's glyph resolves to (drives which of <c>isol</c>/<c>init</c>/<c>medi</c>/<c>fina</c> applies).</summary>
internal enum ArabicForm : byte
{
    Isolated,
    Initial,
    Medial,
    Final,
}

/// <summary>
/// Arabic shaping: joining analysis from <c>Joining_Type</c>, then the
/// Microsoft Arabic script shaping order — <c>ccmp → isol/init/medi/fina → rlig/calt/liga →
/// mark/mkmk/kern</c> — driven directly against the font's raw <c>GSUB</c>/<c>GPOS</c> bytes via
/// <see cref="OpenTypeLayoutEngine"/>. <see cref="ArabicJoiningType"/> per codepoint comes from
/// the pinned, generated <c>UnicodeShapingData.g.cs</c> table — covering every
/// Arabic-block joining letter (core U+0621-U+064A, Persian/Urdu/extended letters like U+067E
/// PEH/U+06AF GAF/U+06CC FARSI YEH, and the Arabic Supplement/Extended-A blocks), not just the
/// hand-picked subset this file used to carry — so <c>PdfOptions.Deterministic</c> byte-identity
/// cannot shift with the runtime's own UCD version either. Coverage policy: a font with no
/// <c>arab</c> GSUB script record is a hard <c>PLUME8025</c> refusal naming the font and script
/// — never a silent isolated-form fallback.
/// </summary>
internal static class ArabicShaper
{

    /// <summary>
    /// Shapes <paramref name="text"/> (assumed a single Arabic-script run) against
    /// <paramref name="font"/>, returning the fully substituted-and-positioned buffer:
    /// joining-form glyph selection, ligatures (lam-alef via <c>rlig</c>, etc.), and mark
    /// (harakat) attachment via GPOS.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME8009</c> a codepoint has no glyph in the font at all (cmap coverage);
    /// <c>PLUME8024</c> the shaping work budget was exhausted (hostile/malformed font);
    /// <c>PLUME8025</c> the font has no <c>arab</c> GSUB script record (missing joining-form
    /// substitution — Arabic cannot be shaped correctly without it, so this is refused rather
    /// than silently rendered as unjoined isolated forms).
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
            throw MissingCapability(font, "no GSUB table at all — Arabic shaping requires isol/init/medi/fina joining-form substitution");
        }

        var gsubEngine = OpenTypeLayoutEngine.TryCreate(gsubBytes, font.Gdef, isGsub: true);
        if (gsubEngine is null || !gsubEngine.HasScript("arab"))
        {
            throw MissingCapability(font, "no 'arab' GSUB script record (missing joining-form substitution)");
        }

        double GetAdvance(ushort gid) => font.GetAdvanceWidth(gid);

        ApplyBlanket(gsubEngine, buffer, budget, GetAdvance, font.BaseFontName, "ccmp");

        var forms = ComputeJoiningForms(codepoints);
        var isolLookups = gsubEngine.ResolveLookupIndices("arab", null, "isol");
        var initLookups = gsubEngine.ResolveLookupIndices("arab", null, "init");
        var mediLookups = gsubEngine.ResolveLookupIndices("arab", null, "medi");
        var finaLookups = gsubEngine.ResolveLookupIndices("arab", null, "fina");

        for (var i = 0; i < buffer.Count; i++)
        {
            if (!forms.TryGetValue(buffer[i].Cluster, out var form))
            {
                continue;
            }

            var lookups = form switch
            {
                ArabicForm.Isolated => isolLookups,
                ArabicForm.Initial => initLookups,
                ArabicForm.Medial => mediLookups,
                ArabicForm.Final => finaLookups,
                _ => null,
            };

            if (lookups is { Count: > 0 })
            {
                gsubEngine.TryApplyOneAt(buffer, i, lookups, budget, GetAdvance, font.BaseFontName);
            }
        }

        ApplyBlanket(gsubEngine, buffer, budget, GetAdvance, font.BaseFontName, "rlig");
        ApplyBlanket(gsubEngine, buffer, budget, GetAdvance, font.BaseFontName, "calt");
        ApplyBlanket(gsubEngine, buffer, budget, GetAdvance, font.BaseFontName, "liga");

        if (font.Sfnt.TryGetTable("GPOS", out var gposBytes))
        {
            var gposEngine = OpenTypeLayoutEngine.TryCreate(gposBytes, font.Gdef, isGsub: false);
            if (gposEngine is not null)
            {
                foreach (var feature in new[] { "curs", "mark", "mkmk", "kern" })
                {
                    var lookups = gposEngine.ResolveLookupIndices("arab", null, feature);
                    if (lookups.Count > 0)
                    {
                        // Arabic paints right-to-left: mark-attachment offsets must compensate
                        // for the advances between base and mark in THAT direction (see
                        // OpenTypeLayoutEngine.PenRelativeAttachmentDelta's remarks).
                        gposEngine.ApplyPositioning(buffer, lookups, budget, font.BaseFontName, TextDirection.RightToLeft);
                    }
                }
            }
        }

        return buffer;
    }

    private static PlumePdfException MissingCapability(TrueTypeFontProgram font, string reason) =>
        new(
            "PLUME8025",
            $"Cannot shape Arabic text with font '{font.BaseFontName}': {reason}. " +
            "PlumePDF refuses rather than falling back to unjoined isolated forms (the no-silent-tofu policy) — embed a font with real Arabic OpenType Layout support.");

    private static void ApplyBlanket(OpenTypeLayoutEngine engine, GlyphBuffer buffer, ShapingBudget budget, Func<ushort, double> getAdvance, string fontName, string featureTag)
    {
        var lookups = engine.ResolveLookupIndices("arab", null, featureTag);
        if (lookups.Count > 0)
        {
            engine.ApplySubstitution(buffer, lookups, budget, getAdvance, fontName);
        }
    }

    /// <summary>
    /// Assigns each non-transparent codepoint (keyed by its text index — <see cref="ShapingGlyph.Cluster"/>
    /// tracks back to the same value) the joining form its neighbors dictate: a preceding
    /// Dual/JoinCausing letter that this letter's own type can receive a join from yields
    /// <see cref="ArabicForm.Medial"/>/<see cref="ArabicForm.Final"/>; a following letter this
    /// letter can extend a join to (only Dual/JoinCausing letters can) yields
    /// <see cref="ArabicForm.Medial"/>/<see cref="ArabicForm.Initial"/>. Transparent codepoints
    /// (harakat/combining marks) are skipped when scanning for neighbors and get no form entry
    /// at all — the shaper leaves their base glyph untouched.
    /// </summary>
    private static Dictionary<int, ArabicForm> ComputeJoiningForms(IReadOnlyList<(int Codepoint, int TextIndex, int CharLength)> codepoints)
    {
        var types = new ArabicJoiningType[codepoints.Count];
        for (var i = 0; i < codepoints.Count; i++)
        {
            types[i] = ClassifyJoiningType(codepoints[i].Codepoint);
        }

        var result = new Dictionary<int, ArabicForm>();
        for (var i = 0; i < codepoints.Count; i++)
        {
            if (types[i] == ArabicJoiningType.Transparent)
            {
                continue;
            }

            if (types[i] == ArabicJoiningType.NonJoining)
            {
                result[codepoints[i].TextIndex] = ArabicForm.Isolated;
                continue;
            }

            var joinsFromPrev = false;
            for (var p = i - 1; p >= 0; p--)
            {
                if (types[p] == ArabicJoiningType.Transparent)
                {
                    continue;
                }

                joinsFromPrev = CanExtendJoin(types[p]) && CanReceiveJoin(types[i]);
                break;
            }

            var joinsToNext = false;
            if (CanExtendJoin(types[i]))
            {
                for (var n = i + 1; n < codepoints.Count; n++)
                {
                    if (types[n] == ArabicJoiningType.Transparent)
                    {
                        continue;
                    }

                    joinsToNext = CanReceiveJoin(types[n]);
                    break;
                }
            }

            result[codepoints[i].TextIndex] = (joinsFromPrev, joinsToNext) switch
            {
                (true, true) => ArabicForm.Medial,
                (true, false) => ArabicForm.Final,
                (false, true) => ArabicForm.Initial,
                (false, false) => ArabicForm.Isolated,
            };
        }

        return result;
    }

    private static bool CanExtendJoin(ArabicJoiningType type) => type is ArabicJoiningType.Dual or ArabicJoiningType.JoinCausing;

    private static bool CanReceiveJoin(ArabicJoiningType type) => type is ArabicJoiningType.Right or ArabicJoiningType.Dual or ArabicJoiningType.JoinCausing;

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
    /// Resolves one codepoint's effective <see cref="ArabicJoiningType"/> from the generated
    /// table's <see cref="JoiningType"/>. <c>ArabicShaping.txt</c> (the source
    /// <c>scripts/generate-unicode-data.csx</c> transcribes) explicitly lists only the letters
    /// of the cursive-joining scripts plus a few format characters — every combining mark
    /// (harakat, Qur'anic annotation signs, the superscript alef) is deliberately unlisted and
    /// relies on that file's own documented default: an unlisted codepoint with a nonzero
    /// Canonical_Combining_Class defaults to Transparent, not Non_Joining (the bare
    /// <see cref="UnicodeShapingData.GetJoiningType"/> default) — it must not break the joining
    /// chain between the letters it sits between. That default derivation is applied here
    /// rather than in the generated table itself, so it stays exactly as visible/auditable as
    /// every other generated lookup.
    /// </summary>
    private static ArabicJoiningType ClassifyJoiningType(int codepoint)
    {
        var type = UnicodeShapingData.GetJoiningType(codepoint);
        if (type == JoiningType.U && UnicodeShapingData.GetCombiningClass(codepoint) != 0)
        {
            return ArabicJoiningType.Transparent;
        }

        return type switch
        {
            JoiningType.R => ArabicJoiningType.Right,
            JoiningType.D => ArabicJoiningType.Dual,
            JoiningType.C => ArabicJoiningType.JoinCausing,
            JoiningType.T => ArabicJoiningType.Transparent,
            _ => ArabicJoiningType.NonJoining, // U (Non_Joining), L (Left_Joining, unused by any real letter)
        };
    }
}
