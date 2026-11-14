using Xunit;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// The programmer-error checks on <see cref="PageCollection.Add"/>, <see cref="PageCollection.Insert"/>,
/// <see cref="PageCollection.AddImage"/> and <see cref="PageCollection.InsertImage"/>: BCL
/// exceptions, thrown before anything is read or changed.
/// </summary>
public class PageAddArgumentTests
{
    private static readonly RasterImageFrame Frame = new(new byte[3], 1, 1, RasterPixelFormat.Rgb24);

    [Fact]
    public void Add_NullPage_Throws()
    {
        using var document = Open();

        Assert.Throws<ArgumentNullException>(() => document.Pages.Add(null!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Insert_IndexOutsideZeroToCount_Throws(int index)
    {
        using var document = Open();
        using var source = Open();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Pages.Insert(index, source.Pages[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Pages.InsertImage(index, Frame));
    }

    [Fact]
    public void Add_PageOfADisposedDocument_Throws()
    {
        using var document = Open();
        var source = Open();
        var page = source.Pages[0];
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => document.Pages.Add(page));
    }

    [Fact]
    public void Add_ToADisposedDocument_Throws()
    {
        var document = Open();
        var pages = document.Pages;
        using var source = Open();
        document.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pages.Add(source.Pages[0]));
        Assert.Throws<ObjectDisposedException>(() => pages.AddImage(Frame));
    }

    [Fact]
    public void AddImage_NullFrame_Throws()
    {
        using var document = Open();

        Assert.Throws<ArgumentNullException>(() => document.Pages.AddImage(null!));
    }

    [Fact]
    public void AFailedArgumentCheck_ChangesNothing()
    {
        using var document = Open();

        Assert.Throws<ArgumentOutOfRangeException>(() => document.Pages.InsertImage(5, Frame));

        Assert.Single(document.Pages);
        Assert.Empty(document.Objects.DirtyObjects);
    }

    private static PdfDocument Open() => PdfDocument.Open(PdfFixtureBuilder.BuildClassicXrefPdf(
        [(1, "<< /Type /Catalog /Pages 2 0 R >>"), (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>")],
        rootObjectNumber: 1));
}
