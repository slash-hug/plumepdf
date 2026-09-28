using PlumePdf.Compose;
using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// End-to-end coverage for the orchestration layer that was missing entirely:
/// <c>Pdf.Sign</c>/<c>SignAsync</c>/<c>Verify</c> and
/// <c>doc.Signatures</c>, driving <c>SigningWriteSession</c> and
/// <c>CmsSignatureBuilder</c>/<c>CmsSignatureReader</c> against a real, on-disk document.
/// </summary>
public class SignVerifyRoundTripTests
{
    [Fact]
    public void Add_RsaCertificateBaseline_ProducesAValidSignature()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = TempPdfPath();
        var signedPath = TempPdfPath();
        try
        {
            CreateSampleDocument(sourcePath);

            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions
                {
                    Certificate = ca.LeafRsa,
                    Reason = "Testing",
                    Location = "Unit tests",
                });
            }

            using var signed = PdfDocument.Open(signedPath);
            Assert.Single(signed.Signatures);
            Assert.Equal("Signature1", signed.Signatures[0].FieldName);
            Assert.Equal("Testing", signed.Signatures[0].Reason);

            var result = signed.Signatures[0].Verify();
            Assert.True(result.IsValid);
            Assert.Equal(SignatureCryptographicStatus.Valid, result.CryptographicStatus);
            Assert.True(result.CoversWholeDocument);
            Assert.Equal(ca.LeafRsa.Thumbprint, result.SigningCertificate?.Thumbprint);
            Assert.False(result.HasTimestamp);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }

    [Fact]
    public async Task SignAsync_EcdsaCertificateWithTimestamp_ProducesAValidBTSignature()
    {
        using var ca = new TestCertificateAuthority();
        using var tsa = new FakeTimestampAuthority(ca.Intermediate);
        var sourcePath = TempPdfPath();
        var signedPath = TempPdfPath();
        try
        {
            CreateSampleDocument(sourcePath);

            using (var document = PdfDocument.Open(sourcePath))
            {
                await document.Signatures.SignAsync(signedPath, new PdfSignOptions
                {
                    Certificate = ca.LeafEcdsa,
                    Level = PdfSignatureLevel.T,
                    TimestampAuthority = tsa,
                });
            }

            using var signed = PdfDocument.Open(signedPath);
            var result = signed.Signatures[0].Verify();
            Assert.True(result.IsValid);
            Assert.True(result.HasTimestamp);
            Assert.NotNull(result.TimestampTime);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }

    [Fact]
    public void Verify_TamperedByteInsideByteRange_DetectsDigestMismatch()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = TempPdfPath();
        var signedPath = TempPdfPath();
        try
        {
            CreateSampleDocument(sourcePath);
            var originalLength = new FileInfo(sourcePath).Length;

            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            var bytes = File.ReadAllBytes(signedPath);

            // Flip a byte inside the ORIGINAL (pre-signature) file's own trailing "%%EOF" —
            // guaranteed to fall inside ByteRange segment 1 ([0, ContentsOffset)) yet
            // structurally inert: the reader chains from the newest revision's own startxref
            // (in the appendix this signature added), never re-parsing the superseded original
            // trailer's %%EOF marker, so the document still opens and the signature is still
            // discoverable — only its recomputed digest changes.
            var tamperOffset = (int)originalLength - 2;
            bytes[tamperOffset] ^= 0xFF;
            File.WriteAllBytes(signedPath, bytes);

            using var tampered = PdfDocument.Open(signedPath);
            Assert.Single(tampered.Signatures);

            var result = tampered.Signatures[0].Verify();
            Assert.False(result.IsValid);
            Assert.Equal(SignatureCryptographicStatus.DigestMismatch, result.CryptographicStatus);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }

    [Fact]
    public void Add_LevelTWithNoTimestampAuthority_ThrowsPlume6049()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = TempPdfPath();
        var signedPath = TempPdfPath();
        try
        {
            CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            var ex = Assert.Throws<PlumePdfException>(() => document.Signatures.Add(signedPath, new PdfSignOptions
            {
                Certificate = ca.LeafRsa,
                Level = PdfSignatureLevel.T,
            }));

            Assert.Equal("PLUME6049", ex.Code);
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
    public void Add_NoSignerOrCertificate_ThrowsPlume6048()
    {
        var sourcePath = TempPdfPath();
        var signedPath = TempPdfPath();
        try
        {
            CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);

            var ex = Assert.Throws<PlumePdfException>(() => document.Signatures.Add(signedPath, new PdfSignOptions()));
            Assert.Equal("PLUME6048", ex.Code);
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
    public void Add_SecondSignature_ProducesTwoDiscoverableSignatures()
    {
        using var ca = new TestCertificateAuthority();
        var sourcePath = TempPdfPath();
        var firstSignedPath = TempPdfPath();
        var secondSignedPath = TempPdfPath();
        try
        {
            CreateSampleDocument(sourcePath);

            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(firstSignedPath, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            using (var document = PdfDocument.Open(firstSignedPath))
            {
                document.Signatures.Add(secondSignedPath, new PdfSignOptions { Certificate = ca.LeafEcdsa });
            }

            using var twiceSigned = PdfDocument.Open(secondSignedPath);
            Assert.Equal(2, twiceSigned.Signatures.Count);
            Assert.Equal("Signature1", twiceSigned.Signatures[0].FieldName);
            Assert.Equal("Signature2", twiceSigned.Signatures[1].FieldName);

            // Both signatures survive the second incremental revision (append-only
            // byte-preservation) — each still verifies cryptographically. Signature1 no longer
            // covers the *whole* file (the second signature's own revision extended it past
            // Signature1's own /ByteRange, exactly as a real signature-then-append workflow
            // produces) — CoversWholeDocument correctly reflects that; only the second,
            // newest signature actually covers the whole document.
            var firstResult = twiceSigned.Signatures[0].Verify();
            Assert.Equal(SignatureCryptographicStatus.Valid, firstResult.CryptographicStatus);
            Assert.False(firstResult.CoversWholeDocument);

            var secondResult = twiceSigned.Signatures[1].Verify();
            Assert.True(secondResult.IsValid);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(firstSignedPath);
            File.Delete(secondSignedPath);
        }
    }

    internal static void CreateSampleDocument(string path)
    {
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("Sample content for signing.");
        });
        document.Save(path);
    }

    internal static string TempPdfPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-sign-{Guid.NewGuid():N}.pdf");
}
