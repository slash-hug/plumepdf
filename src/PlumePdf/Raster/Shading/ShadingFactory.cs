using System;
using PlumePdf.Objects;
using PlumePdf.Raster.Color;
using PlumePdf.Raster.DisplayList;
using PlumePdf.Raster.Functions;

namespace PlumePdf.Raster.Shading;

/// <summary>
/// Builds a paintable <see cref="IRasterShading"/> for a <c>sh</c>-operator display-list object,
/// switching on the shading's <c>/ShadingType</c>. This is the seam:
/// shading-type dispatch lives here, not in <see cref="RasterInterpreter"/>, so mesh
/// shadings (types 4-7) can be added by extending this factory and adding new <see cref="IRasterShading"/>
/// implementations under <c>Raster/Shading/*</c> without ever touching the interpreter.
/// </summary>
/// <remarks>
/// Types 2 (axial) and 3 (radial) resolve to <b>one representative color</b> from the shading's
/// own <c>/ColorSpace</c>+<c>/Function</c> (via the real <see cref="AxialShading"/>/<see cref="RadialShading"/>
/// <c>Parse</c> entry points, so <see cref="PdfOptions.MaxShadingSamples"/> is enforced exactly as
/// documented, PLUME7721 included) and flood the clip rectangle with a <see cref="FlatColorShading"/>
/// — this is the Phase 8 behavior, unchanged. Types 4-7 (mesh shadings) build a true
/// per-pixel <see cref="MeshShading"/> when <see cref="Build"/> is given the shading's own
/// <see cref="PdfStream"/> (mesh vertex/patch data lives in the stream payload, which
/// <see cref="ShadingPageObject.Shading"/> alone — a resolved dictionary — does not carry); the
/// real <c>sh</c> operator (<see cref="RasterInterpreter.HandleSh"/>) threads that stream through
/// via <see cref="ShadingPageObject.ShadingStream"/> whenever the resolved <c>/Shading</c>
/// resource is one. A caller with no stream to supply, plus a missing
/// <c>/ColorSpace</c>/<c>/Function</c> or an indirect reference with no <see cref="ObjectRegistry"/>
/// to resolve it against, falls back to a flat mid-gray flood ("paint something recognizable,
/// never an unexplained hole," the lenient-by-default philosophy of the whole paint pass). A
/// capped (<c>PLUME7735</c>) or malformed (<c>PLUME7736</c>) mesh shading degrades the same way.
/// </remarks>
internal static class ShadingFactory
{
    private static readonly PdfName ShadingTypeName = PdfName.Get("ShadingType");
    private static readonly PdfName ColorSpaceName = PdfName.Get("ColorSpace");
    private static readonly PdfName FunctionName = PdfName.Get("Function");

    /// <summary>
    /// Builds the paintable shading for <paramref name="shading"/>. Never returns
    /// <see langword="null"/> — an unresolved shading yields a flat mid-gray
    /// <see cref="FlatColorShading"/> so the paint pass always has something to draw.
    /// </summary>
    /// <param name="shading">The <c>sh</c>-operator display-list object (carries the <c>/Shading</c> dictionary and the paint-time fill alpha).</param>
    /// <param name="options">Threaded to shading color resolution (<see cref="PdfOptions.MaxShadingSamples"/>); <see langword="null"/> degrades to the flat mid-gray placeholder.</param>
    /// <param name="objects">Resolves indirect references a shading's <c>/ColorSpace</c>/<c>/Function</c> entries make, or <see langword="null"/> when none exist.</param>
    /// <param name="diagnostics">Recoverable deviations encountered while resolving a shading's color are appended here.</param>
    /// <param name="shadingStream">
    /// The shading's own stream object, when the caller has one (mesh shadings — types 4-7 —
    /// are always stream objects; <see cref="ShadingPageObject.Shading"/> alone only carries the
    /// resolved dictionary, not the stream's vertex/patch payload bytes).
    /// <see cref="RasterInterpreter.HandleSh"/> — the <c>sh</c> operator's own resource lookup —
    /// supplies <see cref="ShadingPageObject.ShadingStream"/> here whenever the resolved
    /// <c>/Shading</c> resource was itself a stream, so a real mesh <c>sh</c> invocation builds a
    /// true per-pixel <see cref="MeshShading"/> rather than the types-4-7 mid-gray
    /// fallback. <see langword="null"/> (the default) is that fallback path: types 2/3 still
    /// resolve to one representative color as before, and types 4-7 flood mid-gray — the shape
    /// <c>Build</c> falls back to for any other caller (e.g. a <c>/PatternType 2</c> shading
    /// pattern) that has not yet threaded a stream through.
    /// </param>
    public static IRasterShading Build(ShadingPageObject shading, PdfOptions? options, ObjectRegistry? objects, DiagnosticCollection? diagnostics, PdfStream? shadingStream = null)
    {
        var alpha = (byte)Math.Clamp((int)Math.Round(shading.FillAlpha * 255), 0, 255);

        if (options is not null && shadingStream is not null)
        {
            var mesh = TryBuildMeshShading(shading, shadingStream, options, objects, diagnostics, alpha);
            if (mesh is not null)
            {
                return mesh;
            }
        }

        var (b, g, r) = TryResolveShadingColor(shading, options, objects, diagnostics) ?? ((byte)128, (byte)128, (byte)128);
        return new FlatColorShading(b, g, r, alpha);
    }

    /// <summary>
    /// Builds a mesh shading (types 4-7) from <paramref name="shadingStream"/>, or
    /// <see langword="null"/> when the shading isn't a mesh type, has no <c>/ColorSpace</c>, or
    /// its vertex/patch data is capped-out or malformed — every one of those degrades to
    /// <see cref="Build"/>'s existing flat mid-gray fallback rather than aborting the page's
    /// paint pass, mirroring <see cref="TryResolveShadingColor"/>'s own lenient-by-default catch.
    /// </summary>
    private static IRasterShading? TryBuildMeshShading(ShadingPageObject shading, PdfStream shadingStream, PdfOptions options, ObjectRegistry? objects, DiagnosticCollection? diagnostics, byte alpha)
    {
        var dict = shading.Shading;
        Func<IndirectReference, PdfObject> resolve = objects is not null
            ? r => objects[r]
            : r => throw new PlumePdfException("PLUME7504", "A /Shading dictionary referenced an indirect object, but this Paint call has no ObjectRegistry to resolve it against.");

        if (!dict.TryGetValue(ShadingTypeName, out var shadingTypeObj)
            || FunctionEvaluator.Resolve(shadingTypeObj, resolve) is not PdfNumber { IsInteger: true } shadingTypeNumber
            || shadingTypeNumber.Value is not (4 or 5 or 6 or 7))
        {
            return null;
        }

        if (!dict.TryGetValue(ColorSpaceName, out var colorSpaceObj))
        {
            return null; // Malformed mesh shading with no /ColorSpace — fall back to flat mid-gray.
        }

        try
        {
            var colorSpace = ColorSpace.Parse(colorSpaceObj, resolve, options.Filters, options, diagnostics);
            PdfFunction? function = dict.TryGetValue(FunctionName, out var functionObj)
                ? FunctionEvaluator.ParseArray(functionObj, resolve, options.Filters, options, diagnostics)
                : null;

            return MeshShading.Parse(shadingStream, colorSpace, function, shading.Ctm, alpha, resolve, options, diagnostics);
        }
        catch (PlumePdfException)
        {
            // A capped (PLUME7735) or malformed (PLUME7736) mesh degrades to the mid-gray
            // placeholder rather than aborting the whole page's paint pass — the same
            // lenient-by-default posture TryResolveShadingColor's catch already uses below.
            return null;
        }
    }

    private static (byte B, byte G, byte R)? TryResolveShadingColor(ShadingPageObject shading, PdfOptions? options, ObjectRegistry? objects, DiagnosticCollection? diagnostics)
    {
        if (options is null)
        {
            return null;
        }

        var dict = shading.Shading;
        Func<IndirectReference, PdfObject> resolve = objects is not null
            ? r => objects[r]
            : r => throw new PlumePdfException("PLUME7504", "A /Shading dictionary referenced an indirect object, but this Paint call has no ObjectRegistry to resolve it against.");

        try
        {
            if (!dict.TryGetValue(ShadingTypeName, out var shadingTypeObj)
                || FunctionEvaluator.Resolve(shadingTypeObj, resolve) is not PdfNumber { IsInteger: true } shadingTypeNumber)
            {
                return null;
            }

            if (!dict.TryGetValue(ColorSpaceName, out var colorSpaceObj) || !dict.TryGetValue(FunctionName, out var functionObj))
            {
                return null; // e.g. a mesh shading (types 4-7) with per-vertex colors and no single /Function — out of this minimal path's scope.
            }

            var coords = FunctionEvaluator.TryNumberArray(dict, "Coords", resolve);
            var colorSpace = ColorSpace.Parse(colorSpaceObj, resolve, options.Filters, options, diagnostics);
            var function = FunctionEvaluator.ParseArray(functionObj, resolve, options.Filters, options, diagnostics);

            // PDFium's own flat-ramp convention (AxialShading's remarks) is 256 steps — never
            // more than that for this one-color-sample use, and never more than the document's
            // own MaxShadingSamples cap (enforced for real by Parse below, PLUME7721).
            var sampleCount = Math.Min(256, Math.Max(2, options.MaxShadingSamples));

            return shadingTypeNumber.Value switch
            {
                2 when coords is { Length: 4 } => AxialShading.Parse(dict, colorSpace, function, resolve, sampleCount, options.MaxShadingSamples)
                    .TryGetColor(coords[0], coords[1], out var ar, out var ag, out var ab) ? (ab, ag, ar) : null,
                3 when coords is { Length: 6 } => RadialShading.Parse(dict, colorSpace, function, resolve, sampleCount, options.MaxShadingSamples)
                    .TryGetColor(coords[0], coords[1], out var rr, out var rg, out var rb) ? (rb, rg, rr) : null,
                _ => null,
            };
        }
        catch (PlumePdfException)
        {
            // A malformed/unsupported shading (e.g. MaxShadingSamples exceeded, an
            // unparseable /ColorSpace or /Function) degrades to the mid-gray placeholder rather
            // than aborting the whole page's paint pass — lenient-by-default, same as every
            // other paint-time deviation in this class.
            return null;
        }
    }
}
