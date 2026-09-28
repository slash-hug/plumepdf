using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using PlumePdf.Objects.Signing;

namespace PlumePdf.Documents.Signing;

/// <summary>
/// Assembles and writes a document's <c>/DSS</c> (Document Security Store, ISO 32000-2 §12.8.4.3)
/// as one incremental revision (Task C4, B-LT maintenance): every embedded certificate across
/// every existing signature/document-timestamp, plus OCSP/CRL revocation material fetched
/// through the caller's <see cref="IRevocationFetcher"/>.
/// </summary>
/// <remarks>
/// Scope note: this writes a fresh <c>/Certs</c>/<c>/OCSPs</c>/<c>/CRLs</c> triad each call
/// (replacing, not merging with, any prior <c>/DSS</c>) and does not populate per-signature
/// <c>/VRI</c> entries — both are optional richness a validator may use as a hint;
/// <c>/DSS</c>'s own arrays are what LTV actually requires. Reuses <see cref="PdfDocument.SaveIncremental(string,PdfOptions?)"/>
/// for the actual write — a <c>/DSS</c> revision needs no <see cref="Objects.SigningWriteSession"/>
/// placeholder dance (unlike a signature or document timestamp), since nothing about the DSS's
/// own bytes is itself hashed/signed.
/// </remarks>
internal static class DssWriter
{
    /// <summary>
    /// Adds LTV material for every existing signature/timestamp in <paramref name="document"/>
    /// and saves the result to <paramref name="outputPath"/>.
    /// </summary>
    /// <exception cref="PlumePdfException">This document carries no signature to build LTV material for (<c>PLUME6050</c>).</exception>
    public static async Task AddLtvAsync(PdfDocument document, string outputPath, IRevocationFetcher fetcher, IReadOnlyList<X509Certificate2>? trustedRoots, PdfOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(fetcher);

        var effectiveOptions = options ?? document.Options;
        SigningOrchestrator.RefuseDeterministicMaintenance(effectiveOptions, "AddLtvAsync");
        var signatures = SignatureDictionaryReader.ReadAll(document);
        if (signatures.Count == 0)
        {
            throw new PlumePdfException("PLUME6050", "AddLtvAsync requires at least one existing signature to build LTV material for — sign the document first.");
        }

        var certificatesByThumbprint = new Dictionary<string, X509Certificate2>();
        var ocspResponses = new List<byte[]>();
        var crls = new List<byte[]>();

        foreach (var signature in signatures)
        {
            var (leaf, embedded) = DecodeCertificates(signature.Contents);
            if (leaf is null)
            {
                continue;
            }

            foreach (var certificate in embedded)
            {
                certificatesByThumbprint[certificate.Thumbprint] = certificate;
            }

            var chain = CertificateChainResolver.BuildChain(leaf, embedded, trustedRoots, validationTime: DateTime.UtcNow, maxChainDepth: effectiveOptions.MaxCertificateChainDepth);
            var material = await CertificateChainResolver.CollectRevocationMaterialAsync(chain.Chain, fetcher, effectiveOptions.MaxDssCertificates, effectiveOptions.MaxRevocationResponseBytes, effectiveOptions.RevocationTimeout, cancellationToken).ConfigureAwait(false);
            ocspResponses.AddRange(material.OcspResponses);
            crls.AddRange(material.Crls);

            if (ocspResponses.Count + crls.Count > effectiveOptions.MaxDssRevocationEntries)
            {
                throw new PlumePdfException("PLUME6052", $"Collected {ocspResponses.Count + crls.Count} revocation entries, exceeding the configured cap of {effectiveOptions.MaxDssRevocationEntries} (PdfOptions.MaxDssRevocationEntries).");
            }
        }

        // Same rollback discipline as SigningOrchestrator.SignAsync and
        // DocumentTimestamper.AddDocumentTimestampAsync: every registry/catalog mutation goes
        // through the scope, so a failed SaveIncremental (unwritable path, disk full) leaves the
        // in-memory document exactly as it was instead of carrying orphaned DSS objects and a
        // dirty catalog into the caller's next save.
        var scope = new SignatureMutationScope(document.Objects);
        try
        {
            var dss = new PdfDictionary();
            dss.Set(PdfName.Type, PdfName.Get("DSS"));
            dss.Set(PdfName.Certs, RegisterStreams(document, certificatesByThumbprint.Values.Select(static c => c.RawData), scope));
            if (ocspResponses.Count > 0)
            {
                dss.Set(PdfName.OCSPs, RegisterStreams(document, ocspResponses, scope));
            }

            if (crls.Count > 0)
            {
                dss.Set(PdfName.CRLs, RegisterStreams(document, crls, scope));
            }

            var dssReference = document.Objects.AllocateNumber();
            scope.RegisterNew(dssReference, dss);

            var catalog = document.Catalog ?? throw new PlumePdfException("PLUME6047", "This document's catalog could not be resolved — cannot attach /DSS.");
            scope.Set(catalog.Dictionary, PdfName.DSS, new PdfReference(dssReference));
            scope.MarkDirty(catalog.Reference);

            document.SaveIncremental(outputPath, effectiveOptions);
        }
        catch
        {
            scope.Rollback();
            throw;
        }
    }

    private static PdfArray RegisterStreams(PdfDocument document, IEnumerable<byte[]> blobs, SignatureMutationScope scope)
    {
        var array = new PdfArray();
        foreach (var blob in blobs)
        {
            var reference = document.Objects.AllocateNumber();
            scope.RegisterNew(reference, new PdfStream(new PdfDictionary(), blob));
            array.Add(new PdfReference(reference));
        }

        return array;
    }

    private static (X509Certificate2? Leaf, IReadOnlyList<X509Certificate2> Embedded) DecodeCertificates(byte[] cms)
    {
        try
        {
            var decoded = new SignedCms();
            decoded.Decode(cms);
            var embedded = new List<X509Certificate2>();
            foreach (X509Certificate2 certificate in decoded.Certificates)
            {
                embedded.Add(certificate);
            }

            var leaf = decoded.SignerInfos.Count == 1 ? decoded.SignerInfos[0].Certificate : null;
            return (leaf, embedded);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (null, []);
        }
    }
}
