using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <c>PdfDocument.Save</c> (full rewrite): open → Save → open object-graph
/// equivalence — the rewritten document resolves to the same logical content even though
/// every object was renumbered and the page tree was flattened.
/// </summary>
public class RoundTripTests
{
    [Fact]
    public void Save_ThenReopen_SamePageCountAndContent()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 4));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-roundtrip-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Equal(4, reopened.Pages.Count);
            Assert.NotNull(reopened.Objects.Trailer);

            foreach (var page in reopened.Pages)
            {
                Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Contents"), out var contents));
                Assert.IsType<PdfReference>(contents);

                Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Resources"), out var resources));
                var resourceDict = Assert.IsType<PdfDictionary>(resources);
                Assert.True(resourceDict.ContainsKey(PdfName.Get("Font")));
            }
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Save_PreservesDocumentInformation()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1, includeInfo: true));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-roundtrip-info-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.True(reopened.Objects.Trailer.TryGetValue(PdfName.Info, out var infoValue));
            var infoRef = Assert.IsType<PdfReference>(infoValue);
            var info = Assert.IsType<PdfDictionary>(reopened.Objects[infoRef.Target]);
            var title = Assert.IsType<PdfString>(info[PdfName.Get("Title")]);
            Assert.Equal("Test Document", title.GetText());
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Save_AfterPageRemoval_GarbageCollectsTheRemovedPageContent()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-roundtrip-gc-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Pages.RemoveAt(0);
                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Open_PageAttributesOnlyOnPagesNode_AreResolvedOntoTheLeaf()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithInheritedAttributes());
        try
        {
            using var document = PdfDocument.Open(sourcePath);
            var page = Assert.Single(document.Pages);

            // The page itself sets none of these directly - they must come from the /Pages root.
            Assert.True(page.Dictionary.TryGetValue(PdfName.Get("MediaBox"), out var mediaBox));
            Assert.IsType<PdfArray>(mediaBox);
            Assert.True(page.Dictionary.TryGetValue(PdfName.Get("CropBox"), out _));
            Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Rotate"), out var rotate));
            Assert.Equal(90, ((PdfNumber)rotate).ToInt32());
            Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Resources"), out var resources));
            Assert.True(((PdfDictionary)resources).ContainsKey(PdfName.Get("Font")));

            // doc.Objects is the raw escape hatch - the page's own dictionary is untouched.
            var raw = Assert.IsType<PdfDictionary>(document.Objects[page.Reference]);
            Assert.False(raw.ContainsKey(PdfName.Get("MediaBox")));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public void Save_InheritedPageAttributes_SurviveFullRewrite()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithInheritedAttributes());
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-roundtrip-inherit-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            var page = Assert.Single(reopened.Pages);
            Assert.True(page.Dictionary.TryGetValue(PdfName.Get("MediaBox"), out _));
            Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Resources"), out var resources));
            Assert.True(((PdfDictionary)resources).ContainsKey(PdfName.Get("Font")));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_InheritedPageAttributes_SurviveAppendedRevision()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithInheritedAttributes());
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.SaveIncremental(sourcePath);
            }

            using var reopened = PdfDocument.Open(sourcePath);
            var page = Assert.Single(reopened.Pages);
            Assert.True(page.Dictionary.TryGetValue(PdfName.Get("MediaBox"), out _));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }
}
