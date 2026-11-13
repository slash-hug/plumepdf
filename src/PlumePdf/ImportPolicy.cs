namespace PlumePdf;

/// <summary>
/// What <see cref="PageGraphCopier"/> leaves out of the copies it makes. Merge and Split copy
/// everything (<see cref="Merge"/>); a page added to an opened document drops what only means
/// something in its source (<see cref="PageAdd"/>).
/// </summary>
/// <param name="StripStructureKeys">
/// Drop <c>/StructParents</c> and <c>/StructParent</c> wherever the keys appear (on pages,
/// annotations and Form and image XObjects, the dictionaries that use them), and an action's
/// <c>/SD</c> structure destination. They point into the source's structure tree, which is not
/// copied, and in a tagged destination the keys would collide with its own entries.
/// </param>
/// <param name="DropArticleBeads">Drop a page's <c>/B</c> (article beads): their thread lives in the source's catalog.</param>
/// <param name="DropSignatureValues">
/// Drop <c>/V</c> and <c>/AP</c> from signature fields (<c>/FT /Sig</c>, on the field itself or
/// inherited from a parent field): the signature's <c>/ByteRange</c> covers the source file's
/// bytes, so in the destination it would only read as a broken signature, and its appearance
/// would show a signature that is no longer there. The field comes along empty, keeping
/// <c>/Lock</c> and <c>/SV</c>.
/// </param>
/// <param name="LeaveOutDocumentStructures">
/// Treat the source's structure tree (from <c>/StructTreeRoot</c>) and outline (from
/// <c>/Outlines</c>) as left out, like pages that aren't copied: a reference into either is copied
/// as <c>null</c>. They belong to the source document, and following one (a link's structure
/// destination, an action's <c>/Next</c> naming an outline item, ...) would bring along content
/// of the source's other pages.
/// </param>
internal sealed record ImportPolicy(bool StripStructureKeys, bool DropArticleBeads, bool DropSignatureValues, bool LeaveOutDocumentStructures)
{
    /// <summary>Copy everything: Merge and Split.</summary>
    public static ImportPolicy Merge { get; } = new(StripStructureKeys: false, DropArticleBeads: false, DropSignatureValues: false, LeaveOutDocumentStructures: false);

    /// <summary>A page added to an opened document.</summary>
    public static ImportPolicy PageAdd { get; } = new(StripStructureKeys: true, DropArticleBeads: true, DropSignatureValues: true, LeaveOutDocumentStructures: true);
}
