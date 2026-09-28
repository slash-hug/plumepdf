using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// Regression coverage for the deterministic-signing guarantee ("RSA PKCS#1 v1.5 +
/// caller-supplied SigningTime + no TSA/LTV → byte-identical guaranteed and snapshot-tested"),
/// unexercised anywhere before this: neither the byte-identical promise itself
/// nor the <c>PLUME6053</c> refusal path for every combination the promise excludes had a test.
/// </summary>
public class DeterministicSigningTests
{
    [Fact]
    public void Add_RsaDeterministicWithCallerSigningTime_ProducesByteIdenticalOutputAcrossTwoRuns()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var firstOutputPath = SignVerifyRoundTripTests.TempPdfPath();
        var secondOutputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            var signingTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
            var options = PdfOptions.Default with { Deterministic = true };
            var signOptions = new PdfSignOptions
            {
                Certificate = ca.LeafRsa,
                SigningTime = signingTime,
                Reason = "Deterministic signing regression test",
            };

            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(firstOutputPath, signOptions, options);
            }

            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(secondOutputPath, signOptions, options);
            }

            var firstBytes = File.ReadAllBytes(firstOutputPath);
            var secondBytes = File.ReadAllBytes(secondOutputPath);
            Assert.Equal(firstBytes, secondBytes);

            // Byte-identical isn't vacuously true (e.g. two empty/garbage files) — confirm the
            // output is also a real, valid signature.
            using var signed = PdfDocument.Open(firstOutputPath);
            Assert.True(signed.Signatures[0].Verify().IsValid);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(firstOutputPath);
            File.Delete(secondOutputPath);
        }
    }

    [Fact]
    public void Add_DeterministicWithEcdsaCertificate_ThrowsPlume6053()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            // ECDSA signature octets are randomized per RFC 6979/nonce generation even at a
            // fixed digest and key — two runs can never agree byte-for-byte.
            var ex = Assert.Throws<PlumePdfException>(() => document.Signatures.Add(
                outputPath,
                new PdfSignOptions { Certificate = ca.LeafEcdsa, SigningTime = DateTimeOffset.UtcNow },
                PdfOptions.Default with { Deterministic = true }));

            Assert.Equal("PLUME6053", ex.Code);
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
    public async Task SignAsync_DeterministicWithTimestampAuthority_ThrowsPlume6053()
    {
        using var ca = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            // A TSA token embeds the TSA's own clock/nonce/signature — none of which two runs
            // can reproduce, even with an RSA leaf and a caller-supplied SigningTime.
            var ex = await Assert.ThrowsAsync<PlumePdfException>(() => document.Signatures.SignAsync(
                outputPath,
                new PdfSignOptions
                {
                    Certificate = ca.LeafRsa,
                    SigningTime = DateTimeOffset.UtcNow,
                    Level = PdfSignatureLevel.T,
                    TimestampAuthority = tsa,
                },
                PdfOptions.Default with { Deterministic = true }));

            Assert.Equal("PLUME6053", ex.Code);
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
    public void Add_DeterministicWithNoCallerSuppliedSigningTime_ThrowsPlume6053()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            // Falling back to DateTimeOffset.UtcNow for /M would make two runs disagree on the
            // one field the determinism guarantee requires the caller to pin explicitly.
            var ex = Assert.Throws<PlumePdfException>(() => document.Signatures.Add(
                outputPath,
                new PdfSignOptions { Certificate = ca.LeafRsa },
                PdfOptions.Default with { Deterministic = true }));

            Assert.Equal("PLUME6053", ex.Code);
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
}
