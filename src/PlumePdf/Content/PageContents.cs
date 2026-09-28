using PlumePdf.Objects;

namespace PlumePdf.Content;

/// <summary>
/// Resolves a page dictionary's <c>/Contents</c> value into its ordered content streams — the
/// one shape-normalization every <c>/Contents</c> consumer shares. ISO 32000-1 §7.7.3.3 allows
/// the value to be a stream or an array of streams, and either form may sit behind an indirect
/// reference; the reference-to-<em>array</em> form in particular (common in LiveCycle/AEM-
/// generated AcroForms) was mishandled by four independently-maintained copies of this switch,
/// each of which only recognized "reference to a stream" and "direct array" — so such pages
/// rendered, extracted, redacted, and stamped as if they had no content at all (the resulting
/// silently-blank pages). Centralized here so the shape can never fork again.
/// </summary>
internal static class PageContents
{
    /// <summary>
    /// Returns the content streams <paramref name="contentsValue"/> names, in order — empty for
    /// a missing/unresolvable value or one that is neither a stream nor an array (lenient; the
    /// caller decides whether emptiness deserves a diagnostic). Array items that do not resolve
    /// to streams are skipped individually, matching the prior per-site behavior.
    /// </summary>
    internal static List<PdfStream> ResolveStreams(PdfObject? contentsValue, ObjectRegistry objects)
    {
        var streams = new List<PdfStream>();
        var resolved = contentsValue is PdfReference reference ? objects[reference.Target] : contentsValue;
        switch (resolved)
        {
            case PdfStream single:
                streams.Add(single);
                break;

            case PdfArray array:
                foreach (var item in array)
                {
                    var itemResolved = item is PdfReference itemReference ? objects[itemReference.Target] : item;
                    if (itemResolved is PdfStream itemStream)
                    {
                        streams.Add(itemStream);
                    }
                }

                break;
        }

        return streams;
    }
}
