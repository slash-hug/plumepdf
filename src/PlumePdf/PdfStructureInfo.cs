using PlumePdf.Documents.Structure;

namespace PlumePdf;

/// <summary>
/// The public facade over a document's tagged-PDF / PDF/UA structure tree (ISO 32000-1 §14.7)
/// — whether the document is marked as tagged (<see cref="IsTagged"/>), its
/// declared natural language (<see cref="Language"/>), and the parsed structure tree itself
/// (<see cref="Root"/>). Reading is lenient (like the rest of the reading engine): a malformed
/// or absent structure tree never throws — <see cref="Root"/> is simply <see langword="null"/>,
/// and any deviation tolerated along the way is recorded to this instance's own
/// <see cref="Diagnostics"/> (scoped per-read, the same "extraction results carry their own
/// diagnostics" pattern <c>ExtractedText</c> already established, rather than growing
/// <c>doc.Diagnostics</c> unboundedly across repeated calls).
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("report.pdf");
/// var structure = PdfStructureInfo.For(document);
///
/// if (structure.IsTagged &amp;&amp; structure.Root is { } root)
/// {
///     Console.WriteLine($"Language: {structure.Language}");
///     PrintTree(root, indent: 0);
/// }
///
/// static void PrintTree(PdfStructureNode node, int indent)
/// {
///     if (node is PdfStructureElement element)
///     {
///         Console.WriteLine(new string(' ', indent) + element.Role);
///         foreach (var child in element.Children)
///         {
///             PrintTree(child, indent + 2);
///         }
///     }
/// }
/// </code>
/// </example>
public sealed class PdfStructureInfo
{
    private readonly Lazy<StructureTreeReadResult> _read;

    private PdfStructureInfo(PdfDocument document)
    {
        Diagnostics = new DiagnosticCollection();
        _read = new Lazy<StructureTreeReadResult>(() => StructureTreeReader.Read(document, document.Options, Diagnostics));
    }

    /// <summary>Returns the structure facade for <paramref name="document"/>. Reading the tree is deferred until <see cref="IsTagged"/>/<see cref="Root"/> is first used.</summary>
    public static PdfStructureInfo For(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PdfStructureInfo(document);
    }

    /// <summary>Whether the document's catalog declares itself tagged (<c>/MarkInfo &lt;&lt; /Marked true &gt;&gt;</c>). A document can be tagged with no readable <see cref="Root"/> (a malformed <c>/StructTreeRoot</c>) — check both when correctness matters.</summary>
    public bool IsTagged => _read.Value.IsMarked;

    /// <summary>The document's declared natural language (catalog <c>/Lang</c>, a BCP 47 tag such as <c>"en-US"</c>), or <see langword="null"/> if not declared.</summary>
    public string? Language => _read.Value.Language;

    /// <summary>The document's structure tree root, or <see langword="null"/> when there is no catalog, no resolvable <c>/StructTreeRoot</c>, or it has no children.</summary>
    public PdfStructureNode? Root => _read.Value.Root is { } root ? Convert(root) : null;

    /// <summary>Deviations tolerated while reading the structure tree (a cycle, a dangling <c>/Pg</c>, an over-deep tree truncated at <see cref="PdfOptions.MaxObjectNestingDepth"/>) — populated once, the first time the tree is actually read.</summary>
    public DiagnosticCollection Diagnostics { get; }

    private static PdfStructureNode Convert(StructureTreeNode node) => node switch
    {
        StructureElement element => new PdfStructureElement(
            element.Role,
            element.Language,
            element.AlternateText,
            element.ActualText,
            element.TableHeaderScope,
            [.. element.Children.Select(Convert)]),
        MarkedContentReference mcr => new PdfMarkedContentReference(mcr.PageIndex, mcr.Mcid),
        _ => throw new InvalidOperationException($"Unreachable: unknown {nameof(StructureTreeNode)} subtype {node.GetType()}."),
    };
}

/// <summary>One node of a <see cref="PdfStructureInfo.Root"/> tree — either a <see cref="PdfStructureElement"/> or a <see cref="PdfMarkedContentReference"/> leaf.</summary>
public abstract class PdfStructureNode
{
}

/// <summary>
/// A structure element: a structure type (<see cref="Role"/>, e.g. <c>"P"</c>, <c>"H1"</c>,
/// <c>"Figure"</c>, <c>"Table"</c>, <c>"TR"</c>, <c>"TH"</c>, <c>"TD"</c> — ISO 32000-1 Table 351)
/// plus ordered child nodes.
/// </summary>
public sealed class PdfStructureElement : PdfStructureNode
{
    internal PdfStructureElement(string role, string? language, string? alternateText, string? actualText, string? tableHeaderScope, IReadOnlyList<PdfStructureNode> children)
    {
        Role = role;
        Language = language;
        AlternateText = alternateText;
        ActualText = actualText;
        TableHeaderScope = tableHeaderScope;
        Children = children;
    }

    /// <summary>The structure type, e.g. <c>"Document"</c>, <c>"P"</c>, <c>"H1"</c>, <c>"Figure"</c>, <c>"Table"</c>.</summary>
    public string Role { get; }

    /// <summary>This element's natural-language identifier (<c>/Lang</c>), or <see langword="null"/> to inherit from an ancestor/<see cref="PdfStructureInfo.Language"/>.</summary>
    public string? Language { get; }

    /// <summary>Alternate, human-readable description for non-text content (<c>/Alt</c>) — a <c>Figure</c> should always have one under PDF/UA.</summary>
    public string? AlternateText { get; }

    /// <summary>A replacement text for extraction/accessibility purposes (<c>/ActualText</c>), or <see langword="null"/>.</summary>
    public string? ActualText { get; }

    /// <summary>For a <c>"TH"</c> table-header cell: <c>"Row"</c>, <c>"Column"</c>, or <c>"Both"</c> — <see langword="null"/> for every other role.</summary>
    public string? TableHeaderScope { get; }

    /// <summary>This element's children, in document (reading) order.</summary>
    public IReadOnlyList<PdfStructureNode> Children { get; }
}

/// <summary>A leaf node referencing one marked-content span painted on a page — the structure tree's connection back to actual page content.</summary>
public sealed class PdfMarkedContentReference : PdfStructureNode
{
    internal PdfMarkedContentReference(int pageIndex, int mcid)
    {
        PageIndex = pageIndex;
        Mcid = mcid;
    }

    /// <summary>The zero-based index, into <c>document.Pages</c>, of the page this marked-content span was painted on.</summary>
    public int PageIndex { get; }

    /// <summary>The marked-content sequence's <c>MCID</c>, unique within <see cref="PageIndex"/>'s content stream.</summary>
    public int Mcid { get; }
}
