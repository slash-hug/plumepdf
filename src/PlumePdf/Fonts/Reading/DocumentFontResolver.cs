using PlumePdf.Objects;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// Resolves a <c>/DA</c> font resource name against an AcroForm's <c>/DR</c> resource
/// dictionary to the pair appearance generation needs: the decode-direction
/// <see cref="ExtractionFont"/> (reused as the metrics engine — encode the text, then walk
/// <see cref="ExtractionFont.DecodeNext"/> to sum advance widths) and the encode-direction
/// <see cref="FontEncoder"/>. Phase 4 scope: simple fonts only — a Type0
/// <c>/DR</c> font returns <see langword="null"/> and the caller degrades to the
/// <c>/NeedAppearances</c> escape hatch with a coded diagnostic, because Unicode→CID
/// encoding needs the reverse-CMap machinery scheduled with complex-script work.
/// </summary>
internal sealed class DocumentFontResolver
{
    private static readonly PdfName FontName = PdfName.Get("Font");
    private static readonly PdfName SubtypeName = PdfName.Subtype;
    private static readonly PdfName Type0Name = PdfName.Get("Type0");

    private readonly IObjectSource _objects;
    private readonly ExtractionFontFactory _fonts;
    private readonly PdfOptions _options;
    private readonly DiagnosticCollection? _diagnostics;
    private readonly Dictionary<string, ResolvedFormFont?> _cache = [];

    public DocumentFontResolver(IObjectSource objects, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        _objects = objects;
        _options = options;
        _diagnostics = diagnostics;
        _fonts = new ExtractionFontFactory(objects, options.Filters, options, diagnostics);
    }

    /// <summary>
    /// Resolves <paramref name="resourceName"/> (the <c>/DA</c> font name, without the slash)
    /// in <paramref name="resources"/>' <c>/Font</c> dictionary. Returns <see langword="null"/>
    /// when the name is absent, the dictionary is malformed, or the font is out of encode
    /// scope (Type0) — the caller decides how loudly to degrade.
    /// </summary>
    public ResolvedFormFont? Resolve(string resourceName, PdfDictionary resources)
    {
        if (_cache.TryGetValue(resourceName, out var cached))
        {
            return cached;
        }

        var resolved = ResolveUncached(resourceName, resources);
        _cache[resourceName] = resolved;
        return resolved;
    }

    private ResolvedFormFont? ResolveUncached(string resourceName, PdfDictionary resources)
    {
        if (ResolveValue(resources.TryGetValue(FontName, out var fonts) ? fonts : null) is not PdfDictionary fontDir)
        {
            return null;
        }

        var rawEntry = fontDir.TryGetValue(PdfName.Get(resourceName), out var entry) ? entry : null;
        if (ResolveValue(rawEntry) is not PdfDictionary fontDict)
        {
            return null;
        }

        if (fontDict.TryGetValue(SubtypeName, out var subtype) && subtype is PdfName st && ReferenceEquals(st, Type0Name))
        {
            return null; // encode direction for composite fonts is out of Phase 4 scope
        }

        var metrics = rawEntry is PdfReference reference
            ? _fonts.GetOrBuild(reference.Target, fontDict)
            : _fonts.Build(fontDict);
        var encoder = FontEncoder.Create(fontDict, _objects, _options, _diagnostics);
        if (encoder is null)
        {
            return null;
        }

        // The resource value the appearance stream's own /Resources /Font entry should carry:
        // the /DR entry's indirect reference when it has one (shares the object), else the
        // inline dictionary itself.
        return new ResolvedFormFont(metrics, encoder, rawEntry!);
    }

    private PdfObject? ResolveValue(PdfObject? value) =>
        value is PdfReference reference ? _objects.Resolve(reference.Target) : value;
}

/// <summary>A <c>/DR</c> font resolved for appearance generation: metrics (decode direction), encoder (encode direction), and the resource value to reference it by.</summary>
internal sealed record ResolvedFormFont(ExtractionFont Metrics, FontEncoder Encoder, PdfObject ResourceValue);
