namespace PlumePdf;

/// <summary>
/// The verdict one <see cref="PdfARuleFinding"/> reaches for a single structural check
/// (<c>PlumePdf.Documents.PdfA.PdfAValidator</c>, Phase 6).
/// </summary>
public enum PdfARuleStatus
{
    /// <summary>The document satisfies this rule.</summary>
    Pass,

    /// <summary>The document violates this rule — see <see cref="PdfARuleFinding.Message"/> for detail.</summary>
    Fail,

    /// <summary>
    /// PlumePDF's self-check does not implement this rule (an honestly out-of-scope corner of
    /// its structural subset). Never reported as <see cref="Pass"/> — a rule
    /// PlumePDF cannot evaluate must never look conformant by omission.
    /// </summary>
    NotChecked,
}

/// <summary>
/// One structural rule's verdict against a document, as reported by
/// <c>PlumePdf.Documents.PdfA.PdfAValidator</c>.
/// </summary>
public sealed class PdfARuleFinding
{
    internal PdfARuleFinding(string ruleId, PdfARuleStatus status, string message, string? clauseId)
    {
        RuleId = ruleId;
        Status = status;
        Message = message;
        ClauseId = clauseId;
    }

    /// <summary>
    /// A short, stable identifier for this rule (e.g. <c>"OutputIntent"</c>,
    /// <c>"DocInfoXmpAgreement.Title"</c>) — stable across releases so a caller can filter or
    /// suppress a specific finding by name.
    /// </summary>
    public string RuleId { get; }

    /// <summary>This rule's verdict.</summary>
    public PdfARuleStatus Status { get; }

    /// <summary>
    /// A human-readable explanation. For <see cref="PdfARuleStatus.Fail"/>, names what was
    /// found and why it violates PDF/A. For <see cref="PdfARuleStatus.NotChecked"/>, names the
    /// veraPDF-corpus clause this rule set does not implement.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// The veraPDF-corpus clause directory this rule corresponds to (e.g. <c>"6.7.3"</c>), or
    /// <see langword="null"/> for a rule PlumePDF derived directly from ISO 32000-1 base
    /// grammar rather than from a specific labelled clause (per the clean-room policy in
    /// AGENTS.md: veraPDF's own validation profiles/source are never read to author rules —
    /// only the corpus's self-documenting fixture outlines, per-clause directory structure,
    /// and free ISO 32000-1 text are the authority).
    /// </summary>
    public string? ClauseId { get; }
}

/// <summary>
/// The result of validating a document against PlumePDF's own bounded PDF/A structural
/// self-check (<c>PlumePdf.Documents.PdfA.PdfAValidator.Validate</c>). This is a narrower,
/// honestly-enumerated companion to the veraPDF CLI — the CI-gating exit-demo oracle — never a
/// claim of full PDF/A conformance.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("candidate.pdf");
/// var result = PdfAValidator.Validate(document);
/// if (!result.IsConformant)
/// {
///     foreach (var failure in result.Failures)
///     {
///         Console.WriteLine($"{failure.RuleId}: {failure.Message}");
///     }
/// }
/// </code>
/// </example>
public sealed class PdfAValidationResult
{
    internal PdfAValidationResult(string? declaredPart, string? declaredConformance, IReadOnlyList<PdfARuleFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        DeclaredPart = declaredPart;
        DeclaredConformance = declaredConformance;
        Findings = findings;
    }

    /// <summary>
    /// The PDF/A part the document's own XMP <c>pdfaid:part</c> property declares (e.g.
    /// <c>"1"</c>, <c>"2"</c>), or <see langword="null"/> when no parseable declaration was
    /// found — see the <c>PdfAIdentification</c> finding for why.
    /// </summary>
    public string? DeclaredPart { get; }

    /// <summary>
    /// The PDF/A conformance level the document's own XMP <c>pdfaid:conformance</c> property
    /// declares (e.g. <c>"B"</c>), or <see langword="null"/> when no parseable declaration was
    /// found.
    /// </summary>
    public string? DeclaredConformance { get; }

    /// <summary>Every rule this self-check evaluated (or explicitly did not — see <see cref="PdfARuleStatus.NotChecked"/>), in evaluation order.</summary>
    public IReadOnlyList<PdfARuleFinding> Findings { get; }

    /// <summary>
    /// Whether every evaluated rule passed. <see cref="PdfARuleStatus.NotChecked"/> findings
    /// never affect this — they are neither proof of conformance nor a failure, only an
    /// honest gap in this self-check's coverage (never claim veraPDF-equivalent completeness
    /// from a <see langword="true"/> result here; use the veraPDF CLI for that).
    /// </summary>
    public bool IsConformant => !Findings.Any(static f => f.Status == PdfARuleStatus.Fail);

    /// <summary>Every finding whose <see cref="PdfARuleFinding.Status"/> is <see cref="PdfARuleStatus.Fail"/>.</summary>
    public IEnumerable<PdfARuleFinding> Failures => Findings.Where(static f => f.Status == PdfARuleStatus.Fail);

    /// <summary>Every rule this self-check does not implement (an honest coverage gap, never silently treated as passing).</summary>
    public IEnumerable<PdfARuleFinding> NotCheckedRules => Findings.Where(static f => f.Status == PdfARuleStatus.NotChecked);
}
