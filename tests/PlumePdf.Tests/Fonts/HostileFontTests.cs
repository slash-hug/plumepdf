using System.Buffers.Binary;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Tables;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>
/// Hand-crafted hostile/malformed SFNT inputs — self-contained (no fetched fixtures needed):
/// truncated table directory, a table whose declared offset+length runs past end of file, a
/// glyph count over the configured limit, and a composite-glyph reference cycle. Every case
/// must raise a coded <see cref="PlumePdfException"/>, never a bare <see cref="OverflowException"/>
/// or a stack overflow (the "untrusted font input" hardening rule).
/// </summary>
public class HostileFontTests
{
    [Fact]
    public void TruncatedTableDirectory_Throws8002()
    {
        // Header declares numTables=2 (needs 12 + 2*16 = 44 bytes) but the buffer is only 16 bytes.
        var data = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4, 2), 2);

        var ex = Assert.Throws<PlumePdfException>(() => SfntFont.Parse(data, FontReadLimits.Default));
        Assert.Equal("PLUME8002", ex.Code);
    }

    [Fact]
    public void TableLengthPastEndOfFile_Throws8004()
    {
        // One table record ("head") whose declared offset+length runs past the 28-byte file.
        var data = new byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4, 2), 1);
        "head"u8.CopyTo(data.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), 28); // offset
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24, 4), 100); // length — runs past EOF

        var ex = Assert.Throws<PlumePdfException>(() => SfntFont.Parse(data, FontReadLimits.Default));
        Assert.Equal("PLUME8004", ex.Code);
    }

    [Fact]
    public void FileExceedsMaxFontFileBytes_Throws8001()
    {
        var data = new byte[64];
        var tinyLimit = FontReadLimits.Default with { MaxFontFileBytes = 32 };

        var ex = Assert.Throws<PlumePdfException>(() => SfntFont.Parse(data, tinyLimit));
        Assert.Equal("PLUME8001", ex.Code);
    }

    [Fact]
    public void GlyphCountOverLimit_Throws8005()
    {
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4, 2), 20); // numGlyphs = 20
        var tinyLimit = FontReadLimits.Default with { MaxFontGlyphCount = 10 };

        var ex = Assert.Throws<PlumePdfException>(() => MaxpTable.Parse(maxp, tinyLimit));
        Assert.Equal("PLUME8005", ex.Code);
    }

    [Fact]
    public void CyclicCompositeGlyph_ThrowsRatherThanOverflowingTheStack()
    {
        // Two composite glyphs that reference each other: glyph 0 -> component glyph 1 ->
        // component glyph 0. A naive recursive resolver would stack-overflow; the work-queue
        // walk must detect this as a cycle and throw a coded exception instead.
        var glyf = BuildTwoGlyphCycle();
        var locaBytes = new byte[12]; // 3 entries (numGlyphs=2 + 1) x 4 bytes, long format.
        BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(4, 4), 18);
        BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(8, 4), 36);
        var loca = LocaTable.Parse(locaBytes.AsMemory(), numGlyphs: 2, longFormat: true);
        var table = GlyfTable.Wrap(glyf, glyfTableOffset: 0, glyfTableLength: glyf.Length, loca);

        var glyphIds = new HashSet<int> { 0 };
        var ex = Assert.Throws<PlumePdfException>(() => table.ResolveCompositeClosure(glyphIds, maxDepth: 8));
        Assert.Equal("PLUME8007", ex.Code);
    }

    [Fact]
    public void CompositeNestingBeyondMaxDepth_Throws8007()
    {
        // A linear chain of composites nested deeper than the configured max depth (no cycle
        // — every glyph is distinct — but the chain itself is too deep to be legitimate).
        const int chainLength = 20;
        var glyf = BuildLinearCompositeChain(chainLength);
        var locaBytes = new byte[(chainLength + 1) * 4];
        for (var i = 0; i <= chainLength; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(i * 4, 4), (uint)(i * 18));
        }

        var loca = LocaTable.Parse(locaBytes.AsMemory(), numGlyphs: chainLength, longFormat: true);
        var table = GlyfTable.Wrap(glyf, 0, glyf.Length, loca);

        var glyphIds = new HashSet<int> { 0 };
        var ex = Assert.Throws<PlumePdfException>(() => table.ResolveCompositeClosure(glyphIds, maxDepth: 8));
        Assert.Equal("PLUME8007", ex.Code);
    }

    /// <summary>Builds two 18-byte composite glyphs, each with exactly one component referencing the other.</summary>
    private static byte[] BuildTwoGlyphCycle()
    {
        var data = new byte[36];
        WriteCompositeGlyphStub(data.AsSpan(0, 18), componentGlyphId: 1);
        WriteCompositeGlyphStub(data.AsSpan(18, 18), componentGlyphId: 0);
        return data;
    }

    /// <summary>Builds <paramref name="length"/> 18-byte composite glyphs, glyph <c>i</c> referencing glyph <c>i + 1</c> (glyph <paramref name="length"/> - 1 references itself as a harmless leaf — never visited since the chain is capped first).</summary>
    private static byte[] BuildLinearCompositeChain(int length)
    {
        var data = new byte[length * 18];
        for (var i = 0; i < length; i++)
        {
            var next = i + 1 < length ? i + 1 : i; // last glyph references itself as a simple leaf-ish stub; never reached before the depth cap trips.
            WriteCompositeGlyphStub(data.AsSpan(i * 18, 18), componentGlyphId: next);
        }

        return data;
    }

    private static void WriteCompositeGlyphStub(Span<byte> glyph, int componentGlyphId)
    {
        BinaryPrimitives.WriteInt16BigEndian(glyph[0..2], -1); // numberOfContours: composite marker.
        // xMin/yMin/xMax/yMax left as zero.
        BinaryPrimitives.WriteUInt16BigEndian(glyph[10..12], 0x0003); // ARG_1_AND_2_ARE_WORDS | ARGS_ARE_XY_VALUES, no MORE_COMPONENTS.
        BinaryPrimitives.WriteUInt16BigEndian(glyph[12..14], checked((ushort)componentGlyphId));
        // dx/dy args (int16 x2) left as zero — 4 more bytes, total 18.
    }
}
