using System.Net;
using PlumePdf.IO.Http;
using Xunit;

namespace PlumePdf.Tests.Signing;

/// <summary>
/// Semantics pinned by the Phase 5 review loop: <see cref="SignatureVerificationResult.IsValid"/>
/// must not read <see langword="true"/> when the caller opted into chain evaluation and it came
/// back bad (the every-example-steers-to-IsValid finding), and
/// <see cref="HttpRevocationFetcher"/>'s default address policy must reject the SSRF target
/// classes a hostile certificate can name in its AIA/CRL-DP extensions.
/// </summary>
public class VerificationSemanticsTests
{
    [Fact]
    public void IsValid_TrustAnchorsSupplied_ChainVerdictParticipates()
    {
        using var ca = new TestCertificateAuthority();
        using var wrongCa = new TestCertificateAuthority();
        var sourcePath = SignVerifyRoundTripTests.TempPdfPath();
        var signedPath = SignVerifyRoundTripTests.TempPdfPath();
        try
        {
            SignVerifyRoundTripTests.CreateSampleDocument(sourcePath);
            using (var document = PdfDocument.Open(sourcePath))
            {
                document.Signatures.Add(signedPath, new PdfSignOptions
                {
                    Certificate = ca.LeafRsa,
                    AdditionalCertificates = [ca.Intermediate, ca.Root],
                });
            }

            using var signed = PdfDocument.Open(signedPath);
            var signature = signed.Signatures[0];

            // Opt-in never exercised: chain not evaluated, IsValid judges crypto + coverage only.
            var withoutAnchors = signature.Verify();
            Assert.Equal(SignatureChainStatus.NotEvaluated, withoutAnchors.ChainStatus);
            Assert.True(withoutAnchors.IsValid);

            // Right anchor: fully valid.
            var withRightAnchor = signature.Verify([ca.Root]);
            Assert.Equal(SignatureChainStatus.Trusted, withRightAnchor.ChainStatus);
            Assert.True(withRightAnchor.IsValid);

            // Wrong anchor: the caller asked for chain evaluation and it failed — IsValid must
            // come back false, never silently pass on crypto+coverage alone.
            var withWrongAnchor = signature.Verify([wrongCa.Root]);
            Assert.NotEqual(SignatureChainStatus.Trusted, withWrongAnchor.ChainStatus);
            Assert.NotEqual(SignatureChainStatus.NotEvaluated, withWrongAnchor.ChainStatus);
            Assert.False(withWrongAnchor.IsValid);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(signedPath);
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]          // loopback
    [InlineData("10.1.2.3")]           // RFC 1918
    [InlineData("172.16.0.9")]         // RFC 1918
    [InlineData("172.31.255.1")]       // RFC 1918 upper edge
    [InlineData("192.168.1.1")]        // RFC 1918
    [InlineData("169.254.169.254")]    // link-local — cloud metadata endpoint
    [InlineData("100.64.0.1")]         // CGNAT
    [InlineData("0.0.0.0")]            // zero network
    [InlineData("::1")]                // IPv6 loopback
    [InlineData("fe80::1")]            // IPv6 link-local
    [InlineData("fd00::1")]            // IPv6 ULA
    [InlineData("::ffff:10.0.0.1")]    // IPv4-mapped IPv6 must not bypass the IPv4 checks
    public void HttpRevocationFetcher_DefaultPolicy_BlocksInternalAddress(string address) =>
        Assert.True(HttpRevocationFetcher.IsBlockedAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("93.184.216.34")]      // public IPv4
    [InlineData("172.32.0.1")]         // just past the RFC 1918 172.16/12 range
    [InlineData("100.128.0.1")]        // just past CGNAT 100.64/10
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")] // public IPv6
    public void HttpRevocationFetcher_DefaultPolicy_AllowsPublicAddress(string address) =>
        Assert.False(HttpRevocationFetcher.IsBlockedAddress(IPAddress.Parse(address)));
}
