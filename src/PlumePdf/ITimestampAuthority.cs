using System.Security.Cryptography;

namespace PlumePdf;

/// <summary>
/// The seam an RFC 3161 timestamp request goes through. PlumePDF opens no
/// socket unless a caller supplies an implementation of this interface (or the
/// <c>PdfSignOptions.TimestampAuthorityUrl</c> convenience property, which constructs
/// <c>IO.Http.HttpTimestampAuthority</c> internally) — offline by default, network strictly
/// opt-in.
/// </summary>
/// <remarks>
/// Gating CI never calls a live TSA: the hermetic test lane uses an in-process fake built over
/// <see cref="System.Security.Cryptography.Pkcs.Rfc3161TimestampToken"/> (see
/// <c>PlumePdf.Tests.Signing.FakeTimestampAuthority</c>). A real network round trip against a
/// public TSA is exercised only in a non-gating, maintainer-run lane.
/// </remarks>
public interface ITimestampAuthority
{
    /// <summary>
    /// Requests an RFC 3161 timestamp token over <paramref name="messageImprint"/> — the hash
    /// of the value being timestamped (for PAdES B-T, the CMS <c>SignerInfo.signature</c>
    /// octets), already hashed with <paramref name="hashAlgorithm"/>. Implementations never
    /// see the un-hashed value.
    /// </summary>
    /// <param name="messageImprint">The precomputed hash to timestamp.</param>
    /// <param name="hashAlgorithm">The algorithm <paramref name="messageImprint"/> was hashed with — reported to the TSA as the request's <c>messageImprint.hashAlgorithm</c>.</param>
    /// <param name="cancellationToken">Propagated to the underlying request (typically an HTTP call).</param>
    /// <returns>
    /// The DER-encoded RFC 3161 <c>TimeStampToken</c> (a CMS <c>ContentInfo</c> wrapping a
    /// <c>SignedData</c> over the TSA's <c>TSTInfo</c>) exactly as returned by the TSA, ready
    /// to be DER-normalized (<c>Objects.Signing.DerNormalizer</c>) and embedded as the CMS
    /// <c>id-aa-signatureTimeStampToken</c> unsigned attribute.
    /// </returns>
    /// <exception cref="PlumePdfException">
    /// The TSA is unreachable, times out, refuses the request, or returns a token that does
    /// not cover <paramref name="messageImprint"/> — a stable <c>PLUME4xxx</c> code (network
    /// and transport failures share the widened "Encryption &amp; signing" range).
    /// </exception>
    Task<byte[]> GetTimestampAsync(ReadOnlyMemory<byte> messageImprint, HashAlgorithmName hashAlgorithm, CancellationToken cancellationToken = default);
}
