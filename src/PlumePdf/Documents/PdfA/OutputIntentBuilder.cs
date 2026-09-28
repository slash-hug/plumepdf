using System.Text;
using PlumePdf.Objects;

namespace PlumePdf.Documents.PdfA;

/// <summary>
/// Builds and attaches a PDF/A output intent (ISO 32000-1 §14.11.5) — the <c>GTS_PDFA1</c>
/// <c>/OutputIntents</c> entry plus its embedded ICC destination profile that PDF/A-1B and
/// PDF/A-2B both require. PlumePDF ships one CC0-1.0
/// compact sRGB profile (<c>Assets/sRGB-v2-micro.icc</c>, sourced from
/// <c>github.com/saucecontrol/Compact-ICC-Profiles</c> — see <c>NOTICE</c>) as the
/// ready-to-use default; a caller may supply a different ICC profile instead.
/// </summary>
/// <remarks>
/// Internal (<c>Documents</c>-layer implementation types stay internals-only,
/// same shape as <see cref="Signing.DssWriter"/> and <see cref="Forms.Appearances.AppearanceGenerator"/>);
/// the public PDF/A create path (the <c>Pdf.*</c> verbs) is where a caller
/// actually reaches this.
/// </remarks>
internal static class OutputIntentBuilder
{
    private const string DefaultOutputConditionIdentifier = "sRGB IEC61966-2.1";

    private static readonly PdfName OutputIntents = PdfName.Get("OutputIntents");
    private static readonly PdfName OutputIntentSubtypeKey = PdfName.Get("S");
    private static readonly PdfName OutputIntentSubtype = PdfName.Get("GTS_PDFA1");
    private static readonly PdfName OutputConditionIdentifierName = PdfName.Get("OutputConditionIdentifier");
    private static readonly PdfName InfoName = PdfName.Get("Info");
    private static readonly PdfName DestOutputProfileName = PdfName.Get("DestOutputProfile");
    private static readonly PdfName OutputIntentTypeName = PdfName.Get("OutputIntent");

    // The bundled compact sRGB v2 ICC profile (456 bytes, CC0-1.0), compiled in as a literal
    // rather than read from an EmbeddedResource via Assembly.GetManifestResourceStream: that
    // API is System.Reflection's member-enumeration/invocation surface, which
    // PlumePdf.ArchitectureTests.ReflectionBanTests forbids anywhere in src/ (the repo's
    // NativeAOT-safety commitment, docs/architecture.md/docs/agent-forward.md "From Phase 2
    // on"). Same shape as Fonts/Standard14/Standard14Metrics.g.cs's checked-in generated
    // tables — a byte-for-byte copy of Assets/sRGB-v2-micro.icc (the file of record for
    // provenance/NOTICE and for regenerating this literal), never re-derived at build or run
    // time. Regenerate with: `python3 -c "d=open('Assets/sRGB-v2-micro.icc','rb').read();
    // print(', '.join(str(b) for b in d))"` if the bundled profile ever changes (this literal is
    // pinned to this exact file; changing it requires regenerating from that file and
    // re-verifying the checked-in bytes).
    private static readonly byte[] SrgbProfileBytes =
    [
        0, 0, 1, 200, 108, 99, 109, 115, 2, 16, 0, 0, 109, 110, 116, 114, 82, 71, 66, 32,
        88, 89, 90, 32, 7, 226, 0, 3, 0, 20, 0, 9, 0, 14, 0, 29, 97, 99, 115, 112,
        77, 83, 70, 84, 0, 0, 0, 0, 115, 97, 119, 115, 99, 116, 114, 108, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 246, 214, 0, 1, 0, 0, 0, 0, 211, 45,
        104, 97, 110, 100, 157, 145, 0, 61, 64, 128, 176, 61, 64, 116, 44, 129, 158, 165, 34, 142,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 9, 100, 101, 115, 99, 0, 0, 0, 240,
        0, 0, 0, 95, 99, 112, 114, 116, 0, 0, 1, 12, 0, 0, 0, 12, 119, 116, 112, 116,
        0, 0, 1, 24, 0, 0, 0, 20, 114, 88, 89, 90, 0, 0, 1, 44, 0, 0, 0, 20,
        103, 88, 89, 90, 0, 0, 1, 64, 0, 0, 0, 20, 98, 88, 89, 90, 0, 0, 1, 84,
        0, 0, 0, 20, 114, 84, 82, 67, 0, 0, 1, 104, 0, 0, 0, 96, 103, 84, 82, 67,
        0, 0, 1, 104, 0, 0, 0, 96, 98, 84, 82, 67, 0, 0, 1, 104, 0, 0, 0, 96,
        100, 101, 115, 99, 0, 0, 0, 0, 0, 0, 0, 5, 117, 82, 71, 66, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 116, 101, 120, 116, 0, 0, 0, 0, 67, 67, 48, 0,
        88, 89, 90, 32, 0, 0, 0, 0, 0, 0, 243, 84, 0, 1, 0, 0, 0, 1, 22, 201,
        88, 89, 90, 32, 0, 0, 0, 0, 0, 0, 111, 160, 0, 0, 56, 242, 0, 0, 3, 143,
        88, 89, 90, 32, 0, 0, 0, 0, 0, 0, 98, 150, 0, 0, 183, 137, 0, 0, 24, 218,
        88, 89, 90, 32, 0, 0, 0, 0, 0, 0, 36, 160, 0, 0, 15, 133, 0, 0, 182, 196,
        99, 117, 114, 118, 0, 0, 0, 0, 0, 0, 0, 42, 0, 0, 0, 124, 0, 248, 1, 156,
        2, 117, 3, 131, 4, 201, 6, 78, 8, 18, 10, 24, 12, 98, 14, 244, 17, 207, 20, 246,
        24, 106, 28, 46, 32, 67, 36, 172, 41, 106, 46, 126, 51, 235, 57, 179, 63, 214, 70, 87,
        77, 54, 84, 118, 92, 23, 100, 29, 108, 134, 117, 86, 126, 141, 136, 44, 146, 54, 156, 171,
        167, 140, 178, 219, 190, 153, 202, 199, 215, 101, 228, 119, 241, 249, 255, 255,
    ];

    /// <summary>
    /// The bundled compact sRGB v2 ICC profile bytes (456 bytes, CC0-1.0) — a fresh defensive
    /// copy on every call (the copy-on-materialize contract: never a view over the
    /// shared static backing array, so a caller mutating the returned array can never corrupt
    /// every subsequent caller's copy).
    /// </summary>
    public static byte[] SrgbProfile => (byte[])SrgbProfileBytes.Clone();

    /// <summary>
    /// Adds a <c>GTS_PDFA1</c> output intent using the bundled <see cref="SrgbProfile"/> to
    /// <paramref name="document"/>'s catalog.
    /// </summary>
    public static void AddSrgbOutputIntent(PdfDocument document) =>
        AddOutputIntent(document, SrgbProfile, DefaultOutputConditionIdentifier);

    /// <summary>
    /// Adds a <c>GTS_PDFA1</c> output intent to <paramref name="document"/>'s catalog using a
    /// caller-supplied ICC profile — registers the profile as a new indirect stream and the
    /// intent dictionary as a new indirect object (<see cref="ObjectRegistry.AllocateNumber"/>/
    /// <see cref="ObjectRegistry.RegisterNew"/>, the same non-replacement addition pattern
    /// <see cref="Signing.DssWriter"/> uses for <c>/DSS</c>), then appends it to
    /// <c>/OutputIntents</c> (creating the array if absent) and marks the catalog dirty.
    /// </summary>
    /// <param name="document">The document to attach the output intent to.</param>
    /// <param name="iccProfile">The raw (undecoded) ICC profile bytes.</param>
    /// <param name="outputConditionIdentifier">
    /// A non-empty string identifying the intended output condition (ISO 32000-1 §14.11.5);
    /// registry names like <c>"sRGB IEC61966-2.1"</c> are conventional.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="iccProfile"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="outputConditionIdentifier"/> is null/empty, or the document has no resolvable catalog.</exception>
    public static void AddOutputIntent(PdfDocument document, byte[] iccProfile, string outputConditionIdentifier)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(iccProfile);
        ArgumentException.ThrowIfNullOrEmpty(outputConditionIdentifier);

        var catalog = document.Catalog ?? throw new ArgumentException("The document has no resolvable /Root catalog; an output intent cannot be attached.", nameof(document));

        var profileDictionary = new PdfDictionary();
        profileDictionary.Set(PdfName.N, PdfNumber.Get(3));
        var profileReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(profileReference, new PdfStream(profileDictionary, iccProfile));

        var intent = new PdfDictionary();
        intent.Set(PdfName.Type, OutputIntentTypeName);
        // ISO 32000-1 §14.11.5 Table 365: an output intent dictionary's subtype key is /S —
        // NOT /Subtype, the key most other subtyped dictionaries use. Writing /Subtype here
        // produced an intent veraPDF (correctly) did not recognize as GTS_PDFA1 at all
        // (observed empirically from its CLI report: "DeviceRGB colour space is used without
        // RGB output intent profile" despite the intent being present).
        intent.Set(OutputIntentSubtypeKey, OutputIntentSubtype);
        intent.Set(OutputConditionIdentifierName, PdfString.FromLiteral(Encoding.ASCII.GetBytes(outputConditionIdentifier)));
        intent.Set(InfoName, PdfString.FromLiteral(Encoding.ASCII.GetBytes(outputConditionIdentifier)));
        intent.Set(DestOutputProfileName, new PdfReference(profileReference));
        var intentReference = document.Objects.AllocateNumber();
        document.Objects.RegisterNew(intentReference, intent);

        var intentsArray = ResolveExistingArray(document.Objects, catalog.Dictionary) ?? new PdfArray();
        intentsArray.Add(new PdfReference(intentReference));
        catalog.Dictionary.Set(OutputIntents, intentsArray);
        document.Objects.MarkDirty(catalog.Reference);
    }

    private static PdfArray? ResolveExistingArray(ObjectRegistry objects, PdfDictionary catalogDictionary)
    {
        if (!catalogDictionary.TryGetValue(OutputIntents, out var existing))
        {
            return null;
        }

        var resolved = existing is PdfReference reference ? objects[reference.Target] : existing;
        return resolved as PdfArray;
    }
}
