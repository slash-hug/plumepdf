using System.Formats.Asn1;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PlumePdf.IO.Http;

/// <summary>
/// The default <see cref="IRevocationFetcher"/>: an OCSP/CRL-over-HTTP client. Never
/// constructed by PlumePDF on a caller's behalf — mirrors <see cref="HttpTimestampAuthority"/>'s
/// opt-in-only stance.
/// </summary>
/// <remarks>
/// <see cref="System.Security.Cryptography.Pkcs"/> has no typed OCSP request/response classes
/// (unlike its RFC 3161 support), so the OCSP request (RFC 6960 §4.1.1) is hand-assembled via
/// <see cref="System.Formats.Asn1"/> the same way <c>Objects.Signing.CmsSignatureBuilder</c>
/// assembles a CMS <c>SignerInfo</c> — mechanical DER structure only, no cryptographic
/// primitives of our own (the request here is unsigned, the common case for a relying party's
/// OCSP query). This type returns raw, as-received response bytes rather than DER-normalizing
/// them itself: <c>Objects.Signing</c> sits above <c>IO</c> in the layer order
/// (docs/architecture.md), so normalization happens one layer up, in
/// <c>CertificateChainResolver.CollectRevocationMaterialAsync</c>.
/// </remarks>
public sealed class HttpRevocationFetcher : IRevocationFetcher, IDisposable
{
    private const long DefaultMaxResponseBytes = 1_000_000;

    // RFC 6960 uses SHA-1 for CertID's issuerNameHash/issuerKeyHash by long-standing
    // convention; most deployed OCSP responders still expect it regardless of the PAdES
    // signature's own (SHA-256+) digest algorithm — an unrelated, protocol-level choice.
    private const string CertIdHashAlgorithmOid = "1.3.14.3.2.26";

    private const string OcspAccessMethodOid = "1.3.6.1.5.5.7.48.1";
    private const string AuthorityInfoAccessExtensionOid = "1.3.6.1.5.5.7.1.1";
    private const string CrlDistributionPointsExtensionOid = "2.5.29.31";

    private static readonly MediaTypeHeaderValue OcspRequestContentType = new("application/ocsp-request");

    private readonly HttpClient _httpClient;
    private readonly long _maxResponseBytes;

    /// <summary>Constructs an HTTP-backed OCSP/CRL client.</summary>
    /// <param name="timeout">The request timeout. Defaults to 30 seconds.</param>
    /// <param name="maxResponseBytes">Cap on any single OCSP response or CRL, enforced while streaming. Defaults to 1,000,000 bytes.</param>
    /// <param name="handler">
    /// An <see cref="HttpMessageHandler"/> to route requests through — for tests, a stub that
    /// never touches the network; <see langword="null"/> uses the BCL default with automatic
    /// redirect-following disabled and the private-address policy below. When a handler is
    /// supplied, its own redirect behavior and address policy are used as-is — callers wiring up
    /// their own handler are responsible for both choices.
    /// </param>
    /// <param name="allowPrivateNetworkTargets">
    /// Opt-out for the default handler's address policy, for the rare deployment whose OCSP
    /// responder or CRL distribution point legitimately lives on a private network. Leave
    /// <see langword="false"/> unless that describes your infrastructure. Ignored when
    /// <paramref name="handler"/> is supplied.
    /// </param>
    /// <remarks>
    /// AIA/CRL-Distribution-Point URIs come from certificates embedded in an untrusted PDF, so
    /// they are hostile input, not configuration. Three layers of SSRF hardening apply:
    /// <see cref="TryReadGeneralNameUri"/> accepts only <c>http</c>/<c>https</c> schemes (never
    /// <c>file</c>/<c>ftp</c>/anything else a document could smuggle in to reach an unintended
    /// URI handler); the default handler never follows a redirect; and the default handler
    /// rejects loopback, link-local (including cloud metadata endpoints such as
    /// <c>169.254.169.254</c>), and private-range (RFC 1918/4193, CGNAT) targets — enforced
    /// <em>after</em> DNS resolution via a connect callback that dials only the addresses it
    /// just validated, so a hostile certificate can neither name an internal address directly
    /// nor reach one through a DNS-rebinding hostname. A blocked target surfaces as the same
    /// coded <c>PLUME4011</c> failure as any other unreachable responder.
    /// </remarks>
    public HttpRevocationFetcher(TimeSpan? timeout = null, long maxResponseBytes = DefaultMaxResponseBytes, HttpMessageHandler? handler = null, bool allowPrivateNetworkTargets = false)
    {
        if (maxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes), maxResponseBytes, "Must be positive.");
        }

        _maxResponseBytes = maxResponseBytes;
        _httpClient = handler is not null ? new HttpClient(handler, disposeHandler: false) : new HttpClient(CreateDefaultHandler(allowPrivateNetworkTargets));
        _httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    private static SocketsHttpHandler CreateDefaultHandler(bool allowPrivateNetworkTargets)
    {
        var defaultHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
        if (allowPrivateNetworkTargets)
        {
            return defaultHandler;
        }

        defaultHandler.ConnectCallback = static async (context, cancellationToken) =>
        {
            // Resolve here, validate every resolved address, then connect to exactly the
            // addresses just validated — never re-resolving — so a rebinding DNS name can't
            // pass validation with one answer and connect with another.
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

            foreach (var address in addresses)
            {
                if (IsBlockedAddress(address))
                {
                    throw new HttpRequestException($"Revocation endpoint '{host}' resolves to '{address}', a loopback/link-local/private address — refused (certificate-supplied URIs are untrusted input; see HttpRevocationFetcher's allowPrivateNetworkTargets).");
                }
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        return defaultHandler;
    }

    /// <summary>
    /// The default address policy: loopback, IPv4 link-local (<c>169.254/16</c> — cloud metadata
    /// endpoints live here), RFC 1918 private ranges, CGNAT (<c>100.64/10</c>), the zero
    /// network, IPv6 link-local/site-local, and IPv6 ULA (<c>fc00::/7</c>) are all rejected.
    /// IPv4-mapped IPv6 addresses are unmapped first so <c>::ffff:10.0.0.1</c> can't slip past
    /// the IPv4 checks.
    /// </summary>
    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var octets = address.GetAddressBytes();
            return octets[0] == 0
                || octets[0] == 10
                || (octets[0] == 100 && (octets[1] & 0xC0) == 64)
                || (octets[0] == 169 && octets[1] == 254)
                || (octets[0] == 172 && (octets[1] & 0xF0) == 16)
                || (octets[0] == 192 && octets[1] == 168);
        }

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }

    /// <inheritdoc/>
    /// <exception cref="PlumePdfException">
    /// The responder is reachable but returns a non-success status, an unsuccessful OCSP
    /// response, or a response larger than the configured cap (<c>PLUME4013</c>).
    /// </exception>
    public async Task<byte[]?> FetchOcspAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(issuer);

        var ocspUrl = TryFindAccessLocationUri(certificate, AuthorityInfoAccessExtensionOid, OcspAccessMethodOid);
        if (ocspUrl is null)
        {
            return null;
        }

        var requestBytes = BuildOcspRequest(certificate, issuer);
        using var requestContent = new ByteArrayContent(requestBytes);
        requestContent.Headers.ContentType = OcspRequestContentType;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(ocspUrl, requestContent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            throw new PlumePdfException("PLUME4011", $"OCSP request to '{ocspUrl}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PlumePdfException("PLUME4011", $"OCSP responder '{ocspUrl}' returned HTTP {(int)response.StatusCode}.");
            }

            var responseBytes = await HttpCappedReader.ReadAsync(response.Content, _maxResponseBytes, "OCSP response", cancellationToken).ConfigureAwait(false);
            // Unwrapped, as-received bytes — DER normalization is Objects.Signing's concern
            // (CertificateChainResolver.CollectRevocationMaterialAsync applies it to whatever
            // this returns), never IO's: the IO layer sits below Objects and must not
            // reference it (docs/architecture.md's layer order).
            return ExtractBasicOcspResponse(responseBytes)
                ?? throw new PlumePdfException("PLUME4011", $"OCSP responder '{ocspUrl}' did not return a successful response.");
        }
    }

    /// <inheritdoc/>
    /// <exception cref="PlumePdfException">
    /// The distribution point is reachable but returns a non-success status, or a response
    /// larger than the configured cap (<c>PLUME4013</c>).
    /// </exception>
    public async Task<byte[]?> FetchCrlAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var crlUrl = TryFindCrlDistributionPointUri(certificate);
        if (crlUrl is null)
        {
            return null;
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(crlUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            throw new PlumePdfException("PLUME4011", $"CRL request to '{crlUrl}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PlumePdfException("PLUME4011", $"CRL distribution point '{crlUrl}' returned HTTP {(int)response.StatusCode}.");
            }

            // As-received bytes — see FetchOcspAsync's remark on why normalization happens one
            // layer up, not here.
            return await HttpCappedReader.ReadAsync(response.Content, _maxResponseBytes, "CRL response", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/>.</summary>
    public void Dispose() => _httpClient.Dispose();

    // --- OCSP request assembly (RFC 6960 §4.1.1) ---

    private static byte[] BuildOcspRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        var issuerNameHash = SHA1.HashData(issuer.SubjectName.RawData);
        var issuerKeyHash = SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData);
        var serialNumber = new BigInteger(certificate.GetSerialNumber(), isUnsigned: true, isBigEndian: false);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence()) // OCSPRequest
        using (writer.PushSequence()) // tbsRequest: TBSRequest
        using (writer.PushSequence()) // requestList: SEQUENCE OF Request
        using (writer.PushSequence()) // Request
        using (writer.PushSequence()) // reqCert: CertID
        {
            using (writer.PushSequence()) // hashAlgorithm
            {
                writer.WriteObjectIdentifier(CertIdHashAlgorithmOid);
                writer.WriteNull();
            }

            writer.WriteOctetString(issuerNameHash);
            writer.WriteOctetString(issuerKeyHash);
            writer.WriteInteger(serialNumber);
        }

        return writer.Encode();
    }

    private static byte[]? ExtractBasicOcspResponse(byte[] ocspResponseDer)
    {
        try
        {
            var reader = new AsnReader(ocspResponseDer, AsnEncodingRules.BER);
            var outer = reader.ReadSequence(); // OCSPResponse
            var status = outer.ReadEnumeratedBytes(); // responseStatus
            if (status.Length != 1 || status.Span[0] != 0) // 0 = successful
            {
                return null;
            }

            if (!outer.HasData)
            {
                return null;
            }

            var responseBytesWrapper = outer.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true)); // [0] EXPLICIT ResponseBytes
            var responseBytes = responseBytesWrapper.ReadSequence(); // ResponseBytes
            responseBytes.ReadObjectIdentifier(); // responseType (id-pkix-ocsp-basic) — trusted implicitly; caller only wants the payload.
            return responseBytes.ReadOctetString(); // response: the BasicOCSPResponse DER bytes
        }
        catch (AsnContentException)
        {
            return null;
        }
    }

    // --- Certificate extension parsing (Authority Information Access / CRL Distribution Points) ---

    private static Uri? TryFindAccessLocationUri(X509Certificate2 certificate, string extensionOid, string accessMethodOid)
    {
        var extension = certificate.Extensions[extensionOid];
        if (extension is null)
        {
            return null;
        }

        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.BER);
            var accessDescriptions = reader.ReadSequence(); // AuthorityInfoAccessSyntax
            while (accessDescriptions.HasData)
            {
                var accessDescription = accessDescriptions.ReadSequence();
                var method = accessDescription.ReadObjectIdentifier();
                var uri = TryReadGeneralNameUri(accessDescription);
                if (method == accessMethodOid && uri is not null)
                {
                    return uri;
                }
            }
        }
        catch (AsnContentException)
        {
            return null;
        }

        return null;
    }

    private static Uri? TryFindCrlDistributionPointUri(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions[CrlDistributionPointsExtensionOid];
        if (extension is null)
        {
            return null;
        }

        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.BER);
            var distributionPoints = reader.ReadSequence(); // CRLDistributionPoints
            while (distributionPoints.HasData)
            {
                var distributionPoint = distributionPoints.ReadSequence();
                if (!distributionPoint.HasData)
                {
                    continue;
                }

                var tag = distributionPoint.PeekTag();
                if (tag.TagClass != TagClass.ContextSpecific || tag.TagValue != 0)
                {
                    continue; // no distributionPoint field on this entry — try the next.
                }

                // distributionPoint [0] DistributionPointName — EXPLICIT (DistributionPointName
                // is a CHOICE, which cannot be tagged IMPLICIT), so unwrap once...
                var nameWrapper = distributionPoint.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                if (!nameWrapper.HasData)
                {
                    continue;
                }

                var nameTag = nameWrapper.PeekTag();
                if (nameTag.TagClass != TagClass.ContextSpecific || nameTag.TagValue != 0)
                {
                    continue; // fullName [0] GeneralNames only — nameRelativeToCRLIssuer [1] is not supported.
                }

                // ...then fullName [0] GeneralNames is itself IMPLICIT-tagged (GeneralNames is
                // a SEQUENCE OF, which can be tagged implicitly).
                var generalNames = nameWrapper.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
                var uri = TryReadGeneralNameUri(generalNames);
                if (uri is not null)
                {
                    return uri;
                }
            }
        }
        catch (AsnContentException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Scans a <c>GeneralNames</c> (or a single already-positioned <c>AccessDescription</c>
    /// reader) for the first <c>uniformResourceIdentifier [6]</c> choice and parses it as a
    /// <see cref="Uri"/> — only when its scheme is <c>http</c> or <c>https</c>. AIA/CRL
    /// Distribution Point URIs come from a certificate embedded in an untrusted PDF: an absolute
    /// URI with any other scheme (<c>file</c>, <c>ftp</c>, a custom handler, ...) is document-
    /// controlled input this fetcher must never hand to <see cref="HttpClient"/>, so it is
    /// treated the same as "no location present" here rather than passed through.
    /// </summary>
    private static Uri? TryReadGeneralNameUri(AsnReader reader)
    {
        while (reader.HasData)
        {
            var tag = reader.PeekTag();
            if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 6 && !tag.IsConstructed)
            {
                var raw = reader.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 6, isConstructed: false));
                var text = System.Text.Encoding.ASCII.GetString(raw);
                if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                {
                    return null;
                }

                return uri.Scheme is "http" or "https" ? uri : null;
            }

            // Not the choice we're looking for — skip past it (primitive or constructed) and keep scanning.
            reader.ReadEncodedValue();
        }

        return null;
    }
}
