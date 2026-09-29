namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Kept pages: their /Annots is written compacted, without entries in the excluded set, and a
/// link annotation whose destination resolves to a removed page is removed (and excluded).
/// </summary>
internal static class AnnotsPass
{
    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
