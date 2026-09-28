using System.Globalization;
using PlumePdf.Content;
using PlumePdf.Documents.PdfA;
using PlumePdf.Documents.Structure;
using PlumePdf.Elements;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using PlumePdf.Fonts.Standard14;
using PlumePdf.Objects;
// Phase 6.5: see TextLayouter.cs's identical alias note — this file's ResolveAlign
// resolves MeasuredText.Direction (the public, Auto-resolved-away `Elements.TextDirection`),
// not the internal shaping-seam one.
using TextDirection = PlumePdf.Elements.TextDirection;

namespace PlumePdf.Layout;

/// <summary>
/// Drives a <see cref="Manuscript"/> all the way to a real <see cref="PdfDocument"/>: paginates
/// every <see cref="Section"/> via <see cref="Paginator"/>, shapes and paints each resulting
/// page's content stream through the real font-encoding seam (<see cref="ILineShaper"/> +
/// <see cref="FontObjectBuilder"/>/<see cref="FontSubsetter"/> for embedded TrueType/OpenType
/// fonts, <see cref="Content.ContentStreamBuilder"/> for the operator stream itself), and hands
/// the finished object graph to <see cref="PdfDocument.CreateSynthetic(IObjectSource,DiagnosticCollection,PdfOptions)"/>
/// — the same route <c>PageImporter</c> uses for <c>Pdf.Merge</c>/<c>Split</c> (decision:
/// "Manuscript.Render follows the DocumentComposer precedent").
/// </summary>
/// <remarks>
/// Rendering runs in three passes over the already-paginated pages, because an embedded font's
/// PDF object (its subsetted <c>/FontFile2</c> and the old-glyph-ID→CID map every <c>Tj</c>
/// string needs) cannot be built until every page that uses it has been walked at least once:
/// <list type="number">
/// <item>Shape every page's text (<see cref="ILineShaper"/> against each <see cref="Text.Font"/>
/// or the resolved Standard-14 default) into an ordered, font-agnostic list of draw
/// instructions, recording which glyphs of which fonts got used along the way. Shaping is
/// where an unencodable codepoint throws <c>PLUME8009</c> — the same fail-fast seam
/// <c>PlumePdf.Fonts.SimpleShaper</c> already implemented, now actually on the render path.</item>
/// <item>Build one PDF font object per distinct <see cref="PdfFont"/> actually used: a plain
/// <c>/Type1</c> dictionary for a Standard-14 font, or a subsetted, embedded
/// <c>/Type0</c>/<c>CIDFontType2</c> pair for a TrueType/OpenType font — subsetted to exactly
/// the glyphs pass 1 collected, never the font's full glyph set.</item>
/// <item>Replay each page's draw instructions into a real <see cref="Content.ContentStreamBuilder"/>
/// and <see cref="ResourceDictionaryBuilder"/>, translating each shaped glyph into the bytes its
/// resolved font's <c>Tj</c> encoding actually needs (a single WinAnsi byte for Standard-14, a
/// 2-byte big-endian CID for an embedded font's Identity-H encoding), and assemble the page via
/// <see cref="PageContentAssembler"/> (Flate-encoded when an encoder is registered).</item>
/// </list>
/// </remarks>
internal static class ManuscriptRenderer
{
    // Phase 6.5/D-4/A-13: kept in step with TextLayouter's own `Shaper` field (its own comment
    // names the swap site) — used directly by watermark/stamp painting below, which are short,
    // direction-agnostic furniture strings with no bidi/wrap concern of their own. This does NOT
    // mean a watermark/stamp containing Arabic/Devanagari renders correctly: DrawWatermark/
    // DrawStamp both hardcode PdfFont.HelveticaBold (a Standard-14, WinAnsi-only font with no
    // GSUB/GPOS tables at all), so ComplexShaper.Shape's per-script dispatch throws PLUME8025 for
    // either script the moment it reaches ArabicShaper/IndicShaper's TrueType-only requirement —
    // watermark/stamp text is a Latin/Cyrillic/Greek-only feature today, not a script-agnostic
    // one; using ComplexShaper here only keeps this call site in step with DrawText's, not a
    // claim of full script support.
    // DrawText's own line painting instead goes through TextLayouter.ShapeVisualLine, which
    // owns the actual shaper call for text lines (so measure and paint always agree — see its
    // own remarks).
    private static readonly ILineShaper Shaper = new ComplexShaper();

    /// <summary>Lays out and renders every section of <paramref name="manuscript"/> into a new synthetic <see cref="PdfDocument"/>.</summary>
    public static PdfDocument Render(Manuscript manuscript, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(manuscript);
        ArgumentNullException.ThrowIfNull(options);

        if (manuscript.Sections.Count == 0)
        {
            throw new PdfLayoutException(
                "PLUME9004",
                "The manuscript has no sections to render.",
                "Manuscript",
                [],
                "A Manuscript must contain at least one Section — Manuscript.Sections was empty.");
        }

        // PDF/A: a PdfOptions.PdfAConformance other than None
        // coerces the version knob up front (A1b forces "1.4"; A2b refuses a header above its
        // 1.7 ceiling, PLUME6071) and fail-fasts the deterministic-dates precondition
        // (PLUME6058) before any pagination work runs. The coerced options are what the
        // synthetic document is created under, so a later plain document.Save(path) inherits
        // the right header version without the caller re-supplying anything.
        var conformance = options.PdfAConformance;
        if (conformance != PdfAConformance.None)
        {
            options = PdfACreationSupport.ApplyVersionKnob(options);
            PdfACreationSupport.EnsureDeterministicDatesSupplied(manuscript.CreateDate, manuscript.ModifyDate, options);
        }

        // PDF/UA: an explicit opt-in, enforced before any pagination work runs.
        // Alternate text (PLUME9010) is enforced per-element during rendering for any tagged
        // output; the two document-level semantics PDF/UA additionally requires — a language
        // and a displayed title — refuse here, naming the Manuscript property to set. Heading
        // levels stay authorial (no range/nesting validation): correct heading structure is an
        // authoring-quality judgment no library can make on the caller's behalf.
        if (manuscript.PdfUa)
        {
            if (manuscript.Language is not { Length: > 0 })
            {
                throw new PlumePdfException(
                    "PLUME9011",
                    "Manuscript.PdfUa requests PDF/UA-1 output, but Manuscript.Language is not set — PDF/UA requires the document's natural language (ISO 14289-1; the catalog's /Lang). Set Language to a BCP 47 tag (e.g. \"en-US\"), a semantic no library can infer on the caller's behalf.");
            }

            if (manuscript.Title is not { Length: > 0 })
            {
                throw new PlumePdfException(
                    "PLUME9012",
                    "Manuscript.PdfUa requests PDF/UA-1 output, but Manuscript.Title is not set — PDF/UA requires a document title displayed in place of the file name (ISO 14289-1; /Info /Title plus /ViewerPreferences /DisplayDocTitle). Set Title, a semantic no library can infer on the caller's behalf.");
            }
        }

        var document = new DocumentBuilder(options);

        // Tagging is opt-in, gated on Manuscript.Language (which Manuscript.PdfUa
        // requires above): setting it is what asks for a real /StructTreeRoot, per-element role
        // tagging, and the required-semantics refusals (PLUME9010) — everything below stays a
        // strict no-op, byte-for-byte identical to pre-tagging output, for every manuscript
        // that leaves Language unset.
        if (manuscript.Language is not null)
        {
            document.BeginTagging();
        }

        var pagePlans = new List<PagePlan>();

        // Pass 1: shape every page's text and record font usage (throws PLUME8009 for any
        // codepoint the resolved font cannot encode).
        foreach (var section in manuscript.Sections)
        {
            var paginated = Paginator.Paginate(section, options);
            var totalPages = paginated.Pages.Count;

            for (var pageIndex = 0; pageIndex < totalPages; pageIndex++)
            {
                var globalPageIndex = pagePlans.Count; // this page's index in the final, flattened pageRefs list built below
                var ops = new List<PageOp>();

                // PDF/A: the initial graphics state's fill/stroke colour space is
                // DeviceGray (ISO 32000-1 §8.4.1), so text painted without an explicit colour
                // operator "uses" DeviceGray — which the sRGB (RGB) output intent does not
                // cover under PDF/A's device-independence rules (veraPDF clause 6.2.4.3,
                // observed empirically from its CLI report, per the clean-room policy in AGENTS.md).
                // Pinning both colours to DeviceRGB black up front keeps every default-coloured
                // operator inside the output intent's colour space; a manuscript that sets its
                // own colours later simply overrides these two operators as usual.
                if (conformance != PdfAConformance.None)
                {
                    ops.Add(new SetFillRgbOp(0, 0, 0));
                    ops.Add(new SetStrokeRgbOp(0, 0, 0));
                }

                RenderPageContent(document, paginated, pageIndex, pageIndex + 1, totalPages, globalPageIndex, ops);
                pagePlans.Add(new PagePlan(section.PageSize, ops));
            }
        }

        // PDF/A × Standard-14: pass 1 has now seen every font any page uses,
        // so this is the earliest point the refusal can name EVERY offender rather than just
        // the first — a Standard-14 font embeds nothing by design, and PDF/A requires every
        // font embedded. Coded refusal, never silent substitution (PLUME8023).
        if (conformance != PdfAConformance.None)
        {
            var offenders = document.CollectStandard14Usage();
            if (offenders.Count > 0)
            {
                throw PdfACreationSupport.Standard14Refusal(conformance, offenders);
            }
        }

        // Pass 2: now that every page's glyph usage is known, build each distinct font's PDF
        // object once (Standard-14: plain dictionary; TrueType: subset + embed).
        document.BuildFontObjects();

        // Pass 3: replay each page's draw instructions into real content-stream bytes.
        var pageRefs = new List<IndirectReference>();
        foreach (var plan in pagePlans)
        {
            var (pageRef, contentRef, pageDictionary, contentStream) = EmitPage(document, plan, options);
            document.Set(contentRef, contentStream);
            document.Set(pageRef, pageDictionary);
            pageRefs.Add(pageRef);
        }

        var (trailer, objects) = document.Finish(manuscript, pageRefs);
        var result = PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, objects), new DiagnosticCollection(), options);

        // PDF/A finishing: the output intent plus the agreeing XMP (pdfaid
        // identification included — and pdfuaid when Manuscript.PdfUa also asks for it, the
        // two schemas coexisting in one packet) and /Info metadata — applied
        // through the same public SetXmpMetadata/SetInfo doors any caller would use, so the
        // same agreement rule and the XMP write caps are enforced identically here.
        if (conformance != PdfAConformance.None)
        {
            PdfACreationSupport.ApplyCreateMetadata(result, manuscript.Title, manuscript.CreateDate, manuscript.ModifyDate, options, manuscript.PdfUa);
        }
        else if (manuscript.PdfUa)
        {
            // PDF/UA without PDF/A: the honest conformance claim (pdfuaid:part = 1)
            // plus the agreeing dc:title, through the same public door. No timestamps
            // are written (PDF/UA mandates none), so deterministic output needs no dates.
            result.SetXmpMetadata(new Documents.Metadata.XmpPacket
            {
                Title = manuscript.Title,
                DeclarePdfUa = true,
            });
        }

        return result;
    }

    private static void RenderPageContent(DocumentBuilder document, PaginatedSection paginated, int pageIndex, int pageNumber, int totalPages, int globalPageIndex, List<PageOp> ops)
    {
        var section = paginated.Section;
        var margins = section.Margins;
        var pageSize = section.PageSize;
        var contentLeft = margins.Left;
        var contentTop = pageSize.Height - margins.Top;

        if (section.Watermark is { } watermark)
        {
            DrawWatermark(document, ops, watermark, pageSize);
        }

        if (paginated.Header is { } header)
        {
            DrawElement(document, ops, header, contentLeft, contentTop, paginated.ContentWidth, pageNumber, totalPages, globalPageIndex);
            contentTop -= header.Height + section.HeaderSpacing;
        }

        var cursor = contentTop;
        var items = paginated.Pages[pageIndex];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i > 0)
            {
                cursor -= item.GapBefore;
            }

            switch (item)
            {
                case MeasuredFlowItem measuredItem:
                    DrawElement(document, ops, measuredItem.Measured, contentLeft, cursor, paginated.ContentWidth, pageNumber, totalPages, globalPageIndex);
                    break;

                case TableRowFlowItem tableRow:
                    DrawRowCells(document, ops, tableRow.Row, tableRow.ColumnWidths, contentLeft, cursor, pageNumber, totalPages, globalPageIndex, tableRow.Table, tableRow.IsHeader);
                    if (tableRow.IsHeader)
                    {
                        DrawHeaderRule(document, ops, contentLeft, cursor - tableRow.Row.Height, tableRow.ColumnWidths.Sum());
                    }

                    break;
            }

            cursor -= item.Height;
        }

        if (paginated.Footer is { } footer)
        {
            var footerTop = margins.Bottom + footer.Height;
            DrawElement(document, ops, footer, contentLeft, footerTop, paginated.ContentWidth, pageNumber, totalPages, globalPageIndex);
        }

        foreach (var stamp in section.Stamps)
        {
            DrawStamp(document, ops, stamp, section);
        }
    }

    private static void DrawElement(DocumentBuilder document, List<PageOp> ops, Measured node, double left, double top, double width, int pageNumber, int totalPages, int globalPageIndex)
    {
        switch (node)
        {
            case MeasuredText text:
                DrawText(document, ops, text, left, top, width, pageNumber, totalPages, globalPageIndex);
                break;

            case MeasuredImage image:
                DrawImage(document, ops, image, left, top, globalPageIndex);
                break;

            case MeasuredRow row:
                var rowPopCount = EnterContainerIfRoled(document, row.Row);
                foreach (var (child, x) in row.Children)
                {
                    DrawElement(document, ops, child, left + x, top, child.Width, pageNumber, totalPages, globalPageIndex);
                }

                ExitContainer(document, rowPopCount);
                break;

            case MeasuredColumn column:
                var columnPopCount = EnterContainerIfRoled(document, column.Column);
                var cursor = top;
                for (var i = 0; i < column.Children.Count; i++)
                {
                    if (i > 0)
                    {
                        cursor -= column.Column.Spacing;
                    }

                    var child = column.Children[i];
                    DrawElement(document, ops, child, left, cursor, width, pageNumber, totalPages, globalPageIndex);
                    cursor -= child.Height;
                }

                ExitContainer(document, columnPopCount);
                break;

            case MeasuredTable table:
                // Reached only for a Table nested inside Header/Footer or a table cell — the
                // section body's own tables are pre-flattened per row by Paginator and drawn
                // through DrawRowCells instead.
                var rowTop = top;
                if (table.Header is { } header)
                {
                    DrawRowCells(document, ops, header, table.ColumnWidths, left, rowTop, pageNumber, totalPages, globalPageIndex, table.Table, isHeader: true);
                    rowTop -= header.Height;
                }

                foreach (var row in table.Rows)
                {
                    DrawRowCells(document, ops, row, table.ColumnWidths, left, rowTop, pageNumber, totalPages, globalPageIndex, table.Table, isHeader: false);
                    rowTop -= row.Height;
                }

                break;

            case MeasuredPageBreak:
                break;
        }
    }

    /// <summary>
    /// When tagging is active and <paramref name="source"/> (a <see cref="Row"/>/<see cref="Column"/>)
    /// carries an explicit <see cref="Elements.Element.Role"/>, creates/reuses its container
    /// structure node and pushes it as the current structural parent for its children — returns
    /// 1 (the number of pushes <see cref="ExitContainer"/> must undo) or 0 when nothing was
    /// pushed (tagging inactive, or no explicit role — children then attach to whatever the
    /// nearest ambient tagged ancestor already is, the common case for a plain layout container).
    /// </summary>
    private static int EnterContainerIfRoled(DocumentBuilder document, Element source)
    {
        if (document.CurrentStructParent is not { } parent || source.Role is not { Length: > 0 } role || role == StructureRoles.Artifact)
        {
            return 0;
        }

        var node = document.GetOrCreateNode(source, parent, role, language: null, alt: null, actualText: null, tableScope: null);
        document.PushStructParent(node);
        return 1;
    }

    private static void ExitContainer(DocumentBuilder document, int pushCount)
    {
        for (var i = 0; i < pushCount; i++)
        {
            document.PopStructParent();
        }
    }

    private static void DrawRowCells(DocumentBuilder document, List<PageOp> ops, MeasuredTableRow row, IReadOnlyList<double> columnWidths, double left, double top, int pageNumber, int totalPages, int globalPageIndex, Table sourceTable, bool isHeader)
    {
        // One TR node per row occurrence (not per cell!) - get-or-create it once, outside the
        // cell loop, so every cell in this row nests under the same TR.
        var ambientParent = document.CurrentStructParent;
        var rowNode = ambientParent is not null
            ? (isHeader ? document.GetOrCreateHeaderRowNode(sourceTable, ambientParent) : document.CreateBodyRowNode(sourceTable, ambientParent))
            : null;

        var x = left;
        for (var i = 0; i < row.Cells.Count; i++)
        {
            if (rowNode is not null)
            {
                var scope = isHeader ? TableScopeName(sourceTable.HeaderScope) : null;
                var cellRole = isHeader ? StructureRoles.TableHeaderCell : StructureRoles.TableDataCell;
                var cellNode = document.GetOrCreateNode(row.Cells[i].Source, rowNode, cellRole, language: null, alt: null, actualText: null, tableScope: scope);
                document.PushStructParent(cellNode);
                DrawElement(document, ops, row.Cells[i], x, top, columnWidths[i], pageNumber, totalPages, globalPageIndex);
                document.PopStructParent();
            }
            else
            {
                DrawElement(document, ops, row.Cells[i], x, top, columnWidths[i], pageNumber, totalPages, globalPageIndex);
            }

            x += columnWidths[i];
        }
    }

    private static string TableScopeName(TableHeaderScope scope) => scope switch
    {
        TableHeaderScope.Row => "Row",
        TableHeaderScope.Both => "Both",
        _ => "Column",
    };

    private static void DrawHeaderRule(DocumentBuilder document, List<PageOp> ops, double left, double y, double width)
    {
        var tagged = document.CurrentStructParent is not null;
        if (tagged)
        {
            ops.Add(new BeginArtifactOp());
        }

        ops.Add(new SaveStateOp());
        ops.Add(new SetStrokeRgbOp(0.4, 0.4, 0.4));
        ops.Add(new SetLineWidthOp(0.75));
        ops.Add(new MoveToOp(left, y));
        ops.Add(new LineToOp(left + width, y));
        ops.Add(new StrokeOp());
        ops.Add(new RestoreStateOp());

        if (tagged)
        {
            ops.Add(new EndMarkedContentOp());
        }
    }

    private static void DrawText(DocumentBuilder document, List<PageOp> ops, MeasuredText text, double left, double top, double width, int pageNumber, int totalPages, int globalPageIndex)
    {
        var element = text.Text;
        if (element.Content.Length == 0)
        {
            return;
        }

        var font = LayoutEngine.ResolveFont(element);
        var lineHeight = text.LineHeight;
        var lines = new List<TextLine>();
        var align = ResolveAlign(element.Align, text.Direction);

        for (var i = 0; i < text.Lines.Count; i++)
        {
            var line = Substitute(text.Lines[i], pageNumber, totalPages);
            if (line.Length == 0)
            {
                continue;
            }

            // Phase 6.5: the exact same bidi-aware shaping call LayoutEngine measured this
            // line with (TextLayouter.ShapeVisualLine, against the same MeasuredText.Direction)
            // — visual-order glyphs, L4-mirrored where a run resolved right-to-left, so measured
            // and painted widths are equal by construction (see that method's own remarks).
            // The font's accumulated GlyphClusterMap (real cluster-driven /ToUnicode
            // provenance, not the independent per-Rune cmap walk RegisterFontUsage falls back
            // to) records every level run's shaped glyphs as this line is shaped.
            var visual = TextLayouter.ShapeVisualLine(line, font.Metrics, text.Direction, document.GetOrCreateClusterMap(font));
            document.RegisterFontUsage(font, new ShapedRun(visual.Glyphs), line);

            var lineWidth = visual.Width * element.FontSize / font.Metrics.UnitsPerEm;
            var x = left + AlignOffset(align, width, lineWidth);
            var baselineY = top - (i * lineHeight) - (element.FontSize * 0.8);
            lines.Add(new TextLine(1, 0, 0, 1, x, baselineY, visual.Glyphs));
        }

        if (lines.Count == 0)
        {
            return;
        }

        EmitTagged(document, ops, element, globalPageIndex, role => role ?? (element.HeadingLevel is { } level ? StructureRoles.Heading(level) : StructureRoles.Paragraph),
            language: null, alt: null, actualText: null, tableScope: null,
            paint: () => ops.Add(new TextRunOp(font, element.FontSize, lines)));
    }

    private static void DrawImage(DocumentBuilder document, List<PageOp> ops, MeasuredImage image, double left, double top, int globalPageIndex)
    {
        var y = top - image.Height;
        var element = image.Image;

        void Paint()
        {
            ops.Add(new SaveStateOp());
            ops.Add(new TransformOp(image.Width, 0, 0, image.Height, left, y));
            ops.Add(new PaintImageOp(element));
            ops.Add(new RestoreStateOp());
        }

        if (document.CurrentStructParent is null)
        {
            Paint();
            return;
        }

        var role = element.Role ?? StructureRoles.Figure;
        if (role != StructureRoles.Artifact && string.IsNullOrWhiteSpace(element.AltText))
        {
            var path = document.DescribeCurrentPath(StructureRoles.Figure);
            throw new PlumePdfException(
                "PLUME9010",
                $"Image at {path} (page {globalPageIndex + 1}) has no AltText while the manuscript requests tagged/PDF-UA output (Manuscript.Language is set) — set Image.AltText to describe it for assistive technology, or set Image.Role = \"Artifact\" to mark it as purely decorative.");
        }

        EmitTagged(document, ops, element, globalPageIndex, _ => role, language: null, alt: element.AltText, actualText: null, tableScope: null, paint: Paint);
    }

    /// <summary>
    /// The shared tag/paint pattern <see cref="DrawText"/> and <see cref="DrawImage"/> both use:
    /// when tagging is inactive, just <paramref name="paint"/>; when active and the element's
    /// effective role is <see cref="StructureRoles.Artifact"/>, wrap <paramref name="paint"/> in
    /// <c>/Artifact BMC…EMC</c> (excluded from the structure tree); otherwise get-or-create this
    /// element's structure node, assign it a fresh MCID for this page, and wrap
    /// <paramref name="paint"/> in <c>BDC…EMC</c> tagged with that MCID.
    /// </summary>
    private static void EmitTagged(DocumentBuilder document, List<PageOp> ops, Element element, int globalPageIndex, Func<string?, string> resolveRole, string? language, string? alt, string? actualText, string? tableScope, Action paint)
    {
        var parent = document.CurrentStructParent;
        if (parent is null)
        {
            paint();
            return;
        }

        var role = resolveRole(element.Role);
        if (role == StructureRoles.Artifact)
        {
            ops.Add(new BeginArtifactOp());
            paint();
            ops.Add(new EndMarkedContentOp());
            return;
        }

        var node = document.GetOrCreateNode(element, parent, role, language, alt, actualText, tableScope);
        var mcid = document.NextMcid(globalPageIndex);
        node.Children.Add(new MarkedContentReference { PageIndex = globalPageIndex, Mcid = mcid });

        ops.Add(new BeginTaggedContentOp(role, mcid));
        paint();
        ops.Add(new EndMarkedContentOp());
    }

    private static void DrawWatermark(DocumentBuilder document, List<PageOp> ops, Watermark watermark, PageSize pageSize)
    {
        var font = PdfFont.HelveticaBold;
        var shaped = Shaper.Shape(watermark.Text, font.Metrics, ShapingOptions.Default);
        document.RegisterFontUsage(font, shaped, watermark.Text, usageKind: "Watermark");

        var textWidth = TextLayouter.MeasureText(watermark.Text, watermark.FontSize, font.Metrics);
        var radians = watermark.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        // Place the text's own baseline-start origin so that, once rotated about it, the
        // string ends up centered on the page.
        var localX = -textWidth / 2;
        var localY = -watermark.FontSize * 0.35;
        var tx = (pageSize.Width / 2) + (localX * cos) - (localY * sin);
        var ty = (pageSize.Height / 2) + (localX * sin) + (localY * cos);

        // A watermark is pagination furniture, not document content (ISO 32000-1 §14.8.2.2) —
        // always marked /Artifact, regardless of whether the rest of the document is tagged.
        ops.Add(new BeginArtifactOp());
        ops.Add(new SaveStateOp());
        ops.Add(new ApplyExtGStateOp(watermark.Opacity));
        ops.Add(new SetFillRgbOp(watermark.Color.Red, watermark.Color.Green, watermark.Color.Blue));
        ops.Add(new TextRunOp(font, watermark.FontSize, [new TextLine(cos, sin, -sin, cos, tx, ty, shaped.Glyphs)]));
        ops.Add(new RestoreStateOp());
        ops.Add(new EndMarkedContentOp());
    }

    private static void DrawStamp(DocumentBuilder document, List<PageOp> ops, Stamp stamp, Section section)
    {
        var font = PdfFont.HelveticaBold;
        var shaped = Shaper.Shape(stamp.Text, font.Metrics, ShapingOptions.Default);
        document.RegisterFontUsage(font, shaped, stamp.Text, usageKind: "Stamp");

        var textWidth = TextLayouter.MeasureText(stamp.Text, stamp.FontSize, font.Metrics);
        var margins = section.Margins;
        var pageSize = section.PageSize;

        var (x, y) = stamp.Position switch
        {
            StampPosition.TopLeft => (margins.Left, pageSize.Height - margins.Top - stamp.FontSize),
            StampPosition.TopRight => (pageSize.Width - margins.Right - textWidth, pageSize.Height - margins.Top - stamp.FontSize),
            StampPosition.BottomLeft => (margins.Left, margins.Bottom),
            _ => (pageSize.Width - margins.Right - textWidth, margins.Bottom),
        };

        // A stamp is pagination furniture, not document content (ISO 32000-1 §14.8.2.2) —
        // always marked /Artifact, regardless of whether the rest of the document is tagged.
        ops.Add(new BeginArtifactOp());
        ops.Add(new SaveStateOp());
        ops.Add(new ApplyExtGStateOp(stamp.Opacity));
        ops.Add(new SetFillRgbOp(stamp.Color.Red, stamp.Color.Green, stamp.Color.Blue));
        ops.Add(new TextRunOp(font, stamp.FontSize, [new TextLine(1, 0, 0, 1, x, y, shaped.Glyphs)]));
        ops.Add(new RestoreStateOp());
        ops.Add(new EndMarkedContentOp());
    }

    /// <summary>
    /// Resolves a <see cref="Text"/>'s alignment against its paragraph direction (Phase 6.5):
    /// <see cref="HorizontalAlign.Left"/>/<see cref="HorizontalAlign.Right"/> pass through
    /// unchanged (physical, never reinterpreted — see <see cref="HorizontalAlign"/>'s own
    /// remarks); <see cref="HorizontalAlign.Start"/>/<see cref="HorizontalAlign.End"/> resolve
    /// to the physical side <paramref name="direction"/> makes them; <see langword="null"/>
    /// (the default) behaves as <see cref="HorizontalAlign.Start"/> — physical left for the
    /// left-to-right direction every pre-Phase-6.5 <see cref="Text"/> resolves to, so this is a
    /// byte-identical no-op for every existing document (the LTR regression <see cref="TextLayouter.ResolveDirection"/>'s
    /// remarks call out).
    /// </summary>
    private static HorizontalAlign ResolveAlign(HorizontalAlign? align, TextDirection direction)
    {
        var isRtl = direction == TextDirection.RightToLeft;
        return align switch
        {
            HorizontalAlign.Left => HorizontalAlign.Left,
            HorizontalAlign.Right => HorizontalAlign.Right,
            HorizontalAlign.Center => HorizontalAlign.Center,
            HorizontalAlign.Start => isRtl ? HorizontalAlign.Right : HorizontalAlign.Left,
            HorizontalAlign.End => isRtl ? HorizontalAlign.Left : HorizontalAlign.Right,
            _ => isRtl ? HorizontalAlign.Right : HorizontalAlign.Left,
        };
    }

    private static double AlignOffset(HorizontalAlign align, double availableWidth, double contentWidth) => align switch
    {
        HorizontalAlign.Center => Math.Max(0, (availableWidth - contentWidth) / 2),
        HorizontalAlign.Right => Math.Max(0, availableWidth - contentWidth),
        _ => 0,
    };

    private static string Substitute(string text, int pageNumber, int totalPages) =>
        text.Contains('{')
            ? text.Replace("{page}", pageNumber.ToString(CultureInfo.InvariantCulture)).Replace("{pages}", totalPages.ToString(CultureInfo.InvariantCulture))
            : text;

    /// <summary>Replays one page's recorded draw instructions into a real content stream and resource dictionary, resolving each font resource name and each glyph's <c>Tj</c> bytes against the objects <see cref="DocumentBuilder.BuildFontObjects"/> already built.</summary>
    private static (IndirectReference PageRef, IndirectReference ContentRef, PdfDictionary PageDictionary, PdfStream ContentStream) EmitPage(DocumentBuilder document, PagePlan plan, PdfOptions options)
    {
        var csb = new ContentStreamBuilder();
        var resources = new ResourceDictionaryBuilder();

        foreach (var op in plan.Ops)
        {
            switch (op)
            {
                case SaveStateOp:
                    csb.SaveState();
                    break;

                case RestoreStateOp:
                    csb.RestoreState();
                    break;

                case SetFillRgbOp o:
                    csb.SetFillRgb(o.R, o.G, o.B);
                    break;

                case SetStrokeRgbOp o:
                    csb.SetStrokeRgb(o.R, o.G, o.B);
                    break;

                case SetLineWidthOp o:
                    csb.SetLineWidth(o.Width);
                    break;

                case MoveToOp o:
                    csb.MoveTo(o.X, o.Y);
                    break;

                case LineToOp o:
                    csb.LineTo(o.X, o.Y);
                    break;

                case StrokeOp:
                    csb.Stroke();
                    break;

                case TransformOp o:
                    csb.Transform(o.A, o.B, o.C, o.D, o.E, o.F);
                    break;

                case ApplyExtGStateOp o:
                    var gsRef = document.GetOrCreateExtGState(o.Opacity);
                    var gsName = resources.AddExtGState(gsRef);
                    csb.ApplyExtGState(gsName.Value);
                    break;

                case PaintImageOp o:
                    var imageRef = document.GetOrCreateImage(o.Image);
                    var imageName = resources.AddXObject(imageRef);
                    csb.PaintXObject(imageName.Value);
                    break;

                case TextRunOp o:
                    var fontRef = document.GetFontReference(o.Font);
                    var fontName = resources.AddFont(fontRef);
                    var glyphIdMap = document.GetGlyphIdMap(o.Font);
                    csb.BeginText();
                    csb.SetFont(fontName.Value, o.FontSize);
                    var unitsPerEm = o.Font.Metrics.UnitsPerEm;
                    var rise = 0.0;
                    foreach (var line in o.Lines)
                    {
                        csb.SetTextMatrix(line.A, line.B, line.C, line.D, line.E, line.F);
                        rise = ShowLine(csb, line.Glyphs, glyphIdMap, unitsPerEm, o.FontSize, rise);
                    }

                    // A later TextRunOp's first line starts a fresh Tm, but Ts (unlike Tm) is
                    // not reset by Td/Tm — leaving it nonzero would misplace the very first
                    // glyph of whatever text object is painted next.
                    if (rise != 0)
                    {
                        csb.SetTextRise(0);
                    }

                    csb.EndText();
                    break;

                case BeginArtifactOp:
                    csb.BeginArtifact();
                    break;

                case BeginTaggedContentOp o:
                    csb.BeginTaggedContent(o.Tag, o.Mcid);
                    break;

                case EndMarkedContentOp:
                    csb.EndMarkedContent();
                    break;
            }
        }

        var contentBytes = csb.Build();
        var mediaBox = new PageBox(0, 0, plan.PageSize.Width, plan.PageSize.Height);
        var assembled = PageContentAssembler.Assemble(mediaBox, resources.Build(), contentBytes, document.PagesRef, options.Filters, options, document.ReserveNumber);

        return (
            new IndirectReference(assembled.PageObjectNumber, 0),
            new IndirectReference(assembled.ContentStreamObjectNumber, 0),
            assembled.PageDictionary,
            assembled.ContentStream);
    }

    /// <summary>
    /// Shows one shaped line, reproducing GPOS kerning and per-glyph mark/cursive placement in
    /// the painted output: layout folds each pair adjustment into the measured
    /// advances (<see cref="ShapedGlyph.KernAdjustment"/>) and mark-attachment positioning into
    /// <see cref="ShapedGlyph.XOffset"/>/<see cref="ShapedGlyph.YOffset"/> (font units, nonzero
    /// only for GPOS mark-to-base/mark-to-ligature/mark-to-mark/cursive positioning — see those
    /// properties' own remarks); a plain <c>Tj</c> would let the viewer paint every glyph at the
    /// bare pen position with the font's unkerned hmtx widths, so measured and painted results
    /// would diverge (kerning) and every attached mark (harakat, Devanagari matras/anusvara,
    /// stacked accents) would land at its base's origin instead of its own (kerning and
    /// placement alike). <see cref="ShapedGlyph.XOffset"/> is reproduced with a <c>TJ</c>
    /// shift-and-cancel pair around the one glyph it applies to (pure horizontal, composes with
    /// kerning, and needs no font-width bookkeeping to undo — the <c>TJ</c> adjustment is
    /// additive regardless of what the font's own declared width does after <c>Tj</c>);
    /// <see cref="ShapedGlyph.YOffset"/> is reproduced with the text-state <c>Ts</c> (rise)
    /// operator, which likewise never touches horizontal pen advance — <paramref name="rise"/>
    /// carries the caller's current <c>Ts</c> value in across lines (it is text *state*, not
    /// reset by <c>Tm</c>/<c>Td</c> or by a new line) and the return value hands the (possibly
    /// changed) value back for the next line, or for the caller to reset to 0 once the whole
    /// <see cref="TextRunOp"/> is done. Lines with no kerning or placement still use a bare
    /// <c>Tj</c>.
    /// </summary>
    private static double ShowLine(ContentStreamBuilder csb, IReadOnlyList<ShapedGlyph> glyphs, IReadOnlyDictionary<int, int>? glyphIdMap, int unitsPerEm, double fontSize, double rise)
    {
        var needsAdjustment = false;
        for (var i = 0; i < glyphs.Count; i++)
        {
            if (glyphs[i].KernAdjustment != 0 || glyphs[i].XOffset != 0 || glyphs[i].YOffset != 0)
            {
                needsAdjustment = true;
                break;
            }
        }

        if (!needsAdjustment)
        {
            if (rise != 0)
            {
                csb.SetTextRise(0);
                rise = 0;
            }

            csb.ShowText(EncodeGlyphs(glyphs, glyphIdMap));
            return rise;
        }

        var elements = new List<TextShowElement>();
        var runStart = 0;

        void FlushRun(int endExclusive)
        {
            if (endExclusive > runStart)
            {
                elements.Add(TextShowElement.ForText(EncodeGlyphRange(glyphs, runStart, endExclusive - runStart, glyphIdMap)));
            }
        }

        void FlushElements()
        {
            if (elements.Count > 0)
            {
                csb.ShowTextWithAdjustments(elements);
                elements = [];
            }
        }

        for (var i = 0; i < glyphs.Count; i++)
        {
            var glyph = glyphs[i];

            // Ts is text state, not a TJ array element — a rise change must close whatever
            // Tj/TJ is pending and start a fresh one after the operator.
            var riseNeeded = glyph.YOffset * fontSize / unitsPerEm;
            if (riseNeeded != rise)
            {
                FlushRun(i);
                FlushElements();
                csb.SetTextRise(riseNeeded);
                rise = riseNeeded;
                runStart = i;
            }

            if (glyph.XOffset != 0)
            {
                // Shift right by XOffset, show this one glyph alone, then shift back left by
                // XOffset while also applying any kern gap this glyph carries — one combined
                // adjustment, exactly as the kern-only branch below folds its own gap in.
                FlushRun(i);
                elements.Add(TextShowElement.ForAdjustment(ToAdjustment(glyph.XOffset, unitsPerEm)));
                elements.Add(TextShowElement.ForText(EncodeGlyphRange(glyphs, i, 1, glyphIdMap)));
                elements.Add(TextShowElement.ForAdjustment(ToAdjustment(glyph.KernAdjustment - glyph.XOffset, unitsPerEm)));
                runStart = i + 1;
                continue;
            }

            if (glyph.KernAdjustment != 0)
            {
                // Emit the run up to and including this glyph, then the TJ adjustment that
                // reproduces the kern: TJ subtracts (amount/1000 em) from the displacement, so
                // a positive kern (wider) needs a negative element and vice versa.
                FlushRun(i + 1);
                elements.Add(TextShowElement.ForAdjustment(ToAdjustment(glyph.KernAdjustment, unitsPerEm)));
                runStart = i + 1;
            }
        }

        FlushRun(glyphs.Count);
        FlushElements();
        return rise;
    }

    /// <summary>Converts a font-units rightward/wider displacement into the <c>TJ</c> adjustment number that reproduces it (ISO 32000-1 §9.4.3: the operand is subtracted from the pen position, so it is the negation of the desired displacement, scaled to thousandths of an em).</summary>
    private static double ToAdjustment(double displacementFontUnits, int unitsPerEm) => -displacementFontUnits * 1000.0 / unitsPerEm;

    private static byte[] EncodeGlyphRange(IReadOnlyList<ShapedGlyph> glyphs, int start, int count, IReadOnlyDictionary<int, int>? glyphIdMap)
    {
        var slice = new ShapedGlyph[count];
        for (var i = 0; i < count; i++)
        {
            slice[i] = glyphs[start + i];
        }

        return EncodeGlyphs(slice, glyphIdMap);
    }

    /// <summary>Encodes one shaped line's glyphs into the bytes its <c>Tj</c> operand needs: a single WinAnsi byte per glyph for a Standard-14 font (<paramref name="glyphIdMap"/> is <see langword="null"/>), or a 2-byte big-endian CID per glyph for an embedded font under Identity-H (<paramref name="glyphIdMap"/> maps the original font's glyph IDs to the embedded subset's).</summary>
    private static byte[] EncodeGlyphs(IReadOnlyList<ShapedGlyph> glyphs, IReadOnlyDictionary<int, int>? glyphIdMap)
    {
        if (glyphIdMap is null)
        {
            var bytes = new byte[glyphs.Count];
            for (var i = 0; i < glyphs.Count; i++)
            {
                bytes[i] = (byte)glyphs[i].GlyphId;
            }

            return bytes;
        }

        var cidBytes = new byte[glyphs.Count * 2];
        for (var i = 0; i < glyphs.Count; i++)
        {
            var cid = glyphIdMap.TryGetValue(glyphs[i].GlyphId, out var mapped) ? mapped : 0;
            cidBytes[i * 2] = (byte)(cid >> 8);
            cidBytes[(i * 2) + 1] = (byte)cid;
        }

        return cidBytes;
    }

    /// <summary>One page's rendered size and its ordered, font-agnostic draw instructions — the output of pass 1, replayed by <see cref="EmitPage"/> in pass 3.</summary>
    private sealed record PagePlan(PageSize PageSize, List<PageOp> Ops);

    /// <summary>One page-level draw instruction, recorded during pass 1 and replayed against real <see cref="Content.ContentStreamBuilder"/>/<see cref="ResourceDictionaryBuilder"/> calls during pass 3.</summary>
    private abstract class PageOp;

    private sealed class SaveStateOp : PageOp;

    private sealed class RestoreStateOp : PageOp;

    private sealed class SetFillRgbOp(double r, double g, double b) : PageOp
    {
        public double R { get; } = r;
        public double G { get; } = g;
        public double B { get; } = b;
    }

    private sealed class SetStrokeRgbOp(double r, double g, double b) : PageOp
    {
        public double R { get; } = r;
        public double G { get; } = g;
        public double B { get; } = b;
    }

    private sealed class SetLineWidthOp(double width) : PageOp
    {
        public double Width { get; } = width;
    }

    private sealed class MoveToOp(double x, double y) : PageOp
    {
        public double X { get; } = x;
        public double Y { get; } = y;
    }

    private sealed class LineToOp(double x, double y) : PageOp
    {
        public double X { get; } = x;
        public double Y { get; } = y;
    }

    private sealed class StrokeOp : PageOp;

    private sealed class TransformOp(double a, double b, double c, double d, double e, double f) : PageOp
    {
        public double A { get; } = a;
        public double B { get; } = b;
        public double C { get; } = c;
        public double D { get; } = d;
        public double E { get; } = e;
        public double F { get; } = f;
    }

    private sealed class ApplyExtGStateOp(double opacity) : PageOp
    {
        public double Opacity { get; } = opacity;
    }

    /// <summary>Emits <c>/Artifact BMC</c> (<see cref="Content.ContentStreamBuilder.BeginArtifact"/>) — pagination furniture (watermark, stamp, decorative rule), excluded from the structure tree.</summary>
    private sealed class BeginArtifactOp : PageOp;

    /// <summary>Emits <c>/Tag &lt;&lt;/MCID n&gt;&gt; BDC</c> (<see cref="Content.ContentStreamBuilder.BeginTaggedContent"/>) — the real, structure-tree-connected marked content a tagged manuscript's elements paint through.</summary>
    private sealed class BeginTaggedContentOp(string tag, int mcid) : PageOp
    {
        public string Tag { get; } = tag;
        public int Mcid { get; } = mcid;
    }

    /// <summary>Emits <c>EMC</c> (<see cref="Content.ContentStreamBuilder.EndMarkedContent"/>), closing whichever of <see cref="BeginArtifactOp"/>/<see cref="BeginTaggedContentOp"/> opened it.</summary>
    private sealed class EndMarkedContentOp : PageOp;

    private sealed class PaintImageOp(Image image) : PageOp
    {
        public Image Image { get; } = image;
    }

    /// <summary>One positioned, already-shaped line of text within a <see cref="TextRunOp"/> — the text matrix (ISO 32000-1 §9.4.2's <c>Tm</c> operands) it paints at, and the glyphs it shows.</summary>
    private sealed record TextLine(double A, double B, double C, double D, double E, double F, IReadOnlyList<ShapedGlyph> Glyphs);

    private sealed class TextRunOp(PdfFont font, double fontSize, IReadOnlyList<TextLine> lines) : PageOp
    {
        public PdfFont Font { get; } = font;
        public double FontSize { get; } = fontSize;
        public IReadOnlyList<TextLine> Lines { get; } = lines;
    }

    /// <summary>Per-font glyph usage accumulated across every page during pass 1 — only meaningful for an embedded <see cref="TrueTypeFontProgram"/> (a Standard-14 font's <c>/Type1</c> dictionary always declares the full WinAnsi code range regardless of what was actually drawn).</summary>
    private sealed class FontUsage
    {
        public HashSet<int> UsedGlyphIds { get; } = [];

        public Dictionary<int, int> CodepointToGlyphId { get; } = [];
    }

    /// <summary>Accumulates the PDF object graph (catalog, pages tree, and every shared font/ExtGState/image object) for one <see cref="Manuscript"/> render.</summary>
    private sealed class DocumentBuilder
    {
        private readonly PdfOptions _options;
        private readonly Dictionary<int, PdfObject> _objects = [];
        private readonly Dictionary<double, IndirectReference> _extGStates = [];
        private readonly Dictionary<Image, IndirectReference> _images = [];
        private readonly HashSet<PdfFont> _usedFonts = [];
        private readonly Dictionary<PdfFont, FontUsage> _trueTypeUsage = [];
        private readonly Dictionary<PdfFont, IndirectReference> _fontReferences = [];
        private readonly Dictionary<PdfFont, IReadOnlyDictionary<int, int>> _fontGlyphIdMaps = [];

        // Cluster-driven /ToUnicode provenance — one GlyphClusterMap per
        // embedded font, populated as DrawText shapes each line (TextLayouter.ShapeVisualLine's
        // clusterMap parameter) and consumed by BuildFontObjects below.
        private readonly Dictionary<PdfFont, GlyphClusterMap> _clusterMaps = [];

        // PDF/A × Standard-14: the first use site recorded per non-embeddable
        // font, so the PLUME8023 refusal names every offender with where it came from.
        private readonly Dictionary<PdfFont, string> _standard14FirstUse = [];

        // Tagging: the structure tree built alongside pass 1's ops, entirely
        // inert (every field stays empty, every method below a guaranteed no-op) unless
        // BeginTagging() is called — Manuscript.Language's opt-in.
        private StructureElement? _documentStructureRoot;
        private StructureElement? _currentStructParent;
        private readonly Stack<StructureElement?> _structParentStack = [];
        private readonly Dictionary<Element, StructureElement> _structureNodes = [];
        private readonly Dictionary<Table, StructureElement> _headerRowNodes = [];
        private readonly Dictionary<int, int> _nextMcidByPage = [];

        private int _nextNumber = 1;

        public DocumentBuilder(PdfOptions options)
        {
            _options = options;
            CatalogRef = Reserve();
            PagesRef = Reserve();
        }

        public IndirectReference CatalogRef { get; }

        public IndirectReference PagesRef { get; }

        public IndirectReference Reserve() => new(_nextNumber++, 0);

        public int ReserveNumber() => _nextNumber++;

        public void Set(IndirectReference reference, PdfObject value) => _objects[reference.Number] = value;

        public IndirectReference GetOrCreateExtGState(double opacity)
        {
            if (_extGStates.TryGetValue(opacity, out var existing))
            {
                return existing;
            }

            var reference = Reserve();
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("ExtGState"));
            dict.Set(PdfName.Get("ca"), PdfNumber.Get(opacity));
            dict.Set(PdfName.Get("CA"), PdfNumber.Get(opacity));
            Set(reference, dict);
            _extGStates[opacity] = reference;
            return reference;
        }

        public IndirectReference GetOrCreateImage(Image image)
        {
            if (_images.TryGetValue(image, out var existing))
            {
                return existing;
            }

            var reference = Reserve();
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("XObject"));
            dict.Set(PdfName.Subtype, PdfName.Get("Image"));
            dict.Set(PdfName.Get("Width"), PdfNumber.Get(image.PixelWidth));
            dict.Set(PdfName.Get("Height"), PdfNumber.Get(image.PixelHeight));
            dict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));

            // DCT pass-through: a JPEG-sourced Image embeds the
            // original encoder's bytes unchanged behind /DCTDecode instead of re-encoding
            // image.RgbPixels' normalized view (for a 4-component source, already
            // CMYK->RGB-converted and therefore the wrong bytes to pass through at all) — no
            // decode/re-encode round trip, byte-identical to the source regardless of
            // PdfOptions.Deterministic (nothing here is re-encoded). A 4-component source
            // whose original file carried an Adobe APP14 marker stores its samples inverted
            // (0 = full ink); /Decode [1 0 1 0 1 0 1 0] is PDF's standard mechanism for
            // telling a viewer to undo that inversion at render time without touching a
            // single sample byte (ISO 32000-1 §8.9.5.2 Table 90).
            if (image.OriginalJpegBytes is { } originalJpeg)
            {
                // A 4-component (DeviceCMYK) JPEG cannot go into a PDF/A file: PlumePDF's PDF/A
                // output bundles only a CC0 sRGB output intent, so a DeviceCMYK
                // image has no colour characterization and fails 2b/1b conformance. Refuse loudly
                // here rather than emit a silently non-conformant document (the deeper defect
                // behind PLUME3610's now-honest guidance — a CMYK JPEG has no clean PDF/A path in
                // this version; decode to RGB first, or compose without PdfAConformance).
                if (image.OriginalJpegComponentCount == 4 && _options.PdfAConformance != PdfAConformance.None)
                {
                    throw new PlumePdfException("PLUME9014", "Image: a 4-component (CMYK) JPEG cannot be embedded via DCT pass-through under PdfOptions.PdfAConformance - PlumePDF's PDF/A output carries only an sRGB output intent, so DeviceCMYK is non-conformant. Decode the JPEG to RGB (RasterImage.Decode normalizes CMYK/YCCK to RGB) and compose that, or produce a non-PDF/A document.");
                }

                var colorSpaceName = image.OriginalJpegComponentCount switch
                {
                    1 => "DeviceGray",
                    3 => "DeviceRGB",
                    4 => "DeviceCMYK",
                    _ => throw new PlumePdfException("PLUME3602", $"Image: JPEG pass-through source declares {image.OriginalJpegComponentCount} components - only 1, 3, or 4 are supported."),
                };

                dict.Set(PdfName.Get("ColorSpace"), PdfName.Get(colorSpaceName));
                if (image.OriginalJpegComponentCount == 4 && image.OriginalJpegAdobeInverted)
                {
                    dict.Set(PdfName.Get("Decode"), new PdfArray([PdfNumber.Get(1), PdfNumber.Get(0), PdfNumber.Get(1), PdfNumber.Get(0), PdfNumber.Get(1), PdfNumber.Get(0), PdfNumber.Get(1), PdfNumber.Get(0)]));
                }

                var jpegBytes = originalJpeg.ToArray();
                dict.Set(PdfName.Filter, PdfName.Get("DCTDecode"));
                dict.Set(PdfName.Length, PdfNumber.Get(jpegBytes.Length));

                if (image.AlphaPixels is { } jpegAlphaPixels)
                {
                    var jpegSmaskReference = CreateSoftMask(jpegAlphaPixels.ToArray(), image.PixelWidth, image.PixelHeight);
                    dict.Set(PdfName.SMask, new PdfReference(jpegSmaskReference));
                }

                Set(reference, new PdfStream(dict, jpegBytes));
                _images[image] = reference;
                return reference;
            }

            // Phase 7: a grayscale-sourced Image paints as 8-bpc /DeviceGray
            // instead of being expanded to RGB first — a 300-dpi grayscale scan page is a
            // third the size, uncompressed, of its RGB expansion, and the renderer's _images
            // cache holds every image for the whole render (correctness-of-scale, not an
            // optimization, for a multi-page scanned-document exit demo). An RGBA-sourced
            // Image paints as RGB plus a separate /SMask soft-mask XObject (§11.6.5.3) — PDF
            // image XObjects have no native 4-component-with-alpha color space.
            byte[] samples;
            if (image.GrayPixels is { } grayPixels)
            {
                dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
                samples = grayPixels.ToArray();
            }
            else
            {
                dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
                samples = image.RgbPixels.ToArray();
            }

            if (image.AlphaPixels is { } alphaPixels)
            {
                var smaskReference = CreateSoftMask(alphaPixels.ToArray(), image.PixelWidth, image.PixelHeight);
                dict.Set(PdfName.SMask, new PdfReference(smaskReference));
            }

            // Raw 8-bit samples compress several-fold; degrade to uncompressed only when no
            // encoder is registered (same contract as PageContentAssembler).
            var bytes = samples;
            if (_options.Filters.TryGetEncoder("FlateDecode", out var encoder) && encoder is not null)
            {
                bytes = encoder.Encode(bytes, _options);
                dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            }

            dict.Set(PdfName.Length, PdfNumber.Get(bytes.Length));
            Set(reference, new PdfStream(dict, bytes));
            _images[image] = reference;
            return reference;
        }

        /// <summary>Builds a grayscale (<c>/DeviceGray</c>, no further <c>/SMask</c>) image XObject holding <paramref name="alpha8"/> as an RGBA-sourced image's soft mask (ISO 32000-1 §11.6.5.3).</summary>
        private IndirectReference CreateSoftMask(byte[] alpha8, int width, int height)
        {
            var reference = Reserve();
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("XObject"));
            dict.Set(PdfName.Subtype, PdfName.Get("Image"));
            dict.Set(PdfName.Get("Width"), PdfNumber.Get(width));
            dict.Set(PdfName.Get("Height"), PdfNumber.Get(height));
            dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
            dict.Set(PdfName.BitsPerComponent, PdfNumber.Get(8));

            var bytes = alpha8;
            if (_options.Filters.TryGetEncoder("FlateDecode", out var encoder) && encoder is not null)
            {
                bytes = encoder.Encode(bytes, _options);
                dict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            }

            dict.Set(PdfName.Length, PdfNumber.Get(bytes.Length));
            Set(reference, new PdfStream(dict, bytes));
            return reference;
        }

        /// <summary>Records that <paramref name="line"/> was shaped and drawn against <paramref name="font"/> — for an embedded TrueType font, accumulates the glyph IDs and a best-effort codepoint→glyph-ID map (for <c>/ToUnicode</c>) that <see cref="BuildFontObjects"/> subsets against; for a Standard-14 font, records the first use site (<paramref name="usageKind"/> + the line's text) so a PDF/A refusal (<see cref="CollectStandard14Usage"/>) can name where every offending font came from.</summary>
        public void RegisterFontUsage(PdfFont font, ShapedRun shaped, string line, string usageKind = "Text")
        {
            _usedFonts.Add(font);

            if (font.Metrics is not TrueTypeFontProgram ttf)
            {
                _standard14FirstUse.TryAdd(font, $"{usageKind} \"{TruncateForMessage(line)}\"");
                return;
            }

            if (!_trueTypeUsage.TryGetValue(font, out var usage))
            {
                usage = new FontUsage();
                _trueTypeUsage[font] = usage;
            }

            foreach (var glyph in shaped.Glyphs)
            {
                usage.UsedGlyphIds.Add(glyph.GlyphId);
            }

            foreach (var rune in line.EnumerateRunes())
            {
                if (ttf.TryGetGlyphId(rune.Value, out var glyphId))
                {
                    usage.CodepointToGlyphId.TryAdd(rune.Value, glyphId);
                }
            }
        }

        /// <summary>Every Standard-14 (non-embeddable) font <see cref="RegisterFontUsage"/> recorded, each with its first use site — ordered by font name so the PLUME8023 refusal message is deterministic. Empty when every font in use is an embedded TrueType/OpenType font.</summary>
        public IReadOnlyList<(string FontName, string FirstUse)> CollectStandard14Usage() =>
            _standard14FirstUse
                .Select(static kv => (kv.Key.Name, kv.Value))
                .OrderBy(static pair => pair.Name, StringComparer.Ordinal)
                .ToList();

        /// <summary>Bounds a drawn line's text for use inside a refusal message — long lines are cut at 40 characters with an ellipsis.</summary>
        private static string TruncateForMessage(string line) =>
            line.Length <= 40 ? line : line[..40] + "…";

        /// <summary>Gets or creates <paramref name="font"/>'s accumulated <see cref="GlyphClusterMap"/> — one instance per font usage, shared across every line drawn against it so a glyph id's cluster is recorded once regardless of how many pages repeat it.</summary>
        public GlyphClusterMap GetOrCreateClusterMap(PdfFont font)
        {
            if (!_clusterMaps.TryGetValue(font, out var map))
            {
                map = new GlyphClusterMap();
                _clusterMaps[font] = map;
            }

            return map;
        }

        /// <summary>Converts <paramref name="map"/>'s glyph-id→codepoints strings into <see cref="FontObjectBuilder"/>'s local <see cref="GlyphCluster"/> shape — the one <see cref="FontObjectBuilder.BuildEmbeddedTrueType"/> consumes.</summary>
        private static List<GlyphCluster> ConvertClusters(GlyphClusterMap map)
        {
            var result = new List<GlyphCluster>(map.GlyphIdToCodepoints.Count);
            foreach (var (glyphId, text) in map.GlyphIdToCodepoints)
            {
                var codepoints = new List<int>(text.Length);
                var i = 0;
                while (i < text.Length)
                {
                    if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        codepoints.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                        i += 2;
                    }
                    else
                    {
                        codepoints.Add(text[i]);
                        i += 1;
                    }
                }

                result.Add(new GlyphCluster(glyphId, codepoints));
            }

            return result;
        }

        /// <summary>Builds one PDF font object per distinct <see cref="PdfFont"/> <see cref="RegisterFontUsage"/> recorded — call once, after every page has been walked.</summary>
        public void BuildFontObjects()
        {
            foreach (var font in _usedFonts)
            {
                switch (font.Metrics)
                {
                    case Standard14Font standard14:
                        _fontReferences[font] = FontObjectBuilder.BuildStandard14(standard14, Allocate);
                        break;

                    case TrueTypeFontProgram trueType:
                        var usage = _trueTypeUsage[font];
                        var clusters = _clusterMaps.TryGetValue(font, out var clusterMap) ? ConvertClusters(clusterMap) : null;
                        var result = FontObjectBuilder.BuildEmbeddedTrueType(trueType, usage.UsedGlyphIds, usage.CodepointToGlyphId, Allocate, _options.Filters, _options, clusters);
                        _fontReferences[font] = result.FontDictionaryReference;
                        _fontGlyphIdMaps[font] = result.GlyphIdMap;
                        break;
                }
            }
        }

        public IndirectReference GetFontReference(PdfFont font) => _fontReferences[font];

        public IReadOnlyDictionary<int, int>? GetGlyphIdMap(PdfFont font) => _fontGlyphIdMaps.GetValueOrDefault(font);

        /// <summary>Activates tagging (gated on <see cref="Manuscript.Language"/>): creates the tree's <see cref="StructureRoles.Document"/> root and makes it the ambient parent every top-level tagged element attaches under.</summary>
        public void BeginTagging()
        {
            _documentStructureRoot = new StructureElement { Role = StructureRoles.Document };
            _currentStructParent = _documentStructureRoot;
        }

        /// <summary>The structural parent new nodes attach under right now, or <see langword="null"/> when tagging is inactive (<see cref="BeginTagging"/> never called) — every draw method treats <see langword="null"/> as "paint untagged, exactly as before tagging existed".</summary>
        public StructureElement? CurrentStructParent => _currentStructParent;

        /// <summary>Descends into <paramref name="node"/> as the ambient parent for whatever is drawn until the matching <see cref="PopStructParent"/>.</summary>
        public void PushStructParent(StructureElement node)
        {
            _structParentStack.Push(_currentStructParent);
            _currentStructParent = node;
        }

        /// <summary>Restores the ambient parent to what it was before the matching <see cref="PushStructParent"/>.</summary>
        public void PopStructParent() => _currentStructParent = _structParentStack.Pop();

        /// <summary>
        /// Returns the structure node already created for <paramref name="source"/>, or creates
        /// and attaches a new one under <paramref name="parent"/> the first time this
        /// <see cref="Element"/> instance is drawn — a header/footer/repeating table-header cell
        /// paints on several pages, and every occurrence after the first reuses (rather than
        /// duplicates) this same node, so a subsequent <see cref="MarkedContentReference"/> for
        /// that occurrence is simply appended to it by the caller.
        /// </summary>
        public StructureElement GetOrCreateNode(Element source, StructureElement parent, string role, string? language, string? alt, string? actualText, string? tableScope)
        {
            if (_structureNodes.TryGetValue(source, out var existing))
            {
                return existing;
            }

            var node = new StructureElement
            {
                Role = role,
                Language = language,
                AlternateText = alt,
                ActualText = actualText,
                TableHeaderScope = tableScope,
                Parent = parent,
            };

            _structureNodes[source] = node;
            parent.Children.Add(node);
            return node;
        }

        /// <summary>Gets or creates <paramref name="table"/>'s <see cref="StructureRoles.Table"/> container node.</summary>
        public StructureElement GetOrCreateTableNode(Table table, StructureElement ambientParent) =>
            GetOrCreateNode(table, ambientParent, StructureRoles.Table, language: null, alt: null, actualText: null, tableScope: null);

        /// <summary>
        /// Gets or creates <paramref name="table"/>'s header row's <see cref="StructureRoles.TableRow"/>
        /// node — stable across every page the header repeats on (unlike a body row, a header
        /// row's <see cref="StructureRoles.TableRow"/> node must be the <em>same</em> node every
        /// repeat, so its cells' structure nodes (also stable, via <see cref="GetOrCreateNode"/>)
        /// each accumulate one more <see cref="MarkedContentReference"/> child per repeat instead
        /// of the tree gaining a duplicate header row per page.
        /// </summary>
        public StructureElement GetOrCreateHeaderRowNode(Table table, StructureElement ambientParent)
        {
            if (_headerRowNodes.TryGetValue(table, out var existing))
            {
                return existing;
            }

            var row = new StructureElement { Role = StructureRoles.TableRow, Parent = GetOrCreateTableNode(table, ambientParent) };
            row.Parent!.Children.Add(row);
            _headerRowNodes[table] = row;
            return row;
        }

        /// <summary>Creates a fresh <see cref="StructureRoles.TableRow"/> node under <paramref name="table"/> — safe to call unconditionally for a body row, since (unlike the header) each body row paints exactly once.</summary>
        public StructureElement CreateBodyRowNode(Table table, StructureElement ambientParent)
        {
            var row = new StructureElement { Role = StructureRoles.TableRow, Parent = GetOrCreateTableNode(table, ambientParent) };
            row.Parent!.Children.Add(row);
            return row;
        }

        /// <summary>Returns the next sequential MCID for <paramref name="pageIndex"/> (0, 1, 2, … per page).</summary>
        public int NextMcid(int pageIndex)
        {
            var next = _nextMcidByPage.GetValueOrDefault(pageIndex);
            _nextMcidByPage[pageIndex] = next + 1;
            return next;
        }

        /// <summary>Builds a human-readable "Document/Table/TR/TD" style path from the current ambient parent down to (and including) <paramref name="leafRole"/> — for naming the element in a coded refusal (<c>PLUME9010</c>).</summary>
        public string DescribeCurrentPath(string leafRole)
        {
            var segments = new List<string>();
            for (var node = _currentStructParent; node is not null; node = node.Parent)
            {
                segments.Add(node.Role);
            }

            segments.Reverse();
            segments.Add(leafRole);
            return string.Join("/", segments);
        }

        public (PdfDictionary Trailer, IReadOnlyDictionary<int, PdfObject> Objects) Finish(Manuscript manuscript, IReadOnlyList<IndirectReference> pageRefs)
        {
            var pagesDict = new PdfDictionary();
            pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
            pagesDict.Set(PdfName.Get("Kids"), new PdfArray(pageRefs.Select(static r => (PdfObject)new PdfReference(r))));
            pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(pageRefs.Count));
            Set(PagesRef, pagesDict);

            var catalogDict = new PdfDictionary();
            catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
            catalogDict.Set(PdfName.Get("Pages"), new PdfReference(PagesRef));

            if (manuscript.Language is { Length: > 0 } language)
            {
                catalogDict.Set(PdfName.Get("Lang"), PdfString.FromLiteral(System.Text.Encoding.Latin1.GetBytes(language)));
            }

            if (_documentStructureRoot is { } root)
            {
                var result = StructureTreeBuilder.Build(root, pageRefs, _options, Reserve, Set);
                catalogDict.Set(PdfName.Get("StructTreeRoot"), new PdfReference(result.StructTreeRootReference));
                catalogDict.Set(PdfName.Get("MarkInfo"), result.MarkInfoDictionary);

                foreach (var pageIndex in result.TaggedPageIndices)
                {
                    if (_objects.TryGetValue(pageRefs[pageIndex].Number, out var pageObject) && pageObject is PdfDictionary pageDict)
                    {
                        pageDict.Set(PdfName.Get("StructParents"), PdfNumber.Get(pageIndex));
                    }
                }

                if (manuscript.Title is { Length: > 0 })
                {
                    var viewerPreferences = new PdfDictionary();
                    viewerPreferences.Set(PdfName.Get("DisplayDocTitle"), PdfBoolean.True);
                    catalogDict.Set(PdfName.Get("ViewerPreferences"), viewerPreferences);
                }
            }

            Set(CatalogRef, catalogDict);

            var trailer = new PdfDictionary();
            trailer.Set(PdfName.Root, new PdfReference(CatalogRef));

            if (manuscript.Title is { Length: > 0 } title)
            {
                var infoDict = new PdfDictionary();
                infoDict.Set(PdfName.Get("Title"), ToPdfDocString(title));
                trailer.Set(PdfName.Info, new PdfReference(Allocate(infoDict)));
            }

            // /Size must be stamped AFTER the last allocation above: a stale (one-low) Size
            // made ObjectRegistry.AllocateNumber hand the /Info object's own number back out
            // on the synthetic document, so the next RegisterNew (e.g. SetXmpMetadata's
            // /Metadata stream) silently overwrote /Info.
            trailer.Set(PdfName.Size, PdfNumber.Get(_nextNumber));

            return (trailer, _objects);
        }

        // UTF-16BE with the §7.9.2.2 byte-order mark, matching PdfString.GetText()'s own
        // recognized lexical form — so an arbitrary-Unicode Manuscript.Title round-trips exactly
        // through GetInfo().Title on reopen, not just its Latin-1 subset.
        private static PdfString ToPdfDocString(string text)
        {
            var utf16 = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
            var withBom = new byte[utf16.Length + 2];
            withBom[0] = 0xFE;
            withBom[1] = 0xFF;
            utf16.CopyTo(withBom, 2);
            return PdfString.FromLiteral(withBom);
        }

        private IndirectReference Allocate(PdfObject value)
        {
            var reference = Reserve();
            Set(reference, value);
            return reference;
        }
    }
}
