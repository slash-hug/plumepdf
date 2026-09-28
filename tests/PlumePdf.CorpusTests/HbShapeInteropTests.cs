using System.Text.Json;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// External-oracle interop for Phase 6.5's shaper boundary:
/// runs the <c>hb-shape</c> CLI — HarfBuzz's own reference shaper, the ARMED external half of
/// the dual conformance oracle (the hermetic half is <see cref="ShapingOracleTests"/>) — and
/// compares its live output against <see cref="SimpleShaper"/>'s for Latin text and against
/// <see cref="ComplexShaper"/>'s for the same curated Arabic/Devanagari fixture strings
/// <see cref="ShapingOracleTests"/> already pins as golden data — the live half of that same
/// dual oracle, not a separate scope. <c>ComplexShaper</c> is installed at both
/// production call sites (<c>TextLayouter</c>/<c>ManuscriptRenderer</c>), and now exercised here
/// against the live external oracle too, not only the hermetic golden-data replay.
/// </summary>
/// <remarks>
/// <para>
/// Follows the exact ARMED-lane shape <see cref="VeraPdfInteropTests"/> established: skips
/// (no-ops) when <c>hb-shape</c> isn't installed, UNLESS <c>PLUMEPDF_REQUIRE_HBSHAPE=1</c> is
/// set, in which case an absent tool is a test FAILURE, never a skip. The CI <c>corpus</c> job
/// builds hb-shape from a version+SHA-256 pinned HarfBuzz source release (no prebuilt Linux CLI
/// asset exists upstream) and exports that variable, so "tool expected but not armed" can never
/// read as green there.
/// </para>
/// <para>
/// The Latin comparison strings exercise <see cref="SimpleShaper"/> — a boundary/plumbing proof
/// (the probe, invocation, and glyph-stream comparison are correct). Ligature strings ("fi",
/// "ffi") specifically exercise the same N-codepoints-to-one-glyph substitution shape
/// complex-script conjuncts/ligatures share. GPOS pair kerning is explicitly excluded from both
/// sides for the Latin comparison (<c>hb-shape --features=...,-kern</c>): <see cref="SimpleShaper"/>'s
/// kern support is a pre-existing, separately-scoped Phase 2 concern this lane is not the place
/// to audit, and mixing it in would make an otherwise-stable oracle lane flaky against
/// font-specific kerning coverage gaps unrelated to Phase 6.5. The Arabic/Devanagari comparison
/// runs hb-shape with its own script-default feature set (no <c>--features</c> override — the
/// same invocation <see cref="ShapingOracleTests"/>' golden data was captured with), matching
/// what <see cref="ComplexShaper"/>/<see cref="OpenTypeLayoutEngine"/> apply for real; glyph ID,
/// advance, and GPOS X/Y offset are compared (never cluster numbering — see
/// <see cref="ShapingOracleTests"/>'s own remarks on why), and an Arabic (right-to-left) run's
/// hb-shape output is reversed back to logical order first, exactly as the hermetic lane does.
/// </para>
/// </remarks>
public class HbShapeInteropTests
{
    // Internal (not private): mirrors VeraPdfInteropTests' shape so a future ShapingOracleTests/
    // exit-demo reuse doesn't have to duplicate the probe.
    internal static readonly bool HbShapeAvailable = ProbeHbShape();

    private static bool ProbeHbShape()
    {
        // The probe inspects output rather than trusting Process.Start alone (matching
        // VeraPdfInteropTests/PdfsigInteropTests' precedent): an unrelated binary that happens
        // to be named "hb-shape" must not arm the lane. This "HarfBuzz"-in-output predicate is
        // deliberately the SAME check ci.yml's install step makes after building — the two
        // must agree on what "armed" means.
        var (started, _, stdout, stderr) = ExternalTool.TryRun("hb-shape", "--version");
        return started && (stdout + stderr).Contains("HarfBuzz", StringComparison.Ordinal);
    }

    /// <summary>
    /// The self-skip sentinel, made unable to lie (the CI-gating requirement): returns
    /// <c>true</c> when <c>hb-shape</c> probed available; returns <c>false</c> (callers skip)
    /// when it's absent on a hermetic machine; but when <c>PLUMEPDF_REQUIRE_HBSHAPE=1</c> — set
    /// by the CI <c>corpus</c> job, which builds the tool in a hard-failing step — an absent
    /// tool FAILS the test instead of skipping, so an unarmed oracle lane can never read as
    /// green.
    /// </summary>
    internal static bool HbShapeAvailableOrFailIfRequired()
    {
        if (HbShapeAvailable)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_HBSHAPE") == "1")
        {
            Assert.Fail("PLUMEPDF_REQUIRE_HBSHAPE=1 but the hb-shape CLI probe found no working tool — this lane built hb-shape and expects it armed; a self-skip here would silently disable the CI gate.");
        }

        return false;
    }

    [Fact]
    public void AbsentTool_IsDetectablyAbsent()
    {
        // Proves the detection mechanism itself works, independent of whether hb-shape happens
        // to be installed on the machine running this test: a definitely-nonexistent binary
        // name must probe as unavailable, exactly the shape a real absent-tool CI lane would see.
        var (started, _, _, _) = ExternalTool.TryRun("hb-shape-definitely-not-a-real-binary-mrz7", "--version");
        Assert.False(started);
    }

    /// <summary>One glyph hb-shape's JSON output (<c>--output-format=json --no-glyph-names</c>) reports: <c>g</c> (glyph ID), <c>cl</c> (cluster — the first UTF-16 index of the input this glyph's cluster starts at, matching <see cref="ShapedGlyph.TextIndex"/>'s semantics for a monotone left-to-right run), <c>ax</c>/<c>dx</c>/<c>dy</c> (X advance and X/Y placement offset, font units — the same units <see cref="IFontMetrics.GetAdvanceWidth"/> reports since neither side scales by a point size).</summary>
    private readonly record struct HbShapeGlyph(int GlyphId, int Cluster, int AdvanceX, int OffsetX, int OffsetY);

    private static IReadOnlyList<HbShapeGlyph> RunHbShape(string fontPath, string text, string? features = null)
    {
        var featureArg = features is null ? string.Empty : $" --features=\"{features}\"";
        var arguments = $"--output-format=json --no-glyph-names{featureArg} \"{fontPath}\" \"{text}\"";
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun("hb-shape", arguments);
        Assert.True(started, "hb-shape did not start even though the probe reported it available.");
        Assert.True(exitCode == 0, $"hb-shape exited {exitCode} for text \"{text}\": {stderr}");

        using var document = JsonDocument.Parse(stdout);
        var glyphs = new List<HbShapeGlyph>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            glyphs.Add(new HbShapeGlyph(
                element.GetProperty("g").GetInt32(),
                element.GetProperty("cl").GetInt32(),
                element.GetProperty("ax").GetInt32(),
                element.TryGetProperty("dx", out var dx) ? dx.GetInt32() : 0,
                element.TryGetProperty("dy", out var dy) ? dy.GetInt32() : 0));
        }

        return glyphs;
    }

    /// <summary>
    /// Curated Latin test strings ("generated test strings"): plain runs, and the two
    /// ligature-bearing strings <see cref="ToUnicodeClusterTests"/>' regression pins the glyph
    /// IDs for (997/991 in EBGaramond) — the N-codepoints-to-one-glyph shape this lane exists to
    /// keep honest against an independent reference shaper.
    /// </summary>
    public static TheoryData<string> LatinTestStrings => new()
    {
        "AVATAR",
        "Type",
        "Waffle",
        "fi",
        "ffi",
        "Hello, World!",
    };

    [Theory]
    [MemberData(nameof(LatinTestStrings))]
    public void SimpleShaperOutput_MatchesHbShapeReferenceOutput(string text)
    {
        if (!HbShapeAvailableOrFailIfRequired() || !CorpusFixture.CorporaAvailable)
        {
            Console.WriteLine($"SKIPPED (requires the hb-shape CLI and {CorpusFixture.CorporaRoot}/fonts — run scripts/fetch-corpora.sh)");
            return;
        }

        var fontPath = Path.Combine(CorpusFixture.CorporaRoot, "fonts", "EBGaramond.ttf");
        if (!File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED (requires {fontPath} — run scripts/fetch-corpora.sh)");
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(fontPath));
        var shaped = new SimpleShaper().Shape(text, font, ShapingOptions.Default);

        // liga stays on (matches SimpleShaper's GSUB ligature support); kern stays off (see
        // the type's remarks); every other default-on OpenType feature HarfBuzz's default
        // shaper would normally apply is explicitly disabled so a font that happens to declare
        // ccmp/clig/calt/rlig/mark/mkmk features can't silently diverge this comparison from
        // what SimpleShaper actually implements.
        var reference = RunHbShape(fontPath, text, "-ccmp,-clig,-calt,-rlig,-curs,-mark,-mkmk,+liga,-kern");

        Assert.Equal(reference.Count, shaped.Glyphs.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].GlyphId, shaped.Glyphs[i].GlyphId);
            Assert.Equal(reference[i].Cluster, shaped.Glyphs[i].TextIndex);
            Assert.Equal(reference[i].AdvanceX, shaped.Glyphs[i].AdvanceWidth);
        }
    }

    [Fact]
    public void LatinTestStrings_IsNonEmpty()
    {
        // Corpus-lesson discipline, applied to a fixed in-source fixture set too: prove
        // the Theory actually has cases to run rather than trusting an empty TheoryData to read
        // as a vacuous pass.
        Assert.NotEmpty(LatinTestStrings);
    }

    /// <summary>
    /// The same curated real-word Arabic fixtures <see cref="ShapingOracleTests.ArabicFixtures"/>
    /// pins as hermetic golden data — reused here, run live, so the ARMED external oracle
    /// actually validates the feature this phase exists to ship, not
    /// only Latin text a pre-6.5 shaper already handled. Excludes nothing: every one of these now
    /// matches hb-shape (the lam-alef ligature root-cause fix landed the last excluded case).
    /// </summary>
    public static TheoryData<string> ArabicTestStrings => new()
    {
        "على",
        "كتاب",
        "مَعَ",
        "بِسْمِ",
        "لا",
    };

    /// <summary>
    /// The same curated real-word Devanagari fixtures <see cref="ShapingOracleTests.DevanagariFixtures"/>
    /// pins as hermetic golden data. Excludes nothing: "र्क" (reph), "कं"/"दिसंबर" (anusvara),
    /// and "क्त" (dist kern) — the cases this lane used to exclude for the then-not-root-caused
    /// mark-positioning divergence — are included since the direction-dependent mark-attachment
    /// fix (see <c>OpenTypeLayoutEngine.PenRelativeAttachmentDelta</c>) and the
    /// first-matching-GPOS-subtable fix landed.
    /// </summary>
    public static TheoryData<string> DevanagariTestStrings => new()
    {
        "क्ष",
        "कि",
        "हिन्दी",
        "मित्र",
        "कं",
        "र्क",
        "दिसंबर",
        "क्त",
    };

    [Theory]
    [MemberData(nameof(ArabicTestStrings))]
    public void ComplexShaperOutput_MatchesLiveHbShapeReferenceOutput_Arabic(string text)
    {
        if (!HbShapeAvailableOrFailIfRequired() || !CorpusFixture.CorporaAvailable)
        {
            Console.WriteLine($"SKIPPED (requires the hb-shape CLI and {CorpusFixture.CorporaRoot}/fonts — run scripts/fetch-corpora.sh)");
            return;
        }

        var fontPath = Path.Combine(CorpusFixture.CorporaRoot, "fonts", "NotoNaskhArabic-Regular.ttf");
        if (!File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED (requires {fontPath} — run scripts/fetch-corpora.sh)");
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(fontPath));
        var shaped = new ComplexShaper().Shape(text, font, ShapingOptions.Default);

        // hb-shape's default output for an Arabic (right-to-left) run is already visually
        // reordered; ComplexShaper never reverses (that is TextLayouter's job) — reverse
        // hb-shape's raw glyph list back to logical order before comparing, exactly as
        // ShapingOracleTests' golden data was captured.
        var reference = RunHbShape(fontPath, text).Reverse().ToList();

        AssertGlyphsMatch(reference, shaped, text);
    }

    [Theory]
    [MemberData(nameof(DevanagariTestStrings))]
    public void ComplexShaperOutput_MatchesLiveHbShapeReferenceOutput_Devanagari(string text)
    {
        if (!HbShapeAvailableOrFailIfRequired() || !CorpusFixture.CorporaAvailable)
        {
            Console.WriteLine($"SKIPPED (requires the hb-shape CLI and {CorpusFixture.CorporaRoot}/fonts — run scripts/fetch-corpora.sh)");
            return;
        }

        var fontPath = Path.Combine(CorpusFixture.CorporaRoot, "fonts", "NotoSansDevanagari-Regular.ttf");
        if (!File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED (requires {fontPath} — run scripts/fetch-corpora.sh)");
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(fontPath));
        var shaped = new ComplexShaper().Shape(text, font, ShapingOptions.Default);

        // Devanagari is LTR-storage — hb-shape's default output is already in logical order.
        var reference = RunHbShape(fontPath, text);

        AssertGlyphsMatch(reference, shaped, text);
    }

    /// <summary>Glyph ID, advance, and GPOS X/Y placement offset — never cluster numbering (see <see cref="ShapingOracleTests"/>'s own remarks on why <see cref="ComplexShaper"/>'s N:M cluster bookkeeping isn't compared against hb-shape's own convention).</summary>
    private static void AssertGlyphsMatch(IReadOnlyList<HbShapeGlyph> expected, ShapedRun actual, string description)
    {
        Assert.True(expected.Count == actual.Glyphs.Count, $"{description}: expected {expected.Count} glyphs, got {actual.Glyphs.Count}.");
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var g = actual.Glyphs[i];
            Assert.True(e.GlyphId == g.GlyphId, $"{description}[{i}]: glyph id expected {e.GlyphId}, got {g.GlyphId}.");
            Assert.True(e.AdvanceX == g.AdvanceWidth, $"{description}[{i}]: advance expected {e.AdvanceX}, got {g.AdvanceWidth}.");
            Assert.True(e.OffsetX == g.XOffset, $"{description}[{i}]: XOffset expected {e.OffsetX}, got {g.XOffset}.");
            Assert.True(e.OffsetY == g.YOffset, $"{description}[{i}]: YOffset expected {e.OffsetY}, got {g.YOffset}.");
        }
    }

    [Fact]
    public void ArabicTestStrings_IsNonEmpty() => Assert.NotEmpty(ArabicTestStrings);

    [Fact]
    public void DevanagariTestStrings_IsNonEmpty() => Assert.NotEmpty(DevanagariTestStrings);
}
