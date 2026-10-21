using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// The read verbs (<c>ExtractImages</c>, <c>ExtractText</c>, <c>Rasterize</c>) read the streams a
/// page needs for that call only: nothing they resolve is kept in the document's object cache,
/// so reading every page of a large scan stays bounded by one page, not the document. A stream
/// the caller already holds through <c>doc.Objects</c> is the one they read, edits included.
/// </summary>
public class ReadScopeTests
{
    private const int PageCount = 6;
    private const int ImageBytes = 64 * 1024;
    private const int WrongLengthPage = 3;

    private static int ContentObjectNumber(int page) => 4 + (4 * page);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractImages_OnEveryPage_LeavesNoImageStreamInTheCache(bool indirectLength)
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength, WrongLengthPage));

        for (var page = 0; page < PageCount; page++)
        {
            ImageDocumentFixture.AssertImageIsPage(document.Pages[page].ExtractImages(), page, ImageBytes);
        }

        for (var page = 0; page < PageCount; page++)
        {
            Assert.False(document.Objects.IsCached(ImageDocumentFixture.ImageObjectNumber(page)), $"page {page}'s image stream stayed cached");
        }
    }

    [Fact]
    public void ExtractText_LeavesNoContentStreamInTheCache()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false));

        for (var page = 0; page < PageCount; page++)
        {
            document.Pages[page].ExtractText();
        }

        Assert.All(Enumerable.Range(0, PageCount), page => Assert.False(document.Objects.IsCached(ContentObjectNumber(page))));
    }

    [Fact]
    public void Rasterize_LeavesNoContentOrImageStreamInTheCache()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(2, ImageBytes, indirectLength: false));

        document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { Dpi = 9 });

        Assert.False(document.Objects.IsCached(ContentObjectNumber(0)));
        Assert.False(document.Objects.IsCached(ImageDocumentFixture.ImageObjectNumber(0)));
    }

    [Fact]
    public void TheRasterEntryPointPdfRasterizeUses_LeavesNoStreamInTheCache()
    {
        // Pdf.Rasterize renders through PageRasterAdapter directly, not PdfPage.Rasterize, so the
        // scope has to be the adapter's own.
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(2, ImageBytes, indirectLength: false));

        PlumePdf.Documents.PageRasterAdapter.RasterizePageFrame(document.Pages[1], document.Objects, document.Options, PdfRasterizeOptions.Default with { Dpi = 9 }, new DiagnosticCollection(), document);

        Assert.False(document.Objects.IsCached(ContentObjectNumber(1)));
        Assert.False(document.Objects.IsCached(ImageDocumentFixture.ImageObjectNumber(1)));
    }

    [Fact]
    public void AStreamTheCallerAlreadyHolds_IsTheOneTheReadVerbsUse_EditsIncluded()
    {
        // An edit made in place and never passed to MarkDirty: the read verbs must see it, and
        // the stream stays the caller's cached instance.
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false));
        var reference = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(1), 0);
        var held = (PdfStream)document.Objects[reference];
        held.Dictionary.Set(PdfName.Decode, new PdfArray([PdfNumber.Get(1), PdfNumber.Get(0), PdfNumber.Get(1), PdfNumber.Get(0), PdfNumber.Get(1), PdfNumber.Get(0)]));

        var image = Assert.Single(document.Pages[1].ExtractImages());

        Assert.Equal([1.0, 0.0, 1.0, 0.0, 1.0, 0.0], image.Decode);
        Assert.Same(held, document.Objects[reference]);
    }

    [Fact]
    public void ADeviationInAStreamReadAgain_IsReportedOnce()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false, WrongLengthPage));
        var before = document.Diagnostics.Count;

        document.Pages[WrongLengthPage].ExtractImages();
        var afterFirst = document.Diagnostics.Count;
        document.Pages[WrongLengthPage].ExtractImages();
        document.Pages[WrongLengthPage].ExtractImages();

        Assert.True(afterFirst > before, "the wrong /Length is reported");
        Assert.Equal(afterFirst, document.Diagnostics.Count);
    }

    [Fact]
    public void WithinAScope_AStreamIsOneInstance_AndAfterwardsIsNotCached()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false));
        var reference = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(2), 0);

        PdfObject first, second;
        using (StreamReadScope.Begin(document.Objects))
        {
            first = document.Objects[reference];
            second = document.Objects[reference];
        }

        Assert.IsType<PdfStream>(first);
        Assert.Same(first, second);
        Assert.False(document.Objects.IsCached(reference.Number));
        Assert.NotSame(first, document.Objects[reference]);
        Assert.True(document.Objects.IsCached(reference.Number), "outside a scope a resolve caches as before");
    }

    [Fact]
    public void ANestedScope_SharesTheOuterOne()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false));
        var reference = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(0), 0);

        using (StreamReadScope.Begin(document.Objects))
        {
            var outer = document.Objects[reference];
            using (StreamReadScope.Begin(document.Objects))
            {
                Assert.Same(outer, document.Objects[reference]);
            }

            Assert.Same(outer, document.Objects[reference]);
        }

        Assert.False(document.Objects.IsCached(reference.Number));
    }

    [Fact]
    public async Task ScopesOnTwoThreads_AreTheirOwn()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false));
        var reference = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(4), 0);
        using var bothResolved = new Barrier(2);

        PdfObject ReadInScope()
        {
            using (StreamReadScope.Begin(document.Objects))
            {
                var value = document.Objects[reference];
                bothResolved.SignalAndWait(TimeSpan.FromSeconds(10));
                return value;
            }
        }

        var both = await Task.WhenAll(Task.Run(ReadInScope), Task.Run(ReadInScope)).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.NotSame(both[0], both[1]);
        Assert.False(document.Objects.IsCached(reference.Number));
    }

    [Fact]
    public void AScopeForOneDocument_LeavesAnotherDocumentCachingAsBefore()
    {
        using var scoped = PdfDocument.Open(ImageDocumentFixture.Bytes(2, ImageBytes, indirectLength: false));
        using var other = PdfDocument.Open(ImageDocumentFixture.Bytes(2, ImageBytes, indirectLength: false));
        var number = ImageDocumentFixture.ImageObjectNumber(1);

        using (StreamReadScope.Begin(scoped.Objects))
        {
            _ = other.Objects[new IndirectReference(number, 0)];
        }

        Assert.True(other.Objects.IsCached(number));
    }
}
