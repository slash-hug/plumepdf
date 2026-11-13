using PlumePdf.Tests.Signing;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// Page-add on a signed document: <c>SaveIncremental</c> appends one revision, so the original
/// bytes stay a prefix of the output and an earlier signature still verifies (it then no longer
/// covers the whole document).
/// </summary>
public class PageAddSignatureTests
{
    [Fact]
    public void AnEarlierSignature_StillVerifies_AfterAnIncrementalAdd()
    {
        using var ca = new TestCertificateAuthority();
        var unsigned = SignVerifyRoundTripTests.TempPdfPath();
        var signed = SignVerifyRoundTripTests.TempPdfPath();
        var output = TempPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(unsigned);
            using (var document = PdfDocument.Open(unsigned))
            {
                document.Signatures.Add(signed, new PdfSignOptions { Certificate = ca.LeafRsa });
            }

            using (var document = PdfDocument.Open(signed))
            using (var source = PdfDocument.Open(Text(["New"])))
            {
                document.Pages.Add(source.Pages[0]);
                document.SaveIncremental(output);
            }

            var original = File.ReadAllBytes(signed);
            Assert.Equal(original, File.ReadAllBytes(output).AsSpan(0, original.Length).ToArray());

            using var reopened = PdfDocument.Open(output);
            Assert.Equal("New", reopened.Pages[^1].ExtractText().Text.Trim());
            var result = Assert.Single(reopened.Signatures).Verify([ca.Root]);
            Assert.Equal(SignatureCryptographicStatus.Valid, result.CryptographicStatus);
            Assert.False(result.CoversWholeDocument);
        }
        finally
        {
            File.Delete(unsigned);
            File.Delete(signed);
            File.Delete(output);
        }
    }
}
