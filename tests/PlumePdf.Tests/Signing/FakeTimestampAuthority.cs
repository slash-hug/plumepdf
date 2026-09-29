using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.Objects.Signing;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// An in-process RFC 3161 timestamp authority for hermetic tests (Task B1): never opens a
/// socket, signs with its own generated TSA certificate, and answers instantly — the gating
/// lane's stand-in for <c>IO.Http.HttpTimestampAuthority</c> so PAdES B-T/B-LTA signing tests
/// never depend on a live network endpoint.
/// </summary>
public sealed class FakeTimestampAuthority : ITimestampAuthority, IDisposable
{
    private static readonly Oid TestPolicyId = new("1.2.3.4.5.6");
    private static readonly Oid TstInfoContentType = new("1.2.840.113549.1.9.16.1.4"); // id-ct-TSTInfo

    private readonly X509Certificate2 _tsaCertificate;
    private readonly X509Certificate2 _issuer;
    private long _nextSerial;

    /// <summary>The TSA's own signing certificate — signed by <paramref name="issuer"/>, so tests can chain-verify a returned token.</summary>
    public X509Certificate2 Certificate => _tsaCertificate;

    /// <summary>Builds a fake TSA whose certificate is issued by <paramref name="issuer"/> (typically <see cref="TestCertificateAuthority.Intermediate"/>).</summary>
    public FakeTimestampAuthority(X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        _issuer = issuer;
        _tsaCertificate = CreateTsaCertificate(issuer);
    }

    /// <inheritdoc/>
    public Task<byte[]> GetTimestampAsync(ReadOnlyMemory<byte> messageImprint, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hashAlgorithmOid = new Oid(hashAlgorithm.Name switch
        {
            "SHA256" => "2.16.840.1.101.3.4.2.1",
            "SHA384" => "2.16.840.1.101.3.4.2.2",
            "SHA512" => "2.16.840.1.101.3.4.2.3",
            _ => throw new NotSupportedException($"FakeTimestampAuthority does not support digest algorithm '{hashAlgorithm.Name}'."),
        });

        var serial = BitConverter.GetBytes(Interlocked.Increment(ref _nextSerial));
        var tokenInfo = new Rfc3161TimestampTokenInfo(
            TestPolicyId,
            hashAlgorithmOid,
            messageImprint,
            serial,
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            null,
            null);

        var tstInfoBytes = tokenInfo.Encode();

        // A TimeStampToken is a non-detached CMS embedding the TSTInfo as its content (RFC
        // 3161 §2.4.2) — small, fixed-size content, so SignedCms.ComputeSignature applies
        // cleanly here (unlike CmsSignatureBuilder's own PAdES signature, which signs a
        // document too large to buffer; see that type's remarks).
        var contentInfo = new ContentInfo(TstInfoContentType, tstInfoBytes);
        var signedCms = new SignedCms(contentInfo);
        var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _tsaCertificate)
        {
            // ExcludeRoot (with the issuing intermediate supplied via Certificates below, since
            // it lives in no OS trust/intermediate store the chain build could otherwise
            // discover it from) mirrors a real-world TSA that ships its own issuing chain in
            // the token — letting a relying party chain-build the TSA certificate against a
            // trust anchor it actually holds, exactly what SignatureVerifier's timestamp-trust
            // check (see TimestampTrustTests) needs to exercise.
            IncludeOption = X509IncludeOption.ExcludeRoot,
        };
        cmsSigner.Certificates.Add(_issuer);

        // RFC 3161 §2.4.2: "The certificate identifier (ESSCertID) of the TSA certificate MUST
        // be included as a signerInfo attribute inside a SigningCertificate attribute" (RFC
        // 5816 permits the v2 form used here) — System.Security.Cryptography.Pkcs's own
        // Rfc3161TimestampToken.TryDecode requires this to accept a token at all, so a fake
        // missing it would validate nothing.
        var essAttributeValue = CmsSignatureBuilder.EncodeEssSigningCertificateV2(_tsaCertificate, hashAlgorithm);
        cmsSigner.SignedAttributes.Add(new AsnEncodedData(CmsOids.SigningCertificateV2, essAttributeValue));

        signedCms.ComputeSignature(cmsSigner, silent: true);

        return Task.FromResult(signedCms.Encode());
    }

    private static X509Certificate2 CreateTsaCertificate(X509Certificate2 issuer)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=PlumePDF Fake TSA, O=PlumePDF Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        // RFC 3161 §2.3: a TSA certificate MUST carry exactly id-kp-timeStamping, marked critical.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, includeKeyIdentifier: true, includeIssuerAndSerial: false));

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2), serial);
        using var withKey = issued.CopyWithPrivateKey(key);

        // Re-import from a PFX export so the private key is independent of `key`'s lifetime —
        // see TestCertificateAuthority.Persist's remarks for why this matters.
        return TestPfx.Load(withKey.Export(X509ContentType.Pfx));
    }

    /// <inheritdoc/>
    public void Dispose() => _tsaCertificate.Dispose();
}

/// <summary>
/// The hermetic-lane sentinel for <see cref="FakeTimestampAuthority"/> (Task B1's "zero-row"
/// pattern): fails loudly if the fake TSA cannot produce a token that the BCL's own
/// <see cref="Rfc3161TimestampToken"/> accepts as valid, rather than letting every dependent
/// B-T/B-LTA test fail with an unrelated-looking error.
/// </summary>
public sealed class FakeTimestampAuthoritySentinelTests
{
    [Fact]
    public async Task GetTimestampAsync_ProducesADecodableAndVerifiableToken()
    {
        using var ca = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);

        var digest = SHA256.HashData("hello, timestamp"u8.ToArray());
        var tokenBytes = await tsa.GetTimestampAsync(digest, HashAlgorithmName.SHA256);

        Assert.NotEmpty(tokenBytes);
        Assert.True(Rfc3161TimestampToken.TryDecode(tokenBytes, out var token, out _));
        Assert.NotNull(token);

        var candidates = new X509Certificate2Collection(tsa.Certificate);
        Assert.True(token!.VerifySignatureForHash(digest, HashAlgorithmName.SHA256, out var signerCertificate, candidates));
        Assert.Equal(tsa.Certificate.Thumbprint, signerCertificate!.Thumbprint);
    }
}
