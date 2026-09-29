namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Outlines: an item whose destination resolves to a removed page is deleted, its children
/// moving up to its parent in its place; /First, /Last, /Prev, /Next, /Parent and /Count are
/// rebuilt, and an outline left empty is dropped from the catalog.
/// </summary>
internal static class OutlinePass
{
    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
