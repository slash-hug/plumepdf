using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// <c>ObjectRegistry</c>'s general dirty-object set (<c>MarkDirty</c>/<c>RegisterNew</c>) as
/// consumed by <c>IncrementalUpdateWriter</c> and <c>FullRewriteWriter</c>
/// — including the live bug this replaces: mutating an object via
/// <c>doc.Objects</c> and calling <c>SaveIncremental</c> used to produce byte-identical
/// (i.e. silently unchanged) output, because the writer only ever looked at a single
/// page-tree-reorder bool. Also covers the narrow encrypted-source re-encryption on
/// <c>SaveIncremental</c>.
/// </summary>
public class DirtyObjectSetTests
{
    private static readonly PdfName VName = PdfName.Get("V");
    private static readonly PdfName CustomName = PdfName.Get("Custom");
    private static readonly PdfName CustomLinkName = PdfName.Get("CustomLink");

    [Fact]
    public void SaveIncremental_MarkDirtyAfterInPlaceMutation_ReopenedValueReflectsChange()
    {
        var sourceBytes = WriterTestDocuments.BuildDocument(pageCount: 2);
        var sourcePath = WriterTestDocuments.WriteTempFile(sourceBytes);
        var outputPath = TempPdfPath();
        try
        {
            var fontReference = new IndirectReference(3, 0);
            using (var document = PdfDocument.Open(sourcePath))
            {
                var font = Assert.IsType<PdfDictionary>(document.Objects[fontReference]);
                font.Set(VName, PdfString.FromLiteral("Jane Doe"u8.ToArray()));
                document.Objects.MarkDirty(fontReference);

                document.SaveIncremental(outputPath);
            }

            // The whole point of this test: the appendix must actually exist now, not be a no-op copy.
            var outputBytes = File.ReadAllBytes(outputPath);
            Assert.True(outputBytes.Length > sourceBytes.Length, "SaveIncremental produced no appendix for a marked-dirty mutation.");

            using var reopened = PdfDocument.Open(outputPath);
            var reopenedFont = Assert.IsType<PdfDictionary>(reopened.Objects[fontReference]);
            Assert.True(reopenedFont.TryGetValue(VName, out var value));
            Assert.Equal("Jane Doe"u8.ToArray(), Assert.IsType<PdfString>(value).Bytes.ToArray());
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_InPlaceMutationWithoutMarkDirty_IsNotWritten()
    {
        // The documented flip side of the test above: ObjectRegistry has no ambient/automatic
        // dirty tracking — an in-place edit that never calls MarkDirty is, by contract, not
        // picked up by SaveIncremental. This is what makes MarkDirty meaningful rather than
        // vestigial; it must keep failing this way, not silently start "just working".
        var sourceBytes = WriterTestDocuments.BuildDocument(pageCount: 2);
        var sourcePath = WriterTestDocuments.WriteTempFile(sourceBytes);
        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                var font = Assert.IsType<PdfDictionary>(document.Objects[new IndirectReference(3, 0)]);
                font.Set(VName, PdfString.FromLiteral("never marked dirty"u8.ToArray()));

                document.SaveIncremental(outputPath);
            }

            Assert.Equal(sourceBytes, File.ReadAllBytes(outputPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_RegisterNewObject_ResolvesImmediatelyAndAfterReopen()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = TempPdfPath();
        try
        {
            IndirectReference newReference;
            using (var document = PdfDocument.Open(sourcePath))
            {
                newReference = document.Objects.AllocateNumber();
                var newDict = new PdfDictionary();
                newDict.Set(CustomName, PdfString.FromLiteral("hello, new object"u8.ToArray()));
                document.Objects.RegisterNew(newReference, newDict);

                // Resolvable through the same live registry immediately, before any save.
                Assert.Same(newDict, document.Objects[newReference]);

                document.SaveIncremental(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            var reopenedDict = Assert.IsType<PdfDictionary>(reopened.Objects[newReference]);
            Assert.True(reopenedDict.TryGetValue(CustomName, out var value));
            Assert.Equal("hello, new object"u8.ToArray(), Assert.IsType<PdfString>(value).Bytes.ToArray());
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Save_FullRewrite_NewObjectWiredIntoCatalog_SurvivesRenumbering()
    {
        // FullRewriteWriter needs no special-casing for RegisterNew objects — its ordinary
        // reference-discovery walk resolves them through ObjectRegistry's overlay exactly like
        // any original object, so a reachable new object gets renumbered and serialized right
        // alongside everything else. Resolved dynamically via the catalog's own new key after
        // reopening, since Save renumbers everything from 1 (the original newReference number
        // is meaningless in the output).
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = TempPdfPath();
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                var newReference = document.Objects.AllocateNumber();
                var newDict = new PdfDictionary();
                newDict.Set(CustomName, PdfNumber.Get(42));
                document.Objects.RegisterNew(newReference, newDict);

                document.Catalog!.Dictionary.Set(CustomLinkName, new PdfReference(newReference));
                document.Objects.MarkDirty(document.Catalog.Reference);

                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            Assert.True(reopened.Catalog!.Dictionary.TryGetValue(CustomLinkName, out var linkValue));
            var linked = Assert.IsType<PdfDictionary>(reopened.Objects[Assert.IsType<PdfReference>(linkValue).Target]);
            Assert.True(linked.TryGetValue(CustomName, out var customValue));
            Assert.True(Assert.IsType<PdfNumber>(customValue).TryToInt32(out var intValue));
            Assert.Equal(42, intValue);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData("RC4-128.pdf")]
    [InlineData("AES-128.pdf")]
    public void SaveIncremental_EncryptedSource_NewObjectsReEncryptedAndRoundTripAfterReopen(string fixtureName)
    {
        // An encrypted source no longer refuses SaveIncremental outright — the
        // appended string/stream must come back correctly decrypted on reopen, which can only
        // happen if IncrementalUpdateWriter actually re-encrypted it (the reader unconditionally
        // tries to decrypt every string/stream of an encrypted document, so a plaintext-by-mistake
        // write would come back garbled here, not merely "not encrypted").
        var sourcePath = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", fixtureName);
        var outputPath = TempPdfPath();
        var streamPlaintext = Encoding.ASCII.GetBytes("Filled via SaveIncremental on an encrypted source.");
        try
        {
            IndirectReference stringReference;
            IndirectReference streamReference;
            using (var document = PdfDocument.Open(sourcePath))
            {
                Assert.True(document.HasEncryptedSource);

                stringReference = document.Objects.AllocateNumber();
                var stringDict = new PdfDictionary();
                stringDict.Set(VName, PdfString.FromLiteral("Jane Doe"u8.ToArray()));
                document.Objects.RegisterNew(stringReference, stringDict);

                streamReference = document.Objects.AllocateNumber();
                document.Objects.RegisterNew(streamReference, new PdfStream(new PdfDictionary(), streamPlaintext));

                document.SaveIncremental(outputPath);
            }

            // The appended trailer must repeat /Encrypt itself (verified independently against
            // qpdf --check during development: qpdf determines whether a file is encrypted from
            // the newest trailer alone and does not walk /Prev looking for one, so an appendix
            // that omits it produces a file every third-party PDF processor treats as
            // unencrypted — silently exposing PlumePDF's own re-encrypted ciphertext as if it
            // were plaintext to every reader but PlumePDF's own lenient, backfilling one).
            var outputBytes = File.ReadAllBytes(outputPath);
            var appendedTrailerStart = outputBytes.AsSpan().LastIndexOf("trailer"u8);
            Assert.True(appendedTrailerStart >= 0, "no 'trailer' keyword found in the appended revision.");
            var appendedTrailerText = Encoding.ASCII.GetString(outputBytes, appendedTrailerStart, outputBytes.Length - appendedTrailerStart);
            Assert.Contains("/Encrypt", appendedTrailerText, StringComparison.Ordinal);

            using var reopened = PdfDocument.Open(outputPath);
            Assert.True(reopened.HasEncryptedSource);
            Assert.DoesNotContain(reopened.Diagnostics, d => d.Code.StartsWith("PLUME4", StringComparison.Ordinal));

            var reopenedDict = Assert.IsType<PdfDictionary>(reopened.Objects[stringReference]);
            Assert.True(reopenedDict.TryGetValue(VName, out var value));
            Assert.Equal("Jane Doe"u8.ToArray(), Assert.IsType<PdfString>(value).Bytes.ToArray());

            var reopenedStream = Assert.IsType<PdfStream>(reopened.Objects[streamReference]);
            Assert.Equal(streamPlaintext, reopenedStream.GetDecodedBytes(PdfFilterRegistry.Default));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-dirtyset-{Guid.NewGuid():N}.pdf");

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
}
