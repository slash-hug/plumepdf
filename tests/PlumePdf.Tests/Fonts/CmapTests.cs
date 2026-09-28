using System.Buffers.Binary;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Tables;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>
/// Byte-exact unit tests for <see cref="CmapTable"/>'s format 4 and format 12 subtable
/// parsers, built from hand-crafted minimal subtables rather than relying on which format a
/// real-world fixture happens to prefer (every fixture in <c>corpora/fonts/</c> ships both
/// formats simultaneously, and <see cref="CmapTable"/> always prefers format 12 when present
/// — so a real-fixture test alone would never actually exercise the format 4 parser).
/// </summary>
public class CmapTests
{
    [Fact]
    public void Format4_MapsSingleSegment()
    {
        var table = BuildCmapTableWithFormat4Subtable(codepoint: 'A', glyphId: 5);

        var cmap = CmapTable.Parse(table);

        Assert.True(cmap.TryGetGlyphId('A', out var glyphId));
        Assert.Equal(5, glyphId);
        Assert.False(cmap.TryGetGlyphId('B', out _));
    }

    [Fact]
    public void Format12_MapsSingleGroup()
    {
        var table = BuildCmapTableWithFormat12Subtable(startCode: 0x1F600, endCode: 0x1F602, startGlyphId: 100);

        var cmap = CmapTable.Parse(table);

        Assert.True(cmap.TryGetGlyphId(0x1F600, out var first));
        Assert.Equal(100, first);
        Assert.True(cmap.TryGetGlyphId(0x1F602, out var last));
        Assert.Equal(102, last);
        Assert.False(cmap.TryGetGlyphId(0x1F603, out _));
    }

    [Fact]
    public void Format12_ManyGroupsEachSpanningTheFullRange_Throws8011InsteadOfExpandingUnbounded()
    {
        // Regression test (a denial-of-service on the explicitly attacker-controlled font
        // path): TryParseFormat12 already capped a *single* group's expansion at 0x10FFFF
        // entries, but applied no cap *across* groups — numGroups is bounded only by the
        // table's byte length (12 bytes/group), so a few hundred groups each spanning a huge
        // range could force tens of minutes of single-threaded CPU from one CmapTable.Parse
        // call. 50 groups each spanning 100,000 codepoints is representative of that shape
        // (5,000,000 total entries) and must be refused, not slowly expanded.
        var table = BuildCmapTableWithManyFormat12Groups(groupCount: 50, codepointsPerGroup: 100_000);

        var ex = Assert.Throws<PlumePdfException>(() => CmapTable.Parse(table));
        Assert.Equal("PLUME8011", ex.Code);
    }

    [Fact]
    public void Format12_GroupStartingPastValidUnicodeRange_IsSkippedRatherThanProducingNegativeKeys()
    {
        // Regression test: `(int)(startCharCode + i)` truncated codepoints above int.MaxValue
        // into negative dictionary keys instead of rejecting the group. A group whose
        // startCharCode already lies past the last valid Unicode scalar value (0x10FFFF) has
        // no valid codepoint in it at all.
        var table = BuildCmapTableWithFormat12Subtable(startCode: 0x7FFFFFFF, endCode: 0x7FFFFFFF, startGlyphId: 1);

        var cmap = CmapTable.Parse(table);

        Assert.False(cmap.TryGetGlyphId(0x10FFFF, out _));
    }

    [Fact]
    public void NoUsableSubtable_Throws8008()
    {
        // version=0, numTables=1, one record pointing at an unsupported format (6).
        var table = new byte[4 + 8 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12, 2), 6); // unsupported format

        var ex = Assert.Throws<PlumePdfException>(() => CmapTable.Parse(table));
        Assert.Equal("PLUME8008", ex.Code);
    }

    [Fact]
    public void RealFixture_MapsKnownGlyph()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.Cmap.TryGetGlyphId('A', out var glyphId));
        Assert.Equal(36, glyphId);
    }

    private static byte[] BuildCmapTableWithFormat4Subtable(char codepoint, ushort glyphId)
    {
        const int segCount = 2; // one real segment + the mandatory terminal 0xFFFF segment.
        const ushort subtableLength = 16 + (segCount * 8);
        var subtable = new byte[subtableLength];

        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(0, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(2, 2), subtableLength);
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(6, 2), segCount * 2);

        var endCodesOffset = 14;
        var startCodesOffset = endCodesOffset + (segCount * 2) + 2;
        var idDeltasOffset = startCodesOffset + (segCount * 2);

        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(endCodesOffset, 2), codepoint);
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(startCodesOffset, 2), codepoint);
        BinaryPrimitives.WriteInt16BigEndian(subtable.AsSpan(idDeltasOffset, 2), unchecked((short)(glyphId - codepoint)));

        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(endCodesOffset + 2, 2), 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(startCodesOffset + 2, 2), 0xFFFF);
        BinaryPrimitives.WriteInt16BigEndian(subtable.AsSpan(idDeltasOffset + 2, 2), 1);

        return WrapCmapTable(subtable, platformId: 3, encodingId: 1);
    }

    private static byte[] BuildCmapTableWithFormat12Subtable(int startCode, int endCode, int startGlyphId)
    {
        var subtable = new byte[28];
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(0, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(4, 4), 28);
        BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(12, 4), 1); // numGroups
        BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(16, 4), (uint)startCode);
        BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(20, 4), (uint)endCode);
        BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(24, 4), (uint)startGlyphId);

        return WrapCmapTable(subtable, platformId: 3, encodingId: 10);
    }

    private static byte[] BuildCmapTableWithManyFormat12Groups(int groupCount, int codepointsPerGroup)
    {
        var subtableLength = 16 + (groupCount * 12);
        var subtable = new byte[subtableLength];
        BinaryPrimitives.WriteUInt16BigEndian(subtable.AsSpan(0, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(4, 4), (uint)subtableLength);
        BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(12, 4), (uint)groupCount);

        // Groups placed at widely-spaced, non-overlapping starting codepoints so the parser
        // can't dedupe/short-circuit them — each contributes its own full share of entries.
        for (var g = 0; g < groupCount; g++)
        {
            var groupOffset = 16 + (g * 12);
            var startCode = g * (codepointsPerGroup + 1000);
            var endCode = startCode + codepointsPerGroup - 1;
            BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(groupOffset, 4), (uint)startCode);
            BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(groupOffset + 4, 4), (uint)endCode);
            BinaryPrimitives.WriteUInt32BigEndian(subtable.AsSpan(groupOffset + 8, 4), 1);
        }

        return WrapCmapTable(subtable, platformId: 3, encodingId: 10);
    }

    private static byte[] WrapCmapTable(byte[] subtable, ushort platformId, ushort encodingId)
    {
        var table = new byte[12 + subtable.Length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), platformId);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), encodingId);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8, 4), 12);
        subtable.CopyTo(table, 12);
        return table;
    }
}
