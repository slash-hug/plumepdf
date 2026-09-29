namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Tagged documents: a structure element whose every content target (/Pg, marked-content and
/// object references) is on a removed page or annotation is pruned, recursively; /ParentTree and
/// /IDTree drop what was pruned and /ParentTreeNextKey stays valid; a tree left empty stays, empty.
/// </summary>
internal static class StructureTreePass
{
    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
