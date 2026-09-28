using System.Linq;
using PlumePdf.Content;
using PlumePdf.Fonts.Outlines;
using PlumePdf.Fonts.Reading;
using PlumePdf.Objects;
using PlumePdf.Raster.Agg;
using PlumePdf.Raster.Color;
using PlumePdf.Raster.DisplayList;
using PlumePdf.Raster.Functions;
using PlumePdf.Raster.Glyphs;
using PlumePdf.Raster.OptionalContent;
using PlumePdf.Raster.Patterns;
using PlumePdf.Raster.Shading;
using PlumePdf.Raster.Transparency;

namespace PlumePdf.Raster;

/// <summary>
/// The two-pass raster interpreter: pass 1
/// (<see cref="BuildDisplayList"/>) walks <see cref="ContentStreamReader"/>'s operator stream
/// (reuse — no second content-stream tokenizer) and normalizes it into a
/// <see cref="DisplayList.PageObject"/> tree; pass 2 (<see cref="Paint"/>) walks that tree and
/// paints it onto a <see cref="RasterSurface"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type owns path construction/painting (<see cref="Agg.OutlineRasterizer"/>/
/// <see cref="Agg.ScanlineRasterizer"/>/<see cref="Agg.StrokeGenerator"/>), image painting
/// (<see cref="ImagePainter"/>), and Form XObject recursion. Text-showing operators
/// (<c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c>) update text-positioning state (<c>Tf</c>/<c>Td</c>/
/// <c>TD</c>/<c>Tm</c>/<c>T*</c>/<c>Tc</c>/<c>Tw</c>/<c>Tz</c>/<c>TL</c>/<c>Ts</c>/<c>Tr</c> are all
/// tracked on <see cref="RasterGraphicsState"/>) and emit <see cref="TextPageObject"/>s: each
/// shown code is resolved through <see cref="RenderFontFactory"/> (which reuses the Phase 3
/// <see cref="ExtractionFont"/> for advance widths/code lengths and the Fonts-layer
/// <c>SfntReader</c>/<c>CffParser</c>/<c>Type1Parser</c>/substitute store for glyph outlines) into
/// a positioned <see cref="Fonts.Outlines.GlyphOutline"/>, which the paint pass hands to
/// <c>Raster/Glyphs/GlyphRasterizer.cs</c>. Decoding an <c>/Image</c> XObject's samples
/// into a <see cref="RasterImageFrame"/> (colorspace/<c>/Decode</c>/<c>/SMask</c> resolution) is
/// today's <c>Documents.ImageExtractor</c> logic, one layer above <c>PlumePdf.Raster</c> — this
/// interpreter accepts that decode as an injected <see cref="ImageResolver"/> delegate rather
/// than duplicating it, keeping the layer boundary (<c>docs/architecture.md</c>) intact; the
/// integration checkpoint is expected to supply one backed by the real decode path. Shading
/// (<c>sh</c>) and pattern colors (<c>scn</c> with a <c>/Pattern</c> name) are captured
/// structurally (<see cref="ShadingPageObject"/>, <see cref="PaintColor.PatternName"/>);
/// pattern colors still paint as a flat mid-gray placeholder. <c>sh</c> shadings resolve through
/// the real <c>Color/ColorSpace.cs</c>/<c>Functions/FunctionEvaluator.cs</c>: types 2/3
/// (axial/radial, <c>Shading/AxialShading.cs</c>/<c>RadialShading.cs</c>) paint one representative
/// color — a flat approximation, not a true per-pixel gradient, a deliberate Phase 8 scope
/// decision, not a gap — while types 4-7 (mesh, <c>Shading/MeshShading.cs</c>)
/// build a true per-pixel render from the shading resource's own stream
/// (<see cref="ShadingPageObject.ShadingStream"/>, threaded through by <see cref="HandleSh"/>).
/// Never silently "nothing" in any case, so a page that uses them still renders something
/// recognizable rather than a blank hole.
/// </para>
/// <para>
/// Clipping (<c>W</c>/<c>W*</c>) is enforced as the device-space bounding box of the
/// accumulated clip chain, not a full arbitrary-shape coverage mask — an intentional
/// simplification for paths/images (a rectangular <c>re W n</c> crop, by
/// far the common case, is exact; a non-rectangular clip degrades to its bounding box). Exact
/// per-pixel clip-shape intersection is naturally revisited alongside transparency-group
/// compositing, which needs real coverage masks for soft masks regardless.
/// </para>
/// </remarks>
internal static class RasterInterpreter
{
    private const int DefaultMaxFormNestingDepth = 32;

    /// <summary>Resolves a decoded <c>/Image</c> XObject stream to pixels — see the type remarks for why this is injected rather than implemented here.</summary>
    public delegate RasterImageFrame? ImageResolver(PdfDictionary imageDictionary, PdfStream stream);

    /// <summary>
    /// The cumulative display-list object count for one <see cref="BuildDisplayList"/> call tree
    /// (<c>PdfOptions.MaxDisplayListObjects</c>) — one instance created at the
    /// top-level call (<c>formDepth == 0</c>) and threaded unchanged through every recursive Form
    /// XObject call, mirroring <c>Documents.TextExtractor.WalkContext.TotalOperators</c>'s
    /// "total work across the whole page, not per-call" discipline.
    /// </summary>
    internal sealed class DisplayListBudget(int max)
    {
        private int _count;

        public void Increment()
        {
            if (++_count > max)
            {
                throw new PlumePdfException("PLUME7503", $"Raster display-list build produced more than {max} objects (PdfOptions.MaxDisplayListObjects); refusing to continue (a resource-limit guard against a hostile or pathological content stream).");
            }
        }
    }

    // Every PageObject the display-list build adds must go through here — the one choke point
    // that makes MaxDisplayListObjects a real, enforced cap rather than a declared-but-dead
    // property (PdfOptionsCapWiringTests; the cap's own get_MaxDisplayListObjects call site is
    // BuildDisplayList's DisplayListBudget construction, above).
    private static void AddChild(List<PageObject> children, DisplayListBudget budget, PageObject obj)
    {
        budget.Increment();
        children.Add(obj);
    }

    /// <summary>
    /// Pass 1: builds the normalized display-list tree for one content stream (a page's own, or
    /// a Form XObject's, recursively).
    /// </summary>
    /// <param name="contentBytes">The already filter-decoded content-stream bytes.</param>
    /// <param name="resources">The <c>/Resources</c> dictionary in scope (already resolved from any indirect reference).</param>
    /// <param name="initialCtm">The transformation matrix in effect before this content stream's first operator (the page-to-device matrix for a page, or the composed Form/pattern matrix for a nested call).</param>
    /// <param name="options">Controls resource limits and <see cref="PdfOptions.Strict"/> (threaded to <see cref="ContentStreamReader"/>).</param>
    /// <param name="diagnostics">Recoverable deviations are appended here.</param>
    /// <param name="objects">Resolves indirect references reached through <paramref name="resources"/> (Form XObject <c>/Resources</c>/<c>/Contents</c>, <c>/ExtGState</c> entries), or <see langword="null"/> when the caller has none (a resource dictionary with only direct entries still works).</param>
    /// <param name="imageResolver">Decodes an <c>/Image</c> XObject to pixels — see the type remarks.</param>
    /// <param name="formDepth">The current Form XObject recursion depth — internal reentry guard, callers always omit this.</param>
    /// <param name="initialClip">The clip region already in effect before this content stream's first operator — <see langword="null"/> for a page's top-level build; a recursive Form XObject call passes the clip active at the <c>Do</c> call site so the form's own content inherits it.</param>
    /// <param name="budget">The cumulative display-list-object counter (<see cref="PdfOptions.MaxDisplayListObjects"/>) — <see langword="null"/> for a page's top-level build (a fresh one is created); a recursive Form XObject call always threads the top-level call's own instance through, so the cap applies across the whole page, not per Form.</param>
    /// <param name="renderFonts">The render-font resolver text-showing operators use to turn a <c>/Font</c> resource into positioned glyph outlines — <see langword="null"/> for a page's top-level build (a fresh one is created when <paramref name="objects"/> is available; text is skipped when it is not); a recursive Form XObject call always threads the top-level call's own instance through so a font is parsed once per page.</param>
    /// <param name="optionalContent">
    /// Resolves <c>BDC /OC … EMC</c> marked-content visibility (Phase 9, ISO 32000-1 §8.11) — a
    /// suppressed scope's painting operators (path fill/stroke, text, <c>Do</c>, <c>sh</c>)
    /// contribute nothing to the display list, though graphics-state operators inside it (<c>q</c>/
    /// <c>Q</c>/<c>cm</c>/color/clip) still run, so state after the matching <c>EMC</c> stays
    /// correct. <see langword="null"/> (the default) never suppresses anything — every
    /// <c>BDC</c>/<c>BMC</c>/<c>EMC</c> is still tracked for nesting, but no tag is looked up. A
    /// recursive Form XObject or annotation-appearance call threads the same instance through, so
    /// its once-per-page <c>PLUME7733</c> notice (<see cref="OptionalContentConfig.TryClaimSuppressionNotice"/>)
    /// fires at most once across the whole <c>Rasterizer.Rasterize</c> call, not once per content
    /// stream. Marked-content nesting itself is always local to <em>this</em> content stream — a
    /// <c>BDC</c> can only be closed by an <c>EMC</c> in the same stream (§14.6) — so, unlike
    /// <paramref name="budget"/>/<paramref name="renderFonts"/>, no separate "stack" parameter is
    /// threaded through recursion.
    /// </param>
    /// <param name="printIntent">Whether this build is for print (honors an OCG's <c>/Usage</c> <c>/Print</c> override) rather than view — <see cref="AnnotationFlagMatrix.ShouldRender"/>'s own view/print split, applied identically here so a page's marked content and its annotations agree on which OCGs are ON.</param>
    /// <exception cref="PlumePdfException"><c>PLUME7502</c> — Form XObject recursion exceeded <see cref="DefaultMaxFormNestingDepth"/> (mirrors <c>PdfOptions.MaxXObjectNestingDepth</c>'s existing role elsewhere, applying the same discipline to recursion rather than a buffer).</exception>
    public static FormPageObject BuildDisplayList(
        ReadOnlyMemory<byte> contentBytes,
        PdfDictionary? resources,
        PdfMatrix initialCtm,
        PdfOptions options,
        DiagnosticCollection? diagnostics,
        ObjectRegistry? objects = null,
        ImageResolver? imageResolver = null,
        int formDepth = 0,
        ClipPath? initialClip = null,
        DisplayListBudget? budget = null,
        RenderFontFactory? renderFonts = null,
        OptionalContentConfig? optionalContent = null,
        bool printIntent = false)
    {
        if (formDepth > DefaultMaxFormNestingDepth)
        {
            throw new PlumePdfException("PLUME7502", $"Raster display-list build recursed through more than {DefaultMaxFormNestingDepth} nested Form XObjects — refusing to continue (a resource-limit guard against a hostile or pathological document).");
        }

        // A fresh counter at the top-level call (formDepth == 0), threaded unchanged through
        // every recursive Form XObject call below — cumulative across the whole page's display
        // list, the same "total work done, not just nesting depth" discipline
        // Documents.TextExtractor.WalkContext.TotalOperators already applies to content-stream
        // operators (PdfOptions.MaxDisplayListObjects).
        var activeBudget = budget ?? new DisplayListBudget(options.MaxDisplayListObjects);

        // One render-font factory per page (formDepth == 0), threaded unchanged through every
        // nested Form XObject call so a font's parsed program and diagnostics are shared, not
        // rebuilt per form. Requires an object graph to dereference indirect font dictionaries and
        // embedded font programs; a build with none simply renders no text (degraded, never fatal).
        var activeRenderFonts = renderFonts ?? (objects is not null ? new RenderFontFactory(objects, options.Filters, options, diagnostics) : null);

        var ops = ContentStreamReader.Read(contentBytes.Span, options, diagnostics, options.MaxContentStreamOperators);
        var state = new RasterGraphicsState { Ctm = initialCtm, Clip = initialClip };
        var children = new List<PageObject>();
        var builder = new PathBuilder();
        var textMatrix = PdfMatrix.Identity;
        var textLineMatrix = PdfMatrix.Identity;

        // Marked-content visibility stack (BDC/BMC/EMC, §14.6) — always local to this one content
        // stream (see the optionalContent parameter's remarks). Each entry is "suppressed at this
        // nesting level", cumulative with its parent so a child BDC/BMC cannot re-enable content
        // an ancestor's /OC turned off.
        var mcSuppressed = new Stack<bool>();
        bool IsContentSuppressed() => mcSuppressed.Count > 0 && mcSuppressed.Peek();

        foreach (var op in ops)
        {
            switch (op.Operator)
            {
                case "q":
                    state.Save();
                    break;
                case "Q":
                    state.Restore();
                    break;
                case "cm":
                    state.Concatenate(ReadMatrix(op.Operands));
                    break;

                case "m":
                    builder.MoveTo(Transform(state.Ctm, Num(op.Operands, 0), Num(op.Operands, 1)));
                    break;
                case "l":
                    builder.LineTo(Transform(state.Ctm, Num(op.Operands, 0), Num(op.Operands, 1)));
                    break;
                case "c":
                    builder.CurveTo(
                        Transform(state.Ctm, Num(op.Operands, 0), Num(op.Operands, 1)),
                        Transform(state.Ctm, Num(op.Operands, 2), Num(op.Operands, 3)),
                        Transform(state.Ctm, Num(op.Operands, 4), Num(op.Operands, 5)));
                    break;
                case "v":
                    builder.CurveTo(
                        builder.CurrentPoint,
                        Transform(state.Ctm, Num(op.Operands, 0), Num(op.Operands, 1)),
                        Transform(state.Ctm, Num(op.Operands, 2), Num(op.Operands, 3)));
                    break;
                case "y":
                    {
                        var end = Transform(state.Ctm, Num(op.Operands, 2), Num(op.Operands, 3));
                        builder.CurveTo(Transform(state.Ctm, Num(op.Operands, 0), Num(op.Operands, 1)), end, end);
                        break;
                    }

                case "h":
                    builder.ClosePath();
                    break;
                case "re":
                    {
                        var x = Num(op.Operands, 0);
                        var y = Num(op.Operands, 1);
                        var w = Num(op.Operands, 2);
                        var h = Num(op.Operands, 3);
                        builder.MoveTo(Transform(state.Ctm, x, y));
                        builder.LineTo(Transform(state.Ctm, x + w, y));
                        builder.LineTo(Transform(state.Ctm, x + w, y + h));
                        builder.LineTo(Transform(state.Ctm, x, y + h));
                        builder.ClosePath();
                        break;
                    }

                case "W":
                    builder.PendingClip = FillRule.NonZero;
                    break;
                case "W*":
                    builder.PendingClip = FillRule.EvenOdd;
                    break;

                case "f":
                case "F":
                case "f*":
                case "S":
                case "s":
                case "B":
                case "B*":
                case "b":
                case "b*":
                case "n":
                    PaintPath(op.Operator, builder, state, children, activeBudget, IsContentSuppressed(), resources, objects, options, diagnostics, imageResolver, formDepth, activeRenderFonts, optionalContent, printIntent, initialCtm);
                    builder.Reset();
                    break;

                case "w":
                    state.LineWidth = Num(op.Operands, 0);
                    break;
                case "J":
                    state.LineCap = (LineCap)Math.Clamp((int)Num(op.Operands, 0), 0, 2);
                    break;
                case "j":
                    state.LineJoin = (LineJoin)Math.Clamp((int)Num(op.Operands, 0), 0, 2);
                    break;
                case "M":
                    state.MiterLimit = Num(op.Operands, 0);
                    break;
                case "d":
                    state.DashArray = ReadDashArray(op.Operands);
                    state.DashPhase = op.Operands.Count > 1 ? Num(op.Operands, 1) : 0;
                    break;

                case "g":
                    state.FillColor = new PaintColor("DeviceGray", [Num(op.Operands, 0)]);
                    break;
                case "G":
                    state.StrokeColor = new PaintColor("DeviceGray", [Num(op.Operands, 0)]);
                    break;
                case "rg":
                    state.FillColor = new PaintColor("DeviceRGB", [Num(op.Operands, 0), Num(op.Operands, 1), Num(op.Operands, 2)]);
                    break;
                case "RG":
                    state.StrokeColor = new PaintColor("DeviceRGB", [Num(op.Operands, 0), Num(op.Operands, 1), Num(op.Operands, 2)]);
                    break;
                case "k":
                    state.FillColor = new PaintColor("DeviceCMYK", [Num(op.Operands, 0), Num(op.Operands, 1), Num(op.Operands, 2), Num(op.Operands, 3)]);
                    break;
                case "K":
                    state.StrokeColor = new PaintColor("DeviceCMYK", [Num(op.Operands, 0), Num(op.Operands, 1), Num(op.Operands, 2), Num(op.Operands, 3)]);
                    break;
                case "cs":
                    state.FillColor = SelectColorSpace(NameAt(op.Operands, 0), state.FillColor.Components, resources, objects, options, diagnostics);
                    break;
                case "CS":
                    state.StrokeColor = SelectColorSpace(NameAt(op.Operands, 0), state.StrokeColor.Components, resources, objects, options, diagnostics);
                    break;
                case "sc":
                case "scn":
                    state.FillColor = ReadScn(op.Operands, state.FillColor);
                    break;
                case "SC":
                case "SCN":
                    state.StrokeColor = ReadScn(op.Operands, state.StrokeColor);
                    break;

                case "gs":
                    ApplyExtGState(NameAt(op.Operands, 0), resources, objects, state, diagnostics);
                    break;

                case "BT":
                    textMatrix = PdfMatrix.Identity;
                    textLineMatrix = PdfMatrix.Identity;
                    break;
                case "ET":
                    break;
                case "Tf":
                    state.FontResourceName = NameAt(op.Operands, 0);
                    state.FontSize = Num(op.Operands, 1);
                    break;
                case "Tc":
                    state.CharSpacing = Num(op.Operands, 0);
                    break;
                case "Tw":
                    state.WordSpacing = Num(op.Operands, 0);
                    break;
                case "Tz":
                    state.HorizontalScaling = Num(op.Operands, 0) / 100.0;
                    break;
                case "TL":
                    state.Leading = Num(op.Operands, 0);
                    break;
                case "Ts":
                    state.TextRise = Num(op.Operands, 0);
                    break;
                case "Tr":
                    state.RenderingMode = (TextRenderingMode)Math.Clamp((int)Num(op.Operands, 0), 0, 7);
                    break;
                case "Td":
                    textLineMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, Num(op.Operands, 0), Num(op.Operands, 1)), textLineMatrix);
                    textMatrix = textLineMatrix;
                    break;
                case "TD":
                    state.Leading = -Num(op.Operands, 1);
                    textLineMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, Num(op.Operands, 0), Num(op.Operands, 1)), textLineMatrix);
                    textMatrix = textLineMatrix;
                    break;
                case "Tm":
                    textLineMatrix = ReadMatrix(op.Operands);
                    textMatrix = textLineMatrix;
                    break;
                case "T*":
                    textLineMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, 0, -state.Leading), textLineMatrix);
                    textMatrix = textLineMatrix;
                    break;

                case "Tj":
                    if (op.Operands.Count > 0 && op.Operands[0] is PdfString showString)
                    {
                        ShowText(showString.Bytes.Span);
                    }

                    break;
                case "TJ":
                    if (op.Operands.Count > 0 && op.Operands[0] is PdfArray showArray)
                    {
                        ShowTextArray(showArray);
                    }

                    break;
                case "'":
                    // Move to the next line, then show (§9.4.3): T* followed by Tj.
                    textLineMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, 0, -state.Leading), textLineMatrix);
                    textMatrix = textLineMatrix;
                    if (op.Operands.Count > 0 && op.Operands[0] is PdfString lineString)
                    {
                        ShowText(lineString.Bytes.Span);
                    }

                    break;
                case "\"":
                    // aw ac string " : set word spacing (aw) and char spacing (ac), move to the next line, then show.
                    if (op.Operands.Count >= 3)
                    {
                        state.WordSpacing = Num(op.Operands, 0);
                        state.CharSpacing = Num(op.Operands, 1);
                    }

                    textLineMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, 0, -state.Leading), textLineMatrix);
                    textMatrix = textLineMatrix;
                    if (op.Operands.Count >= 3 && op.Operands[2] is PdfString spacedString)
                    {
                        ShowText(spacedString.Bytes.Span);
                    }

                    break;

                case "Do":
                    if (!IsContentSuppressed())
                    {
                        HandleDo(NameAt(op.Operands, 0), resources, objects, options, diagnostics, state, children, imageResolver, formDepth, activeBudget, activeRenderFonts, optionalContent, printIntent);
                    }

                    break;

                case "BI":
                    if (!IsContentSuppressed())
                    {
                        HandleInlineImage(op, contentBytes, resources, objects, diagnostics, state, children, imageResolver, activeBudget);
                    }

                    break;

                case "sh":
                    if (!IsContentSuppressed())
                    {
                        HandleSh(NameAt(op.Operands, 0), resources, objects, state, children, activeBudget);
                    }

                    break;

                case "BDC":
                    {
                        var suppressed = IsContentSuppressed();
                        if (!suppressed && optionalContent is not null && NameAt(op.Operands, 0) == "OC" && op.Operands.Count > 1 && op.Operands[1] is PdfName tagName)
                        {
                            var ocValue = LookupPropertyRaw(resources, tagName.Value, objects);
                            if (ocValue is not null && !optionalContent.IsVisible(ocValue, objects, printIntent))
                            {
                                suppressed = true;
                                if (optionalContent.TryClaimSuppressionNotice())
                                {
                                    diagnostics?.Add(new PdfDiagnostic("PLUME7733", DiagnosticSeverity.Info, "Optional-content marked content (BDC /OC) resolved to OFF for the current view/print configuration; its content was suppressed."));
                                }
                            }
                        }

                        mcSuppressed.Push(suppressed);
                        break;
                    }

                case "BMC":
                    mcSuppressed.Push(IsContentSuppressed());
                    break;

                case "EMC":
                    if (mcSuppressed.Count > 0)
                    {
                        mcSuppressed.Pop();
                    }

                    break;

                default:
                    break;
            }
        }

        return new FormPageObject
        {
            Ctm = initialCtm,
            Clip = null,
            Children = children,
        };

        // Emits one TextPageObject for a Tj/'/"-shown string (or one TJ array element): resolves the
        // active /Font to a RenderFont, decodes each code to a positioned glyph outline, and
        // advances the (captured) text matrix so later text/operators stay positioned. A local
        // function so it can share BuildDisplayList's text-matrix state directly.
        void ShowText(ReadOnlySpan<byte> str)
        {
            if (activeRenderFonts is null || state.FontResourceName is null)
            {
                return;
            }

            if (LookupResource(resources, "Font", state.FontResourceName, objects) is not PdfDictionary fontDict)
            {
                return;
            }

            var renderFont = activeRenderFonts.GetOrBuild(fontDict);
            if (renderFont is null)
            {
                return;
            }

            var mode = state.RenderingMode;
            var paintsInk = mode is not (TextRenderingMode.Invisible or TextRenderingMode.Clip);

            // Stroke/clip text render modes are painted best-effort (stroke outlines approximated as
            // a fill; clip-by-text not applied) — recorded once per page rather than silently
            // mis-rendered (a documented Phase 9 follow-up).
            if (mode is not TextRenderingMode.Fill and not TextRenderingMode.Invisible && activeRenderFonts.TryClaimRenderModeNotice())
            {
                diagnostics?.Add(new PdfDiagnostic("PLUME7730", DiagnosticSeverity.Warning, $"Text rendering mode {(int)mode} (a stroke/clip Tr mode) is painted best-effort this phase — glyph stroking is approximated as a fill and clip-by-text is not applied; fills still render."));
            }

            var upem = renderFont.UnitsPerEm;
            var th = state.HorizontalScaling;
            var param = new PdfMatrix(state.FontSize * th, 0, 0, state.FontSize, 0, state.TextRise);
            var glyphScale = new PdfMatrix(1.0 / upem, 0, 0, 1.0 / upem, 0, 0);
            // Suppressed optional-content still advances the text matrix below (subsequent
            // unsuppressed text must stay correctly positioned) but never accumulates glyphs.
            var glyphs = paintsInk && !IsContentSuppressed() ? new List<GlyphPlacement>() : null;

            var remaining = str;
            while (remaining.Length > 0)
            {
                renderFont.DecodeNext(remaining, out var codeLength, out var code, out var unicode, out var width);
                if (codeLength <= 0)
                {
                    break;
                }

                if (glyphs is not null && renderFont.TryGetGlyphOutline(code, unicode, out var outline))
                {
                    // Trm = [fontSize*Th 0 0 fontSize 0 Ts] x Tm x CTM (§9.4.4); fold in the design
                    // -> text-space 1/unitsPerEm scale so the outline's raw design-unit coordinates
                    // map straight to device space with no further text-state math at paint time.
                    var trm = PdfMatrix.Multiply(PdfMatrix.Multiply(param, textMatrix), state.Ctm);
                    var glyphMatrix = PdfMatrix.Multiply(glyphScale, trm);
                    if (glyphMatrix.IsFinite)
                    {
                        glyphs.Add(new GlyphPlacement(outline, glyphMatrix));
                    }
                }

                // Advance (§9.4.4): tx = ((w0 - 0)*Tfs + Tc + Tw) * Th, w0 in text space (width/1000);
                // word spacing Tw applies only to a single-byte code 32.
                var w0 = width / 1000.0;
                var wordSpacing = codeLength == 1 && code == 32 ? state.WordSpacing : 0.0;
                var tx = ((w0 * state.FontSize) + state.CharSpacing + wordSpacing) * th;
                textMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, tx, 0), textMatrix);

                if (codeLength >= remaining.Length)
                {
                    break;
                }

                remaining = remaining[codeLength..];
            }

            if (glyphs is { Count: > 0 })
            {
                AddChild(children, activeBudget, new TextPageObject
                {
                    Ctm = PdfMatrix.Identity,
                    Clip = state.Clip,
                    FillAlpha = state.FillAlpha,
                    StrokeAlpha = state.StrokeAlpha,
                    BlendMode = state.BlendMode,
                    FontResourceName = state.FontResourceName,
                    Glyphs = glyphs,
                    RenderingMode = mode,
                    FillColor = state.FillColor,
                    StrokeColor = state.StrokeColor,
                    LineWidth = ScaleLineWidth(state.LineWidth, state.Ctm),
                });
            }
        }

        void ShowTextArray(PdfArray array)
        {
            foreach (var element in array)
            {
                switch (element)
                {
                    case PdfString elementString:
                        ShowText(elementString.Bytes.Span);
                        break;
                    case PdfNumber adjustment:
                        // A TJ number displaces the next glyph: subtract adjustment/1000 text-space
                        // units, scaled by font size and horizontal scaling (§9.4.3).
                        var tx = -(adjustment.Value / 1000.0) * state.FontSize * state.HorizontalScaling;
                        textMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, tx, 0), textMatrix);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Pass 2: paints a display-list tree (from <see cref="BuildDisplayList"/>) onto
    /// <paramref name="surface"/>.
    /// </summary>
    /// <param name="surface">The target surface, already sized and background-filled.</param>
    /// <param name="root">The display-list tree to paint.</param>
    /// <param name="context">The per-call paint intent (resampling mode, anti-aliasing) threaded to every painter — <see cref="RasterPaintContext"/>.</param>
    /// <param name="options">Threaded to shading color resolution (<see cref="PdfOptions.MaxShadingSamples"/>); <see langword="null"/> degrades every <c>sh</c>-painted shading to the flat mid-gray placeholder.</param>
    /// <param name="objects">Resolves indirect references a shading's <c>/ColorSpace</c>/<c>/Function</c> entries make, or <see langword="null"/> when none exist.</param>
    /// <param name="diagnostics">Recoverable deviations encountered while resolving a shading's color are appended here.</param>
    public static void Paint(RasterSurface surface, PageObject root, RasterPaintContext context, PdfOptions? options = null, ObjectRegistry? objects = null, DiagnosticCollection? diagnostics = null)
    {
        PaintObject(surface, root, context, options, objects, diagnostics);
    }

    private static void PaintObject(RasterSurface surface, PageObject obj, RasterPaintContext context, PdfOptions? options, ObjectRegistry? objects, DiagnosticCollection? diagnostics)
    {
        // The clip resolves to a window plus (only when the chain's true region is
        // not its bounding box) a per-pixel coverage map — see ClipRegionResolver. A chain of
        // axis-aligned rectangles keeps the exact pre-coverage bbox behavior, map-free.
        var clip = ClipRegionResolver.Resolve(obj.Clip, surface.Width, surface.Height);
        if (clip.IsEmpty && obj is not FormPageObject)
        {
            return; // Nothing can paint inside an empty clip region (e.g. even-odd coincident rects).
        }

        switch (obj)
        {
            case FormPageObject form:
                if (form.IsTransparencyGroup)
                {
                    PaintTransparencyGroup(surface, form, context, options, objects, diagnostics);
                }
                else
                {
                    foreach (var child in form.Children)
                    {
                        PaintObject(surface, child, context, options, objects, diagnostics);
                    }
                }

                break;

            case PathPageObject path:
                PaintPathObject(surface, path, clip, context, options, objects, diagnostics);
                break;

            case ImagePageObject image:
                ImagePainter.Paint(surface, image, clip, context);
                break;

            case ShadingPageObject shading:
                ShadingFactory.Build(shading, options, objects, diagnostics, shading.ShadingStream)
                    .Paint(surface, clip);
                break;

            case TextPageObject text:
                PaintTextObject(surface, text, clip, context);
                break;
        }
    }

    private static void PaintTextObject(RasterSurface surface, TextPageObject text, ClipWindow clip, RasterPaintContext context)
    {
        var mode = text.RenderingMode;
        if (mode is TextRenderingMode.Invisible or TextRenderingMode.Clip)
        {
            return; // No ink (build already emitted no glyphs for these modes).
        }

        // Stroke-only modes are approximated by filling with the stroke color (a diagnostic was
        // recorded at build time, PLUME7730); every fill mode uses the fill color.
        var color = mode is TextRenderingMode.Stroke or TextRenderingMode.StrokeClip ? text.StrokeColor : text.FillColor;
        var (b, g, r) = ToDeviceRgb(color);
        var alpha = (byte)Math.Clamp((int)Math.Round(text.FillAlpha * 255), 0, 255);

        // One reusable outline rasterizer across every glyph on this run — the allocation-per-glyph
        // that GlyphRasterizer's reuse parameter exists to avoid. Glyphs honor the active clip
        // like every other paint (they previously ignored it entirely): the
        // sweep restricts to the clip window and, for non-rectangular regions, blends through
        // the coverage map.
        var reusable = new OutlineRasterizer();
        var sink = MakeSpanSink(surface, clip, b, g, r, alpha);
        foreach (var glyph in text.Glyphs)
        {
            GlyphRasterizer.Paint(glyph.Outline, glyph.Ctm, surface, clip.MinX, clip.MinY, clip.MaxX, clip.MaxY, sink, context.AntiAlias, reusable);
        }
    }

    /// <summary>
    /// Paints a transparency group (§11.4.5-.7): renders <paramref name="group"/>'s
    /// children into an offscreen buffer the same size as <paramref name="surface"/> (avoiding
    /// any origin-offset bookkeeping — each child's own <see cref="PageObject.Ctm"/>/<see cref="PageObject.Clip"/>
    /// already addresses full-page device space, exactly as it would painting straight onto
    /// <paramref name="surface"/>; a bbox-cropped buffer is a documented 1.x memory optimization,
    /// not a correctness requirement), then composites that buffer back using
    /// <see cref="TransparencyGroup"/>'s isolated/non-isolated/knockout math and, when
    /// <see cref="FormPageObject.SoftMask"/> is active, a per-pixel coverage map from
    /// <see cref="Transparency.SoftMask.ComputeCoverage(ReadOnlySpan{byte}, int, int, int, int, int)"/>.
    /// </summary>
    private static void PaintTransparencyGroup(RasterSurface surface, FormPageObject group, RasterPaintContext context, PdfOptions? options, ObjectRegistry? objects, DiagnosticCollection? diagnostics)
    {
        var maxBytes = options?.MaxRasterSurfaceBytes ?? RasterSurface.DefaultMaxSurfaceBytes;

        // The group's compositing math only ever changes destination pixels the
        // group's children can actually paint, so every sweep and every intermediate byte[] is
        // scoped to the children's (conservative, clip-intersected) device bounds — the bbox
        // optimization an earlier comment here once earmarked for 1.x. Children still paint into
        // FULL-page (pooled) surfaces, so their Ctm/Clip keep addressing device space with no
        // origin bookkeeping; only initialization, readback, and compositing are windowed.
        // Pixels outside the window are simply never touched — bit-identical by construction.
        var (cropX, cropY, cropMaxX, cropMaxY) = GroupDeviceBounds(group, surface.Width, surface.Height);
        var cropW = cropMaxX - cropX;
        var cropH = cropMaxY - cropY;
        if (cropW <= 0 || cropH <= 0)
        {
            return; // Nothing the group paints lands on the surface.
        }

        var cropPixels = cropW * cropH;

        // Isolated (§11.4.5): the group starts from a fully transparent buffer, so its own
        // content never blends against anything beneath it. Non-isolated (§11.4.7): it starts
        // from a copy of whatever is already on the destination, so backdrop removal
        // (Backdrop.RemoveBackdrop, folded into CompositeNonIsolated below) can subtract that
        // starting point back out before the final "over" composite, or it would count twice.
        byte[]? capturedBackdrop = group.IsIsolated
            ? null
            : Backdrop.Capture(surface.Pixels, surface.Width, surface.Height, surface.Stride, cropX, cropY, cropW, cropH);

        byte[] groupResult;
        if (group.IsKnockout)
        {
            // Each element composites against the SAME initial backdrop (never the running
            // result) and simply replaces whatever an earlier sibling already left at a pixel it
            // also paints — the defining trait that keeps overlapping semi-transparent siblings
            // from accumulating opacity (KnockoutBackdropTests.KnockoutLayer_*).
            var initialBackdrop = capturedBackdrop ?? new byte[cropPixels * 4];
            groupResult = (byte[])initialBackdrop.Clone();

            // One pooled element surface reset per child, replacing a fresh full-page
            // allocation per child: each knockout element still renders alone onto a fully
            // transparent window (KnockoutLayer's contract: "alpha 0 = painted nothing here"),
            // regardless of the group's own isolated/non-isolated flag.
            var elementSurface = RasterSurface.Rent(surface.Width, surface.Height, maxBytes);
            try
            {
                foreach (var child in group.Children)
                {
                    elementSurface.ClearRegion(cropX, cropY, cropW, cropH, 0, 0, 0, 0);
                    PaintObject(elementSurface, child, context, options, objects, diagnostics);
                    var elementStraight = UnpremultiplyRegionToStraightAlpha(elementSurface, cropX, cropY, cropW, cropH);
                    TransparencyGroup.KnockoutLayer(groupResult, initialBackdrop, elementStraight, BlendModes.Parse(child.BlendMode), cropPixels);
                }
            }
            finally
            {
                elementSurface.ReturnToPool();
            }
        }
        else
        {
            var groupSurface = RasterSurface.Rent(surface.Width, surface.Height, maxBytes);
            try
            {
                if (capturedBackdrop is not null)
                {
                    groupSurface.LoadRegion(capturedBackdrop, cropX, cropY, cropW, cropH);
                }
                else
                {
                    groupSurface.ClearRegion(cropX, cropY, cropW, cropH, 0, 0, 0, 0);
                }

                foreach (var child in group.Children)
                {
                    PaintObject(groupSurface, child, context, options, objects, diagnostics);
                }

                groupResult = UnpremultiplyRegionToStraightAlpha(groupSurface, cropX, cropY, cropW, cropH);
            }
            finally
            {
                groupSurface.ReturnToPool();
            }
        }

        byte[]? softMaskCoverage = group.SoftMask is { } softMask
            ? RenderGroupSoftMaskCoverage(surface.Width, surface.Height, cropX, cropY, cropW, cropH, softMask, context, options, objects, diagnostics, maxBytes)
            : null;

        var blendMode = BlendModes.Parse(group.BlendMode);
        var destination = surface.MutablePixels;
        if (capturedBackdrop is null)
        {
            TransparencyGroup.Composite(destination, surface.Width, surface.Height, surface.Stride, groupResult, cropW, cropH, cropX, cropY, group.FillAlpha, blendMode, softMaskCoverage);
        }
        else
        {
            TransparencyGroup.CompositeNonIsolated(destination, surface.Width, surface.Height, surface.Stride, groupResult, capturedBackdrop, cropW, cropH, cropX, cropY, group.FillAlpha, blendMode, softMaskCoverage);
        }
    }

    /// <summary>
    /// A conservative device-space bounding box over everything <paramref name="group"/>'s
    /// children can paint, clamped to the surface (exclusive max edges; empty when nothing
    /// lands on it). Conservative means it may only ever be TOO BIG: paths take their point
    /// extents (plus stroke width × miter headroom when stroked — a miter spike reaches at most
    /// <c>LineWidth × MiterLimit / 2</c> beyond a vertex), images their transformed unit-square
    /// corners, glyphs their design-space command-point extents mapped through each placement
    /// matrix (a Bézier lies inside its control hull, so control points bound the curve), and
    /// nested forms recurse; a shading — or any shape whose extent this cannot see — falls back
    /// to the full surface. Each child's box is intersected with its own cached clip bounds.
    /// One pixel of margin absorbs the antialiasing footprint of edge coverage.
    /// </summary>
    private static (int MinX, int MinY, int MaxX, int MaxY) GroupDeviceBounds(FormPageObject group, int surfaceWidth, int surfaceHeight)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        foreach (var child in group.Children)
        {
            var (cMinX, cMinY, cMaxX, cMaxY) = ChildDeviceBounds(child, surfaceWidth, surfaceHeight);
            minX = Math.Min(minX, cMinX);
            minY = Math.Min(minY, cMinY);
            maxX = Math.Max(maxX, cMaxX);
            maxY = Math.Max(maxY, cMaxY);
        }

        return (Math.Max(0, minX), Math.Max(0, minY), Math.Min(surfaceWidth, maxX), Math.Min(surfaceHeight, maxY));
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) ChildDeviceBounds(PageObject child, int surfaceWidth, int surfaceHeight)
    {
        double fMinX = double.PositiveInfinity, fMinY = double.PositiveInfinity;
        double fMaxX = double.NegativeInfinity, fMaxY = double.NegativeInfinity;
        var known = false;

        void Grow(double x, double y)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return;
            }

            known = true;
            fMinX = Math.Min(fMinX, x);
            fMinY = Math.Min(fMinY, y);
            fMaxX = Math.Max(fMaxX, x);
            fMaxY = Math.Max(fMaxY, y);
        }

        switch (child)
        {
            case PathPageObject path:
                foreach (var sub in path.Subpaths)
                {
                    foreach (var (x, y) in sub.Points)
                    {
                        Grow(x, y);
                    }
                }

                if (known && path.Stroke)
                {
                    var expand = path.LineWidth * Math.Max(1.0, path.MiterLimit);
                    fMinX -= expand;
                    fMinY -= expand;
                    fMaxX += expand;
                    fMaxY += expand;
                }

                break;

            case ImagePageObject image:
                Grow(image.Ctm.Transform(0, 0).X, image.Ctm.Transform(0, 0).Y);
                Grow(image.Ctm.Transform(1, 0).X, image.Ctm.Transform(1, 0).Y);
                Grow(image.Ctm.Transform(0, 1).X, image.Ctm.Transform(0, 1).Y);
                Grow(image.Ctm.Transform(1, 1).X, image.Ctm.Transform(1, 1).Y);
                break;

            case TextPageObject text:
                foreach (var glyph in text.Glyphs)
                {
                    float gMinX = float.PositiveInfinity, gMinY = float.PositiveInfinity;
                    float gMaxX = float.NegativeInfinity, gMaxY = float.NegativeInfinity;
                    foreach (var cmd in glyph.Outline.Commands)
                    {
                        gMinX = Math.Min(gMinX, Math.Min(cmd.X, Math.Min(cmd.X1, cmd.X2)));
                        gMinY = Math.Min(gMinY, Math.Min(cmd.Y, Math.Min(cmd.Y1, cmd.Y2)));
                        gMaxX = Math.Max(gMaxX, Math.Max(cmd.X, Math.Max(cmd.X1, cmd.X2)));
                        gMaxY = Math.Max(gMaxY, Math.Max(cmd.Y, Math.Max(cmd.Y1, cmd.Y2)));
                    }

                    if (float.IsInfinity(gMinX))
                    {
                        continue;
                    }

                    Grow(glyph.Ctm.Transform(gMinX, gMinY).X, glyph.Ctm.Transform(gMinX, gMinY).Y);
                    Grow(glyph.Ctm.Transform(gMaxX, gMinY).X, glyph.Ctm.Transform(gMaxX, gMinY).Y);
                    Grow(glyph.Ctm.Transform(gMinX, gMaxY).X, glyph.Ctm.Transform(gMinX, gMaxY).Y);
                    Grow(glyph.Ctm.Transform(gMaxX, gMaxY).X, glyph.Ctm.Transform(gMaxX, gMaxY).Y);
                }

                break;

            case FormPageObject nested:
                {
                    var (nMinX, nMinY, nMaxX, nMaxY) = GroupDeviceBounds(nested, surfaceWidth, surfaceHeight);
                    return ClipToChild(child, nMinX, nMinY, nMaxX, nMaxY, surfaceWidth, surfaceHeight);
                }

            default:
                // Shadings (which fill their whole clip region) and any future object kind whose
                // extent this walk cannot see: the full surface, narrowed only by the clip below.
                return ClipToChild(child, 0, 0, surfaceWidth, surfaceHeight, surfaceWidth, surfaceHeight);
        }

        if (!known)
        {
            return (0, 0, 0, 0); // Nothing painted — an empty box that unions away.
        }

        // One pixel of margin for antialiased edge coverage, then floor/ceiling to pixels.
        var minX = (int)Math.Floor(fMinX) - 1;
        var minY = (int)Math.Floor(fMinY) - 1;
        var maxX = (int)Math.Ceiling(fMaxX) + 1;
        var maxY = (int)Math.Ceiling(fMaxY) + 1;
        return ClipToChild(child, minX, minY, maxX, maxY, surfaceWidth, surfaceHeight);
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) ClipToChild(PageObject child, int minX, int minY, int maxX, int maxY, int surfaceWidth, int surfaceHeight)
    {
        var (clipMinX, clipMinY, clipMaxX, clipMaxY) = ClipBounds(child.Clip, surfaceWidth, surfaceHeight);
        return (Math.Max(minX, clipMinX), Math.Max(minY, clipMinY), Math.Min(maxX, clipMaxX), Math.Min(maxY, clipMaxY));
    }

    /// <summary>The sub-rect form of straight-alpha readback: unpremultiplies only the group's device window into a compact <paramref name="w"/>×<paramref name="h"/> buffer — per-pixel math identical to the old full-surface pass.</summary>
    private static byte[] UnpremultiplyRegionToStraightAlpha(RasterSurface source, int x0, int y0, int w, int h)
    {
        var straight = new byte[w * h * 4];
        var pixels = source.Pixels;
        var stride = source.Stride;
        for (var y = 0; y < h; y++)
        {
            var srcRow = pixels.Slice(((y0 + y) * stride) + (x0 * 4), w * 4);
            var outRow = straight.AsSpan(y * w * 4, w * 4);
            for (var o = 0; o < srcRow.Length; o += 4)
            {
                var a = srcRow[o + 3];
                if (a == 0)
                {
                    continue; // Fully transparent: color is undefined either way; leave at zero.
                }

                if (a == 255)
                {
                    srcRow.Slice(o, 4).CopyTo(outRow.Slice(o, 4));
                    continue;
                }

                outRow[o] = UnpremultiplyChannel(srcRow[o], a);
                outRow[o + 1] = UnpremultiplyChannel(srcRow[o + 1], a);
                outRow[o + 2] = UnpremultiplyChannel(srcRow[o + 2], a);
                outRow[o + 3] = a;
            }
        }

        return straight;
    }

    /// <summary>
    /// Renders a group soft mask's own <c>/G</c> form content (already built at pass-1 time,
    /// <see cref="GroupSoftMask.Content"/>) into a full-surface buffer — a fully opaque
    /// <c>/BC</c> (or default black) backdrop for a <see cref="SoftMaskSubtype.Luminosity"/> mask
    /// (§11.6.5.2 requires compositing over an opaque backdrop before reading luminosity), or a
    /// transparent buffer for <see cref="SoftMaskSubtype.Alpha"/> (which reads the buffer's own
    /// alpha channel directly and ignores backdrop entirely) — then reduces it to a per-pixel
    /// coverage map.
    /// </summary>
    private static byte[] RenderGroupSoftMaskCoverage(int width, int height, int cropX, int cropY, int cropW, int cropH, GroupSoftMask softMask, RasterPaintContext context, PdfOptions? options, ObjectRegistry? objects, DiagnosticCollection? diagnostics, long maxBytes)
    {
        var maskSurface = RasterSurface.Rent(width, height, maxBytes);
        try
        {
            // Only the group's device window is ever read back (the coverage map is indexed by
            // the cropped composite), so only that region needs a defined starting state — the
            // mask content still paints in full-page coordinates.
            if (softMask.Mask.Subtype == SoftMaskSubtype.Luminosity)
            {
                var (b, g, r) = ResolveBackdropColor(softMask.Mask.BackdropColor);
                maskSurface.ClearRegion(cropX, cropY, cropW, cropH, b, g, r, 255);
            }
            else
            {
                maskSurface.ClearRegion(cropX, cropY, cropW, cropH, 0, 0, 0, 0);
            }

            PaintObject(maskSurface, softMask.Content, context, options, objects, diagnostics);
            return softMask.Mask.ComputeCoverage(maskSurface.Pixels, width, cropX, cropY, cropW, cropH);
        }
        finally
        {
            maskSurface.ReturnToPool();
        }
    }

    private static byte UnpremultiplyChannel(byte premultipliedChannel, byte alpha) =>
        (byte)Math.Clamp(((premultipliedChannel * 255) + (alpha / 2)) / alpha, 0, 255);

    // Backdrop colors arrive in the mask group's own colorspace (§11.6.5.2's /BC); this
    // only needs a device approximation good enough to compute luminosity from, the same minimal
    // component-count heuristic ToDeviceRgb already uses for everything else this phase doesn't
    // fully resolve through Color/ColorSpace.cs.
    private static (byte B, byte G, byte R) ResolveBackdropColor(double[]? components)
    {
        if (components is null)
        {
            return (0, 0, 0); // Default: fully opaque black (§11.6.5.2).
        }

        return components.Length switch
        {
            1 => GrayToBgr(components[0]),
            3 => (Ch(components[2]), Ch(components[1]), Ch(components[0])),
            4 => CmykToBgr(components[0], components[1], components[2], components[3]),
            _ => (0, 0, 0),
        };
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) ClipBounds(ClipPath? clip, int surfaceWidth, int surfaceHeight) =>
        ClipRegionResolver.Bounds(clip, surfaceWidth, surfaceHeight);

    private static void PaintPathObject(RasterSurface surface, PathPageObject path, ClipWindow clip, RasterPaintContext context, PdfOptions? options, ObjectRegistry? objects, DiagnosticCollection? diagnostics)
    {
        // One rasterizer for both passes — Reset between them; the sweep's
        // callbacks blend whole constant-coverage spans instead of pixel-at-a-time (or, when a
        // non-rectangular clip's coverage map applies, pixel-at-a-time through it).
        OutlineRasterizer? outline = null;

        if (path.Fill && path.FillPattern is { } fillPattern)
        {
            PaintTilingPatternFill(surface, path, fillPattern, clip, context, options, objects, diagnostics);
        }
        else if (path.Fill && path.FillColor is { } fillColor)
        {
            outline = new OutlineRasterizer();
            foreach (var sub in path.Subpaths)
            {
                EmitSubpath(outline, sub.Points, closed: true);
            }

            var (b, g, r) = ToDeviceRgb(fillColor);
            var alpha = (byte)Math.Clamp((int)Math.Round(path.FillAlpha * 255), 0, 255);
            ScanlineRasterizer.Sweep(outline, path.FillRule, clip.MinX, clip.MinY, clip.MaxX, clip.MaxY, context.AntiAlias, MakeSpanSink(surface, clip, b, g, r, alpha));
        }

        if (path.Stroke && path.StrokeColor is { } strokeColor)
        {
            if (outline is null)
            {
                outline = new OutlineRasterizer();
            }
            else
            {
                outline.Reset();
            }

            foreach (var sub in path.Subpaths)
            {
                if (path.DashArray is { Count: > 0 })
                {
                    foreach (var run in StrokeGenerator.ApplyDash(sub.Points, sub.Closed, path.DashArray, path.DashPhase))
                    {
                        StrokeGenerator.GenerateOutline(run, closed: false, path.LineWidth, path.Cap, path.Join, path.MiterLimit, outline);
                    }
                }
                else
                {
                    // No dash: the points feed the stroker directly — the old shape copied every
                    // subpath's point list into a fresh single-run list first.
                    StrokeGenerator.GenerateOutline(sub.Points, sub.Closed, path.LineWidth, path.Cap, path.Join, path.MiterLimit, outline);
                }
            }

            var (b, g, r) = ToDeviceRgb(strokeColor);
            var alpha = (byte)Math.Clamp((int)Math.Round(path.StrokeAlpha * 255), 0, 255);
            ScanlineRasterizer.Sweep(outline, FillRule.NonZero, clip.MinX, clip.MinY, clip.MaxX, clip.MaxY, context.AntiAlias, MakeSpanSink(surface, clip, b, g, r, alpha));
        }
    }

    /// <summary>
    /// The span sink every solid-color sweep uses: the whole-span <see cref="RasterSurface.BlendSpan"/>
    /// fast path when the clip has no coverage map (byte-identical to the pre-coverage code),
    /// or a per-pixel walk multiplying the span coverage by the clip coverage when one applies
    /// (non-rectangular/even-odd clip regions).
    /// </summary>
    private static ScanlineRasterizer.SpanAction MakeSpanSink(RasterSurface surface, ClipWindow clip, byte b, byte g, byte r, byte alpha)
    {
        if (!clip.HasCoverage)
        {
            return (y, x, len, coverage) => surface.BlendSpan(y, x, len, b, g, r, alpha, coverage);
        }

        return (y, x, len, coverage) =>
        {
            for (var i = 0; i < len; i++)
            {
                var combined = clip.CombineCoverage(x + i, y, coverage);
                if (combined > 0)
                {
                    surface.BlendPixel(x + i, y, b, g, r, alpha, combined);
                }
            }
        };
    }

    /// <summary>
    /// Paints a tiling-pattern fill: renders the captured cell display list once
    /// into a pooled cell raster, tiles it across the fill's device window with
    /// <see cref="TilingPattern.CompositeAxisAligned"/> into a pooled full-page scratch
    /// (windowed to the fill's bounds), then sweeps the fill path
    /// through the normal scan converter and composites the scratch through the coverage —
    /// exactly the "caller masks by the actual fill shape" split
    /// <see cref="TilingPattern.CompositeAxisAligned"/>'s own contract describes. A tile-count
    /// or surface-cap refusal degrades this one fill with <c>PLUME7752</c> instead of failing
    /// the page (the lenient per-object posture every other paint path keeps).
    /// </summary>
    private static void PaintTilingPatternFill(RasterSurface surface, PathPageObject path, CapturedTilingPatternFill pattern, ClipWindow clip, RasterPaintContext context, PdfOptions? options, ObjectRegistry? objects, DiagnosticCollection? diagnostics)
    {
        // The fill's device window: the path's own bounding box (one pixel of antialiasing
        // margin), intersected with the caller's clip window.
        var fMinX = double.PositiveInfinity;
        var fMinY = double.PositiveInfinity;
        var fMaxX = double.NegativeInfinity;
        var fMaxY = double.NegativeInfinity;
        foreach (var sub in path.Subpaths)
        {
            foreach (var (x, y) in sub.Points)
            {
                if (!double.IsFinite(x) || !double.IsFinite(y))
                {
                    continue;
                }

                fMinX = Math.Min(fMinX, x);
                fMinY = Math.Min(fMinY, y);
                fMaxX = Math.Max(fMaxX, x);
                fMaxY = Math.Max(fMaxY, y);
            }
        }

        if (double.IsInfinity(fMinX))
        {
            return; // No finite geometry — nothing to fill.
        }

        var x0 = Math.Max(clip.MinX, (int)Math.Floor(fMinX) - 1);
        var y0 = Math.Max(clip.MinY, (int)Math.Floor(fMinY) - 1);
        var x1 = Math.Min(clip.MaxX, (int)Math.Ceiling(fMaxX) + 1);
        var y1 = Math.Min(clip.MaxY, (int)Math.Ceiling(fMaxY) + 1);
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        var maxBytes = options?.MaxRasterSurfaceBytes ?? RasterSurface.DefaultMaxSurfaceBytes;
        try
        {
            // 1) The cell, rendered once: its content display list's coordinates already land
            // in cell-pixel space (the capture composed the matrices), over a transparent
            // ground so unpainted cell area lets the page show through between tiles.
            byte[] cellBgra;
            var cellSurface = RasterSurface.Rent(pattern.CellWidth, pattern.CellHeight, maxBytes);
            try
            {
                cellSurface.ClearRegion(0, 0, pattern.CellWidth, pattern.CellHeight, 0, 0, 0, 0);
                PaintObject(cellSurface, pattern.CellContent, context, options, objects, diagnostics);
                cellBgra = cellSurface.Pixels.ToArray();
            }
            finally
            {
                cellSurface.ReturnToPool();
            }

            // 2) Tile into a pooled scratch, windowed to the fill bounds.
            var scratch = RasterSurface.Rent(surface.Width, surface.Height, maxBytes);
            try
            {
                scratch.ClearRegion(x0, y0, x1 - x0, y1 - y0, 0, 0, 0, 0);
                TilingPattern.CompositeAxisAligned(
                    scratch.MutablePixels, surface.Width, surface.Height, surface.Stride,
                    new TilingCellBuffer(cellBgra, pattern.CellWidth, pattern.CellHeight),
                    pattern.OriginX, pattern.OriginY, pattern.StepXPixels, pattern.StepYPixels,
                    x0, y0, x1, y1);

                // 3) Sweep the fill path; composite the tiled scratch through its coverage.
                // Surface pixels are effectively premultiplied by their alpha (BlendPixel's
                // over-composite onto a transparent ground), so straighten each sample before
                // handing it to BlendPixel, exactly as the transparency-group readback does.
                var outline = new OutlineRasterizer();
                foreach (var sub in path.Subpaths)
                {
                    EmitSubpath(outline, sub.Points, closed: true);
                }

                var alphaScale = Math.Clamp((int)Math.Round(path.FillAlpha * 255), 0, 255);
                ScanlineRasterizer.Sweep(outline, path.FillRule, x0, y0, x1, y1, context.AntiAlias, (y, xStart, length, spanCoverage) =>
                {
                    for (var i = 0; i < length; i++)
                    {
                        var coverage = clip.CombineCoverage(xStart + i, y, spanCoverage);
                        if (coverage == 0)
                        {
                            continue;
                        }

                        var (b, g, r, a) = scratch.GetPixel(xStart + i, y);
                        if (a == 0)
                        {
                            continue;
                        }

                        var straightB = UnpremultiplyChannel(b, a);
                        var straightG = UnpremultiplyChannel(g, a);
                        var straightR = UnpremultiplyChannel(r, a);
                        var effectiveAlpha = (byte)(((a * alphaScale) + 127) / 255);
                        surface.BlendPixel(xStart + i, y, straightB, straightG, straightR, effectiveAlpha, coverage);
                    }
                });
            }
            finally
            {
                scratch.ReturnToPool();
            }
        }
        catch (PlumePdfException ex)
        {
            // A tile-instance or surface-cap refusal (PLUME7725/7727/7500) degrades this one
            // fill, never the page.
            diagnostics?.Add(new PdfDiagnostic("PLUME7752", DiagnosticSeverity.Warning, $"Pattern fill was not painted: {ex.Message}"));
        }
    }

    private static void EmitSubpath(OutlineRasterizer outline, IReadOnlyList<(double X, double Y)> points, bool closed)
    {
        if (points.Count == 0)
        {
            return;
        }

        outline.MoveTo(FixedMath.ToSubpixel(points[0].X), FixedMath.ToSubpixel(points[0].Y));
        for (var i = 1; i < points.Count; i++)
        {
            outline.LineTo(FixedMath.ToSubpixel(points[i].X), FixedMath.ToSubpixel(points[i].Y));
        }

        outline.ClosePath();
    }

    // A minimal device-color resolution for the common device colorspaces — full ICC/Separation/
    // DeviceN/Indexed/Pattern resolution is Color/ColorSpace.cs's job; anything this doesn't
    // recognize falls back to black (lenient-by-default: paint something, not nothing).
    private static (byte B, byte G, byte R) ToDeviceRgb(PaintColor color)
    {
        var c = color.Components;
        return color.ColorSpaceName switch
        {
            "DeviceGray" when c.Count >= 1 => GrayToBgr(c[0]),
            "DeviceRGB" when c.Count >= 3 => (Ch(c[2]), Ch(c[1]), Ch(c[0])),
            "DeviceCMYK" when c.Count >= 4 => CmykToBgr(c[0], c[1], c[2], c[3]),
            _ => (0, 0, 0),
        };
    }

    private static (byte B, byte G, byte R) GrayToBgr(double gray)
    {
        var v = Ch(gray);
        return (v, v, v);
    }

    private static (byte B, byte G, byte R) CmykToBgr(double c, double m, double y, double k)
    {
        var r = Ch(1 - Math.Min(1, c + k));
        var g = Ch(1 - Math.Min(1, m + k));
        var b = Ch(1 - Math.Min(1, y + k));
        return (b, g, r);
    }

    private static byte Ch(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    private static void PaintPath(string op, PathBuilder builder, RasterGraphicsState state, List<PageObject> children, DisplayListBudget budget, bool suppressed, PdfDictionary? resources, ObjectRegistry? objects, PdfOptions options, DiagnosticCollection? diagnostics, ImageResolver? imageResolver, int formDepth, RenderFontFactory? renderFonts, OptionalContentConfig? optionalContent, bool printIntent, PdfMatrix initialCtm)
    {
        var subpaths = builder.Finish();

        // A clip set inside a suppressed optional-content scope still applies to whatever paints
        // after it (clipping is graphics state, not marking) — only the actual painting below is
        // gated on suppression.
        if (builder.PendingClip is { } clipRule)
        {
            state.IntersectClip(subpaths, clipRule);
        }

        if (suppressed)
        {
            return;
        }

        var fill = op is "f" or "F" or "f*" or "B" or "B*" or "b" or "b*";
        var stroke = op is "S" or "s" or "B" or "B*" or "b" or "b*";
        if (!fill && !stroke)
        {
            return;
        }

        // Pattern-colored paints: a fill in the /Pattern colorspace captures its
        // tiling pattern here, where the resources/objects/resolver context exists — the paint
        // pass has none of it (the GroupSoftMask precedent). A pattern that cannot be captured
        // (shading/uncolored/rotated/unparseable) DROPS that paint with PLUME7752 instead of
        // falling through to ToDeviceRgb's black fallback — the silent solid-black fill was
        // exactly the corpus bug. Stroke patterns are not painted this phase, same diagnostic.
        CapturedTilingPatternFill? fillPattern = null;
        if (fill && state.FillColor.PatternName is { } fillPatternName)
        {
            fillPattern = TryCaptureTilingPatternFill(fillPatternName, resources, objects, options, diagnostics, imageResolver, formDepth, budget, renderFonts, optionalContent, printIntent, initialCtm);
            if (fillPattern is null)
            {
                fill = false;
            }
        }

        if (stroke && state.StrokeColor.PatternName is { } strokePatternName)
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME7752", DiagnosticSeverity.Warning, $"Pattern stroke \"{strokePatternName}\" was not painted: pattern-colored strokes are not supported this phase."));
            stroke = false;
        }

        if (!fill && !stroke)
        {
            return;
        }

        var rule = op is "f*" or "B*" or "b*" ? FillRule.EvenOdd : FillRule.NonZero;
        // "b"/"b*" implicitly close every subpath before stroking (§8.5.3.1).
        var closeAllForStroke = op is "b" or "b*";
        var finalSubpaths = closeAllForStroke
            ? subpaths.Select(static s => s with { Closed = true }).ToList()
            : subpaths;

        AddChild(children, budget, new PathPageObject
        {
            Ctm = PdfMatrix.Identity,
            Clip = state.Clip,
            FillAlpha = state.FillAlpha,
            StrokeAlpha = state.StrokeAlpha,
            BlendMode = state.BlendMode,
            Subpaths = finalSubpaths,
            Fill = fill,
            FillRule = rule,
            FillColor = fill ? state.FillColor : null,
            FillPattern = fill ? fillPattern : null,
            Stroke = stroke,
            StrokeColor = stroke ? state.StrokeColor : null,
            LineWidth = ScaleLineWidth(state.LineWidth, state.Ctm),
            Cap = state.LineCap,
            Join = state.LineJoin,
            MiterLimit = state.MiterLimit,
            DashArray = state.DashArray,
            DashPhase = state.DashPhase,
        });
    }

    /// <summary>
    /// Resolves and captures a tiling pattern for a fill operator: parses the
    /// pattern dictionary, composes the §8.7.3.1 pattern matrix with the content stream's
    /// INITIAL CTM (pattern space maps to the parent content stream's default space, not the
    /// CTM at fill time), sizes the cell raster at that composed scale, and builds the cell's
    /// content stream into a display list (recursion depth and the display-list budget thread
    /// through, so a pattern-within-a-pattern is bounded exactly like nested Form XObjects).
    /// Every unsupported shape returns <see langword="null"/> after recording <c>PLUME7752</c> —
    /// the caller then paints NOTHING for that fill, never a fallback color.
    /// </summary>
    private static CapturedTilingPatternFill? TryCaptureTilingPatternFill(string patternName, PdfDictionary? resources, ObjectRegistry? objects, PdfOptions options, DiagnosticCollection? diagnostics, ImageResolver? imageResolver, int formDepth, DisplayListBudget budget, RenderFontFactory? renderFonts, OptionalContentConfig? optionalContent, bool printIntent, PdfMatrix initialCtm)
    {
        CapturedTilingPatternFill? Degrade(string reason)
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME7752", DiagnosticSeverity.Warning, $"Pattern fill \"{patternName}\" was not painted: {reason}."));
            return null;
        }

        var resolved = LookupResource(resources, "Pattern", patternName, objects);
        var stream = resolved as PdfStream;
        var dict = stream?.Dictionary ?? resolved as PdfDictionary;
        if (dict is null)
        {
            return Degrade("the /Pattern resource is missing, or is not a stream or dictionary");
        }

        var patternType = Resolve(dict, "PatternType", objects) is PdfNumber pt && pt.TryToInt32(out var ptValue) ? ptValue : 1;
        if (patternType == 2)
        {
            return Degrade("shading patterns (/PatternType 2) are not painted this phase");
        }

        if (stream is null)
        {
            return Degrade("the tiling pattern has no content stream");
        }

        TilingPatternDefinition definition;
        try
        {
            definition = TilingPatternDefinition.Parse(dict, r => objects is not null ? objects[r] : PdfNull.Instance);
        }
        catch (PlumePdfException ex)
        {
            return Degrade(ex.Message);
        }

        if (definition.PaintType == PatternPaintType.Uncolored)
        {
            return Degrade("uncolored (/PaintType 2) tiling patterns are not painted this phase");
        }

        var patternToDevice = PdfMatrix.Multiply(new PdfMatrix(definition.MatrixA, definition.MatrixB, definition.MatrixC, definition.MatrixD, definition.MatrixE, definition.MatrixF), initialCtm);
        if (patternToDevice.B != 0 || patternToDevice.C != 0 || patternToDevice.A <= 0 || patternToDevice.D == 0)
        {
            return Degrade("the pattern-to-device matrix is rotated, skewed, or mirrored, which the axis-aligned tiler does not handle this phase");
        }

        var bboxWidth = definition.BBoxUrx - definition.BBoxLlx;
        var bboxHeight = definition.BBoxUry - definition.BBoxLly;
        if (!(bboxWidth > 0) || !(bboxHeight > 0))
        {
            return Degrade($"the pattern /BBox is empty ({bboxWidth}x{bboxHeight} in pattern space)");
        }

        var scaleY = Math.Abs(patternToDevice.D);
        var cellWidth = Math.Max(1, (int)Math.Ceiling(bboxWidth * patternToDevice.A));
        var cellHeight = Math.Max(1, (int)Math.Ceiling(bboxHeight * scaleY));
        var maxSurfaceBytes = options.MaxRasterSurfaceBytes;
        if ((long)cellWidth * cellHeight * 4 > maxSurfaceBytes)
        {
            return Degrade($"the pattern cell needs a {cellWidth}x{cellHeight} raster, exceeding the surface byte cap (PdfOptions.MaxRasterSurfaceBytes)");
        }

        var stepXPixels = patternToDevice.A * definition.XStep;
        var stepYPixels = scaleY * definition.YStep;
        if (!(stepXPixels > 0) || !(stepYPixels > 0) || !double.IsFinite(stepXPixels) || !double.IsFinite(stepYPixels))
        {
            return Degrade($"the device-space tile step ({stepXPixels}x{stepYPixels} px) is not positive and finite");
        }

        byte[] cellBytes;
        try
        {
            cellBytes = stream.GetDecodedBytes(options.Filters, options, objects is null ? null : r => objects[r]);
        }
        catch (PlumePdfException ex)
        {
            return Degrade($"the cell content stream failed to decode ({ex.Message})");
        }

        // Cell-local coordinates are device coordinates translated so the BBox's device-space
        // top-left lands at the raster's (0, 0): with an axis-aligned composed matrix that is
        // exactly the composed matrix with its translation replaced (A > 0 fixes the left edge
        // at BBox llx; D's sign decides whether pattern-space up is raster up or down).
        var originX = (patternToDevice.A * definition.BBoxLlx) + patternToDevice.E;
        var originY = Math.Min(patternToDevice.D * definition.BBoxLly, patternToDevice.D * definition.BBoxUry) + patternToDevice.F;
        var cellMatrix = new PdfMatrix(
            patternToDevice.A, 0, 0, patternToDevice.D,
            -(patternToDevice.A * definition.BBoxLlx),
            -Math.Min(patternToDevice.D * definition.BBoxLly, patternToDevice.D * definition.BBoxUry));

        var cellResources = Resolve(dict, "Resources", objects) as PdfDictionary ?? resources;
        var cellContent = BuildDisplayList(cellBytes, cellResources, cellMatrix, options, diagnostics, objects, imageResolver, formDepth + 1, initialClip: null, budget, renderFonts, optionalContent, printIntent);

        return new CapturedTilingPatternFill(definition, cellContent, cellWidth, cellHeight, originX, originY, stepXPixels, stepYPixels);
    }

    // Approximates the CTM's uniform scale factor to convert a user-space line width into
    // device-space pixels (exact only for similarity transforms — a non-uniform scale/shear CTM
    // makes "line width" itself direction-dependent, which this codebase's quad-based stroker
    // does not model; the average of the two axis scales is the conventional approximation every
    // simple renderer in this class uses).
    private static double ScaleLineWidth(double width, PdfMatrix ctm)
    {
        var scaleX = FixedMath.Sqrt((ctm.A * ctm.A) + (ctm.B * ctm.B));
        var scaleY = FixedMath.Sqrt((ctm.C * ctm.C) + (ctm.D * ctm.D));
        var scale = (scaleX + scaleY) / 2.0;
        return double.IsFinite(scale) && scale > 0 ? width * scale : width;
    }

    private static void ApplyExtGState(string? name, PdfDictionary? resources, ObjectRegistry? objects, RasterGraphicsState state, DiagnosticCollection? diagnostics)
    {
        if (name is null)
        {
            return;
        }

        var gs = LookupResource(resources, "ExtGState", name, objects);
        if (gs is not PdfDictionary dict)
        {
            return;
        }

        // Info-tier diagnostics for the print-pipeline entries this rasterizer honors ISO
        // 32000-1's own permission to skip (§8.6.5.6/§10.4) rather than silently ignoring
        // (PLUME7740-7742) — every /gs resolution is checked, matching every other
        // entry this method reads from the same dictionary.
        PrintPipelineDiagnostics.CheckExtGState(dict, diagnostics);

        if (Resolve(dict, "ca", objects) is PdfNumber ca)
        {
            state.FillAlpha = Math.Clamp(ca.Value, 0, 1);
        }

        if (Resolve(dict, "CA", objects) is PdfNumber capValue)
        {
            state.StrokeAlpha = Math.Clamp(capValue.Value, 0, 1);
        }

        if (Resolve(dict, "LW", objects) is PdfNumber lw)
        {
            state.LineWidth = lw.Value;
        }

        if (Resolve(dict, "BM", objects) is PdfName bmName)
        {
            state.BlendMode = bmName.Value;
        }
        else if (Resolve(dict, "BM", objects) is PdfArray bmArray && bmArray.Count > 0 && bmArray[0] is PdfName firstBm)
        {
            state.BlendMode = firstBm.Value;
        }

        // /SMask (§11.6.4.3): /None clears whatever soft mask was active; a dictionary activates
        // one, captured with the CTM in effect right now — the mask's own /G form renders using
        // this CTM, not whatever is active later when a group Do finally consumes it (§11.6.5.2).
        if (dict.TryGetValue(PdfName.Get("SMask"), out var smaskValue))
        {
            var resolvedSmask = Resolve(smaskValue, objects);
            if (resolvedSmask is PdfDictionary smaskDict)
            {
                state.SoftMaskDict = smaskDict;
                state.SoftMaskCtm = state.Ctm;
            }
            else
            {
                state.SoftMaskDict = null;
            }
        }
    }

    private static void HandleDo(string? name, PdfDictionary? resources, ObjectRegistry? objects, PdfOptions options, DiagnosticCollection? diagnostics, RasterGraphicsState state, List<PageObject> children, ImageResolver? imageResolver, int formDepth, DisplayListBudget budget, RenderFontFactory? renderFonts, OptionalContentConfig? optionalContent, bool printIntent)
    {
        if (name is null)
        {
            return;
        }

        if (LookupResource(resources, "XObject", name, objects) is not PdfStream stream)
        {
            // This was once a fully silent drop ("not even a
            // diagnostic" finding); the missing/non-stream resource itself is the deviation,
            // not anything the (possibly absent) image resolver could ever have seen.
            diagnostics?.Add(new PdfDiagnostic("PLUME7747", DiagnosticSeverity.Warning, $"The /XObject resource \"{name}\" named by a Do operator is missing from /Resources /XObject, or is not a stream; nothing painted for this operator."));
            return;
        }

        var subtype = Resolve(stream.Dictionary, "Subtype", objects) as PdfName;
        if (subtype?.Value == "Image")
        {
            if (IsXObjectSuppressedByOc(stream.Dictionary, optionalContent, objects, printIntent, diagnostics))
            {
                return;
            }

            if (imageResolver?.Invoke(ResolveImageDictionaryColorSpaceName(stream.Dictionary, resources, objects), stream) is { } frame)
            {
                var isMask = Resolve(stream.Dictionary, "ImageMask", objects) is PdfBoolean { Value: true };
                var interpolate = ResolveHint(stream.Dictionary, "Interpolate", objects) is PdfBoolean { Value: true };
                AddChild(children, budget, new ImagePageObject
                {
                    Ctm = state.Ctm,
                    Interpolate = interpolate,
                    Clip = state.Clip,
                    FillAlpha = state.FillAlpha,
                    StrokeAlpha = state.StrokeAlpha,
                    BlendMode = state.BlendMode,
                    Frame = frame,
                    IsStencilMask = isMask,
                    StencilColor = state.FillColor,
                });
            }

            // A null resolver result (resolver present but declined to decode, or no resolver
            // at all) is never double-reported here — a present resolver already recorded its
            // own PLUME7744/7746/7753 diagnostic for whatever went wrong (PLUME7745 retired); an absent
            // resolver is an embedder configuration choice, not a deviation worth flagging on
            // every Image Do (unlike the inline-image path below, which has no resolver-less
            // silent-drop precedent to preserve and gets its own diagnostic instead).
            return;
        }

        if (subtype?.Value != "Form")
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME7748", DiagnosticSeverity.Warning, $"The /XObject resource \"{name}\" named by a Do operator has a missing or unrecognized /Subtype (neither /Image nor /Form); nothing painted for this operator."));
            return;
        }

        if (IsXObjectSuppressedByOc(stream.Dictionary, optionalContent, objects, printIntent, diagnostics))
        {
            return;
        }

        var formResources = Resolve(stream.Dictionary, "Resources", objects) as PdfDictionary ?? resources;
        var formCtm = state.Ctm;
        if (Resolve(stream.Dictionary, "Matrix", objects) is PdfArray matrixArray && matrixArray.Count == 6)
        {
            formCtm = PdfMatrix.Multiply(ReadMatrixArray(matrixArray), formCtm);
        }

        byte[] decoded;
        try
        {
            decoded = stream.GetDecodedBytes(options.Filters, options, objects is null ? null : r => objects[r]);
        }
        catch (PlumePdfException)
        {
            return; // An undecodable Form XObject stream contributes nothing rather than aborting the whole page.
        }

        var nested = BuildDisplayList(decoded, formResources, formCtm, options, diagnostics, objects, imageResolver, formDepth + 1, state.Clip, budget, renderFonts, optionalContent, printIntent);

        // A Form XObject is a transparency group (§11.4.5) exactly when its own /Group dictionary
        // says /S /Transparency — captured here (not on the raw nested tree, which knows nothing
        // about this specific invocation's group-ness) because the same Form XObject stream could
        // in principle be invoked without ever being tagged a group by a caller that mis-set
        // /Group, and because the group's compositing alpha/blend mode/soft mask are the ca/CA/BM/
        // SMask active at THIS Do call site, not anything captured mid-recursion.
        var groupDict = Resolve(stream.Dictionary, "Group", objects) as PdfDictionary;
        var isTransparencyGroup = groupDict is not null && Resolve(groupDict, "S", objects) is PdfName { Value: "Transparency" };
        if (!isTransparencyGroup)
        {
            AddChild(children, budget, nested);
            return;
        }

        var isIsolated = Resolve(groupDict!, "I", objects) is PdfBoolean { Value: true };
        var isKnockout = Resolve(groupDict!, "K", objects) is PdfBoolean { Value: true };
        var softMask = BuildGroupSoftMask(state, options, diagnostics, objects, imageResolver, formDepth, budget, renderFonts, optionalContent, printIntent);

        AddChild(children, budget, new FormPageObject
        {
            Ctm = nested.Ctm,
            Clip = state.Clip,
            FillAlpha = state.FillAlpha,
            StrokeAlpha = state.StrokeAlpha,
            BlendMode = state.BlendMode,
            Children = nested.Children,
            IsTransparencyGroup = true,
            IsIsolated = isIsolated,
            IsKnockout = isKnockout,
            SoftMask = softMask,
        });
    }

    /// <summary>
    /// Whether <paramref name="dictionary"/> (an <c>/Image</c> or <c>/Form</c> XObject's own
    /// stream dictionary) is suppressed by its own <c>/OC</c> entry (ISO 32000-1 §7.8.3/§8.11.3.3)
    /// — independent of any <c>BDC /OC ... EMC</c> marked-content scope the <c>Do</c> call site
    /// happens to be wrapped in (that mechanism is handled separately, above, via
    /// <c>IsContentSuppressed</c>/<c>PLUME7733</c>). Closes a silent-drop gap: without this, a
    /// default-OFF-layer image/form still paints where PDFium/poppler hide it, and the resulting
    /// pixel divergence gets misdiagnosed as resampling noise during SSIM calibration rather than
    /// the real <c>/OC</c> gap it is. Records <c>PLUME7750</c> and returns <see langword="true"/>
    /// when suppressed; a <see langword="null"/> <paramref name="optionalContent"/> (no
    /// <c>/OCProperties</c> in the document, or a caller that never built one) never suppresses
    /// anything, matching every other optional-content check in this type.
    /// </summary>
    private static bool IsXObjectSuppressedByOc(PdfDictionary dictionary, OptionalContentConfig? optionalContent, ObjectRegistry? objects, bool printIntent, DiagnosticCollection? diagnostics)
    {
        if (optionalContent is null)
        {
            return false;
        }

        // Raw (not Resolve()'d away) — OptionalContentConfig.IsVisible needs the entry's own
        // PdfReference identity to check /OFF-array membership, the same reason BDC /OC's
        // LookupPropertyRaw above skips the final Resolve.
        var ocValue = dictionary.TryGetValue(PdfName.Get("OC"), out var raw) ? raw : null;
        if (optionalContent.IsVisible(ocValue, objects, printIntent))
        {
            return false;
        }

        diagnostics?.Add(new PdfDiagnostic("PLUME7750", DiagnosticSeverity.Info, "An XObject's own /OC optional-content membership resolved to OFF for the current view/print configuration; it was not painted."));
        return true;
    }

    // ISO 32000-1 Table 93: inline-image (BI ... ID) dictionary key abbreviations, expanded to
    // their canonical XObject-stream-dictionary equivalents so the injected ImageResolver (built
    // against real /Image XObject dictionaries) can be reused unchanged. Filter
    // name abbreviations (Table 95: AHx/A85/LZW/Fl/RL/CCF/DCT) are deliberately left untouched —
    // PdfFilterRegistry.Default already registers those short names directly, so a /Filter
    // value's own entries need no expansion here. static readonly (never mutated after the
    // static constructor runs) per MutableStaticBanTests' "static readonly, or make it
    // instance/local" rule for PlumePdf.Raster lookup tables.
    private static readonly Dictionary<string, string> InlineImageKeyAbbreviations = new()
    {
        ["BPC"] = "BitsPerComponent",
        ["CS"] = "ColorSpace",
        ["D"] = "Decode",
        ["DP"] = "DecodeParms",
        ["F"] = "Filter",
        ["H"] = "Height",
        ["IM"] = "ImageMask",
        ["I"] = "Interpolate",
        ["W"] = "Width",
        ["L"] = "Length",
    };

    // ISO 32000-1 Table 94: inline-image /ColorSpace name abbreviations — distinct from the key
    // table above (its own "I" abbreviation means Interpolate as a dictionary key, but Indexed
    // as a colorspace name; the two tables are never conflated because one applies to keys, the
    // other only to an already-expanded /ColorSpace entry's value).
    private static readonly Dictionary<string, string> InlineImageColorSpaceAbbreviations = new()
    {
        ["G"] = "DeviceGray",
        ["RGB"] = "DeviceRGB",
        ["CMYK"] = "DeviceCMYK",
        ["I"] = "Indexed",
    };

    /// <summary>
    /// Dispatches a <c>BI ... ID ... EI</c> inline image (ISO 32000-1 §8.9.7): the
    /// reader already parsed <paramref name="op"/>'s abbreviated parameter dictionary and
    /// recorded the raw payload span (<see cref="ContentOperation.InlineImageDataSpan"/>) without
    /// materializing it — this expands the dictionary's abbreviations, slices the
    /// payload out of <paramref name="contentBytes"/>, synthesizes a <see cref="PdfStream"/> that
    /// looks exactly like a normal <c>/Image</c> XObject stream, and hands both to the same
    /// injected <paramref name="imageResolver"/> <c>Do</c> already uses — no second decode path.
    /// </summary>
    private static void HandleInlineImage(ContentOperation op, ReadOnlyMemory<byte> contentBytes, PdfDictionary? resources, ObjectRegistry? objects, DiagnosticCollection? diagnostics, RasterGraphicsState state, List<PageObject> children, ImageResolver? imageResolver, DisplayListBudget budget)
    {
        if (op.Operands.Count == 0 || op.Operands[0] is not PdfDictionary rawDict)
        {
            return;
        }

        if (op.InlineImageDataSpan is not { } dataSpan)
        {
            // No locatable ID/EI payload span (a malformed inline image whose terminator could
            // not be found) — the reader already recorded PLUME7012 for this; nothing safe to
            // decode, and re-diagnosing the same deviation a second time here would double-report.
            return;
        }

        var dict = ExpandInlineImageAbbreviations(rawDict, resources, objects);

        if (IsTerminalJpxFilter(dict))
        {
            // ISO 32000-1 §8.9.7 forbids /JPXDecode on an inline image
            // outright - there is no abbreviated Table-95 spelling for it (unlike DCT/CCF/etc),
            // so any occurrence at all is a malformed content stream. HandleInlineImage
            // synthesizes the same PdfStream shape ImageXObjectResolver.BuildResolver's delegate
            // decodes /Image XObjects through (:109), so once JPXDecode is registered by default
            // an illegal inline JPX would otherwise silently decode and paint - refused
            // here, before the resolver ever sees it, rather than parked as a known gap.
            diagnostics?.Add(new PdfDiagnostic("PLUME7746", DiagnosticSeverity.Warning, "An inline image (BI/ID/EI) declares /JPXDecode, which ISO 32000-1 §8.9.7 forbids for inline images; painting nothing for this image."));
            return;
        }

        var payload = contentBytes.Slice(dataSpan.Start, dataSpan.End - dataSpan.Start);
        var syntheticStream = new PdfStream(dict, payload);

        if (imageResolver is null)
        {
            // Unlike Do's Image branch (which stays silent when no resolver is wired, an
            // embedder configuration choice with no prior silent-drop precedent to preserve),
            // an inline image with nothing able to decode it must never be skipped silently
            // — PLUME7744 is the closest minted code (no new code
            // is minted here; the existing list is exhaustive) since "no resolver available" and
            // "the resolver failed" both cash out to the same observable fact: this one image
            // could not be decoded.
            diagnostics?.Add(new PdfDiagnostic("PLUME7744", DiagnosticSeverity.Warning, "An inline image (BI/ID/EI) could not be decoded: no image resolver is configured for this Rasterize call."));
            return;
        }

        if (imageResolver.Invoke(dict, syntheticStream) is not { } frame)
        {
            return; // The resolver already recorded its own PLUME7744/7746/7753 diagnostic (PLUME7745 retired).
        }

        var isMask = Resolve(dict, "ImageMask", objects) is PdfBoolean { Value: true };
        var interpolate = ResolveHint(dict, "Interpolate", objects) is PdfBoolean { Value: true };
        AddChild(children, budget, new ImagePageObject
        {
            Ctm = state.Ctm,
            Interpolate = interpolate,
            Clip = state.Clip,
            FillAlpha = state.FillAlpha,
            StrokeAlpha = state.StrokeAlpha,
            BlendMode = state.BlendMode,
            Frame = frame,
            IsStencilMask = isMask,
            StencilColor = state.FillColor,
        });
    }

    /// <summary>Whether <paramref name="dict"/>'s (already key-expanded) <c>/Filter</c> chain ends in <c>JPXDecode</c> — the one filter ISO 32000-1 §8.9.7 forbids on an inline image outright, so this is checked unconditionally rather than only when a resolver happens to be wired.</summary>
    private static bool IsTerminalJpxFilter(PdfDictionary dict)
    {
        if (!dict.TryGetValue(PdfName.Get("Filter"), out var value))
        {
            return false;
        }

        return value switch
        {
            PdfName name => name.Value == "JPXDecode",
            PdfArray array when array.Count > 0 => array[^1] is PdfName last && last.Value == "JPXDecode",
            _ => false,
        };
    }

    /// <summary>Expands every key in <paramref name="raw"/> per <see cref="InlineImageKeyAbbreviations"/>, then resolves the (now-canonical) <c>/ColorSpace</c> entry per <see cref="ResolveInlineColorSpace"/>.</summary>
    private static PdfDictionary ExpandInlineImageAbbreviations(PdfDictionary raw, PdfDictionary? resources, ObjectRegistry? objects)
    {
        var expanded = new PdfDictionary();
        foreach (var (key, value) in raw)
        {
            var canonicalName = InlineImageKeyAbbreviations.TryGetValue(key.Value, out var full) ? full : key.Value;
            expanded.Set(PdfName.Get(canonicalName), value);
        }

        var colorSpaceKey = PdfName.Get("ColorSpace");
        if (expanded.TryGetValue(colorSpaceKey, out var csValue))
        {
            expanded.Set(colorSpaceKey, ResolveInlineColorSpace(csValue, resources, objects));
        }

        return expanded;
    }

    /// <summary>
    /// Resolves an inline image's (already key-expanded) <c>/ColorSpace</c> value: a direct
    /// abbreviated device name (Table 94: <c>/G</c>/<c>/RGB</c>/<c>/CMYK</c>/<c>/I</c>) expands
    /// to its full name; an <c>/Indexed</c> array's own abbreviated base space (its second
    /// element) gets the same treatment; anything else is a name that must be looked up in the
    /// page's own <c>/Resources /ColorSpace</c> dictionary (ISO 32000-1 §8.9.5.2 — an inline
    /// image can share a Separation/DeviceN/ICCBased/Cal* space already defined for the rest of
    /// the page), falling back to the raw name when nothing resolves (best-effort: the resolver
    /// degrades an unrecognized colorspace to its own decode-failure diagnostic rather than this
    /// method ever throwing).
    /// </summary>
    /// <summary>
    /// An <c>/Image</c> XObject whose <c>/ColorSpace</c> is a bare non-device NAME (e.g.
    /// <c>/CS0</c>) refers to the page's <c>/Resources /ColorSpace</c> dictionary — PDFium and
    /// poppler resolve it there, so real (if sloppy) producers that do this render (review
    /// finding: the resolver refused the bare name via PLUME7715→PLUME7744 while the
    /// inline-image path resolved the same shape). Returns the original dictionary untouched
    /// unless a substitution actually resolves — device names and unknown names pass through so
    /// the resolver's own diagnostics still fire; no inline-image ABBREVIATION expansion runs
    /// here (<c>/G</c> etc. are an inline-image-only vocabulary, Table 94).
    /// </summary>
    private static PdfDictionary ResolveImageDictionaryColorSpaceName(PdfDictionary imageDictionary, PdfDictionary? resources, ObjectRegistry? objects)
    {
        if (!imageDictionary.TryGetValue(PdfName.Get("ColorSpace"), out var csValue) || csValue is not PdfName csName
            || csName.Value is "DeviceRGB" or "DeviceGray" or "DeviceCMYK" or "Pattern"
            || LookupResource(resources, "ColorSpace", csName.Value, objects) is not { } resolved)
        {
            return imageDictionary;
        }

        var substituted = new PdfDictionary();
        foreach (var (key, value) in imageDictionary)
        {
            substituted.Set(key, value);
        }

        substituted.Set(PdfName.Get("ColorSpace"), resolved);
        return substituted;
    }

    private static PdfObject ResolveInlineColorSpace(PdfObject csValue, PdfDictionary? resources, ObjectRegistry? objects)
    {
        switch (csValue)
        {
            case PdfName csName:
                if (InlineImageColorSpaceAbbreviations.TryGetValue(csName.Value, out var fullName))
                {
                    return PdfName.Get(fullName);
                }

                return LookupResource(resources, "ColorSpace", csName.Value, objects) is { } resolved ? resolved : csName;

            case PdfArray csArray when csArray.Count > 0 && csArray[0] is PdfName:
                // §8.9.7 permits mixing spelled-out and abbreviated forms within the same array
                // (e.g. [/Indexed /RGB 3 <...>]) - element 0 and element 1 are expanded
                // independently rather than gating element 1's expansion on element 0 having
                // been abbreviated, so a spelled-out /Indexed with an abbreviated base space
                // still resolves instead of falling through to the raw, unparseable array.
                var items = new List<PdfObject>(csArray);
                if (items[0] is PdfName firstName && InlineImageColorSpaceAbbreviations.TryGetValue(firstName.Value, out var arrayFull))
                {
                    items[0] = PdfName.Get(arrayFull);
                }

                if (items.Count > 1 && items[1] is PdfName baseAbbrev && InlineImageColorSpaceAbbreviations.TryGetValue(baseAbbrev.Value, out var baseFull))
                {
                    items[1] = PdfName.Get(baseFull);
                }

                return new PdfArray(items);

            default:
                return csValue;
        }
    }

    /// <summary>
    /// Builds the ExtGState <c>/SMask</c> active on <paramref name="state"/> (if any) into a
    /// <see cref="GroupSoftMask"/> — parses the mask descriptor and recursively builds its own
    /// <c>/G</c> form's display list right here (pass 1), using the exact same
    /// image-decode/font/optional-content machinery already in scope, so pass 2's group
    /// compositing only has to walk an already-normalized tree. Any parse/resolve failure (a
    /// malformed <c>/SMask</c>, PLUME7728; an unresolvable <c>/G</c>; an undecodable mask stream)
    /// degrades to "no soft mask" rather than aborting the group's own paint — lenient-by-default,
    /// the same posture every other paint-time deviation in this class takes.
    /// </summary>
    private static GroupSoftMask? BuildGroupSoftMask(RasterGraphicsState state, PdfOptions options, DiagnosticCollection? diagnostics, ObjectRegistry? objects, ImageResolver? imageResolver, int formDepth, DisplayListBudget budget, RenderFontFactory? renderFonts, OptionalContentConfig? optionalContent, bool printIntent)
    {
        if (state.SoftMaskDict is not { } smaskDict)
        {
            return null;
        }

        Transparency.SoftMask parsedMask;
        try
        {
            parsedMask = Transparency.SoftMask.Parse(
                smaskDict,
                r => objects is not null ? objects[r] : throw new PlumePdfException("PLUME7504", "An ExtGState /SMask referenced an indirect object, but this build has no ObjectRegistry to resolve it against."),
                options.Filters,
                options,
                diagnostics);
        }
        catch (PlumePdfException)
        {
            return null;
        }

        if (Resolve(smaskDict, "G", objects) is not PdfStream maskFormStream)
        {
            return null; // /SMask with no (or an unresolved) /G form has nothing to render.
        }

        var maskResources = Resolve(maskFormStream.Dictionary, "Resources", objects) as PdfDictionary;
        var maskCtm = state.SoftMaskCtm;
        if (Resolve(maskFormStream.Dictionary, "Matrix", objects) is PdfArray matrixArray && matrixArray.Count == 6)
        {
            maskCtm = PdfMatrix.Multiply(ReadMatrixArray(matrixArray), maskCtm);
        }

        byte[] decoded;
        try
        {
            decoded = maskFormStream.GetDecodedBytes(options.Filters, options, objects is null ? null : r => objects[r]);
        }
        catch (PlumePdfException)
        {
            return null;
        }

        var maskContent = BuildDisplayList(decoded, maskResources, maskCtm, options, diagnostics, objects, imageResolver, formDepth + 1, null, budget, renderFonts, optionalContent, printIntent);
        return new GroupSoftMask { Mask = parsedMask, Content = maskContent };
    }

    private static void HandleSh(string? name, PdfDictionary? resources, ObjectRegistry? objects, RasterGraphicsState state, List<PageObject> children, DisplayListBudget budget)
    {
        if (name is null)
        {
            return;
        }

        // A mesh shading (types 4-7) resource is always a PdfStream (its vertex/patch data is
        // the stream payload, ISO 32000-1 §8.7.4.5.5-.9) — PdfStream is not a PdfDictionary
        // (src/PlumePdf/Objects/PdfStream.cs), so both shapes have to be accepted here or every
        // genuine mesh sh invocation resolves to nothing. An axial/radial/function-based shading
        // (types 1-3) is a plain dictionary with no stream half.
        var resolved = LookupResource(resources, "Shading", name, objects);
        PdfDictionary? shadingDict = null;
        PdfStream? shadingStream = null;
        switch (resolved)
        {
            case PdfStream stream:
                shadingDict = stream.Dictionary;
                shadingStream = stream;
                break;
            case PdfDictionary dict:
                shadingDict = dict;
                break;
        }

        if (shadingDict is null)
        {
            return;
        }

        AddChild(children, budget, new ShadingPageObject
        {
            Ctm = state.Ctm,
            Clip = state.Clip,
            FillAlpha = state.FillAlpha,
            StrokeAlpha = state.StrokeAlpha,
            BlendMode = state.BlendMode,
            Shading = shadingDict,
            ShadingStream = shadingStream,
            Resources = resources,
        });
    }

    private static PdfObject? LookupResource(PdfDictionary? resources, string category, string name, ObjectRegistry? objects)
    {
        if (resources is null)
        {
            return null;
        }

        if (Resolve(resources, category, objects) is not PdfDictionary categoryDict)
        {
            return null;
        }

        return categoryDict.TryGetValue(PdfName.Get(name), out var value) ? Resolve(value, objects) : null;
    }

    // Like LookupResource, but for /Properties (the BDC /OC tag's resource category): returns the
    // raw entry — typically a PdfReference — without a final Resolve, so OptionalContentConfig can
    // recover the OCG/OCMD's own object-number identity for its /OFF-array membership check.
    // Resolving it away here would make every OCG look "direct" and defeat that lookup.
    private static PdfObject? LookupPropertyRaw(PdfDictionary? resources, string name, ObjectRegistry? objects)
    {
        if (resources is null || Resolve(resources, "Properties", objects) is not PdfDictionary properties)
        {
            return null;
        }

        return properties.TryGetValue(PdfName.Get(name), out var value) ? value : null;
    }

    private static PdfObject? Resolve(PdfDictionary dict, string key, ObjectRegistry? objects) =>
        dict.TryGetValue(PdfName.Get(key), out var value) ? Resolve(value, objects) : null;

    private static PdfObject? Resolve(PdfObject? obj, ObjectRegistry? objects) =>
        obj is PdfReference r && objects is not null ? objects[r.Target] : obj;

    /// <summary>
    /// <see cref="Resolve(PdfDictionary, string, ObjectRegistry?)"/> for a HINT entry
    /// (<c>/Interpolate</c>): a value the render never depended on before must not
    /// change the document's diagnostics or its <c>Strict</c> outcome, so a dangling reference
    /// reads as <see langword="null"/> without the indexer's <c>PLUME2060</c>; a live reference
    /// resolves like any other dictionary value (ISO 32000-1 §7.3.10).
    /// </summary>
    private static PdfObject? ResolveHint(PdfDictionary dict, string key, ObjectRegistry? objects)
    {
        if (!dict.TryGetValue(PdfName.Get(key), out var value))
        {
            return null;
        }

        if (value is PdfReference r)
        {
            return objects is not null && objects.IsResolvable(r.Target) ? objects[r.Target] : null;
        }

        return value;
    }

    private static PdfMatrix ReadMatrix(IReadOnlyList<PdfObject> operands) => new(
        Num(operands, 0), Num(operands, 1), Num(operands, 2), Num(operands, 3), Num(operands, 4), Num(operands, 5));

    private static PdfMatrix ReadMatrixArray(PdfArray array) => new(
        NumAt(array, 0), NumAt(array, 1), NumAt(array, 2), NumAt(array, 3), NumAt(array, 4), NumAt(array, 5));

    private static double NumAt(PdfArray array, int index) => index < array.Count && array[index] is PdfNumber n ? n.Value : (index == 0 || index == 3 ? 1 : 0);

    private static (double X, double Y) Transform(PdfMatrix ctm, double x, double y) => ctm.Transform(x, y);

    private static double Num(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber n && double.IsFinite(n.Value) ? n.Value : 0;

    private static string? NameAt(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfName n ? n.Value : null;

    private static IReadOnlyList<double>? ReadDashArray(IReadOnlyList<PdfObject> operands)
    {
        if (operands.Count == 0 || operands[0] is not PdfArray array)
        {
            return null;
        }

        var result = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            result[i] = array[i] is PdfNumber n ? n.Value : 0;
        }

        return result;
    }

    /// <summary>
    /// The <c>cs</c>/<c>CS</c> colorspace selection (§8.6.8): device names and <c>/Pattern</c>
    /// pass through as-is, and a NAMED colorspace resolves through the page's
    /// <c>/Resources /ColorSpace</c> dictionary — either to a device alias (the common
    /// Qt-generator shape, <c>/CSp → /DeviceRGB</c>) or to a parsed
    /// <see cref="RasterColorSpace"/> carried on the color so the next <c>scn</c> can convert
    /// its components for real. The raw resource name used to be kept verbatim,
    /// which <see cref="ToDeviceRgb"/>'s lenient fallback painted as BLACK — turning every
    /// background/rule fill on Qt-emitted pages black and whole pages dark. An unresolvable or
    /// unparseable name still falls back to the old shape (name kept, paint-time black), with
    /// any parse diagnostics recorded by <see cref="ColorSpace.Parse"/> itself.
    /// </summary>
    private static PaintColor SelectColorSpace(string? name, IReadOnlyList<double> currentComponents, PdfDictionary? resources, ObjectRegistry? objects, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        name ??= "DeviceGray";
        if (name is "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern")
        {
            return new PaintColor(name, currentComponents);
        }

        var resolved = LookupResource(resources, "ColorSpace", name, objects);
        if (resolved is PdfName alias && alias.Value is "DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern")
        {
            return new PaintColor(alias.Value, currentComponents);
        }

        if (resolved is not null)
        {
            try
            {
                var space = ColorSpace.Parse(
                    resolved,
                    objects is { } registry
                        ? r => registry[r]
                        : r => throw new PlumePdfException("PLUME7504", "A /ColorSpace resource referenced an indirect object, but this build call has no ObjectRegistry to resolve it against."),
                    options.Filters,
                    options,
                    diagnostics);
                return new PaintColor(name, currentComponents, ResolvedSpace: space);
            }
            catch (PlumePdfException)
            {
                // Unparseable (e.g. /Indexed fill space) — keep the named shape; scn stores raw
                // components and paint time uses the lenient fallback, exactly as before.
            }
        }

        return new PaintColor(name, currentComponents);
    }

    private static PaintColor ReadScn(IReadOnlyList<PdfObject> operands, PaintColor current)
    {
        string? patternName = null;
        var componentCount = operands.Count;
        if (operands.Count > 0 && operands[^1] is PdfName trailingName)
        {
            patternName = trailingName.Value;
            componentCount--;
        }

        var components = new double[componentCount];
        for (var i = 0; i < componentCount; i++)
        {
            components[i] = operands[i] is PdfNumber n ? n.Value : 0;
        }

        if (patternName is not null)
        {
            return new PaintColor("Pattern", components, patternName);
        }

        // A named colorspace resolved at cs-time: convert the components to device
        // RGB through the real color machinery NOW — set-color happens once, paint happens many
        // times, so this is also the cheap place to do it. Conversion failure (component-count
        // mismatch against a hostile stream, a tint transform that throws) degrades to the raw
        // named shape, never aborts the page.
        if (current.ResolvedSpace is { } space)
        {
            try
            {
                Span<double> input = components.Length <= 8 ? stackalloc double[components.Length] : new double[components.Length];
                for (var i = 0; i < components.Length; i++)
                {
                    input[i] = components[i];
                }

                var (r, g, b) = space.ToRgb(input);
                return new PaintColor("DeviceRGB", [r / 255.0, g / 255.0, b / 255.0]);
            }
            catch (PlumePdfException)
            {
                // Fall through to the raw named shape below.
            }
        }

        return new PaintColor(current.ColorSpaceName, components, ResolvedSpace: current.ResolvedSpace);
    }

    /// <summary>Accumulates one content stream's path-construction operators (§8.5.2) into device-space subpaths, curve-flattened as each curve operator arrives.</summary>
    private sealed class PathBuilder
    {
        private readonly List<FlattenedSubpath> _subpaths = [];
        private List<(double X, double Y)>? _current;
        private (double X, double Y) _start;
        private bool _closed;

        public (double X, double Y) CurrentPoint { get; private set; }

        public FillRule? PendingClip { get; set; }

        public void MoveTo((double X, double Y) point)
        {
            Flush();
            _current = [point];
            _start = point;
            CurrentPoint = point;
            _closed = false;
        }

        public void LineTo((double X, double Y) point)
        {
            _current ??= [_start];
            _current.Add(point);
            CurrentPoint = point;
        }

        public void CurveTo((double X, double Y) c1, (double X, double Y) c2, (double X, double Y) end)
        {
            _current ??= [_start];
            CurveFlattener.FlattenCubic(CurrentPoint.X, CurrentPoint.Y, c1.X, c1.Y, c2.X, c2.Y, end.X, end.Y, _current);
            CurrentPoint = end;
        }

        public void ClosePath()
        {
            if (_current is { Count: > 0 })
            {
                _closed = true;
                CurrentPoint = _start;
            }
        }

        public IReadOnlyList<FlattenedSubpath> Finish()
        {
            Flush();

            // A defensive copy: Reset() (called by the caller immediately after painting) clears
            // the live accumulator, and the returned list is captured by reference into the
            // PageObject the caller is about to construct — returning the live list directly
            // would silently empty that object's Subpaths the moment Reset() runs.
            return [.. _subpaths];
        }

        public void Reset()
        {
            _subpaths.Clear();
            _current = null;
            _closed = false;
            PendingClip = null;
        }

        private void Flush()
        {
            if (_current is { Count: > 1 })
            {
                _subpaths.Add(new FlattenedSubpath(_current, _closed));
            }

            _current = null;
            _closed = false;
        }
    }
}
