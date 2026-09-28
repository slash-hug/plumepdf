namespace PlumePdf.Documents.Structure;

/// <summary>
/// One node of a tagged PDF's logical structure tree (ISO 32000-1 §14.7) — the single shared
/// model both <see cref="StructureTreeBuilder"/> (write side) and
/// <see cref="StructureTreeReader"/> (read side) build/consume, so a document PlumePDF wrote
/// and then re-opens describes the same tree the same way (round-trip equality is
/// <c>StructureTreeTests</c>' core coverage). Either a <see cref="StructureElement"/> (a
/// structure type plus child nodes) or a <see cref="MarkedContentReference"/> leaf pointing at
/// one <c>BDC</c>/<c>EMC</c> marked-content span painted on a page.
/// </summary>
internal abstract class StructureTreeNode
{
    /// <summary>
    /// The structure element this node is nested under, or <see langword="null"/> for the tree's
    /// root. Set by whichever of <see cref="StructureTreeBuilder"/>/<see cref="StructureTreeReader"/>
    /// added this node as a child — used only to reconstruct a human-readable tree path for a
    /// coded refusal (e.g. <c>PLUME9010</c>'s "naming the element and its tree path"
    /// requirement); never consulted for the round-trip identity itself.
    /// </summary>
    public StructureElement? Parent { get; set; }
}

/// <summary>
/// A structure element: a structure type (<see cref="Role"/>, e.g. <c>"P"</c>, <c>"H1"</c>,
/// <c>"Figure"</c>, <c>"Table"</c> — ISO 32000-1 Table 351's standard structure types) plus
/// ordered child nodes, each either a nested <see cref="StructureElement"/> or a
/// <see cref="MarkedContentReference"/> leaf.
/// </summary>
internal sealed class StructureElement : StructureTreeNode
{
    /// <summary>The structure type, e.g. <c>"Document"</c>, <c>"P"</c>, <c>"H1"</c>, <c>"Figure"</c>, <c>"Table"</c>, <c>"TR"</c>, <c>"TH"</c>, <c>"TD"</c>.</summary>
    public required string Role { get; init; }

    /// <summary>This element's natural-language identifier (<c>/Lang</c>, a BCP 47 tag), or <see langword="null"/> to inherit from an ancestor/the catalog's <c>/Lang</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Alternate, human-readable description for non-text content (<c>/Alt</c>) — required by PDF/UA for every <c>Figure</c>.</summary>
    public string? AlternateText { get; init; }

    /// <summary>A replacement text PlumePDF should treat as this element's actual content for extraction/accessibility purposes (<c>/ActualText</c>), or <see langword="null"/> to use the element's own marked content.</summary>
    public string? ActualText { get; init; }

    /// <summary>For a <c>TH</c> table-header cell: which axis it's a header for — <c>"Row"</c>, <c>"Column"</c>, or <c>"Both"</c> (the <c>/A</c> table attribute's <c>/Scope</c>, ISO 32000-1 §14.8.5.6). <see langword="null"/> for every other role.</summary>
    public string? TableHeaderScope { get; init; }

    /// <summary>
    /// This element's children, in document (reading) order. A plain mutable list rather than
    /// an immutable snapshot: <see cref="Layout.ManuscriptRenderer"/> builds a node once per
    /// distinct source <c>Element</c> and appends further children to it as rendering discovers
    /// more occurrences (a repeating header/footer/table-header row paints on several pages, each
    /// occurrence contributing one more <see cref="MarkedContentReference"/> to the same node).
    /// </summary>
    public List<StructureTreeNode> Children { get; } = [];
}

/// <summary>
/// A leaf node referencing one marked-content span (<c>BDC</c>/<c>EMC</c>,
/// <see cref="Content.ContentStreamBuilder.BeginTaggedContent"/>) painted on a page — the
/// structure tree's connection back to actual page content, mirrored in the <c>/ParentTree</c>
/// number tree so extraction can walk from a page's marked content back up to the structure
/// element that owns it.
/// </summary>
internal sealed class MarkedContentReference : StructureTreeNode
{
    /// <summary>The zero-based index, into the finished document's page list, of the page this marked-content span was painted on.</summary>
    public required int PageIndex { get; init; }

    /// <summary>The marked-content sequence's <c>MCID</c>, unique within <see cref="PageIndex"/>'s content stream.</summary>
    public required int Mcid { get; init; }
}

/// <summary>
/// The handful of ISO 32000-1 Table 351 standard structure types PlumePDF's creation and
/// reading sides recognize by name (plan scope: headings, paragraphs, figures, tables, and the
/// synthetic tree-root wrapper <see cref="StructureTreeReader"/> uses when a real
/// <c>/StructTreeRoot</c> has more than one top-level child). Any other role name a document
/// (self-authored or third-party) uses round-trips as plain text in <see cref="StructureElement.Role"/> —
/// this list is a set of well-known constants for callers to compare against, not an exhaustive
/// enum, since a real-world structure tree may use any of ISO 32000-1's ~30 standard types or a
/// custom one mapped via <c>/RoleMap</c>.
/// </summary>
internal static class StructureRoles
{
    /// <summary>The document's single top-level element, per ISO 32000-1 Table 351.</summary>
    public const string Document = "Document";

    /// <summary>An ordinary body paragraph.</summary>
    public const string Paragraph = "P";

    /// <summary>A figure (image, chart, diagram) — requires <see cref="StructureElement.AlternateText"/> for PDF/UA conformance.</summary>
    public const string Figure = "Figure";

    /// <summary>A table.</summary>
    public const string Table = "Table";

    /// <summary>A table row, nested directly under <see cref="Table"/>.</summary>
    public const string TableRow = "TR";

    /// <summary>A table header cell, nested under <see cref="TableRow"/>.</summary>
    public const string TableHeaderCell = "TH";

    /// <summary>A table data cell, nested under <see cref="TableRow"/>.</summary>
    public const string TableDataCell = "TD";

    /// <summary>
    /// Not a real ISO 32000-1 structure type — the sentinel <see cref="Layout.ManuscriptRenderer"/>
    /// and <see cref="Elements.Element.Role"/> use to mark content as a <c>/Artifact</c>
    /// marked-content span (pagination furniture: page numbers, running headers, watermarks,
    /// stamps) instead of a real structure element. Content marked this way is excluded from the
    /// structure tree entirely (ISO 32000-1 §14.8.2.2).
    /// </summary>
    public const string Artifact = "Artifact";

    /// <summary>Returns <c>"H1"</c>–<c>"H6"</c> for <paramref name="level"/> 1–6.</summary>
    public static string Heading(int level) => $"H{level}";
}
