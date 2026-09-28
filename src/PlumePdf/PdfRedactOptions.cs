namespace PlumePdf;

/// <summary>
/// Options controlling <c>PdfDocument.Redact</c> — mirrors <c>PdfOptions</c>'s
/// resource-cap-plus-behavior-switch shape but scoped to one redaction call rather
/// than a whole document.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("contract.pdf");
/// var result = document.Redact([RedactionTarget.Text("Jane Doe")]);
/// Console.WriteLine($"{result.MatchCount} match(es) redacted");
/// document.Save("contract-redacted.pdf"); // Save only — SaveIncremental would leave the
///                                          // original bytes recoverable.
/// </code>
/// </example>
public sealed record PdfRedactOptions
{
    /// <summary>The default redaction options.</summary>
    public static PdfRedactOptions Default { get; } = new();

    /// <summary>
    /// The maximum number of text/pattern matches a single <c>Redact</c> call may resolve
    /// across every page in scope before refusing to continue — a resource-limit guard
    /// against a hostile or pathological regex target matching an unbounded number of
    /// times. Region targets never count against this cap (their count is bounded by how many
    /// the caller supplied). Default 10,000.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new PdfRedactOptions { MaxMatches = 500 };
    /// document.Redact([RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}"))], options);
    /// </code>
    /// </example>
    public int MaxMatches { get; init; } = 10_000;

    /// <summary>
    /// Whether <c>Redact</c> is allowed to proceed against a source that carries existing
    /// signature(s) or document timestamp(s). Default <see langword="false"/>: <c>Redact</c>
    /// refuses outright (<c>PLUME6062</c>) rather than silently producing a document that
    /// still carries a signature dictionary no longer valid over the (now-redacted) bytes — a
    /// stronger-than-<c>Save</c>'s-own-diagnostic default because a
    /// redacted-yet-apparently-signed document is a false-trust failure mode, not merely a
    /// broken-signature one. Setting this <see langword="true"/> instead makes <c>Redact</c>
    /// strip every signature field's <c>/V</c>, the catalog's <c>/Perms</c> and <c>/DSS</c>,
    /// and any standalone <c>/DocTimeStamp</c> dictionary it reaches — so the saved output is
    /// honestly unsigned rather than carrying dead signature machinery.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new PdfRedactOptions { AllowInvalidatingSignatures = true };
    /// var result = document.Redact(targets, options);
    /// Console.WriteLine($"stripped {result.SignaturesStripped} signature(s)");
    /// </code>
    /// </example>
    public bool AllowInvalidatingSignatures { get; init; }

    /// <summary>
    /// Whether a literal <see cref="PlumePdf.Documents.Redaction.RedactionTarget.Text"/> match
    /// is case-sensitive. Default <see langword="false"/> (case-insensitive, ordinal). Has no
    /// effect on <see cref="PlumePdf.Documents.Redaction.RedactionTarget.Pattern"/> targets —
    /// case sensitivity for those is controlled by the caller's own
    /// <see cref="System.Text.RegularExpressions.Regex"/> options.
    /// </summary>
    public bool CaseSensitiveText { get; init; }

    /// <summary>
    /// Whether a redaction region intersecting an image makes the whole <c>Redact</c> call a
    /// coded refusal (<c>PLUME6074</c>) instead of removing the image. Default
    /// <see langword="false"/>: an intersecting image — XObject or inline — is removed whole
    /// (never a best-effort blank rectangle over pixels that survive
    /// underneath), which can take an unrelated corner of a large image with it. Set this
    /// <see langword="true"/> to fail loudly instead of losing the image: the refusal names the
    /// image and the painted area so the caller can tighten the region or accept the removal by
    /// clearing the switch. The document is left unmodified only up to the point of refusal —
    /// treat a <c>PLUME6074</c> as "discard this document instance and retry", the same
    /// discipline any failed redaction call requires.
    /// </summary>
    /// <example>
    /// <code>
    /// var options = new PdfRedactOptions { RefuseOnImageRemoval = true };
    /// document.Redact([RedactionTarget.Region(0, rect)], options); // throws PLUME6074 if rect touches an image
    /// </code>
    /// </example>
    public bool RefuseOnImageRemoval { get; init; }
}
