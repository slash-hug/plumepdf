using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>
/// The one mapping from a <c>/Filter</c> value to the <see cref="ImageEncoding"/> family a
/// compression result reports: the last filter in the chain that isn't an ASCII text wrapper
/// decides it, and full and abbreviated (inline-image) names map alike.
/// </summary>
internal static class ImageEncodings
{
    private static readonly PdfName InlineFilterName = PdfName.Get("F");

    /// <summary>The family of a stream (image XObject) dictionary's <c>/Filter</c>; <c>/F</c> there names an external file, not a filter.</summary>
    /// <param name="dictionary">The stream's dictionary.</param>
    /// <param name="source">Resolves an indirect <c>/Filter</c> or array element; null when there is nothing to resolve against.</param>
    public static ImageEncoding OfStream(PdfDictionary dictionary, IObjectSource? source) =>
        FromFilter(dictionary.TryGetValue(PdfName.Filter, out var filter) ? filter : null, source);

    /// <summary>The family of an inline image's parameter dictionary: its <c>/F</c> entry, or a full-name <c>/Filter</c>.</summary>
    /// <param name="dictionary">The <c>BI</c> operation's parameter dictionary (which holds no indirect references).</param>
    public static ImageEncoding OfInlineImage(PdfDictionary dictionary) =>
        FromFilter(
            dictionary.TryGetValue(InlineFilterName, out var filter) || dictionary.TryGetValue(PdfName.Filter, out filter) ? filter : null,
            source: null);

    /// <summary>
    /// The family of a <c>/Filter</c> value: absent, <c>null</c> or an empty array is
    /// <see cref="ImageEncoding.Uncompressed"/>; otherwise the last element that isn't
    /// <c>ASCIIHexDecode</c>/<c>ASCII85Decode</c> decides it (none left: uncompressed), and
    /// anything that isn't a known name is <see cref="ImageEncoding.Other"/>.
    /// </summary>
    /// <param name="filter">The <c>/Filter</c> value, direct or indirect, or null when absent.</param>
    /// <param name="source">Resolves indirect values (a dangling reference reads as <c>null</c>); a reference with no source to follow it is <see cref="ImageEncoding.Other"/>.</param>
    public static ImageEncoding FromFilter(PdfObject? filter, IObjectSource? source)
    {
        var value = Resolve(filter, source);
        if (value is not PdfArray chain)
        {
            return value is null ? ImageEncoding.Uncompressed : FromName(value);
        }

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var element = Resolve(chain[i], source);
            if (element is PdfName name && IsAsciiWrapper(name.Value))
            {
                continue;
            }

            return element is null ? ImageEncoding.Other : FromName(element);
        }

        return ImageEncoding.Uncompressed;
    }

    // Null for an absent or null value; a reference that can't be followed is returned as is.
    private static PdfObject? Resolve(PdfObject? value, IObjectSource? source)
    {
        if (value is PdfReference reference && source is not null)
        {
            value = source.Resolve(reference.Target);
        }

        return value is PdfNull ? null : value;
    }

    private static bool IsAsciiWrapper(string name) => name is "ASCIIHexDecode" or "AHx" or "ASCII85Decode" or "A85";

    private static ImageEncoding FromName(PdfObject value) => value is not PdfName name
        ? ImageEncoding.Other
        : name.Value switch
        {
            "FlateDecode" or "Fl" => ImageEncoding.Flate,
            "DCTDecode" or "DCT" => ImageEncoding.Jpeg,
            "JPXDecode" => ImageEncoding.Jpeg2000,
            "JBIG2Decode" => ImageEncoding.Jbig2,
            "CCITTFaxDecode" or "CCF" => ImageEncoding.Ccitt,
            "RunLengthDecode" or "RL" => ImageEncoding.RunLength,
            "LZWDecode" or "LZW" => ImageEncoding.Lzw,
            "ASCIIHexDecode" or "AHx" or "ASCII85Decode" or "A85" => ImageEncoding.Uncompressed,
            _ => ImageEncoding.Other,
        };
}
