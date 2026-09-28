namespace PlumePdf;

/// <summary>
/// How serious a <see cref="PdfDiagnostic"/> is. Purely informational at
/// <see cref="Info"/> and <see cref="Warning"/>; <see cref="Error"/> marks a deviation
/// that <see cref="PdfOptions.Strict"/> would have thrown on instead of tolerating.
/// </summary>
public enum DiagnosticSeverity
{
    /// <summary>Purely informational — no deviation from a well-formed document occurred.</summary>
    Info,

    /// <summary>A recoverable deviation from a well-formed document was tolerated and repaired.</summary>
    Warning,

    /// <summary>A deviation serious enough that <see cref="PdfOptions.Strict"/> would reject it outright.</summary>
    Error,
}

/// <summary>
/// One entry in <see cref="DiagnosticCollection"/> — a recoverable deviation PlumePDF
/// tolerated while reading, writing, or repairing a document. Shares its
/// <see cref="Code"/> space with <see cref="PlumePdfException.Code"/>: the same
/// code identifies the same class of deviation whether it surfaced as a diagnostic (this
/// type, under lenient reading) or as a thrown exception (under <see cref="PdfOptions.Strict"/>,
/// or when the deviation turned out to be unrecoverable).
/// </summary>
/// <example>
/// <code>
/// foreach (var diagnostic in document.Diagnostics)
/// {
///     Console.WriteLine($"[{diagnostic.Severity}] {diagnostic.Code}: {diagnostic.Message}");
/// }
/// </code>
/// </example>
public sealed class PdfDiagnostic
{
    /// <summary>Creates a diagnostic entry.</summary>
    /// <param name="code">The stable <c>PLUME####</c> code identifying this class of deviation.</param>
    /// <param name="severity">How serious the deviation is.</param>
    /// <param name="message">An actionable description of what deviated and what was done about it.</param>
    /// <param name="byteOffset">The byte offset in the source where the deviation was observed, if known.</param>
    /// <param name="subject">The indirect object the deviation concerns, if any.</param>
    public PdfDiagnostic(string code, DiagnosticSeverity severity, string message, long? byteOffset = null, IndirectReference? subject = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentException.ThrowIfNullOrEmpty(message);
        Code = code;
        Severity = severity;
        Message = message;
        ByteOffset = byteOffset;
        Subject = subject;
    }

    /// <summary>The stable, greppable code identifying this class of deviation.</summary>
    public string Code { get; }

    /// <summary>How serious the deviation is.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>An actionable description of what deviated and what was done about it.</summary>
    public string Message { get; }

    /// <summary>The byte offset in the source where the deviation was observed, if known.</summary>
    public long? ByteOffset { get; }

    /// <summary>The indirect object the deviation concerns, if any.</summary>
    public IndirectReference? Subject { get; }

    /// <inheritdoc/>
    public override string ToString() => $"[{Severity}] {Code}: {Message}";
}
