using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PlumePdf.Objects;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Text renderings of an object graph for equality checks: <see cref="Describe"/> for one
/// object, <see cref="GraphSnapshot"/> for a whole opened document, and
/// <see cref="DecodedFormHash"/> for a saved file whose raw bytes are not comparable across
/// platforms (the runtime's zlib decides the bytes of <see cref="PdfOptions.Optimize"/>'s
/// Flate-compressed object and cross-reference streams).
/// </summary>
internal static class CanonicalPdf
{
    private static readonly PdfName ObjStm = PdfName.Get("ObjStm");
    private static readonly PdfName XRef = PdfName.Get("XRef");

    /// <summary>
    /// Every object number below the trailer's <c>/Size</c>, resolved through
    /// <c>doc.Objects</c> and described in number order, plus the trailer. Streams are described
    /// by their raw bytes, so an in-memory re-encode is a difference.
    /// </summary>
    public static string GraphSnapshot(PdfDocument document)
    {
        var text = new StringBuilder();
        text.Append("trailer ").Append(Describe(document.Objects.Trailer, decodeStreams: false)).Append('\n');
        for (var n = 1; n < TrailerSize(document); n++)
        {
            text.Append(n).Append(": ").Append(Describe(document.Objects[new IndirectReference(n, 0)], decodeStreams: false)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// SHA-256 over the saved file's decoded canonical form: every object in object-number
    /// order with each stream filter-decoded (and its <c>/Length</c>, <c>/Filter</c> and
    /// <c>/DecodeParms</c> dropped), object-stream members resolved in place, object and
    /// cross-reference streams themselves skipped, plus the trailer's <c>/Root</c>,
    /// <c>/Info</c> and <c>/ID</c>.
    /// </summary>
    public static string DecodedFormHash(byte[] savedFile)
    {
        using var document = PdfDocument.Open(savedFile);
        var text = new StringBuilder();
        var trailer = document.Objects.Trailer;
        foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.Id })
        {
            if (trailer.TryGetValue(key, out var value))
            {
                text.Append(key).Append(' ').Append(Describe(value, decodeStreams: true)).Append('\n');
            }
        }

        for (var n = 1; n < TrailerSize(document); n++)
        {
            var value = document.Objects[new IndirectReference(n, 0)];
            if (value is PdfStream stream && stream.Dictionary.TryGetValue(PdfName.Type, out var type) && (type == ObjStm || type == XRef))
            {
                continue;
            }

            text.Append(n).Append(": ").Append(Describe(value, decodeStreams: true)).Append('\n');
        }

        return Sha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>A deterministic one-line rendering of <paramref name="value"/>; references are not followed.</summary>
    public static string Describe(PdfObject value, bool decodeStreams)
    {
        var text = new StringBuilder();
        Append(text, value, decodeStreams);
        return text.ToString();
    }

    private static int TrailerSize(PdfDocument document) =>
        document.Objects.Trailer.TryGetValue(PdfName.Size, out var size) && size is PdfNumber number ? number.ToInt32() : 0;

    private static void Append(StringBuilder text, PdfObject value, bool decodeStreams)
    {
        switch (value)
        {
            case PdfNull:
                text.Append("null");
                break;
            case PdfBoolean boolean:
                text.Append(boolean.Value ? "true" : "false");
                break;
            case PdfNumber number:
                text.Append(number.IsInteger ? number.ToInt64().ToString(CultureInfo.InvariantCulture) : number.Value.ToString("R", CultureInfo.InvariantCulture));
                break;
            case PdfName name:
                text.Append('/').Append(name.Value);
                break;
            case PdfString s:
                text.Append('<').Append(Convert.ToHexString(s.Bytes.Span)).Append('>');
                break;
            case PdfReference reference:
                text.Append(reference.Target.Number).Append(' ').Append(reference.Target.Generation).Append(" R");
                break;
            case PdfArray array:
                text.Append('[');
                foreach (var element in array)
                {
                    Append(text, element, decodeStreams);
                    text.Append(' ');
                }

                text.Append(']');
                break;
            case PdfDictionary dictionary:
                AppendDictionary(text, dictionary, decodeStreams, skipStreamKeys: false);
                break;
            case PdfStream stream:
                AppendDictionary(text, stream.Dictionary, decodeStreams, skipStreamKeys: decodeStreams);
                var bytes = decodeStreams ? stream.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default) : stream.RawBytes.ToArray();
                text.Append(" stream ").Append(Sha256(bytes));
                break;
            default:
                text.Append(value.GetType().Name);
                break;
        }
    }

    private static void AppendDictionary(StringBuilder text, PdfDictionary dictionary, bool decodeStreams, bool skipStreamKeys)
    {
        text.Append("<<");
        foreach (var (key, entry) in dictionary.OrderBy(static e => e.Key.Value, StringComparer.Ordinal))
        {
            if (skipStreamKeys && (key == PdfName.Length || key == PdfName.Filter || key == PdfName.DecodeParms))
            {
                continue;
            }

            text.Append(" /").Append(key.Value).Append(' ');
            Append(text, entry, decodeStreams);
        }

        text.Append(" >>");
    }
}
