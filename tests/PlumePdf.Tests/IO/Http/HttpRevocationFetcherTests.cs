using System.Formats.Asn1;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.IO.Http;
using PlumePdf.Tests.Signing;
using Xunit;

namespace PlumePdf.Tests.IO.Http;

/// <summary>
/// Regression coverage for an SSRF bug against
/// <see cref="HttpRevocationFetcher"/>: AIA/CRL-Distribution-Point URIs come from a certificate
/// embedded in an untrusted PDF, so a non-<c>http</c>/<c>https</c> scheme must never reach
/// <see cref="System.Net.Http.HttpClient"/> at all — never a request, never an unhandled
/// <see cref="NotSupportedException"/> escaping the public API.
/// </summary>
public class HttpRevocationFetcherTests
{
    private const string AuthorityInfoAccessOid = "1.3.6.1.5.5.7.1.1";
    private const string OcspAccessMethodOid = "1.3.6.1.5.5.7.48.1";
    private const string CrlDistributionPointsOid = "2.5.29.31";

    [Fact]
    public async Task FetchOcspAsync_AiaUsesFileScheme_ReturnsNullWithoutIssuingAnyRequest()
    {
        using var issuer = CreateSelfSigned("CN=Test Issuer");
        using var certificate = CreateSelfSigned("CN=Test Leaf", BuildAiaExtension(OcspAccessMethodOid, "file:///etc/passwd"));
        using var handler = new ThrowIfInvokedHandler();
        using var fetcher = new HttpRevocationFetcher(handler: handler);

        var result = await fetcher.FetchOcspAsync(certificate, issuer);

        Assert.Null(result);
        Assert.False(handler.WasInvoked);
    }

    [Fact]
    public async Task FetchCrlAsync_DistributionPointUsesFtpScheme_ReturnsNullWithoutIssuingAnyRequest()
    {
        using var certificate = CreateSelfSigned("CN=Test Leaf", BuildCrlDistributionPointExtension("ftp://attacker.example/revoked.crl"));
        using var handler = new ThrowIfInvokedHandler();
        using var fetcher = new HttpRevocationFetcher(handler: handler);

        var result = await fetcher.FetchCrlAsync(certificate);

        Assert.Null(result);
        Assert.False(handler.WasInvoked);
    }

    [Fact]
    public async Task FetchOcspAsync_AiaUsesHttpsScheme_IssuesRequestNormally()
    {
        using var issuer = CreateSelfSigned("CN=Test Issuer");
        using var certificate = CreateSelfSigned("CN=Test Leaf", BuildAiaExtension(OcspAccessMethodOid, "https://ocsp.example.test/"));
        using var handler = new ThrowIfInvokedHandler();
        using var fetcher = new HttpRevocationFetcher(handler: handler);

        // The request itself fails (the stub handler always throws) — proving a well-formed
        // https:// AIA location DOES reach HttpClient, unlike the disallowed schemes above.
        await Assert.ThrowsAsync<PlumePdfException>(() => fetcher.FetchOcspAsync(certificate, issuer));
        Assert.True(handler.WasInvoked);
    }

    private static X509Extension BuildAiaExtension(string accessMethodOid, string uri)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // AuthorityInfoAccessSyntax ::= SEQUENCE OF AccessDescription
        {
            using (writer.PushSequence()) // AccessDescription
            {
                writer.WriteObjectIdentifier(accessMethodOid);
                writer.WriteCharacterString(UniversalTagNumber.IA5String, uri, new Asn1Tag(TagClass.ContextSpecific, 6, isConstructed: false));
            }
        }

        return new X509Extension(AuthorityInfoAccessOid, writer.Encode(), critical: false);
    }

    private static X509Extension BuildCrlDistributionPointExtension(string uri)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // CRLDistributionPoints ::= SEQUENCE OF DistributionPoint
        {
            using (writer.PushSequence()) // DistributionPoint
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) // distributionPoint [0] EXPLICIT
                {
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true))) // fullName [0] IMPLICIT GeneralNames
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, uri, new Asn1Tag(TagClass.ContextSpecific, 6, isConstructed: false));
                    }
                }
            }
        }

        return new X509Extension(CrlDistributionPointsOid, writer.Encode(), critical: false);
    }

    private static X509Certificate2 CreateSelfSigned(string subjectName, params X509Extension[] extensions)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        foreach (var extension in extensions)
        {
            request.CertificateExtensions.Add(extension);
        }

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return TestPfx.Load(certificate.Export(X509ContentType.Pfx));
    }

    /// <summary>An <see cref="HttpMessageHandler"/> that fails the test if a request is ever actually sent through it.</summary>
    private sealed class ThrowIfInvokedHandler : HttpMessageHandler
    {
        public bool WasInvoked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasInvoked = true;
            throw new HttpRequestException("Simulated unreachable responder.");
        }
    }
}
