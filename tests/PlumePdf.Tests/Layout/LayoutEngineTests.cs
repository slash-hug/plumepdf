using PlumePdf.Elements;
using PlumePdf.Layout;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="LayoutEngine"/>: measure-pass sizing for Text/Row/Column/Table,
/// and the coded <see cref="PdfLayoutException"/> every constraint failure raises.
/// </summary>
public class LayoutEngineTests
{
    [Fact]
    public void Measure_Text_WrapsAndReportsHeightProportionalToLineCount()
    {
        var text = new Text("The quick brown fox jumps over the lazy dog") { FontSize = 12 };
        var measured = (MeasuredText)LayoutEngine.Measure(text, availableWidth: 80, "Body");

        Assert.True(measured.Lines.Count > 1);
        Assert.Equal(measured.Lines.Count * text.FontSize * text.LineSpacing, measured.Height, precision: 6);
        Assert.True(measured.Width <= 80.5);
    }

    [Fact]
    public void Measure_Column_StacksChildrenAndSumsHeightPlusSpacing()
    {
        var column = new Column(new Text("one") { FontSize = 10 }, new Text("two") { FontSize = 10 }) { Spacing = 5 };
        var measured = (MeasuredColumn)LayoutEngine.Measure(column, availableWidth: 200, "Body");

        Assert.Equal(2, measured.Children.Count);
        var expectedHeight = measured.Children[0].Height + measured.Children[1].Height + 5;
        Assert.Equal(expectedHeight, measured.Height, precision: 6);
        Assert.Equal(200, measured.Width);
    }

    [Fact]
    public void Measure_Row_FitsWithinAvailableWidth_Succeeds()
    {
        var row = new Row(new Text("logo") { FontSize = 12 }, new Text("title") { FontSize = 12 });
        var measured = (MeasuredRow)LayoutEngine.Measure(row, availableWidth: 500, "Header");

        Assert.Equal(2, measured.Children.Count);
        Assert.True(measured.Children[1].X > measured.Children[0].X);
    }

    [Fact]
    public void Measure_Row_Overconstrained_ThrowsWithElementPathAndBothSizes()
    {
        var row = new Row(
            new Text("a very long piece of unwrapped header text that will not fit") { FontSize = 24, Bold = true },
            new Text("another very long piece of unwrapped header text") { FontSize = 24, Bold = true });

        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(row, availableWidth: 100, "Header"));

        Assert.Equal("PLUME9001", ex.Code);
        Assert.Contains("Header", ex.ElementPath);
        Assert.NotEmpty(ex.Measurements);
        Assert.Equal(100, ex.Measurements[0].AvailableWidth);
        Assert.True(ex.Measurements[0].RequiredWidth > 100);
        Assert.False(string.IsNullOrWhiteSpace(ex.Constraint));
    }

    [Fact]
    public void Measure_Table_RelativeColumns_DivideRemainingWidthProportionally()
    {
        var table = new Table
        {
            Columns = [TableColumn.Relative(3), TableColumn.Relative(1)],
            Rows = [[new Text("a"), new Text("b")]],
        };

        var measured = (MeasuredTable)LayoutEngine.Measure(table, availableWidth: 400, "Body");

        Assert.Equal(300, measured.ColumnWidths[0], precision: 6);
        Assert.Equal(100, measured.ColumnWidths[1], precision: 6);
    }

    [Fact]
    public void Measure_Table_FixedAndRelativeColumns_FixedTakesPointsBeforeRelativeDivides()
    {
        var table = new Table
        {
            Columns = [TableColumn.Fixed(50), TableColumn.Relative(1), TableColumn.Relative(1)],
            Rows = [[new Text("a"), new Text("b"), new Text("c")]],
        };

        var measured = (MeasuredTable)LayoutEngine.Measure(table, availableWidth: 250, "Body");

        Assert.Equal(50, measured.ColumnWidths[0], precision: 6);
        Assert.Equal(100, measured.ColumnWidths[1], precision: 6);
        Assert.Equal(100, measured.ColumnWidths[2], precision: 6);
    }

    [Fact]
    public void Measure_Table_RowSpacing_AddsGapsBetweenHeaderAndEveryRow()
    {
        var withoutSpacing = new Table
        {
            Columns = [TableColumn.Relative(1)],
            HeaderRow = [new Text("H")],
            Rows = [[new Text("a")], [new Text("b")]],
        };
        var withSpacing = new Table
        {
            Columns = [TableColumn.Relative(1)],
            HeaderRow = [new Text("H")],
            Rows = [[new Text("a")], [new Text("b")]],
            RowSpacing = 5,
        };

        var plain = LayoutEngine.Measure(withoutSpacing, 200, "Body");
        var spaced = LayoutEngine.Measure(withSpacing, 200, "Body");

        // 3 rows total (header + 2 body rows) => 2 gaps.
        Assert.Equal(plain.Height + (5 * 2), spaced.Height, precision: 6);
    }

    [Fact]
    public void Measure_Table_ZeroColumns_Throws()
    {
        var table = new Table { Columns = [] };
        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(table, 400, "Body"));
        Assert.Equal("PLUME9002", ex.Code);
    }

    [Fact]
    public void Measure_Table_RowCellCountMismatch_Throws()
    {
        var table = new Table
        {
            Columns = [TableColumn.Relative(1), TableColumn.Relative(1)],
            Rows = [[new Text("only one cell")]],
        };

        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(table, 400, "Body"));
        Assert.Equal("PLUME9002", ex.Code);
    }

    [Fact]
    public void Measure_Table_GivenUnboundedWidth_Throws()
    {
        var table = new Table { Columns = [TableColumn.Relative(1)], Rows = [[new Text("a")]] };
        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(table, double.PositiveInfinity, "Body"));
        Assert.Equal("PLUME9007", ex.Code);
    }

    [Fact]
    public void Measure_NonPositiveAvailableWidth_Throws()
    {
        var column = new Column(new Text("x"));
        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(column, 0, "Body"));
        Assert.Equal("PLUME9007", ex.Code);
    }

    [Fact]
    public void Measure_DeeplyNestedColumns_ExceedsMaxDepth_Throws()
    {
        Element current = new Text("leaf");
        for (var i = 0; i < LayoutEngine.DefaultMaxNestingDepth + 5; i++)
        {
            current = new Column(current);
        }

        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(current, 200, "Body"));
        Assert.Equal("PLUME9006", ex.Code);
    }

    [Fact]
    public void Measure_UnsupportedElementSubclass_Throws()
    {
        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(new CustomElement(), 200, "Body"));
        Assert.Equal("PLUME9008", ex.Code);
    }

    [Fact]
    public void Measure_Image_WithExplicitDimensions_UsesThemDirectly()
    {
        var image = new Image(new byte[2 * 2 * 3], pixelWidth: 2, pixelHeight: 2) { Width = 40, Height = 20 };
        var measured = (MeasuredImage)LayoutEngine.Measure(image, 200, "Body");

        Assert.Equal(40, measured.Width);
        Assert.Equal(20, measured.Height);
    }

    [Fact]
    public void Measure_Image_WiderThanAvailableWidth_Throws()
    {
        var image = new Image(new byte[2 * 2 * 3], pixelWidth: 2, pixelHeight: 2) { Width = 500, Height = 20 };
        var ex = Assert.Throws<PdfLayoutException>(() => LayoutEngine.Measure(image, 200, "Body"));
        Assert.Equal("PLUME9001", ex.Code);
    }

    private sealed class CustomElement : Element;
}
