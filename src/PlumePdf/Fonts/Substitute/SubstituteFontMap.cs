namespace PlumePdf.Fonts.Substitute;

/// <summary>
/// The outcome of resolving a non-embedded font to a bundled substitute face
/// (<see cref="SubstituteFontMap.Resolve"/>): either a bundled <see cref="FaceKey"/> — looked up
/// in <see cref="SubstituteFontStore"/> — plus an informational <see cref="Diagnostic"/>
/// recording the substitution, or (when the font appears to need glyph coverage the bundle
/// doesn't carry, e.g. CJK) a <see langword="null"/> <see cref="FaceKey"/> with a coded gap
/// <see cref="Diagnostic"/> instead.
/// </summary>
/// <param name="FaceKey">The bundled face's key (a <see cref="SubstituteFontStore.TryGetFont"/> lookup key), or <see langword="null"/> if no bundled face can stand in for this font.</param>
/// <param name="Diagnostic">The result-scoped diagnostic to attach to the render result — always present (a substitution is always worth recording, per Phase 8's active-diagnostic precedent).</param>
internal readonly record struct SubstitutionResolution(string? FaceKey, PdfDiagnostic Diagnostic);

/// <summary>
/// Maps a non-embedded PDF font's declared characteristics to one of the 14 bundled substitute
/// faces (Phase 8, later widened): the 12 Liberation faces for the
/// ordinary Western case, plus PDFium's own Foxit Symbol/Dingbats faces for the two Standard-14
/// symbol fonts — the render-path fallback used when a page references a font PlumePDF cannot
/// find an embedded program for. Liberation Sans/Serif/Mono are metric-compatible with
/// Arial/Times New Roman/Courier New respectively by design (the whole point of the Liberation
/// project), so name-based substitution for the common Western fonts is visually reasonable, not
/// just present-something. The Foxit rule fires only for the exact names
/// <see cref="Standard14SymbolFonts"/> recognizes (<c>Symbol</c>/<c>SymbolMT</c>/<c>ZapfDingbats</c>)
/// on a Type 1/MMType1 font — <see cref="Resolve"/>'s <c>isType1Subtype</c> parameter gates it — because those two
/// names carry a PDF-defined built-in encoding (ISO 32000-1 Annex D) that only makes sense
/// against a symbol glyph set; a <c>/Subtype /TrueType</c> font merely named <c>Symbol</c> is a
/// different animal (PDFium's <c>kMsSymbol</c> mapper path) and keeps the ordinary Latin
/// substitution heuristic below. Mints <c>PLUME7510</c>/<c>PLUME7511</c>, within the shared
/// <c>PLUME7500</c>-<c>7999</c> Phase 8 band, which also covers <c>7500</c>-<c>7502</c> for
/// <c>RasterSurface</c>/<c>RasterGraphicsState</c>/<c>RasterInterpreter</c>.
/// </summary>
internal static class SubstituteFontMap
{
    // Deliberately conservative name-substring heuristics (case-insensitive) — false negatives
    // (missing a CJK font by an unusual name) fail safe into an ordinary Latin substitution,
    // which is the same degraded-but-functional outcome as PDF viewers give for any font
    // dropped on a system that lacks it. False positives are far worse (silently refusing a
    // Latin font because its name coincidentally matched), so the list stays narrow and
    // well-known rather than broad and speculative.
    private static readonly string[] CjkNameMarkers =
    [
        "CJK", "SC", "TC", "GB", "SIMSUN", "SIMHEI", "NSIMSUN", "FANGSONG", "KAITI", "YOUYUAN",
        "MINGLIU", "PMINGLIU", "MICROSOFT JHENGHEI", "MICROSOFT YAHEI", "SONGTI", "HEITI",
        "PINGFANG", "HIRAGINO", "MS GOTHIC", "MS MINCHO", "MS PGOTHIC", "MS PMINCHO", "YU GOTHIC",
        "YU MINCHO", "MEIRYO", "BATANG", "DOTUM", "GULIM", "GUNGSUH", "MALGUN GOTHIC", "NOTO SANS CJK",
        "NOTO SERIF CJK", "SOURCE HAN",
    ];

    private static readonly string[] BoldNameMarkers = ["BOLD", "HEAVY", "BLACK", "SEMIBOLD"];
    private static readonly string[] ItalicNameMarkers = ["ITALIC", "OBLIQUE"];
    private static readonly string[] FixedPitchNameMarkers = ["MONO", "COURIER", "CONSOLAS", "CONSOLE", "TYPEWRITER"];
    private static readonly string[] SerifNameMarkers = ["TIMES", "GEORGIA", "SERIF", "GARAMOND", "CAMBRIA", "MINION", "BOOKMAN", "PALATINO", "CENTURY"];

    /// <summary>
    /// Resolves the best bundled substitute for a non-embedded font. <paramref name="baseFontName"/>
    /// is the font's <c>/BaseFont</c> name (used both for the CJK-gap heuristic and, combined
    /// with the descriptor flags, as a secondary style signal — many producers omit accurate
    /// <c>/Flags</c> but still encode style in the name, e.g. <c>"Arial-BoldMT"</c>).
    /// </summary>
    /// <param name="baseFontName">The font's declared <c>/BaseFont</c> name, or <see langword="null"/> if unknown.</param>
    /// <param name="bold">The font descriptor's declared bold-ness (<c>/Flags</c> bit 19, ForceBold, or a <c>/FontWeight</c> ≥ 600 reading — whatever the caller's descriptor layer resolved).</param>
    /// <param name="italic">The font descriptor's declared italic-ness (<c>/Flags</c> bit 7, Italic, or a nonzero <c>/ItalicAngle</c>).</param>
    /// <param name="serif">The font descriptor's declared <c>/Flags</c> bit 2 (Serif).</param>
    /// <param name="fixedPitch">The font descriptor's declared <c>/Flags</c> bit 1 (FixedPitch).</param>
    /// <param name="isType1Subtype">
    /// <see langword="true"/> when the PDF font dictionary's <c>/Subtype</c> is <c>Type1</c> or
    /// <c>MMType1</c> and it is not reached through a Type0/CID wrapper — the gate for the Foxit
    /// Symbol/Dingbats exact-name rule (see the class summary). A <c>/Subtype /TrueType</c> font
    /// named <c>Symbol</c> passes <see langword="false"/> and falls through to the ordinary
    /// Latin-substitution heuristic below.
    /// </param>
    public static SubstitutionResolution Resolve(string? baseFontName, bool bold = false, bool italic = false, bool serif = false, bool fixedPitch = false, bool isType1Subtype = false)
    {
        if (isType1Subtype && Standard14SymbolFonts.TryGet(baseFontName ?? string.Empty, "Type1", out _, out var foxitFaceKey))
        {
            var symbolOrDingbats = foxitFaceKey == Standard14SymbolFonts.FoxitDingbatsFaceKey ? "Dingbats" : "Symbol";
            var foxitDiagnostic = new PdfDiagnostic(
                "PLUME7510",
                DiagnosticSeverity.Info,
                $"Font '{baseFontName}' is not embedded; rendered using the bundled substitute face '{foxitFaceKey}' (PDFium's Foxit {symbolOrDingbats} face).");
            return new SubstitutionResolution(foxitFaceKey, foxitDiagnostic);
        }

        var upperName = baseFontName?.ToUpperInvariant() ?? string.Empty;

        // "ZAPFDIN[GB]ATS" / "DIN[GB]ATS" contain the "GB" CJK marker by accident of spelling; a
        // TrueType-subtype ZapfDingbats (outside the Type 1 Foxit rule above) must degrade to the
        // Latin heuristic, not to the CJK gap's blank .notdef (PDFium
        // would draw the Foxit face here — routing that case to it is a follow-up).
        var isDingbatsName = upperName is "ZAPFDINGBATS" or "DINGBATS";

        if (!isDingbatsName && ContainsAny(upperName, CjkNameMarkers))
        {
            var gapDiagnostic = new PdfDiagnostic(
                "PLUME7511",
                DiagnosticSeverity.Warning,
                $"Font '{baseFontName}' is not embedded and appears to require CJK glyph coverage, which PlumePDF's bundled substitute-font set does not include (a known gap — see docs/cookbook/rasterize-substitute-fonts.md); affected glyphs render using the substitute's .notdef glyph.");
            return new SubstitutionResolution(null, gapDiagnostic);
        }

        var resolvedBold = bold || ContainsAny(upperName, BoldNameMarkers);
        var resolvedItalic = italic || ContainsAny(upperName, ItalicNameMarkers);
        var resolvedFixedPitch = fixedPitch || ContainsAny(upperName, FixedPitchNameMarkers);
        var resolvedSerif = !resolvedFixedPitch && (serif || ContainsAny(upperName, SerifNameMarkers));

        var family = resolvedFixedPitch ? "LiberationMono" : resolvedSerif ? "LiberationSerif" : "LiberationSans";
        var style = (resolvedBold, resolvedItalic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            (false, false) => "Regular",
        };

        var faceKey = $"{family}-{style}";
        var diagnostic = new PdfDiagnostic(
            "PLUME7510",
            DiagnosticSeverity.Info,
            $"Font '{baseFontName ?? "(unnamed)"}' is not embedded; rendered using the bundled substitute face '{faceKey}'.");

        return new SubstitutionResolution(faceKey, diagnostic);
    }

    private static bool ContainsAny(string upperHaystack, string[] upperNeedles)
    {
        foreach (var needle in upperNeedles)
        {
            if (upperHaystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
