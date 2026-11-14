using PlumePdf.Documents.Forms;
using PlumePdf.Objects;

namespace PlumePdf.Documents.Signing;

/// <summary>
/// Finds a DocMDP certification (ISO 32000-1 §12.8.2.2): a signature whose <c>/Reference</c>
/// array carries a <c>/DocMDP</c> transform, and the <c>/P</c> permission level in its
/// <c>/TransformParams</c>. Shared by every verb that checks a certification before changing a
/// document: form fill and flatten, and page-add.
/// </summary>
internal static class DocMdpCertification
{
    private static readonly PdfName PName = PdfName.Get("P");

    /// <summary>
    /// The certifying signature field and its P-value, or <c>(null, null)</c> when no signature
    /// of <paramref name="document"/> (document timestamps excluded) is a DocMDP certification.
    /// </summary>
    internal static (string? FieldName, int? PValue) Find(PdfDocument document) =>
        Find(document.Objects, [.. SignatureDictionaryReader.ReadAll(document).Where(static s => !s.IsDocTimeStamp)]);

    /// <summary>
    /// The first of <paramref name="signatures"/> that is a DocMDP certification, and its P-value,
    /// or <c>(null, null)</c> when none is.
    /// </summary>
    internal static (string? FieldName, int? PValue) Find(ObjectRegistry objects, IReadOnlyList<SignatureDictionaryInfo> signatures)
    {
        foreach (var signature in signatures)
        {
            if (!signature.Dictionary.TryGetValue(PdfName.Reference, out var referenceValue) || AcroFormReader.Resolve(objects, referenceValue) is not PdfArray references)
            {
                continue;
            }

            // Every level here may legally be indirect (ISO 32000-1 §12.8.2.2) — a producer that
            // writes the sig-ref entries or /TransformParams as indirect objects must not make
            // the certification invisible, so each is resolved before the type test.
            foreach (var entry in references)
            {
                if (AcroFormReader.Resolve(objects, entry) is PdfDictionary sigRef
                    && sigRef.TryGetValue(PdfName.TransformMethod, out var methodValue) && PdfName.DocMDP.Equals(AcroFormReader.Resolve(objects, methodValue))
                    && sigRef.TryGetValue(PdfName.TransformParams, out var transformParamsValue) && AcroFormReader.Resolve(objects, transformParamsValue) is PdfDictionary transformParams
                    && transformParams.TryGetValue(PName, out var pValueObject) && AcroFormReader.Resolve(objects, pValueObject) is PdfNumber { IsInteger: true } number
                    && number.TryToInt32(out var converted))
                {
                    return (signature.FieldName, converted);
                }
            }
        }

        return (null, null);
    }

    /// <summary>What a DocMDP P-value permits, in words, for a diagnostic or exception message.</summary>
    internal static string DescribeP(int pValue) => pValue switch
    {
        1 => "no changes allowed",
        2 => "form fill-in and signing only",
        3 => "form fill-in, signing, and annotations",
        _ => "an unrecognized permission level",
    };
}
