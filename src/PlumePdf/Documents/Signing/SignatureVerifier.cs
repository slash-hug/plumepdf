using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.IO;
using PlumePdf.Objects.Signing;

namespace PlumePdf.Documents.Signing;

/// <summary>
/// Verifies one <see cref="SignatureDictionaryInfo"/> (Task C2): recomputes the content digest
/// by streaming the document's own <c>/ByteRange</c>-covered bytes (never re-hashing a copy —
/// the same "never buffer the whole document" discipline <c>Objects.Signing.CmsSignatureBuilder</c>
/// follows), decodes and verifies the CMS via <c>Objects.Signing.CmsSignatureReader</c>, and
/// — when the caller supplies trust anchors — builds the certificate chain via
/// <c>Objects.Signing.CertificateChainResolver</c>. Every field of
/// <see cref="SignatureVerificationResult"/> is populated on every call (ByteRange coverage is
/// evaluated purely structurally by <see cref="SignatureDictionaryReader"/> and is never
/// skipped, even when the cryptographic check itself cannot run).
/// </summary>
internal static class SignatureVerifier
{
    // PlumePDF signs with exactly one of these three (Objects.Signing.CmsSignatureBuilder); a
    // document produced by a third-party signer might too, but this reader has no
    // pre-decode way to ask "what digest algorithm does this CMS actually use" other than a
    // BCL SignedCms.Decode peek (see TryPeekDigestAlgorithm) — kept local to this file so
    // Objects.Signing.CmsSignatureReader's own contract (caller supplies the algorithm)
    // doesn't need to widen just to serve this one caller.
    private static readonly HashAlgorithmName[] SupportedAlgorithms =
    [
        HashAlgorithmName.SHA256,
        HashAlgorithmName.SHA384,
        HashAlgorithmName.SHA512,
    ];

    /// <summary>Verifies <paramref name="info"/> against <paramref name="document"/>'s actual bytes.</summary>
    /// <param name="document">The document <paramref name="info"/> was read from.</param>
    /// <param name="info">The signature (or document timestamp) to verify.</param>
    /// <param name="trustedRoots">Trust anchors for chain building, or <see langword="null"/>/empty to skip chain evaluation entirely (<see cref="SignatureChainStatus.NotEvaluated"/>) — PlumePDF never falls back to the OS trust store silently.</param>
    public static SignatureVerificationResult Verify(PdfDocument document, SignatureDictionaryInfo info, IReadOnlyList<X509Certificate2>? trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(info);

        if (info.Contents.Length == 0)
        {
            return Result(SignatureCryptographicStatus.Malformed, info, detail: "No /Contents to verify.");
        }

        if (document.Source is not { } source)
        {
            return Result(SignatureCryptographicStatus.Malformed, info, detail: "This document has no backing byte source (e.g. the result of Pdf.Merge) — its actual on-disk bytes cannot be re-hashed for verification.");
        }

        if (info.ByteRange.Count == 0 || info.ByteRange.Count % 2 != 0)
        {
            return Result(SignatureCryptographicStatus.Malformed, info, detail: "/ByteRange is missing or has an odd number of entries — cannot determine which bytes were signed.");
        }

        // A segment naming bytes the file doesn't have is malformed DOCUMENT data (real
        // producers ship it — e.g. a veraPDF Permissions fixture whose /ByteRange outruns the
        // file), so it maps to a verdict per the read-leniently/verify-strictly split — unlike
        // the resource caps, which throw. Checked here so HashByteRange's own bounds throw
        // is left guarding only what remains: an IO race after this validation passed.
        for (var i = 0; i < info.ByteRange.Count; i += 2)
        {
            var offset = info.ByteRange[i];
            var length = info.ByteRange[i + 1];
            if (offset < 0 || length < 0 || offset + length > source.Length)
            {
                return Result(SignatureCryptographicStatus.Malformed, info, detail: $"/ByteRange segment [{offset}, {length}] falls outside the document's actual {source.Length}-byte length — the bytes it claims were signed do not exist.");
            }
        }

        // A /DocTimeStamp's /Contents is the raw RFC 3161 TimeStampToken itself — a
        // NON-detached CMS whose signed content is the TSTInfo structure (carrying its own
        // internal messageImprint), not a detached CMS over the document digest directly.
        // Objects.Signing.CmsSignatureReader's digest-comparison contract assumes the latter
        // shape (PlumePDF's own signature CMS, and any adbe.pkcs7.detached-style third-party
        // one) — verifying a document timestamp needs BCL Rfc3161TimestampToken's own
        // messageImprint-aware verification instead.
        if (info.IsDocTimeStamp)
        {
            return VerifyDocTimeStamp(document, info, source);
        }

        var digestAlgorithm = TryPeekDigestAlgorithm(info.Contents) ?? HashAlgorithmName.SHA256;
        HashAlgorithmName[] candidates = SupportedAlgorithms.Contains(digestAlgorithm) ? [digestAlgorithm] : SupportedAlgorithms;

        CmsVerificationResult? best = null;
        HashAlgorithmName usedAlgorithm = digestAlgorithm;
        foreach (var algorithm in candidates)
        {
            byte[] digest;
            try
            {
                digest = HashByteRange(source, info.ByteRange, algorithm, document.Options);
            }
            catch (PlumePdfException)
            {
                throw; // resource-cap refusals propagate — not a verification verdict.
            }

            var attempt = CmsSignatureReader.Verify(info.Contents, digest, algorithm, document.Options.MaxDssCertificates);
            if (attempt.Outcome != CmsVerificationOutcome.DigestMismatch || best is null)
            {
                best = attempt;
                usedAlgorithm = algorithm;
            }

            if (attempt.Outcome != CmsVerificationOutcome.DigestMismatch)
            {
                break;
            }
        }

        var cmsResult = best!;
        var cryptographicStatus = MapOutcome(cmsResult.Outcome);
        var hasTimestamp = cmsResult.Timestamp is not null;
        var timestampTime = cmsResult.Timestamp?.TokenInfo.Timestamp;

        // A timestamp token is a CMS *unsigned* attribute (RFC 5652 §11) — outside the
        // signature's own protection — so its signature-valid-ness alone only proves it was
        // produced (and never altered) by whoever holds the key for its own signing
        // certificate; it does not prove that certificate is a trustworthy TSA. Only once that
        // TSA certificate itself chain-builds to a supplied trust anchor is the claimed time
        // "proven" and safe to feed into the signing certificate's own chain build below —
        // otherwise an attacker could splice a self-signed timestamp with an attacker-chosen
        // genTime into any validly-signed CMS's unsigned attributes and steer chain validation
        // to an arbitrary instant.
        var isTimestampTrusted = false;
        DateTimeOffset? provenTimestampTime = null;
        if (trustedRoots is { Count: > 0 } &&
            cmsResult.TimestampVerification is { SignatureValid: true, SigningCertificate: { } tsaCertificate } timestampVerification)
        {
            var tsaChain = CertificateChainResolver.BuildChain(
                tsaCertificate,
                timestampVerification.Certificates,
                trustedRoots,
                validationTime: timestampTime?.UtcDateTime,
                maxChainDepth: document.Options.MaxCertificateChainDepth);
            if (tsaChain.Outcome == ChainBuildOutcome.Trusted)
            {
                isTimestampTrusted = true;
                provenTimestampTime = timestampTime;
            }
        }

        var chainStatus = SignatureChainStatus.NotEvaluated;
        if (trustedRoots is { Count: > 0 } && cmsResult.SigningCertificate is not null)
        {
            var chain = CertificateChainResolver.BuildChain(
                cmsResult.SigningCertificate,
                cmsResult.Certificates,
                trustedRoots,
                validationTime: (provenTimestampTime ?? info.SigningTime)?.UtcDateTime,
                maxChainDepth: document.Options.MaxCertificateChainDepth);
            chainStatus = MapChainOutcome(chain.Outcome);
        }

        var algorithmTrialDetail = usedAlgorithm != digestAlgorithm
            ? $"Digest algorithm resolved to {usedAlgorithm.Name} by trial (CMS decode did not identify one)."
            : null;
        var chainStatusDetail = chainStatus is not (SignatureChainStatus.Trusted or SignatureChainStatus.NotEvaluated)
            ? $"Certificate chain status: {chainStatus}."
            : null;

        return new SignatureVerificationResult(
            cryptographicStatus,
            info.CoversWholeDocument,
            chainStatus,
            cmsResult.SigningCertificate,
            cmsResult.Certificates,
            info.SigningTime,
            hasTimestamp,
            timestampTime,
            isTimestampTrusted,
            cmsResult.Detail ?? algorithmTrialDetail ?? chainStatusDetail);
    }

    private static SignatureVerificationResult VerifyDocTimeStamp(PdfDocument document, SignatureDictionaryInfo info, ByteSource source)
    {
        if (!Rfc3161TimestampToken.TryDecode(info.Contents, out var token, out _))
        {
            return Result(SignatureCryptographicStatus.Malformed, info, detail: "Could not decode /Contents as an RFC 3161 TimeStampToken.");
        }

        var hashAlgorithm = MapHashAlgorithmOid(token!.TokenInfo.HashAlgorithmId.Value) ?? HashAlgorithmName.SHA256;
        var digest = HashByteRange(source, info.ByteRange, hashAlgorithm, document.Options);

        var signedCms = token.AsSignedCms();
        var embeddedCertificates = new List<X509Certificate2>();
        foreach (X509Certificate2 certificate in signedCms.Certificates)
        {
            embeddedCertificates.Add(certificate);
        }

        var candidates = new X509Certificate2Collection(embeddedCertificates.ToArray());
        var signatureValid = token.VerifySignatureForHash(digest, hashAlgorithm, out var signingCertificate, candidates);

        SignatureCryptographicStatus status;
        if (!signatureValid)
        {
            var actualImprint = token.TokenInfo.GetMessageHash();
            status = !actualImprint.Span.SequenceEqual(digest) ? SignatureCryptographicStatus.DigestMismatch : SignatureCryptographicStatus.SignatureInvalid;
        }
        else
        {
            status = SignatureCryptographicStatus.Valid;
        }

        return new SignatureVerificationResult(
            status,
            info.CoversWholeDocument,
            SignatureChainStatus.NotEvaluated,
            signingCertificate,
            embeddedCertificates,
            info.SigningTime,
            hasTimestamp: true,
            timestampTime: token.TokenInfo.Timestamp,
            // ChainStatus above is always NotEvaluated for a /DocTimeStamp (this method never
            // chain-builds the TSA certificate against trustedRoots) — IsTimestampTrusted stays
            // false to match; a cryptographically-valid token signature alone does not make its
            // claimed time proven without also trusting the TSA certificate that produced it.
            isTimestampTrusted: false,
            detail: status == SignatureCryptographicStatus.Valid ? null : "The RFC 3161 token's messageImprint/signature did not verify against the document's actual /ByteRange-covered bytes.");
    }

    private static HashAlgorithmName? MapHashAlgorithmOid(string? oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    private static byte[] HashByteRange(ByteSource source, IReadOnlyList<long> byteRange, HashAlgorithmName algorithm, PdfOptions options)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        Span<byte> buffer = new byte[81920];

        for (var i = 0; i < byteRange.Count; i += 2)
        {
            var offset = byteRange[i];
            var length = byteRange[i + 1];

            if (offset < 0 || length < 0 || offset + length > source.Length)
            {
                throw new PlumePdfException("PLUME6052", $"/ByteRange segment [{offset}, {length}] falls outside the document's actual {source.Length}-byte length.");
            }

            var remaining = length;
            var position = offset;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(remaining, buffer.Length);
                var read = source.Read(position, buffer[..chunk]);
                if (read == 0)
                {
                    // A short read here is an IO condition (truncated or concurrently modified
                    // source), not tampering — digesting the prefix would misreport it as a
                    // DigestMismatch tamper verdict, so fail with the coded IO story instead.
                    throw new PlumePdfException("PLUME6052", $"/ByteRange segment [{offset}, {length}] ended after {length - remaining} byte(s) — the byte source returned no data at offset {position}, so the document is shorter than its /ByteRange claims (truncated or modified while reading).");
                }

                hash.AppendData(buffer[..read]);
                position += read;
                remaining -= read;
            }
        }

        return hash.GetHashAndReset();
    }

    private static HashAlgorithmName? TryPeekDigestAlgorithm(byte[] cms)
    {
        try
        {
            var decoded = new SignedCms();
            decoded.Decode(cms);
            if (decoded.SignerInfos.Count != 1)
            {
                return null;
            }

            return decoded.SignerInfos[0].DigestAlgorithm.Value switch
            {
                "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
                "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
                "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
                _ => null,
            };
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static SignatureCryptographicStatus MapOutcome(CmsVerificationOutcome outcome) => outcome switch
    {
        CmsVerificationOutcome.Valid => SignatureCryptographicStatus.Valid,
        CmsVerificationOutcome.DigestMismatch => SignatureCryptographicStatus.DigestMismatch,
        CmsVerificationOutcome.SignatureInvalid => SignatureCryptographicStatus.SignatureInvalid,
        CmsVerificationOutcome.UnsupportedAlgorithm => SignatureCryptographicStatus.UnsupportedAlgorithm,
        CmsVerificationOutcome.SigningCertificateMismatch => SignatureCryptographicStatus.SigningCertificateMismatch,
        _ => SignatureCryptographicStatus.Malformed,
    };

    private static SignatureChainStatus MapChainOutcome(ChainBuildOutcome outcome) => outcome switch
    {
        ChainBuildOutcome.Trusted => SignatureChainStatus.Trusted,
        ChainBuildOutcome.UntrustedRoot => SignatureChainStatus.UntrustedRoot,
        ChainBuildOutcome.NotTimeValid => SignatureChainStatus.NotTimeValid,
        ChainBuildOutcome.Revoked => SignatureChainStatus.Revoked,
        ChainBuildOutcome.PartialChain => SignatureChainStatus.PartialChain,
        _ => SignatureChainStatus.Error,
    };

    private static SignatureVerificationResult Result(SignatureCryptographicStatus status, SignatureDictionaryInfo info, string? detail) =>
        new(status, info.CoversWholeDocument, SignatureChainStatus.NotEvaluated, null, [], info.SigningTime, false, null, isTimestampTrusted: false, detail);
}
