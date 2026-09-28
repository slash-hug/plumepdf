using PlumePdf.Fonts;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>
/// An outline-format gate: verifies the pinned Arabic/Devanagari
/// shaping fixtures (<c>scripts/fetch-corpora.sh</c>'s Noto Naskh Arabic / Noto Sans
/// Devanagari stanza) are static-<c>glyf</c> builds — <c>glyf</c>/<c>loca</c> present, no
/// <c>CFF </c>/<c>CFF2</c>/<c>fvar</c> — since <c>FontSubsetter</c> is <c>glyf</c>-only
/// and discovering a CFF-only fixture late would be a schedule blocker. Also
/// asserts each carries the GSUB script record its shaping tier needs (<c>arab</c>;
/// <c>dev2</c> or <c>deva</c>), so later work cannot silently build against a fixture
/// that doesn't actually exercise script-specific substitution. Self-skips loudly (never
/// silently) when the font corpus hasn't been fetched — a skipped lane must be
/// distinguishable from a passing one, so it never reports a
/// vacuous pass.
/// </summary>
public class FontFixtureOutlineFormatTests
{
    [Fact]
    public void NotoNaskhArabic_IsStaticGlyfWithArabicGsubScript()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoNaskhArabic));

        AssertStaticGlyfOutline(font);

        var scripts = ReadGsubScriptTags(font.RawData);
        Assert.Contains("arab", scripts);
    }

    [Fact]
    public void NotoSansDevanagari_IsStaticGlyfWithDevanagariGsubScript()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansDevanagari));

        AssertStaticGlyfOutline(font);

        var scripts = ReadGsubScriptTags(font.RawData);
        Assert.True(scripts.Contains("dev2") || scripts.Contains("deva"), $"expected 'dev2' or 'deva' among GSUB script tags, found: {string.Join(", ", scripts)}");
    }

    private static void AssertStaticGlyfOutline(TrueTypeFontProgram font)
    {
        var tables = font.Sfnt.Tables;
        Assert.True(tables.ContainsKey("glyf"), "expected a 'glyf' table (static TrueType outlines)");
        Assert.True(tables.ContainsKey("loca"), "expected a 'loca' table (static TrueType outlines)");
        Assert.False(tables.ContainsKey("CFF "), "CFF-flavored OpenType is out of scope for the subsetter");
        Assert.False(tables.ContainsKey("CFF2"), "CFF2-flavored OpenType is out of scope for the subsetter");
        Assert.False(tables.ContainsKey("fvar"), "variable-font instancing is out of scope for the subsetter");
    }

    /// <summary>
    /// Reads the OpenType <c>GSUB</c> table's <c>ScriptList</c> script tags directly from the
    /// raw font bytes (OpenType spec's public table-directory/ScriptList layout — the
    /// clean-room policy in AGENTS.md keeps this to no other library's source). This is
    /// deliberately test-local: the production
    /// <c>GsubTable</c> extractor doesn't expose script/langsys structure yet (that lands with
    /// the real lookup executor) — this fixture gate only needs to prove the *data* is
    /// present in the font, not shape anything.
    /// </summary>
    private static List<string> ReadGsubScriptTags(byte[] fontData)
    {
        var sfnt = SfntFont.Parse(fontData, FontReadLimits.Default);
        if (!sfnt.TryGetTable("GSUB", out var gsubMemory))
        {
            return [];
        }

        var gsub = gsubMemory.Span;
        Assert.True(SfntPrimitives.TryReadUInt16(gsub, 4, out var scriptListOffset), "malformed GSUB header");

        var scriptList = gsub[scriptListOffset..];
        Assert.True(SfntPrimitives.TryReadUInt16(scriptList, 0, out var scriptCount), "malformed GSUB ScriptList");

        var tags = new List<string>(scriptCount);
        for (var i = 0; i < scriptCount; i++)
        {
            var recordOffset = 2 + i * 6;
            Assert.True(SfntPrimitives.TryReadTag(scriptList, recordOffset, out var tag), "malformed GSUB ScriptRecord");
            tags.Add(tag);
        }

        return tags;
    }
}
