using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxBitReader"/>'s bit-stuffing (B.10.1) and
/// <see cref="JpxTagTree"/>'s resumable inclusion/value decoding (B.10.2).
/// </summary>
/// <remarks>
/// Both worked examples below are hand-traced bit sequences for a tag tree built and queried by
/// this test (not transcribed from the standard's own figure — see the comments at each test for
/// the pencil-and-paper derivation), so every expected byte is independently verifiable from the
/// tag-tree algorithm's own definition: an internal node's value is the minimum of its children,
/// and a node's value is signalled to the decoder as a unary code (<c>V</c> zero-bits then one
/// terminating 1-bit) read only when the decoder actually needs a bit more precision than it
/// already has for the current query's threshold.
/// </remarks>
public class JpxTagTreeTests
{
    // ---- JpxBitReader: B.10.1 bit-stuffing ----

    [Fact]
    public void ReadBits_PlainBytes_NoStuffingNeeded()
    {
        byte[] data = [0b1011_0010, 0b0110_1101];
        var reader = new JpxBitReader(data, 0);

        Assert.Equal(0b1011, reader.ReadBits(4));
        Assert.Equal(0b0010, reader.ReadBits(4));
        Assert.Equal(0b0110, reader.ReadBits(4));
        Assert.Equal(0b1101, reader.ReadBits(4));
        Assert.Equal(2, reader.Position);
    }

    [Fact]
    public void ReadBits_AfterFF_ByteContributesOnlySevenBits()
    {
        // 0xFF fully consumed (8 bits), then the byte-stuffing rule means the following byte
        // contributes only its low 7 bits -- its own top bit is never part of the bitstream.
        // Byte 2 here is 0xC3 = 1100_0011; only "100_0011" (7 bits) should ever be read.
        byte[] data = [0xFF, 0xC3, 0x00];
        var reader = new JpxBitReader(data, 0);

        Assert.Equal(0xFF, reader.ReadBits(8));
        Assert.Equal(0b100_0011, reader.ReadBits(7)); // the stuffed byte's low 7 bits only
    }

    [Fact]
    public void AlignToByte_AfterNonFFByte_LandsOnTheNextByte()
    {
        byte[] data = [0b1010_0000, 0xAB];
        var reader = new JpxBitReader(data, 0);

        _ = reader.ReadBits(3); // partial byte -- some bits of byte 0 still unread
        reader.AlignToByte();

        Assert.Equal(1, reader.Position);
        Assert.Equal(0xAB, reader.ReadBits(8));
    }

    [Fact]
    public void AlignToByte_AfterFFByte_SkipsTheMandatoryStuffedByteWhole()
    {
        // The header ends exactly after consuming an 0xFF byte's bits; per B.10.1 the byte
        // immediately following 0xFF in the stream is a mandatory stuffing byte (its top bit is
        // never real data) and must be skipped entirely before the packet body begins, even
        // though the header decoder never touched any of its bits.
        byte[] data = [0xFF, 0x7F, 0xCD]; // 0x7F is the stuffed byte; 0xCD is the packet body's first byte
        var reader = new JpxBitReader(data, 0);

        _ = reader.ReadBits(8); // consume the 0xFF byte fully
        reader.AlignToByte();

        Assert.Equal(2, reader.Position);
        Assert.Equal(0xCD, reader.ReadBits(8));
    }

    [Fact]
    public void AlignToByte_MidwayThroughAnFFByte_StillSkipsTheStuffedByte()
    {
        // Stopping partway through the 0xFF byte's own bits still means the NEXT physical byte
        // is the mandatory stuffed one -- align must skip it regardless of how many of 0xFF's own
        // bits were actually consumed.
        byte[] data = [0xFF, 0x00, 0x42];
        var reader = new JpxBitReader(data, 0);

        _ = reader.ReadBits(3); // only 3 of 0xFF's 8 bits consumed
        reader.AlignToByte();

        Assert.Equal(2, reader.Position);
        Assert.Equal(0x42, reader.ReadBits(8));
    }

    [Fact]
    public void AlignToByte_WithoutAnyRead_DoesNothing()
    {
        byte[] data = [0x11, 0x22];
        var reader = new JpxBitReader(data, 0);

        reader.AlignToByte();

        Assert.Equal(0, reader.Position);
    }

    // ---- JpxTagTree: T.800 B.10.2 worked examples ----

    [Fact]
    public void DecodeValue_ThreeLevelTree_WorkedExample()
    {
        // A 3x3 leaf grid: level 0 = 9 leaves, level 1 = a 2x2 grid of 4 nodes (each the min of up
        // to 4 leaves), level 2 = the single root (min of the 4 level-1 nodes) -- three levels.
        // Only the path from the root down to leaf (2,2) is exercised by this test, so only that
        // path's three node values need be assigned: root = 0, its level-1 parent (index (1,1)
        // in the 2x2 mid grid) = 1, leaf (2,2) = 2.
        //
        // T.800 B.10.2 (and every reference codec's tgt_decode/tgt_encode, e.g. OpenJPEG's
        // opj_tgt_decode carrying `low` down the stack): a node is signalled as a unary code
        // (V zero-bits then a terminating 1-bit) counted from its PARENT's own resolved value,
        // not from zero -- the min-of-children invariant means a child's value can never be
        // BELOW its parent's, so there is nothing to signal for that already-implied lower
        // bound. Root has no parent, so it counts from 0 as usual:
        //   root  (value 0, counts from 0): "1"        (1 bit)
        //   mid   (value 1, counts from root's 0): "01" (2 bits)
        //   leaf  (value 2, counts from mid's 1, i.e. only the remaining distance of 1): "01" (2 bits)
        // Concatenated in root-to-leaf order: "1" + "01" + "01" = "10101" (5 bits). Packed with
        // three trailing marker bits "011" (distinctive, not the unary codes' own natural
        // padding) so the test can prove DecodeValue read exactly those 5 bits and no more:
        // 1010 1011 = 0xAB.
        var tree = new JpxTagTree(width: 3, height: 3);
        byte[] data = [0b1010_1011];
        var reader = new JpxBitReader(data, 0);

        var value = tree.DecodeValue(ref reader, x: 2, y: 2);

        Assert.Equal(2, value);
        Assert.Equal(0b011, reader.ReadBits(3)); // the three marker bits immediately after the 5 consumed
    }

    [Fact]
    public void DecodeInclusion_ResumedAcrossThresholds_MatchesOneShotDecodeAtFinalThreshold()
    {
        // A 2x2 leaf grid collapses to a single root in one level: root = min(0,2,1,3) = 0,
        // leaf (1,1) = 3. Hand-traced unary sequence for querying leaf (1,1) up through
        // threshold 4 (the first threshold at which "value < threshold" becomes true for a
        // value of 3):
        //   threshold 1: root "1" (finalizes at 0) + leaf "0" (0->1, still >= 1)      => false
        //   threshold 2: leaf "0" (1->2, still >= 2)                                  => false
        //   threshold 3: leaf "0" (2->3, still >= 3)                                  => false
        //   threshold 4: leaf "1" (finalizes at 3, 3 < 4)                             => true
        // Concatenated: "1" + "0001" = "10001" (5 bits), followed by a distinctive 3-bit marker
        // "101" (not the unary codes' own natural padding) so the test can prove both decode
        // strategies consumed exactly those 5 bits and land on the same next bit: 1000 1101 = 0x8D.
        byte[] data = [0b1000_1101];

        var resumedTree = new JpxTagTree(width: 2, height: 2);
        var resumedReader = new JpxBitReader(data, 0);
        var r1 = resumedTree.DecodeInclusion(ref resumedReader, x: 1, y: 1, threshold: 1);
        var r2 = resumedTree.DecodeInclusion(ref resumedReader, x: 1, y: 1, threshold: 2);
        var r3 = resumedTree.DecodeInclusion(ref resumedReader, x: 1, y: 1, threshold: 3);
        var r4 = resumedTree.DecodeInclusion(ref resumedReader, x: 1, y: 1, threshold: 4);

        Assert.False(r1);
        Assert.False(r2);
        Assert.False(r3);
        Assert.True(r4);

        var oneShotTree = new JpxTagTree(width: 2, height: 2);
        var oneShotReader = new JpxBitReader(data, 0);
        var oneShot = oneShotTree.DecodeInclusion(ref oneShotReader, x: 1, y: 1, threshold: 4);

        Assert.True(oneShot);

        // Both strategies must land at exactly the same next bit: the 3-bit marker "101"
        // immediately following the 5 bits either one consumed.
        Assert.Equal(0b101, resumedReader.ReadBits(3));
        Assert.Equal(0b101, oneShotReader.ReadBits(3));
    }

    [Fact]
    public void DecodeInclusion_QueryingADifferentLeafFirst_DoesNotDisturbAnUnrelatedLeafsState()
    {
        // A 2x2 tree with distinct leaf values: (0,0)=0 -> root=min=0. Querying leaf (0,0) at a
        // low threshold only ever needs the root (finalizes at value 0) plus that leaf's own
        // bits; leaf (1,1)'s own bits, appearing later in the stream, are untouched.
        // root "1" (value 0) + leaf(0,0) "1" (value 0, finalizes immediately) + leaf(1,1) "001" (value 2).
        byte[] data = [0b1100_1000];
        var tree = new JpxTagTree(width: 2, height: 2);
        var reader = new JpxBitReader(data, 0);

        var includedAtZero = tree.DecodeInclusion(ref reader, x: 0, y: 0, threshold: 1);
        Assert.True(includedAtZero); // leaf (0,0) value 0 < threshold 1

        var value11 = tree.DecodeValue(ref reader, x: 1, y: 1);
        Assert.Equal(2, value11);
    }
}
