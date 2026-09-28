using System.Security.Cryptography.X509Certificates;

namespace PlumePdf.Objects.Signing;

/// <summary>The verdict <see cref="CertificateChainResolver.BuildChain"/> reaches for a certificate chain.</summary>
internal enum ChainBuildOutcome
{
    /// <summary>The chain builds to a trusted root with no status flags.</summary>
    Trusted,

    /// <summary>The chain builds, but its root is not among the supplied trusted anchors.</summary>
    UntrustedRoot,

    /// <summary>At least one certificate in the chain is expired or not yet valid at the verification time.</summary>
    NotTimeValid,

    /// <summary>At least one certificate in the chain is revoked (only surfaces when revocation checking was supplied — this resolver itself never calls out to a revocation source; see <see cref="CertificateChainResolver.CollectRevocationMaterialAsync"/>).</summary>
    Revoked,

    /// <summary>No path to any root could be built — an intermediate or root is missing.</summary>
    PartialChain,

    /// <summary>The chain failed to build for a reason not covered by the other outcomes; inspect <see cref="ChainBuildResult.StatusFlags"/>.</summary>
    Error,
}

/// <summary>The result of <see cref="CertificateChainResolver.BuildChain"/>.</summary>
/// <param name="Outcome">The overall verdict.</param>
/// <param name="Chain">The built chain, leaf-first, when a path could be constructed at all (even an untrusted or expired one) — empty only when nothing could be built.</param>
/// <param name="StatusFlags">Every <see cref="X509ChainStatus"/> the BCL's <see cref="X509Chain"/> reported, for detail beyond the collapsed <see cref="Outcome"/>.</param>
internal sealed record ChainBuildResult(ChainBuildOutcome Outcome, IReadOnlyList<X509Certificate2> Chain, IReadOnlyList<X509ChainStatus> StatusFlags);

/// <summary>Revocation material collected for a chain's non-root certificates, ready to embed in a document's <c>/DSS</c>.</summary>
/// <param name="OcspResponses">DER-normalized <c>BasicOCSPResponse</c> bytes, one per certificate an OCSP responder answered for.</param>
/// <param name="Crls">DER-normalized <c>CertificateList</c> (CRL) bytes, one per certificate that had no OCSP answer but did have a CRL Distribution Point.</param>
internal sealed record RevocationMaterial(IReadOnlyList<byte[]> OcspResponses, IReadOnlyList<byte[]> Crls);

/// <summary>
/// Builds a certificate chain offline (<see cref="X509Chain"/> never makes
/// its own network call here; <see cref="X509RevocationMode.NoCheck"/> is always set) against
/// an injected extra-certificate store and, optionally, an injected trust anchor set (so tests
/// can verify against <c>TestCertificateAuthority</c>'s root without touching the OS trust
/// store), and assembles OCSP/CRL revocation material for <c>/DSS</c> via
/// <see cref="IRevocationFetcher"/>.
/// </summary>
internal static class CertificateChainResolver
{
    /// <summary>Builds the chain for <paramref name="leaf"/>.</summary>
    /// <param name="leaf">The end-entity certificate to build a chain for.</param>
    /// <param name="extraCertificates">Intermediates (and, optionally, the root) to consider — typically the CMS's own embedded certificates.</param>
    /// <param name="trustedRoots">
    /// Roots to trust for this build, in place of the OS trust store. <see langword="null"/> or
    /// empty falls back to <see cref="X509Chain"/>'s default (the OS trust store) — pass the
    /// document's own configured trust anchors, or a test CA's root, explicitly rather than
    /// relying on that default in production code.
    /// </param>
    /// <param name="validationTime">The instant to validate against — the signing time (or the signature's RFC 3161 timestamp) for a signature verification, or <see langword="null"/> for "now".</param>
    /// <param name="maxChainDepth">Cap: refuses a chain longer than this, guarding against a maliciously oversized certificate chain.</param>
    /// <exception cref="PlumePdfException">The built chain is longer than <paramref name="maxChainDepth"/> (<c>PLUME4013</c>).</exception>
    public static ChainBuildResult BuildChain(
        X509Certificate2 leaf,
        IReadOnlyList<X509Certificate2>? extraCertificates = null,
        IReadOnlyList<X509Certificate2>? trustedRoots = null,
        DateTime? validationTime = null,
        int maxChainDepth = 10)
    {
        ArgumentNullException.ThrowIfNull(leaf);

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.Zero;
        chain.ChainPolicy.VerificationTime = validationTime ?? DateTime.Now;

        if (extraCertificates is { Count: > 0 })
        {
            chain.ChainPolicy.ExtraStore.AddRange(extraCertificates.ToArray());
        }

        if (trustedRoots is { Count: > 0 })
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots.ToArray());
        }

        var built = chain.Build(leaf);
        var chainCertificates = chain.ChainElements.Select(element => element.Certificate).ToArray();

        if (chainCertificates.Length > maxChainDepth)
        {
            throw new PlumePdfException("PLUME4013", $"Certificate chain depth {chainCertificates.Length} exceeds the configured cap of {maxChainDepth}.");
        }

        var statusFlags = chain.ChainStatus;
        var combined = X509ChainStatusFlags.NoError;
        foreach (var status in statusFlags)
        {
            combined |= status.Status;
        }

        var outcome = ClassifyOutcome(built, combined);
        return new ChainBuildResult(outcome, chainCertificates, statusFlags);
    }

    private static ChainBuildOutcome ClassifyOutcome(bool built, X509ChainStatusFlags flags)
    {
        if (built)
        {
            return ChainBuildOutcome.Trusted;
        }

        if (flags.HasFlag(X509ChainStatusFlags.Revoked))
        {
            return ChainBuildOutcome.Revoked;
        }

        if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid))
        {
            return ChainBuildOutcome.NotTimeValid;
        }

        if (flags.HasFlag(X509ChainStatusFlags.PartialChain))
        {
            return ChainBuildOutcome.PartialChain;
        }

        if (flags.HasFlag(X509ChainStatusFlags.UntrustedRoot))
        {
            return ChainBuildOutcome.UntrustedRoot;
        }

        return ChainBuildOutcome.Error;
    }

    /// <summary>
    /// Fetches and DER-normalizes OCSP responses (falling back to a CRL when no OCSP answer is
    /// available) for every non-root certificate in <paramref name="chain"/>, for embedding in
    /// a document's <c>/DSS</c>.
    /// </summary>
    /// <param name="chain">A leaf-first chain, e.g. from <see cref="BuildChain"/>'s <see cref="ChainBuildResult.Chain"/>.</param>
    /// <param name="fetcher">The seam the actual OCSP/CRL requests go through — never called unless the caller supplies one.</param>
    /// <param name="maxCertificates">Cap on <paramref name="chain"/>'s length before any fetch is attempted.</param>
    /// <param name="maxResponseBytes">Cap on any single OCSP response or CRL's size.</param>
    /// <param name="perCallTimeout">
    /// Cap on each individual <see cref="IRevocationFetcher"/> call (<see cref="PdfOptions.RevocationTimeout"/>),
    /// enforced here regardless of whatever timeout <paramref name="fetcher"/>'s own
    /// implementation may or may not apply internally — a defense-in-depth guard against a slow
    /// or hostile responder hanging an LTV call indefinitely even when the caller supplied a
    /// custom <see cref="IRevocationFetcher"/> with no timeout of its own. <see langword="null"/>
    /// applies no additional bound beyond <paramref name="cancellationToken"/>.
    /// </param>
    /// <param name="cancellationToken">Propagated to every fetch.</param>
    /// <exception cref="PlumePdfException">
    /// <paramref name="chain"/> is longer than <paramref name="maxCertificates"/>, or a fetched
    /// response exceeds <paramref name="maxResponseBytes"/> (both <c>PLUME4013</c>); or a single
    /// fetch call ran longer than <paramref name="perCallTimeout"/> (<c>PLUME4011</c>).
    /// </exception>
    public static async Task<RevocationMaterial> CollectRevocationMaterialAsync(
        IReadOnlyList<X509Certificate2> chain,
        IRevocationFetcher fetcher,
        int maxCertificates = 32,
        long maxResponseBytes = 1_000_000,
        TimeSpan? perCallTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(fetcher);

        if (chain.Count > maxCertificates)
        {
            throw new PlumePdfException("PLUME4013", $"Chain has {chain.Count} certificates, exceeding the configured cap of {maxCertificates} for revocation-material collection.");
        }

        var ocspResponses = new List<byte[]>();
        var crls = new List<byte[]>();

        // The last element is the root, which has no issuer to check it against and is trusted
        // directly by policy rather than by revocation status — every certificate above it
        // (leaf through the top intermediate) is checked against the certificate one position
        // higher in the chain, its issuer.
        for (var i = 0; i < chain.Count - 1; i++)
        {
            var certificate = chain[i];
            var issuer = chain[i + 1];
            cancellationToken.ThrowIfCancellationRequested();

            var ocsp = await FetchWithTimeoutAsync(ct => fetcher.FetchOcspAsync(certificate, issuer, ct), perCallTimeout, "OCSP", cancellationToken).ConfigureAwait(false);
            if (ocsp is not null)
            {
                if (ocsp.Length > maxResponseBytes)
                {
                    throw new PlumePdfException("PLUME4013", $"OCSP response for '{certificate.Subject}' is {ocsp.Length} bytes, exceeding the configured cap of {maxResponseBytes}.");
                }

                ocspResponses.Add(DerNormalizer.Normalize(ocsp));
                continue;
            }

            var crl = await FetchWithTimeoutAsync(ct => fetcher.FetchCrlAsync(certificate, ct), perCallTimeout, "CRL", cancellationToken).ConfigureAwait(false);
            if (crl is not null)
            {
                if (crl.Length > maxResponseBytes)
                {
                    throw new PlumePdfException("PLUME4013", $"CRL for '{certificate.Subject}' is {crl.Length} bytes, exceeding the configured cap of {maxResponseBytes}.");
                }

                crls.Add(DerNormalizer.Normalize(crl));
            }
        }

        return new RevocationMaterial(ocspResponses, crls);
    }

    /// <summary>
    /// Invokes <paramref name="fetch"/> with a token that also cancels after
    /// <paramref name="perCallTimeout"/>, translating a cancellation caused by that timer (as
    /// opposed to <paramref name="cancellationToken"/> itself firing) into a coded
    /// <c>PLUME4011</c> — an untrusted-network-boundary timeout is a document/IO failure
    /// PlumePDF must report through its own exception policy, not a bare
    /// <see cref="OperationCanceledException"/> escaping a public API path.
    /// </summary>
    private static async Task<byte[]?> FetchWithTimeoutAsync(Func<CancellationToken, Task<byte[]?>> fetch, TimeSpan? perCallTimeout, string kind, CancellationToken cancellationToken)
    {
        if (perCallTimeout is not { } timeout)
        {
            return await fetch(cancellationToken).ConfigureAwait(false);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            return await fetch(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlumePdfException("PLUME4011", $"{kind} request timed out after {timeout} (PdfOptions.RevocationTimeout).");
        }
    }
}
