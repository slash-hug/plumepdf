using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PlumePdf.Objects.Signing;

/// <summary>The result of decoding and verifying a CMS blob against an externally-supplied content digest.</summary>
internal enum CmsVerificationOutcome
{
    /// <summary>Structurally sound, the digest matches, and the cryptographic signature checks out.</summary>
    Valid,

    /// <summary>The CMS bytes could not be decoded, or lack a structure this reader requires (single <c>SignerInfo</c>, embedded signing certificate, message-digest attribute).</summary>
    Malformed,

    /// <summary>The signed message-digest attribute does not match the caller-supplied content digest — the signed bytes have been altered since signing.</summary>
    DigestMismatch,

    /// <summary>The message-digest matched, but the cryptographic signature over the signed-attributes set does not verify against the embedded signing certificate.</summary>
    SignatureInvalid,

    /// <summary>The SignerInfo declares a signature algorithm this reader does not support (only RSA PKCS#1 v1.5 and ECDSA are implemented — Phase 5 does not ship RSASSA-PSS).</summary>
    UnsupportedAlgorithm,

    /// <summary>An ESS signing-certificate-v2 attribute is present but its certificate hash does not match the embedded signing certificate — a substitution attempt.</summary>
    SigningCertificateMismatch,
}

/// <summary>
/// The result of verifying an RFC 3161 signature-timestamp token found in a CMS's unsigned
/// attributes, produced by <see cref="CmsSignatureReader"/>.
/// </summary>
/// <param name="SignatureValid">
/// Whether the token's own signature verifies against the hash of the CMS's
/// <c>SignerInfo.signature</c> octets (RFC 3161/CAdES: a signature-timestamp's
/// <c>messageImprint</c> covers the signature value it timestamps, not the document). This
/// proves the token was produced by whoever holds the private key for
/// <see cref="SigningCertificate"/> and was never altered — it does <em>not</em> by itself
/// prove that certificate is a trustworthy TSA; callers must still chain-build
/// <see cref="SigningCertificate"/> against trusted TSA roots (RFC 5652 unsigned attributes,
/// including this one, sit outside the CMS's own signature and so cannot be trusted from
/// decoding alone).
/// </param>
/// <param name="SigningCertificate">The token's own signing (TSA) certificate, when one could be identified.</param>
/// <param name="Certificates">Every certificate embedded in the timestamp token, for chain building.</param>
internal sealed record TimestampVerification(bool SignatureValid, X509Certificate2? SigningCertificate, IReadOnlyList<X509Certificate2> Certificates);

/// <summary>The decoded and verified shape of a CMS signature, returned by <see cref="CmsSignatureReader.Verify"/>.</summary>
/// <param name="Outcome">The verification verdict. Only <see cref="CmsVerificationOutcome.Valid"/> means every check passed.</param>
/// <param name="SigningCertificate">The end-entity certificate the CMS names as its signer, when one could be identified.</param>
/// <param name="Certificates">Every certificate embedded in the CMS (signing certificate included), for chain building.</param>
/// <param name="Timestamp">The decoded RFC 3161 signature timestamp token, when an <c>id-aa-signatureTimeStampToken</c> unsigned attribute is present and well-formed.</param>
/// <param name="TimestampVerification">
/// The verdict for <paramref name="Timestamp"/>'s own signature, or <see langword="null"/> when
/// no timestamp is present. Callers must check <see cref="TimestampVerification.SignatureValid"/>
/// (and chain-build <see cref="TimestampVerification.SigningCertificate"/>) before treating the
/// token's claimed time as anything more than an unverified assertion — see that type's remarks.
/// </param>
/// <param name="Detail">A human-readable explanation for any non-<see cref="CmsVerificationOutcome.Valid"/> outcome.</param>
internal sealed record CmsVerificationResult(
    CmsVerificationOutcome Outcome,
    X509Certificate2? SigningCertificate,
    IReadOnlyList<X509Certificate2> Certificates,
    Rfc3161TimestampToken? Timestamp,
    TimestampVerification? TimestampVerification,
    string? Detail)
{
    /// <summary>Shorthand for <c>Outcome == Valid</c>.</summary>
    public bool IsValid => Outcome == CmsVerificationOutcome.Valid;
}

/// <summary>
/// Decodes a CMS signature and verifies it against an externally-supplied content digest
/// (Task B4) — never against a re-hashed copy of the document's actual bytes, for the same
/// "never buffer the whole document" reason <see cref="CmsSignatureBuilder"/> hand-assembles
/// its output (see that type's remarks).
/// </summary>
/// <remarks>
/// <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> is used here only for convenient
/// <em>parsing</em> (pulling <see cref="SignerInfo"/>, certificates, and attribute values out
/// of the DER) — never for its own <c>CheckSignature</c>/<c>CheckHash</c>, both of which
/// require the real detached content bytes to validate the message-digest attribute
/// (empirically confirmed: <c>CheckSignature(verifySignatureOnly: true)</c> still fails when
/// given the wrong — or no — content, so it cannot be used with only a precomputed digest).
/// The message-digest comparison and the raw cryptographic signature check are therefore done
/// by hand here, mirroring exactly what <see cref="CmsSignatureBuilder"/> assembled.
/// </remarks>
internal static class CmsSignatureReader
{
    /// <summary>Decodes and verifies a CMS signature.</summary>
    /// <param name="cms">The DER-encoded CMS <c>ContentInfo</c> (a PDF signature's <c>/Contents</c>, decoded from hex).</param>
    /// <param name="expectedContentDigest">The independently-computed digest of the document's actual <c>/ByteRange</c>-covered bytes.</param>
    /// <param name="digestAlgorithm">The algorithm <paramref name="expectedContentDigest"/> was hashed with.</param>
    /// <param name="maxEmbeddedCertificates">Cap: refuses CMS blobs embedding more than this many certificates, guarding against a maliciously oversized signature dictionary.</param>
    /// <returns>The verification result — see <see cref="CmsVerificationResult"/>. Never throws for an invalid, tampered, or algorithm-mismatched signature; that is an ordinary, expected verification outcome, not a failure of this method.</returns>
    /// <exception cref="PlumePdfException"><paramref name="cms"/> embeds more certificates than <paramref name="maxEmbeddedCertificates"/> allows (<c>PLUME4013</c>) — an untrusted-input resource cap, not a verification verdict.</exception>
    public static CmsVerificationResult Verify(byte[] cms, byte[] expectedContentDigest, HashAlgorithmName digestAlgorithm, int maxEmbeddedCertificates = 32)
    {
        ArgumentNullException.ThrowIfNull(cms);
        ArgumentNullException.ThrowIfNull(expectedContentDigest);

        SignedCms decoded;
        try
        {
            decoded = new SignedCms();
            decoded.Decode(cms);
        }
        catch (CryptographicException ex)
        {
            return Result(CmsVerificationOutcome.Malformed, detail: $"CMS decode failed: {ex.Message}");
        }

        if (decoded.SignerInfos.Count != 1)
        {
            return Result(CmsVerificationOutcome.Malformed, detail: $"Expected exactly one SignerInfo, found {decoded.SignerInfos.Count}.");
        }

        var signerInfo = decoded.SignerInfos[0];
        var signingCertificate = signerInfo.Certificate;
        if (signingCertificate is null)
        {
            return Result(CmsVerificationOutcome.Malformed, detail: "The signing certificate is not embedded in the CMS.");
        }

        var embeddedCertificates = new List<X509Certificate2>();
        foreach (X509Certificate2 certificate in decoded.Certificates)
        {
            embeddedCertificates.Add(certificate);
        }

        if (embeddedCertificates.Count > maxEmbeddedCertificates)
        {
            throw new PlumePdfException("PLUME4013", $"CMS embeds {embeddedCertificates.Count} certificates, exceeding the configured cap of {maxEmbeddedCertificates}.");
        }

        // RFC 5652 §11.1: whenever signedAttrs is present it MUST carry a content-type attribute
        // whose value matches encapContentInfo.eContentType (id-data for a PAdES detached
        // signature, id-ct-TSTInfo inside a timestamp token) — a mismatch means the signed
        // attribute set was transplanted from a different content.
        var contentTypeValue = FindAttributeValue(signerInfo.SignedAttributes, CmsOids.ContentType);
        if (contentTypeValue is null || !TryReadOidContent(contentTypeValue, out var contentTypeOid))
        {
            return Result(CmsVerificationOutcome.Malformed, signingCertificate, embeddedCertificates, detail: "signed-attrs has no well-formed content-type attribute (RFC 5652 §11.1 requires one whenever signedAttrs is present).");
        }

        if (contentTypeOid != decoded.ContentInfo.ContentType.Value)
        {
            return Result(CmsVerificationOutcome.Malformed, signingCertificate, embeddedCertificates, detail: $"The content-type signed attribute ('{contentTypeOid}') does not match encapContentInfo.eContentType ('{decoded.ContentInfo.ContentType.Value}') — the signed-attributes set does not belong to this content.");
        }

        var messageDigestValue = FindAttributeValue(signerInfo.SignedAttributes, CmsOids.MessageDigest);
        if (messageDigestValue is null || !TryReadOctetStringContent(messageDigestValue, out var actualDigest))
        {
            return Result(CmsVerificationOutcome.Malformed, signingCertificate, embeddedCertificates, detail: "signed-attrs has no well-formed message-digest attribute.");
        }

        if (!actualDigest.AsSpan().SequenceEqual(expectedContentDigest))
        {
            return Result(CmsVerificationOutcome.DigestMismatch, signingCertificate, embeddedCertificates, detail: "The message-digest signed attribute does not match the document's actual /ByteRange-covered bytes — the signed content has been altered since signing.");
        }

        byte[] reconstructedSignedAttributes;
        try
        {
            reconstructedSignedAttributes = ReconstructSignedAttributes(signerInfo.SignedAttributes);
        }
        catch (AsnContentException ex)
        {
            return Result(CmsVerificationOutcome.Malformed, signingCertificate, embeddedCertificates, detail: $"Could not reconstruct the signed-attributes DER: {ex.Message}");
        }

        var digestOverAttributes = CmsSignatureBuilder.ComputeHash(digestAlgorithm, reconstructedSignedAttributes);
        var signatureAlgorithmOid = signerInfo.SignatureAlgorithm.Value;
        if (signatureAlgorithmOid is null)
        {
            return Result(CmsVerificationOutcome.Malformed, signingCertificate, embeddedCertificates, detail: "SignerInfo has no signature algorithm OID.");
        }

        var signatureValid = VerifyRawSignature(signingCertificate, digestOverAttributes, digestAlgorithm, signerInfo.GetSignature(), signatureAlgorithmOid);
        if (signatureValid is null)
        {
            return Result(CmsVerificationOutcome.UnsupportedAlgorithm, signingCertificate, embeddedCertificates, detail: $"Unsupported signature algorithm '{signatureAlgorithmOid}'.");
        }

        if (signatureValid == false)
        {
            return Result(CmsVerificationOutcome.SignatureInvalid, signingCertificate, embeddedCertificates, detail: "The cryptographic signature over the signed-attributes set is invalid.");
        }

        var essValue = FindAttributeValue(signerInfo.SignedAttributes, CmsOids.SigningCertificateV2);
        if (essValue is not null && TryParseEssCertHash(essValue, out var certHashFromAttribute, out var essHashAlgorithm))
        {
            // RFC 5035: ESSCertIDv2.hashAlgorithm is independent of the CMS's own digest
            // algorithm and defaults to id-sha256 when absent — hashing with the CMS digest
            // algorithm instead would falsely flag a good SHA-384/512 signature whose producer
            // wrote (or defaulted) a SHA-256 ESS hash as a certificate-substitution attempt.
            var actualCertHash = CmsSignatureBuilder.ComputeHash(essHashAlgorithm, signingCertificate.RawData);
            if (!certHashFromAttribute.AsSpan().SequenceEqual(actualCertHash))
            {
                return Result(CmsVerificationOutcome.SigningCertificateMismatch, signingCertificate, embeddedCertificates, detail: "The ESS signing-certificate-v2 attribute's certHash does not match the embedded signing certificate.");
            }
        }

        Rfc3161TimestampToken? timestamp = null;
        TimestampVerification? timestampVerification = null;
        var timestampValue = FindAttributeValue(signerInfo.UnsignedAttributes, CmsOids.SignatureTimeStampToken);
        if (timestampValue is not null && Rfc3161TimestampToken.TryDecode(timestampValue, out var decodedToken, out _))
        {
            timestamp = decodedToken;
            timestampVerification = VerifyTimestampToken(decodedToken, signerInfo.GetSignature());
        }

        return Result(CmsVerificationOutcome.Valid, signingCertificate, embeddedCertificates, timestamp, timestampVerification);
    }

    /// <summary>
    /// Verifies an RFC 3161 signature-timestamp token's own signature against the CMS
    /// <c>SignerInfo.signature</c> octets it timestamps (RFC 3161/CAdES: the token's
    /// <c>messageImprint</c> covers the hash of the signature value, not the document content).
    /// Does not evaluate trust in the resulting TSA certificate — callers must chain-build
    /// <see cref="TimestampVerification.SigningCertificate"/> themselves; see that type's remarks.
    /// </summary>
    private static TimestampVerification VerifyTimestampToken(Rfc3161TimestampToken token, byte[] signerInfoSignature)
    {
        var hashAlgorithm = MapHashAlgorithmOid(token.TokenInfo.HashAlgorithmId.Value) ?? HashAlgorithmName.SHA256;
        var digest = CmsSignatureBuilder.ComputeHash(hashAlgorithm, signerInfoSignature);

        var signedCms = token.AsSignedCms();
        var embeddedCertificates = new List<X509Certificate2>();
        foreach (X509Certificate2 certificate in signedCms.Certificates)
        {
            embeddedCertificates.Add(certificate);
        }

        var candidates = new X509Certificate2Collection(embeddedCertificates.ToArray());

        bool signatureValid;
        X509Certificate2? signingCertificate;
        try
        {
            signatureValid = token.VerifySignatureForHash(digest, hashAlgorithm, out signingCertificate, candidates);
        }
        catch (CryptographicException)
        {
            // A malformed or unsupported-algorithm timestamp token is not a trustworthy time
            // source — treat it the same as a signature that failed to verify.
            signatureValid = false;
            signingCertificate = null;
        }

        return new TimestampVerification(signatureValid, signingCertificate, embeddedCertificates);
    }

    private static HashAlgorithmName? MapHashAlgorithmOid(string? oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    private static CmsVerificationResult Result(
        CmsVerificationOutcome outcome,
        X509Certificate2? signingCertificate = null,
        IReadOnlyList<X509Certificate2>? certificates = null,
        Rfc3161TimestampToken? timestamp = null,
        TimestampVerification? timestampVerification = null,
        string? detail = null) =>
        new(outcome, signingCertificate, certificates ?? [], timestamp, timestampVerification, detail);

    private static byte[]? FindAttributeValue(CryptographicAttributeObjectCollection attributes, string oid)
    {
        foreach (CryptographicAttributeObject attribute in attributes)
        {
            if (attribute.Oid?.Value == oid && attribute.Values.Count > 0)
            {
                return attribute.Values[0]!.RawData;
            }
        }

        return null;
    }

    private static bool TryReadOidContent(byte[] derValue, out string oid)
    {
        try
        {
            var reader = new AsnReader(derValue, AsnEncodingRules.BER);
            oid = reader.ReadObjectIdentifier();
            return !reader.HasData;
        }
        catch (AsnContentException)
        {
            oid = string.Empty;
            return false;
        }
    }

    private static bool TryReadOctetStringContent(byte[] derValue, out byte[] content)
    {
        try
        {
            var reader = new AsnReader(derValue, AsnEncodingRules.BER);
            content = reader.ReadOctetString();
            return !reader.HasData;
        }
        catch (AsnContentException)
        {
            content = [];
            return false;
        }
    }

    /// <summary>
    /// Re-derives the DER encoding of the CMS <c>signedAttrs</c> set — tagged with the
    /// UNIVERSAL SET tag for hashing, per RFC 5652 §5.4, not the <c>[0]</c> IMPLICIT tag it
    /// carries inside <c>SignerInfo</c> — from the already-decoded, already-parsed attribute
    /// collection. The reverse of <see cref="CmsSignatureBuilder"/>'s
    /// <c>EncodeAttributeMultiValue</c>/<c>EncodeSetOf</c> pair.
    /// </summary>
    private static byte[] ReconstructSignedAttributes(CryptographicAttributeObjectCollection attributes)
    {
        var attributeEncodings = new List<byte[]>();
        foreach (CryptographicAttributeObject attribute in attributes)
        {
            var oid = attribute.Oid?.Value ?? throw new AsnContentException("A signed attribute has no OID.");
            var values = new List<byte[]>();
            foreach (AsnEncodedData value in attribute.Values)
            {
                values.Add(value.RawData);
            }

            attributeEncodings.Add(CmsSignatureBuilder.EncodeAttributeMultiValue(oid, values));
        }

        return CmsSignatureBuilder.EncodeSetOf(attributeEncodings, Asn1Tag.SetOf);
    }

    private static bool? VerifyRawSignature(X509Certificate2 certificate, byte[] digest, HashAlgorithmName digestAlgorithm, byte[] signature, string signatureAlgorithmOid)
    {
        // RFC 5754 §3.2 requires implementations to accept the combined shaNNNWithRSAEncryption
        // OIDs in SignerInfo.signatureAlgorithm as well as plain rsaEncryption — both are common
        // in the wild from third-party producers. PKCS#1 v1.5 verification itself only needs
        // the digest algorithm (SignerInfo's separate DigestAlgorithm field) and the padding, so
        // no other branch here needs to change to support them.
        if (signatureAlgorithmOid is CmsOids.RsaEncryption or CmsOids.Sha256WithRsaEncryption or CmsOids.Sha384WithRsaEncryption or CmsOids.Sha512WithRsaEncryption)
        {
            using var rsa = certificate.GetRSAPublicKey();
            return rsa is null ? false : rsa.VerifyHash(digest, signature, digestAlgorithm, RSASignaturePadding.Pkcs1);
        }

        if (signatureAlgorithmOid is CmsOids.EcdsaWithSha256 or CmsOids.EcdsaWithSha384 or CmsOids.EcdsaWithSha512)
        {
            using var ecdsa = certificate.GetECDsaPublicKey();
            return ecdsa is null ? false : ecdsa.VerifyHash(digest, signature, DSASignatureFormat.Rfc3279DerSequence);
        }

        // RSASSA-PSS (1.2.840.113549.1.1.10) and anything else: not implemented this phase.
        return null;
    }

    /// <param name="essValue">The raw DER of the <c>id-aa-signingCertificateV2</c> signed attribute's value.</param>
    /// <param name="certHash">The first ESSCertIDv2's <c>certHash</c>.</param>
    /// <param name="hashAlgorithm">
    /// The ESSCertIDv2's own <c>hashAlgorithm</c> field, when present, or SHA-256 — its RFC 5035
    /// DEFAULT — when absent. Independent of the CMS's own digest algorithm.
    /// </param>
    private static bool TryParseEssCertHash(byte[] essValue, out byte[] certHash, out HashAlgorithmName hashAlgorithm)
    {
        hashAlgorithm = HashAlgorithmName.SHA256;
        try
        {
            var reader = new AsnReader(essValue, AsnEncodingRules.BER);
            var signingCertificateV2 = reader.ReadSequence();
            var certs = signingCertificateV2.ReadSequence(); // certs SEQUENCE OF ESSCertIDv2
            var firstCertId = certs.ReadSequence(); // the first ESSCertIDv2

            if (firstCertId.HasData && firstCertId.PeekTag() == Asn1Tag.Sequence)
            {
                var algorithmIdentifier = firstCertId.ReadSequence(); // hashAlgorithm AlgorithmIdentifier
                var oid = algorithmIdentifier.ReadObjectIdentifier();
                hashAlgorithm = MapHashAlgorithmOid(oid) ?? HashAlgorithmName.SHA256;
            }

            certHash = firstCertId.ReadOctetString(); // certHash
            return true;
        }
        catch (AsnContentException)
        {
            certHash = [];
            return false;
        }
    }
}
