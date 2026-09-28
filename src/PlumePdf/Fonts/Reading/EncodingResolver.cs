using PlumePdf.Objects;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// Resolves a simple font's effective code→glyph-name→Unicode table from its font dictionary
/// (ISO 32000-1 §9.6.6): the <c>/Encoding</c> entry (a base-encoding name, or a dictionary with
/// <c>/BaseEncoding</c> plus a <c>/Differences</c> override array), falling back to the font's
/// "built-in encoding": a caller-supplied <c>builtInBase</c> when the program's own encoding is
/// known (an embedded Type 1 program's cleartext <c>/Encoding</c>, a name-keyed bare-CFF program's
/// <c>Encoding</c> table, or the Standard-14 <c>Symbol</c>/<c>ZapfDingbats</c> table),
/// otherwise StandardEncoding for a nonsymbolic font with no <c>/Encoding</c> entry and
/// an opaque (all-unassigned) table for a symbolic one. Only an embedded symbolic <em>TrueType</em>
/// font's built-in encoding remains unresolved here (it would require its own <c>cmap</c>; the
/// render path handles that case through the (3,0) cmap conventions instead).
/// Never returns <see langword="null"/>: the result is always a 256-entry table, with an
/// unresolved code left as <c>("", -1)</c> — the same convention <see cref="SimpleFontEncodings"/>
/// itself uses for an unassigned code in a base table.
/// </summary>
internal static class EncodingResolver
{
    // FontDescriptor /Flags bit positions (ISO 32000-1 Table 123, 1-indexed bit numbers 3 and 6).
    private const int SymbolicFlag = 1 << 2;
    private const int NonsymbolicFlag = 1 << 5;

    private static readonly PdfName EncodingName = PdfName.Get("Encoding");
    private static readonly PdfName BaseEncodingName = PdfName.Get("BaseEncoding");
    private static readonly PdfName DifferencesName = PdfName.Get("Differences");
    private static readonly PdfName FontDescriptorName = PdfName.Get("FontDescriptor");
    private static readonly PdfName FlagsName = PdfName.Get("Flags");

    /// <summary>Resolves the effective encoding table for a simple font's dictionary.</summary>
    /// <param name="fontDict">The simple font dictionary whose <c>/Encoding</c> is resolved.</param>
    /// <param name="objects">Resolves indirect references reached from <paramref name="fontDict"/>.</param>
    /// <param name="options">Leniency/diagnostic options.</param>
    /// <param name="diagnostics">Receives recoverable-deviation diagnostics; may be <see langword="null"/>.</param>
    /// <param name="builtInBase">
    /// The font program's own built-in encoding when the caller knows it (an embedded Type 1 or
    /// bare-CFF program's encoding, or the Standard-14 <c>Symbol</c>/<c>ZapfDingbats</c> table),
    /// used as the base table wherever ISO 32000-1 §9.6.6.1 says "the font's built-in encoding";
    /// <see langword="null"/> keeps the historical StandardEncoding/opaque default.
    /// </param>
    /// <param name="builtInIsAuthoritative">
    /// When <see langword="true"/> (the Standard-14 <c>Symbol</c>/<c>ZapfDingbats</c> rule), a
    /// named <c>/Encoding</c> and a <c>/BaseEncoding</c> are ignored and only <c>/Differences</c>
    /// applies over <paramref name="builtInBase"/>; requires a non-null <paramref name="builtInBase"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="builtInIsAuthoritative"/> is <see langword="true"/> but <paramref name="builtInBase"/> is <see langword="null"/> — a programmer error, since there is nothing to be authoritative over.</exception>
    public static (string GlyphName, int Unicode)[] Resolve(PdfDictionary fontDict, IObjectSource objects, PdfOptions options, DiagnosticCollection? diagnostics, (string GlyphName, int Unicode)[]? builtInBase = null, bool builtInIsAuthoritative = false)
    {
        ArgumentNullException.ThrowIfNull(fontDict);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        if (builtInIsAuthoritative && builtInBase is null)
        {
            throw new ArgumentException($"{nameof(builtInBase)} must be non-null when {nameof(builtInIsAuthoritative)} is true.", nameof(builtInBase));
        }

        var isSymbolic = IsSymbolic(fontDict, objects);
        var encodingValue = fontDict.TryGetValue(EncodingName, out var rawEncoding) ? Resolve(objects, rawEncoding) : null;

        if (builtInIsAuthoritative)
        {
            // A named /Encoding is ignored outright (not even consulted, so it is not a
            // deviation); a /Differences array still layers on top of the built-in base;
            // /BaseEncoding is ignored inside the dict. Anything else — a malformed /Encoding
            // that is neither a name nor a dictionary — is the same deviation the non-authoritative
            // path below reports via PLUME8017; losing that diagnostic here just because the
            // caller passed builtInIsAuthoritative would drop a real document deviation silently.
            if (encodingValue is PdfDictionary authoritativeDict)
            {
                return CloneAndApplyDifferences(builtInBase!, authoritativeDict, objects, options, diagnostics);
            }

            if (encodingValue is not null and not PdfName)
            {
                ReportDeviation(options, diagnostics, "PLUME8017", $"Font /Encoding is a {encodingValue.GetType().Name}, neither a name nor a dictionary; falling back to the font's built-in encoding.");
            }

            return CloneTable(builtInBase!);
        }

        switch (encodingValue)
        {
            case null:
                return CloneTable(builtInBase ?? DefaultBaseTable(isSymbolic));

            case PdfName baseName:
                return CloneTable(ResolveBaseEncodingName(baseName.Value, isSymbolic, builtInBase, options, diagnostics));

            case PdfDictionary encodingDict:
                {
                    var baseTable = encodingDict.TryGetValue(BaseEncodingName, out var rawBase) && Resolve(objects, rawBase) is PdfName explicitBase
                        ? ResolveBaseEncodingName(explicitBase.Value, isSymbolic: false, builtInBase, options, diagnostics)
                        : builtInBase ?? DefaultBaseTable(isSymbolic);

                    return CloneAndApplyDifferences(baseTable, encodingDict, objects, options, diagnostics);
                }

            default:
                ReportDeviation(options, diagnostics, "PLUME8017", $"Font /Encoding is a {encodingValue.GetType().Name}, neither a name nor a dictionary; falling back to the font's default built-in encoding.");
                return CloneTable(builtInBase ?? DefaultBaseTable(isSymbolic));
        }
    }

    /// <summary>Clones <paramref name="baseTable"/> and layers <paramref name="encodingDict"/>'s <c>/Differences</c> array over it, if present — the one place both the authoritative-built-in path and the ordinary <c>/Encoding</c> dictionary path apply a differences override, so the two cannot diverge.</summary>
    private static (string GlyphName, int Unicode)[] CloneAndApplyDifferences((string GlyphName, int Unicode)[] baseTable, PdfDictionary encodingDict, IObjectSource objects, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var table = CloneTable(baseTable);
        if (encodingDict.TryGetValue(DifferencesName, out var rawDifferences) && Resolve(objects, rawDifferences) is PdfArray differences)
        {
            ApplyDifferences(differences, table, objects, options, diagnostics);
        }

        return table;
    }

    private static (string GlyphName, int Unicode)[] DefaultBaseTable(bool isSymbolic) =>
        isSymbolic ? OpaqueTable : SimpleFontEncodings.StandardEncoding;

    private static readonly (string GlyphName, int Unicode)[] OpaqueTable = BuildOpaqueTable();

    private static (string GlyphName, int Unicode)[] BuildOpaqueTable()
    {
        var table = new (string, int)[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = ("", -1);
        }

        return table;
    }

    private static (string GlyphName, int Unicode)[] CloneTable((string GlyphName, int Unicode)[] source)
    {
        var copy = new (string, int)[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
    }

    /// <param name="name">The base-encoding name from a <c>/Encoding</c> name or a <c>/BaseEncoding</c> entry.</param>
    /// <param name="isSymbolic">Whether the font is flagged symbolic — selects the default fallback's shape when <paramref name="builtInBase"/> is <see langword="null"/>.</param>
    /// <param name="builtInBase">
    /// The font program's own built-in encoding, when the caller has one — both fallback
    /// branches below use it in preference to <see cref="DefaultBaseTable"/> so their PLUME8017/
    /// PLUME8019 diagnostics ("falls back to the font's default built-in encoding") are actually
    /// true rather than silently reverting to StandardEncoding/opaque when a real built-in table
    /// was available.
    /// </param>
    /// <param name="options">Leniency/diagnostic options.</param>
    /// <param name="diagnostics">Receives recoverable-deviation diagnostics; may be <see langword="null"/>.</param>
    private static (string GlyphName, int Unicode)[] ResolveBaseEncodingName(string name, bool isSymbolic, (string GlyphName, int Unicode)[]? builtInBase, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        switch (name)
        {
            case "WinAnsiEncoding":
                return SimpleFontEncodings.WinAnsiEncoding;
            case "MacRomanEncoding":
                return SimpleFontEncodings.MacRomanEncoding;
            case "StandardEncoding":
                return SimpleFontEncodings.StandardEncoding;
            case "MacExpertEncoding":
                // Deferred: MacExpertEncoding's glyph set (ligatures, small caps,
                // fractions under distinct names) has no useful StandardEncoding-shaped
                // fallback; recorded and treated as unassigned throughout rather than guessing.
                ReportDeviation(options, diagnostics, "PLUME8019", "Font /Encoding names MacExpertEncoding, which PlumePDF does not resolve in this phase; codes fall back to the font's default built-in encoding.");
                return builtInBase ?? DefaultBaseTable(isSymbolic);
            default:
                ReportDeviation(options, diagnostics, "PLUME8017", $"Font /Encoding names unrecognized base encoding '{name}'; falling back to the font's default built-in encoding.");
                return builtInBase ?? DefaultBaseTable(isSymbolic);
        }
    }

    private static void ApplyDifferences(PdfArray differences, (string GlyphName, int Unicode)[] table, IObjectSource objects, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var code = -1;
        foreach (var rawItem in differences)
        {
            var item = Resolve(objects, rawItem);
            switch (item)
            {
                case PdfNumber number when number.TryToInt32(out var n):
                    code = n;
                    break;

                case PdfName glyphName:
                    if (code is >= 0 and <= 255)
                    {
                        var unicode = SimpleFontEncodings.TryGlyphNameToUnicode(glyphName.Value, out var resolvedUnicode) ? resolvedUnicode : -1;
                        table[code] = (glyphName.Value, unicode);
                    }
                    else
                    {
                        ReportDeviation(options, diagnostics, "PLUME8018", $"/Differences entry '{glyphName.Value}' falls outside the valid 0-255 code range (running code {code}); skipped.");
                    }

                    code++;
                    break;

                default:
                    ReportDeviation(options, diagnostics, "PLUME8018", "Malformed /Differences entry: expected an integer code or a glyph name; entry skipped.");
                    break;
            }
        }
    }

    private static bool IsSymbolic(PdfDictionary fontDict, IObjectSource objects)
    {
        if (!fontDict.TryGetValue(FontDescriptorName, out var rawDescriptor) || Resolve(objects, rawDescriptor) is not PdfDictionary descriptor)
        {
            return false;
        }

        if (!descriptor.TryGetValue(FlagsName, out var rawFlags) || Resolve(objects, rawFlags) is not PdfNumber flagsNumber || !flagsNumber.TryToInt32(out var flags))
        {
            return false;
        }

        return (flags & SymbolicFlag) != 0 && (flags & NonsymbolicFlag) == 0;
    }

    private static PdfObject Resolve(IObjectSource objects, PdfObject value) =>
        value is PdfReference reference ? objects.Resolve(reference.Target) : value;

    private static void ReportDeviation(PdfOptions options, DiagnosticCollection? diagnostics, string code, string message)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}
