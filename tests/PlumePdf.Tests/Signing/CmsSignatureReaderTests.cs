using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.Objects.Signing;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// Regression coverage for two bugs against
/// <c>Objects.Signing.CmsSignatureReader</c> that only surface against a CMS PlumePDF did not
/// itself produce — <c>CmsSignatureBuilder</c> never emits either shape, so neither defect was
/// ever exercised by the closed round-trip tests in <see cref="SignVerifyRoundTripTests"/>.
/// </summary>
public class CmsSignatureReaderTests
{
    [Fact]
    public async Task Verify_RsaSignatureUsesCombinedSha256WithRsaEncryptionOid_StillVerifiesPerRfc5754()
    {
        using var ca = new TestCertificateAuthority();
        var digest = SHA256.HashData("rfc5754 compat"u8.ToArray());
        var signer = new CertificateSigner(ca.LeafRsa);
        var cms = await CmsSignatureBuilder.BuildAsync(digest, HashAlgorithmName.SHA256, signer, CmsSignatureLevel.B);

        // PlumePDF's own builder always emits plain rsaEncryption (1.2.840.113549.1.1.1) as
        // SignerInfo.signatureAlgorithm, matching System.Security.Cryptography.Pkcs.CmsSigner's
        // convention — but RFC 5754 §3.2 requires verifiers to also accept the combined
        // shaNNNWithRSAEncryption forms third-party producers commonly emit instead. Both OIDs
        // DER-encode to the same 11-byte length (tag+len+9 value bytes, differing only in the
        // final value byte: ...01 for rsaEncryption vs ...0B for sha256WithRSAEncryption), and
        // SignerInfo.signatureAlgorithm is never itself covered by the signed-attrs digest
        // (RFC 5652 §5.4 signs the attributes, not the algorithm identifier fields around them)
        // — patching the LAST occurrence of the OID bytes in the CMS therefore simulates a
        // third-party producer's choice without invalidating the signature, and without needing
        // to hand-roll an entire second CMS encoder. The certificate's own SubjectPublicKeyInfo
        // (an earlier occurrence of the same OID, since CMS SignedData orders certificates
        // before signerInfos) is left untouched.
        var rsaEncryptionOidDer = new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01 };
        var lastIndex = LastIndexOf(cms, rsaEncryptionOidDer);
        Assert.True(lastIndex >= 0, "Expected the rsaEncryption OID to appear in the CMS.");

        var patched = (byte[])cms.Clone();
        patched[lastIndex + rsaEncryptionOidDer.Length - 1] = 0x0B; // sha256WithRSAEncryption (1.2.840.113549.1.1.11)

        var result = CmsSignatureReader.Verify(patched, digest, HashAlgorithmName.SHA256);

        Assert.Equal(CmsVerificationOutcome.Valid, result.Outcome);
    }

    [Fact]
    public void Verify_EssCertIdHashAlgorithmDiffersFromCmsDigest_MatchesInsteadOfFalseMismatch()
    {
        using var ca = new TestCertificateAuthority();
        var content = "third-party producer: SHA-384 CMS digest, SHA-256 ESS certHash"u8.ToArray();

        // Simulates a real third-party CMS producer (bypassing CmsSignatureBuilder, which
        // always keeps the two aligned by construction — exactly why this defect was never
        // caught by the closed round-trip tests) built at SHA-384 for the CMS's own digest and
        // signature, but whose ESSCertIDv2 signing-certificate attribute uses SHA-256 for
        // certHash — legal per RFC 5035 (ESSCertIDv2.hashAlgorithm is independent of the CMS's
        // own digest algorithm, and DEFAULTs to id-sha256 regardless of it).
        var contentInfo = new ContentInfo(content);
        var signedCms = new SignedCms(contentInfo, detached: true);
        var cmsSigner = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, ca.LeafRsa)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.2"), // SHA-384
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        var essAttributeValue = CmsSignatureBuilder.EncodeEssSigningCertificateV2(ca.LeafRsa, HashAlgorithmName.SHA256);
        cmsSigner.SignedAttributes.Add(new AsnEncodedData(CmsOids.SigningCertificateV2, essAttributeValue));
        signedCms.ComputeSignature(cmsSigner, silent: true);

        var digest = SHA384.HashData(content);
        var result = CmsSignatureReader.Verify(signedCms.Encode(), digest, HashAlgorithmName.SHA384);

        // Before the fix, TryParseEssCertHash's caller hashed the certificate with the CMS's
        // own digest algorithm (SHA-384) instead of the attribute's own (SHA-256), producing a
        // spurious SigningCertificateMismatch — SignatureCryptographicStatus's own documented
        // meaning for that status is "a substitution attempt", a false accusation against a
        // perfectly good signature.
        Assert.Equal(CmsVerificationOutcome.Valid, result.Outcome);
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
