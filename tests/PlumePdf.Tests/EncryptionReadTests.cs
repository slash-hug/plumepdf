using System.Security.Cryptography;
using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Builds standalone encrypted-document credentials (independent of
/// <c>StandardSecurityHandler</c>'s own implementation, sharing only the RC4/MD5/AES/SHA
/// primitives) so <c>EncryptionReadTests</c> doesn't need a real Acrobat/qpdf-produced
/// fixture — fixtures are generated inline until corpus
/// fixtures (which may include real encrypted PDFs) land.
/// </summary>
internal static class EncryptionFixtureBuilder
{
    private static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    public static (byte[] O, byte[] U, byte[] FileKey) BuildRc4OrAesCredentials(string userPassword, string ownerPassword, int permissions, byte[] fileId, int keyLengthBytes, int revision)
    {
        var ownerHash = MD5.HashData(Pad(ownerPassword));
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                ownerHash = MD5.HashData(ownerHash);
            }
        }

        var rc4KeyForO = ownerHash[..keyLengthBytes];
        var userPad = Pad(userPassword);

        byte[] o;
        if (revision == 2)
        {
            o = Rc4.Transform(rc4KeyForO, userPad);
        }
        else
        {
            var current = Rc4.Transform(rc4KeyForO, userPad);
            for (var round = 1; round <= 19; round++)
            {
                current = Rc4.Transform(Xor(rc4KeyForO, round), current);
            }

            o = current;
        }

        using var keyBuffer = new MemoryStream();
        keyBuffer.Write(userPad);
        keyBuffer.Write(o, 0, 32);
        keyBuffer.WriteByte((byte)(permissions & 0xFF));
        keyBuffer.WriteByte((byte)((permissions >> 8) & 0xFF));
        keyBuffer.WriteByte((byte)((permissions >> 16) & 0xFF));
        keyBuffer.WriteByte((byte)((permissions >> 24) & 0xFF));
        keyBuffer.Write(fileId);

        var fileKeyHash = MD5.HashData(keyBuffer.ToArray());
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                fileKeyHash = MD5.HashData(fileKeyHash.AsSpan(0, keyLengthBytes).ToArray());
            }
        }

        var fileKey = fileKeyHash[..keyLengthBytes];

        byte[] u;
        if (revision == 2)
        {
            u = Rc4.Transform(fileKey, Padding);
        }
        else
        {
            using var digestBuffer = new MemoryStream();
            digestBuffer.Write(Padding);
            digestBuffer.Write(fileId);
            var digest = MD5.HashData(digestBuffer.ToArray());
            var encrypted = Rc4.Transform(fileKey, digest);
            for (var round = 1; round <= 19; round++)
            {
                encrypted = Rc4.Transform(Xor(fileKey, round), encrypted);
            }

            u = new byte[32];
            encrypted.CopyTo(u, 0);
        }

        return (o, u, fileKey);
    }

    public static byte[] ObjectKey(byte[] fileKey, int number, int generation, bool aes)
    {
        var extra = new byte[aes ? 9 : 5];
        extra[0] = (byte)number;
        extra[1] = (byte)(number >> 8);
        extra[2] = (byte)(number >> 16);
        extra[3] = (byte)generation;
        extra[4] = (byte)(generation >> 8);
        if (aes)
        {
            "sAlT"u8.CopyTo(extra.AsSpan(5));
        }

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        incremental.AppendData(fileKey);
        incremental.AppendData(extra);
        var digest = incremental.GetHashAndReset();
        return digest[..Math.Min(fileKey.Length + 5, 16)];
    }

    public static byte[] EncryptAes(byte[] key, byte[] plaintext)
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

    public static (byte[] O, byte[] U, byte[] OE, byte[] UE, byte[] FileKey) BuildAes256Credentials(string userPassword, string ownerPassword, int revision)
    {
        var fileKey = RandomNumberGenerator.GetBytes(32);

        var userValidationSalt = RandomNumberGenerator.GetBytes(8);
        var userKeySalt = RandomNumberGenerator.GetBytes(8);
        var userPasswordBytes = Encoding.UTF8.GetBytes(userPassword);

        var uHash = Hash2B(userPasswordBytes, userValidationSalt, [], revision);
        var u = Concat(uHash, userValidationSalt, userKeySalt);

        var userIntermediateKey = Hash2B(userPasswordBytes, userKeySalt, [], revision);
        var ue = AesCbcNoPadding(fileKey, userIntermediateKey, new byte[16], encrypt: true);

        var ownerValidationSalt = RandomNumberGenerator.GetBytes(8);
        var ownerKeySalt = RandomNumberGenerator.GetBytes(8);
        var ownerPasswordBytes = Encoding.UTF8.GetBytes(ownerPassword);

        var oHash = Hash2B(ownerPasswordBytes, ownerValidationSalt, u, revision);
        var o = Concat(oHash, ownerValidationSalt, ownerKeySalt);

        var ownerIntermediateKey = Hash2B(ownerPasswordBytes, ownerKeySalt, u, revision);
        var oe = AesCbcNoPadding(fileKey, ownerIntermediateKey, new byte[16], encrypt: true);

        return (o, u, oe, ue, fileKey);
    }

    private static byte[] Hash2B(byte[] passwordUtf8, byte[] salt, byte[] udata, int revision)
    {
        var k = SHA256.HashData(Concat(passwordUtf8, salt, udata));
        if (revision < 6)
        {
            return k;
        }

        var round = 0;
        byte[] e;
        while (true)
        {
            var k1Source = Concat(passwordUtf8, k, udata);
            var k1 = new byte[k1Source.Length * 64];
            for (var i = 0; i < 64; i++)
            {
                k1Source.CopyTo(k1, i * k1Source.Length);
            }

            e = AesCbcNoPadding(k1, k[..16], k[16..32], encrypt: true);

            var sum = 0;
            for (var i = 0; i < 16; i++)
            {
                sum += e[i];
            }

            k = (sum % 3) switch
            {
                0 => SHA256.HashData(e),
                1 => SHA384.HashData(e),
                _ => SHA512.HashData(e),
            };

            round++;
            if (round >= 64 && e[^1] <= round - 32)
            {
                break;
            }
        }

        return k[..32];
    }

    private static byte[] AesCbcNoPadding(byte[] data, byte[] key, byte[] iv, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] Xor(byte[] key, int round)
    {
        var result = new byte[key.Length];
        for (var i = 0; i < key.Length; i++)
        {
            result[i] = (byte)(key[i] ^ round);
        }

        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] Pad(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        var result = new byte[32];
        var count = Math.Min(bytes.Length, 32);
        bytes.AsSpan(0, count).CopyTo(result);
        Padding.AsSpan(0, 32 - count).CopyTo(result.AsSpan(count));
        return result;
    }
}

public class EncryptionReadTests
{
    private static readonly byte[] FileId = Encoding.ASCII.GetBytes("0123456789ABCDEF");

    private static PdfDictionary BuildRc4EncryptDict(byte[] o, byte[] u, int permissions, int v, int r, int lengthBits)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.V, PdfNumber.Get(v));
        dict.Set(PdfName.R, PdfNumber.Get(r));
        dict.Set(PdfName.O, PdfString.FromLiteral(o));
        dict.Set(PdfName.U, PdfString.FromLiteral(u));
        dict.Set(PdfName.P, PdfNumber.Get(permissions));
        dict.Set(PdfName.Length, PdfNumber.Get(lengthBits));
        return dict;
    }

    [Fact]
    public void Rc4_EmptyUserPassword_OpensWithDefaultOptions()
    {
        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "", ownerPassword: "owner-secret", permissions: -4, FileId, keyLengthBytes: 5, revision: 2);
        var dict = BuildRc4EncryptDict(o, u, -4, v: 1, r: 2, lengthBits: 40);

        var handler = new StandardSecurityHandler(dict, FileId, PdfOptions.Default);

        var reference = new IndirectReference(7, 0);
        var objectKey = EncryptionFixtureBuilder.ObjectKey(fileKey, 7, 0, aes: false);
        var plaintext = Encoding.ASCII.GetBytes("Hello, encrypted world!");
        var ciphertext = Rc4.Transform(objectKey, plaintext);

        var decrypted = handler.DecryptString(ciphertext, reference);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Rc4_UnsignedPToken_ReadTwosComplement_EmptyUserPasswordStillOpens()
    {
        // Issue #68: ISO 32000-1 Table 22 makes /P a 32-bit SIGNED integer, but real-world
        // writers (Adobe included) serialize the same bit pattern as an unsigned decimal
        // token — 4294966996 IS -300. The key material below is derived from -300; the
        // dictionary spells it unsigned. Pre-fix, the strict int read fell back to 0,
        // Algorithm 2 got zeroed permission bytes, and the document was rejected PLUME4002.
        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "", ownerPassword: "owner-secret", permissions: -300, FileId, keyLengthBytes: 16, revision: 3);
        var dict = BuildRc4EncryptDict(o, u, permissions: -300, v: 2, r: 3, lengthBits: 128);
        dict.Set(PdfName.P, PdfNumber.Get(4294966996L)); // unchecked((uint)-300)

        var handler = new StandardSecurityHandler(dict, FileId, PdfOptions.Default);

        Assert.Equal(-300, handler.Permissions);
        var reference = new IndirectReference(7, 0);
        var objectKey = EncryptionFixtureBuilder.ObjectKey(fileKey, 7, 0, aes: false);
        var plaintext = Encoding.ASCII.GetBytes("unsigned /P, same key");
        var decrypted = handler.DecryptString(Rc4.Transform(objectKey, plaintext), reference);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Rc4_128Bit_Revision3_RoundTrips()
    {
        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "open-me", ownerPassword: "owner-secret", permissions: -44, FileId, keyLengthBytes: 16, revision: 3);
        var dict = BuildRc4EncryptDict(o, u, -44, v: 2, r: 3, lengthBits: 128);

        var options = PdfOptions.Default with { UserPassword = "open-me" };
        var handler = new StandardSecurityHandler(dict, FileId, options);

        var reference = new IndirectReference(3, 0);
        var objectKey = EncryptionFixtureBuilder.ObjectKey(fileKey, 3, 0, aes: false);
        var plaintext = Encoding.ASCII.GetBytes("128-bit RC4 stream payload.");
        var ciphertext = Rc4.Transform(objectKey, plaintext);

        Assert.Equal(plaintext, handler.DecryptStream(ciphertext, reference));
    }

    [Fact]
    public void WrongPassword_ThrowsCodedException()
    {
        var (o, u, _) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "correct-password", ownerPassword: "owner-secret", permissions: -4, FileId, keyLengthBytes: 5, revision: 2);
        var dict = BuildRc4EncryptDict(o, u, -4, v: 1, r: 2, lengthBits: 40);

        var options = PdfOptions.Default with { UserPassword = "wrong-password" };
        var ex = Assert.Throws<PlumePdfException>(() => new StandardSecurityHandler(dict, FileId, options));
        Assert.Equal("PLUME4002", ex.Code);
    }

    [Fact]
    public void OwnerPassword_RecoversUserKey_WhenUserPasswordNotSupplied()
    {
        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "required-user-pw", ownerPassword: "owner-secret", permissions: -4, FileId, keyLengthBytes: 5, revision: 3);
        var dict = BuildRc4EncryptDict(o, u, -4, v: 2, r: 3, lengthBits: 40);

        var options = PdfOptions.Default with { OwnerPassword = "owner-secret" };
        var handler = new StandardSecurityHandler(dict, FileId, options);

        var reference = new IndirectReference(1, 0);
        var objectKey = EncryptionFixtureBuilder.ObjectKey(fileKey, 1, 0, aes: false);
        var plaintext = Encoding.ASCII.GetBytes("recovered via owner password");
        var ciphertext = Rc4.Transform(objectKey, plaintext);

        Assert.Equal(plaintext, handler.DecryptStream(ciphertext, reference));
    }

    [Fact]
    public void Aes128_Revision4_RoundTrips()
    {
        var (o, u, fileKey) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "", ownerPassword: "owner-secret", permissions: -4, FileId, keyLengthBytes: 16, revision: 4);

        var cfEntry = new PdfDictionary();
        cfEntry.Set(PdfName.Get("CFM"), PdfName.Get("AESV2"));
        cfEntry.Set(PdfName.Length, PdfNumber.Get(16));

        var cf = new PdfDictionary();
        cf.Set(PdfName.Get("StdCF"), cfEntry);

        var dict = BuildRc4EncryptDict(o, u, -4, v: 4, r: 4, lengthBits: 128);
        dict.Set(PdfName.Get("CF"), cf);
        dict.Set(PdfName.Get("StmF"), PdfName.Get("StdCF"));
        dict.Set(PdfName.Get("StrF"), PdfName.Get("StdCF"));

        var handler = new StandardSecurityHandler(dict, FileId, PdfOptions.Default);

        var reference = new IndirectReference(9, 0);
        var objectKey = EncryptionFixtureBuilder.ObjectKey(fileKey, 9, 0, aes: true);
        var plaintext = Encoding.ASCII.GetBytes("AES-128 stream payload, longer than one block to exercise chaining.");
        var ciphertext = EncryptionFixtureBuilder.EncryptAes(objectKey, plaintext);

        Assert.Equal(plaintext, handler.DecryptStream(ciphertext, reference));
    }

    [Fact]
    public void EncryptString_Deterministic_DifferentRevisionSalt_ProducesDifferentCiphertext()
    {
        // SaveIncremental never renumbers an existing object, so re-writing the SAME object
        // across two successive incremental revisions calls EncryptString/EncryptStream with
        // the identical (fileKey, owner) pair both times. Before revisionSalt, DeterministicIv
        // depended on nothing else, so both revisions got the exact same IV despite different
        // plaintext — the CBC common-prefix leak the IV derivation exists to avoid, just along
        // the revision axis instead of the object axis. IncrementalUpdateWriter passes the
        // previous startxref offset as the salt; this test exercises the handler directly.
        var (o, u, _) = EncryptionFixtureBuilder.BuildRc4OrAesCredentials(userPassword: "", ownerPassword: "owner-secret", permissions: -4, FileId, keyLengthBytes: 16, revision: 4);

        var cfEntry = new PdfDictionary();
        cfEntry.Set(PdfName.Get("CFM"), PdfName.Get("AESV2"));
        cfEntry.Set(PdfName.Length, PdfNumber.Get(16));

        var cf = new PdfDictionary();
        cf.Set(PdfName.Get("StdCF"), cfEntry);

        var dict = BuildRc4EncryptDict(o, u, -4, v: 4, r: 4, lengthBits: 128);
        dict.Set(PdfName.Get("CF"), cf);
        dict.Set(PdfName.Get("StmF"), PdfName.Get("StdCF"));
        dict.Set(PdfName.Get("StrF"), PdfName.Get("StdCF"));

        var handler = new StandardSecurityHandler(dict, FileId, PdfOptions.Default);

        var owner = new IndirectReference(9, 0);
        var plaintext = Encoding.ASCII.GetBytes("Same object, same plaintext, two different appended revisions.");

        var revisionOne = handler.EncryptString(plaintext, owner, deterministic: true, revisionSalt: 1000);
        var revisionTwo = handler.EncryptString(plaintext, owner, deterministic: true, revisionSalt: 2000);
        Assert.NotEqual(revisionOne, revisionTwo);
        // The 16-byte IV prefix specifically must differ — that's the actual leak being closed.
        Assert.NotEqual(revisionOne[..16], revisionTwo[..16]);

        // Same (owner, revisionSalt) must still reproduce byte-identically — R-m's determinism
        // contract; this isn't a return to true randomness.
        var revisionOneRepeat = handler.EncryptString(plaintext, owner, deterministic: true, revisionSalt: 1000);
        Assert.Equal(revisionOne, revisionOneRepeat);
    }

    [Fact]
    public void Aes256_Revision6_EmptyUserPassword_RoundTrips()
    {
        var (o, u, oe, ue, fileKey) = EncryptionFixtureBuilder.BuildAes256Credentials(userPassword: "", ownerPassword: "owner-secret", revision: 6);

        var dict = new PdfDictionary();
        dict.Set(PdfName.V, PdfNumber.Get(5));
        dict.Set(PdfName.R, PdfNumber.Get(6));
        dict.Set(PdfName.O, PdfString.FromLiteral(o));
        dict.Set(PdfName.U, PdfString.FromLiteral(u));
        dict.Set(PdfName.Get("OE"), PdfString.FromLiteral(oe));
        dict.Set(PdfName.Get("UE"), PdfString.FromLiteral(ue));

        var handler = new StandardSecurityHandler(dict, FileId, PdfOptions.Default);

        var reference = new IndirectReference(4, 0);
        var plaintext = Encoding.ASCII.GetBytes("AES-256 (R6) stream payload.");
        var ciphertext = EncryptionFixtureBuilder.EncryptAes(fileKey, plaintext);

        Assert.Equal(plaintext, handler.DecryptStream(ciphertext, reference));
    }

    [Fact]
    public void Aes256_Revision6_OwnerPassword_Recovers()
    {
        var (o, u, oe, ue, fileKey) = EncryptionFixtureBuilder.BuildAes256Credentials(userPassword: "user-pw", ownerPassword: "owner-pw", revision: 6);

        var dict = new PdfDictionary();
        dict.Set(PdfName.V, PdfNumber.Get(5));
        dict.Set(PdfName.R, PdfNumber.Get(6));
        dict.Set(PdfName.O, PdfString.FromLiteral(o));
        dict.Set(PdfName.U, PdfString.FromLiteral(u));
        dict.Set(PdfName.Get("OE"), PdfString.FromLiteral(oe));
        dict.Set(PdfName.Get("UE"), PdfString.FromLiteral(ue));

        var options = PdfOptions.Default with { OwnerPassword = "owner-pw" };
        var handler = new StandardSecurityHandler(dict, FileId, options);

        var reference = new IndirectReference(2, 0);
        var plaintext = Encoding.ASCII.GetBytes("owner-recovered AES-256 payload.");
        var ciphertext = EncryptionFixtureBuilder.EncryptAes(fileKey, plaintext);

        Assert.Equal(plaintext, handler.DecryptStream(ciphertext, reference));
    }

    [Fact]
    public void Aes256_Revision6_WrongPassword_ThrowsCoded()
    {
        var (o, u, oe, ue, _) = EncryptionFixtureBuilder.BuildAes256Credentials(userPassword: "correct", ownerPassword: "owner-pw", revision: 6);

        var dict = new PdfDictionary();
        dict.Set(PdfName.V, PdfNumber.Get(5));
        dict.Set(PdfName.R, PdfNumber.Get(6));
        dict.Set(PdfName.O, PdfString.FromLiteral(o));
        dict.Set(PdfName.U, PdfString.FromLiteral(u));
        dict.Set(PdfName.Get("OE"), PdfString.FromLiteral(oe));
        dict.Set(PdfName.Get("UE"), PdfString.FromLiteral(ue));

        var options = PdfOptions.Default with { UserPassword = "wrong" };
        var ex = Assert.Throws<PlumePdfException>(() => new StandardSecurityHandler(dict, FileId, options));
        Assert.Equal("PLUME4002", ex.Code);
    }

    [Fact]
    public void MissingEncryptionDictionaryFields_ThrowsCoded()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.V, PdfNumber.Get(1));
        dict.Set(PdfName.R, PdfNumber.Get(2));

        var ex = Assert.Throws<PlumePdfException>(() => new StandardSecurityHandler(dict, FileId, PdfOptions.Default));
        Assert.Equal("PLUME4001", ex.Code);
    }
}

/// <summary>
/// End-to-end: opening one of the committed encrypted fixtures through the public
/// <see cref="PdfDocument.Open(string,PdfOptions?)"/> path (not <see cref="StandardSecurityHandler"/>
/// directly) must hand back real decrypted content, not still-encrypted ciphertext — the
/// regression this guards against (the handler was constructed only to authenticate the
/// password and then discarded, so every string/stream doc.Objects returned for an
/// encrypted source was raw ciphertext, with nothing recorded to doc.Diagnostics).
/// </summary>
public class EncryptionEndToEndTests
{
    private static readonly byte[] ExpectedContent = Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 50 Td (Hello, PlumePDF fixture) Tj ET");

    public static TheoryData<string> EncryptedFixtureNames() =>
    [
        "RC4-40.pdf",
        "RC4-128.pdf",
        "AES-128.pdf",
        "AES-256.pdf",
    ];

    [Theory]
    [MemberData(nameof(EncryptedFixtureNames))]
    public void Open_EncryptedFixture_ContentStreamDecryptsToRealPlaintext(string fixtureName)
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", fixtureName);
        using var document = PdfDocument.Open(path);

        Assert.True(document.HasEncryptedSource);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code.StartsWith("PLUME4", StringComparison.Ordinal));
        Assert.Single(document.Pages);

        var page = document.Pages[0];
        Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Contents"), out var contentsValue));
        var contentsRef = Assert.IsType<PdfReference>(contentsValue);
        var contentStream = Assert.IsType<PdfStream>(document.Objects[contentsRef.Target]);

        var decoded = contentStream.GetDecodedBytes(PdfFilterRegistry.Default);
        Assert.Equal(ExpectedContent, decoded);
    }

    [Theory]
    [MemberData(nameof(EncryptedFixtureNames))]
    public void Save_OnEncryptedSource_StillRefusesEvenThoughReadsNowDecrypt(string fixtureName)
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", fixtureName);
        using var document = PdfDocument.Open(path);

        var tempPath = Path.Combine(Path.GetTempPath(), $"plumepdf-encryption-e2e-{Guid.NewGuid():N}.pdf");
        var ex = Assert.Throws<PlumePdfException>(() => document.Save(tempPath));
        Assert.Equal("PLUME5001", ex.Code);
        Assert.False(File.Exists(tempPath));
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
}
