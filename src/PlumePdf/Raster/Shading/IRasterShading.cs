namespace PlumePdf.Raster.Shading;

/// <summary>
/// A resolved, paintable shading — the seam that keeps shading-type
/// dispatch out of <see cref="RasterInterpreter"/>. <see cref="ShadingFactory.Build"/> switches on
/// the <c>/ShadingType</c> and returns the right implementation; the interpreter then calls
/// <see cref="Paint"/> without ever knowing the concrete type. This is what lets mesh
/// shadings (types 4-7) be added entirely inside <c>Raster/Shading/*</c> — a new <see cref="IRasterShading"/>
/// plus a factory branch — without touching the interpreter.
/// </summary>
internal interface IRasterShading
{
    /// <summary>
    /// Paints this shading within <paramref name="clip"/> on <paramref name="surface"/> — the
    /// window's half-open bounds are already intersected with the surface and the object's clip
    /// chain, and a non-rectangular clip region additionally carries a per-pixel coverage map
    /// every painted sample must multiply through.
    /// </summary>
    void Paint(RasterSurface surface, ClipWindow clip);
}
