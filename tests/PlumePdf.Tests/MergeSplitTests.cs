using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <c>Pdf.Merge</c>/<c>Pdf.Split</c>: page-level composition, deep-copied
/// per source so source documents can be disposed immediately after.
/// </summary>
public class MergeSplitTests
{
    [Fact]
    public void Merge_Paths_CombinesPageCounts()
    {
        var pathA = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        var pathB = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 3));
        try
        {
            using var merged = Pdf.Merge(pathA, pathB);
            Assert.Equal(5, merged.Pages.Count);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }

    [Fact]
    public void Merge_ZeroPaths_Throws()
    {
        Assert.Throws<ArgumentException>(() => Pdf.Merge(Array.Empty<string>()));
    }

    [Fact]
    public void Merge_ZeroDocuments_Throws()
    {
        Assert.Throws<ArgumentException>(() => Pdf.Merge(Array.Empty<PdfDocument>()));
    }

    [Fact]
    public void Merge_SourceDisposedAfterMerge_MergedDocumentStillUsable()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        try
        {
            var source = PdfDocument.Open(path);
            var merged = Pdf.Merge(source);
            source.Dispose(); // Merge already deep-copied everything it needs.

            var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-merge-{Guid.NewGuid():N}.pdf");
            try
            {
                merged.Save(outputPath);
                using var reopened = PdfDocument.Open(outputPath);
                Assert.Equal(2, reopened.Pages.Count);
            }
            finally
            {
                merged.Dispose();
                File.Delete(outputPath);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_ThenSave_ProducesOpenableDocument()
    {
        var pathA = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var pathB = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-merge-save-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var merged = Pdf.Merge(pathA, pathB))
            {
                merged.Save(outputPath, new PdfOptions { Deterministic = true });
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.Equal(2, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Merge_InheritedPageAttributes_AreFlattenedOntoTheImportedCopy()
    {
        var pathA = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocumentWithInheritedAttributes());
        try
        {
            using var source = PdfDocument.Open(pathA);
            using var merged = Pdf.Merge(source);

            var importedPage = Assert.Single(merged.Pages);
            Assert.True(importedPage.Dictionary.TryGetValue(PdfName.Get("MediaBox"), out _));
            Assert.True(importedPage.Dictionary.TryGetValue(PdfName.Get("Resources"), out var resources));
            Assert.True(((PdfDictionary)resources).ContainsKey(PdfName.Get("Font")));
        }
        finally
        {
            File.Delete(pathA);
        }
    }

    [Fact]
    public void Merge_EncryptedSource_RefusesWithCodedError()
    {
        // Composing pages out of an encrypted
        // source would emit an unencrypted copy of restricted content — the merge/split
        // path refuses exactly like Save/SaveIncremental (PLUME5001/PLUME5002) do.
        var path = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "AES-128.pdf");
        using var source = PdfDocument.Open(path);

        var ex = Assert.Throws<PlumePdfException>(() => Pdf.Merge(source));
        Assert.Equal("PLUME6012", ex.Code);
    }

    [Fact]
    public void Merge_LongIndirectReferenceChain_DoesNotOverflowTheStack()
    {
        // A page whose /Resources points at object 2, which points at object 3, ... a chain
        // deep enough that a naive recursive importer (one Import->ImportValue->Import stack
        // frame per hop) would overflow the call stack — nothing in the object model
        // bounds a reference chain's length the way MaxObjectNestingDepth bounds nesting
        // within one object.
        const int chainLength = 50_000;
        var path = WriterTestDocuments.WriteTempFile(BuildDocumentWithReferenceChain(chainLength));
        try
        {
            using var source = PdfDocument.Open(path);
            using var merged = Pdf.Merge(source);
            Assert.Single(merged.Pages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] BuildDocumentWithReferenceChain(int chainLength)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"));
        var offsets = new Dictionary<int, int>();

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int contentNum = 4;
        const int chainStart = 5;
        var totalObjects = chainStart + chainLength;

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        WriteObject(pageNum, $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 100] /Contents {contentNum} 0 R /Extra {chainStart} 0 R >>");

        var content = "BT ET";
        offsets[contentNum] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"{contentNum} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        for (var i = 0; i < chainLength; i++)
        {
            var number = chainStart + i;
            var next = number + 1;
            var body = next < totalObjects ? $"<< /Next {next} 0 R >>" : "<< /Type /Null >>";
            WriteObject(number, body);
        }

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void Split_ProducesOneDocumentPerPage()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 3));
        try
        {
            using var document = PdfDocument.Open(path);
            using var split = Pdf.Split(document);

            Assert.Equal(3, split.Documents.Count);
            foreach (var part in split.Documents)
            {
                Assert.Single(part.Pages);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Split_SaveAll_WritesOneFilePerPart()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        var directory = Path.Combine(Path.GetTempPath(), $"plumepdf-split-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var document = PdfDocument.Open(path);
            using var split = Pdf.Split(document);

            var pattern = Path.Combine(directory, "part-{n}.pdf");
            split.SaveAll(pattern);

            Assert.True(File.Exists(Path.Combine(directory, "part-1.pdf")));
            Assert.True(File.Exists(Path.Combine(directory, "part-2.pdf")));

            using var reopened = PdfDocument.Open(Path.Combine(directory, "part-1.pdf"));
            Assert.Single(reopened.Pages);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAll_PatternWithoutToken_Throws()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            using var document = PdfDocument.Open(path);
            using var split = Pdf.Split(document);
            Assert.Throws<ArgumentException>(() => split.SaveAll("no-token.pdf"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
