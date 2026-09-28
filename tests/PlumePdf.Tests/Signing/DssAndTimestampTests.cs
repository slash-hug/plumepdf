using System.Security.Cryptography.X509Certificates;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// <c>doc.Signatures.AddLtvAsync</c> (<c>/DSS</c>) and
/// <c>AddDocumentTimestampAsync</c> (standalone <c>/DocTimeStamp</c>, B-LTA) — the B-B → B-LT →
/// B-LTA maintenance ladder, each step its own incremental revision, with every prior signature
/// still verifying afterward.
/// </summary>
public class DssAndTimestampTests
{
    [Fact]
    public async Task Ladder_SignThenAddLtvThenAddDocumentTimestamp_EveryStepStaysValid()
    {
        using var ca = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        var ltvPath = SignVerifyRoundTripTests.TempPdfPath();
        var ltaPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);

            // B-B
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            // B-LT: add DSS material for the existing signature.
            using (var document = PdfDocument.Open(signedPath))
            {
                await document.Signatures.AddLtvAsync(ltvPath, new NullRevocationFetcher(), trustedRoots: [ca.Root]);
            }

            using (var withDss = PdfDocument.Open(ltvPath))
            {
                Assert.True(withDss.Catalog!.Dictionary.ContainsKey(PdfName.DSS));
                Assert.True(withDss.Signatures[0].Verify().CryptographicStatus == SignatureCryptographicStatus.Valid);
            }

            // B-LTA: a standalone document timestamp covering everything so far.
            using (var document = PdfDocument.Open(ltvPath))
            {
                await document.Signatures.AddDocumentTimestampAsync(ltaPath, tsa);
            }

            using var final = PdfDocument.Open(ltaPath);
            Assert.Equal(2, final.Signatures.Count);
            Assert.False(final.Signatures[0].IsDocumentTimestamp);
            Assert.True(final.Signatures[1].IsDocumentTimestamp);

            var originalSignatureResult = final.Signatures[0].Verify();
            Assert.Equal(SignatureCryptographicStatus.Valid, originalSignatureResult.CryptographicStatus);

            var timestampResult = final.Signatures[1].Verify();
            Assert.True(timestampResult.IsValid);
            Assert.True(timestampResult.HasTimestamp);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
            File.Delete(ltvPath);
            File.Delete(ltaPath);
        }
    }

    [Fact]
    public async Task AddDocumentTimestampAsync_Deterministic_ThrowsPlume6053()
    {
        // On the maintenance paths, a TSA token's genTime/nonce differ per run, so
        // AddDocumentTimestampAsync can never honour Deterministic — it must refuse with the
        // same coded exception SigningOrchestrator uses, not silently produce flapping bytes.
        using var ca = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            using var signed = PdfDocument.Open(signedPath);
            var ex = await Assert.ThrowsAsync<PlumePdfException>(() => signed.Signatures.AddDocumentTimestampAsync(outputPath, tsa, options: PdfOptions.Default with { Deterministic = true }));
            Assert.Equal("PLUME6053", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task AddLtvAsync_Deterministic_ThrowsPlume6053()
    {
        // Same rule for the B-LT path: freshly fetched revocation material embeds
        // producedAt/thisUpdate times — never reproducible under the flag.
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            using var signed = PdfDocument.Open(signedPath);
            var ex = await Assert.ThrowsAsync<PlumePdfException>(() => signed.Signatures.AddLtvAsync(outputPath, new NullRevocationFetcher(), options: PdfOptions.Default with { Deterministic = true }));
            Assert.Equal("PLUME6053", ex.Code);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task AddLtvAsync_NoExistingSignature_ThrowsPlume6050()
    {
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            var ex = await Assert.ThrowsAsync<PlumePdfException>(() => document.Signatures.AddLtvAsync(outputPath, new NullRevocationFetcher()));
            Assert.Equal("PLUME6050", ex.Code);
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
    public async Task AddLtvAsync_FetcherHangsPastRevocationTimeout_ThrowsPlume4011PromptlyInsteadOfHanging()
    {
        // Regression coverage: PdfOptions.RevocationTimeout was documented as guarding "a slow
        // or hostile OCSP responder ... from hanging an LTV call indefinitely" but nothing read
        // it — a caller's own IRevocationFetcher (with no timeout of its own) could hang
        // AddLtvAsync for however long the network peer chose to. CertificateChainResolver now
        // enforces this option itself, independent of the fetcher's own behavior.
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        var ltvPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                // AdditionalCertificates embeds the intermediate so the chain BuildChain
                // resolves below actually has a non-root certificate to collect revocation
                // material for — otherwise the fetcher is never even called.
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa, AdditionalCertificates = [ca.Intermediate] });
            }

            using var signed = PdfDocument.Open(signedPath);
            var options = PdfOptions.Default with { RevocationTimeout = TimeSpan.FromMilliseconds(200) };

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<PlumePdfException>(() => signed.Signatures.AddLtvAsync(
                ltvPath,
                new HangingRevocationFetcher(),
                trustedRoots: [ca.Root],
                options: options));
            stopwatch.Stop();

            Assert.Equal("PLUME4011", ex.Code);
            // The fetcher's own delay is 30 seconds — this proves RevocationTimeout actually cut
            // the call short rather than the fetcher's own (nonexistent) timeout doing the work.
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Expected the call to be cut short by RevocationTimeout, but it took {stopwatch.Elapsed}.");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
            if (File.Exists(ltvPath))
            {
                File.Delete(ltvPath);
            }
        }
    }

    private sealed class NullRevocationFetcher : IRevocationFetcher
    {
        public Task<byte[]?> FetchOcspAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<byte[]?> FetchCrlAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);
    }

    /// <summary>An <see cref="IRevocationFetcher"/> that never responds on its own — standing in for a hostile or hung OCSP responder with no timeout of its own.</summary>
    private sealed class HangingRevocationFetcher : IRevocationFetcher
    {
        public async Task<byte[]?> FetchOcspAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return null;
        }

        public async Task<byte[]?> FetchCrlAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return null;
        }
    }
}
