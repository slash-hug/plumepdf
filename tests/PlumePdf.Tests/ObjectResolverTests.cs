using System.Security.Cryptography;
using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Coverage for two <see cref="ObjectResolver"/>-facing rulings:
/// <see cref="PdfOptions.Filters"/> is actually consulted while resolving a document, not
/// just stored, and the <c>/EncryptMetadata false</c> exemption (ISO 32000-1 §7.6.2).
/// Every fixture here is a hand-authored, minimal PDF built in-memory (no committed binary
/// fixture, no third-party PDF library) — same self-authorship discipline as
/// <c>tests/PlumePdf.CorpusTests/Fixtures/generate_fixtures.py</c>, just built at test-run
/// time in C# instead of pre-generated to disk.
/// </summary>
public class ObjectResolverTests
{
    private static readonly byte[] Header = "%PDF-1.7\n%\xE2\xE3\xCF\xD3\n"u8.ToArray();

    private static byte[] IndirectObj(int number, string body) =>
        Encoding.ASCII.GetBytes($"{number} 0 obj\n{body}\nendobj\n");

    private static byte[] StreamObj(int number, string dictEntries, byte[] data)
    {
        var head = Encoding.ASCII.GetBytes($"{number} 0 obj\n<< {dictEntries} /Length {data.Length} >>\nstream\n");
        var tail = "\nendstream\nendobj\n"u8.ToArray();
        var result = new byte[head.Length + data.Length + tail.Length];
        head.CopyTo(result, 0);
        data.CopyTo(result, head.Length);
        tail.CopyTo(result, head.Length + data.Length);
        return result;
    }

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

    // --- S4: PdfOptions.Filters plumbing ---------------------------------------------------

    /// <summary>Self-inverse XOR "filter" - just enough of a codec to prove a caller-supplied <see cref="PdfFilterRegistry"/> is the one actually consulted, without needing a second real codec.</summary>
    private sealed class XorTestFilter(byte[] key) : IPdfFilter
    {
        public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
        {
            var span = data.Span;
            var result = new byte[span.Length];
            for (var i = 0; i < span.Length; i++)
            {
                result[i] = (byte)(span[i] ^ key[i % key.Length]);
            }

            return result;
        }
    }

    private const string CustomFilterName = "X-PlumePdfTestXor";
    private static readonly byte[] XorKey = "plumepdf-filters-plumbing-test-key"u8.ToArray();

    /// <summary>
    /// Builds a minimal document whose object 3 lives inside object stream 4, itself encoded
    /// with <see cref="CustomFilterName"/> (not one of <see cref="PdfFilterRegistry.Default"/>'s
    /// registrations) - resolving object 3 only succeeds when the caller's own
    /// <see cref="PdfOptions.Filters"/> registry is what <see cref="ObjectStreamReader"/>
    /// actually decodes through. Object numbering: 1 Catalog, 2 Pages (no kids - nothing here
    /// exercises the page tree), 3 the payload dictionary, 4 the object stream, 5 the
    /// cross-reference stream (itself unfiltered, so it decodes identically under either
    /// registry - only object 4 is the discriminator).
    /// </summary>
    private static byte[] BuildCustomFilterObjectStreamFixture(out string expectedMarker)
    {
        expectedMarker = "custom-filter-consulted";
        var buffer = new List<byte>(Header);
        var offsets = new Dictionary<int, int>();

        void Append(int number, byte[] bytes)
        {
            offsets[number] = buffer.Count;
            buffer.AddRange(bytes);
        }

        Append(1, IndirectObj(1, "<< /Type /Catalog /Pages 2 0 R >>"));
        Append(2, IndirectObj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));

        const string memberHeader = "3 0";
        var memberBody = $"<< /Marker ({expectedMarker}) >>";
        var first = memberHeader.Length + 1;
        var plainObjStm = Encoding.ASCII.GetBytes($"{memberHeader} {memberBody}");
        var encodedObjStm = new byte[plainObjStm.Length];
        for (var i = 0; i < plainObjStm.Length; i++)
        {
            encodedObjStm[i] = (byte)(plainObjStm[i] ^ XorKey[i % XorKey.Length]);
        }

        Append(4, StreamObj(4, $"/Type /ObjStm /N 1 /First {first} /Filter /{CustomFilterName}", encodedObjStm));

        const int size = 6;
        var selfOffset = buffer.Count;
        var records = new (int Type, int Field2, int Field3)[size];
        records[0] = (0, 0, 0);
        records[1] = (1, offsets[1], 0);
        records[2] = (1, offsets[2], 0);
        records[3] = (2, 4, 0); // object 3 lives in object stream 4, index 0
        records[4] = (1, offsets[4], 0);
        records[5] = (1, selfOffset, 0);

        var raw = new byte[size * 6]; // widths (1,4,1)
        for (var i = 0; i < size; i++)
        {
            var (type, field2, field3) = records[i];
            raw[(i * 6) + 0] = (byte)type;
            raw[(i * 6) + 1] = (byte)(field2 >> 24);
            raw[(i * 6) + 2] = (byte)(field2 >> 16);
            raw[(i * 6) + 3] = (byte)(field2 >> 8);
            raw[(i * 6) + 4] = (byte)field2;
            raw[(i * 6) + 5] = (byte)field3;
        }

        Append(5, StreamObj(5, $"/Type /XRef /Size {size} /W [1 4 1] /Root 1 0 R", raw));

        buffer.AddRange(Encoding.ASCII.GetBytes($"\nstartxref\n{selfOffset}\n%%EOF"));
        return [.. buffer];
    }

    [Fact]
    public void CustomFilterRegistry_SuppliedViaOptions_IsConsultedDecodingAnObjectStream()
    {
        var pdfBytes = BuildCustomFilterObjectStreamFixture(out var expectedMarker);
        var registry = new PdfFilterRegistry();
        registry.Register(CustomFilterName, new XorTestFilter(XorKey));
        var options = PdfOptions.Default with { Filters = registry };

        using var document = PdfDocument.Open(pdfBytes, options);
        var resolved = document.Objects[new IndirectReference(3, 0)];

        var dict = Assert.IsType<PdfDictionary>(resolved);
        Assert.True(dict.TryGetValue(PdfName.Get("Marker"), out var markerValue));
        var marker = Assert.IsType<PdfString>(markerValue);
        Assert.Equal(expectedMarker, Encoding.ASCII.GetString(marker.Bytes.Span));
    }

    [Fact]
    public void DefaultFilterRegistry_DoesNotKnowTheCustomFilter_RecordsADiagnosticInstead()
    {
        // The negative control that makes the test above meaningful: PdfOptions.Default's
        // registry has no reason to know a filter name this test just invented, so leaving
        // PdfOptions.Filters unset must fail to decode object 4's payload - proving the
        // custom-registry test above genuinely depends on options.Filters being threaded
        // through ObjectStreamReader, not on the object being trivially resolvable anyway.
        var pdfBytes = BuildCustomFilterObjectStreamFixture(out _);

        using var document = PdfDocument.Open(pdfBytes); // PdfOptions.Default: Filters unset
        var resolved = document.Objects[new IndirectReference(3, 0)];

        Assert.IsType<PdfNull>(resolved);
        Assert.Contains(document.Diagnostics, d => d.Code == "PLUME2063" && d.Message.Contains(CustomFilterName, StringComparison.Ordinal));
    }

    // --- S5: /EncryptMetadata false decryption exemption (ISO 32000-1 §7.6.2) --------------

    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static byte[] PadPassword(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        var result = new byte[32];
        var count = Math.Min(bytes.Length, 32);
        bytes.AsSpan(0, count).CopyTo(result);
        Pad.AsSpan(0, 32 - count).CopyTo(result.AsSpan(count));
        return result;
    }

    private static byte[] RoundKey(byte[] key, int round)
    {
        var result = new byte[key.Length];
        for (var i = 0; i < key.Length; i++)
        {
            result[i] = (byte)(key[i] ^ round);
        }

        return result;
    }

    /// <summary>Algorithm 3 (owner entry), revision &gt;= 3 form - unaffected by <c>/EncryptMetadata</c>.</summary>
    private static byte[] ComputeOwnerEntry(string ownerPassword, string userPassword, int keyLengthBytes)
    {
        var hash = MD5.HashData(PadPassword(ownerPassword));
        for (var i = 0; i < 50; i++)
        {
            hash = MD5.HashData(hash);
        }

        var rc4Key = hash[..keyLengthBytes];
        var current = Rc4.Transform(rc4Key, PadPassword(userPassword));
        for (var round = 1; round <= 19; round++)
        {
            current = Rc4.Transform(RoundKey(rc4Key, round), current);
        }

        return current;
    }

    /// <summary>Algorithm 2 (file encryption key), including the revision-&gt;=4 <c>/EncryptMetadata false</c> suffix this fixture exists to exercise.</summary>
    private static byte[] ComputeFileKey(string userPassword, byte[] ownerEntry, int permissions, byte[] fileId, int keyLengthBytes, int revision, bool encryptMetadata)
    {
        using var buffer = new MemoryStream();
        buffer.Write(PadPassword(userPassword));
        buffer.Write(ownerEntry, 0, Math.Min(32, ownerEntry.Length));
        buffer.WriteByte((byte)(permissions & 0xFF));
        buffer.WriteByte((byte)((permissions >> 8) & 0xFF));
        buffer.WriteByte((byte)((permissions >> 16) & 0xFF));
        buffer.WriteByte((byte)((permissions >> 24) & 0xFF));
        buffer.Write(fileId);
        if (revision >= 4 && !encryptMetadata)
        {
            buffer.Write([0xFF, 0xFF, 0xFF, 0xFF]);
        }

        var hash = MD5.HashData(buffer.ToArray());
        for (var i = 0; i < 50; i++)
        {
            hash = MD5.HashData(hash.AsSpan(0, keyLengthBytes).ToArray());
        }

        return hash[..keyLengthBytes];
    }

    /// <summary>Algorithm 5 (user entry), revision &gt;= 3 form.</summary>
    private static byte[] ComputeUserEntry(byte[] fileKey, byte[] fileId)
    {
        using var buffer = new MemoryStream();
        buffer.Write(Pad);
        buffer.Write(fileId);
        var digest = MD5.HashData(buffer.ToArray());
        var encrypted = Rc4.Transform(fileKey, digest);
        for (var round = 1; round <= 19; round++)
        {
            encrypted = Rc4.Transform(RoundKey(fileKey, round), encrypted);
        }

        var result = new byte[32];
        encrypted.CopyTo(result, 0);
        return result;
    }

    private static byte[] AesObjectKey(byte[] fileKey, int number, int generation)
    {
        var extra = new byte[9];
        extra[0] = (byte)number;
        extra[1] = (byte)(number >> 8);
        extra[2] = (byte)(number >> 16);
        extra[3] = (byte)generation;
        extra[4] = (byte)(generation >> 8);
        "sAlT"u8.CopyTo(extra.AsSpan(5));

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        incremental.AppendData(fileKey);
        incremental.AppendData(extra);
        var digest = incremental.GetHashAndReset();
        return digest[..Math.Min(fileKey.Length + 5, 16)];
    }

    private static byte[] EncryptAes(byte[] key, byte[] plaintext)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        var result = new byte[16 + ciphertext.Length];
        iv.CopyTo(result, 0);
        ciphertext.CopyTo(result, 16);
        return result;
    }

    private static string HexString(byte[] data) => "<" + Convert.ToHexString(data) + ">";

    /// <summary>
    /// Builds an AES-128 (V4/R4, <c>/AESV2</c>) encrypted document, empty user/owner
    /// passwords, whose <c>/Encrypt</c> dictionary declares <c>/EncryptMetadata false</c>: a
    /// document-level <c>/Type /Metadata</c> stream carrying <paramref name="xmpPlaintext"/>
    /// bytes exactly as-is (never encrypted - real producers don't encrypt this stream at all
    /// under this configuration, per ISO 32000-1 §7.6.2), plus an ordinary content stream
    /// carrying <paramref name="contentPlaintext"/>, genuinely AES-encrypted, to prove normal
    /// decryption still works. Object numbering: 1 Catalog, 2 Pages (no kids), 3 content
    /// stream, 4 metadata stream, 5 Encrypt dictionary.
    /// </summary>
    private static byte[] BuildEncryptMetadataFalseFixture(byte[] xmpPlaintext, byte[] contentPlaintext) =>
        BuildEncryptMetadataFalseFixture(xmpPlaintext, contentPlaintext, out _);

    private static byte[] BuildEncryptMetadataFalseFixture(byte[] xmpPlaintext, byte[] contentPlaintext, out byte[] contentCiphertext)
    {
        const int keyLengthBytes = 16;
        const int revision = 4;
        const int permissions = -4;
        var fileId = "0123456789ABCDEF"u8.ToArray();

        var ownerEntry = ComputeOwnerEntry(ownerPassword: "owner-secret", userPassword: "", keyLengthBytes);
        var fileKey = ComputeFileKey(userPassword: "", ownerEntry, permissions, fileId, keyLengthBytes, revision, encryptMetadata: false);
        var userEntry = ComputeUserEntry(fileKey, fileId);

        contentCiphertext = EncryptAes(AesObjectKey(fileKey, 3, 0), contentPlaintext);

        var buffer = new List<byte>(Header);
        var offsets = new Dictionary<int, int>();

        void Append(int number, byte[] bytes)
        {
            offsets[number] = buffer.Count;
            buffer.AddRange(bytes);
        }

        Append(1, IndirectObj(1, "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>"));
        Append(2, IndirectObj(2, "<< /Type /Pages /Kids [] /Count 0 >>"));
        Append(3, StreamObj(3, string.Empty, contentCiphertext));
        Append(4, StreamObj(4, "/Type /Metadata /Subtype /XML", xmpPlaintext));

        var encryptDict =
            "<< /Filter /Standard /V 4 /R 4 /Length 128 " +
            "/CF << /StdCF << /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> " +
            "/StmF /StdCF /StrF /StdCF " +
            $"/O {HexString(ownerEntry)} /U {HexString(userEntry)} /P {permissions} /EncryptMetadata false >>";
        Append(5, IndirectObj(5, encryptDict));

        var trailer = $"<< /Size 6 /Root 1 0 R /Encrypt 5 0 R /ID [{HexString(fileId)} {HexString(fileId)}] >>";
        buffer.AddRange(ClassicXrefAndTrailer(buffer, offsets, 6, trailer));
        return [.. buffer];
    }

    [Fact]
    public void EncryptMetadataFalse_MetadataStream_IsNeverDecrypted()
    {
        var xmp = Encoding.ASCII.GetBytes("<x:xmpmeta>plumepdf encryptmetadata=false marker</x:xmpmeta>");
        var content = "BT /F1 12 Tf 20 50 Td (Hello) Tj ET"u8.ToArray();
        // The fixture only proves what it claims to if AES-CBC decryption of this un-encrypted
        // payload would deterministically fail (a block-size mismatch, not merely an
        // improbable padding coincidence) were the S5 exemption missing - see BuildEncryptMetadataFalseFixture's remarks.
        Assert.NotEqual(0, xmp.Length % 16);

        var pdfBytes = BuildEncryptMetadataFalseFixture(xmp, content);

        using var document = PdfDocument.Open(pdfBytes);

        Assert.True(document.HasEncryptedSource);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code.StartsWith("PLUME4", StringComparison.Ordinal));

        var metadataStream = Assert.IsType<PdfStream>(document.Objects[new IndirectReference(4, 0)]);
        Assert.Equal(xmp, metadataStream.RawBytes.ToArray());

        var contentStream = Assert.IsType<PdfStream>(document.Objects[new IndirectReference(3, 0)]);
        Assert.Equal(content, contentStream.GetDecodedBytes(PdfFilterRegistry.Default));
    }

    [Fact]
    public void EncryptMetadataFalse_NonMetadataStreamOfTheSameShape_StillDecryptsNormally()
    {
        // Guards the exemption's precision: only a /Type /Metadata stream is exempted. A
        // stream that merely happens to sit alongside one (here, the ordinary content stream
        // object 3) must still go through real decryption - this is EncryptionEndToEndTests'
        // existing regression, re-asserted under the /EncryptMetadata false configuration
        // specifically, since S5 changes the same Decrypt code path both share.
        var xmp = Encoding.ASCII.GetBytes("<x:xmpmeta>unused for this test</x:xmpmeta>");
        var content = "q 1 0 0 1 0 0 cm Q"u8.ToArray();
        var pdfBytes = BuildEncryptMetadataFalseFixture(xmp, content, out var contentCiphertext);
        // Proves the fixture genuinely encrypted object 3 (so decryption below is meaningful,
        // not vacuously true because nothing was encrypted to begin with).
        Assert.NotEqual(content, contentCiphertext);

        using var document = PdfDocument.Open(pdfBytes);
        var contentStream = Assert.IsType<PdfStream>(document.Objects[new IndirectReference(3, 0)]);

        // ObjectResolver.Decrypt replaces the resolved PdfStream's RawBytes with the
        // decrypted payload in place - real decryption ran, unlike the exempted /Type
        // /Metadata stream, whose RawBytes stay exactly the source bytes (previous test).
        Assert.Equal(content, contentStream.RawBytes.ToArray());
        Assert.Equal(content, contentStream.GetDecodedBytes(PdfFilterRegistry.Default));
    }
}
