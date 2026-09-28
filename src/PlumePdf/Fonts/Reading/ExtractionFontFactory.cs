using System.Collections.Concurrent;
using PlumePdf.Fonts;
using PlumePdf.Objects;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// Builds <see cref="ExtractionFont"/>s from a document's resolved font dictionaries, caching
/// one instance per indirect font object for the lifetime of this factory. A factory lives for
/// exactly ONE extraction call (<c>TextExtractor.Extract</c> constructs a fresh one per call):
/// each <see cref="ExtractionFont"/> holds that call's result-scoped
/// <see cref="DiagnosticCollection"/>, so an instance cannot outlive the
/// extraction whose diagnostics it reports into. The cache therefore de-duplicates font
/// parsing *within* one page extraction (a page referencing one font from many runs parses it
/// once); cross-page/cross-call caching would require splitting parsed font data from
/// diagnostic reporting and is a recorded follow-up, not this type's contract.
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>-backed so any concurrent use never races
/// destructively — <see cref="GetOrBuild"/> may still *build* a font more than once under a
/// race, but every caller always observes one winning, fully constructed instance.
/// </summary>
internal sealed class ExtractionFontFactory
{
    private static readonly PdfName SubtypeName = PdfName.Subtype;
    private static readonly PdfName BaseFontName = PdfName.Get("BaseFont");
    private static readonly PdfName EncodingName = PdfName.Encoding;
    private static readonly PdfName ToUnicodeName = PdfName.ToUnicode;
    private static readonly PdfName FirstCharName = PdfName.FirstChar;
    private static readonly PdfName WidthsName = PdfName.Widths;
    private static readonly PdfName FontDescriptorName = PdfName.Get("FontDescriptor");
    private static readonly PdfName MissingWidthName = PdfName.Get("MissingWidth");
    private static readonly PdfName FontFileName = PdfName.Get("FontFile");
    private static readonly PdfName FontFile2Name = PdfName.Get("FontFile2");
    private static readonly PdfName FontFile3Name = PdfName.Get("FontFile3");
    private static readonly PdfName DescendantFontsName = PdfName.DescendantFonts;
    private static readonly PdfName WName = PdfName.Get("W");
    private static readonly PdfName DwName = PdfName.DW;

    // A hostile /W CID range entry ("cFirst cLast w") could declare a span of billions of
    // CIDs; capped independently of any PdfOptions knob (none exists yet for this) so a single
    // crafted descendant font dictionary can't force an unbounded per-CID dictionary expansion.
    private const int MaxCidWidthRangeSpan = 200_000;

    private readonly ConcurrentDictionary<IndirectReference, ExtractionFont> _cache = new();
    private readonly IObjectSource _objects;
    private readonly PdfFilterRegistry _filters;
    private readonly PdfOptions _options;
    private readonly DiagnosticCollection? _diagnostics;

    /// <summary>Creates a factory over one document's object graph.</summary>
    /// <param name="objects">The document's object resolver, for dereferencing indirect font-dictionary entries.</param>
    /// <param name="filters">The filter registry used to decode <c>/ToUnicode</c> and embedded <c>/Encoding</c> CMap streams.</param>
    /// <param name="options">Active options.</param>
    /// <param name="diagnostics">Where recoverable deviations are recorded, if any.</param>
    public ExtractionFontFactory(IObjectSource objects, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(options);
        _objects = objects;
        _filters = filters;
        _options = options;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Returns the cached <see cref="ExtractionFont"/> for <paramref name="reference"/>,
    /// building it from <paramref name="fontDict"/> on first access.
    /// </summary>
    public ExtractionFont GetOrBuild(IndirectReference reference, PdfDictionary fontDict) =>
        _cache.GetOrAdd(reference, _ => Build(fontDict));

    /// <summary>Builds an <see cref="ExtractionFont"/> directly from a font dictionary that has no indirect identity to cache under (an inline, non-indirect <c>/Font</c> entry — legal but rare).</summary>
    public ExtractionFont Build(PdfDictionary fontDict)
    {
        ArgumentNullException.ThrowIfNull(fontDict);

        var subtype = Resolve(fontDict, SubtypeName) is PdfName subtypeName ? subtypeName.Value : string.Empty;
        var baseFontName = Resolve(fontDict, BaseFontName) is PdfName baseFontValue ? baseFontValue.Value : "Unknown";

        return subtype == "Type0"
            ? BuildType0(fontDict, baseFontName)
            : BuildSimple(fontDict, subtype, baseFontName);
    }

    private SimpleExtractionFont BuildSimple(PdfDictionary fontDict, string subtype, string baseFontName)
    {
        var descriptor = Resolve(fontDict, FontDescriptorName) as PdfDictionary;

        // The Standard-14 Symbol/ZapfDingbats sticky built-in-encoding rule is
        // authoritative only when the font descriptor carries no /FontFile, /FontFile2, or
        // /FontFile3 at all — the same presence check RenderFontFactory.BuildNonEmbedded's
        // callers use to decide whether the sticky rule applies there (RenderFontFactory.cs'
        // hadEmbeddedProgram flag), so the two sides agree for every font, including one whose
        // embedded program exists but fails to parse. An embedded Type 1 font merely *named*
        // Symbol/ZapfDingbats keeps its own declared /Encoding non-authoritatively instead of
        // extraction silently overriding it while render honours it.
        var isEmbedded = descriptor is not null
            && (descriptor.TryGetValue(FontFileName, out _)
                || descriptor.TryGetValue(FontFile2Name, out _)
                || descriptor.TryGetValue(FontFile3Name, out _));

        var stripped = FontNames.StripSubsetPrefix(baseFontName);
        var isSymbolFont = Standard14SymbolFonts.TryGet(stripped, subtype, out var symbolBase, out _);
        var authoritative = isSymbolFont && !isEmbedded;
        // The Standard-14 table is always the built-in base for a symbol font; only its authority
        // toggles on embedding — an embedded-but-unparseable Symbol with
        // no declared /Encoding falls back to Symbol's own table, not the opaque default. Same pair
        // as RenderFontFactory.BuildNonEmbedded, so the two sides agree for every font.
        var encoding = EncodingResolver.Resolve(fontDict, _objects, _options, _diagnostics, builtInBase: isSymbolFont ? symbolBase : null, builtInIsAuthoritative: authoritative);

        var firstChar = 0;
        if (Resolve(fontDict, FirstCharName) is PdfNumber firstCharNumber && firstCharNumber.TryToInt32(out var fc))
        {
            firstChar = fc;
        }

        var widths = new Dictionary<int, double>();
        if (Resolve(fontDict, WidthsName) is PdfArray widthsArray)
        {
            for (var i = 0; i < widthsArray.Count; i++)
            {
                if (Resolve(widthsArray[i]) is PdfNumber width)
                {
                    widths[firstChar + i] = width.Value;
                }
            }
        }

        var missingWidth = 0.0;
        if (descriptor is not null && Resolve(descriptor, MissingWidthName) is PdfNumber missingWidthNumber)
        {
            missingWidth = missingWidthNumber.Value;
        }

        var toUnicode = ResolveToUnicodeCMap(fontDict, baseFontName);

        return new SimpleExtractionFont(baseFontName, encoding, widths, missingWidth, toUnicode, _options, _diagnostics);
    }

    private Type0ExtractionFont BuildType0(PdfDictionary fontDict, string baseFontName)
    {
        var (isIdentity, encodingCMap) = ResolveCidEncoding(fontDict, baseFontName);

        var descendant = ResolveDescendantFont(fontDict);
        var defaultWidth = 1000.0;
        var widths = new Dictionary<int, double>();

        if (descendant is not null)
        {
            if (Resolve(descendant, DwName) is PdfNumber dwNumber)
            {
                defaultWidth = dwNumber.Value;
            }

            if (Resolve(descendant, WName) is PdfArray wArray)
            {
                ParseCidWidths(wArray, widths);
            }
        }

        var toUnicode = ResolveToUnicodeCMap(fontDict, baseFontName);

        return new Type0ExtractionFont(baseFontName, isIdentity, encodingCMap, widths, defaultWidth, toUnicode, _options, _diagnostics);
    }

    private PdfDictionary? ResolveDescendantFont(PdfDictionary fontDict)
    {
        if (Resolve(fontDict, DescendantFontsName) is not PdfArray descendants || descendants.Count == 0)
        {
            return null;
        }

        return Resolve(descendants[0]) as PdfDictionary;
    }

    private (bool IsIdentity, CMap? EncodingCMap) ResolveCidEncoding(PdfDictionary fontDict, string baseFontName)
    {
        if (!fontDict.TryGetValue(EncodingName, out var raw))
        {
            ReportDeviation("PLUME8022", $"Type0 font '{baseFontName}' has no /Encoding; assuming Identity-H.");
            return (true, null);
        }

        switch (Resolve(raw))
        {
            case PdfName { Value: "Identity-H" or "Identity-V" }:
                return (true, null);

            case PdfName otherName:
                // A predefined CMap other than Identity-H/V needs Adobe's CMap resource
                // files to resolve, out of scope this phase: report and fall back to
                // Identity-H so the rest of the font (widths, ToUnicode) is still usable.
                ReportDeviation("PLUME8021", $"Type0 font '{baseFontName}' uses predefined CMap '{otherName.Value}', which PlumePDF does not resolve in this phase; falling back to Identity-H code mapping.");
                return (true, null);

            case PdfStream cmapStream:
                try
                {
                    var decoded = cmapStream.GetDecodedBytes(_filters, _options, r => _objects.Resolve(r));
                    return (false, CMapParser.Parse(decoded, _options, _diagnostics, _options.MaxCMapEntries));
                }
                catch (PlumePdfException ex)
                {
                    ReportDeviation("PLUME8022", $"Type0 font '{baseFontName}' embedded /Encoding CMap stream could not be decoded ({ex.Message}); falling back to Identity-H code mapping.");
                    return (true, null);
                }

            default:
                ReportDeviation("PLUME8022", $"Type0 font '{baseFontName}' has an /Encoding entry of an unexpected type; falling back to Identity-H code mapping.");
                return (true, null);
        }
    }

    private CMap? ResolveToUnicodeCMap(PdfDictionary fontDict, string baseFontName)
    {
        if (Resolve(fontDict, ToUnicodeName) is not PdfStream stream)
        {
            return null;
        }

        try
        {
            var decoded = stream.GetDecodedBytes(_filters, _options, r => _objects.Resolve(r));
            return CMapParser.Parse(decoded, _options, _diagnostics, _options.MaxCMapEntries);
        }
        catch (PlumePdfException ex)
        {
            ReportDeviation("PLUME8022", $"Font '{baseFontName}' /ToUnicode stream could not be decoded ({ex.Message}); continuing without a ToUnicode overlay.");
            return null;
        }
    }

    private void ParseCidWidths(PdfArray array, Dictionary<int, double> widths)
    {
        var i = 0;
        while (i < array.Count)
        {
            if (Resolve(array[i]) is not PdfNumber firstNumber || !firstNumber.TryToInt32(out var first))
            {
                i++;
                continue;
            }

            i++;
            if (i >= array.Count)
            {
                break;
            }

            if (Resolve(array[i]) is PdfArray individualWidths)
            {
                for (var j = 0; j < individualWidths.Count; j++)
                {
                    if (Resolve(individualWidths[j]) is PdfNumber w)
                    {
                        widths[first + j] = w.Value;
                    }
                }

                i++;
                continue;
            }

            if (Resolve(array[i]) is PdfNumber lastNumber && lastNumber.TryToInt32(out var last))
            {
                i++;
                if (i >= array.Count)
                {
                    break;
                }

                if (Resolve(array[i]) is PdfNumber rangeWidth)
                {
                    var span = (long)last - first;
                    if (span < 0 || span > MaxCidWidthRangeSpan)
                    {
                        ReportDeviation("PLUME8022", $"Descendant font /W range [{first}, {last}] exceeds the configured span limit ({MaxCidWidthRangeSpan}); range skipped.");
                    }
                    else
                    {
                        for (var cid = first; cid <= last; cid++)
                        {
                            widths[cid] = rangeWidth.Value;
                        }
                    }
                }

                i++;
                continue;
            }

            i++;
        }
    }

    private PdfObject? Resolve(PdfDictionary dictionary, PdfName key) =>
        dictionary.TryGetValue(key, out var value) ? Resolve(value) : null;

    private PdfObject Resolve(PdfObject value) =>
        value is PdfReference reference ? _objects.Resolve(reference.Target) : value;

    private void ReportDeviation(string code, string message)
    {
        if (_options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        _diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}
