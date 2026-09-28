using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

public class JpegHuffmanTableTests
{
    [Fact]
    public void DecodeSide_RoundTripsEveryEncodedSymbol()
    {
        var table = JpegHuffmanTable.StandardLuminanceAc;
        foreach (var symbol in new byte[] { 0x00, 0x01, 0x02, 0xF0, 0xFA, 0x11 })
        {
            var (code, length) = table.GetCode(symbol);
            Assert.True(length > 0, $"symbol 0x{symbol:X2} has no code in the standard luminance AC table.");
            Assert.True(table.TryLookup(length, code, out var decoded));
            Assert.Equal(symbol, decoded);
        }
    }

    [Fact]
    public void TryLookup_ReturnsFalse_ForUnknownCode()
    {
        var table = JpegHuffmanTable.StandardLuminanceDc;
        Assert.False(table.TryLookup(16, 0xFFFF, out _));
    }

    [Fact]
    public void StandardTables_HaveTheExpectedSymbolCounts()
    {
        // Annex K.3: 12 DC symbols, 162 AC symbols for both luminance and chrominance.
        Assert.Equal(12, JpegHuffmanTable.StandardLuminanceDcValues.Length);
        Assert.Equal(12, JpegHuffmanTable.StandardChrominanceDcValues.Length);
        Assert.Equal(162, JpegHuffmanTable.StandardLuminanceAcValues.Length);
        Assert.Equal(162, JpegHuffmanTable.StandardChrominanceAcValues.Length);
    }

    [Fact]
    public void ZigzagToNatural_IsAPermutationOf0To63()
    {
        var seen = new bool[64];
        foreach (var natural in JpegHuffmanTable.ZigzagToNatural)
        {
            Assert.InRange(natural, 0, 63);
            Assert.False(seen[natural], $"natural index {natural} appears more than once.");
            seen[natural] = true;
        }

        Assert.Equal(0, JpegHuffmanTable.ZigzagToNatural[0]); // DC is always zigzag position 0.
    }

    [Fact]
    public void Constructor_BuildsCanonicalCodesFromBitsAndValues()
    {
        // Two 1-bit codes would be illegal Huffman (both "0" and separately "0" again) - use
        // one 1-bit and one 2-bit code, the smallest legal canonical tree.
        byte[] bits = new byte[17];
        bits[1] = 1;
        bits[2] = 1;
        var table = new JpegHuffmanTable(bits, [0x05, 0x09]);

        Assert.True(table.TryLookup(1, 0b0, out var first));
        Assert.Equal(0x05, first);
        Assert.True(table.TryLookup(2, 0b10, out var second));
        Assert.Equal(0x09, second);
    }
}
