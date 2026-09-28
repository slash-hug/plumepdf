namespace PlumePdf.Elements;

/// <summary>
/// One node of a <see cref="Manuscript"/>'s tree (CONTEXT.md "Element") — a
/// <see cref="Section"/>'s <c>Header</c>/<c>Body</c>/<c>Footer</c> is built out of these.
/// Immutable data (Variant B): construct a full tree with object initializers, hand
/// it to <see cref="Manuscript.Render(PdfOptions?)"/> or <see cref="PdfDocument.Compose"/>.
/// Elements carry no layout state of their own — <c>PlumePdf.Layout</c> measures and arranges
/// a tree without mutating it, so the same <see cref="Element"/> instance can be rendered
/// more than once (e.g. a shared header reused across several <see cref="Section"/>s).
/// </summary>
/// <example>
/// <code>
/// Elements.Element body = new Column(
///     new Text("Bill to: Acme Corp"),
///     new Text("Total: $500.00") { Bold = true });
/// </code>
/// </example>
public abstract class Element
{
    /// <summary>
    /// How this element positions itself within the extra space its parent leaves for it, or
    /// <see langword="null"/> to use the parent's default (left/start). Only containers that
    /// document their own alignment behavior (e.g. <see cref="Row"/>'s <c>Justify</c>) honor
    /// per-child alignment beyond this; most block containers (<see cref="Column"/>) fill the
    /// full available width and this has no effect there except for <see cref="Text"/>'s own
    /// line alignment.
    /// </summary>
    public HorizontalAlign? Align { get; init; }

    /// <summary>
    /// An explicit PDF logical-structure type (ISO 32000-1 Table 351, e.g. <c>"P"</c>,
    /// <c>"H1"</c>, <c>"Figure"</c>, <c>"L"</c>) to tag this element's painted content with,
    /// overriding whatever role <c>ManuscriptRenderer</c> would otherwise infer for it (a
    /// <see cref="Text"/>'s <see cref="Text.HeadingLevel"/>-derived heading or plain
    /// <c>"P"</c>, an <see cref="Image"/>'s <c>"Figure"</c>). Only takes effect when
    /// <see cref="Manuscript.Language"/> is set — that is what opts a manuscript into tagged
    /// output at all (see the cookbook, "Create a tagged PDF").
    /// </summary>
    /// <remarks>
    /// The one reserved value is the literal string <c>"Artifact"</c>: it marks this element's
    /// content as pagination furniture (ISO 32000-1 §14.8.2.2) — excluded from the structure
    /// tree entirely rather than added to it with that role name — the escape hatch for a
    /// decorative <see cref="Image"/>/<see cref="Text"/> that would otherwise trip the
    /// "every <see cref="Image"/> needs <see cref="Image.AltText"/>" tagged-output refusal
    /// (<c>PLUME9010</c>). <see cref="Watermark"/> and <see cref="Stamp"/> are always marked
    /// this way automatically; setting <c>Role = "Artifact"</c> on an ordinary <see cref="Element"/>
    /// does the same thing explicitly.
    /// </remarks>
    /// <example>
    /// <code>
    /// var decorativeDivider = new Image(pixels, 4, 1) { Role = "Artifact" }; // no AltText needed
    /// var pullQuote = new Text("“Simplicity is the ultimate sophistication.”") { Role = "BlockQuote" };
    /// </code>
    /// </example>
    public string? Role { get; init; }
}

/// <summary>
/// Horizontal alignment for an <see cref="Element"/> or a line of <see cref="Text"/>.
/// <see cref="Left"/>/<see cref="Right"/> are physical — they mean the same side of the page
/// no matter what direction the text reads, and <see cref="Text"/> defaults to physical-left
/// behavior when <see cref="Element.Align"/> is left <see langword="null"/>, exactly as every
/// <see cref="Text"/> rendered before <see cref="Start"/>/<see cref="End"/> existed. Use
/// <see cref="Start"/>/<see cref="End"/> (added for Phase 6.5's right-to-left support) when
/// the alignment should follow a <see cref="Text.Direction"/>-resolved paragraph's own reading
/// direction instead — the leading/trailing edge of the text, not a fixed page side.
/// </summary>
public enum HorizontalAlign
{
    /// <summary>Aligned to the physical left edge of the available space, regardless of text direction. Never reinterpreted as a logical side — see this type's remarks.</summary>
    Left,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>Aligned to the physical right edge of the available space, regardless of text direction. Never reinterpreted as a logical side — see this type's remarks.</summary>
    Right,

    /// <summary>
    /// Aligned to the leading edge of the text's own reading direction: the physical left for a
    /// left-to-right paragraph, the physical right for a right-to-left one (<see cref="Text.Direction"/>).
    /// On a <see cref="Text"/> whose resolved direction is left-to-right, behaves exactly like
    /// <see cref="Left"/> — including on every pre-Phase-6.5 document, which never sets
    /// <see cref="Text.Direction"/> and so always resolves left-to-right.
    /// </summary>
    Start,

    /// <summary>
    /// Aligned to the trailing edge of the text's own reading direction: the physical right for a
    /// left-to-right paragraph, the physical left for a right-to-left one (<see cref="Text.Direction"/>).
    /// On a <see cref="Text"/> whose resolved direction is left-to-right, behaves exactly like
    /// <see cref="Right"/>.
    /// </summary>
    End,
}
