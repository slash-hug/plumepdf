using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace PlumePdf.IO.Http;

/// <summary>
/// The default <see cref="ITimestampAuthority"/>: an RFC 3161-over-HTTP client (RFC 3161
/// §3.4). Never constructed by PlumePDF on a caller's behalf — the offline-by-default
/// stance means a caller must explicitly opt into network timestamping by constructing this
/// (or a <c>PdfSignOptions.TimestampAuthorityUrl</c> convenience property that does so
/// internally).
/// </summary>
/// <remarks>
/// Every step here is genuinely asynchronous down to the socket (no <c>Task.Run</c>
/// wrapping a blocking call) — this is the phase's first true-async member. The gating CI
/// test lane never exercises this class over a live network; it uses
/// <c>PlumePdf.Tests.Signing.FakeTimestampAuthority</c> for hermeticity, and unit tests here
/// use an injected <see cref="HttpMessageHandler"/> stub instead of a real endpoint.
/// </remarks>
public sealed class HttpTimestampAuthority : ITimestampAuthority, IDisposable
{
    private const long DefaultMaxResponseBytes = 1_000_000;
    private static readonly MediaTypeHeaderValue RequestContentType = new("application/timestamp-query");

    private readonly HttpClient _httpClient;
    private readonly long _maxResponseBytes;

    /// <summary>The TSA endpoint every request is POSTed to.</summary>
    public Uri Endpoint { get; }

    /// <summary>Constructs an HTTP-backed RFC 3161 client.</summary>
    /// <param name="endpoint">The TSA's HTTP(S) endpoint, e.g. a public TSA's <c>/tsr</c> URL.</param>
    /// <param name="timeout">The request timeout. Defaults to 30 seconds.</param>
    /// <param name="maxResponseBytes">Cap on the TSA response size, enforced while streaming the response — a malicious or misbehaving TSA cannot force an unbounded read. Defaults to 1,000,000 bytes.</param>
    /// <param name="handler">An <see cref="HttpMessageHandler"/> to route requests through — for tests, a stub that never touches the network; <see langword="null"/> uses the BCL default.</param>
    public HttpTimestampAuthority(Uri endpoint, TimeSpan? timeout = null, long maxResponseBytes = DefaultMaxResponseBytes, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (maxResponseBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes), maxResponseBytes, "Must be positive.");
        }

        Endpoint = endpoint;
        _maxResponseBytes = maxResponseBytes;
        _httpClient = handler is not null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _httpClient.Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc/>
    /// <exception cref="PlumePdfException">
    /// The endpoint is unreachable or times out, returns a non-success status, a response
    /// larger than the configured cap (<c>PLUME4013</c>), or a response that does not answer
    /// this request (<c>PLUME4012</c>).
    /// </exception>
    public async Task<byte[]> GetTimestampAsync(ReadOnlyMemory<byte> messageImprint, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default)
    {
        Rfc3161TimestampRequest request;
        try
        {
            request = Rfc3161TimestampRequest.CreateFromHash(messageImprint, hashAlgorithm, requestedPolicyId: null, nonce: RandomNumberGenerator.GetBytes(16), requestSignerCertificates: true);
        }
        catch (CryptographicException ex)
        {
            throw new PlumePdfException("PLUME4012", $"Could not build an RFC 3161 timestamp request: {ex.Message}", ex);
        }

        using var requestContent = new ByteArrayContent(request.Encode());
        requestContent.Headers.ContentType = RequestContentType;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(Endpoint, requestContent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new PlumePdfException("PLUME4012", $"Timestamp authority request to '{Endpoint}' failed: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PlumePdfException("PLUME4012", $"Timestamp authority '{Endpoint}' returned HTTP {(int)response.StatusCode}.");
            }

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is > 0 && declaredLength > _maxResponseBytes)
            {
                throw new PlumePdfException("PLUME4013", $"Timestamp authority response declares {declaredLength} bytes, exceeding the configured cap of {_maxResponseBytes}.");
            }

            var responseBytes = await HttpCappedReader.ReadAsync(response.Content, _maxResponseBytes, "Timestamp authority response", cancellationToken).ConfigureAwait(false);

            Rfc3161TimestampToken token;
            try
            {
                token = request.ProcessResponse(responseBytes, out _);
            }
            catch (CryptographicException ex)
            {
                throw new PlumePdfException("PLUME4012", $"Timestamp authority '{Endpoint}' returned a response that does not answer this request: {ex.Message}", ex);
            }

            return token.AsSignedCms().Encode();
        }
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/>.</summary>
    public void Dispose() => _httpClient.Dispose();
}

/// <summary>
/// Streams an <see cref="HttpContent"/> body into memory while enforcing a byte cap as it
/// reads — shared by <see cref="HttpTimestampAuthority"/> and <see cref="HttpRevocationFetcher"/>
/// so neither has to trust a response's (attacker-controllable, and sometimes simply absent)
/// <c>Content-Length</c> header alone (an untrusted network peer must not be able to
/// force an unbounded read).
/// </summary>
internal static class HttpCappedReader
{
    private const int ChunkSize = 8192;

    public static async Task<byte[]> ReadAsync(HttpContent content, long maxBytes, string subjectForErrorMessage, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new PlumePdfException("PLUME4013", $"{subjectForErrorMessage} exceeded the configured cap of {maxBytes} bytes while streaming.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
