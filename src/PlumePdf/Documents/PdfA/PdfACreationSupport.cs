using System.Globalization;
using PlumePdf.Documents.Metadata;

namespace PlumePdf.Documents.PdfA;

/// <summary>
/// The PDF/A create-path orchestration behind
/// <see cref="PdfOptions.PdfAConformance"/>: the version-knob coercion both
/// <c>Manuscript.Render</c> and <c>PdfDocument.Save</c> apply, the XMP + DocInfo + output
/// intent finishing pass the renderer runs over a freshly composed document, and the
/// Standard-14 font refusal (<c>PLUME8023</c>). Internal, same shape as
/// <see cref="OutputIntentBuilder"/> — the public surface is the
/// <see cref="PdfOptions.PdfAConformance"/> knob itself.
/// </summary>
/// <remarks>
/// Deliberately takes plain values (title, dates) rather than a <c>Manuscript</c>:
/// <c>PlumePdf.Manuscript</c> is the Layout layer's façade
/// (<c>tests/PlumePdf.ArchitectureTests/LayeringTests.FacadeTypes</c> pins it at layer 7), so
/// this Documents-layer type must never depend on it — the renderer unwraps the manuscript's
/// metadata into these primitives, exactly like <c>PdfDocument.Save</c> unwraps its state for
/// <c>FullRewriteWriter</c>.
/// </remarks>
internal static class PdfACreationSupport
{
    /// <summary>The <c>/Producer</c>/<c>pdf:Producer</c> string the PDF/A create path writes — a fixed, version-free literal so <see cref="PdfOptions.Deterministic"/> output never varies across PlumePDF releases.</summary>
    public const string Producer = "PlumePDF";

    /// <summary>
    /// Applies <see cref="PdfOptions.PdfAConformance"/>'s version-knob rules
    /// to <paramref name="options"/>: <see cref="PdfAConformance.A1b"/> forces
    /// <see cref="PdfOptions.PdfVersion"/> to <c>"1.4"</c> (object/cross-reference streams do
    /// not exist at that version, so nothing forbidden can be emitted);
    /// <see cref="PdfAConformance.A2b"/> keeps the caller's version but refuses one above the
    /// PDF/A-2 ceiling of 1.7 (or one that does not parse as <c>major.minor</c> at all) —
    /// forcing an explicitly chosen higher version down silently would be a silent behavior
    /// change, so it refuses instead.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME6071</c> — see above.</exception>
    public static PdfOptions ApplyVersionKnob(PdfOptions options)
    {
        switch (options.PdfAConformance)
        {
            case PdfAConformance.A1b:
                return options.PdfVersion == "1.4" ? options : options with { PdfVersion = "1.4" };

            case PdfAConformance.A2b:
                if (!TryParseVersion(options.PdfVersion, out var major, out var minor))
                {
                    throw new PlumePdfException(
                        "PLUME6071",
                        $"PdfOptions.PdfVersion (\"{options.PdfVersion}\") does not parse as a 'major.minor' PDF version, so PDF/A-2B's 1.7 header ceiling cannot be honored — set a parseable version at or below \"1.7\" (or leave the default).");
                }

                if (major > 1 || (major == 1 && minor > 7))
                {
                    throw new PlumePdfException(
                        "PLUME6071",
                        $"PdfOptions.PdfVersion (\"{options.PdfVersion}\") exceeds PDF/A-2B's header ceiling of 1.7 — lower the version (or leave the default \"1.7\"), or drop PdfOptions.PdfAConformance.");
                }

                return options;

            default:
                return options;
        }
    }

    /// <summary>
    /// The finishing pass the renderer runs over a freshly composed document when
    /// <see cref="PdfOptions.PdfAConformance"/> is set: attaches the <c>GTS_PDFA1</c> output
    /// intent (bundled sRGB unless <see cref="PdfOptions.PdfAOutputIntentProfile"/> overrides
    /// it — the override is defensively copied, so later caller mutation never reaches the
    /// written document), then writes the XMP packet (<c>pdfaid</c> identification included)
    /// and the <c>/Info</c> dictionary with agreeing title/producer/dates (the same
    /// agreement rule, enforced again by <c>PdfDocument.SetInfo</c>/<c>SetXmpMetadata</c>).
    /// </summary>
    /// <param name="document">The freshly composed document to finish.</param>
    /// <param name="title">The manuscript's title, or <see langword="null"/> for none.</param>
    /// <param name="createDate">The caller-supplied creation date, or <see langword="null"/> to default to now (refused under <see cref="PdfOptions.Deterministic"/>).</param>
    /// <param name="modifyDate">The caller-supplied modification date, with <paramref name="createDate"/>'s contract.</param>
    /// <param name="options">The render options (conformance level, determinism, output-intent override, XMP write cap).</param>
    /// <param name="declarePdfUa">Whether to also declare <c>pdfuaid:part = 1</c> (Manuscript.PdfUa) — the pdfaid and pdfuaid schemas coexist in the one packet.</param>
    /// <exception cref="PlumePdfException"><c>PLUME6056</c>/<c>PLUME6059</c> via <c>SetXmpMetadata</c>. The date/determinism precondition itself is <see cref="EnsureDeterministicDatesSupplied"/>'s to check, before any layout work runs — per the house "internal layers assume validated input from the layer above" rule, this method does not re-check it (<c>XmpWriter</c>'s own <c>PLUME6058</c> guard remains the ultimate backstop regardless).</exception>
    public static void ApplyCreateMetadata(PdfDocument document, string? title, DateTimeOffset? createDate, DateTimeOffset? modifyDate, PdfOptions options, bool declarePdfUa = false)
    {
        var now = DateTimeOffset.Now;
        var effectiveCreate = createDate ?? now;
        var effectiveModify = modifyDate ?? now;

        var profile = options.PdfAOutputIntentProfile is { } custom ? (byte[])custom.Clone() : OutputIntentBuilder.SrgbProfile;
        OutputIntentBuilder.AddOutputIntent(document, profile, options.PdfAOutputConditionIdentifier);

        document.SetXmpMetadata(new XmpPacket
        {
            Title = title,
            Producer = Producer,
            CreateDate = effectiveCreate,
            ModifyDate = effectiveModify,
            Conformance = options.PdfAConformance,
            DeclarePdfUa = declarePdfUa,
        });

        document.SetInfo(new DocInfoMetadata
        {
            Title = title,
            Producer = Producer,
            CreationDate = effectiveCreate,
            ModDate = effectiveModify,
        });
    }

    /// <summary>
    /// The precondition, checked before any layout work runs: PDF/A output
    /// combined with <see cref="PdfOptions.Deterministic"/> requires both dates supplied by
    /// the caller — PlumePDF never invents a fixed or "now" timestamp under the byte-identical
    /// guarantee, since either would silently violate one of the two promises. Same condition
    /// class (and therefore the same code) as <c>XmpWriter</c>'s own <c>PLUME6058</c> guard —
    /// thrown here so the message can name the Manuscript-level surface a Render/Compose
    /// caller actually has in hand, and so the refusal fires before pagination rather than
    /// after a full render.
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME6058</c> — see above.</exception>
    public static void EnsureDeterministicDatesSupplied(DateTimeOffset? createDate, DateTimeOffset? modifyDate, PdfOptions options)
    {
        if (options.Deterministic && (createDate is null || modifyDate is null))
        {
            throw new PlumePdfException(
                "PLUME6058",
                "PDF/A output (PdfOptions.PdfAConformance) combined with PdfOptions.Deterministic requires both Manuscript.CreateDate and Manuscript.ModifyDate to be supplied explicitly — PlumePDF never invents a fixed or 'now' timestamp on the caller's behalf, since either would silently violate one of the two guarantees. (PdfDocument.Compose exposes no date surface; use the Manuscript route for deterministic PDF/A.)");
        }
    }

    /// <summary>
    /// Builds the <c>PLUME8023</c> Standard-14 refusal: PDF/A requires every
    /// font embedded, Standard-14 fonts embed nothing by design, and the refusal names
    /// <em>every</em> offending font plus where it was first used and the one-call fix path —
    /// never a silent substitution, never a bundled fallback font.
    /// </summary>
    /// <param name="conformance">The requested conformance level, for the message.</param>
    /// <param name="offenders">Every Standard-14 font in use: its name and a short description of where it was first used.</param>
    public static PlumePdfException Standard14Refusal(PdfAConformance conformance, IReadOnlyList<(string FontName, string FirstUse)> offenders)
    {
        var level = conformance == PdfAConformance.A1b ? "PDF/A-1B" : "PDF/A-2B";
        var names = string.Join(", ", offenders.Select(static o => $"'{o.FontName}' (first used by {o.FirstUse})"));
        var count = offenders.Count.ToString(CultureInfo.InvariantCulture);
        var watermarkNote = offenders.Any(static o => o.FirstUse.StartsWith("Watermark", StringComparison.Ordinal) || o.FirstUse.StartsWith("Stamp", StringComparison.Ordinal))
            ? " Note: Section.Watermark/Section.Stamps always draw with Helvetica-Bold and cannot be used under a PDF/A conformance in v1.0."
            : string.Empty;

        return new PlumePdfException(
            "PLUME8023",
            $"{level} requires every font to be embedded; {count} Standard-14 font(s) in use embed nothing by design: {names}. Supply a TrueType/OpenType file instead — e.g. Font = PdfFont.FromFile(\"fonts/NotoSans-Regular.ttf\") on each Text — and PlumePDF embeds and subsets it automatically; no silent substitution is ever made.{watermarkNote}");
    }

    private static bool TryParseVersion(string? version, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            // FullRewriteWriter treats a blank knob as its "1.7" default — within the ceiling.
            major = 1;
            minor = 7;
            return true;
        }

        var separator = version.IndexOf('.', StringComparison.Ordinal);
        return separator > 0
            && int.TryParse(version.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && int.TryParse(version.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }
}
