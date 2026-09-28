using System.Security.Cryptography;
using System.Text;

namespace PlumePdf.Objects;

/// <summary>
/// The seam a document's decryption goes through (the Crypto cross-cutting service is
/// real from Phase 1, unlike Fonts). <see cref="StandardSecurityHandler"/> is the only
/// implementation Phase 1 ships — the ISO 32000-1 §7.6 "Standard Security Handler" covers
/// essentially every encrypted PDF encountered in practice.
/// </summary>
internal interface ISecurityHandler
{
    /// <summary>Decrypts a string literal's bytes belonging to indirect object <paramref name="owner"/>.</summary>
    byte[] DecryptString(ReadOnlySpan<byte> data, IndirectReference owner);

    /// <summary>Decrypts a stream's raw payload bytes belonging to indirect object <paramref name="owner"/>.</summary>
    byte[] DecryptStream(ReadOnlySpan<byte> data, IndirectReference owner);
}

internal enum EncryptionAlgorithm
{
    Rc4,
    Aes128,
    Aes256,
}

/// <summary>
/// Implements the PDF Standard Security Handler (ISO 32000-1 §7.6.3): RC4-40/128 (revisions
/// 2-4), AES-128 via <c>System.Security.Cryptography.Aes</c> (revision 4, <c>/AESV2</c>
/// crypt filter), and AES-256 (revision 6, <c>/AESV3</c>). Authenticates against
/// <see cref="PdfOptions.UserPassword"/> first, falling back to
/// <see cref="PdfOptions.OwnerPassword"/> (recovering the user password from <c>/O</c> per
/// Algorithm 7); both default to the empty password, which is how most permission-restricted
/// (but not "requires a password to open") documents are protected. A password that
/// authenticates neither way throws a coded <c>PLUME4xxx</c> exception at construction —
/// decryption never silently produces garbage.
/// </summary>
/// <remarks>
/// The revision-6 (AES-256) key derivation (ISO 32000-2 §7.6.4.3.4's "hardened hash",
/// Algorithm 2.B) is implemented from the algorithm's shape as widely documented in public,
/// independent technical write-ups — never from ISO 32000-2's restricted text itself
/// (per the clean-room policy in AGENTS.md; cited here by section number only). It is
/// validated by this codebase's own round-trip tests (encrypt-then-decrypt agreement)
/// rather than against an external reference-encrypted corpus, which Phase 1 does not have
/// license to redistribute; treat it as less battle-tested than the R2-R4 path until it has
/// been exercised against real Acrobat/qpdf-produced AES-256 files.
/// </remarks>
internal sealed class StandardSecurityHandler : ISecurityHandler
{
    private static readonly byte[] PaddingString =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41,
        0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80,
        0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private readonly byte[] _fileKey;
    private readonly EncryptionAlgorithm _algorithm;

    /// <summary>
    /// The raw <c>/P</c> permission bits (ISO 32000-1 §7.6.3.2, Table 22) — surfaced publicly
    /// as <c>PdfPermissions</c> via <c>PdfDocument.Permissions</c>.
    /// </summary>
    internal int Permissions { get; }

    /// <summary>
    /// The encryption dictionary's own <c>/EncryptMetadata</c> flag (default
    /// <see langword="true"/> when absent) — a <c>/Type /Metadata</c> stream is exempt from
    /// encryption entirely (ISO 32000-1 §7.6.2) when this is <see langword="false"/>, both when
    /// decrypting (<see cref="ObjectResolver"/>) and, when
    /// re-encrypting an appended revision (<see cref="EncryptStream"/> callers check this
    /// directly, since encryption exemption is a property of the object being written, not of
    /// this handler's own transform).
    /// </summary>
    internal bool EncryptMetadata { get; }

    /// <summary>Authenticates against <paramref name="options"/>' passwords and derives the file encryption key.</summary>
    /// <param name="encryptDict">The trailer's <c>/Encrypt</c> dictionary.</param>
    /// <param name="fileId">The first element of the trailer's <c>/ID</c> array, or an empty array if absent.</param>
    /// <param name="options">Supplies <see cref="PdfOptions.UserPassword"/>/<see cref="PdfOptions.OwnerPassword"/>.</param>
    /// <exception cref="PlumePdfException">The encryption dictionary is malformed, or no supplied password authenticates.</exception>
    public StandardSecurityHandler(PdfDictionary encryptDict, byte[]? fileId, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(encryptDict);
        ArgumentNullException.ThrowIfNull(options);

        var v = GetInt(encryptDict, PdfName.V) ?? 0;
        var r = GetInt(encryptDict, PdfName.R) ?? 2;
        var lengthBits = GetInt(encryptDict, PdfName.Length) ?? 40;
        var permissions = GetPermissions(encryptDict) ?? 0;
        Permissions = permissions;
        var encryptMetadata = GetBool(encryptDict, "EncryptMetadata") ?? true;
        EncryptMetadata = encryptMetadata;

        var o = GetStringBytes(encryptDict, PdfName.O) ?? throw new PlumePdfException("PLUME4001", "Encryption dictionary is missing /O.");
        var u = GetStringBytes(encryptDict, PdfName.U) ?? throw new PlumePdfException("PLUME4001", "Encryption dictionary is missing /U.");

        _algorithm = DetermineAlgorithm(v, encryptDict);

        _fileKey = _algorithm == EncryptionAlgorithm.Aes256
            ? ComputeFileKeyR6(o, u, encryptDict, options, r)
            : ComputeFileKeyR2ToR4(o, u, permissions, fileId ?? [], v == 1 ? 5 : Math.Clamp(lengthBits / 8, 5, 16), r, encryptMetadata, options);
    }

    /// <inheritdoc/>
    public byte[] DecryptString(ReadOnlySpan<byte> data, IndirectReference owner) => Decrypt(data, owner);

    /// <inheritdoc/>
    public byte[] DecryptStream(ReadOnlySpan<byte> data, IndirectReference owner) => Decrypt(data, owner);

    private byte[] Decrypt(ReadOnlySpan<byte> data, IndirectReference owner) => _algorithm switch
    {
        EncryptionAlgorithm.Rc4 => Rc4.Transform(DeriveObjectKey(owner, aes: false), data),
        EncryptionAlgorithm.Aes128 => DecryptAes(data, DeriveObjectKey(owner, aes: true)),
        EncryptionAlgorithm.Aes256 => DecryptAes(data, _fileKey),
        _ => data.ToArray(),
    };

    /// <summary>
    /// Encrypts a string literal's plaintext bytes for a newly-appended or freshly-registered
    /// indirect object <paramref name="owner"/> — narrow re-encryption for
    /// <c>PdfDocument.SaveIncremental</c>, reusing this handler's already-derived file key
    /// rather than deriving a new one. Uses the same per-object key derivation (§7.6.2
    /// Algorithm 1) <see cref="DecryptString"/> uses, keyed by the object's own (possibly
    /// freshly-allocated) <c>(Number, Generation)</c>.
    /// </summary>
    /// <param name="data">The plaintext bytes to encrypt.</param>
    /// <param name="owner">The indirect object these bytes belong to.</param>
    /// <param name="deterministic">
    /// When <see langword="true"/> (<see cref="PdfOptions.Deterministic"/>), an AES IV is
    /// derived deterministically from the file key, <paramref name="owner"/>, and
    /// <paramref name="revisionSalt"/> instead of drawn from a cryptographically random
    /// source, so repeated runs over the same input produce byte-identical ciphertext —
    /// the same trade-off <see cref="DeterministicContext"/> already makes for the trailer's
    /// <c>/ID</c>. Has no effect for RC4, which carries no IV.
    /// </param>
    /// <param name="revisionSalt">
    /// A value unique to the incremental revision being appended (<c>IncrementalUpdateWriter</c>
    /// passes the previous revision's <c>startxref</c> offset) — mixed into the deterministic
    /// IV derivation so the same object re-written across successive <c>SaveIncremental</c>
    /// calls gets a distinct IV per revision instead of reusing the one keyed only by
    /// <paramref name="owner"/>'s stable <c>(Number, Generation)</c>, which would otherwise
    /// leak a CBC common-prefix between the two revisions' ciphertexts whenever their
    /// plaintexts share one. Irrelevant (and ignored) for a non-deterministic write, since a
    /// fresh random IV never repeats regardless.
    /// </param>
    public byte[] EncryptString(ReadOnlySpan<byte> data, IndirectReference owner, bool deterministic, long revisionSalt = 0) => Encrypt(data, owner, deterministic, revisionSalt);

    /// <summary>Encrypts a stream's raw payload bytes for a newly-appended or freshly-registered indirect object <paramref name="owner"/>. See <see cref="EncryptString"/>'s remarks — identical transform, different payload kind.</summary>
    public byte[] EncryptStream(ReadOnlySpan<byte> data, IndirectReference owner, bool deterministic, long revisionSalt = 0) => Encrypt(data, owner, deterministic, revisionSalt);

    private byte[] Encrypt(ReadOnlySpan<byte> data, IndirectReference owner, bool deterministic, long revisionSalt) => _algorithm switch
    {
        // RC4 is its own inverse (a symmetric stream cipher) — the exact same transform
        // DecryptString/DecryptStream use above.
        EncryptionAlgorithm.Rc4 => Rc4.Transform(DeriveObjectKey(owner, aes: false), data),
        EncryptionAlgorithm.Aes128 => EncryptAes(data, DeriveObjectKey(owner, aes: true), owner, deterministic, revisionSalt),
        EncryptionAlgorithm.Aes256 => EncryptAes(data, _fileKey, owner, deterministic, revisionSalt),
        _ => data.ToArray(),
    };

    private static byte[] EncryptAes(ReadOnlySpan<byte> data, byte[] key, IndirectReference owner, bool deterministic, long revisionSalt)
    {
        var iv = deterministic ? DeterministicIv(key, owner, revisionSalt, data) : RandomNumberGenerator.GetBytes(16);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(data.ToArray(), 0, data.Length);

        var result = new byte[16 + ciphertext.Length];
        iv.CopyTo(result, 0);
        ciphertext.CopyTo(result, 16);
        return result;
    }

    // Deterministic mode (PdfOptions.Deterministic, R-m) trades true randomness for
    // byte-identical repeated runs. A single fixed IV reused for every value would leak that
    // two ciphertexts share a common prefix whenever their plaintexts do (CBC's well-known
    // fixed-IV weakness), so instead the IV is derived from the key together with the specific
    // object it belongs to — distinct objects (and a reused number's distinct generation) never
    // share an IV, while the same (key, owner) pair still reproduces the same IV run to run.
    // revisionSalt closes the remaining axis: SaveIncremental never renumbers an existing
    // object, so the *same* owner re-written across successive incremental revisions would
    // otherwise get the identical IV every time despite different plaintext — the same
    // fixed-IV leak, just along the revision axis instead of the object axis. Mixing in a
    // value unique per appended revision (the caller passes the previous startxref offset)
    // fixes that while staying reproducible: the same document history always chains through
    // the same sequence of offsets.
    // …and mixing the plaintext's own hash closes the remaining prefix-leak axis: two fills
    // of the SAME source (same owner, same revision salt) with different values would
    // otherwise share an IV, letting an observer compare ciphertexts for a common prefix.
    // Same plaintext still gives the same IV — determinism intact.
    private static byte[] DeterministicIv(byte[] key, IndirectReference owner, long revisionSalt, ReadOnlySpan<byte> plaintext)
    {
        var ownerBytes = new byte[16];
        BitConverter.TryWriteBytes(ownerBytes.AsSpan(0, 4), owner.Number);
        BitConverter.TryWriteBytes(ownerBytes.AsSpan(4, 4), owner.Generation);
        BitConverter.TryWriteBytes(ownerBytes.AsSpan(8, 8), revisionSalt);
        return MD5.HashData(Concat(Concat(key, ownerBytes), MD5.HashData(plaintext)))[..16];
    }

    private byte[] DeriveObjectKey(IndirectReference owner, bool aes)
    {
        // Algorithm 1 (ISO 32000-1 §7.6.2): MD5(fileKey + 3-byte LE object number +
        // 2-byte LE generation [+ "sAlT" for an AES crypt filter]), truncated to
        // min(fileKey.Length + 5, 16) bytes.
        var extra = new byte[aes ? 9 : 5];
        extra[0] = (byte)owner.Number;
        extra[1] = (byte)(owner.Number >> 8);
        extra[2] = (byte)(owner.Number >> 16);
        extra[3] = (byte)owner.Generation;
        extra[4] = (byte)(owner.Generation >> 8);
        if (aes)
        {
            "sAlT"u8.CopyTo(extra.AsSpan(5));
        }

        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        incremental.AppendData(_fileKey);
        incremental.AppendData(extra);
        var digest = incremental.GetHashAndReset();

        var keyLength = Math.Min(_fileKey.Length + 5, 16);
        return digest[..keyLength];
    }

    private static byte[] DecryptAes(ReadOnlySpan<byte> data, byte[] key)
    {
        if (data.Length < 16)
        {
            throw new PlumePdfException("PLUME4004", "AES-encrypted data is shorter than its required 16-byte IV prefix.");
        }

        var iv = data[..16].ToArray();
        var ciphertext = data[16..].ToArray();
        if (ciphertext.Length == 0)
        {
            return [];
        }

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();

        try
        {
            return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }
        catch (CryptographicException ex)
        {
            throw new PlumePdfException("PLUME4005", "AES decryption failed - the data is corrupt, or the wrong key was used.", ex);
        }
    }

    private static EncryptionAlgorithm DetermineAlgorithm(int v, PdfDictionary dict)
    {
        if (v == 5)
        {
            return EncryptionAlgorithm.Aes256;
        }

        if (v == 4
            && dict.TryGetValue(PdfName.Get("CF"), out var cfValue) && cfValue is PdfDictionary cf
            && dict.TryGetValue(PdfName.Get("StmF"), out var stmFValue) && stmFValue is PdfName stmF
            && cf.TryGetValue(stmF, out var filterDictValue) && filterDictValue is PdfDictionary filterDict
            && filterDict.TryGetValue(PdfName.Get("CFM"), out var cfmValue) && cfmValue is PdfName cfm)
        {
            return cfm.Value switch
            {
                "AESV2" => EncryptionAlgorithm.Aes128,
                "AESV3" => EncryptionAlgorithm.Aes256,
                _ => EncryptionAlgorithm.Rc4,
            };
        }

        return EncryptionAlgorithm.Rc4;
    }

    // --- Revisions 2-4 (RC4 / AES-128): Algorithms 2, 6, and 7 ---

    private static byte[] ComputeFileKeyR2ToR4(byte[] o, byte[] u, int permissions, byte[] fileId, int keyLengthBytes, int revision, bool encryptMetadata, PdfOptions options)
    {
        var userPad = PadPassword(options.UserPassword ?? string.Empty);
        var candidateKey = DeriveKey(userPad, o, permissions, fileId, keyLengthBytes, revision, encryptMetadata);
        if (AuthenticatesAsUser(candidateKey, u, fileId, revision))
        {
            return candidateKey;
        }

        if (options.OwnerPassword is { } ownerPassword)
        {
            var recoveredUserPad = RecoverUserPasswordFromOwner(ownerPassword, o, keyLengthBytes, revision);
            var ownerDerivedKey = DeriveKey(recoveredUserPad, o, permissions, fileId, keyLengthBytes, revision, encryptMetadata);
            if (AuthenticatesAsUser(ownerDerivedKey, u, fileId, revision))
            {
                return ownerDerivedKey;
            }
        }

        throw new PlumePdfException("PLUME4002", "Neither the supplied user password nor owner password opens this encrypted document.");
    }

    private static byte[] DeriveKey(byte[] paddedPassword, byte[] o, int permissions, byte[] fileId, int keyLengthBytes, int revision, bool encryptMetadata)
    {
        using var buffer = new MemoryStream();
        buffer.Write(paddedPassword);
        buffer.Write(o, 0, Math.Min(32, o.Length));
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
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash.AsSpan(0, keyLengthBytes));
            }
        }

        return hash[..keyLengthBytes];
    }

    private static bool AuthenticatesAsUser(byte[] fileKey, byte[] u, byte[] fileId, int revision)
    {
        if (revision == 2)
        {
            var expected = Rc4.Transform(fileKey, PaddingString);
            return u.Length >= 32 && expected.AsSpan().SequenceEqual(u.AsSpan(0, 32));
        }

        using var buffer = new MemoryStream();
        buffer.Write(PaddingString);
        buffer.Write(fileId);
        var digest = MD5.HashData(buffer.ToArray());

        var encrypted = Rc4.Transform(fileKey, digest);
        for (var round = 1; round <= 19; round++)
        {
            var roundKey = new byte[fileKey.Length];
            for (var i = 0; i < fileKey.Length; i++)
            {
                roundKey[i] = (byte)(fileKey[i] ^ round);
            }

            encrypted = Rc4.Transform(roundKey, encrypted);
        }

        return u.Length >= 16 && encrypted.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
    }

    private static byte[] RecoverUserPasswordFromOwner(string ownerPassword, byte[] o, int keyLengthBytes, int revision)
    {
        var hash = MD5.HashData(PadPassword(ownerPassword));
        if (revision >= 3)
        {
            for (var i = 0; i < 50; i++)
            {
                hash = MD5.HashData(hash);
            }
        }

        var rc4Key = hash[..keyLengthBytes];

        if (revision == 2)
        {
            return Rc4.Transform(rc4Key, o);
        }

        var current = o;
        for (var round = 19; round >= 1; round--)
        {
            var roundKey = new byte[rc4Key.Length];
            for (var i = 0; i < rc4Key.Length; i++)
            {
                roundKey[i] = (byte)(rc4Key[i] ^ round);
            }

            current = Rc4.Transform(roundKey, current);
        }

        return Rc4.Transform(rc4Key, current);
    }

    private static byte[] PadPassword(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        var result = new byte[32];
        var count = Math.Min(bytes.Length, 32);
        bytes.AsSpan(0, count).CopyTo(result);
        PaddingString.AsSpan(0, 32 - count).CopyTo(result.AsSpan(count));
        return result;
    }

    // --- Revision 6 (AES-256): Algorithms 2.A / 2.B ---

    private static byte[] ComputeFileKeyR6(byte[] o, byte[] u, PdfDictionary dict, PdfOptions options, int revision)
    {
        if (o.Length < 48 || u.Length < 48)
        {
            throw new PlumePdfException("PLUME4003", "AES-256 encryption dictionary's /O or /U is shorter than the required 48 bytes.");
        }

        var oe = GetStringBytes(dict, PdfName.Get("OE"));
        var ue = GetStringBytes(dict, PdfName.Get("UE"));
        if (oe is null || ue is null)
        {
            throw new PlumePdfException("PLUME4003", "AES-256 encryption dictionary is missing /OE or /UE.");
        }

        var userPasswordBytes = Encoding.UTF8.GetBytes(TruncateForR6(options.UserPassword ?? string.Empty));
        var userValidationSalt = u[32..40];
        var userKeySalt = u[40..48];

        if (Hash2B(userPasswordBytes, userValidationSalt, [], revision).AsSpan().SequenceEqual(u.AsSpan(0, 32)))
        {
            var intermediateKey = Hash2B(userPasswordBytes, userKeySalt, [], revision);
            return AesCbcTransform(ue, intermediateKey, new byte[16], encrypt: false);
        }

        if (options.OwnerPassword is { } ownerPassword)
        {
            var ownerPasswordBytes = Encoding.UTF8.GetBytes(TruncateForR6(ownerPassword));
            var ownerValidationSalt = o[32..40];
            var ownerKeySalt = o[40..48];
            var fullU = u[..48];

            if (Hash2B(ownerPasswordBytes, ownerValidationSalt, fullU, revision).AsSpan().SequenceEqual(o.AsSpan(0, 32)))
            {
                var intermediateKey = Hash2B(ownerPasswordBytes, ownerKeySalt, fullU, revision);
                return AesCbcTransform(oe, intermediateKey, new byte[16], encrypt: false);
            }
        }

        throw new PlumePdfException("PLUME4002", "Neither the supplied user password nor owner password opens this AES-256 encrypted document.");
    }

    private static string TruncateForR6(string password) => password.Length > 127 ? password[..127] : password;

    private static byte[] Hash2B(byte[] passwordUtf8, byte[] salt, byte[] udata, int revision)
    {
        var k = SHA256.HashData(Concat(passwordUtf8, salt, udata));

        if (revision < 6)
        {
            return k; // revision 5 (deprecated): plain SHA-256, no hardening rounds
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

            e = AesCbcTransform(k1, k[..16], k[16..32], encrypt: true);

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

    private static byte[] AesCbcTransform(byte[] data, byte[] key, byte[] iv, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
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

    private static int? GetInt(PdfDictionary dict, PdfName key) =>
        dict.TryGetValue(key, out var value) && value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var converted) ? converted : null;

    /// <summary>
    /// Reads <c>/P</c> with two's-complement wrap of the low 32 bits. ISO 32000-1 Table 22
    /// says <c>/P</c> is a 32-bit SIGNED integer, but several real-world writers (Adobe
    /// products included) serialize the same bit pattern as an UNSIGNED decimal token —
    /// <c>4294966996</c> and <c>-300</c> are the same permissions. The strict
    /// <see cref="GetInt"/> read returned <see langword="null"/> for the unsigned spelling,
    /// so the <c>?? 0</c> fallback fed Algorithm 2 zeroed permission bytes, the derived file
    /// key was wrong, and an otherwise-readable empty-user-password document was rejected
    /// with PLUME4002.
    /// </summary>
    private static int? GetPermissions(PdfDictionary dict) =>
        dict.TryGetValue(PdfName.P, out var value) && value is PdfNumber { IsInteger: true } number && number.TryToInt64(out var wide)
            ? unchecked((int)wide)
            : null;

    private static bool? GetBool(PdfDictionary dict, string key) =>
        dict.TryGetValue(PdfName.Get(key), out var value) && value is PdfBoolean b ? b.Value : null;

    private static byte[]? GetStringBytes(PdfDictionary dict, PdfName key) =>
        dict.TryGetValue(key, out var value) && value is PdfString s ? s.Bytes.ToArray() : null;
}
