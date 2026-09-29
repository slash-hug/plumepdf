using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.Objects.Signing;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// Regression coverage for a critical bug against
/// <c>Documents.Signing.SignatureVerifier</c>/<c>Objects.Signing.CmsSignatureReader</c>: an
/// RFC 3161 signature-timestamp lives in a CMS's UNSIGNED attributes (RFC 5652 §11) — entirely
/// outside the signature's own cryptographic protection — so its claimed time must never be
/// trusted as "proven time" (and in particular must never drive certificate time-validity
/// during chain building) unless both (a) the token's own signature verifies against the
/// signature bytes it timestamps, and (b) its TSA certificate itself chain-builds to a trust
/// anchor the caller actually supplied.
/// </summary>
public class TimestampTrustTests
{
    [Fact]
    public async Task Verify_SplicedUnsignedTimestampFromUntrustedTsa_DecodesAndSelfVerifiesButProvesNoTrust()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData("splice attack payload"u8.ToArray());
        var signer = new CertificateSigner(ca.LeafRsa);
        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);

        // Splice an attacker-controlled RFC 3161 signature-timestamp into the CMS's UNSIGNED
        // attributes after the fact — exactly the attack CmsSignatureReader.cs's finding
        // describes. This requires no forgery of ca.LeafRsa's own signature at all: unsigned
        // attributes sit outside SignerInfo's own signed-attrs digest (RFC 5652 §5.4), so
        // SignerInfo.AddUnsignedAttribute leaves the original signature untouched.
        var decoded = new SignedCms();
        decoded.Decode(cms);
        var signerInfo = decoded.SignerInfos[0];

        using var attackerKey = RSA.Create(2048);
        var attackerRequest = new CertificateRequest("CN=Attacker-Controlled Fake TSA, O=Not A Real TSA", attackerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        attackerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        attackerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        attackerRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], critical: true)); // RFC 3161 §2.3: id-kp-timeStamping, critical.
        attackerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(attackerRequest.PublicKey, critical: false));
        using var attackerCertEphemeral = attackerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var attackerCert = TestPfx.Load(attackerCertEphemeral.Export(X509ContentType.Pfx));

        var messageImprint = SHA256.HashData(signerInfo.GetSignature());
        var tokenInfo = new Rfc3161TimestampTokenInfo(
            new Oid("1.2.3.4.5.6"),
            new Oid("2.16.840.1.101.3.4.2.1"),
            messageImprint,
            new byte[] { 1 },
            DateTimeOffset.UtcNow.AddSeconds(-5), // attacker-chosen genTime — a value the attacker picks, not "now".
            null,
            false,
            null,
            null,
            null);

        var tokenContentInfo = new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), tokenInfo.Encode());
        var tokenCms = new SignedCms(tokenContentInfo);
        var tokenCmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, attackerCert) { IncludeOption = X509IncludeOption.EndCertOnly };
        tokenCmsSigner.SignedAttributes.Add(new AsnEncodedData(CmsOids.SigningCertificateV2, CmsSignatureBuilder.EncodeEssSigningCertificateV2(attackerCert, HashAlgorithmName.SHA256)));
        tokenCms.ComputeSignature(tokenCmsSigner, silent: true);

        // DerNormalizer.Normalize matches what CmsSignatureBuilder itself applies to every TSA
        // token before embedding it: SignedCms.ComputeSignature can produce
        // indefinite-length BER/CER on some platforms (notably macOS's native crypto backend),
        // which Rfc3161TimestampToken.TryDecode rejects.
        var normalizedTokenBytes = DerNormalizer.Normalize(tokenCms.Encode());
        signerInfo.AddUnsignedAttribute(new AsnEncodedData(CmsOids.SignatureTimeStampToken, normalizedTokenBytes));
        var splicedCms = decoded.Encode();

        var result = CmsSignatureReader.Verify(splicedCms, digest, HashAlgorithmName.SHA256);

        // The document's own signature is completely untouched and still verifies...
        Assert.Equal(CmsVerificationOutcome.Valid, result.Outcome);
        Assert.NotNull(result.Timestamp);

        // ...and the spliced token's OWN signature verifies too — it really was produced by the
        // attacker's own key over the right bytes. This is exactly why "the token decodes and
        // its signature verifies" can never, by itself, be treated as proof of trustworthy time:
        // it proves only that *someone* produced it, not that they are a trusted TSA.
        Assert.NotNull(result.TimestampVerification);
        Assert.True(result.TimestampVerification!.SignatureValid);
        Assert.Equal(attackerCert.Thumbprint, result.TimestampVerification.SigningCertificate?.Thumbprint);
    }

    [Fact]
    public async Task Verify_TsaCertificateChainsToSuppliedTrustAnchor_MarksTimestampTrusted()
    {
        using var ca = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);

            using (var document = PdfDocument.Open(sourcePath))
            {
                await document.Signatures.SignAsync(signedPath, new PdfSignOptions
                {
                    Certificate = ca.LeafRsa,
                    Level = PdfSignatureLevel.T,
                    TimestampAuthority = tsa,
                });
            }

            using var signed = PdfDocument.Open(signedPath);
            // FakeTimestampAuthority embeds its own issuing chain (minus root) in the token, so
            // trusting ca.Root alone is enough for the TSA certificate to chain-build all the
            // way — proving IsTimestampTrusted actually turns true once both legs (a verified
            // token signature AND a trusted TSA chain) hold, not just decode success.
            var result = signed.Signatures[0].Verify(trustedRoots: [ca.Root]);

            Assert.True(result.HasTimestamp);
            Assert.True(result.IsTimestampTrusted);
            Assert.NotNull(result.TimestampTime);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }

    [Fact]
    public async Task Verify_TsaCertificateDoesNotChainToSuppliedTrustAnchors_LeavesTimestampUntrusted()
    {
        using var ca = new TestCertificateAuthority();
        using var unrelatedCa = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);

            using (var document = PdfDocument.Open(sourcePath))
            {
                await document.Signatures.SignAsync(signedPath, new PdfSignOptions
                {
                    Certificate = ca.LeafRsa,
                    Level = PdfSignatureLevel.T,
                    TimestampAuthority = tsa,
                });
            }

            using var signed = PdfDocument.Open(signedPath);
            // trustedRoots names a CA entirely unrelated to the TSA that actually produced this
            // token. The token still decodes and its own signature still verifies (HasTimestamp
            // stays true, matching a real client that has no reason to doubt the token's
            // internal consistency) — but IsTimestampTrusted must stay false: a signature-valid
            // but untrusted TSA certificate is never proof of the claimed time.
            var result = signed.Signatures[0].Verify(trustedRoots: [unrelatedCa.Root]);

            Assert.True(result.HasTimestamp);
            Assert.False(result.IsTimestampTrusted);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }

    [Fact]
    public void Verify_NoTrustAnchorsSupplied_NeverMarksTimestampTrusted()
    {
        // SignatureVerificationResult.IsTimestampTrusted's own contract (see its <summary>):
        // always false when no trust anchors were supplied, matching ChainStatus's own
        // opt-in-only stance (never falls back to the OS trust store).
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            using var signed = PdfDocument.Open(signedPath);
            var result = signed.Signatures[0].Verify();

            Assert.Equal(SignatureChainStatus.NotEvaluated, result.ChainStatus);
            Assert.False(result.IsTimestampTrusted);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }
}
