using Mono.Cecil;
using Xunit;

namespace PlumePdf.ArchitectureTests;

// =============================================================================
// SCOPING: Phase 9 mandates an architecture test banning mutable statics
// in the eventual Raster layer, on the observation that a raster codec's natural habitat —
// shared lookup tables (CCITT white/black Huffman code tables, JPEG zigzag order, IJG
// quantization/Huffman constants, JBIG2 context templates) — is exactly where a
// "dequantize this row IN PLACE into the shared table" bug becomes a cross-request data
// race the moment two threads decode concurrently (PlumePdf.PdfFilterRegistry.Default is a
// documented mutable process-wide singleton other threads may be decoding through at any
// time; docs/architecture.md "Threading & mutation"). That Phase 9
// mandate is pulled forward to Phase 7's own PlumePdf.Filters namespace ("adopting at birth is cheap;
// retrofitting is expensive") rather than waiting for the Raster layer to accumulate the
// same class of bug first. This test enforces the mechanical half of that ruling: every
// static field a type in namespace PlumePdf.Filters declares must be `static readonly`
// (IL: InitOnly) — reassignable after construction is exactly the shape a codec's shared
// constant table must never have. It says nothing about a `static readonly` array's
// ELEMENTS being mutated after construction (Mono.Cecil's InitOnly flag is field-level, not
// a deep-immutability proof) — codec authors still owe every lookup table the same
// "dequantize into a fresh per-call buffer, never into the shared table" discipline this
// rule's own name calls out; the field-level check is what a static architecture gate can
// mechanically prove, not a substitute for that per-codec review discipline.
//
// Scoped to namespace PlumePdf.Filters (the actual, NetArchTest-enforced Filters layer per
// LayeringTests.cs — not the root-namespace `PlumePdf` facade types like
// PdfFilterRegistry/PdfOptions that also happen to live under the Filters/ folder; those are
// shared contracts every layer legitimately depends on, not codec internals) AND, to
// namespace PlumePdf.Raster: Phase 8 is the phase that *creates* the Raster namespace and
// immediately fills it with exactly the shared-lookup-table hazard class this gate exists to
// catch — AGG23 scanline/coverage cells, glyph outline caches, the CMYK 9^4 LUT, shading
// sample tables — so the ban is adopted at birth rather than deferred to a later phase, per
// the same "adopting at birth is cheap; retrofitting is expensive" rationale.
//
// Phase 9 confirmation: the Phase 9 rasterizer-completeness work re-examined this gate's
// scope on the concern that new Phase 9 Raster surface (Raster/Annotations/*, Raster/OptionalContent/*,
// Raster/Shading/MeshShading.cs, Raster/Color/CieConversions.cs, Raster/Transparency/Backdrop.cs
// — new lookup-table-shaped machinery: OCG visibility state, mesh vertex/interpolation tables,
// CIE conversion constants) might need its own new namespace prefix here. It does not: every
// one of those files lands under the PlumePdf.Raster namespace/folder tree the prefix below
// already matches (the "or a sub-namespace" IsInTargetNamespace check, e.g.
// PlumePdf.Raster.Annotations/OptionalContent/Shading/Color/Transparency all match the
// "PlumePdf.Raster" prefix). No behavior change was needed; this note exists so a future
// reader doesn't re-derive the same "is Phase 9 in scope" question from scratch — it was asked
// and confirmed already, and the concurrency-proof pairing (the per-call scratch-registry
// mechanism + this gate's shared-mutable-lookup-table ban) is exactly the two-sided guarantee
// tests/PlumePdf.CorpusTests/ConcurrencyStressTests.cs proves observably from the public API.
// =============================================================================

/// <summary>
/// Mechanical enforcement (Phase 7 and Phase 8): every static field declared by a type in namespace <c>PlumePdf.Filters</c> or
/// <c>PlumePdf.Raster</c> must be <see langword="readonly"/> (IL <c>InitOnly</c>) — a
/// reassignable static is the shape a codec's or rasterizer's shared lookup table
/// (Huffman/zigzag/quantization/context constants; AGG23 coverage cells; the CMYK LUT) must
/// never have, since <see cref="PdfFilterRegistry.Default"/> is a mutable, concurrently-read
/// process-wide singleton and any codec or rasterizer it dispatches into may run on multiple
/// threads at once.
/// </summary>
public class MutableStaticBanTests
{
    private static readonly string[] TargetNamespacePrefixes = ["PlumePdf.Filters", "PlumePdf.Raster"];

    [Fact]
    public void EveryStaticFieldInTargetNamespacesIsReadOnly()
    {
        var assemblyPath = typeof(PlumePdfException).Assembly.Location;
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        var offenders = new List<string>();

        foreach (var type in module.Types)
        {
            CheckType(type, type.Namespace, offenders);
        }

        Assert.True(offenders.Count == 0,
            "Every static field on a type in namespace " + string.Join(" or ", TargetNamespacePrefixes) +
            " (or a sub-namespace, e.g. PlumePdf.Filters.Png/Tiff/Jbig2 or PlumePdf.Raster.Agg) " +
            "must be readonly (codec and rasterizer shared lookup " +
            "tables are exactly where an in-place-mutation bug becomes a cross-thread data race " +
            "through PdfFilterRegistry.Default's concurrent-reads contract). Make the field " +
            "`static readonly` and construct its value once, or — if it truly needs per-call " +
            "mutation — make it an instance/local value instead of a static one. Offenders:\n" +
            string.Join("\n", offenders));
    }

    /// <summary>
    /// Whether <paramref name="ns"/> is one of <see cref="TargetNamespacePrefixes"/> itself or
    /// one of its sub-namespaces (e.g. <c>PlumePdf.Filters.Png</c>, <c>PlumePdf.Raster.Agg</c>)
    /// — matching NetArchTest's own prefix-match convention (<c>LayeringTests.cs</c>) rather
    /// than exact string equality, which silently exempted every codec's own sub-namespace
    /// (Png/Tiff/Jbig2) from this gate.
    /// </summary>
    private static bool IsInTargetNamespace(string ns) =>
        TargetNamespacePrefixes.Any(prefix =>
            ns == prefix || ns.StartsWith(prefix + ".", StringComparison.Ordinal));

    private static bool IsCompilerGenerated(TypeDefinition type) =>
        type.CustomAttributes.Any(static a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");

    private static void CheckType(TypeDefinition type, string enclosingNamespace, List<string> offenders)
    {
        // A compiler-generated closure/display class (e.g. `<>c` caching a static lambda's
        // delegate instance in a lazily-assigned, non-InitOnly field) is never a hand-written
        // mutable lookup table - the whole point of this gate. Re-assigning the same cache
        // field from two racing threads is an idempotent, functionally-identical write (at
        // worst a redundant allocation), not the "dequantize a shared table in place" data
        // race this gate exists to catch - same reasoning as the auto-property-backing-field
        // and enum value__ exclusions below, just for a different kind of compiler machinery.
        if (IsCompilerGenerated(type))
        {
            return;
        }

        // Mono.Cecil reports a nested type's own Namespace as "" (C# nested types don't carry
        // an independent namespace at the IL level - they inherit the declaring type's), so
        // the effective namespace to check against has to be threaded down from the outermost
        // enclosing type rather than re-read from each nested TypeDefinition.
        var effectiveNamespace = string.IsNullOrEmpty(type.Namespace) ? enclosingNamespace : type.Namespace;

        if (IsInTargetNamespace(effectiveNamespace))
        {
            foreach (var field in type.Fields)
            {
                // Compiler-generated backing fields for auto-properties, and the runtime's own
                // enum `value__` field, are never hand-written mutable statics; nothing about
                // this gate is about property syntax. A property getter/setter pair that
                // itself introduces a mutable static would still be caught here, since the
                // compiler-generated backing field is exactly what fails the InitOnly check.
                if (field.IsStatic && !field.IsLiteral && !field.IsInitOnly)
                {
                    offenders.Add($"  {type.FullName}.{field.Name} ({field.FieldType.FullName})");
                }
            }
        }

        foreach (var nested in type.NestedTypes)
        {
            CheckType(nested, effectiveNamespace, offenders);
        }
    }
}
