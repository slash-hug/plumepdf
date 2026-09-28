using PlumePdf.Objects;

namespace PlumePdf.Raster;

/// <summary>
/// The seam for the Phase 9 read-only rasterization invariant — the linchpin
/// decision. Render-time synthesis of widget appearances (<c>/V</c>-no-<c>/AP</c> widgets and
/// <c>NeedAppearances</c> documents) runs Phase 4's <c>AppearanceGenerator</c>, which writes only
/// through an <see cref="ObjectRegistry"/> (<c>AllocateNumber</c>/<c>RegisterNew</c>) and never
/// reads pre-existing objects through it. Handing it a <b>scratch</b> registry — one that wraps the
/// real document as its read-through resolver but collects every write in its own private overlay,
/// discarded when the <c>Rasterize</c> call returns — means synthesis can allocate and cross-reference
/// freely while the opened document's object graph is <b>never mutated</b>. That single mechanism
/// satisfies both the requirement that rasterization never mutates the opened document and the
/// requirement that parallel rasterization of one open document is safe: N threads = N private scratch
/// registries, no shared mutable write path. The invariant is guarded mechanically by
/// <c>RasterReadOnlyInvariantTests</c>, which asserts the real registry's
/// next-number/dirty-set/free-list are byte-identical across a <c>Rasterize</c> call.
/// </summary>
internal static class ScratchObjectRegistry
{
    /// <summary>
    /// Creates a scratch <see cref="ObjectRegistry"/> that reads through <paramref name="document"/>
    /// but keeps all writes private (discarded with the returned instance). The real
    /// <paramref name="document"/> is never mutated.
    /// </summary>
    /// <param name="document">
    /// The opened document's object source (typically <c>doc.Objects</c>). Reads for objects the
    /// scratch registry has not itself allocated fall through to this source.
    /// </param>
    /// <param name="nextObjectNumber">
    /// The first object number the scratch registry hands out from <c>AllocateNumber</c>. <b>Must be
    /// seeded past the document's highest object number</b> — reads check the scratch registry's own
    /// new-objects overlay <i>before</i> falling through to <paramref name="document"/>, so a number
    /// that collides with a real object would shadow it during synthesis. Callers pass the document's
    /// own next-fresh-number here.
    /// </param>
    /// <param name="freeObjectNumbers">
    /// Optional free-list numbers the scratch registry may reuse before extending upward. Pass
    /// <see langword="null"/> (the default) to always extend from <paramref name="nextObjectNumber"/>,
    /// which keeps synthesis allocations off the real document's free list entirely.
    /// </param>
    internal static ObjectRegistry CreateFor(IObjectSource document, int nextObjectNumber, IReadOnlyCollection<int>? freeObjectNumbers = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new ObjectRegistry(document, nextObjectNumber, freeObjectNumbers);
    }
}
