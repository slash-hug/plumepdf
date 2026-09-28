using PlumePdf.Elements;
using PlumePdf.Layout;

namespace PlumePdf;

/// <summary>
/// The creation-side element tree (CONTEXT.md "Manuscript"): a composed description of a
/// document before layout, built as plain data (Variant B — construct a full tree
/// with object initializers). <see cref="Render(PdfOptions?)"/> is the data-first route to a
/// real <see cref="PdfDocument"/>; <see cref="PdfDocument.Compose"/> is the fluent veneer that
/// builds a <see cref="Manuscript"/> internally for callers who never need to hold the tree
/// itself. Use <see cref="Manuscript"/> directly when you need to build, inspect, or transform
/// the document's structure as data before rendering it — use <see cref="PdfDocument.Compose"/>
/// for everything else.
/// </summary>
/// <example>
/// <code>
/// var manuscript = new Manuscript
/// {
///     Sections =
///     [
///         new Section
///         {
///             Header = new Text("INVOICE #1042") { Bold = true, FontSize = 20 },
///             Body = new Text("Bill to: Acme Corp"),
///             Footer = new Text("Page {page} of {pages}") { Align = HorizontalAlign.Center },
///         },
///     ],
/// };
///
/// using var document = manuscript.Render();
/// document.Save("invoice.pdf");
/// </code>
/// </example>
public sealed class Manuscript
{
    /// <summary>The document's sections, in order. Rendering at least one section is required — see <see cref="Render(PdfOptions?)"/>.</summary>
    public IReadOnlyList<Section> Sections { get; init; } = [];

    /// <summary>
    /// The document's natural language (a BCP 47 tag, e.g. <c>"en-US"</c>, <c>"fr"</c>) — written
    /// as the catalog's <c>/Lang</c> (ISO 32000-1 §14.9.2) and, more importantly, the switch that
    /// opts this manuscript into <em>tagged</em> output at all: setting <see cref="Language"/> is
    /// what makes <see cref="Render(PdfOptions?)"/> build a real <c>/StructTreeRoot</c>, mark
    /// every <see cref="Text"/>/<see cref="Image"/>/<see cref="Table"/> cell with the PDF
    /// structure role it implies (<see cref="Text.HeadingLevel"/>, <see cref="Image.AltText"/>,
    /// <see cref="Elements.Element.Role"/>), and enforce the required-semantics refusals that
    /// only make sense for output someone actually intends to be tagged/PDF-UA
    /// (<c>PLUME9010</c>: every <see cref="Image"/> needs <see cref="Image.AltText"/> or
    /// <c>Role = "Artifact"</c>). Leaving this <see langword="null"/> (the default) renders
    /// exactly as PlumePDF always has — plain, untagged content, byte-for-byte unchanged from
    /// before this property existed. Setting <see cref="Language"/> alone means <em>plain
    /// tagged PDF</em>: no <c>pdfuaid</c> conformance claim is written and no document title is
    /// required — set <see cref="PdfUa"/> to explicitly request PDF/UA-1 output and its
    /// stricter required-semantics enforcement. See the cookbook, "Create a
    /// tagged PDF".
    /// </summary>
    /// <example>
    /// <code>
    /// var manuscript = new Manuscript
    /// {
    ///     Language = "en-US",
    ///     Title = "Quarterly Report",
    ///     Sections = [section],
    /// };
    /// </code>
    /// </example>
    public string? Language { get; init; }

    /// <summary>
    /// The document's title — written to the document information dictionary's <c>/Title</c>
    /// (ISO 32000-1 §14.3.3) when set. PDF/UA also requires a viewer to display this title in
    /// its title bar in place of the file name (<c>/ViewerPreferences /DisplayDocTitle true</c>);
    /// setting <see cref="Title"/> together with <see cref="Language"/> covers both. Purely
    /// informational metadata — has no effect on <see cref="Language"/>'s tagged-output opt-in
    /// by itself.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Explicitly requests PDF/UA-1 (ISO 14289-1) output — the honest-claim opt-in
    /// for semantics no library can infer on the caller's behalf. Setting this:
    /// (a) implies tagged output (a real <c>/StructTreeRoot</c>, exactly as
    /// <see cref="Language"/> requests); (b) enforces, as coded refusals at
    /// <see cref="Render(PdfOptions?)"/> time, the caller-required semantics PDF/UA-1 mandates —
    /// <see cref="Language"/> must be set (<c>PLUME9011</c>), <see cref="Title"/> must be set
    /// (<c>PLUME9012</c>, PDF/UA requires a displayed document title), and every
    /// <see cref="Image"/> needs <see cref="Image.AltText"/> or <c>Role = "Artifact"</c>
    /// (<c>PLUME9010</c>, already enforced for any tagged output); and (c) declares
    /// <c>pdfuaid:part = 1</c> in the document's XMP packet, coexisting with any
    /// <see cref="PdfOptions.PdfAConformance"/> declaration in the same packet. Heading levels
    /// (<see cref="Text.HeadingLevel"/>) stay authorial — PlumePDF never validates their range
    /// or nesting order, since correct heading structure is an authoring-quality judgment no
    /// library can make (an honest-claim scoping choice). Defaults to <see langword="false"/>:
    /// <see cref="Language"/> alone continues to mean plain tagged PDF with no conformance
    /// claim and no title requirement.
    /// </summary>
    /// <example>
    /// <code>
    /// var manuscript = new Manuscript
    /// {
    ///     PdfUa = true,
    ///     Language = "en-US",
    ///     Title = "Quarterly Report",
    ///     Sections = [section],
    /// };
    /// using var document = manuscript.Render();
    /// document.Save("accessible-report.pdf");
    /// </code>
    /// </example>
    public bool PdfUa { get; init; }

    /// <summary>
    /// The document's creation date — consumed by the PDF/A create path (a
    /// <see cref="PdfOptions.PdfAConformance"/> other than <see cref="PdfAConformance.None"/>)
    /// as both the XMP packet's <c>xmp:CreateDate</c> and the <c>/Info</c> dictionary's
    /// <c>/CreationDate</c>, written to agree. Required together with
    /// <see cref="ModifyDate"/> when PDF/A output is combined with
    /// <see cref="PdfOptions.Deterministic"/> (<c>PLUME6058</c> — PlumePDF never
    /// invents a fixed or "now" timestamp under the byte-identical guarantee); a
    /// non-deterministic PDF/A render defaults it to the render's wall-clock time. Ignored
    /// outside the PDF/A create path — use <see cref="PdfDocument.SetInfo"/>/
    /// <see cref="PdfDocument.SetXmpMetadata"/> on the rendered document for general metadata.
    /// </summary>
    /// <example>
    /// <code>
    /// var manuscript = new Manuscript
    /// {
    ///     Title = "Quarterly Report",
    ///     CreateDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
    ///     ModifyDate = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
    ///     Sections = [section],
    /// };
    /// </code>
    /// </example>
    public DateTimeOffset? CreateDate { get; init; }

    /// <summary>
    /// The document's last-modification date — the XMP packet's <c>xmp:ModifyDate</c> and the
    /// <c>/Info</c> dictionary's <c>/ModDate</c> on the PDF/A create path, with exactly
    /// <see cref="CreateDate"/>'s contract (required under
    /// <see cref="PdfOptions.Deterministic"/> + PDF/A, wall-clock default otherwise, ignored
    /// outside the PDF/A create path).
    /// </summary>
    public DateTimeOffset? ModifyDate { get; init; }

    /// <summary>
    /// Lays out and renders this manuscript into a real, in-memory <see cref="PdfDocument"/>
    /// (no whole-document byte buffer — each page's content stream is built and encoded
    /// independently). The result has no backing file: save it with
    /// <see cref="PdfDocument.Save"/> to write it out.
    /// </summary>
    /// <param name="options">
    /// Options controlling layout resource limits and write determinism. Defaults to
    /// <see cref="PdfOptions.Default"/>.
    /// </param>
    /// <exception cref="PdfLayoutException">
    /// This manuscript has no sections, a section's body has no renderable content
    /// (<c>PLUME9004</c>), or the element tree cannot be laid out within the space it is
    /// given (creation input is programmer-authored, so a degenerate tree
    /// fails fast rather than rendering a silently broken document).
    /// </exception>
    /// <example>
    /// <code>
    /// using var document = manuscript.Render(new PdfOptions { Deterministic = true });
    /// </code>
    /// </example>
    public PdfDocument Render(PdfOptions? options = null) =>
        ManuscriptRenderer.Render(this, options ?? PdfOptions.Default);
}
