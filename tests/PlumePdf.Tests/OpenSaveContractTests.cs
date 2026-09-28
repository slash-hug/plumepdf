using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <c>PdfDocument.Open</c>/<c>Dispose</c> lifetime and the save-to-open-path contract:
/// <c>Save</c> to the currently-open path via
/// temp-file-then-atomic-replace; <c>SaveIncremental</c> to the currently-open path via an
/// independent append handle.
/// </summary>
public class OpenSaveContractTests
{
    [Fact]
    public void Open_NonExistentPath_ThrowsCodedException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-does-not-exist-{Guid.NewGuid():N}.pdf");
        var ex = Assert.Throws<PlumePdfException>(() => PdfDocument.Open(path));
        Assert.Equal("PLUME1002", ex.Code);
    }

    [Fact]
    public void Dispose_IsSafeToCallMoreThanOnce()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            var document = PdfDocument.Open(path);
            document.Dispose();
            document.Dispose(); // must not throw
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_PreferStreamIo_OpensSuccessfully()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 2));
        try
        {
            using var document = PdfDocument.Open(path, PdfOptions.Default with { PreferStreamIo = true });
            Assert.Equal(2, document.Pages.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_ToItsOwnOpenPath_RoundTrips()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 3));
        try
        {
            using (var document = PdfDocument.Open(path))
            {
                document.Save(path);

                // The save detached the document from the file (buffer-and-release):
                // it must remain fully readable afterwards, still seeing the ORIGINAL revision.
                Assert.Equal(3, document.Pages.Count);
                Assert.NotNull(document.Objects.Trailer);
            }

            using var reopened = PdfDocument.Open(path);
            Assert.Equal(3, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveIncremental_ToItsOwnOpenPath_AppendsRatherThanTruncating()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 3));
        try
        {
            var originalLength = new FileInfo(path).Length;

            using (var document = PdfDocument.Open(path))
            {
                document.Pages.RemoveAt(0);
                document.SaveIncremental(path);
            }

            var updatedLength = new FileInfo(path).Length;
            Assert.True(updatedLength > originalLength, "SaveIncremental to the open path should append, not truncate.");

            var bytes = File.ReadAllBytes(path);
            using var reopened = PdfDocument.Open(bytes.AsMemory());
            Assert.Equal(2, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Open_EmptyOptionsDefaultsAreUsed()
    {
        var path = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            using var document = PdfDocument.Open(path);
            Assert.NotNull(document.Objects.Trailer);
            Assert.Empty(document.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
