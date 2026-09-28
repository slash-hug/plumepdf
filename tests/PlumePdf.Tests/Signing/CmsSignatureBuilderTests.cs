using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using PlumePdf.Objects.Signing;
using Xunit;

namespace PlumePdf.Tests.Signing;

public sealed class CmsSignatureBuilderTests
{
    private static readonly byte[] SampleContent = "The quick brown fox jumps over the lazy dog"u8.ToArray();

    [Fact]
    public async Task BuildAsync_Rsa_LevelB_RoundTripsThroughSignedCms()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);
        var signer = new CertificateSigner(ca.LeafRsa, additionalCertificates: [ca.Intermediate]);

        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);

        // The BCL's own SignedCms — not this codebase's reader — is the referee here: if it
        // accepts the structure and the signature checks out against the real content, the
        // hand-assembled CMS is RFC 5652-conformant.
        var decoded = new SignedCms(new ContentInfo(SampleContent), detached: true);
        decoded.Decode(cms);
        decoded.CheckSignature(verifySignatureOnly: true);

        Assert.Single(decoded.SignerInfos);
        Assert.NotNull(decoded.SignerInfos[0].Certificate);
        Assert.Equal(ca.LeafRsa.Thumbprint, decoded.SignerInfos[0].Certificate!.Thumbprint);
        Assert.Equal(2, decoded.Certificates.Count); // leaf + the one additional (intermediate) certificate.
    }

    [Fact]
    public async Task BuildAsync_Ecdsa_LevelB_RoundTripsThroughSignedCms()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);
        var signer = new CertificateSigner(ca.LeafEcdsa);

        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);

        var decoded = new SignedCms(new ContentInfo(SampleContent), detached: true);
        decoded.Decode(cms);
        decoded.CheckSignature(verifySignatureOnly: true);

        Assert.Equal(ca.LeafEcdsa.Thumbprint, decoded.SignerInfos[0].Certificate!.Thumbprint);
    }

    [Fact]
    public async Task BuildAsync_Output_IsAlreadyDefiniteLengthDer()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);
        var signer = new CertificateSigner(ca.LeafRsa);

        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);

        // Idempotent under DerNormalizer.Normalize is exactly what "already definite-length
        // DER" means for a structure this normalizer round-trips faithfully.
        var normalizedAgain = DerNormalizer.Normalize(cms);
        Assert.Equal(cms, normalizedAgain);

        // The authoritative check (Task B3: "assert definite-length encoding on all
        // platforms"): re-parsing under strict DER rules throws on any indefinite length or
        // chunked-primitive encoding anywhere in the tree, on every OS this test runs on.
        AssertStrictDer(cms);
    }

    [Fact]
    public async Task BuildAsync_LevelT_EmbedsATimestampTokenAndReaderExposesIt()
    {
        using var ca = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);
        var digest = SHA256.HashData(SampleContent);
        var signer = new CertificateSigner(ca.LeafRsa);

        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.T, tsa);

        var result = CmsSignatureReader.Verify(cms, digest, HashAlgorithmName.SHA256);

        Assert.Equal(CmsVerificationOutcome.Valid, result.Outcome);
        Assert.NotNull(result.Timestamp);
    }

    [Fact]
    public async Task BuildAsync_LevelT_WithoutTimestampAuthority_Throws()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);
        var signer = new CertificateSigner(ca.LeafRsa);

        var ex = await Assert.ThrowsAsync<PlumePdfException>(() =>
            CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.T));

        Assert.Equal("PLUME4012", ex.Code);
    }

    [Fact]
    public async Task Reader_Verify_RoundTripsToValid()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);
        var signer = new CertificateSigner(ca.LeafRsa);

        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);
        var result = CmsSignatureReader.Verify(cms, digest, HashAlgorithmName.SHA256);

        Assert.True(result.IsValid);
        Assert.Equal(ca.LeafRsa.Thumbprint, result.SigningCertificate!.Thumbprint);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Reader_Verify_DetectsDigestMismatch()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);
        var wrongDigest = SHA256.HashData("a different document entirely"u8.ToArray());
        var signer = new CertificateSigner(ca.LeafRsa);

        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);
        var result = CmsSignatureReader.Verify(cms, wrongDigest, HashAlgorithmName.SHA256);

        Assert.Equal(CmsVerificationOutcome.DigestMismatch, result.Outcome);
    }

    [Fact]
    public async Task Reader_Verify_DetectsTamperedSignatureBytes()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);
        var signer = new CertificateSigner(ca.LeafRsa);

        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);
        var tampered = (byte[])cms.Clone();
        tampered[^1] ^= 0xFF; // flip the last byte — inside the signature octet string for this structure.

        var result = CmsSignatureReader.Verify(tampered, digest, HashAlgorithmName.SHA256);

        Assert.True(result.Outcome is CmsVerificationOutcome.SignatureInvalid or CmsVerificationOutcome.Malformed);
    }

    [Fact]
    public void Reader_Verify_MalformedBytes_ReturnsMalformedRatherThanThrowing()
    {
        var result = CmsSignatureReader.Verify([1, 2, 3, 4], SHA256.HashData([]), HashAlgorithmName.SHA256);

        Assert.Equal(CmsVerificationOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public async Task Signer_UnsupportedKeyAlgorithm_ThrowsCoded()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData(SampleContent);

        // A certificate with no private key at all cannot even become a CertificateSigner.
        using var publicOnly = new System.Security.Cryptography.X509Certificates.X509Certificate2(ca.LeafRsa.RawData);
        var ex = Assert.Throws<PlumePdfException>(() => new CertificateSigner(publicOnly));
        Assert.Equal("PLUME4007", ex.Code);
        await Task.CompletedTask;
    }

    /// <summary>Re-parses <paramref name="der"/> under strict DER rules, recursively, which throws on any indefinite length or chunked-primitive encoding anywhere in the tree.</summary>
    private static void AssertStrictDer(ReadOnlyMemory<byte> der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        WalkAllValues(reader);
        Assert.False(reader.HasData);
    }

    private static void WalkAllValues(AsnReader reader)
    {
        while (reader.HasData)
        {
            var tag = reader.PeekTag();
            if (tag.IsConstructed)
            {
                // AsnReader refuses a UNIVERSAL-class tag that doesn't match the method's own
                // built-in tag number — ReadSequence throws for a UNIVERSAL SET, so that one
                // case reads via ReadSetOf instead (same restriction DerNormalizer hits).
                var inner = tag.TagClass == TagClass.Universal && (UniversalTagNumber)tag.TagValue == UniversalTagNumber.SetOf
                    ? reader.ReadSetOf(tag)
                    : reader.ReadSequence(tag);
                WalkAllValues(inner);
            }
            else
            {
                reader.ReadEncodedValue();
            }
        }
    }
}
