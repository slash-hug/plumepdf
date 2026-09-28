namespace PlumePdf.Raster.Color;

/// <summary>
/// Info-tier diagnostics for the print-pipeline exotica this rasterizer explicitly does
/// <b>not</b> render in 1.x: non-soft-mask transfer
/// functions, black-generation/undercolor-removal (BG/UCR), and halftone dictionaries — every
/// one of these only matters for a physical (or physically-simulated) print pipeline, not
/// on-screen sRGB rendering, so PlumePDF's rasterizer honors ISO 32000-1's own permission to
/// ignore them (§8.6.5.6, §10.4) and records why rather than silently doing nothing.
/// </summary>
/// <remarks>
/// These three are diagnostics, never thrown exceptions — an ExtGState carrying <c>/TR</c>,
/// <c>/BG</c>, or <c>/HT</c> for print-pipeline purposes is a perfectly well-formed PDF, not a
/// deviation; the diagnostic exists purely so a caller inspecting <c>doc.Diagnostics</c> can
/// tell "PlumePDF saw this and chose not to render it" from "PlumePDF never noticed it." The
/// one exception already handled elsewhere: an ExtGState's <c>/SMask</c>-scoped <c>/TR</c>
/// (soft-mask transfer function, ISO 32000-1 §11.6.5.2) already renders for real via
/// <see cref="Transparency.SoftMask"/> — <see cref="ReportTransferFunction"/> is only for the
/// separate, non-soft-mask <c>/TR</c>/<c>/TR2</c> ExtGState entries (§8.6.5.6/Table 58), which
/// this class's caller must not confuse with the soft mask's own.
/// </remarks>
internal static class PrintPipelineDiagnostics
{
    private static readonly PdfName TrName = PdfName.Get("TR");
    private static readonly PdfName Tr2Name = PdfName.Get("TR2");
    private static readonly PdfName BgName = PdfName.Get("BG");
    private static readonly PdfName Bg2Name = PdfName.Get("BG2");
    private static readonly PdfName UcrName = PdfName.Get("UCR");
    private static readonly PdfName Ucr2Name = PdfName.Get("UCR2");
    private static readonly PdfName HtName = PdfName.Get("HT");

    /// <summary>
    /// Inspects an ExtGState dictionary for every one of the three 1.x-deferred print-exotica
    /// entry families and records the matching diagnostic for each one present. Call this once
    /// per <c>gs</c> operator (or ExtGState resource resolution) a caller processes; a
    /// dictionary carrying none of these entries is a silent no-op.
    /// </summary>
    /// <param name="extGState">The resolved ExtGState dictionary.</param>
    /// <param name="diagnostics">Receives the diagnostics; <see langword="null"/> makes this a no-op.</param>
    internal static void CheckExtGState(PdfDictionary extGState, DiagnosticCollection? diagnostics)
    {
        if (diagnostics is null)
        {
            return;
        }

        if (extGState.ContainsKey(TrName) || extGState.ContainsKey(Tr2Name))
        {
            ReportTransferFunction(diagnostics);
        }

        if (extGState.ContainsKey(BgName) || extGState.ContainsKey(Bg2Name) || extGState.ContainsKey(UcrName) || extGState.ContainsKey(Ucr2Name))
        {
            ReportBlackGenerationUndercolorRemoval(diagnostics);
        }

        if (extGState.ContainsKey(HtName))
        {
            ReportHalftone(diagnostics);
        }
    }

    /// <summary>Records <c>PLUME7740</c>: a non-soft-mask <c>/TR</c>/<c>/TR2</c> transfer function was present and is not applied.</summary>
    internal static void ReportTransferFunction(DiagnosticCollection diagnostics) =>
        diagnostics.Add(new PdfDiagnostic(
            "PLUME7740",
            DiagnosticSeverity.Info,
            "An ExtGState /TR or /TR2 (non-soft-mask) transfer function was present but is not applied — transfer functions outside soft-mask use are a print-pipeline concern PlumePDF's rasterizer does not simulate in 1.x (ISO 32000-1 §8.6.5.6).",
            null));

    /// <summary>Records <c>PLUME7741</c>: a <c>/BG</c>/<c>/BG2</c>/<c>/UCR</c>/<c>/UCR2</c> entry was present and is not applied.</summary>
    internal static void ReportBlackGenerationUndercolorRemoval(DiagnosticCollection diagnostics) =>
        diagnostics.Add(new PdfDiagnostic(
            "PLUME7741",
            DiagnosticSeverity.Info,
            "An ExtGState /BG, /BG2, /UCR, or /UCR2 (black-generation/undercolor-removal) function was present but is not applied — BG/UCR only affects a physical CMYK separation pipeline, out of scope for PlumePDF's sRGB rasterizer in 1.x.",
            null));

    /// <summary>Records <c>PLUME7742</c>: an <c>/HT</c> halftone dictionary was present and is not applied.</summary>
    internal static void ReportHalftone(DiagnosticCollection diagnostics) =>
        diagnostics.Add(new PdfDiagnostic(
            "PLUME7742",
            DiagnosticSeverity.Info,
            "An ExtGState /HT halftone dictionary was present but is not applied — halftoning only affects physical print output, out of scope for PlumePDF's continuous-tone sRGB rasterizer in 1.x (ISO 32000-1 §10.4).",
            null));
}
