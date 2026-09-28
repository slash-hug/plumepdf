using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// Regression coverage for important-severity bugs against
/// <c>Documents.Signing.SigningOrchestrator</c>/<c>IPdfSigner</c>/<c>Objects.Writing.ObjectSerializer</c>:
/// a failed signing pass must not permanently mutate the document's object graph (rollback), a
/// custom <see cref="IPdfSigner"/> whose declared digest algorithm disagrees with
/// <see cref="PdfSignOptions.DigestAlgorithm"/> must be refused rather than silently producing
/// an invalid CMS, and a half-finished signing placeholder must never be serializable as a real
/// signature by an ordinary save.
/// </summary>
public class SigningHardeningTests
{
    [Fact]
    public async Task SignAsync_SignerThrows_RollsBackAndLeavesDocumentRetryable()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            // First attempt: an IPdfSigner that fails deep inside CMS building (after the
            // signature dictionary/field/AcroForm mutations have already been registered) —
            // simulating an HSM/KMS timeout or rejection.
            await Assert.ThrowsAsync<InvalidOperationException>(() => document.Signatures.SignAsync(signedPath, new PdfSignOptions
            {
                Signer = new ThrowingSigner(ca.LeafRsa),
            }));

            // Before the fix, the failed attempt's PdfContentsPlaceholder/field/AcroForm
            // registrations would still stand here, so this retry would trip PLUME5013
            // ("must register exactly one PdfContentsPlaceholder (found 2)") — an
            // internal-invariant error surfaced to a caller who did nothing wrong.
            await document.Signatures.SignAsync(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });

            using var signed = PdfDocument.Open(signedPath);
            Assert.Single(signed.Signatures);
            Assert.True(signed.Signatures[0].Verify().IsValid);
        }
        finally
        {
            File.Delete(sourcePath);
            if (File.Exists(signedPath))
            {
                File.Delete(signedPath);
            }
        }
    }

    [Fact]
    public async Task SignAsync_SignerThrows_FallbackSaveIncrementalNeverWritesAFakeSignature()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        var fallbackPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() => document.Signatures.SignAsync(signedPath, new PdfSignOptions
            {
                Signer = new ThrowingSigner(ca.LeafRsa),
            }));

            // Before the fix, this would silently write a syntactically well-formed but
            // entirely fake `/Contents <0000...0000>` signature dictionary (ObjectSerializer's
            // placeholder arms had no guard against running outside a SigningWriteSession pass)
            // — the rollback above means there is nothing left to write, so this must be a
            // completely ordinary save with no trace of the failed attempt.
            document.SaveIncremental(fallbackPath);

            using var fallback = PdfDocument.Open(fallbackPath);
            Assert.Empty(fallback.Signatures);
        }
        finally
        {
            File.Delete(sourcePath);
            if (File.Exists(signedPath))
            {
                File.Delete(signedPath);
            }

            if (File.Exists(fallbackPath))
            {
                File.Delete(fallbackPath);
            }
        }
    }

    [Fact]
    public void ObjectSerializer_PlaceholderRegisteredOutsideSigningSession_ThrowsPlume5015()
    {
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            // A PdfContentsPlaceholder reaching the object graph with no SigningWriteSession
            // driving the write — exactly the state a half-finished signing pass would leave
            // behind absent the rollback fix (see the tests above).
            var reference = document.Objects.AllocateNumber();
            document.Objects.RegisterNew(reference, new PdfContentsPlaceholder(16));

            var ex = Assert.Throws<PlumePdfException>(() => document.SaveIncremental(outputPath));
            Assert.Equal("PLUME5015", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task SignAsync_SignerDigestAlgorithmDisagreesWithOptions_ThrowsPlume6055()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            // A signer declaring SHA-384 while PdfSignOptions.DigestAlgorithm stays at its
            // SHA-256 default: the digest actually hashed and handed to SignAsync would be
            // SHA-256, silently mismatched against what the signer claims to sign with.
            var ex = await Assert.ThrowsAsync<PlumePdfException>(() => document.Signatures.SignAsync(signedPath, new PdfSignOptions
            {
                Signer = new DigestAlgorithmDeclaringSigner(ca.LeafRsa, HashAlgorithmName.SHA384),
                DigestAlgorithm = HashAlgorithmName.SHA256,
            }));

            Assert.Equal("PLUME6055", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            if (File.Exists(signedPath))
            {
                File.Delete(signedPath);
            }
        }
    }

    /// <summary>An <see cref="IPdfSigner"/> that always fails, simulating an HSM/KMS rejecting the sign request.</summary>
    private sealed class ThrowingSigner : IPdfSigner
    {
        public ThrowingSigner(X509Certificate2 certificate) => Certificate = certificate;

        public X509Certificate2 Certificate { get; }
        public IReadOnlyList<X509Certificate2> AdditionalCertificates { get; } = [];
        public HashAlgorithmName DigestAlgorithm => HashAlgorithmName.SHA256;

        public Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Simulated HSM/KMS signing failure.");
    }

    /// <summary>An <see cref="IPdfSigner"/> that declares a caller-chosen (possibly wrong) <see cref="DigestAlgorithm"/>, wrapping a real local key so it would otherwise sign successfully.</summary>
    private sealed class DigestAlgorithmDeclaringSigner : IPdfSigner
    {
        public DigestAlgorithmDeclaringSigner(X509Certificate2 certificate, HashAlgorithmName digestAlgorithm)
        {
            Certificate = certificate;
            DigestAlgorithm = digestAlgorithm;
        }

        public X509Certificate2 Certificate { get; }
        public IReadOnlyList<X509Certificate2> AdditionalCertificates { get; } = [];
        public HashAlgorithmName DigestAlgorithm { get; }

        public Task<byte[]> SignAsync(byte[] digest, CancellationToken cancellationToken = default)
        {
            using var rsa = Certificate.GetRSAPrivateKey()!;
            return Task.FromResult(rsa.SignHash(digest, DigestAlgorithm, RSASignaturePadding.Pkcs1));
        }
    }
}
