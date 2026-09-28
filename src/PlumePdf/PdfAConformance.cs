namespace PlumePdf;

/// <summary>
/// The PDF/A conformance level a document is created against or validated for (ISO 19005,
/// consulted only through the veraPDF-corpus + Arlington + free-ISO-32000-1 authority spine
/// this project relies on — never ISO 19005 text
/// itself, which PlumePDF has not purchased). Phase 6 create/validate coverage is
/// <see cref="A1b"/> and <see cref="A2b"/> only; every other profile (2u/2a/3(a/b/u),
/// 4(e/f), PDF/UA-2) is out of v1.0 scope and reported as not-checked rather than silently
/// treated as passing.
/// </summary>
/// <example>
/// <code>
/// var target = PdfAConformance.A2b;
/// // Consumed by the PDF/A creation path (Phase 6): version knob, XMP pdfaid
/// // identification, OutputIntent, and Standard-14-font refusal all key off this value.
/// </code>
/// </example>
public enum PdfAConformance
{
    /// <summary>No PDF/A conformance is targeted or asserted — an ordinary document.</summary>
    None = 0,

    /// <summary>
    /// PDF/A-1b (basic, ISO 19005-1) — the minimum-bar visual-fidelity conformance level.
    /// Forces the writer's PDF version knob (<see cref="PdfOptions.PdfVersion"/>) to
    /// <c>"1.4"</c> and forbids object streams / cross-reference streams.
    /// </summary>
    A1b,

    /// <summary>
    /// PDF/A-2b (basic, ISO 19005-2) — Phase 6's primary create target.
    /// Permits object streams / cross-reference streams and PDF 1.7.
    /// </summary>
    A2b,
}
