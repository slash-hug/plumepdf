using PlumePdf.Fonts.Substitute;
using Xunit;

namespace PlumePdf.Tests.Fonts.Substitute;

/// <summary><see cref="SubstituteFontMap"/>'s name/flags-driven face resolution and its CJK-gap coded diagnostic.</summary>
public class SubstituteFontMapTests
{
    [Theory]
    [InlineData("Arial", false, false, false, false, "LiberationSans-Regular")]
    [InlineData("Arial-BoldMT", false, false, false, false, "LiberationSans-Bold")] // name-derived bold, no explicit flag.
    [InlineData("Helvetica", true, false, false, false, "LiberationSans-Bold")]
    [InlineData("Helvetica", true, true, false, false, "LiberationSans-BoldItalic")]
    [InlineData("Helvetica-Oblique", false, false, false, false, "LiberationSans-Italic")]
    [InlineData("TimesNewRomanPSMT", false, false, false, false, "LiberationSerif-Regular")]
    [InlineData("Times New Roman", false, false, true, false, "LiberationSerif-Regular")]
    [InlineData("Georgia-Bold", false, false, false, false, "LiberationSerif-Bold")]
    [InlineData("CourierNewPSMT", false, false, false, false, "LiberationMono-Regular")]
    [InlineData("Consolas", false, false, false, true, "LiberationMono-Regular")]
    [InlineData("SomeRandomFontName", false, false, false, false, "LiberationSans-Regular")] // unknown -> sans-serif default.
    public void Resolve_PicksExpectedFace(string baseFontName, bool bold, bool italic, bool serif, bool fixedPitch, string expectedFaceKey)
    {
        var result = SubstituteFontMap.Resolve(baseFontName, bold, italic, serif, fixedPitch);

        Assert.Equal(expectedFaceKey, result.FaceKey);
        Assert.Equal("PLUME7510", result.Diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Info, result.Diagnostic.Severity);
    }

    [Theory]
    [InlineData("SimSun")]
    [InlineData("MS Gothic")]
    [InlineData("Microsoft YaHei")]
    [InlineData("Noto Sans CJK JP")]
    [InlineData("PMingLiU")]
    public void Resolve_CjkNamedFont_ReturnsGapDiagnosticAndNoFace(string baseFontName)
    {
        var result = SubstituteFontMap.Resolve(baseFontName);

        Assert.Null(result.FaceKey);
        Assert.Equal("PLUME7511", result.Diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, result.Diagnostic.Severity);
        Assert.Contains(baseFontName, result.Diagnostic.Message);
    }

    [Fact]
    public void Resolve_NullBaseFontName_DoesNotThrow_DefaultsToSansRegular()
    {
        var result = SubstituteFontMap.Resolve(null);

        Assert.Equal("LiberationSans-Regular", result.FaceKey);
    }

    [Fact]
    public void Resolve_MonoWinsOverSerifWhenBothMatch()
    {
        // A font named e.g. "Courier-Serif-ish" (contrived) should still resolve to Mono —
        // fixed-pitch is a stronger, mutually-exclusive family signal than serif detection.
        var result = SubstituteFontMap.Resolve("Courier Serif Special", fixedPitch: true, serif: true);

        Assert.Equal("LiberationMono-Regular", result.FaceKey);
    }

    // =========================================================================================
    // The Foxit Symbol/Dingbats exact-name rule, gated on isType1Subtype.
    // =========================================================================================

    [Theory]
    [InlineData("Symbol", "FoxitSymbol")]
    [InlineData("SymbolMT", "FoxitSymbol")]
    [InlineData("ZapfDingbats", "FoxitDingbats")]
    public void Resolve_StandardSymbolNameWithType1Subtype_PicksFoxitFace(string baseFontName, string expectedFaceKey)
    {
        var result = SubstituteFontMap.Resolve(baseFontName, isType1Subtype: true);

        Assert.Equal(expectedFaceKey, result.FaceKey);
        Assert.Equal("PLUME7510", result.Diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Info, result.Diagnostic.Severity);
        Assert.Contains(expectedFaceKey, result.Diagnostic.Message);
    }

    [Theory]
    [InlineData("Symbol")]
    [InlineData("SymbolMT")]
    public void Resolve_StandardSymbolName_WithoutType1Subtype_KeepsLiberationDefault(string baseFontName)
    {
        // isType1Subtype defaults to false — the historical behaviour (e.g. a /Subtype /TrueType
        // font merely named "Symbol") must be unchanged.
        var result = SubstituteFontMap.Resolve(baseFontName);

        Assert.Equal("LiberationSans-Regular", result.FaceKey);
        Assert.Equal("PLUME7510", result.Diagnostic.Code);
    }

    [Theory]
    [InlineData("ZapfDingbats", false)]
    [InlineData("Dingbats", false)]
    [InlineData("Dingbats", true)] // not one of PDFium's aliases even as Type 1 — falls to the heuristic, not the Foxit face
    public void Resolve_DingbatsNames_OutsideTheFoxitRule_DegradeToLiberation_NotTheCjkGap(string name, bool isType1Subtype)
    {
        // "ZAPFDIN[GB]ATS" and "DIN[GB]ATS" contain the "GB" CJK marker by accident of spelling; they
        // used to fall into the CJK gap (PLUME7511, null face — blank .notdef) but
        // must degrade to a Latin face instead. (PDFium draws the Foxit Dingbats
        // face for a TrueType-subtype ZapfDingbats too; routing that case there is a follow-up.)
        var result = SubstituteFontMap.Resolve(name, isType1Subtype: isType1Subtype);

        Assert.NotNull(result.FaceKey);
        Assert.StartsWith("Liberation", result.FaceKey, StringComparison.Ordinal);
        Assert.Equal("PLUME7510", result.Diagnostic.Code);
    }
}
