using PlumePdf.Fonts;
using PlumePdf.Fonts.Substitute;
using Xunit;

namespace PlumePdf.Tests.Fonts.Substitute;

/// <summary>
/// <see cref="SubstituteFontStore"/>'s AOT-safe (reflection-free) blob lookup
/// against the real, compiled-in substitute-font bundle — 12 Liberation TrueType faces (OFL 1.1,
/// upstream liberationfonts/liberation-fonts 2.1.5 — see NOTICE) plus PDFium's own
/// two bundled Foxit CFF faces — proves the whole FieldRVA mechanism end-to-end for both
/// program formats: every bundled face parses as a real font and rasterizes a real glyph outline,
/// not just that the bytes round-trip.
/// </summary>
public class SubstituteFontStoreTests
{
    public static TheoryData<string> AllFaceKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in SubstituteFontStore.AvailableFaceKeys)
        {
            data.Add(key);
        }

        return data;
    }

    [Fact]
    public void AvailableFaceKeys_HasTwelveLiberationAndTwoFoxitFaces()
    {
        string[] expected =
        [
            "LiberationSans-Regular", "LiberationSans-Bold", "LiberationSans-Italic", "LiberationSans-BoldItalic",
            "LiberationSerif-Regular", "LiberationSerif-Bold", "LiberationSerif-Italic", "LiberationSerif-BoldItalic",
            "LiberationMono-Regular", "LiberationMono-Bold", "LiberationMono-Italic", "LiberationMono-BoldItalic",
            "FoxitSymbol", "FoxitDingbats",
        ];

        Assert.Equal(expected.Length, SubstituteFontStore.AvailableFaceKeys.Count);
        foreach (var key in expected)
        {
            Assert.Contains(key, SubstituteFontStore.AvailableFaceKeys);
        }
    }

    [Theory]
    [MemberData(nameof(AllFaceKeys))]
    public void EveryFace_LoadsAndRasterizesAGlyph(string faceKey)
    {
        switch (SubstituteFontStore.GetFaceKind(faceKey))
        {
            case SubstituteFaceKind.TrueType:
                AssertLiberationFaceLoadsAndRasterizes(faceKey);
                break;

            case SubstituteFaceKind.Cff:
                AssertFoxitFaceLoadsAndRasterizes(faceKey);
                break;

            default:
                Assert.Fail($"unexpected {nameof(SubstituteFaceKind)} for '{faceKey}'");
                break;
        }
    }

    private static void AssertLiberationFaceLoadsAndRasterizes(string faceKey)
    {
        Assert.True(SubstituteFontStore.TryGetFont(faceKey, FontReadLimits.Default, out var font));

        Assert.True(font.Maxp.NumGlyphs > 0);
        Assert.True(font.UnitsPerEm > 0);

        // Every Liberation face has a Latin 'A' — decode its real outline through the same
        // C-OUTLINE-1 path GlyfTableOutlineTests exercises against NotoSans.
        Assert.True(font.TryGetGlyphId('A', out var glyphId));
        var outline = font.Glyf.BuildOutline(glyphId, font.Limits);

        Assert.NotEmpty(outline.Commands);
        Assert.True(outline.PointCount > 0);
    }

    private static void AssertFoxitFaceLoadsAndRasterizes(string faceKey)
    {
        Assert.True(SubstituteFontStore.TryGetCffFont(faceKey, FontReadLimits.Default, out var font));

        // Glyph counts pinned against the real PDFium-at-272ef36 blobs.
        var expectedGlyphCount = faceKey == Standard14SymbolFonts.FoxitSymbolFaceKey ? 191 : 203;
        Assert.Equal(expectedGlyphCount, font.GlyphCount);

        // "alpha" (Symbol) / "a20" (Dingbats, the heavy check mark) are real named glyphs in each
        // face's own charset — proves name-keyed lookup, not just that the bytes parse.
        var probeGlyphName = faceKey == Standard14SymbolFonts.FoxitSymbolFaceKey ? "alpha" : "a20";
        Assert.True(font.TryGetGlyphId(probeGlyphName, out var glyphId));
        var outline = font.GetGlyphOutline(glyphId);
        Assert.True(outline.Commands.Count > 0);

        Assert.True(font.HasGlyphNames);
        Assert.Equal(1000.0, font.UnitsPerEm);
        Assert.NotNull(font.BuiltInEncoding); // a predefined (Standard) built-in encoding offset.
    }

    [Fact]
    public void TryGetFont_CachesAcrossCalls_SameInstanceReturned()
    {
        Assert.True(SubstituteFontStore.TryGetFont("LiberationSans-Regular", FontReadLimits.Default, out var first));
        Assert.True(SubstituteFontStore.TryGetFont("LiberationSans-Regular", FontReadLimits.Default, out var second));

        Assert.Same(first, second);
    }

    [Fact]
    public void TryGetFont_UnknownKey_ReturnsFalse_DoesNotThrow()
    {
        Assert.False(SubstituteFontStore.TryGetFont("NotABundledFace", FontReadLimits.Default, out var font));
        Assert.Null(font);
    }

    [Fact]
    public void TryGetCffFont_CachesAcrossCalls_SameInstanceReturned()
    {
        Assert.True(SubstituteFontStore.TryGetCffFont("FoxitSymbol", FontReadLimits.Default, out var first));
        Assert.True(SubstituteFontStore.TryGetCffFont("FoxitSymbol", FontReadLimits.Default, out var second));

        Assert.Same(first, second);
    }

    [Fact]
    public void TryGetCffFont_UnknownKey_ReturnsFalse_DoesNotThrow()
    {
        Assert.False(SubstituteFontStore.TryGetCffFont("NotABundledFace", FontReadLimits.Default, out var font));
        Assert.Null(font);
    }

    [Fact]
    public void TryGetCffFont_OnATrueTypeKey_ReturnsFalse_KindMismatchIsNotAThrow()
    {
        // A key that names a bundled Liberation (TrueType) face is not in SubstituteCffBlobs —
        // a kind mismatch, documented as a programmer error, never surfaces as an exception.
        Assert.False(SubstituteFontStore.TryGetCffFont("LiberationSans-Regular", FontReadLimits.Default, out var font));
        Assert.Null(font);
    }

    [Fact]
    public void TryGetFont_OnACffKey_ReturnsFalse_KindMismatchIsNotAThrow()
    {
        // The mirror image: a key that names a bundled Foxit (CFF) face is not in
        // SubstituteFontBlobs.
        Assert.False(SubstituteFontStore.TryGetFont("FoxitSymbol", FontReadLimits.Default, out var font));
        Assert.Null(font);
    }

    [Fact]
    public void FoxitBlob_UnderTinyGlyphLimit_ParseThrowsCodedException_NotBare()
    {
        // FoxitSymbol declares 191 glyphs — far more than this artificially tiny ceiling. The
        // limit behaviour is tested on the parser directly: the store's
        // caches are process-wide and keyed by face key alone, so observing "the very first parse
        // under a tiny limit" through the store needed a test-only cache-clearing hook and a
        // serialized xUnit collection, which only reordered the race rather than removing it.
        Assert.True(SubstituteCffBlobs.TryGetBlob("FoxitSymbol", out var data));
        var bytes = data.ToArray(); // a ReadOnlySpan cannot be captured by the lambda below
        var tinyLimits = FontReadLimits.Default with { MaxFontGlyphCount = 10 };

        var ex = Assert.Throws<PlumePdfException>(() => PlumePdf.Fonts.Outlines.CffParser.Parse(bytes, tinyLimits));

        Assert.Equal("PLUME8005", ex.Code);
    }

    [Fact]
    public void TryGetCffFont_UnknownKey_ReturnsFalse_AndKnownKeyIsCachedOnce()
    {
        Assert.False(SubstituteFontStore.TryGetCffFont("NoSuchFace", FontReadLimits.Default, out var missing));
        Assert.Null(missing);

        // Same contract as the TrueType cache: keyed by face key alone, one
        // parsed instance per key for the process lifetime.
        Assert.True(SubstituteFontStore.TryGetCffFont("FoxitSymbol", FontReadLimits.Default, out var first));
        Assert.True(SubstituteFontStore.TryGetCffFont("FoxitSymbol", FontReadLimits.Default, out var second));
        Assert.Same(first, second);
    }

    [Fact]
    public void SubstituteFontMap_Resolve_ThenStore_TryGetFont_RoundTripsForEveryCommonStyleCombination()
    {
        foreach (var bold in new[] { false, true })
        {
            foreach (var italic in new[] { false, true })
            {
                var resolution = SubstituteFontMap.Resolve("Arial", bold, italic);
                Assert.NotNull(resolution.FaceKey);
                Assert.True(SubstituteFontStore.TryGetFont(resolution.FaceKey!, FontReadLimits.Default, out var font));
                Assert.True(font.Maxp.NumGlyphs > 0);
            }
        }
    }
}
