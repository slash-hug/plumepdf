namespace PlumePdf.Content;

/// <summary>
/// Builds a page's <c>/Resources</c> dictionary (ISO 32000-1 §7.8.3), allocating a
/// deterministic resource name for each distinct font/XObject reference added: <c>/F1</c>,
/// <c>/F2</c>, ... for fonts (in the order they're first added) and <c>/X1</c>, <c>/X2</c>,
/// ... for XObjects (images and form XObjects share one namespace, per §7.8.3's
/// <c>XObject</c> subdictionary covering both). Adding the same <see cref="IndirectReference"/>
/// twice returns the name already assigned to it rather than allocating a duplicate — a
/// resource referenced by several operators in the same content stream is named once. The
/// same-input-same-output property this gives (name allocation depends only on add order,
/// never on hashing or iteration order of anything unordered) is what makes an assembled
/// page's bytes reproducible under <see cref="PdfOptions.Deterministic"/>.
/// </summary>
/// <example>
/// <code>
/// var resources = new ResourceDictionaryBuilder();
/// PdfName fontName = resources.AddFont(helveticaReference); // "/F1"
/// PdfDictionary dict = resources.Build();
/// </code>
/// </example>
internal sealed class ResourceDictionaryBuilder
{
    private static readonly PdfName ResourcesFont = PdfName.Get("Font");
    private static readonly PdfName ResourcesXObject = PdfName.Get("XObject");
    private static readonly PdfName ResourcesExtGState = PdfName.Get("ExtGState");

    private readonly Dictionary<IndirectReference, PdfName> _fonts = [];
    private readonly Dictionary<IndirectReference, PdfName> _xObjects = [];
    private readonly Dictionary<IndirectReference, PdfName> _extGStates = [];

    /// <summary>
    /// Adds a <c>/Font</c> resource, returning the name allocated to it (a fresh <c>/F{n}</c>
    /// the first time <paramref name="fontReference"/> is added, the same name on any
    /// subsequent add of the same reference).
    /// </summary>
    public PdfName AddFont(IndirectReference fontReference) => AddOrGet(_fonts, fontReference, "F");

    /// <summary>
    /// Adds an <c>/XObject</c> resource (image or form), returning the name allocated to it
    /// (a fresh <c>/X{n}</c> the first time <paramref name="xObjectReference"/> is added, the
    /// same name on any subsequent add of the same reference).
    /// </summary>
    public PdfName AddXObject(IndirectReference xObjectReference) => AddOrGet(_xObjects, xObjectReference, "X");

    /// <summary>
    /// Adds an <c>/ExtGState</c> resource (e.g. an alpha-constant dictionary for watermark/stamp
    /// opacity), returning the name allocated to it (a fresh <c>/GS{n}</c> the first time
    /// <paramref name="extGStateReference"/> is added, the same name on any subsequent add of
    /// the same reference).
    /// </summary>
    public PdfName AddExtGState(IndirectReference extGStateReference) => AddOrGet(_extGStates, extGStateReference, "GS");

    /// <summary>
    /// Builds the <c>/Resources</c> dictionary out of every font/XObject/ExtGState added so
    /// far. A category with no entries is omitted entirely rather than written as an empty
    /// subdictionary.
    /// </summary>
    public PdfDictionary Build()
    {
        var resources = new PdfDictionary();

        if (_fonts.Count > 0)
        {
            resources.Set(ResourcesFont, ToSubdictionary(_fonts));
        }

        if (_xObjects.Count > 0)
        {
            resources.Set(ResourcesXObject, ToSubdictionary(_xObjects));
        }

        if (_extGStates.Count > 0)
        {
            resources.Set(ResourcesExtGState, ToSubdictionary(_extGStates));
        }

        return resources;
    }

    private static PdfName AddOrGet(Dictionary<IndirectReference, PdfName> map, IndirectReference reference, string prefix)
    {
        if (map.TryGetValue(reference, out var existing))
        {
            return existing;
        }

        var name = PdfName.Get($"{prefix}{map.Count + 1}");
        map[reference] = name;
        return name;
    }

    private static PdfDictionary ToSubdictionary(Dictionary<IndirectReference, PdfName> map)
    {
        var dictionary = new PdfDictionary();
        foreach (var (reference, name) in map)
        {
            dictionary.Set(name, new PdfReference(reference));
        }

        return dictionary;
    }
}
