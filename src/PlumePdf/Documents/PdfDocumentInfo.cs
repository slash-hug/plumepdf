namespace PlumePdf.Documents;

/// <summary>
/// A document's Document Information Dictionary (ISO 32000-1 §14.3.3), typed —
/// <c>PdfDocument.GetInfo()</c>. Every field is a plain PDF string or date; no new parsing
/// technology is involved (the line is drawn at XMP, which stays a raw
/// bytes/UTF-8-string escape hatch via <c>PdfDocument.GetXmpMetadataBytes()</c>/
/// <c>GetXmpMetadataText()</c> rather than a typed object model). <see langword="null"/>
/// fields mean the entry was absent, not that it failed to parse — a malformed date string is
/// recorded as a diagnostic and reported as <see langword="null"/> rather than thrown
/// (the reading engine's lenient-by-default philosophy).
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("input.pdf");
/// PdfDocumentInfo info = document.GetInfo();
/// Console.WriteLine($"{info.Title} by {info.Author}, created {info.CreationDate}");
/// </code>
/// </example>
public sealed class PdfDocumentInfo
{
    internal PdfDocumentInfo(string? title, string? author, string? subject, string? keywords, string? creator, string? producer, DateTimeOffset? creationDate, DateTimeOffset? modDate)
    {
        Title = title;
        Author = author;
        Subject = subject;
        Keywords = keywords;
        Creator = creator;
        Producer = producer;
        CreationDate = creationDate;
        ModDate = modDate;
    }

    /// <summary>The document's title (<c>/Title</c>), or <see langword="null"/> when absent.</summary>
    public string? Title { get; }

    /// <summary>The document's author (<c>/Author</c>), or <see langword="null"/> when absent.</summary>
    public string? Author { get; }

    /// <summary>The document's subject (<c>/Subject</c>), or <see langword="null"/> when absent.</summary>
    public string? Subject { get; }

    /// <summary>The document's keywords (<c>/Keywords</c>), or <see langword="null"/> when absent.</summary>
    public string? Keywords { get; }

    /// <summary>The application that created the original (non-PDF) document (<c>/Creator</c>), or <see langword="null"/> when absent.</summary>
    public string? Creator { get; }

    /// <summary>The application/library that produced this PDF (<c>/Producer</c>), or <see langword="null"/> when absent.</summary>
    public string? Producer { get; }

    /// <summary>The document's creation date (<c>/CreationDate</c>), or <see langword="null"/> when absent or unparseable.</summary>
    public DateTimeOffset? CreationDate { get; }

    /// <summary>The document's last-modification date (<c>/ModDate</c>), or <see langword="null"/> when absent or unparseable.</summary>
    public DateTimeOffset? ModDate { get; }
}
