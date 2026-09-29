namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Runs the save-time clean-up after pages were removed, so the rewritten document has no
/// bookmark, form field, link, open action, named destination or structure element that points
/// at a removed page. The passes run in a fixed order; each later pass sees every object the
/// earlier ones added to <see cref="SaveCleanupContext.Excluded"/>.
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
        OutlinePass.Apply(context);
        NamedDestinationPass.Apply(context);
        OpenActionPass.Apply(context);
        StructureTreePass.Apply(context);
    }
}
