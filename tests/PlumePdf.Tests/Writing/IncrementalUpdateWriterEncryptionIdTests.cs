using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// <c>IncrementalUpdateWriter.ComputeId</c>'s handling of an encrypted source with no
/// original <c>/ID</c> — the gap where a freshly-fabricated <c>/ID[0]</c>
/// would desynchronize the appended revision's trailer from the file key
/// <c>StandardSecurityHandler</c> actually derived at <c>Open</c> time.
/// </summary>
public class IncrementalUpdateWriterEncryptionIdTests
{
    [Fact]
    public void WriteAppendix_EncryptedSourceWithNoOriginalId_PreservesAbsenceRatherThanFabricatingOne()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(2));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));

        var encryptDict = new PdfDictionary();
        encryptDict.Set(PdfName.Get("Filter"), PdfName.Get("Standard"));
        trailer.Set(PdfName.Encrypt, encryptDict);
        // Deliberately no /ID entry at all — the exact source shape this fix targets.

        var objects = new Dictionary<int, PdfObject> { [1] = new PdfDictionary() };
        var registry = new ObjectRegistry(new InMemoryObjectSource(trailer, objects));
        registry.MarkDirty(new IndirectReference(1, 0));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteAppendix(output, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);

        var text = Encoding.ASCII.GetString(output.ToArray());
        var trailerStart = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailerStart >= 0, "no 'trailer' keyword found in the written appendix.");
        var trailerText = text[trailerStart..];

        // The written /ID array's first element must be the empty hex string "<>" — matching
        // what the security handler actually derived the file key against when the source
        // carried no /ID (StandardSecurityHandler's fileId contract: null/absent becomes an
        // empty byte array, never a fabricated one) — not a freshly-minted 16+ byte random id
        // that would desynchronize the key on reopen.
        Assert.Matches(@"/ID\s*\[\s*<>\s*<[0-9A-Fa-f]+>\s*\]", trailerText);
    }

    [Fact]
    public void WriteAppendix_UnencryptedSourceWithNoOriginalId_StillFabricatesOne()
    {
        // The flip side: an unencrypted source has no file key to desynchronize, so the
        // existing "fabricate a fresh document id" behavior is correct and must not change.
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(2));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));
        // No /Encrypt, no /ID.

        var objects = new Dictionary<int, PdfObject> { [1] = new PdfDictionary() };
        var registry = new ObjectRegistry(new InMemoryObjectSource(trailer, objects));
        registry.MarkDirty(new IndirectReference(1, 0));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteAppendix(output, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default);

        var text = Encoding.ASCII.GetString(output.ToArray());
        var trailerStart = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailerStart >= 0);
        var trailerText = text[trailerStart..];

        Assert.DoesNotMatch(@"/ID\s*\[\s*<>", trailerText);
        Assert.Matches(@"/ID\s*\[\s*<[0-9A-Fa-f]{16,}>", trailerText);
    }
}
