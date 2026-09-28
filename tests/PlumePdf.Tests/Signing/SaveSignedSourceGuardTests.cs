using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary><c>PdfDocument.Save</c> (full rewrite) on a source carrying a real signature.</summary>
public class SaveSignedSourceGuardTests
{
    [Fact]
    public void Save_SignedSource_Strict_ThrowsPlume5014()
    {
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

            using var signed = PdfDocument.Open(signedPath, new PdfOptions { Strict = true });
            var ex = Assert.Throws<PlumePdfException>(() => signed.Save(outputPath));
            Assert.Equal("PLUME5014", ex.Code);
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
    public void Save_SignedSource_NonStrict_RecordsDiagnosticAndProceeds()
    {
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
            signed.Save(outputPath);

            Assert.Contains(signed.Diagnostics, d => d.Code == "PLUME5014");
            Assert.True(File.Exists(outputPath));
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
    public void Save_UnsignedSource_NoDiagnostic()
    {
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var outputPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using var document = PdfDocument.Open(sourcePath);
            document.Save(outputPath);

            Assert.DoesNotContain(document.Diagnostics, d => d.Code == "PLUME5014");
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
