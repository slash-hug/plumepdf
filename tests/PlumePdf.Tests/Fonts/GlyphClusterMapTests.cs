using PlumePdf.Fonts;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>
/// Unit tests for the renderer→<c>FontObjectBuilder</c> cluster contract — exercised directly
/// against hand-built <see cref="ShapedGlyph"/> values since no production caller (the
/// renderer, <c>FontObjectBuilder</c>) wires it up yet.
/// </summary>
public class GlyphClusterMapTests
{
    [Fact]
    public void OrdinaryOneToOneGlyphs_EachGetsItsOwnCodepoint()
    {
        var map = new GlyphClusterMap();
        var text = "ax";
        var glyphs = new[]
        {
            new ShapedGlyph(GlyphId: 10, AdvanceWidth: 500, TextIndex: 0, CodepointCount: 1, Cluster: 0),
            new ShapedGlyph(GlyphId: 20, AdvanceWidth: 500, TextIndex: 1, CodepointCount: 1, Cluster: 1),
        };

        map.Record(glyphs, text);

        Assert.Equal("a", map.GlyphIdToCodepoints[10]);
        Assert.Equal("x", map.GlyphIdToCodepoints[20]);
    }

    [Fact]
    public void LigatureCluster_SingleGlyphCarriesAllItsCodepoints()
    {
        // "ffi" -> one ligature glyph, cluster id 0, CodepointCount 3 (SimpleShaper's own shape).
        var map = new GlyphClusterMap();
        var text = "ffi";
        var glyphs = new[]
        {
            new ShapedGlyph(GlyphId: 991, AdvanceWidth: 700, TextIndex: 0, CodepointCount: 3, Cluster: 0),
        };

        map.Record(glyphs, text);

        Assert.Equal("ffi", map.GlyphIdToCodepoints[991]);
    }

    [Fact]
    public void ReorderedManyToManyCluster_OnlyTheFirstGlyphInOrderCarriesCodepoints()
    {
        // A hypothetical reordered N:M cluster (e.g. a pre-base-matra Devanagari syllable):
        // two glyphs, both tagged with the same cluster id, sharing the same source-text slice.
        var map = new GlyphClusterMap();
        var text = "कि"; // KA + VOWEL SIGN I (visually reorders to matra-first)
        var glyphs = new[]
        {
            new ShapedGlyph(GlyphId: 501, AdvanceWidth: 300, TextIndex: 0, CodepointCount: 2, Cluster: 7), // matra glyph, drawn first
            new ShapedGlyph(GlyphId: 502, AdvanceWidth: 400, TextIndex: 0, CodepointCount: 2, Cluster: 7), // base glyph, same cluster
        };

        map.Record(glyphs, text);

        Assert.True(map.GlyphIdToCodepoints.ContainsKey(501));
        Assert.False(map.GlyphIdToCodepoints.ContainsKey(502));
        Assert.Equal(text, map.GlyphIdToCodepoints[501]);
    }

    [Fact]
    public void RepeatedCalls_NeverOverwriteAnAlreadyRecordedGlyphId()
    {
        var map = new GlyphClusterMap();
        map.Record([new ShapedGlyph(GlyphId: 10, AdvanceWidth: 500, TextIndex: 0, CodepointCount: 1, Cluster: 0)], "a");

        // A later line reuses glyph id 10 for an unrelated character -- the first recording wins.
        map.Record([new ShapedGlyph(GlyphId: 10, AdvanceWidth: 500, TextIndex: 0, CodepointCount: 1, Cluster: 0)], "z");

        Assert.Equal("a", map.GlyphIdToCodepoints[10]);
    }

    [Fact]
    public void UnusedGlyphId_HasNoEntry()
    {
        var map = new GlyphClusterMap();
        map.Record([new ShapedGlyph(GlyphId: 10, AdvanceWidth: 500, TextIndex: 0, CodepointCount: 1, Cluster: 0)], "a");

        Assert.False(map.GlyphIdToCodepoints.ContainsKey(999));
    }
}
