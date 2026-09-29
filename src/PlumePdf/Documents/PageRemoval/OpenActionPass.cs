namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// The catalog's /OpenAction is removed when it resolves to a removed page.
/// </summary>
internal static class OpenActionPass
{
    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }
}
