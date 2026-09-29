namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Runs the save-time clean-up passes after pages were removed: each pass rewrites (as copies) one
/// kind of structure that pointed at a removed page — outlines, the form, links, the open action,
/// named destinations, the structure tree. The passes run in a fixed order; each later pass sees
/// every object the earlier ones added to <see cref="SaveCleanupContext.Excluded"/>. Whatever a
/// pass leaves alone is still safe: <see cref="RemovedSetBuilder"/>'s exclusion set already keeps
/// removed-page data out of the output, with <c>null</c> where it was referenced.
/// </summary>
internal static class SaveCleanup
{
    /// <summary>Applies every pass to <paramref name="context"/>.</summary>
    public static void Compute(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RemovedPages.Count == 0)
        {
            return;
        }

        AnnotsPass.Apply(context);
        AcroFormPass.Apply(context);

        // Before the outline: bookmarks name structure elements (/SE) the structure pass prunes.
        StructureTreePass.Apply(context);
        OutlinePass.Apply(context);
        NamedDestinationPass.Apply(context);
        OpenActionPass.Apply(context);
    }

    /// <summary>
    /// The passes that matter when pages are imported into a new document (<c>Pdf.Split</c>,
    /// <c>Pdf.Merge</c>): the new document gets a fresh catalog, so outlines, named destinations,
    /// the open action and the structure tree are not carried and are not rewritten.
    /// </summary>
    public static void ComputeForImport(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RemovedPages.Count == 0)
        {
            return;
        }

        AnnotsPass.Apply(context);
        AcroFormPass.Apply(context);
    }
}
