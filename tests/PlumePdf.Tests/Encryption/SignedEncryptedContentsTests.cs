using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Encryption;

/// <summary>
/// ISO 32000-1 §7.6.2 excludes a signature or
/// document-timestamp dictionary's <c>/Contents</c> entry from encryption in both directions
/// — a real signing producer writes it as literal, never-encrypted bytes even inside an
/// encrypted document, since it carries the raw CMS/PKCS#7 (or RFC 3161 DER) bytes the
/// signature's digest was computed and verified over. Before this fix,
/// <see cref="ObjectResolver"/>'s <c>Decrypt</c> ran <c>/Contents</c> through the security
/// handler like any other string, corrupting the embedded signature bytes on every open of a
/// signed, encrypted document. These fixtures are hand-authored (no committed binary, no
/// third-party PDF library) — same discipline as <c>ObjectResolverTests</c>, which this file's
/// helpers mirror.
/// </summary>
public class SignedEncryptedContentsTests
{
    private static readonly byte[] Header = "%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8.ToArray();
    private static readonly byte[] FileId = "0123456789ABCDEF"u8.ToArray();

    // Stands in for a real CMS/PKCS#7 DER blob. Deliberately not valid AES-CBC ciphertext
    // under any key (arbitrary ASCII, not a multiple-of-16 tail that happens to PKCS7-depad) —
    // if ObjectResolver ever again tries to run /Contents through AES decryption, that fails
    // loudly (PLUME4005, wrapped as PLUME4006 at the object-resolve level) rather than
    // silently handing back different bytes. The regression this test guards against is
    // therefore observable two ways: no PLUME4xxx diagnostic, AND byte-for-byte identity.
    private static readonly byte[] FakeCmsBytes = Encoding.ASCII.GetBytes("--FAKE-CMS-SIGNEDDATA-PLACEHOLDER-NOT-REAL-ASN1-BYTES--");

    private static byte[] IndirectObj(int number, string body) =>
        Encoding.ASCII.GetBytes($"{number} 0 obj\n{body}\nendobj\n");

    private static string HexString(byte[] data) => "<" + Convert.ToHexString(data) + ">";

    private static byte[] ClassicXrefAndTrailer(List<byte> buffer, IReadOnlyDictionary<int, int> offsets, int size, string trailerEntries)
    {
        var xrefOffset = buffer.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n").Append("0 ").Append(size).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var n = 1; n < size; n++)
        {
            sb.Append(offsets.TryGetValue(n, out var offset)
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 00000 f \n");
        }

        sb.Append("trailer\n").Append(trailerEntries).Append("\nstartxref\n").Append(xrefOffset).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string AesEncryptDict(string o, string u, int permissions, int revision) =>
        $"<< /Filter /Standard /V 4 /R {revision} /Length 128 " +
        "/CF << /StdCF << /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> /StmF /StdCF /StrF /StdCF " +
        $"/O {o} /U {u} /P {permissions} >>";

    /// <summary>
    /// Object numbering: 1 Catalog, 2 Pages (no kids), 3 the signature/timestamp dictionary
    /// under test, 4 the Encrypt dictionary. Object 3's <c>/Contents</c> is
    /// <see cref="FakeCmsBytes"/> written literally (never encrypted, matching real signing
    /// producers); its <c>/Reason</c> is a genuinely AES-encrypted string, present as the
    /// precision control proving the carve-out doesn't blanket-skip the rest of the
    /// dictionary.
    /// </summary>
    private static byte[] BuildSignatureFixture(string typeValue, out byte[] reasonPlaintext)
    {
        const int keyLengthBytes = 16;
        const int revision = 4;
        const int permissions = -4;

        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(
            userPassword: "", ownerPassword: "owner-secret", permissions, FileId, keyLengthBytes, revision);

        reasonPlaintext = Encoding.ASCII.GetBytes("Approved by PlumePDF test fixture");
        var reasonObjectKey = EncryptionFixtureBuilder.ObjectKey(fileKey, 3, 0, aes: true);
        var reasonCiphertext = EncryptionFixtureBuilder.EncryptAes(reasonObjectKey, reasonPlaintext);

        var buffer = new List<byte>(Header);
        var offsets = new Dictionary<int, int>();

        void Append(int number, byte[] bytes)
        {
            offsets[number] = buffer.Count;
            buffer.AddRange(bytes);
        }

        Append(1, IndirectObj(1, "<< /Type /Catalog /Pages 2 0 R >>"));
        Append(2, IndirectObj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        Append(3, IndirectObj(3,
            $"<< /Type /{typeValue} /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
            $"/Contents {HexString(FakeCmsBytes)} /ByteRange [0 1 2 3] " +
            $"/Reason {HexString(reasonCiphertext)} >>"));
        Append(4, IndirectObj(4, AesEncryptDict(HexString(o), HexString(u), permissions, revision)));

        var trailer = $"<< /Size 5 /Root 1 0 R /Encrypt 4 0 R /ID [{HexString(FileId)} {HexString(FileId)}] >>";
        buffer.AddRange(ClassicXrefAndTrailer(buffer, offsets, 5, trailer));
        return [.. buffer];
    }

    [Theory]
    [InlineData("Sig")]
    [InlineData("DocTimeStamp")]
    public void SignatureContents_SurvivesEncryptedRoundTrip_ByteIdentical(string typeValue)
    {
        var pdfBytes = BuildSignatureFixture(typeValue, out var reasonPlaintext);

        using var document = PdfDocument.Open(pdfBytes);

        Assert.True(document.HasEncryptedSource);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code.StartsWith("PLUME4", StringComparison.Ordinal));

        var sigDict = Assert.IsType<PdfDictionary>(document.Objects[new IndirectReference(3, 0)]);
        Assert.True(sigDict.TryGetValue(PdfName.Get("Contents"), out var contentsValue));
        var contents = Assert.IsType<PdfString>(contentsValue);
        Assert.Equal(FakeCmsBytes, contents.Bytes.ToArray());

        // Precision control: /Reason sits in the same dictionary and IS genuinely encrypted
        // on disk - it must still come back decrypted, proving the carve-out targets exactly
        // /Contents rather than skipping the whole signature dictionary.
        Assert.True(sigDict.TryGetValue(PdfName.Get("Reason"), out var reasonValue));
        var reason = Assert.IsType<PdfString>(reasonValue);
        Assert.Equal(reasonPlaintext, reason.Bytes.ToArray());
    }

    [Fact]
    public void NonSignatureDictionary_ContentsKeyStillDecryptsNormally()
    {
        // Precision control the other direction: a /Type /Annot dictionary's own /Contents
        // (annotation text, ISO 32000-1 §12.5.6.2 - an unrelated meaning) must still decrypt
        // normally. The carve-out gates on /Type /Sig or /Type /DocTimeStamp specifically,
        // never on the mere presence of a /Contents key.
        const int keyLengthBytes = 16;
        const int revision = 4;
        const int permissions = -4;

        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(
            userPassword: "", ownerPassword: "owner-secret", permissions, FileId, keyLengthBytes, revision);

        var annotPlaintext = Encoding.ASCII.GetBytes("A sticky note, not a signature.");
        var annotObjectKey = EncryptionFixtureBuilder.ObjectKey(fileKey, 3, 0, aes: true);
        var annotCiphertext = EncryptionFixtureBuilder.EncryptAes(annotObjectKey, annotPlaintext);

        var buffer = new List<byte>(Header);
        var offsets = new Dictionary<int, int>();

        void Append(int number, byte[] bytes)
        {
            offsets[number] = buffer.Count;
            buffer.AddRange(bytes);
        }

        Append(1, IndirectObj(1, "<< /Type /Catalog /Pages 2 0 R >>"));
        Append(2, IndirectObj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        Append(3, IndirectObj(3, $"<< /Type /Annot /Subtype /Text /Contents {HexString(annotCiphertext)} >>"));
        Append(4, IndirectObj(4, AesEncryptDict(HexString(o), HexString(u), permissions, revision)));

        var trailer = $"<< /Size 5 /Root 1 0 R /Encrypt 4 0 R /ID [{HexString(FileId)} {HexString(FileId)}] >>";
        buffer.AddRange(ClassicXrefAndTrailer(buffer, offsets, 5, trailer));

        byte[] pdfBytes = [.. buffer];
        using var document = PdfDocument.Open(pdfBytes);

        Assert.DoesNotContain(document.Diagnostics, d => d.Code.StartsWith("PLUME4", StringComparison.Ordinal));
        var annotDict = Assert.IsType<PdfDictionary>(document.Objects[new IndirectReference(3, 0)]);
        Assert.True(annotDict.TryGetValue(PdfName.Get("Contents"), out var contentsValue));
        var contents = Assert.IsType<PdfString>(contentsValue);
        Assert.Equal(annotPlaintext, contents.Bytes.ToArray());
    }
}
