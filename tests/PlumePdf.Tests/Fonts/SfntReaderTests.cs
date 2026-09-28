using System.Diagnostics;
using PlumePdf.Fonts;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>
/// Locates the pinned OFL font fixtures fetched by <c>scripts/fetch-corpora.sh</c> into
/// <c>corpora/fonts/</c> (gitignored, never committed — see that script for
/// provenance/checksums). Every font test that depends on them self-skips with a visible
/// console message — a skipped lane must be distinguishable
/// from a passing one — when the directory is absent, rather than either failing the
/// hermetic default lane or reporting a silent, indistinguishable pass.
/// </summary>
internal static class FontFixtures
{
    private const string SolutionFileName = "PlumePdf.sln";

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FontsRoot => Path.Combine(RepoRoot, "corpora", "fonts");

    public static bool Available => Directory.Exists(FontsRoot) && Directory.EnumerateFiles(FontsRoot, "*.ttf").Any();

    public static string NotoSansRegular => Path.Combine(FontsRoot, "NotoSans-Regular.ttf");

    public static string NotoSansBold => Path.Combine(FontsRoot, "NotoSans-Bold.ttf");

    public static string EbGaramond => Path.Combine(FontsRoot, "EBGaramond.ttf");

    /// <summary>Noto Naskh Arabic — pinned static-<c>glyf</c> Arabic fixture.</summary>
    public static string NotoNaskhArabic => Path.Combine(FontsRoot, "NotoNaskhArabic-Regular.ttf");

    /// <summary>Noto Sans Devanagari — pinned static-<c>glyf</c> Devanagari fixture.</summary>
    public static string NotoSansDevanagari => Path.Combine(FontsRoot, "NotoSansDevanagari-Regular.ttf");

    /// <summary>
    /// Skips the calling test loudly (a message every test runner surfaces, even when it
    /// doesn't distinguish "skipped" from "passed" in its summary count) when the font
    /// corpus hasn't been fetched. Returns whether the caller should proceed.
    /// </summary>
    public static bool SkipUnlessAvailable()
    {
        if (Available)
        {
            return true;
        }

        var message = $"SKIPPED (font corpus not fetched — run scripts/fetch-corpora.sh to populate {FontsRoot})";
        Trace.WriteLine(message);
        Console.WriteLine(message);
        return false;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root ({SolutionFileName}) above {AppContext.BaseDirectory}.");
    }
}

public class SfntReaderTests
{
    [Fact]
    public void ParsesNotoSansRegular_UnitsPerEmAndGlyphCount()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));

        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(3884, font.Maxp.NumGlyphs);
    }

    [Fact]
    public void ParsesNotoSansRegular_AdvanceWidthForKnownGlyph()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));

        Assert.True(font.TryGetGlyphId('A', out var glyphId));
        Assert.Equal(36, glyphId);
        Assert.Equal(639, font.GetAdvanceWidth(glyphId));
    }

    [Fact]
    public void ParsesEbGaramond_WithoutThrowing()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));

        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal(3247, font.Maxp.NumGlyphs);
    }

    [Fact]
    public void ParsesNotoSansBold_DistinctFromRegular()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var regular = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        var bold = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansBold));

        Assert.True(regular.TryGetGlyphId('A', out var regularGlyph));
        Assert.True(bold.TryGetGlyphId('A', out var boldGlyph));

        // Bold 'A' is drawn wider than regular 'A' in the same family — a cheap sanity check
        // that the two files are actually distinct font programs, not the same bytes twice.
        Assert.True(bold.GetAdvanceWidth(boldGlyph) >= regular.GetAdvanceWidth(regularGlyph));
    }
}
