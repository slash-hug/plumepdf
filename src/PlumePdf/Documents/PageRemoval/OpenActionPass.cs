namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// The catalog's /OpenAction is removed when it resolves to a removed page — an explicit
/// destination, or a <c>GoTo</c> action whose chain's first <c>GoTo</c> lands there.
/// </summary>
internal static class OpenActionPass
{
    private static readonly PdfName OpenActionName = PdfName.Get("OpenAction");

    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RemovedPages.Count == 0 || context.Catalog is not { } catalog
            || !catalog.TryGetValue(OpenActionName, out var openAction)
            || DestinationResolver.ForOriginalNames(context).Resolve(openAction) is not int page
            || !context.RemovedPages.Contains(page))
        {
            return;
        }

        var copy = new PdfDictionary();
        foreach (var (key, value) in catalog)
        {
            if (!ReferenceEquals(key, OpenActionName))
            {
                copy.Set(key, value);
            }
        }

        context.Catalog = copy;
        context.Counts.OpenActions = 1;

        // An indirect action object is dropped with it (never the catalog or a kept page, which a
        // malformed /OpenAction could name).
        if (openAction is PdfReference reference && reference.Target.Number != context.CatalogReference?.Number
            && !context.Pages.Exists(p => p.Reference.Number == reference.Target.Number))
        {
            context.Excluded.Add(reference.Target.Number);
        }
    }
}
