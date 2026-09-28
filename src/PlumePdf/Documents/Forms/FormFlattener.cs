using System.Globalization;
using System.Text;
using PlumePdf.Documents.Forms;
using PlumePdf.Documents.Forms.Appearances;
using PlumePdf.Objects;
using PlumePdf.Raster;

namespace PlumePdf.Documents;

/// <summary>
/// Flattens a document's interactive form (ISO 32000-1 §12.7.3.3's "flatten"
/// operation): stamps each widget's normal appearance into its page's content via a Form
/// XObject reference, then drops the widgets from <c>/Annots</c> and the fields/
/// <c>/AcroForm</c> entirely, producing a document with no interactive form left at all.
/// </summary>
/// <remarks>
/// Builds a new, independent document using the same "local object-number map + explicit
/// work queue" technique <see cref="PageImporter"/> uses for <see cref="Pdf.Merge(PdfDocument[])"/>/
/// <see cref="Pdf.Split"/> (single source document here, not a multi-document merge) — see
/// <see cref="PdfForm.Flatten"/>'s remarks for why a new document, not an in-place mutation,
/// is the only shape available without a general object-number allocator on an
/// already-open document's <see cref="ObjectRegistry"/>.
/// </remarks>
internal static class FormFlattener
{
    private static readonly PdfName PagesName = PdfName.Get("Pages");
    private static readonly PdfName CatalogName = PdfName.Get("Catalog");
    private static readonly PdfName KidsName = AcroFormNames.Kids;
    private static readonly PdfName CountName = PdfName.Get("Count");
    private static readonly PdfName ParentName = AcroFormNames.Parent;

    public static PdfDocument Flatten(PdfDocument document, AcroFormReadResult form)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(form);

        if (document.HasEncryptedSource)
        {
            throw new PlumePdfException("PLUME6012", "Form.Flatten cannot compose pages from a document opened from an encrypted source (Phase 1 does not support encryption write).");
        }

        // The usage-rights/permission-bit rulings apply to Flatten exactly as to Fill:
        // flattening destroys usage-rights grants and signatures outright, so the advisory
        // diagnostics fire here too.
        FormFiller.ApplyUsageRightsDiagnostic(document, isFlatten: true);
        FormFiller.ApplyPermissionBitDiagnostic(document);

        var merged = new Dictionary<int, PdfObject>();
        var map = new Dictionary<int, int>();
        var pending = new Queue<int>();
        var nextNumber = 1;
        var diagnostics = document.Diagnostics;
        var options = document.Options;

        // Implements the "generated or pre-existing" text rule: one
        // scratch registry for the whole flatten call — generation writes never touch the
        // source document's own registry, exactly the posture PageRasterAdapter's
        // render-time synthesis uses. Objects the generator allocates live at numbers >=
        // the seed (ScratchObjectRegistry.CreateFor extends strictly upward from it), which
        // is how ImportValue below tells a scratch reference from a source-document one.
        var synthesis = BuildSynthesisContext(document, form);
        var scratchImports = new Dictionary<int, int>();

        int Reserve(IndirectReference original)
        {
            if (map.TryGetValue(original.Number, out var existing))
            {
                return existing;
            }

            var assigned = nextNumber++;
            map[original.Number] = assigned;
            pending.Enqueue(original.Number);
            return assigned;
        }

        int AllocateExtra(PdfObject value)
        {
            var assigned = nextNumber++;
            merged[assigned] = value;
            return assigned;
        }

        PdfObject ImportValue(PdfObject value)
        {
            switch (value)
            {
                case PdfReference reference:
                    // A synthesized appearance (or the shared dingbats font dictionary it may
                    // reference) lives only in the scratch registry — resolve it there and
                    // import its body directly (memoized, so the shared font imports once);
                    // the drain loop below reads document.Objects, which cannot see it.
                    if (synthesis is not null && reference.Target.Number >= synthesis.ScratchSeed)
                    {
                        if (!scratchImports.TryGetValue(reference.Target.Number, out var assignedScratch))
                        {
                            assignedScratch = nextNumber++;
                            scratchImports[reference.Target.Number] = assignedScratch;
                            merged[assignedScratch] = ImportValue(synthesis.Scratch[reference.Target]);
                        }

                        return new PdfReference(new IndirectReference(assignedScratch, 0));
                    }

                    return new PdfReference(new IndirectReference(Reserve(reference.Target), 0));

                case PdfArray array:
                    var newArray = new PdfArray();
                    foreach (var item in array)
                    {
                        newArray.Add(ImportValue(item));
                    }

                    return newArray;

                case PdfDictionary dict:
                    var newDict = new PdfDictionary();
                    foreach (var (key, item) in dict)
                    {
                        newDict.Set(key, ImportValue(item));
                    }

                    return newDict;

                case PdfStream stream:
                    var newStreamDict = new PdfDictionary();
                    foreach (var (key, item) in stream.Dictionary)
                    {
                        newStreamDict.Set(key, ImportValue(item));
                    }

                    return new PdfStream(newStreamDict, stream.RawBytes);

                default:
                    return value;
            }
        }

        var catalogNumber = nextNumber++;
        var pagesNumber = nextNumber++;
        var kidsReferences = new List<IndirectReference>();

        // Pre-map every original page's object number to its flattened replacement's number
        // (and the original catalog/pages root to theirs) BEFORE any ImportValue runs: an
        // imported object that references a page (an annotation /Dest, a field /P, an
        // outline target) must resolve to the flattened page — without this, Reserve would
        // import the ORIGINAL page dictionary as a shadow copy, un-flattened widgets and all
        // (review critical).
        var newPageNumbers = new List<int>(document.Pages.Count);
        foreach (var page in document.Pages)
        {
            var assigned = nextNumber++;
            newPageNumbers.Add(assigned);
            map[page.Reference.Number] = assigned;
        }

        if (document.Catalog is { } catalogForMap)
        {
            map[catalogForMap.Reference.Number] = catalogNumber;
            if (catalogForMap.Dictionary.TryGetValue(PagesName, out var pagesRootValue) && pagesRootValue is PdfReference pagesRoot)
            {
                map[pagesRoot.Target.Number] = pagesNumber;
            }
        }

        var pageIndex = 0;
        foreach (var page in document.Pages)
        {
            var widgets = WidgetAnnotationReader.ReadPageWidgets(document.Objects, page.Dictionary, options, diagnostics);
            var stamps = new List<(PdfObject Appearance, PdfArray Rect)>();
            var keepLiveWidgets = new HashSet<int>();

            foreach (var widget in widgets)
            {
                if (TryResolveAppearance(document, widget.Dictionary, synthesis, options, diagnostics, out var appearance))
                {
                    if (appearance is not null
                        && widget.Dictionary.TryGetValue(AcroFormNames.Rect, out var rectValue) && rectValue is PdfArray rectArray)
                    {
                        stamps.Add((appearance, rectArray));
                    }

                    // appearance is null with a true result: the widget's /AS legitimately
                    // resolves to no drawable state (e.g. an unchecked checkbox with only an
                    // "on" appearance defined) — nothing to stamp, not a refusal.
                }
                else
                {
                    // A widget with no /AP and nothing synthesizable degrades
                    // per-widget — it stays a live annotation on the flattened page (the
                    // diagnostic was recorded inside TryResolveAppearance) instead of
                    // aborting the whole document's flatten.
                    keepLiveWidgets.Add(widget.Reference.Number);
                }
            }

            var newPageDict = new PdfDictionary();
            foreach (var (key, value) in page.Dictionary)
            {
                if (ReferenceEquals(key, ParentName) || ReferenceEquals(key, AcroFormNames.Annots))
                {
                    continue;
                }

                newPageDict.Set(key, ImportValue(value));
            }

            var keptAnnots = BuildKeptAnnotsArray(document.Objects, page.Dictionary, widgets, keepLiveWidgets, ImportValue);
            if (keptAnnots is not null)
            {
                newPageDict.Set(AcroFormNames.Annots, keptAnnots);
            }

            if (stamps.Count > 0)
            {
                // A synthesized appearance's reference resolves only through the scratch
                // registry (which reads through to the real document for everything else),
                // so stamping resolves via it whenever synthesis is in play.
                StampAppearances(document, synthesis?.Scratch ?? document.Objects, page.Dictionary, newPageDict, stamps, ImportValue, AllocateExtra);
            }

            newPageDict.Set(ParentName, new PdfReference(new IndirectReference(pagesNumber, 0)));

            var pageNumber = newPageNumbers[pageIndex++];
            merged[pageNumber] = newPageDict;
            kidsReferences.Add(new IndirectReference(pageNumber, 0));
        }

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PagesName);
        pagesDict.Set(KidsName, new PdfArray(kidsReferences.ConvertAll(static r => (PdfObject)new PdfReference(r))));
        pagesDict.Set(CountName, PdfNumber.Get(kidsReferences.Count));
        merged[pagesNumber] = pagesDict;

        var catalogDict = new PdfDictionary();
        if (document.Catalog is { } sourceCatalog)
        {
            foreach (var (key, value) in sourceCatalog.Dictionary)
            {
                if (ReferenceEquals(key, PagesName) || ReferenceEquals(key, AcroFormNames.AcroForm))
                {
                    continue;
                }

                catalogDict.Set(key, ImportValue(value));
            }
        }

        catalogDict.Set(PdfName.Type, CatalogName);
        catalogDict.Set(PagesName, new PdfReference(new IndirectReference(pagesNumber, 0)));
        merged[catalogNumber] = catalogDict;

        // Drain AFTER every ImportValue caller (pages, stamps, kept annots, AND the catalog):
        // importing an object can Reserve more, so this loop must be the last consumer — a
        // queue drained before the catalog import silently dropped every catalog-only-
        // reachable object and left dangling references resolving to null (review critical).
        // A page number pre-mapped above never enters `pending`, so this cannot re-import an
        // original page as a shadow.
        while (pending.Count > 0)
        {
            var originalNumber = pending.Dequeue();
            merged[map[originalNumber]] = ImportValue(document.Objects[new IndirectReference(originalNumber, 0)]);
        }

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(nextNumber));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(catalogNumber, 0)));

        return PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, merged), new DiagnosticCollection());
    }

    private static PdfArray? BuildKeptAnnotsArray(ObjectRegistry objects, PdfDictionary pageDictionary, IReadOnlyList<PageWidget> widgets, IReadOnlySet<int> keepLiveWidgets, Func<PdfObject, PdfObject> importValue)
    {
        if (!pageDictionary.TryGetValue(AcroFormNames.Annots, out var annotsValue) || AcroFormReader.Resolve(objects, annotsValue) is not PdfArray annots)
        {
            return null;
        }

        var widgetNumbers = new HashSet<int>(widgets.Count);
        foreach (var widget in widgets)
        {
            // A widget that could neither stamp nor synthesize an appearance
            // is NOT dropped — it survives as a live annotation on the flattened page.
            if (!keepLiveWidgets.Contains(widget.Reference.Number))
            {
                widgetNumbers.Add(widget.Reference.Number);
            }
        }

        var kept = new PdfArray();
        foreach (var entry in annots)
        {
            if (entry is PdfReference reference && widgetNumbers.Contains(reference.Target.Number))
            {
                continue;
            }

            kept.Add(importValue(entry));
        }

        return kept;
    }

    /// <summary>
    /// Resolves the appearance to stamp for a widget's current <c>/AS</c> state. Returns
    /// <see langword="true"/> with a <see langword="null"/> appearance when the widget's
    /// <c>/AP</c> legitimately has nothing to draw for the current state (e.g. an unchecked
    /// checkbox); returns <see langword="false"/> — with a <c>PLUME6036</c> Warning recorded —
    /// only when the widget has no <c>/AP</c> at all AND nothing could be synthesized from its
    /// field value, in which case the caller leaves it as a live annotation (a
    /// per-widget degradation) instead of aborting the whole flatten.
    /// </summary>
    private static bool TryResolveAppearance(PdfDocument document, PdfDictionary widgetDictionary, SynthesisContext? synthesis, PdfOptions options, DiagnosticCollection diagnostics, out PdfObject? appearance)
    {
        appearance = null;
        var objects = document.Objects;

        // Flatten always requires a normal appearance
        // stream, GENERATED or pre-existing — so a widget with no /AP first gets the same
        // AppearanceGenerator synthesis Fill and Phase-9 Rasterize already use (against the
        // flatten call's scratch registry; the source document is never mutated). Only a
        // widget with no /AP AND nothing generatable (no field, no usable value) degrades:
        // a PLUME6036 Warning, and the widget stays live rather than failing the document.
        if (!widgetDictionary.TryGetValue(AcroFormNames.AP, out var apValue) || AcroFormReader.Resolve(objects, apValue) is not PdfDictionary apDict
            || !apDict.TryGetValue(AcroFormNames.N, out var nValue))
        {
            if (synthesis is not null && TrySynthesizeAppearance(document, widgetDictionary, synthesis, options, diagnostics, out var generated))
            {
                appearance = new PdfReference(generated);
                return true;
            }

            diagnostics.Add(new PdfDiagnostic(
                "PLUME6036",
                DiagnosticSeverity.Warning,
                "A widget has no /AP (normal appearance) and no appearance could be synthesized from its field value; it was left as a live annotation instead of being flattened (the rest of the document still flattens)."));
            return false;
        }

        var resolvedN = AcroFormReader.Resolve(objects, nValue);
        if (resolvedN is PdfStream)
        {
            appearance = nValue;
            return true;
        }

        if (resolvedN is PdfDictionary stateDict)
        {
            var stateName = widgetDictionary.TryGetValue(AcroFormNames.AS, out var asValue) && asValue is PdfName asName ? asName : AcroFormNames.Off;
            if (stateDict.TryGetValue(stateName, out var stateValue) && AcroFormReader.Resolve(objects, stateValue) is PdfStream)
            {
                appearance = stateValue;
                return true;
            }

            // No drawable appearance for the current state (e.g. Off) — valid, draw nothing.
            return true;
        }

        diagnostics.Add(new PdfDiagnostic("PLUME6040", DiagnosticSeverity.Warning, "A widget's /AP /N did not resolve to a stream or a state dictionary; treating it as having nothing to stamp."));
        return true;
    }

    /// <summary>
    /// Everything one flatten call's appearance synthesis needs, built once up front:
    /// the field tree indexed by widget-dictionary identity (reference equality —
    /// every widget dictionary the page walk hands us was resolved through the same
    /// <c>document.Objects</c> cache <see cref="AcroFormReader.Read"/> read the field tree
    /// through), the AcroForm's <c>/DR</c> resources and form-level <c>/DA</c>, and the
    /// scratch registry (seeded past every object number the source document uses — that seed
    /// is also how <c>ImportValue</c> recognizes a scratch reference) all generation writes go
    /// to, so the source document is never mutated (the same posture
    /// <c>PageRasterAdapter.BuildWidgetAppearanceResolver</c> established).
    /// </summary>
    private sealed record SynthesisContext(
        ObjectRegistry Scratch,
        int ScratchSeed,
        AppearanceGenerator Generator,
        Dictionary<PdfDictionary, AcroFormField> WidgetToField,
        PdfDictionary DrResources,
        string? FormDefaultAppearance);

    private static SynthesisContext? BuildSynthesisContext(PdfDocument document, AcroFormReadResult form)
    {
        if (form.Fields.Count == 0)
        {
            return null;
        }

        var widgetToField = new Dictionary<PdfDictionary, AcroFormField>(ReferenceEqualityComparer.Instance);
        foreach (var field in form.Fields)
        {
            foreach (var (_, widgetDictionary) in field.Widgets)
            {
                widgetToField[widgetDictionary] = field;
            }
        }

        if (widgetToField.Count == 0)
        {
            return null;
        }

        var acroFormDictionary = form.AcroFormDictionary;
        var resources = FormFiller.ResolveDictionary(document, acroFormDictionary is not null && acroFormDictionary.TryGetValue(AcroFormNames.DR, out var drValue) ? drValue : null) ?? new PdfDictionary();
        var formDefaultAppearance = FormFiller.ReadDaString(document, acroFormDictionary is not null && acroFormDictionary.TryGetValue(AcroFormNames.DA, out var formDa) ? formDa : null);

        var scratchSeed = document.Objects.NextFreshObjectNumber;
        var scratch = ScratchObjectRegistry.CreateFor(document.Objects, scratchSeed);
        return new SynthesisContext(scratch, scratchSeed, new AppearanceGenerator(scratch), widgetToField, resources, formDefaultAppearance);
    }

    /// <summary>
    /// Synthesizes a missing normal appearance for <paramref name="widgetDictionary"/> from its
    /// field's value and effective <c>/DA</c> chain — the exact
    /// <see cref="FormFiller.BuildFieldValue"/>/<see cref="FormFiller.IsWidgetOn"/>/
    /// <see cref="FormFiller.ReadDaString"/> recipe Fill and Phase-9 Rasterize already use, so
    /// all three routes to <see cref="AppearanceGenerator"/> agree. Returns
    /// <see langword="false"/> (no diagnostic of its own beyond any the generator recorded)
    /// when the widget belongs to no known field, has no paintable value (e.g. an off-state
    /// button), or generation itself fails — the caller then degrades the widget to a live annotation.
    /// </summary>
    private static bool TrySynthesizeAppearance(PdfDocument document, PdfDictionary widgetDictionary, SynthesisContext synthesis, PdfOptions options, DiagnosticCollection diagnostics, out IndirectReference generated)
    {
        generated = default;

        if (!synthesis.WidgetToField.TryGetValue(widgetDictionary, out var field))
        {
            return false; // Not a widget the document's own field tree recognizes — nothing to synthesize from.
        }

        var fieldValue = FormFiller.BuildFieldValue(field);
        if (fieldValue is null || (fieldValue.Kind == FormFieldKind.Button && !FormFiller.IsWidgetOn(widgetDictionary)))
        {
            return false; // An off-state button widget, or a field kind with no paintable synthesized state.
        }

        var effectiveDa = FormFiller.ReadDaString(document, widgetDictionary.TryGetValue(AcroFormNames.DA, out var widgetDa) ? widgetDa : null)
            ?? FormFiller.ReadDaString(document, field.Dictionary.TryGetValue(AcroFormNames.DA, out var fieldDa) ? fieldDa : null)
            ?? synthesis.FormDefaultAppearance
            ?? string.Empty;

        try
        {
            generated = synthesis.Generator.GenerateAppearance(fieldValue, widgetDictionary, effectiveDa, synthesis.DrResources, synthesis.Scratch, options, diagnostics);
            return true;
        }
        catch (PlumePdfException ex)
        {
            diagnostics.Add(new PdfDiagnostic(
                ex.Code,
                DiagnosticSeverity.Warning,
                $"An appearance for field '{field.FullyQualifiedName}' could not be synthesized during flatten: {ex.Message}"));
            return false;
        }
    }

    private static void StampAppearances(PdfDocument document, ObjectRegistry resolveObjects, PdfDictionary originalPageDictionary, PdfDictionary newPageDict, List<(PdfObject Appearance, PdfArray Rect)> stamps, Func<PdfObject, PdfObject> importValue, Func<PdfObject, int> allocateExtra)
    {
        var xobjectNames = new PdfDictionary();
        var operators = new StringBuilder();

        var index = 0;
        foreach (var (appearance, rect) in stamps)
        {
            index++;
            var name = PdfName.Get($"FXFlatten{index}");
            var appearanceReference = appearance is PdfReference r
                ? (PdfObject)importValue(r)
                : new PdfReference(new IndirectReference(allocateExtra(importValue(appearance)), 0));

            xobjectNames.Set(name, appearanceReference);

            if (!TryReadRect(rect, out var rx0, out var ry0, out var rx1, out var ry1))
            {
                continue;
            }

            var resolvedAppearance = AcroFormReader.Resolve(resolveObjects, appearance) as PdfStream;
            var (bbox0, bbox1, bbox2, bbox3) = ReadBBox(resolvedAppearance?.Dictionary);
            var (ma, mb, mc, md, me, mf) = ReadMatrix(resolvedAppearance?.Dictionary);

            var (tx0, ty0) = TransformPoint(ma, mb, mc, md, me, mf, bbox0, bbox1);
            var (tx1, ty1) = TransformPoint(ma, mb, mc, md, me, mf, bbox2, bbox3);
            var (tx2, ty2) = TransformPoint(ma, mb, mc, md, me, mf, bbox0, bbox3);
            var (tx3, ty3) = TransformPoint(ma, mb, mc, md, me, mf, bbox2, bbox1);

            var minX = Math.Min(Math.Min(tx0, tx1), Math.Min(tx2, tx3));
            var maxX = Math.Max(Math.Max(tx0, tx1), Math.Max(tx2, tx3));
            var minY = Math.Min(Math.Min(ty0, ty1), Math.Min(ty2, ty3));
            var maxY = Math.Max(Math.Max(ty0, ty1), Math.Max(ty2, ty3));

            var transformedWidth = maxX - minX;
            var transformedHeight = maxY - minY;

            var rectLeft = Math.Min(rx0, rx1);
            var rectBottom = Math.Min(ry0, ry1);
            var rectWidth = Math.Abs(rx1 - rx0);
            var rectHeight = Math.Abs(ry1 - ry0);

            var sx = transformedWidth is > -1e-9 and < 1e-9 ? 1 : rectWidth / transformedWidth;
            var sy = transformedHeight is > -1e-9 and < 1e-9 ? 1 : rectHeight / transformedHeight;
            var tx = rectLeft - (minX * sx);
            var ty = rectBottom - (minY * sy);

            operators.Append(
                $"q {sx.ToString("F4", CultureInfo.InvariantCulture)} 0 0 {sy.ToString("F4", CultureInfo.InvariantCulture)} " +
                $"{tx.ToString("F4", CultureInfo.InvariantCulture)} {ty.ToString("F4", CultureInfo.InvariantCulture)} cm /{name.Value} Do Q\n");
        }

        // Deliberately rebuilt fresh from the *original* page's /Resources rather than read
        // back out of newPageDict: several pages commonly share one indirect /Resources
        // object, and ImportValue's Reserve-based dedup means newPageDict's copy of a shared
        // /Resources is itself just a reference to one shared new dictionary — mutating that
        // in place to add this page's own "FXFlattenN" XObject names would silently leak (and
        // on a name collision, overwrite) another page's stamps. A page-local copy sidesteps
        // that entirely; the /XObject sub-dictionary's own entries (e.g. shared images) are
        // still deep-copied through the same dedup, which is correct at that level.
        var sourceResources = originalPageDictionary.TryGetValue(AcroFormNames.Resources, out var resourcesValue)
            ? AcroFormReader.Resolve(document.Objects, resourcesValue) as PdfDictionary
            : null;

        var resources = new PdfDictionary();
        if (sourceResources is not null)
        {
            foreach (var (key, value) in sourceResources)
            {
                resources.Set(key, importValue(value));
            }
        }

        var sourceXObjects = sourceResources is not null && sourceResources.TryGetValue(AcroFormNames.XObject, out var xobjectsValue)
            ? AcroFormReader.Resolve(document.Objects, xobjectsValue) as PdfDictionary
            : null;

        var xobjectDict = new PdfDictionary();
        if (sourceXObjects is not null)
        {
            foreach (var (key, value) in sourceXObjects)
            {
                xobjectDict.Set(key, importValue(value));
            }
        }

        foreach (var (key, value) in xobjectNames)
        {
            xobjectDict.Set(key, value);
        }

        resources.Set(AcroFormNames.XObject, xobjectDict);
        newPageDict.Set(AcroFormNames.Resources, resources);

        var operatorsText = operators.ToString();
        var originalBytes = DecodePageContent(document, originalPageDictionary);

        // The original content may end with a dangling graphics-state change (unbalanced cm,
        // color, clip …); wrap it in q/Q so the stamp operators always start from the default
        // state — otherwise the baked-in appearance inherits whatever the page left behind
        // (measured drift occurs when the page ends inside a transformed state).
        var prefix = "q\n"u8;
        var infix = "\nQ\n"u8;
        var combined = new byte[prefix.Length + originalBytes.Length + infix.Length + Encoding.ASCII.GetByteCount(operatorsText)];
        prefix.CopyTo(combined);
        originalBytes.CopyTo(combined, prefix.Length);
        infix.CopyTo(combined.AsSpan(prefix.Length + originalBytes.Length));
        Encoding.ASCII.GetBytes(operatorsText, 0, operatorsText.Length, combined, prefix.Length + originalBytes.Length + infix.Length);

        var contentDict = new PdfDictionary();
        var contentStream = new PdfStream(contentDict, combined);
        var contentNumber = allocateExtra(contentStream);
        newPageDict.Set(AcroFormNames.Contents, new PdfReference(new IndirectReference(contentNumber, 0)));
    }

    private static byte[] DecodePageContent(PdfDocument document, PdfDictionary pageDictionary)
    {
        if (!pageDictionary.TryGetValue(AcroFormNames.Contents, out var contentsValue))
        {
            return [];
        }

        var resolved = AcroFormReader.Resolve(document.Objects, contentsValue);
        if (resolved is PdfStream single)
        {
            return single.GetDecodedBytes(document.Options.Filters, document.Options, r => document.Objects[r]);
        }

        if (resolved is PdfArray array)
        {
            var buffer = new List<byte>();
            foreach (var entry in array)
            {
                if (AcroFormReader.Resolve(document.Objects, entry) is PdfStream part)
                {
                    if (buffer.Count > 0)
                    {
                        buffer.Add((byte)'\n');
                    }

                    buffer.AddRange(part.GetDecodedBytes(document.Options.Filters, document.Options, r => document.Objects[r]));
                }
            }

            return [.. buffer];
        }

        return [];
    }

    private static bool TryReadRect(PdfArray rect, out double x0, out double y0, out double x1, out double y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (rect.Count != 4
            || rect[0] is not PdfNumber a || rect[1] is not PdfNumber b || rect[2] is not PdfNumber c || rect[3] is not PdfNumber d)
        {
            return false;
        }

        x0 = a.Value;
        y0 = b.Value;
        x1 = c.Value;
        y1 = d.Value;
        return true;
    }

    private static (double, double, double, double) ReadBBox(PdfDictionary? dict)
    {
        if (dict is not null && dict.TryGetValue(AcroFormNames.BBox, out var bboxValue) && bboxValue is PdfArray bbox && bbox.Count == 4
            && bbox[0] is PdfNumber a && bbox[1] is PdfNumber b && bbox[2] is PdfNumber c && bbox[3] is PdfNumber d)
        {
            return (a.Value, b.Value, c.Value, d.Value);
        }

        return (0, 0, 1, 1);
    }

    private static (double, double, double, double, double, double) ReadMatrix(PdfDictionary? dict)
    {
        if (dict is not null && dict.TryGetValue(AcroFormNames.Matrix, out var matrixValue) && matrixValue is PdfArray matrix && matrix.Count == 6
            && matrix[0] is PdfNumber a && matrix[1] is PdfNumber b && matrix[2] is PdfNumber c && matrix[3] is PdfNumber d && matrix[4] is PdfNumber e && matrix[5] is PdfNumber f)
        {
            return (a.Value, b.Value, c.Value, d.Value, e.Value, f.Value);
        }

        return (1, 0, 0, 1, 0, 0);
    }

    private static (double, double) TransformPoint(double a, double b, double c, double d, double e, double f, double x, double y) =>
        ((a * x) + (c * y) + e, (b * x) + (d * y) + f);
}
