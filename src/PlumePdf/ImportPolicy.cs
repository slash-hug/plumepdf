namespace PlumePdf;

/// <summary>
/// What <see cref="PageGraphCopier"/> leaves out of the copies it makes. Merge and Split copy
/// everything (<see cref="Merge"/>); a page added to an opened document drops what only means
/// something in its source (<see cref="PageAdd"/>).
/// </summary>
/// <param name="StripStructureKeys">
/// Drop <c>/StructParents</c> and <c>/StructParent</c> wherever they appear (pages, annotations,
/// Form and image XObjects). They are keys into the source's structure-tree <c>/ParentTree</c>,
/// which is not copied, and in a tagged destination they would collide with its own entries.
/// </param>
/// <param name="DropArticleBeads">Drop a page's <c>/B</c> (article beads): their thread lives in the source's catalog.</param>
/// <param name="DropSignatureValues">
/// Drop <c>/V</c> from signature fields (<c>/FT /Sig</c>): the signature's <c>/ByteRange</c>
/// covers the source file's bytes, so in the destination it would only read as a broken
/// signature. The field comes along empty.
/// </param>
internal sealed record ImportPolicy(bool StripStructureKeys, bool DropArticleBeads, bool DropSignatureValues)
{
    /// <summary>Copy everything: Merge and Split.</summary>
    public static ImportPolicy Merge { get; } = new(StripStructureKeys: false, DropArticleBeads: false, DropSignatureValues: false);

    /// <summary>A page added to an opened document.</summary>
    public static ImportPolicy PageAdd { get; } = new(StripStructureKeys: true, DropArticleBeads: true, DropSignatureValues: true);
}
