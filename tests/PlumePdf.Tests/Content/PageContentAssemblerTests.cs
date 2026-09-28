using PlumePdf.Content;
using PlumePdf.Filters;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Content;

/// <summary><see cref="ResourceDictionaryBuilder"/> and <see cref="PageContentAssembler"/>.</summary>
public class PageContentAssemblerTests
{
    [Fact]
    public void ResourceDictionaryBuilder_AddFont_AllocatesSequentialNames()
    {
        var builder = new ResourceDictionaryBuilder();

        var first = builder.AddFont(new IndirectReference(10, 0));
        var second = builder.AddFont(new IndirectReference(11, 0));

        Assert.Equal("F1", first.Value);
        Assert.Equal("F2", second.Value);
    }

    [Fact]
    public void ResourceDictionaryBuilder_AddFont_SameReferenceTwice_ReturnsSameName()
    {
        var builder = new ResourceDictionaryBuilder();
        var reference = new IndirectReference(10, 0);

        var first = builder.AddFont(reference);
        var second = builder.AddFont(reference);

        Assert.Same(first, second);
    }

    [Fact]
    public void ResourceDictionaryBuilder_AddXObject_AllocatesSequentialNamesIndependentOfFonts()
    {
        var builder = new ResourceDictionaryBuilder();
        builder.AddFont(new IndirectReference(10, 0));

        var image = builder.AddXObject(new IndirectReference(20, 0));

        Assert.Equal("X1", image.Value);
    }

    [Fact]
    public void ResourceDictionaryBuilder_Build_OmitsEmptyCategories()
    {
        var builder = new ResourceDictionaryBuilder();
        builder.AddFont(new IndirectReference(10, 0));

        var resources = builder.Build();

        Assert.True(resources.TryGetValue(PdfName.Get("Font"), out _));
        Assert.False(resources.TryGetValue(PdfName.Get("XObject"), out _));
    }

    [Fact]
    public void ResourceDictionaryBuilder_Build_FontSubdictionaryMapsNameToReference()
    {
        var builder = new ResourceDictionaryBuilder();
        var reference = new IndirectReference(42, 0);
        var name = builder.AddFont(reference);

        var resources = builder.Build();

        var fontDict = Assert.IsType<PdfDictionary>(resources[PdfName.Get("Font")]);
        var value = Assert.IsType<PdfReference>(fontDict[name]);
        Assert.Equal(reference, value.Target);
    }

    [Fact]
    public void Assemble_EncodesContentStreamAndDecodesBackToOriginalOperators()
    {
        var operators = new ContentStreamBuilder().SaveState().Rectangle(0, 0, 10, 10).Fill().RestoreState().Build();
        var resources = new ResourceDictionaryBuilder().Build();
        var next = 3;

        var assembled = PageContentAssembler.Assemble(
            new PageBox(0, 0, 612, 792),
            resources,
            operators,
            parentPages: new IndirectReference(2, 0),
            filterRegistry: PdfFilterRegistry.Default,
            options: PdfOptions.Default,
            allocateObjectNumber: () => next++);

        Assert.Equal(3, assembled.ContentStreamObjectNumber);
        Assert.Equal(4, assembled.PageObjectNumber);

        var decoded = assembled.ContentStream.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
        Assert.Equal(operators, decoded);

        Assert.Equal("Page", ((PdfName)assembled.PageDictionary[PdfName.Type]).Value);
        Assert.Equal(new IndirectReference(2, 0), ((PdfReference)assembled.PageDictionary[PdfName.Get("Parent")]).Target);
        Assert.Equal(new IndirectReference(3, 0), ((PdfReference)assembled.PageDictionary[PdfName.Get("Contents")]).Target);
    }

    [Fact]
    public void Assemble_NoEncoderRegistered_WritesUncompressedWithNoFilterEntry()
    {
        var operators = new ContentStreamBuilder().Fill().Build();
        var emptyRegistry = new PdfFilterRegistry(); // no encoders registered
        var next = 1;

        var assembled = PageContentAssembler.Assemble(
            new PageBox(0, 0, 100, 100),
            new PdfDictionary(),
            operators,
            parentPages: new IndirectReference(99, 0),
            filterRegistry: emptyRegistry,
            options: PdfOptions.Default,
            allocateObjectNumber: () => next++);

        Assert.False(assembled.ContentStream.Dictionary.TryGetValue(PdfName.Filter, out _));
        Assert.Equal(operators, assembled.ContentStream.RawBytes.ToArray());
    }

    /// <summary>
    /// Integration test: hand-build a single page's object graph with
    /// <see cref="ResourceDictionaryBuilder"/> and <see cref="PageContentAssembler"/>,
    /// wrap it in a synthetic <see cref="PdfDocument"/> (mirroring <c>DocumentComposer</c>'s
    /// build pattern), save it, and re-open the saved file through PlumePDF's own reader:
    /// zero diagnostics, a correct <c>/Length</c> (recomputed by <c>ObjectSerializer</c>,
    /// not carried over from what this test wrote), and the content stream decodes back to
    /// the exact operator bytes that went in.
    /// </summary>
    [Fact]
    public void AssembledPage_SavedAndReopened_RoundTripsWithZeroDiagnostics()
    {
        var operators = new ContentStreamBuilder()
            .SaveState()
            .Transform(1, 0, 0, 1, 72, 720)
            .SetFillRgb(0.2, 0.4, 0.6)
            .Rectangle(0, 0, 200, 40)
            .Fill()
            .RestoreState()
            .BeginText()
            .SetFont("F1", 12)
            .MoveText(20, 50)
            .ShowText("Hello, PlumePDF"u8)
            .EndText()
            .Build();

        var objects = new Dictionary<int, PdfObject>();
        var nextNumber = 1;
        int Allocate() => nextNumber++;

        var catalogNumber = Allocate();
        var pagesNumber = Allocate();
        var fontNumber = Allocate();
        objects[fontNumber] = BuildStandardFontDictionary();

        var resourceBuilder = new ResourceDictionaryBuilder();
        resourceBuilder.AddFont(new IndirectReference(fontNumber, 0));
        var resources = resourceBuilder.Build();

        var assembled = PageContentAssembler.Assemble(
            new PageBox(0, 0, 612, 792),
            resources,
            operators,
            parentPages: new IndirectReference(pagesNumber, 0),
            filterRegistry: PdfFilterRegistry.Default,
            options: PdfOptions.Default,
            allocateObjectNumber: Allocate);

        objects[assembled.ContentStreamObjectNumber] = assembled.ContentStream;
        objects[assembled.PageObjectNumber] = assembled.PageDictionary;

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray([new PdfReference(new IndirectReference(assembled.PageObjectNumber, 0))]));
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(1));
        objects[pagesNumber] = pagesDict;

        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(new IndirectReference(pagesNumber, 0)));
        objects[catalogNumber] = catalogDict;

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(nextNumber));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(catalogNumber, 0)));

        var document = PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, objects), new DiagnosticCollection());

        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-page-content-assembler-{Guid.NewGuid():N}.pdf");
        try
        {
            document.Save(outputPath);

            using var reopened = PdfDocument.Open(outputPath);

            Assert.Empty(reopened.Diagnostics);
            Assert.Single(reopened.Pages);

            var page = reopened.Pages[0];
            var contentsReference = Assert.IsType<PdfReference>(page.Dictionary[PdfName.Get("Contents")]);
            var contentStream = Assert.IsType<PdfStream>(reopened.Objects[contentsReference.Target]);

            // /Length must reflect the bytes actually on disk (ObjectSerializer recomputes
            // it rather than trusting whatever was set before writing).
            var lengthEntry = Assert.IsType<PdfNumber>(contentStream.Dictionary[PdfName.Length]);
            Assert.Equal(contentStream.RawBytes.Length, lengthEntry.ToInt32());

            var decodedOperators = contentStream.GetDecodedBytes(PdfFilterRegistry.Default);
            Assert.Equal(operators, decodedOperators);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static PdfDictionary BuildStandardFontDictionary()
    {
        var font = new PdfDictionary();
        font.Set(PdfName.Type, PdfName.Get("Font"));
        font.Set(PdfName.Subtype, PdfName.Get("Type1"));
        font.Set(PdfName.Get("BaseFont"), PdfName.Get("Helvetica"));
        return font;
    }
}
