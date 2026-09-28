namespace PlumePdf.Documents;

/// <summary>
/// Resolves a document's <c>/Root</c> catalog dictionary (ISO 32000-1 §7.7.2), leniently:
/// a missing or malformed catalog is recorded to <see cref="DiagnosticCollection"/> and
/// treated as "no pages" rather than failing <c>PdfDocument.Open</c> outright — consistent
/// with the reading engine's "open anything" philosophy (docs/architecture.md, "Error
/// philosophy"). <see cref="PlumePdf.Objects.CrossReferenceReader"/> already guarantees the
/// trailer itself has a <c>/Root</c> key before a document opens at all; this resolves what
/// that key actually points to.
/// </summary>
internal sealed class DocumentCatalog
{
    private DocumentCatalog(PdfDictionary dictionary, IndirectReference reference)
    {
        Dictionary = dictionary;
        Reference = reference;
    }

    /// <summary>The catalog dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The catalog's own indirect-object identity.</summary>
    public IndirectReference Reference { get; }

    /// <summary>
    /// Resolves the catalog from <paramref name="trailer"/>'s <c>/Root</c> entry, or returns
    /// <see langword="null"/> (recording a diagnostic) when it is missing, not an indirect
    /// reference, or does not resolve to a dictionary.
    /// </summary>
    public static DocumentCatalog? Resolve(ObjectRegistry objects, PdfDictionary trailer, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(trailer);
        ArgumentNullException.ThrowIfNull(options);

        if (!trailer.TryGetValue(PdfName.Root, out var rootValue) || rootValue is not PdfReference rootRef)
        {
            ReportDeviation("PLUME6001", "The trailer's /Root entry is missing or is not an indirect reference; the document has no accessible catalog.", options, diagnostics);
            return null;
        }

        if (objects[rootRef.Target] is not PdfDictionary catalog)
        {
            ReportDeviation("PLUME6001", $"The trailer's /Root entry ({rootRef.Target}) did not resolve to a dictionary; the document has no accessible catalog.", options, diagnostics);
            return null;
        }

        return new DocumentCatalog(catalog, rootRef.Target);
    }

    private static void ReportDeviation(string code, string message, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}
