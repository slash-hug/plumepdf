using PlumePdf.Objects;

namespace PlumePdf.Raster.Annotations;

/// <summary>
/// Decides when a widget annotation needs an in-memory, render-time-only appearance synthesized
/// (a <c>/V</c>-no-<c>/AP</c> widget, or any widget when the AcroForm's <c>NeedAppearances</c> is
/// set) and, if so, obtains one through the injected <see cref="Resolver"/> — never by calling
/// Phase 4's real generator (<c>Documents.Forms.Appearances.AppearanceGenerator</c>) directly.
/// </summary>
/// <remarks>
/// <para>
/// The real generator lives in <c>PlumePdf.Documents</c>, a layer <em>above</em>
/// <c>PlumePdf.Raster</c> (<c>docs/architecture.md</c> "Raster sits above Content and below
/// Documents"; enforced by <c>PlumePdf.ArchitectureTests.LayeringTests</c>) — so this Raster-layer
/// type cannot reference it, exactly the same layering reason
/// <see cref="RasterInterpreter.ImageResolver"/> is injected rather than implemented in
/// <see cref="RasterInterpreter"/> itself (image-XObject decoding is a <c>Documents.ImageExtractor</c>
/// concern). <see cref="Resolver"/> is this type's mirror of that seam for widget-appearance
/// synthesis.
/// </para>
/// <para>
/// A <see cref="Resolver"/> implementation is responsible for the read-only invariant itself:
/// any new object it needs to allocate — the appearance form XObject, a
/// substitute font dictionary, and so on — must go through a scratch <c>ObjectRegistry</c>
/// (<c>Raster.ScratchObjectRegistry.CreateFor</c>), never the real document's own
/// <c>document.Objects</c>, so the returned <see cref="PdfStream"/> is a value nothing durable
/// was written to reach. This type's own job is orchestration only (deciding <em>whether</em> to
/// call the resolver, per the annotation's <c>/V</c>/<c>NeedAppearances</c> state) — it never
/// touches an <see cref="ObjectRegistry"/> itself, so it cannot get that invariant wrong.
/// </para>
/// </remarks>
internal static class WidgetAppearanceSynthesizer
{
    private static readonly PdfName VName = PdfName.V;

    /// <summary>
    /// Synthesizes (or otherwise supplies) one widget's normal appearance as a ready-to-interpret
    /// form XObject stream. Implementations should return <see langword="null"/> when they cannot
    /// produce one (missing font, malformed value, etc.) rather than throwing — a widget this
    /// fails for degrades to "not painted", the same lenient posture <see cref="RasterInterpreter.ImageResolver"/>
    /// callers use for an undecodable image.
    /// </summary>
    /// <param name="widget">The widget annotation dictionary (the merged field+widget shape is the norm).</param>
    /// <param name="diagnostics">Where the implementation should record any recoverable generation deviation.</param>
    public delegate PdfStream? Resolver(PdfDictionary widget, DiagnosticCollection? diagnostics);

    /// <summary>
    /// Attempts to obtain <paramref name="widget"/>'s synthesized appearance via
    /// <paramref name="resolver"/>. Returns <see langword="false"/> — with no diagnostic of its
    /// own — when <paramref name="resolver"/> is <see langword="null"/> (no synthesis capability
    /// injected; mirrors an omitted <c>ImageResolver</c> degrading an image to "not painted"),
    /// when the widget has neither a usable <c>/V</c> nor <paramref name="needAppearances"/> is
    /// set (nothing to synthesize from), or when <paramref name="resolver"/> itself declines.
    /// </summary>
    public static bool TryResolveAppearance(PdfDictionary widget, bool needAppearances, Resolver? resolver, DiagnosticCollection? diagnostics, out PdfStream appearance)
    {
        ArgumentNullException.ThrowIfNull(widget);
        appearance = null!;

        if (resolver is null)
        {
            return false;
        }

        var hasValue = widget.TryGetValue(VName, out var value) && value is not PdfNull;
        if (!hasValue && !needAppearances)
        {
            return false;
        }

        var result = resolver(widget, diagnostics);
        if (result is null)
        {
            return false;
        }

        appearance = result;
        return true;
    }
}
