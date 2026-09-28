namespace PlumePdf;

/// <summary>
/// Thrown when a <see cref="Manuscript"/> cannot be laid out — an element tree that demands
/// more space than its parent can give it, a degenerate constraint (a zero-width table
/// column, an empty composed section), or a resource limit tripped while paginating
/// (per <c>docs/agent-forward.md</c>'s "QuestPDF-grade" layout-error commitment).
/// Creation input is programmer-authored: every layout failure is a coded
/// exception, never a silently degraded render.
/// </summary>
/// <remarks>
/// <see cref="ElementPath"/>, <see cref="Measurements"/>, and <see cref="Constraint"/> are a
/// snapshot copied out of the layout engine at the moment of failure — a permanent, small
/// public data shape independent of the engine's own internal representation, which stays
/// free to change without ever widening or breaking this contract.
/// </remarks>
/// <example>
/// <code>
/// try
/// {
///     manuscript.Render();
/// }
/// catch (PdfLayoutException ex)
/// {
///     Console.WriteLine($"{ex.Code} at {ex.ElementPath}: {ex.Constraint}");
///     foreach (var m in ex.Measurements)
///     {
///         Console.WriteLine($"  {m.Label}: available {m.AvailableWidth}x{m.AvailableHeight}, required {m.RequiredWidth}x{m.RequiredHeight}");
///     }
/// }
/// </code>
/// </example>
public sealed class PdfLayoutException : PlumePdfException
{
    /// <summary>Creates a layout exception carrying the failing element's location and measurements.</summary>
    /// <param name="code">The stable <c>PLUME9###</c> error code (the Layout range).</param>
    /// <param name="message">An actionable description: what failed, where, and what was attempted.</param>
    /// <param name="elementPath">A human-readable path to the offending element, e.g. <c>Section &gt; Body &gt; Row[0]</c>.</param>
    /// <param name="measurements">The available-vs-required sizes recorded for the offending element and, where relevant, its ancestors.</param>
    /// <param name="constraint">A plain-language description of the constraint that was violated.</param>
    public PdfLayoutException(string code, string message, string elementPath, IReadOnlyList<ElementMeasurement> measurements, string constraint)
        : base(code, message)
    {
        ArgumentNullException.ThrowIfNull(elementPath);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(constraint);
        ElementPath = elementPath;
        Measurements = measurements;
        Constraint = constraint;
    }

    /// <summary>
    /// A human-readable path from the section root to the element that failed to lay out,
    /// e.g. <c>Section &gt; Body &gt; Table &gt; Row[3] &gt; Cell[2]</c>.
    /// </summary>
    public string ElementPath { get; }

    /// <summary>The available-vs-required sizes recorded for the offending element and, where relevant, its ancestors, innermost first.</summary>
    public IReadOnlyList<ElementMeasurement> Measurements { get; }

    /// <summary>A plain-language description of the constraint that was violated, e.g. "Row content is wider than the space available to it".</summary>
    public string Constraint { get; }
}

/// <summary>One element's available-vs-required size, as recorded in a <see cref="PdfLayoutException"/>.</summary>
/// <param name="Label">A short description of the element, e.g. <c>Row[0]</c> or <c>Table column 2</c>.</param>
/// <param name="AvailableWidth">The width, in points, the element's parent offered it.</param>
/// <param name="AvailableHeight">The height, in points, the element's parent offered it, or <see cref="double.PositiveInfinity"/> when height is unconstrained (the common case: content flows onto further pages instead).</param>
/// <param name="RequiredWidth">The width, in points, the element actually needed.</param>
/// <param name="RequiredHeight">The height, in points, the element actually needed.</param>
public readonly record struct ElementMeasurement(string Label, double AvailableWidth, double AvailableHeight, double RequiredWidth, double RequiredHeight);
