using System.Text;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Validates encryption read against <b>externally produced</b> ground truth: the
/// <c>qpdf-*.pdf</c> fixtures were encrypted by qpdf 12.4.0 (see Fixtures/README.md), an
/// independent, battle-tested implementation — closing the review gap that the AES-256/R6
/// hardened-hash key derivation had only ever been validated against PlumePDF's own
/// round-trip. Passwords: user <c>"user pass"</c>, owner <c>"owner pass"</c> (except the
/// empty-user variant), payload identical to <c>classic-xref.pdf</c>.
/// </summary>
public class GroundTruthEncryptionTests
{
    private static readonly byte[] ExpectedContent =
        Encoding.ASCII.GetBytes("BT /F1 12 Tf 20 50 Td (Hello, PlumePDF fixture) Tj ET");

    private static string Fixture(string name) => Path.Combine(CorpusFixture.FixturesRoot, name);

    private static void AssertDecryptsToKnownContent(string fixtureName, PdfOptions options)
    {
        using var document = PdfDocument.Open(Fixture(fixtureName), options);

        var page = Assert.Single(document.Pages);
        var contentsRef = Assert.IsType<PdfReference>(page.Dictionary[PdfName.Get("Contents")]);
        var contentStream = Assert.IsType<PdfStream>(document.Objects[contentsRef.Target]);
        var decoded = contentStream.GetDecodedBytes(PdfFilterRegistry.Default, options);

        Assert.Equal(ExpectedContent, decoded);
    }

    [Theory]
    [InlineData("qpdf-aes256.pdf")] // R6 / AES-256: the ISO 32000-2 Algorithm 2.B path
    [InlineData("qpdf-aes128.pdf")] // R4 / AES-128
    [InlineData("qpdf-rc4-128.pdf")] // R3 / RC4-128
    public void QpdfEncryptedFixture_OpensWithUserPassword_AndDecryptsToKnownContent(string fixtureName)
    {
        AssertDecryptsToKnownContent(fixtureName, PdfOptions.Default with { UserPassword = "user pass" });
    }

    [Theory]
    [InlineData("qpdf-aes256.pdf")]
    [InlineData("qpdf-aes128.pdf")]
    [InlineData("qpdf-rc4-128.pdf")]
    public void QpdfEncryptedFixture_OpensWithOwnerPassword_AndDecryptsToKnownContent(string fixtureName)
    {
        AssertDecryptsToKnownContent(fixtureName, PdfOptions.Default with { OwnerPassword = "owner pass" });
    }

    [Fact]
    public void QpdfAes256EmptyUserPassword_OpensWithDefaultOptions()
    {
        AssertDecryptsToKnownContent("qpdf-aes256-empty-user.pdf", PdfOptions.Default);
    }

    [Theory]
    [InlineData("qpdf-aes256.pdf")]
    [InlineData("qpdf-aes128.pdf")]
    [InlineData("qpdf-rc4-128.pdf")]
    public void QpdfEncryptedFixture_WrongPassword_ThrowsCoded(string fixtureName)
    {
        var options = PdfOptions.Default with { UserPassword = "not the password" };

        var ex = Assert.ThrowsAny<PlumePdfException>(() =>
        {
            using var document = PdfDocument.Open(Fixture(fixtureName), options);
            _ = document.Pages.Count;
        });

        Assert.StartsWith("PLUME", ex.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void BrokenXref_LyingOffsets_RecoversPagesViaFullFileScan()
    {
        // Regression for the recovery-ladder gap the cookbook exposed: broken-xref.pdf's
        // table parses (syntax intact) but every offset lies; per-object resolution must
        // fall back to the brute-force scan map (PLUME2064) instead of nulling out to a
        // 0-page document. qpdf reads this file fine — so must PlumePDF.
        using var document = PdfDocument.Open(Path.Combine(CorpusFixture.FixturesRoot, "broken-xref.pdf"));

        Assert.Single(document.Pages);
        Assert.Contains(document.Diagnostics, d => d.Code == "PLUME2064");
    }
}

