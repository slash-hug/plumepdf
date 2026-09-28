using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

public class JpegBitReaderTests
{
    [Fact]
    public void TryReadBit_ReadsMsbFirst()
    {
        var reader = new JpegBitReader([0b1011_0010], 0);
        int[] expected = [1, 0, 1, 1, 0, 0, 1, 0];
        foreach (var bit in expected)
        {
            Assert.True(reader.TryReadBit(out var actual));
            Assert.Equal(bit, actual);
        }
    }

    [Fact]
    public void TryReadBit_ReturnsFalse_AtEndOfData()
    {
        var reader = new JpegBitReader([0xAB], 0);
        for (var i = 0; i < 8; i++)
        {
            Assert.True(reader.TryReadBit(out _));
        }

        Assert.False(reader.TryReadBit(out _));
    }

    [Fact]
    public void TryReadBits_ReadsMultiByteValue()
    {
        var reader = new JpegBitReader([0xFF, 0x00], 0); // stuffed literal 0xFF, no marker.
        Assert.True(reader.TryReadBits(8, out var value));
        Assert.Equal(0xFF, value);
    }

    [Fact]
    public void ByteStuffing_TreatsFF00AsLiteralFF()
    {
        var reader = new JpegBitReader([0xFF, 0x00, 0xAB], 0);
        Assert.True(reader.TryReadBits(16, out var value));
        Assert.Equal(0xFFAB, value);
    }

    [Fact]
    public void MarkerBoundary_StopsSupplyingBitsWithoutConsumingMarker()
    {
        var reader = new JpegBitReader([0xAB, 0xFF, 0xD9], 0); // one real byte then EOI marker.
        Assert.True(reader.TryReadBits(8, out var value));
        Assert.Equal(0xAB, value);
        Assert.False(reader.TryReadBit(out _)); // FF D9 is a marker, not stuffed data.
        Assert.Equal(1, reader.Position); // position sits right after the consumed data byte, at the marker's leading 0xFF.
    }

    [Fact]
    public void TryReceiveExtend_DecodesPositiveAndNegativeMagnitudes()
    {
        // Category 3 (3 bits): values -7..-4 map to 000..011, values 4..7 map to 100..111.
        var negative = new JpegBitReader([0b000_00000], 0);
        Assert.True(negative.TryReceiveExtend(3, out var negValue));
        Assert.Equal(-7, negValue);

        var positive = new JpegBitReader([0b111_00000], 0);
        Assert.True(positive.TryReceiveExtend(3, out var posValue));
        Assert.Equal(7, posValue);
    }

    [Fact]
    public void TryReceiveExtend_ZeroBits_ReturnsZeroWithoutReadingAnyBits()
    {
        var reader = new JpegBitReader([0xFF, 0xD9], 0); // immediately at a marker.
        Assert.True(reader.TryReceiveExtend(0, out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryDecodeHuffman_DecodesCanonicalCode()
    {
        // BITS: one 2-bit code, one 3-bit code -> symbols 0xAA (code "00"), 0xBB (code "010").
        byte[] bits = new byte[17];
        bits[2] = 1;
        bits[3] = 1;
        var table = new JpegHuffmanTable(bits, [0xAA, 0xBB]);

        var reader = new JpegBitReader([0b0001_0000], 0); // "00" then "01..." tail bits irrelevant to first read.
        Assert.True(reader.TryDecodeHuffman(table, out var first));
        Assert.Equal(0xAA, first);
    }

    [Fact]
    public void TryConsumeRestartMarker_ConsumesAndReportsTrue()
    {
        var reader = new JpegBitReader([0xFF, 0xD3, 0xAB], 0); // RST3 then a data byte.
        Assert.True(reader.TryConsumeRestartMarker());
        Assert.True(reader.TryReadBits(8, out var value));
        Assert.Equal(0xAB, value);
    }

    [Fact]
    public void TryConsumeRestartMarker_ReturnsFalse_WhenNoRestartMarkerPresent()
    {
        var reader = new JpegBitReader([0xFF, 0xD9], 0); // EOI, not a restart marker.
        Assert.False(reader.TryConsumeRestartMarker());
    }

    [Fact]
    public void TryConsumeRestartMarker_DiscardsBufferedPaddingBits()
    {
        var reader = new JpegBitReader([0xAB, 0xFF, 0xD0], 0);
        Assert.True(reader.TryReadBit(out _)); // buffers the whole 0xAB byte internally.
        Assert.True(reader.TryConsumeRestartMarker()); // must discard the other 7 buffered bits and find RST0.
    }
}
