using PlumePdf.Objects;

namespace PlumePdf.Raster.OptionalContent;

/// <summary>
/// Resolves optional-content (OCG/OCMD, ISO 32000-1 §8.11) visibility against a document's
/// default configuration (the catalog's <c>/OCProperties</c> <c>/D</c> dictionary). Scope is
/// deliberately basic: only the default configuration's
/// <c>/OFF</c> array and each OCG's <c>/Usage</c> <c>/Print</c> override are honored — there is no
/// layer-toggling public API (1.x), and alternate (<c>/OCProperties</c> <c>/Configs</c>) or
/// <c>/AS</c>-usage-application configurations are not read.
/// </summary>
/// <remarks>
/// One instance is meant to be built fresh per <c>Rasterizer.Rasterize</c> call (via
/// <see cref="Parse"/>, from the already-resolved <c>/OCProperties</c> dictionary the caller reads
/// off the document catalog — a Documents-layer concern, kept out of <c>PlumePdf.Raster</c> per
/// <c>docs/architecture.md</c>'s layer discipline) — its own one-shot "already told the caller
/// about a suppression this page" state (<see cref="TryClaimSuppressionNotice"/>) assumes exactly
/// that lifetime, matching how <c>Fonts.Reading.RenderFontFactory</c>'s own
/// <c>TryClaimRenderModeNotice</c> is scoped to one page's <see cref="RasterInterpreter.BuildDisplayList"/>
/// call tree (PLUME7730's precedent).
/// </remarks>
internal sealed class OptionalContentConfig
{
    private static readonly PdfName TypeName = PdfName.Type;
    private static readonly PdfName OcmdName = PdfName.Get("OCMD");
    private static readonly PdfName OcgsName = PdfName.Get("OCGs");
    private static readonly PdfName PName = PdfName.P;
    // ISO 32000-1 Table 96/101: the /D configuration dictionary's /OFF array key and a /Usage
    // /Print /PrintState value are the literal uppercase name /OFF — a different name entirely
    // from PdfName.Off ("Off", mixed case), which is only the AcroForm widget /AS off-state
    // convention (ISO 32000-1 §12.7.4.2.3). Reusing PdfName.Off here would silently never match
    // either use site (PDF names are case-sensitive tokens).
    private static readonly PdfName OffName = PdfName.Get("OFF");
    private static readonly PdfName OnName = PdfName.Get("ON");
    private static readonly PdfName DName = PdfName.Get("D");
    private static readonly PdfName UsageName = PdfName.Get("Usage");
    private static readonly PdfName PrintName = PdfName.Get("Print");
    private static readonly PdfName PrintStateName = PdfName.Get("PrintState");

    private readonly HashSet<int> _offByDefault;
    private int _suppressionNoticeClaimed;

    private OptionalContentConfig(HashSet<int> offByDefault) => _offByDefault = offByDefault;

    /// <summary>The trivial config every OCG/OCMD resolves visible under — used when a document has no <c>/OCProperties</c> at all (the overwhelming common case, not a malformed-document deviation).</summary>
    public static OptionalContentConfig AllVisible => new([]);

    /// <summary>
    /// Parses <paramref name="ocPropertiesValue"/> (the catalog's <c>/OCProperties</c> entry,
    /// already looked up by the caller — <see langword="null"/> when the document has none) into
    /// a config. A missing or unresolvable <c>/D</c> default-configuration dictionary degrades to
    /// <see cref="AllVisible"/> with a <c>PLUME7734</c> diagnostic (best-effort, never
    /// throws — the Raster paint path degrades per-object/per-document-feature rather than
    /// aborting the page, the same posture <see cref="RasterInterpreter"/>'s own undecodable-Form-XObject
    /// handling already uses).
    /// </summary>
    public static OptionalContentConfig Parse(PdfObject? ocPropertiesValue, ObjectRegistry? objects, DiagnosticCollection? diagnostics)
    {
        if (Resolve(ocPropertiesValue, objects) is not PdfDictionary ocProperties)
        {
            return AllVisible;
        }

        if (Resolve(ocProperties.TryGetValue(DName, out var dValue) ? dValue : null, objects) is not PdfDictionary d)
        {
            ReportMalformed(diagnostics, "has no usable /D default configuration dictionary; every optional-content layer renders visible.");
            return AllVisible;
        }

        var off = new HashSet<int>();
        if (Resolve(d.TryGetValue(OffName, out var offValue) ? offValue : null, objects) is PdfArray offArray)
        {
            foreach (var item in offArray)
            {
                if (item is PdfReference reference)
                {
                    off.Add(reference.Target.Number);
                }
            }
        }

        return new OptionalContentConfig(off);
    }

    /// <summary>
    /// Whether the OCG or OCMD <paramref name="ocgOrOcmdValue"/> (the raw, not-yet-resolved value
    /// as it appears at the use site — a <c>BDC /OC</c> tag's resolved <c>/Properties</c> entry, or
    /// an annotation's own <c>/OC</c> key) is visible under <paramref name="printIntent"/>.
    /// Unresolvable or malformed input degrades to visible (best-effort) rather than
    /// suppressing content the config couldn't actually parse.
    /// </summary>
    public bool IsVisible(PdfObject? ocgOrOcmdValue, ObjectRegistry? objects, bool printIntent)
    {
        if (ocgOrOcmdValue is null)
        {
            return true;
        }

        if (Resolve(ocgOrOcmdValue, objects) is not PdfDictionary dict)
        {
            return true;
        }

        var isOcmd = dict.TryGetValue(TypeName, out var type) && type is PdfName typeName && ReferenceEquals(typeName, OcmdName);
        if (isOcmd)
        {
            return IsOcmdVisible(dict, objects, printIntent);
        }

        var number = ocgOrOcmdValue is PdfReference selfReference ? selfReference.Target.Number : (int?)null;
        return IsOcgVisible(number, dict, objects, printIntent);
    }

    /// <summary>Claims the once-per-page (per this instance's lifetime) right to report a PLUME7733 suppression notice — the same claim-once idiom PLUME7730 uses. Returns <see langword="true"/> exactly once.</summary>
    public bool TryClaimSuppressionNotice() => Interlocked.Exchange(ref _suppressionNoticeClaimed, 1) == 0;

    private bool IsOcgVisible(int? number, PdfDictionary ocg, ObjectRegistry? objects, bool printIntent)
    {
        var baseOn = number is not { } n || !_offByDefault.Contains(n);

        if (!printIntent)
        {
            return baseOn;
        }

        // /Usage /Print /PrintState overrides the base ON/OFF under print intent.
        if (Resolve(ocg.TryGetValue(UsageName, out var usageValue) ? usageValue : null, objects) is PdfDictionary usage
            && Resolve(usage.TryGetValue(PrintName, out var printValue) ? printValue : null, objects) is PdfDictionary printUsage
            && Resolve(printUsage.TryGetValue(PrintStateName, out var stateValue) ? stateValue : null, objects) is PdfName state)
        {
            if (ReferenceEquals(state, OnName))
            {
                return true;
            }

            if (ReferenceEquals(state, OffName))
            {
                return false;
            }
        }

        return baseOn;
    }

    private bool IsOcmdVisible(PdfDictionary ocmd, ObjectRegistry? objects, bool printIntent)
    {
        var members = new List<(int? Number, PdfDictionary Dict)>();
        switch (Resolve(ocmd.TryGetValue(OcgsName, out var membersValue) ? membersValue : null, objects))
        {
            case PdfDictionary singleDirect:
                members.Add((null, singleDirect));
                break;

            case PdfArray array:
                foreach (var item in array)
                {
                    if (Resolve(item, objects) is PdfDictionary member)
                    {
                        members.Add((item is PdfReference memberRef ? memberRef.Target.Number : null, member));
                    }
                }

                break;
        }

        if (members.Count == 0)
        {
            return true; // No members named — spec default is visible.
        }

        var policy = Resolve(ocmd.TryGetValue(PName, out var pValue) ? pValue : null, objects) is PdfName p ? p.Value : "AnyOn";
        var visibleFlags = new bool[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            visibleFlags[i] = IsOcgVisible(members[i].Number, members[i].Dict, objects, printIntent);
        }

        return policy switch
        {
            "AllOn" => Array.TrueForAll(visibleFlags, static v => v),
            "AnyOff" => Array.Exists(visibleFlags, static v => !v),
            "AllOff" => Array.TrueForAll(visibleFlags, static v => !v),
            _ => Array.Exists(visibleFlags, static v => v), // "AnyOn" (default, ISO 32000-1 §8.11.2.3).
        };
    }

    private static void ReportMalformed(DiagnosticCollection? diagnostics, string message) =>
        diagnostics?.Add(new PdfDiagnostic("PLUME7734", DiagnosticSeverity.Warning, $"/OCProperties {message}"));

    private static PdfObject? Resolve(PdfObject? value, ObjectRegistry? objects) =>
        value is PdfReference reference && objects is not null ? objects[reference.Target] : value;
}
