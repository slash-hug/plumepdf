using System.Security.Cryptography.X509Certificates;

namespace PlumePdf;

/// <summary>
/// The seam OCSP/CRL revocation-material fetches go through, used when
/// assembling a document's <c>/DSS</c> (Document Security Store) for LTV. PlumePDF opens no
/// socket unless a caller supplies an implementation of this interface (or
/// <c>LtvOptions.RevocationFetcher</c>'s convenience configuration, which constructs
/// <c>IO.Http.HttpRevocationFetcher</c> internally) — offline by default, network strictly
/// opt-in, mirroring <see cref="ITimestampAuthority"/>.
/// </summary>
public interface IRevocationFetcher
{
    /// <summary>
    /// Fetches a fresh OCSP response for <paramref name="certificate"/>, issued by
    /// <paramref name="issuer"/>.
    /// </summary>
    /// <param name="certificate">The certificate whose revocation status is being checked.</param>
    /// <param name="issuer">The certificate that issued <paramref name="certificate"/> — needed to build the OCSP request's <c>CertID</c> (issuer name hash + issuer key hash + serial number).</param>
    /// <param name="cancellationToken">Propagated to the underlying HTTP request.</param>
    /// <returns>
    /// The DER-encoded OCSP <c>BasicOCSPResponse</c> bytes, or <see langword="null"/> when
    /// <paramref name="certificate"/> carries no Authority Information Access OCSP locator (a
    /// certificate with no reachable revocation source is not itself an error — the caller
    /// decides whether that's acceptable for the LTV level being built).
    /// </returns>
    /// <exception cref="PlumePdfException">The responder is reachable but returns a malformed or non-success OCSP response.</exception>
    Task<byte[]?> FetchOcspAsync(X509Certificate2 certificate, X509Certificate2 issuer, CancellationToken cancellationToken = default);

    /// <summary>Fetches the current CRL covering <paramref name="certificate"/>.</summary>
    /// <param name="certificate">The certificate whose CRL Distribution Point is followed.</param>
    /// <param name="cancellationToken">Propagated to the underlying HTTP request.</param>
    /// <returns>
    /// The DER-encoded <c>CertificateList</c> bytes, or <see langword="null"/> when
    /// <paramref name="certificate"/> carries no CRL Distribution Point extension.
    /// </returns>
    /// <exception cref="PlumePdfException">The distribution point is reachable but returns a malformed CRL.</exception>
    Task<byte[]?> FetchCrlAsync(X509Certificate2 certificate, CancellationToken cancellationToken = default);
}
