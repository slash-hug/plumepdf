using System.Text;

namespace PlumePdf.Documents.Metadata;

/// <summary>
/// The write-side, caller-constructible Document Information Dictionary model — the parameter
/// type for <c>PdfDocument.SetInfo</c>. A distinct type from the read-side
/// <c>PlumePdf.Documents.PdfDocumentInfo</c> (<c>PdfDocument.GetInfo</c>'s return type) rather
/// than reusing it: that type's constructor is <see langword="internal"/> and, being a plain
/// class rather than a record, offers no <c>with</c>-expression path for a caller to build a
/// modified copy — it exists purely to report what a document already carries. This type is
/// the caller-constructible, <c>with</c>-friendly equivalent for the write direction.
/// Immutable — use object initializers or <c>with</c>.
/// </summary>
/// <example>
/// <code>
/// document.SetInfo(new DocInfoMetadata
/// {
///     Title = "Q3 Report",
///     Producer = "PlumePDF",
///     CreationDate = DateTimeOffset.UtcNow,
/// });
/// </code>
/// </example>
public sealed record DocInfoMetadata
{
    /// <summary>The document's title (<c>/Title</c>).</summary>
    public string? Title { get; init; }

    /// <summary>The document's author (<c>/Author</c>).</summary>
    public string? Author { get; init; }

    /// <summary>The document's subject (<c>/Subject</c>).</summary>
    public string? Subject { get; init; }

    /// <summary>The document's keywords (<c>/Keywords</c>).</summary>
    public string? Keywords { get; init; }

    /// <summary>The application that created the original (non-PDF) document (<c>/Creator</c>).</summary>
    public string? Creator { get; init; }

    /// <summary>The application/library that produced this PDF (<c>/Producer</c>).</summary>
    public string? Producer { get; init; }

    /// <summary>
    /// The document's creation date (<c>/CreationDate</c>). PDF/A requires this agree with
    /// an already-set <c>XmpPacket.CreateDate</c> — <c>PdfDocument.SetInfo</c>/
    /// <c>SetXmpMetadata</c> enforce that agreement at write time.
    /// </summary>
    public DateTimeOffset? CreationDate { get; init; }

    /// <summary>
    /// The document's last-modification date (<c>/ModDate</c>). PDF/A requires this agree
    /// with an already-set <c>XmpPacket.ModifyDate</c>, enforced the same way as
    /// <see cref="CreationDate"/>.
    /// </summary>
    public DateTimeOffset? ModDate { get; init; }
}

/// <summary>
/// Builds a Document Information Dictionary (<c>/Info</c>, ISO 32000-1 §14.3.3) from a
/// <see cref="DocInfoMetadata"/> — the write-side counterpart to
/// <c>PlumePdf.Documents.MetadataReader.ReadDocumentInfo</c>, consumed by
/// <c>PdfDocument.SetInfo</c>.
/// </summary>
public static class DocInfoWriter
{
    private static readonly byte[] Utf16BigEndianBom = [0xFE, 0xFF];

    private static readonly PdfName TitleName = PdfName.Get("Title");
    private static readonly PdfName AuthorName = PdfName.Get("Author");
    private static readonly PdfName SubjectName = PdfName.Get("Subject");
    private static readonly PdfName KeywordsName = PdfName.Get("Keywords");
    private static readonly PdfName CreatorName = PdfName.Get("Creator");
    private static readonly PdfName ProducerName = PdfName.Get("Producer");
    private static readonly PdfName CreationDateName = PdfName.Get("CreationDate");
    private static readonly PdfName ModDateName = PdfName.Get("ModDate");

    /// <summary>Builds the <c>/Info</c> dictionary <paramref name="info"/> describes. Every text field is written as a UTF-16BE text string per ISO 32000-1 §7.9.2.2 (round-trips exactly through <see cref="PdfString.GetText"/>); every date as a <c>D:YYYYMMDDHHmmSSOHH'mm'</c> literal string per §7.9.4.</summary>
    /// <example>
    /// <code>
    /// var dict = DocInfoWriter.BuildDictionary(new DocInfoMetadata { Title = "Report" });
    /// </code>
    /// </example>
    public static PdfDictionary BuildDictionary(DocInfoMetadata info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var dict = new PdfDictionary();
        SetTextIfPresent(dict, TitleName, info.Title);
        SetTextIfPresent(dict, AuthorName, info.Author);
        SetTextIfPresent(dict, SubjectName, info.Subject);
        SetTextIfPresent(dict, KeywordsName, info.Keywords);
        SetTextIfPresent(dict, CreatorName, info.Creator);
        SetTextIfPresent(dict, ProducerName, info.Producer);

        if (info.CreationDate is { } creationDate)
        {
            dict.Set(CreationDateName, PdfString.FromLiteral(Encoding.ASCII.GetBytes(FormatPdfDate(creationDate))));
        }

        if (info.ModDate is { } modDate)
        {
            dict.Set(ModDateName, PdfString.FromLiteral(Encoding.ASCII.GetBytes(FormatPdfDate(modDate))));
        }

        return dict;
    }

    private static void SetTextIfPresent(PdfDictionary dict, PdfName key, string? value)
    {
        if (value is null)
        {
            return;
        }

        dict.Set(key, PdfString.FromLiteral([.. Utf16BigEndianBom, .. Encoding.BigEndianUnicode.GetBytes(value)]));
    }

    /// <summary>Formats a date per ISO 32000-1 §7.9.4: <c>D:YYYYMMDDHHmmSSOHH'mm'</c> — the exact inverse of <c>MetadataReader.ParsePdfDate</c>.</summary>
    private static string FormatPdfDate(DateTimeOffset value)
    {
        var sign = value.Offset < TimeSpan.Zero ? '-' : '+';
        var offset = value.Offset.Duration();
        return $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:D2}'{offset.Minutes:D2}'";
    }
}
