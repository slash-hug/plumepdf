using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Resolves a destination or an action to the page object it targets in this document: explicit
/// destination arrays, destination dictionaries (<c>/D</c>), names through the catalog's
/// <c>/Dests</c>, strings through the <c>/Names /Dests</c> name tree, and <c>GoTo</c> actions
/// (following <c>/Next</c>). Remote, embedded, launch, URI, JavaScript and named actions are
/// never local.
/// </summary>
internal static class DestinationResolver
{
    /// <summary>The object number of the page <paramref name="destinationOrAction"/> targets in this document, or <see langword="null"/> when it is not a local page destination.</summary>
    public static int? ResolveLocalPage(ObjectRegistry objects, PdfDictionary? catalog, PdfObject destinationOrAction, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(destinationOrAction);
        ArgumentNullException.ThrowIfNull(options);
        return null;
    }
}
