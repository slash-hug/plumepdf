using NetArchTest.Rules;
using Xunit;

namespace PlumePdf.ArchitectureTests;

/// <summary>
/// Enforces docs/architecture.md: one runtime assembly, nine layers (including verbs, which
/// has no internal namespace of its own), a lower layer never references a higher one. As
/// namespaces come into existence phase by phase, these rules bite automatically — they pass
/// vacuously for namespaces that don't exist yet.
///
/// Two independent rules (docs/architecture.md's "Namespace
/// policy"): a namespace-to-namespace rule below for the eight internal layer namespaces,
/// and an enumerated-façade rule for the public hub types that live in the root
/// `PlumePdf` namespace (a bare "PlumePdf" prefix rule would match every public type in
/// the codebase, including shared contracts like PdfOptions that every layer legitimately
/// depends on — confirmed unusable, hence naming the specific façade types instead).
///
/// Fonts inserted between Objects and Content in Phase 2: every FacadeTypes index below
/// Fonts is unchanged, every index at or above Fonts shifted up by one. Raster inserted
/// between Content and Documents in Phase 8: every FacadeTypes index that was ≥ 5
/// (Documents) before this insertion shifted up by one again.
/// </summary>
public class LayeringTests
{
    // Lowest to highest, per docs/architecture.md "Layers" (Filters below Objects;
    // Fonts between Objects and Content; Raster between Content and Documents).
    private static readonly string[] Layers =
    [
        "PlumePdf.IO",
        "PlumePdf.Filters",
        "PlumePdf.Objects",
        "PlumePdf.Fonts",
        "PlumePdf.Content",
        "PlumePdf.Raster",
        "PlumePdf.Documents",
        "PlumePdf.Elements",
        "PlumePdf.Layout",
        "PlumePdf.Compose",
    ];

    // Public root-namespace hub types that are the face of one specific higher
    // layer, tagged with that layer's index into Layers (Layout=8; verbs, layer 10, has no
    // internal namespace of its own since it's thin wrappers directly in the root
    // namespace). Types not yet implemented are listed anyway —
    // NetArchTest matches by name against IL metadata, so an absent type simply never
    // appears as a dependency and the rule passes vacuously until it lands, exactly like
    // the namespace rule above.
    private static readonly (string TypeName, int LayerIndex)[] FacadeTypes =
    [
        ("PlumePdf.PdfFont", 3),
        ("PlumePdf.PdfDocument", 6),
        ("PlumePdf.PdfPage", 6),
        ("PlumePdf.PageCollection", 6),
        // Phase 4 (AcroForms): named here ahead of
        // them actually shipping (deliberately pre-arrival), per this file's own header
        // comment on how the facade rule tolerates a not-yet-existing type - NetArchTest
        // matches by name against IL metadata, so these three simply never appear as a
        // dependency and the rule below passes vacuously until they land.
        ("PlumePdf.PdfForm", 6),
        ("PlumePdf.FormField", 6),
        ("PlumePdf.FormFieldCollection", 6),
        ("PlumePdf.Manuscript", 8),
        ("PlumePdf.Pdf", 10),
        ("PlumePdf.SplitResult", 10),
        // Phase 5 (Digital signatures): named
        // here ahead of them actually shipping, same pre-arrival pattern as the Phase 4
        // trio above. Deliberately excludes IPdfSigner/ITimestampAuthority/IRevocationFetcher
        // (the public signing seams) - their own concrete implementations live BELOW the
        // Documents layer (CertificateSigner in Objects.Signing, HttpTimestampAuthority/
        // HttpRevocationFetcher in IO.Http), so pinning them at a higher layer index here
        // would forbid the very layers that implement them from depending on them. They get
        // the same "shared contract every layer legitimately depends on" treatment this
        // file's own header comment already carves out for PdfOptions.
        ("PlumePdf.PdfSignature", 6),
        ("PlumePdf.SignatureCollection", 6),
        ("PlumePdf.PdfSignOptions", 6),
        ("PlumePdf.SignatureVerificationResult", 6),
        // Phase 6 (Compliance & polish): PdfAConformance ships early
        // (a plain enum with no dependencies of its own — listing it is harmless either way);
        // PdfAValidationResult/PdfRedactOptions/RedactionResult are named here ahead of them
        // actually shipping, the same pre-arrival pattern as the Phase 4/5 groups above.
        ("PlumePdf.PdfAConformance", 6),
        ("PlumePdf.PdfAValidationResult", 6),
        ("PlumePdf.PdfRedactOptions", 6),
        ("PlumePdf.RedactionResult", 6),
        // Phase 7 (Raster codecs + image->PDF): RasterImage/RasterImageFrame
        // are the PDF-free imaging facade — folder src/PlumePdf/Filters/, root namespace,
        // pinned to layer index 1 (Filters, alongside PdfFilterRegistry) since they decode
        // from raw bytes independent of any PdfDocument and the Filters-layer codecs that
        // produce them cannot depend on anything higher. Named here ahead of them actually
        // shipping, the same pre-arrival pattern as every group above — NetArchTest
        // matches by name against IL metadata, so these simply never appear as a dependency
        // and the rule passes vacuously until they land.
        ("PlumePdf.RasterImage", 1),
        ("PlumePdf.RasterImageFrame", 1),
        // Phase 8 (Rasterizer core): pinned to layer index 6 (Documents, alongside PdfDocument/
        // PdfPage) since it is the options record the Rasterize verbs accept, not a
        // Raster-layer internal type.
        ("PlumePdf.PdfRasterizeOptions", 6),
        // ImageResamplingMode is Raster-layer vocabulary surfaced
        // publicly on the Documents-layer options record — the same shape as RasterImage
        // (index 1, a Filters-layer type in the root namespace), NOT PdfAConformance (index 6).
        // Pinned at the Raster index so Raster.RasterPaintContext may carry it; pinning it at 6
        // would forbid the very layer that consumes it.
        ("PlumePdf.ImageResamplingMode", 5),
    ];

    public static TheoryData<string, string> ForbiddenDependencies()
    {
        var data = new TheoryData<string, string>();
        for (var lower = 0; lower < Layers.Length; lower++)
        {
            for (var higher = lower + 1; higher < Layers.Length; higher++)
            {
                data.Add(Layers[lower], Layers[higher]);
            }
        }

        return data;
    }

    public static TheoryData<string, string> ForbiddenFacadeDependencies()
    {
        var data = new TheoryData<string, string>();
        for (var layerIndex = 0; layerIndex < Layers.Length; layerIndex++)
        {
            foreach (var (typeName, facadeLayerIndex) in FacadeTypes)
            {
                if (facadeLayerIndex > layerIndex)
                {
                    data.Add(Layers[layerIndex], typeName);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ForbiddenDependencies))]
    public void LowerLayerNeverReferencesHigherLayer(string lowerNamespace, string higherNamespace)
    {
        var result = Types.InAssembly(typeof(PlumePdfException).Assembly)
            .That().ResideInNamespace(lowerNamespace)
            .ShouldNot().HaveDependencyOn(higherNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, FailureMessage(result, lowerNamespace, higherNamespace));
    }

    [Theory]
    [MemberData(nameof(ForbiddenFacadeDependencies))]
    public void LowerLayerNeverReferencesHigherLayerFacade(string lowerNamespace, string facadeTypeName)
    {
        var result = Types.InAssembly(typeof(PlumePdfException).Assembly)
            .That().ResideInNamespace(lowerNamespace)
            .ShouldNot().HaveDependencyOn(facadeTypeName)
            .GetResult();

        Assert.True(result.IsSuccessful, FailureMessage(result, lowerNamespace, facadeTypeName));
    }

    private static string FailureMessage(TestResult result, string lower, string higher) =>
        result.IsSuccessful
            ? string.Empty
            : $"Layer violation: {lower} must not depend on {higher}. Offenders: " +
              string.Join(", ", result.FailingTypes?.Select(t => t.FullName ?? t.Name) ?? []);
}
