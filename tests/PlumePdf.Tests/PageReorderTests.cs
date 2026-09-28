using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <c>PageCollection.Move</c>/<c>RemoveAt</c> — the in-memory mutation door,
/// independent of how it's persisted.
/// </summary>
public class PageReorderTests
{
    private static PdfDocument OpenSample(int pageCount)
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount));
        return PdfDocument.Open(path);
    }

    [Fact]
    public void Move_ReordersPages()
    {
        using var document = OpenSample(3);
        var references = new[] { document.Pages[0].Reference, document.Pages[1].Reference, document.Pages[2].Reference };

        document.Pages.Move(0, 2);

        Assert.Equal(references[1], document.Pages[0].Reference);
        Assert.Equal(references[2], document.Pages[1].Reference);
        Assert.Equal(references[0], document.Pages[2].Reference);
    }

    [Fact]
    public void Move_SameIndex_IsANoOp()
    {
        using var document = OpenSample(2);
        var before = document.Pages[0].Reference;
        document.Pages.Move(0, 0);
        Assert.Equal(before, document.Pages[0].Reference);
    }

    [Fact]
    public void Move_OutOfRangeIndex_Throws()
    {
        using var document = OpenSample(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Pages.Move(5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Pages.Move(0, 5));
    }

    [Fact]
    public void RemoveAt_RemovesPageAndShiftsSubsequentPages()
    {
        using var document = OpenSample(3);
        var lastReference = document.Pages[2].Reference;

        document.Pages.RemoveAt(0);

        Assert.Equal(2, document.Pages.Count);
        Assert.Equal(lastReference, document.Pages[1].Reference);
    }

    [Fact]
    public void RemoveAt_OutOfRangeIndex_Throws()
    {
        using var document = OpenSample(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Pages.RemoveAt(5));
    }

    [Fact]
    public void Enumeration_ReflectsCurrentOrder()
    {
        using var document = OpenSample(3);
        document.Pages.Move(2, 0);

        var enumerated = document.Pages.Select(static p => p.Reference).ToList();
        Assert.Equal(document.Pages[0].Reference, enumerated[0]);
        Assert.Equal(document.Pages[1].Reference, enumerated[1]);
        Assert.Equal(document.Pages[2].Reference, enumerated[2]);
    }
}
