using System.Security.Cryptography;
using PlumePdf.Objects;
using PlumePdf.Objects.Signing;

namespace PlumePdf.Documents.Signing;

/// <summary>
/// Adds a standalone <c>/DocTimeStamp</c> revision (ISO 32000-2 §12.8.5, PAdES B-LTA
/// maintenance, Task C4) — a document timestamp is its own <c>/FT /Sig</c> field/widget whose
/// <c>/V</c> is a <c>/Type /DocTimeStamp</c> dictionary carrying the raw RFC 3161 token
/// directly as <c>/Contents</c> (no CMS-over-a-CMS wrapping — unlike a B-T signature-timestamp
/// unsigned attribute, this is the whole signature). Reuses <c>SigningOrchestrator</c>'s
/// field/widget/<c>/AcroForm</c> attachment helpers (same shape, different <c>/V</c> dictionary
/// type) rather than duplicating them.
/// </summary>
internal static class DocumentTimestamper
{
    private static readonly PdfName TypeName = PdfName.Type;
    private static readonly PdfName FilterName = PdfName.Get("Filter");
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName ByteRangeName = PdfName.ByteRange;
    private static readonly PdfName SubFilterName = PdfName.SubFilter;

    /// <summary>
    /// Timestamps <paramref name="document"/>'s current state (its most recent revision,
    /// including any prior signatures/DSS) and writes the result to
    /// <paramref name="outputPath"/>.
    /// </summary>
    public static async Task AddDocumentTimestampAsync(PdfDocument document, string outputPath, ITimestampAuthority timestampAuthority, HashAlgorithmName digestAlgorithm, int? reservationBytes, PdfOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(timestampAuthority);

        var effectiveOptions = options ?? document.Options;
        SigningOrchestrator.RefuseDeterministicMaintenance(effectiveOptions, "AddDocumentTimestampAsync");
        RequireWritable(document);

        var fieldName = SigningOrchestrator.ResolveFieldName(document, requested: null);
        var dictReference = document.Objects.AllocateNumber();

        var dict = new PdfDictionary();
        dict.Set(TypeName, PdfName.DocTimeStamp);
        dict.Set(FilterName, PdfName.Get("Adobe.PPKLite"));
        dict.Set(SubFilterName, PdfName.ETSIRFC3161);
        dict.Set(ContentsName, new PdfContentsPlaceholder(reservationBytes ?? 24_000));
        dict.Set(ByteRangeName, new PdfByteRangePlaceholder(12));

        // See SigningOrchestrator.SignAsync's remark on SignatureMutationScope: every registry
        // mutation below is routed through it so a TSA failure, or a write failure, rolls back
        // cleanly instead of leaving a half-registered /DocTimeStamp for a caller's retry.
        var scope = new SignatureMutationScope(document.Objects);
        try
        {
            scope.RegisterNew(dictReference, dict);

            var fieldReference = SigningOrchestrator.BuildAndAttachField(document, fieldName, dictReference, scope);
            SigningOrchestrator.EnsureAcroFormReferencesField(document, fieldReference, scope);

            var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
            var topPagesReference = SigningOrchestrator.ResolveTopPagesReference(document);

            document.DetachMappedSourceIfSamePath(outputPath);
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, $".{Path.GetFileName(outputPath)}.plumepdf-tmp-{Guid.NewGuid():N}");

            try
            {
                var session = SigningWriteSession.Create(document.Source!, document.Objects, document.PagesTreeDirty, topPagesReference, pages, document.StartXrefOffset!.Value, effectiveOptions);

                using (var hash = IncrementalHash.CreateHash(digestAlgorithm))
                {
                    session.HashDocument(hash);
                    var digest = hash.GetHashAndReset();
                    var token = await timestampAuthority.GetTimestampAsync(digest, digestAlgorithm, cancellationToken).ConfigureAwait(false);
                    session.PatchContents(DerNormalizer.Normalize(token));
                }

                using (var tempStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    session.WriteTo(tempStream);
                }

                File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch
        {
            scope.Rollback();
            throw;
        }
    }

    private static void RequireWritable(PdfDocument document)
    {
        if (document.HasEncryptedSource)
        {
            throw new PlumePdfException("PLUME6054", "Timestamping an encrypted-source document is out of scope for this phase.");
        }

        if (document.Source is null)
        {
            throw new PlumePdfException("PLUME5003", "AddDocumentTimestampAsync requires a document opened from a file, stream, or byte buffer; this document has no backing byte source.");
        }

        if (document.StartXrefOffset is null)
        {
            throw new PlumePdfException("PLUME5005", "The source's cross-reference data was recovered via a brute-force scan and has no reliable prior 'startxref' offset to chain a document-timestamp revision onto.");
        }

        if (document.Catalog is null)
        {
            throw new PlumePdfException("PLUME6047", "This document's catalog could not be resolved (see Diagnostics) — a document timestamp requires a resolvable /Root catalog.");
        }
    }
}
