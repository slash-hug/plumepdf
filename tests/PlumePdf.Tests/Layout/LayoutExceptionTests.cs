using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="PdfLayoutException"/>: the structured public payload —
/// <see cref="PdfLayoutException.ElementPath"/>, <see cref="PdfLayoutException.Measurements"/>,
/// and <see cref="PdfLayoutException.Constraint"/> — copied out of the layout engine at the
/// moment of failure.
/// </summary>
public class LayoutExceptionTests
{
    [Fact]
    public void Constructor_StoresAllFieldsAndBaseExceptionContract()
    {
        var measurements = new[] { new ElementMeasurement("Row[0]", 100, double.PositiveInfinity, 150, 20) };
        var ex = new PdfLayoutException("PLUME9001", "content is wider than available", "Section > Body > Row[0]", measurements, "children must fit within the row's width");

        Assert.Equal("PLUME9001", ex.Code);
        Assert.Equal("content is wider than available", ex.Message);
        Assert.Equal("Section > Body > Row[0]", ex.ElementPath);
        Assert.Same(measurements, ex.Measurements);
        Assert.Equal("children must fit within the row's width", ex.Constraint);
        Assert.Equal("https://github.com/slash-hug/plumepdf/blob/main/docs/errors/PLUME9001.md", ex.HelpLink);
        Assert.IsAssignableFrom<PlumePdfException>(ex);
    }

    [Fact]
    public void Constructor_NullElementPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfLayoutException("PLUME9001", "msg", null!, [], "constraint"));
    }

    [Fact]
    public void Constructor_NullMeasurements_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfLayoutException("PLUME9001", "msg", "path", null!, "constraint"));
    }

    [Fact]
    public void Constructor_NullConstraint_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfLayoutException("PLUME9001", "msg", "path", [], null!));
    }

    [Fact]
    public void ElementMeasurement_IsAPlainValueRecord()
    {
        var a = new ElementMeasurement("Text", 100, 200, 50, 60);
        var b = new ElementMeasurement("Text", 100, 200, 50, 60);

        Assert.Equal(a, b);
    }
}
