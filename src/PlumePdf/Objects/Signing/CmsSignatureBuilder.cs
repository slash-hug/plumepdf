using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PlumePdf.Objects.Signing;

/// <summary>The PAdES conformance level a built CMS targets — see <see cref="CmsSignatureBuilder"/>'s remarks.</summary>
internal enum CmsSignatureLevel
{
    /// <summary>PAdES B-B (baseline): no signature timestamp. The PDF signature dictionary's <c>/M</c> entry carries the claimed signing time.</summary>
    B,

    /// <summary>PAdES B-T (baseline + timestamp): an RFC 3161 signature-timestamp unsigned attribute is fetched and embedded, proving the signature existed no later than the TSA's clock.</summary>
    T,
}

/// <summary>
/// Builds a detached CMS (<c>SignedData</c>) signature over an externally-supplied content
/// digest.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this hand-assembles the CMS structure via <see cref="System.Formats.Asn1"/> instead
/// of <see cref="System.Security.Cryptography.Pkcs.SignedCms"/>.ComputeSignature:</b> the
/// writer's byte-range digest is computed by streaming through the (potentially huge) signed
/// document (<c>SigningWriteSession</c>) — this codebase never buffers a whole document
/// in memory to build a signature. <c>SignedCms.ComputeSignature</c> has no supported way to
/// accept a precomputed digest; it always hashes the literal content bytes you hand it (proven
/// empirically: attempting to add a caller-supplied <c>message-digest</c> signed attribute
/// alongside <c>ComputeSignature</c>'s own auto-computed one produces two attributes of the
/// same type — invalid CMS, and the wrong one wins on decode). Building the small, mechanical
/// <c>SignerInfo</c>/<c>SignedData</c> DER structure directly is therefore the only way to
/// honor both "digest-in" (<see cref="IPdfSigner"/>) and "never buffer the whole
/// document" at once. This is exactly why the ESS signing-certificate-v2 attribute already
/// needed <c>System.Formats.Asn1</c> — the same tool, used narrowly for
/// mechanical DER structure (never for cryptographic primitives, which stay entirely inside
/// the BCL/OS crypto stack via <see cref="IPdfSigner"/>, <see cref="RSA"/>, and
/// <see cref="ECDsa"/>). <see cref="CmsSignatureReader"/> validates every shape
/// this builder produces by decoding it with the BCL's own
/// <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> and checking its signature — see
/// <c>CmsSignatureBuilderTests</c>.
/// </para>
/// <para>
/// Signed attributes: content-type (<c>id-data</c>), message-digest (the caller's precomputed
/// digest), and ESS signing-certificate-v2 (mandatory under PAdES/CAdES to bind the signature
/// to the specific signing certificate, not merely a key that happens to verify it). The CMS
/// <c>signing-time</c> signed attribute is never emitted, on any level — PAdES/CAdES
/// conformance treats the claimed signing time as the PDF signature dictionary's <c>/M</c>
/// entry instead (set by the caller, not this builder), and a B-T-or-later signature's *proven*
/// time comes from the RFC 3161 token, not a self-asserted CMS attribute.
/// </para>
/// </remarks>
internal static class CmsSignatureBuilder
{
    /// <summary>Builds and DER-normalizes a detached CMS signature.</summary>
    /// <param name="contentDigest">The precomputed hash of the document's <c>/ByteRange</c>-covered bytes, hashed with <paramref name="digestAlgorithm"/>.</param>
    /// <param name="digestAlgorithm">The digest algorithm — SHA-256, SHA-384, or SHA-512.</param>
    /// <param name="signer">The seam that performs the actual signing operation.</param>
    /// <param name="level"><see cref="CmsSignatureLevel.B"/> or <see cref="CmsSignatureLevel.T"/>.</param>
    /// <param name="timestampAuthority">Required when <paramref name="level"/> is <see cref="CmsSignatureLevel.T"/>; ignored otherwise.</param>
    /// <param name="cancellationToken">Propagated to <paramref name="signer"/> and <paramref name="timestampAuthority"/>.</param>
    /// <returns>The DER-encoded CMS <c>ContentInfo</c>, ready to hex-encode into a PDF signature's <c>/Contents</c>.</returns>
    /// <exception cref="PlumePdfException">
    /// <paramref name="digestAlgorithm"/> is unsupported, <paramref name="signer"/>'s
    /// certificate uses an unsupported key algorithm, <see cref="CmsSignatureLevel.T"/> was
    /// requested without a <paramref name="timestampAuthority"/>, or the timestamp authority
    /// call fails.
    /// </exception>
    /// <example>
    /// <code>
    /// byte[] cms = await CmsSignatureBuilder.BuildAsync(
    ///     contentDigest: byteRangeDigest,
    ///     digestAlgorithm: HashAlgorithmName.SHA256,
    ///     signer: new CertificateSigner(myCertificate),
    ///     level: CmsSignatureLevel.T,
    ///     timestampAuthority: new IO.Http.HttpTimestampAuthority(new Uri("https://freetsa.org/tsr")));
    /// </code>
    /// </example>
    public static async Task<byte[]> BuildAsync(
        byte[] contentDigest,
        HashAlgorithmName digestAlgorithm,
        IPdfSigner signer,
        CmsSignatureLevel level,
        ITimestampAuthority? timestampAuthority = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(signer);

        if (level == CmsSignatureLevel.T && timestampAuthority is null)
        {
            throw new PlumePdfException("PLUME4012", "PAdES B-T requires an ITimestampAuthority (PdfSignOptions.TimestampAuthority / .TimestampAuthorityUrl) but none was supplied.");
        }

        var certificate = signer.Certificate;
        var signatureAlgorithm = GetSignatureAlgorithm(certificate, digestAlgorithm);

        var signedAttributeEncodings = new[]
        {
            EncodeAttribute(CmsOids.ContentType, EncodeObjectIdentifierValue(CmsOids.IdData)),
            EncodeAttribute(CmsOids.MessageDigest, EncodeOctetStringValue(contentDigest)),
            EncodeAttribute(CmsOids.SigningCertificateV2, EncodeEssSigningCertificateV2(certificate, digestAlgorithm)),
        };

        // RFC 5652 §5.4: the bytes hashed-and-signed use the UNIVERSAL SET tag; the bytes
        // embedded in SignerInfo use the [0] IMPLICIT tag. Same sorted content, different tag
        // — see DerNormalizerTests/CmsSignatureBuilderTests for the empirical proof this pair
        // of encodings agrees byte-for-byte apart from the tag octet.
        var hashInputBytes = EncodeSetOf(signedAttributeEncodings, Asn1Tag.SetOf);
        var embeddedSignedAttrs = EncodeSetOf(signedAttributeEncodings, new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));

        var digestToSign = ComputeHash(digestAlgorithm, hashInputBytes);
        var signatureBytes = await signer.SignAsync(digestToSign, cancellationToken).ConfigureAwait(false);

        var unsignedAttributeEncodings = new List<byte[]>();
        if (level == CmsSignatureLevel.T)
        {
            var messageImprint = ComputeHash(digestAlgorithm, signatureBytes);
            var tokenBytes = await timestampAuthority!.GetTimestampAsync(messageImprint, digestAlgorithm, cancellationToken).ConfigureAwait(false);
            var normalizedToken = DerNormalizer.Normalize(tokenBytes);
            unsignedAttributeEncodings.Add(EncodeAttribute(CmsOids.SignatureTimeStampToken, normalizedToken));
        }

        var signerInfoBytes = EncodeSignerInfo(certificate, digestAlgorithm, embeddedSignedAttrs, signatureAlgorithm, signatureBytes, unsignedAttributeEncodings);

        var certificates = new List<X509Certificate2> { certificate };
        certificates.AddRange(signer.AdditionalCertificates);
        var signedDataBytes = EncodeSignedData(digestAlgorithm, certificates, signerInfoBytes);
        var contentInfoBytes = EncodeContentInfo(CmsOids.IdSignedData, signedDataBytes);

        // Our own AsnWriter(DER) output is already definite-length DER; normalizing anyway is
        // cheap and keeps "every CMS blob passes through DerNormalizer" an unconditional,
        // exception-free invariant rather than one this builder is trusted to uphold on
        // its own.
        return DerNormalizer.Normalize(contentInfoBytes);
    }

    private static byte[] EncodeSignerInfo(
        X509Certificate2 certificate,
        HashAlgorithmName digestAlgorithm,
        byte[] embeddedSignedAttrs,
        (string Oid, bool NullParameters) signatureAlgorithm,
        byte[] signatureBytes,
        IReadOnlyList<byte[]> unsignedAttributeEncodings)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(1); // CMSVersion: 1, mandated when sid is IssuerAndSerialNumber (RFC 5652 §5.3).

            using (writer.PushSequence()) // sid: IssuerAndSerialNumber
            {
                writer.WriteEncodedValue(certificate.IssuerName.RawData);
                writer.WriteInteger(SerialNumberAsBigInteger(certificate));
            }

            writer.WriteEncodedValue(EncodeAlgorithmIdentifier(GetDigestOid(digestAlgorithm), includeNullParameters: true));
            writer.WriteEncodedValue(embeddedSignedAttrs);
            writer.WriteEncodedValue(EncodeAlgorithmIdentifier(signatureAlgorithm.Oid, signatureAlgorithm.NullParameters));
            writer.WriteOctetString(signatureBytes);

            if (unsignedAttributeEncodings.Count > 0)
            {
                writer.WriteEncodedValue(EncodeSetOf(unsignedAttributeEncodings, new Asn1Tag(TagClass.ContextSpecific, 1, isConstructed: true)));
            }
        }

        return writer.Encode();
    }

    private static byte[] EncodeSignedData(HashAlgorithmName digestAlgorithm, IReadOnlyList<X509Certificate2> certificates, byte[] signerInfoBytes)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(1); // CMSVersion: 1 — IssuerAndSerialNumber sids only, no CRLs.

            var digestAlgorithmIdentifier = EncodeAlgorithmIdentifier(GetDigestOid(digestAlgorithm), includeNullParameters: true);
            writer.WriteEncodedValue(EncodeSetOf([digestAlgorithmIdentifier], Asn1Tag.SetOf));

            using (writer.PushSequence()) // encapContentInfo — eContent omitted (detached signature).
            {
                writer.WriteObjectIdentifier(CmsOids.IdData);
            }

            var certificateEncodings = certificates.Select(c => c.RawData).ToArray();
            writer.WriteEncodedValue(EncodeSetOf(certificateEncodings, new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)));

            writer.WriteEncodedValue(EncodeSetOf([signerInfoBytes], Asn1Tag.SetOf));
        }

        return writer.Encode();
    }

    private static byte[] EncodeContentInfo(string contentTypeOid, byte[] content)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(contentTypeOid);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) // [0] EXPLICIT
            {
                writer.WriteEncodedValue(content);
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Encodes the ESS <c>SigningCertificateV2</c> attribute value (RFC 5035) binding a
    /// signature to <paramref name="signingCertificate"/>'s exact identity. Internal rather
    /// than private so <c>PlumePdf.Tests.Signing.FakeTimestampAuthority</c> can build the same
    /// attribute — RFC 3161 §2.4.2 requires it, and <c>System.Security.Cryptography.Pkcs</c>'s
    /// own <see cref="System.Security.Cryptography.Pkcs.Rfc3161TimestampToken.TryDecode"/> is
    /// documented as requiring one to accept a token at all.
    /// </summary>
    internal static byte[] EncodeEssSigningCertificateV2(X509Certificate2 signingCertificate, HashAlgorithmName digestAlgorithm)
    {
        var certHash = ComputeHash(digestAlgorithm, signingCertificate.RawData);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // SigningCertificateV2
        {
            using (writer.PushSequence()) // certs SEQUENCE OF ESSCertIDv2
            {
                using (writer.PushSequence()) // ESSCertIDv2
                {
                    // hashAlgorithm carries a DEFAULT value (id-sha256) per RFC 5035, but is
                    // written explicitly here rather than omitted for non-SHA-256 algorithms —
                    // wider validator tolerance in practice than relying on every reader to
                    // apply the DEFAULT correctly, and harmless when it does equal the default.
                    writer.WriteEncodedValue(EncodeAlgorithmIdentifier(GetDigestOid(digestAlgorithm), includeNullParameters: true));
                    writer.WriteOctetString(certHash);
                    // issuerSerial (OPTIONAL) omitted — certHash alone unambiguously binds the
                    // signature to this exact certificate, which is the attack this attribute
                    // exists to prevent (RFC 5035 §3).
                }
            }
            // policies (OPTIONAL) omitted.
        }

        return writer.Encode();
    }

    private static byte[] EncodeAttribute(string oid, byte[] derValue) => EncodeAttributeMultiValue(oid, [derValue]);

    /// <summary>
    /// Encodes an RFC 5652 <c>Attribute ::= SEQUENCE { attrType OBJECT IDENTIFIER, attrValues
    /// SET OF AttributeValue }</c> from its already-DER-encoded value(s). Shared with
    /// <see cref="CmsSignatureReader"/>, which re-derives this exact encoding from a decoded
    /// <see cref="System.Security.Cryptography.CryptographicAttributeObject"/> to recompute the
    /// signed-attributes digest it verifies against.
    /// </summary>
    internal static byte[] EncodeAttributeMultiValue(string oid, IReadOnlyList<byte[]> derValues)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            using (writer.PushSetOf())
            {
                foreach (var value in derValues)
                {
                    writer.WriteEncodedValue(value);
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Writes each already-DER-encoded element of <paramref name="elements"/> as a member of a
    /// SET tagged with <paramref name="tag"/>, preserving <paramref name="elements"/>' order —
    /// deliberately not <see cref="AsnWriter.PushSetOf(Asn1Tag?)"/>'s automatic canonical
    /// sort, since every caller here already supplies elements in an order that is either
    /// already correct (a single-element set) or governed by a rule this method doesn't need
    /// to re-derive. Shared with <see cref="CmsSignatureReader"/> for the same reason as
    /// <see cref="EncodeAttributeMultiValue"/>.
    /// </summary>
    /// <remarks>
    /// Assembled through a scratch SEQUENCE wrapper re-tagged by hand
    /// (<see cref="DerNormalizer.WrapWithTag"/>) rather than
    /// <c>writer.PushSequence(tag)</c> directly: <see cref="AsnWriter"/> refuses a
    /// UNIVERSAL-class tag that doesn't match the method's own built-in tag number, so
    /// <c>PushSequence</c> throws for the UNIVERSAL SET tag this method is called with for
    /// <c>signedAttrs</c>'s hash-input encoding (RFC 5652 §5.4) — the exact restriction
    /// <see cref="DerNormalizer"/> hits and works around the same way.
    /// </remarks>
    internal static byte[] EncodeSetOf(IReadOnlyList<byte[]> elements, Asn1Tag tag)
    {
        var scratch = new AsnWriter(AsnEncodingRules.DER);
        using (scratch.PushSequence())
        {
            foreach (var element in elements)
            {
                scratch.WriteEncodedValue(element);
            }
        }

        var content = new AsnReader(scratch.Encode(), AsnEncodingRules.DER).PeekContentBytes();
        return DerNormalizer.WrapWithTag(tag, content.Span);
    }

    private static byte[] EncodeAlgorithmIdentifier(string oid, bool includeNullParameters)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            if (includeNullParameters)
            {
                writer.WriteNull();
            }
        }

        return writer.Encode();
    }

    private static byte[] EncodeObjectIdentifierValue(string oid)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteObjectIdentifier(oid);
        return writer.Encode();
    }

    private static byte[] EncodeOctetStringValue(byte[] value)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteOctetString(value);
        return writer.Encode();
    }

    private static BigInteger SerialNumberAsBigInteger(X509Certificate2 certificate) =>
        new(certificate.GetSerialNumber(), isUnsigned: true, isBigEndian: false);

    /// <summary>
    /// Determines the CMS <c>signatureAlgorithm</c> <c>AlgorithmIdentifier</c> for
    /// <paramref name="certificate"/>'s key algorithm — plain <c>rsaEncryption</c> (with NULL
    /// parameters) for RSA, matching <see cref="System.Security.Cryptography.Pkcs.CmsSigner"/>'s
    /// own convention (verified empirically), or the combined <c>ecdsa-with-SHAxxx</c> OID (no
    /// parameters) for ECDSA.
    /// </summary>
    internal static (string Oid, bool NullParameters) GetSignatureAlgorithm(X509Certificate2 certificate, HashAlgorithmName digestAlgorithm)
    {
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is not null)
            {
                return (CmsOids.RsaEncryption, true);
            }
        }

        using (var ecdsa = certificate.GetECDsaPublicKey())
        {
            if (ecdsa is not null)
            {
                var oid = digestAlgorithm.Name switch
                {
                    "SHA256" => CmsOids.EcdsaWithSha256,
                    "SHA384" => CmsOids.EcdsaWithSha384,
                    "SHA512" => CmsOids.EcdsaWithSha512,
                    _ => throw new PlumePdfException("PLUME4016", $"Unsupported digest algorithm '{digestAlgorithm.Name}' for an ECDSA certificate — SHA-256, SHA-384, or SHA-512 only."),
                };
                return (oid, false);
            }
        }

        throw new PlumePdfException("PLUME4007", $"Certificate '{certificate.Subject}' key algorithm is neither RSA nor ECDSA — no other algorithm is supported for signing.");
    }

    internal static string GetDigestOid(HashAlgorithmName algorithm) => algorithm.Name switch
    {
        "SHA256" => CmsOids.Sha256,
        "SHA384" => CmsOids.Sha384,
        "SHA512" => CmsOids.Sha512,
        _ => throw new PlumePdfException("PLUME4016", $"Unsupported digest algorithm '{algorithm.Name}' — PAdES signing supports SHA-256, SHA-384, or SHA-512."),
    };

    internal static byte[] ComputeHash(HashAlgorithmName algorithm, ReadOnlySpan<byte> data) => algorithm.Name switch
    {
        "SHA256" => SHA256.HashData(data),
        "SHA384" => SHA384.HashData(data),
        "SHA512" => SHA512.HashData(data),
        _ => throw new PlumePdfException("PLUME4016", $"Unsupported digest algorithm '{algorithm.Name}' — PAdES signing supports SHA-256, SHA-384, or SHA-512."),
    };
}

/// <summary>Object identifiers used by <see cref="CmsSignatureBuilder"/> and <see cref="CmsSignatureReader"/> that <see cref="System.Security.Cryptography.Pkcs"/> has no typed constant for.</summary>
internal static class CmsOids
{
    internal const string IdData = "1.2.840.113549.1.7.1";
    internal const string IdSignedData = "1.2.840.113549.1.7.2";
    internal const string ContentType = "1.2.840.113549.1.9.3";
    internal const string MessageDigest = "1.2.840.113549.1.9.4";
    internal const string SigningCertificateV2 = "1.2.840.113549.1.9.16.2.47";
    internal const string SignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";
    internal const string RsaEncryption = "1.2.840.113549.1.1.1";
    // RFC 5754 §3.2: implementations MUST accept these combined shaNNNWithRSAEncryption forms
    // in SignerInfo.signatureAlgorithm alongside plain rsaEncryption; third-party producers
    // commonly emit them. PlumePDF's own CmsSignatureBuilder never emits these (it always uses
    // RsaEncryption, matching System.Security.Cryptography.Pkcs.CmsSigner's convention).
    internal const string Sha256WithRsaEncryption = "1.2.840.113549.1.1.11";
    internal const string Sha384WithRsaEncryption = "1.2.840.113549.1.1.12";
    internal const string Sha512WithRsaEncryption = "1.2.840.113549.1.1.13";
    internal const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
    internal const string EcdsaWithSha384 = "1.2.840.10045.4.3.3";
    internal const string EcdsaWithSha512 = "1.2.840.10045.4.3.4";
    internal const string Sha256 = "2.16.840.1.101.3.4.2.1";
    internal const string Sha384 = "2.16.840.1.101.3.4.2.2";
    internal const string Sha512 = "2.16.840.1.101.3.4.2.3";
}
