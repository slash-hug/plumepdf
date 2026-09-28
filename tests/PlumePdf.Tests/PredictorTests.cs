using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests;

public class PredictorTests
{
    [Fact]
    public void NoPredictor_ReturnsDataUnchanged()
    {
        byte[] data = [1, 2, 3, 4];
        Assert.Equal(data, Predictor.Undo(data, predictor: 1, colors: 1, bitsPerComponent: 8, columns: 4));
        Assert.Equal(data, Predictor.Undo(data, predictor: 0, colors: 1, bitsPerComponent: 8, columns: 4));
    }

    [Fact]
    public void Png_FilterTypeNone_PassesThrough()
    {
        byte[] input = [0, 1, 2, 3, 0, 4, 5, 6]; // two rows, type byte 0 (None), columns=3
        var result = Predictor.Undo(input, predictor: 12, colors: 1, bitsPerComponent: 8, columns: 3);

        Assert.Equal([1, 2, 3, 4, 5, 6], result);
    }

    [Fact]
    public void Png_FilterTypeSub_AddsLeftPixel()
    {
        byte[] input = [1, 10, 5, 5]; // one row, type 1 (Sub)
        var result = Predictor.Undo(input, predictor: 11, colors: 1, bitsPerComponent: 8, columns: 3);

        Assert.Equal([10, 15, 20], result);
    }

    [Fact]
    public void Png_FilterTypeUp_AddsAbovePixel()
    {
        byte[] input = [0, 10, 15, 20, 2, 1, 1, 1]; // row0 None, row1 Up
        var result = Predictor.Undo(input, predictor: 12, colors: 1, bitsPerComponent: 8, columns: 3);

        Assert.Equal([10, 15, 20, 11, 16, 21], result);
    }

    [Fact]
    public void Png_FilterTypeAverage_AveragesLeftAndAbove()
    {
        byte[] input = [0, 10, 20, 30, 3, 10, 10, 10]; // row0 None, row1 Average
        var result = Predictor.Undo(input, predictor: 13, colors: 1, bitsPerComponent: 8, columns: 3);

        Assert.Equal([10, 20, 30, 15, 27, 38], result);
    }

    [Fact]
    public void Png_FilterTypePaeth_UsesPaethPredictor()
    {
        byte[] input = [0, 10, 20, 30, 4, 5, 5, 5]; // row0 None, row1 Paeth
        var result = Predictor.Undo(input, predictor: 14, colors: 1, bitsPerComponent: 8, columns: 3);

        Assert.Equal([10, 20, 30, 15, 25, 35], result);
    }

    [Fact]
    public void Png_UnsupportedFilterType_ThrowsCoded()
    {
        byte[] input = [5, 1, 2, 3]; // filter type 5 does not exist

        var ex = Assert.Throws<PlumePdfException>(() => Predictor.Undo(input, predictor: 15, colors: 1, bitsPerComponent: 8, columns: 3));
        Assert.Equal("PLUME3101", ex.Code);
    }

    [Fact]
    public void Tiff_HorizontalDifferencing_UndonePerRowIndependently()
    {
        byte[] input = [10, 5, 5, 1, 1, 1]; // two rows of 3 single-byte samples
        var result = Predictor.Undo(input, predictor: 2, colors: 1, bitsPerComponent: 8, columns: 3);

        Assert.Equal([10, 15, 20, 1, 2, 3], result);
    }

    [Fact]
    public void Tiff_NonEightBitComponents_ThrowsCoded()
    {
        byte[] input = [1, 2, 3];
        var ex = Assert.Throws<PlumePdfException>(() => Predictor.Undo(input, predictor: 2, colors: 1, bitsPerComponent: 4, columns: 3));
        Assert.Equal("PLUME3102", ex.Code);
    }

    [Fact]
    public void PdfFilterRegistry_AppliesPredictorAfterFlateDecode()
    {
        var raw = new byte[] { 0, 10, 20, 30, 2, 1, 1, 1 }; // PNG None row then Up row
        var compressed = ZLibCompress(raw);

        var decodeParms = new PdfDictionary();
        decodeParms.Set(PdfName.Predictor, PdfNumber.Get(12));
        decodeParms.Set(PdfName.Colors, PdfNumber.Get(1));
        decodeParms.Set(PdfName.Columns, PdfNumber.Get(3));

        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        dict.Set(PdfName.DecodeParms, decodeParms);

        var decoded = PdfFilterRegistry.Default.Decode(dict, compressed, PdfOptions.Default);

        Assert.Equal([10, 20, 30, 11, 21, 31], decoded);
    }

    private static byte[] ZLibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var compressor = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    [Fact]
    public void NonPositiveParameters_ThrowCoded()
    {
        // /Colors -1 with a TIFF predictor computed a positive row width
        // but drove a negative index inside UndoTiff (bare IndexOutOfRangeException).
        var ex = Assert.Throws<PlumePdfException>(
            () => Predictor.Undo(new byte[10], predictor: 2, colors: -1, bitsPerComponent: 8, columns: -10));
        Assert.Equal("PLUME3103", ex.Code);
    }
}
