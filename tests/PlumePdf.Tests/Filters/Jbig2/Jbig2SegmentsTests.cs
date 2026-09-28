using PlumePdf.Filters.Jbig2;
using Xunit;

namespace PlumePdf.Tests.Filters.Jbig2;

/// <summary><see cref="Jbig2SegmentReader"/> header parsing (ITU-T T.88 §7.2), including the variable-length referred-to-segment-count encoding's short and long forms.</summary>
public class Jbig2SegmentsTests
{
    [Fact]
    public void ParseSegments_ShortFormReferredCount_ParsesCorrectly()
    {
        // Segment 5, type 0 (symbol dictionary), no referred segments (short form: top 3
        // bits of the ref-flags byte = 0), 1-byte page association = 1, 3 bytes of data.
        var buffer = new List<byte>();
        buffer.AddRange(new byte[] { 0, 0, 0, 5 }); // segment number
        buffer.Add(0); // flags: type 0, page-association 1 byte
        buffer.Add(0x00); // ref flags: short form, count 0
        buffer.Add(1); // page association
        buffer.AddRange(new byte[] { 0, 0, 0, 3 }); // data length
        buffer.AddRange(new byte[] { 0xAA, 0xBB, 0xCC });

        var segments = Jbig2SegmentReader.ParseSegments([.. buffer], maxSegments: 10);

        Assert.Single(segments);
        Assert.Equal(5u, segments[0].Number);
        Assert.Equal(Jbig2SegmentType.SymbolDictionary, segments[0].Type);
        Assert.Empty(segments[0].ReferredTo);
        Assert.Equal(1u, segments[0].PageAssociation);
        Assert.Equal(3, segments[0].DataLength);
    }

    [Fact]
    public void ParseSegments_ReferredSegments_SingleByteEach_ParsesCorrectly()
    {
        // Segment 10 (<=256, so 1-byte referred-segment-number encoding), referring to
        // segments 2 and 3 (short form: count 2 in the top 3 bits of the ref-flags byte).
        var buffer = new List<byte>();
        buffer.AddRange(new byte[] { 0, 0, 0, 10 });
        buffer.Add(38); // type 38 = immediate generic region
        buffer.Add(0x40); // ref flags: top 3 bits = 2 (count), low 5 bits = retention flags (ignored)
        buffer.Add(2);
        buffer.Add(3);
        buffer.Add(1); // page association
        buffer.AddRange(new byte[] { 0, 0, 0, 0 }); // zero-length data
        var segments = Jbig2SegmentReader.ParseSegments([.. buffer], maxSegments: 10);

        Assert.Single(segments);
        Assert.Equal([2u, 3u], segments[0].ReferredTo);
    }

    [Fact]
    public void ParseSegments_LongFormReferredCount_ParsesCorrectly()
    {
        // referredCount = 8 forces the long form (top 3 bits == 7): a 4-byte count field
        // (top 3 bits 111, low 29 bits = 8) followed by ceil((8+1)/8) = 2 retention-flag bytes,
        // then 8 referred-segment numbers (segment number 300 > 256, so each is 2 bytes).
        var buffer = new List<byte>();
        buffer.AddRange(new byte[] { 0, 0, 1, 44 }); // segment number 300
        buffer.Add(6); // type 6 = immediate text region
        buffer.AddRange(new byte[] { 0xE0, 0x00, 0x00, 0x08 }); // long form: (7<<29) | 8
        buffer.AddRange(new byte[] { 0x00, 0x00 }); // 2 retention-flag bytes
        for (ushort i = 1; i <= 8; i++)
        {
            buffer.Add((byte)(i >> 8));
            buffer.Add((byte)i);
        }

        buffer.Add(1); // page association
        buffer.AddRange(new byte[] { 0, 0, 0, 0 });

        var segments = Jbig2SegmentReader.ParseSegments([.. buffer], maxSegments: 10);

        Assert.Single(segments);
        Assert.Equal(300u, segments[0].Number);
        Assert.Equal([1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u], segments[0].ReferredTo);
    }

    [Fact]
    public void ParseSegments_FourBytePageAssociation_ParsesCorrectly()
    {
        var buffer = new List<byte>();
        buffer.AddRange(new byte[] { 0, 0, 0, 1 });
        buffer.Add(0x40 | 48); // type 48 (page info), page-association-size bit set (4 bytes)
        buffer.Add(0x00); // ref flags: count 0
        buffer.AddRange(new byte[] { 0, 0, 0x01, 0x02 }); // 4-byte page association
        buffer.AddRange(new byte[] { 0, 0, 0, 0 });

        var segments = Jbig2SegmentReader.ParseSegments([.. buffer], maxSegments: 10);

        Assert.Single(segments);
        Assert.Equal(0x0102u, segments[0].PageAssociation);
    }

    [Fact]
    public void ParseSegments_TruncatedHeader_StopsWithoutThrowing()
    {
        byte[] truncated = [0, 0, 0, 1, 0, 0]; // way too short for a complete header

        var segments = Jbig2SegmentReader.ParseSegments(truncated, maxSegments: 10);

        Assert.Empty(segments);
    }

    [Fact]
    public void ParseSegments_DeclaredDataLengthRunsPastEndOfBuffer_StopsWithoutThrowing()
    {
        var buffer = new List<byte>();
        buffer.AddRange(new byte[] { 0, 0, 0, 1 });
        buffer.Add(0);
        buffer.Add(0x00);
        buffer.Add(1);
        buffer.AddRange(new byte[] { 0, 0, 0xFF, 0xFF }); // declares 65535 bytes of data that aren't actually there

        var segments = Jbig2SegmentReader.ParseSegments([.. buffer], maxSegments: 10);

        Assert.Empty(segments);
    }

    [Fact]
    public void ParseSegments_FileOrganizationWithMagicHeader_SkipsHeaderAndParsesSegments()
    {
        var buffer = new List<byte>();
        buffer.AddRange(new byte[] { 0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A }); // ITU-T T.88 Annex D.4.1 file magic (raw bytes, not UTF-8 text).
        buffer.Add(0x02); // flags: bit1 set = "number of pages unknown" - no page-count field follows.
        buffer.AddRange(new byte[] { 0, 0, 0, 7 });
        buffer.Add(48);
        buffer.Add(0x00);
        buffer.Add(1);
        buffer.AddRange(new byte[] { 0, 0, 0, 0 });

        var segments = Jbig2SegmentReader.ParseSegments([.. buffer], maxSegments: 10);

        Assert.Single(segments);
        Assert.Equal(7u, segments[0].Number);
    }

    [Fact]
    public void RegionInfo_Parse_ReadsFieldsInBigEndianOrder()
    {
        byte[] data = [0, 0, 0, 100, 0, 0, 0, 50, 0, 0, 0, 10, 0, 0, 0, 5, 0x02];

        var info = Jbig2RegionInfo.Parse(data, 0);

        Assert.Equal(100, info.Width);
        Assert.Equal(50, info.Height);
        Assert.Equal(10, info.X);
        Assert.Equal(5, info.Y);
        Assert.Equal(2, info.CombinationOperator);
    }
}
