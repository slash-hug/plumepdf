namespace PlumePdf.Documents;

/// <summary>
/// Reads a document's Document Information Dictionary (§14.3.3), XMP metadata stream
/// (§14.3.2), and permission flags (§7.6.3.2) into their typed/raw surfaces —
/// <c>PdfDocument.GetInfo()</c>/<c>GetXmpMetadataBytes()</c>/<c>GetXmpMetadataText()</c>/
/// <c>Permissions</c>. No XMP/RDF object model: the metadata stream's bytes
/// are returned as-is (post-decrypt, post-filter-decode) — parsing its RDF/XML is left to the
/// caller (or a future phase's PDF/A work, which needs a typed XMP model anyway).
/// </summary>
internal static class MetadataReader
{
    private static readonly PdfName MetadataName = PdfName.Get("Metadata");
    private static readonly PdfName TitleName = PdfName.Get("Title");
    private static readonly PdfName AuthorName = PdfName.Get("Author");
    private static readonly PdfName SubjectName = PdfName.Get("Subject");
    private static readonly PdfName KeywordsName = PdfName.Get("Keywords");
    private static readonly PdfName CreatorName = PdfName.Get("Creator");
    private static readonly PdfName ProducerName = PdfName.Get("Producer");
    private static readonly PdfName CreationDateName = PdfName.Get("CreationDate");
    private static readonly PdfName ModDateName = PdfName.Get("ModDate");

    /// <summary>Reads the typed Document Information Dictionary from <paramref name="trailer"/>'s <c>/Info</c> entry.</summary>
    public static PdfDocumentInfo ReadDocumentInfo(ObjectRegistry objects, PdfDictionary trailer)
    {
        var info = trailer.TryGetValue(PdfName.Info, out var infoValue)
            ? infoValue switch
            {
                PdfReference reference => objects[reference.Target] as PdfDictionary,
                PdfDictionary direct => direct,
                _ => null,
            }
            : null;

        string? Text(PdfName key) => info is not null && info.TryGetValue(key, out var value) && value is PdfString s ? s.GetText() : null;

        return new PdfDocumentInfo(
            Text(TitleName),
            Text(AuthorName),
            Text(SubjectName),
            Text(KeywordsName),
            Text(CreatorName),
            Text(ProducerName),
            ParsePdfDate(Text(CreationDateName)),
            ParsePdfDate(Text(ModDateName)));
    }

    /// <summary>
    /// Reads and filter-decodes the catalog's <c>/Metadata</c> XMP stream (if any), or returns
    /// <see langword="null"/> when the catalog has none. This is the single read choke point
    /// every XMP consumer goes through (<c>PdfDocument.GetXmpMetadataBytes</c>/
    /// <c>GetXmpMetadataText</c>, and through them <c>PdfAValidator</c>'s packet scan), so the
    /// <see cref="PdfOptions.MaxXmpPacketReadBytes"/> cap enforced here bounds every downstream
    /// scan of the packet's text.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME6080</c> — the decoded packet exceeds <see cref="PdfOptions.MaxXmpPacketReadBytes"/>.</exception>
    public static byte[]? ReadXmpBytes(ObjectRegistry objects, PdfDictionary? catalog, PdfOptions options)
    {
        if (catalog is null || !catalog.TryGetValue(MetadataName, out var value) || value is not PdfReference reference)
        {
            return null;
        }

        if (objects[reference.Target] is not PdfStream stream)
        {
            return null;
        }

        var decoded = stream.GetDecodedBytes(options.Filters, options, r => objects[r]);
        if (decoded.LongLength > options.MaxXmpPacketReadBytes)
        {
            throw new PlumePdfException(
                "PLUME6080",
                $"The document's /Metadata XMP packet decodes to {decoded.LongLength} bytes, exceeding PdfOptions.MaxXmpPacketReadBytes ({options.MaxXmpPacketReadBytes}) — refusing to hand it to any consumer (a resource-limit guard against a hostile packet that is tiny compressed but enormous decoded). Raise the cap only if the source is trusted.");
        }

        return decoded;
    }

    /// <summary>Maps a raw <c>/P</c> value to <see cref="PdfPermissions"/> flags, or <see cref="PdfPermissions.All"/> when <paramref name="rawPermissions"/> is <see langword="null"/> (no <c>/Encrypt</c> dictionary).</summary>
    public static PdfPermissions ReadPermissions(int? rawPermissions) =>
        rawPermissions is int p ? (PdfPermissions)(p & (int)PdfPermissions.All) : PdfPermissions.All;

    /// <summary>
    /// Parses a PDF date string (§7.9.4: <c>D:YYYYMMDDHHmmSSOHH'mm'</c>, every component after
    /// the 4-digit year optional and progressively omittable) leniently — missing components
    /// default to the start of their range, an out-of-range component clamps rather than
    /// throwing, and a string too malformed to identify even a year returns
    /// <see langword="null"/> rather than throwing (the reading engine's recovery-ladder
    /// philosophy applied to a single string field).
    /// </summary>
    public static DateTimeOffset? ParsePdfDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        if (s.Length < 4 || !int.TryParse(s.AsSpan(0, 4), out var year))
        {
            return null;
        }

        var month = Math.Clamp(DigitsOrDefault(s, 4, 2, 1), 1, 12);
        var day = Math.Clamp(DigitsOrDefault(s, 6, 2, 1), 1, DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month));
        var hour = Math.Clamp(DigitsOrDefault(s, 8, 2, 0), 0, 23);
        var minute = Math.Clamp(DigitsOrDefault(s, 10, 2, 0), 0, 59);
        var second = Math.Clamp(DigitsOrDefault(s, 12, 2, 0), 0, 59);

        var offset = TimeSpan.Zero;
        if (s.Length > 14 && s[14] is '+' or '-')
        {
            var offsetHours = Math.Clamp(DigitsOrDefault(s, 15, 2, 0), 0, 23);
            var minuteStart = s.Length > 17 && s[17] == '\'' ? 18 : 17;
            var offsetMinutes = Math.Clamp(DigitsOrDefault(s, minuteStart, 2, 0), 0, 59);
            var span = new TimeSpan(offsetHours, offsetMinutes, 0);
            offset = s[14] == '-' ? -span : span;
        }

        try
        {
            return new DateTimeOffset(Math.Clamp(year, 1, 9999), month, day, hour, minute, second, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int DigitsOrDefault(string s, int start, int length, int fallback)
    {
        if (start < 0 || start + length > s.Length)
        {
            return fallback;
        }

        return int.TryParse(s.AsSpan(start, length), out var value) ? value : fallback;
    }
}
