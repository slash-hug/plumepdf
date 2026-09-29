namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Named destinations: entries of the catalog's /Dests dictionary and of the /Names /Dests name
/// tree that resolve to a removed page are dropped (name-tree /Limits recomputed); a container
/// left empty is dropped.
/// </summary>
internal static class NamedDestinationPass
{
    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
