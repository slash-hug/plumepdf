using System.Linq;
using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// The signature-survival regression lane: once a document carries a completed
/// signature, every byte before the prior revision's <c>%%EOF</c> — including that
/// signature's own <c>/Contents</c> — must come out of a later, unrelated
/// <c>SaveIncremental</c> pass unchanged, on both the same-path (append-in-place) and
/// different-path (copy-then-append) branches. A real externally-signed fixture (e.g.
/// veraPDF's <c>6-1-12-t01-pass-a.pdf</c>) lives in the gitignored, network-fetched
/// <c>corpora/</c> tree that <c>tests/PlumePdf.CorpusTests</c> self-skips without — this lane
/// instead hand-rolls a minimal but structurally real pre-signed fixture directly from
/// ISO 32000-1's object/xref-table grammar, the same approach allowed under
/// the clean-room policy in AGENTS.md that
/// <c>WriterTestDocuments</c> already uses, so the regression is exercised unconditionally in
/// every CI run rather than only when corpora happen to be present. A corpus-backed sibling
/// lane using the real veraPDF fixture can be added under <c>PlumePdf.CorpusTests</c> once
/// that fixture set is fetched.
/// </summary>
public class SignatureSurvivalTests
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

    /// <summary>
    /// A minimal, well-formed classic-xref document with one page and one already-"signed"
    /// <c>/Type /Sig</c> dictionary (a plausible-shaped, non-cryptographically-real
    /// <c>/Contents</c> hex string and a matching <c>/ByteRange</c> covering it) referenced
    /// from the page's <c>/Annots</c>, exactly as a real signature widget would be.
    /// </summary>
    private static byte[] BuildPreSignedDocument()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int contentNum = 4;
        const int sigNum = 5;
        const int widgetNum = 6;
        const int totalObjects = 7;

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        WriteObject(
            pageNum,
            $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 100] /Annots [{widgetNum} 0 R] /Contents {contentNum} 0 R >>");

        var content = "BT 20 50 Td (signed) Tj ET";
        offsets[contentNum] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"{contentNum} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        // A plausible-shaped signature dictionary — not a real CMS blob (this lane tests byte
        // survival, not signature verification), but the right shape: an even-length upper-hex
        // /Contents and a /ByteRange naming two ranges around it.
        var contentsHex = string.Concat(Enumerable.Repeat("AB", 32)); // 64 hex chars = 32 reserved bytes.
        WriteObject(sigNum, $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Contents <{contentsHex}> /ByteRange [0 100 200 300] >>");
        WriteObject(widgetNum, $"<< /Type /Annot /Subtype /Widget /FT /Sig /Rect [0 0 0 0] /P {pageNum} 0 R /V {sigNum} 0 R >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-sigsurvival-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void SaveIncremental_SamePath_MutatingUnrelatedObject_LeavesPriorBytesUntouched()
    {
        var sourceBytes = BuildPreSignedDocument();
        var path = WriteTempFile(sourceBytes);
        try
        {
            using (var document = PdfDocument.Open(path))
            {
                // Mutate something with nothing to do with the signature — the AcroForm-less
                // page's own dictionary, exactly the kind of unrelated edit that must never be
                // allowed to disturb a prior, already-signed revision.
                document.Objects.MarkDirty(document.Pages[0].Reference);
                document.SaveIncremental(path);
            }

            var afterBytes = File.ReadAllBytes(path);
            Assert.True(afterBytes.Length > sourceBytes.Length, "expected the mutation to append a new revision.");
            Assert.Equal(sourceBytes, afterBytes[..sourceBytes.Length]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SaveIncremental_DifferentPath_MutatingUnrelatedObject_LeavesPriorBytesUntouched()
    {
        var sourceBytes = BuildPreSignedDocument();
        var sourcePath = WriteTempFile(sourceBytes);
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-sigsurvival-out-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Objects.MarkDirty(document.Pages[0].Reference);
                document.SaveIncremental(outputPath);
            }

            var afterBytes = File.ReadAllBytes(outputPath);
            Assert.True(afterBytes.Length > sourceBytes.Length);
            Assert.Equal(sourceBytes, afterBytes[..sourceBytes.Length]);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void SaveIncremental_EncryptedAndSigned_ResavingWithSigDictDirty_ContentsSurvivesPlaintext()
    {
        // Overlap-resolution regression: on an encrypted source, EncryptForWrite must never encrypt a signature
        // dictionary's /Contents, even when that dictionary is itself part of the dirty set
        // being re-serialized (e.g. an unrelated widget-array rewrite that happens to touch
        // the same object) — otherwise an already-completed CMS signature would come out of
        // SaveIncremental corrupted (RC4/AES-transformed) despite not being the intended edit.
        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(
            userPassword: string.Empty, ownerPassword: "owner-secret", permissions: -4, FileId, keyLengthBytes: 5, revision: 2);

        var encryptDict = new PdfDictionary();
        encryptDict.Set(PdfName.V, PdfNumber.Get(1));
        encryptDict.Set(PdfName.R, PdfNumber.Get(2));
        encryptDict.Set(PdfName.O, PdfString.FromLiteral(o));
        encryptDict.Set(PdfName.U, PdfString.FromLiteral(u));
        encryptDict.Set(PdfName.P, PdfNumber.Get(-4));
        encryptDict.Set(PdfName.Length, PdfNumber.Get(40));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(3));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(1, 0)));
        trailer.Set(PdfName.Encrypt, encryptDict);
        trailer.Set(PdfName.Id, new PdfArray([PdfString.FromHex(FileId), PdfString.FromHex(FileId)]));

        // A stand-in "already-signed" payload — not a real CMS blob (this test asserts byte
        // survival through re-encryption, not signature validity).
        var sigContentsPlaintext = Encoding.ASCII.GetBytes(new string('Q', 16));

        var sigDict = new PdfDictionary();
        sigDict.Set(PdfName.Type, PdfName.Get("Sig"));
        sigDict.Set(PdfName.Get("Contents"), PdfString.FromHex(sigContentsPlaintext));

        var objectsDict = new Dictionary<int, PdfObject>
        {
            [1] = new PdfDictionary(),
            [2] = sigDict,
        };

        var registry = new ObjectRegistry(new InMemoryObjectSource(trailer, objectsDict), nextObjectNumber: 3);
        var handler = new StandardSecurityHandler(encryptDict, FileId, PdfOptions.Default);

        // The signature dictionary is itself re-serialized this pass (simulating an unrelated
        // operation that happens to touch the same object), not just some other object.
        registry.MarkDirty(new IndirectReference(2, 0));

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteAppendix(output, registry, pagesTreeDirty: false, topPagesReference: null, pages: [], previousStartXrefOffset: 0, PdfOptions.Default, handler);

        var appendixText = Encoding.ASCII.GetString(output.ToArray());
        var expectedHex = Convert.ToHexString(sigContentsPlaintext);
        Assert.Contains($"/Contents <{expectedHex}>", appendixText, StringComparison.Ordinal);
    }

    private static readonly byte[] FileId = Encoding.ASCII.GetBytes("0123456789ABCDEF");
}
