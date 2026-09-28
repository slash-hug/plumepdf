namespace PlumePdf.Documents;

/// <summary>
/// The permission flags an encrypted document's <c>/Encrypt</c> dictionary's <c>/P</c> entry
/// grants (ISO 32000-1 §7.6.3.2, Table 22) — surfaced as advisory metadata
/// (<c>PdfDocument.Permissions</c>). PlumePDF does not enforce these: the encryption key is
/// already derived once a document opens successfully (an empty user password, which is how
/// most permission-restricted-but-not-open-restricted documents work, authenticates
/// unconditionally), so refusing an operation based on <c>/P</c> would be security theater —
/// and would break the accessibility case bit 10
/// (<see cref="ExtractForAccessibility"/>) exists to protect, where a screen reader is
/// <em>expected</em> to extract text even when general content-copying is disallowed. Reading
/// an <c>/Encrypt</c> dictionary that clears <see cref="ExtractContent"/> is instead recorded
/// as an advisory diagnostic on the extraction call — never a refusal. An
/// unencrypted document (no <c>/Encrypt</c> dictionary at all) reports <see cref="All"/>.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("restricted.pdf");
/// if (!document.Permissions.HasFlag(PdfPermissions.ExtractContent))
/// {
///     Console.WriteLine("This document's producer did not intend for its text to be copied.");
/// }
/// </code>
/// </example>
[Flags]
public enum PdfPermissions
{
    /// <summary>No permissions granted.</summary>
    None = 0,

    /// <summary>Bit 3 — print the document (at low quality, or any quality if <see cref="PrintHighQuality"/> is also set).</summary>
    Print = 1 << 2,

    /// <summary>Bit 4 — modify the document's contents by operations other than those controlled by <see cref="AddOrModifyAnnotations"/>, <see cref="FillFormFields"/>, and <see cref="AssembleDocument"/>.</summary>
    Modify = 1 << 3,

    /// <summary>Bit 5 — copy or otherwise extract text and graphics from the document.</summary>
    ExtractContent = 1 << 4,

    /// <summary>Bit 6 — add or modify text annotations, and (if <see cref="Modify"/> is also set) create or modify interactive form fields.</summary>
    AddOrModifyAnnotations = 1 << 5,

    /// <summary>Bit 9 — fill in existing interactive form fields (including signature fields), even when <see cref="AddOrModifyAnnotations"/> is clear.</summary>
    FillFormFields = 1 << 8,

    /// <summary>Bit 10 — extract text and graphics in support of accessibility (e.g. for a screen reader), regardless of <see cref="ExtractContent"/>.</summary>
    ExtractForAccessibility = 1 << 9,

    /// <summary>Bit 11 — insert, rotate, or delete pages and create document outlines/thumbnails, even when <see cref="Modify"/> is clear.</summary>
    AssembleDocument = 1 << 10,

    /// <summary>Bit 12 — print at the full quality the document's content supports, rather than the degraded-quality printing <see cref="Print"/> alone allows.</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>Every permission — reported for an unencrypted document (no <c>/Encrypt</c> dictionary restricts it at all).</summary>
    All = Print | Modify | ExtractContent | AddOrModifyAnnotations | FillFormFields | ExtractForAccessibility | AssembleDocument | PrintHighQuality,
}
