using System.Text;

namespace PlumePdf.Documents;

/// <summary>
/// Carries <c>/AcroForm</c> through <see cref="Pdf.Merge(PdfDocument[])"/>/<see cref="Pdf.Split"/>
/// — the verified live bug this closes: merging drops
/// <c>/AcroForm</c> entirely today, leaving every widget annotation still referenced from a
/// page's <c>/Annots</c> but with no field tree pointing to it (272 orphaned widgets merging
/// the f1040 fixture with itself). Called from <see cref="PageImporter.Compose"/>, reusing
/// its <see cref="PageGraphCopier"/>'s <c>Reserve</c>/<c>ImportValue</c> so a field object also reachable from
/// a page's <c>/Annots</c> (the merged field+widget case — the norm) gets exactly one copy,
/// not two, in the merged graph.
/// </summary>
internal static class AcroFormMerger
{
    /// <summary>
    /// Builds the merged <c>/AcroForm</c> dictionary for every distinct source document among
    /// <paramref name="pages"/> that has one, or <see langword="null"/> when none does.
    /// Field-array union with deterministic collision renaming (a top-level field's own
    /// <c>/T</c> gets a <c>~N</c> suffix the first time its name repeats — renaming only at
    /// the root is sufficient since every descendant's fully-qualified name is prefixed by
    /// it); <c>/DR</c> categories (<c>/Font</c>, <c>/XObject</c>, ...) are unioned
    /// first-source-wins on a name collision (documented limitation: a later source's
    /// same-named-but-different resource is assumed compatible — true for the overwhelmingly
    /// common case of Standard-14 AcroForm fonts like <c>/Helv</c>/<c>/ZaDb</c>, and avoids
    /// silently breaking a field's own unmodified <c>/DA</c> string, which this pass does not
    /// rewrite); <c>/NeedAppearances</c> is OR'd, <c>/SigFlags</c> bits are OR'd; the
    /// form-level <c>/DA</c> (default appearance string) and <c>/Q</c> (quadding) are carried
    /// from the first source that has one — every merged field inheriting its appearance
    /// defaults from the AcroForm root (rather than setting its own) would otherwise silently
    /// lose them; <c>/CO</c> (calculation order) is unioned across sources, each entry mapped
    /// through the same field-identity <paramref name="reserve"/> the <c>/Fields</c> array
    /// uses, since a <c>/CO</c> entry references a field object already in <c>/Fields</c> —
    /// read and preserved, never executed (calculation order itself is out of scope for this
    /// merge).
    /// </summary>
    public static PdfDictionary? Merge(
        IEnumerable<(ImportSource Source, IndirectReference PageReference, PdfDictionary PageDictionary)> pages,
        Func<ImportSource, PdfObject, PdfObject> importValue,
        Func<ImportSource, IndirectReference, PdfDictionary?, Func<PdfDictionary, PdfDictionary>?, IndirectReference> reserve,
        Func<ImportSource, int, bool> isExcluded)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(importValue);
        ArgumentNullException.ThrowIfNull(reserve);

        var sources = new List<ImportSource>();
        var seenSources = new HashSet<ImportSource>();
        foreach (var (source, _, _) in pages)
        {
            if (seenSources.Add(source))
            {
                sources.Add(source);
            }
        }

        var mergedFields = new PdfArray();
        var mergedDr = new PdfDictionary();
        var mergedCo = new PdfArray();
        var usedTopLevelNames = new HashSet<string>(StringComparer.Ordinal);
        var needAppearances = false;
        var sigFlags = 0;
        PdfObject? mergedDa = null;
        PdfObject? mergedQ = null;
        var found = false;

        // Review critical: unioning each source's ENTIRE /Fields array leaked every field in
        // the document into every split part (Pdf.Split imports ONE page per part). Policy:
        // a field travels when it reaches a widget on an IMPORTED page; a field placed on
        // no page at all (pure-data or orphan-widget field) also travels, because dropping
        // it would silently destroy form data — only fields placed exclusively on
        // NON-imported pages are excluded.
        var importedWidgetsBySource = new Dictionary<ImportSource, HashSet<int>>();
        foreach (var (source, _, pageDictionary) in pages)
        {
            if (!importedWidgetsBySource.TryGetValue(source, out var set))
            {
                importedWidgetsBySource[source] = set = [];
            }

            CollectAnnotNumbers(source, pageDictionary, set);
        }

        var placedWidgetsBySource = new Dictionary<ImportSource, HashSet<int>>();
        foreach (var source in importedWidgetsBySource.Keys)
        {
            placedWidgetsBySource[source] = PlacedWidgets(source);
        }

        foreach (var source in sources)
        {
            if (source.Catalog?.Dictionary.TryGetValue(AcroFormNames.AcroForm, out var acroFormValue) != true
                || AcroFormReader.Resolve(source.Objects, acroFormValue) is not PdfDictionary acroForm)
            {
                continue;
            }

            found = true;

            if (acroForm.TryGetValue(AcroFormNames.NeedAppearances, out var naValue) && naValue is PdfBoolean naFlag && naFlag.Value)
            {
                needAppearances = true;
            }

            if (acroForm.TryGetValue(AcroFormNames.SigFlags, out var sfValue) && sfValue is PdfNumber sfNumber && sfNumber.TryToInt32(out var sfInt))
            {
                sigFlags |= sfInt;
            }

            if (mergedDa is null && acroForm.TryGetValue(AcroFormNames.DA, out var daValue))
            {
                mergedDa = importValue(source, daValue);
            }

            if (mergedQ is null && acroForm.TryGetValue(AcroFormNames.Q, out var qValue))
            {
                mergedQ = importValue(source, qValue);
            }


            if (acroForm.TryGetValue(AcroFormNames.DR, out var drValue) && AcroFormReader.Resolve(source.Objects, drValue) is PdfDictionary drDict)
            {
                MergeResources(mergedDr, source, drDict, importValue);
            }

            if (!acroForm.TryGetValue(AcroFormNames.Fields, out var fieldsValue) || AcroFormReader.Resolve(source.Objects, fieldsValue) is not PdfArray fieldsArray)
            {
                continue;
            }

            var importedWidgets = importedWidgetsBySource.TryGetValue(source, out var importedSet) ? importedSet : [];
            if (!placedWidgetsBySource.ContainsKey(source))
            {
                placedWidgetsBySource[source] = PlacedWidgets(source);
            }

            var includedFieldNumbers = new HashSet<int>();

            foreach (var entry in fieldsArray)
            {
                if (entry is not PdfReference fieldRef || AcroFormReader.Resolve(source.Objects, fieldRef) is not PdfDictionary fieldDict
                    || isExcluded(source, fieldRef.Target.Number))
                {
                    continue; // unreadable, or a field that belonged only to pages not imported
                }

                var reachesImported = FieldReachesImportedWidget(source, fieldRef.Target, fieldDict, importedWidgets, depth: 0, visited: []);
                var placedAnywhere = FieldReachesImportedWidget(source, fieldRef.Target, fieldDict, placedWidgetsBySource[source], depth: 0, visited: []);
                if (!reachesImported && placedAnywhere)
                {
                    continue; // placed exclusively on pages that were NOT imported
                }

                includedFieldNumbers.Add(fieldRef.Target.Number);

                var currentName = fieldDict.TryGetValue(AcroFormNames.T, out var tValue) && tValue is PdfString tString
                    ? tString.GetText()
                    : $"Field{fieldRef.Target.Number}";

                var uniqueName = MakeUnique(currentName, usedTopLevelNames);
                Func<PdfDictionary, PdfDictionary>? transform = uniqueName == currentName
                    ? null
                    : original => RenameTopLevelField(original, uniqueName);

                var newRef = reserve(source, fieldRef.Target, null, transform);
                mergedFields.Add(new PdfReference(newRef));
            }

            // /CO (calculation order) entries reference fields already in /Fields — carry
            // only the ones whose field survived the widget-reachability filter above.
            if (acroForm.TryGetValue(AcroFormNames.CO, out var coValue) && AcroFormReader.Resolve(source.Objects, coValue) is PdfArray coArray)
            {
                foreach (var coEntry in coArray)
                {
                    if (coEntry is PdfReference coFieldRef && includedFieldNumbers.Contains(coFieldRef.Target.Number))
                    {
                        mergedCo.Add(new PdfReference(reserve(source, coFieldRef.Target, null, null)));
                    }
                }
            }
        }

        if (!found)
        {
            return null;
        }

        var merged = new PdfDictionary();
        merged.Set(AcroFormNames.Fields, mergedFields);
        if (mergedDr.Count > 0)
        {
            merged.Set(AcroFormNames.DR, mergedDr);
        }

        if (needAppearances)
        {
            merged.Set(AcroFormNames.NeedAppearances, PdfBoolean.True);
        }

        if (sigFlags != 0)
        {
            merged.Set(AcroFormNames.SigFlags, PdfNumber.Get(sigFlags));
        }

        if (mergedDa is not null)
        {
            merged.Set(AcroFormNames.DA, mergedDa);
        }

        if (mergedQ is not null)
        {
            merged.Set(AcroFormNames.Q, mergedQ);
        }

        if (mergedCo.Count > 0)
        {
            merged.Set(AcroFormNames.CO, mergedCo);
        }

        return merged;
    }

    /// <summary>
    /// Plans how the form fields of one page copied into <paramref name="destination"/> join its
    /// form, with the destination as the base. Mutates nothing: the copies are reserved through
    /// <paramref name="copier"/> into <paramref name="target"/>, and the edits to the destination's
    /// own objects are returned as a <see cref="FormMergePlan"/> for the caller to apply.
    /// </summary>
    /// <remarks>
    /// Only top-level fields with a widget on the copied page come along (unlike Merge, whose
    /// fields placed on no page also travel: page-add copies a page, not a form). The
    /// destination's fields, <c>/DR</c> entries and form-level keys are never changed:
    /// <list type="bullet">
    /// <item>An incoming top-level field whose <c>/T</c> is taken gets a <c>~N</c> suffix.</item>
    /// <item>An incoming <c>/DR</c> resource whose name is taken by a different resource is added
    /// under a fresh <c>~N</c> name, and every copied <c>/DA</c> that names a renamed font is
    /// rewritten to match. A non-embedded simple font with the same <c>/Subtype</c>,
    /// <c>/BaseFont</c> and named <c>/Encoding</c> counts as the same resource and is shared.
    /// <c>/DS</c> and <c>/RV</c> rich text are left as they are.</item>
    /// <item>The source's form-level <c>/DA</c> and <c>/Q</c>, when they differ from the
    /// destination's, are written onto incoming top-level fields that lack their own, so those
    /// fields keep the defaults they inherited.</item>
    /// <item><c>/NeedAppearances</c> is OR'd. <c>/SigFlags</c> gains <c>SignaturesExist</c> when a
    /// signature field comes along (empty: the copier drops its value) and never gains
    /// <c>AppendOnly</c> from the source.</item>
    /// <item>Incoming <c>/CO</c> entries for the fields that came along are appended. <c>/XFA</c>
    /// is left as it is.</item>
    /// </list>
    /// </remarks>
    public static FormMergePlan MergeInto(PdfDocument destination, ImportSource source, IndirectReference pageReference, PdfDictionary pageDictionary, PageGraphCopier copier, IImportTarget target)
    {
        var plan = new FormMergePlan();
        if (source.Catalog?.Dictionary.TryGetValue(AcroFormNames.AcroForm, out var sourceFormValue) != true
            || AcroFormReader.Resolve(source.Objects, sourceFormValue) is not PdfDictionary sourceForm
            || !sourceForm.TryGetValue(AcroFormNames.Fields, out var sourceFieldsValue)
            || AcroFormReader.Resolve(source.Objects, sourceFieldsValue) is not PdfArray sourceFields
            || destination.Catalog is not { } catalog)
        {
            return plan;
        }

        var onPage = new HashSet<int>();
        CollectAnnotNumbers(source, pageDictionary, onPage);
        var incoming = new List<(IndirectReference Reference, PdfDictionary Field)>();
        foreach (var entry in sourceFields)
        {
            if (entry is PdfReference fieldRef && !copier.IsExcluded(source, fieldRef.Target.Number)
                && AcroFormReader.Resolve(source.Objects, fieldRef) is PdfDictionary field
                && FieldReachesImportedWidget(source, fieldRef.Target, field, onPage, depth: 0, visited: []))
            {
                incoming.Add((fieldRef.Target, field));
            }
        }

        if (incoming.Count == 0)
        {
            return plan;
        }

        var objects = destination.Objects;

        // The destination's form, and the indirect object that owns each container the plan
        // edits (null for a container the plan creates, which needs no marking).
        var form = Container<PdfDictionary>(plan, objects, catalog.Dictionary, AcroFormNames.AcroForm, catalog.Reference, out var formOwner, create: () =>
        {
            var created = new PdfDictionary();
            var reference = target.Allocate();
            target.Store(reference, created);
            return (created, new PdfReference(reference));
        });
        var fields = Container<PdfArray>(plan, objects, form, AcroFormNames.Fields, formOwner, out var fieldsOwner);

        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in fields)
        {
            if (AcroFormReader.Resolve(objects, entry) is PdfDictionary existing && existing.TryGetValue(AcroFormNames.T, out var name) && name is PdfString nameString)
            {
                usedNames.Add(nameString.GetText());
            }
        }

        var fontRenames = MergeResourcesInto(plan, objects, form, formOwner, source, sourceForm, copier);

        var sourceDa = FormString(source.Objects, sourceForm, AcroFormNames.DA);
        var inheritedDa = sourceDa is not null && sourceDa != FormString(objects, form, AcroFormNames.DA) ? sourceDa : null;
        var sourceQ = FormInt(source.Objects, sourceForm, AcroFormNames.Q);
        var inheritedQ = sourceQ is not null && sourceQ != FormInt(objects, form, AcroFormNames.Q) ? sourceQ : null;

        var included = new HashSet<int>();
        var signatureFieldCame = false;
        foreach (var (reference, field) in incoming)
        {
            included.Add(reference.Number);
            signatureFieldCame |= HasSignatureField(source, field, depth: 0, visited: []);

            var currentName = field.TryGetValue(AcroFormNames.T, out var tValue) && tValue is PdfString tString ? tString.GetText() : $"Field{reference.Number}";
            var uniqueName = MakeUnique(currentName, usedNames);
            if (uniqueName != currentName)
            {
                plan.Renames.Add((currentName, uniqueName));
            }

            var newReference = copier.Reserve(source, reference, transform: original =>
            {
                var copy = uniqueName == currentName ? Copy(original) : RenameTopLevelField(original, uniqueName);
                if (!copy.ContainsKey(AcroFormNames.DA) && inheritedDa is not null)
                {
                    copy.Set(AcroFormNames.DA, PdfString.FromLiteral(Encoding.Latin1.GetBytes(inheritedDa)));
                }

                if (!copy.ContainsKey(AcroFormNames.Q) && inheritedQ is int q)
                {
                    copy.Set(AcroFormNames.Q, PdfNumber.Get(q));
                }

                return RenameDaFonts(source.Objects, copy, fontRenames);
            });
            plan.Appends.Add((fields, new PdfReference(newReference)));
            MarkOwner(plan, fieldsOwner);

            if (fontRenames.Count > 0)
            {
                ReserveDescendantsWithDa(source, field, copier, fontRenames, depth: 0, visited: [reference.Number]);
            }
        }

        if (sourceForm.TryGetValue(AcroFormNames.CO, out var coValue) && AcroFormReader.Resolve(source.Objects, coValue) is PdfArray sourceCo)
        {
            var carried = sourceCo.OfType<PdfReference>().Where(r => included.Contains(r.Target.Number)).ToList();
            if (carried.Count > 0)
            {
                var co = Container<PdfArray>(plan, objects, form, AcroFormNames.CO, formOwner, out var coOwner);
                foreach (var entry in carried)
                {
                    plan.Appends.Add((co, new PdfReference(copier.Reserve(source, entry.Target))));
                }

                MarkOwner(plan, coOwner);
            }
        }

        if (sourceForm.TryGetValue(AcroFormNames.NeedAppearances, out var na) && AcroFormReader.Resolve(source.Objects, na) is PdfBoolean { Value: true }
            && !(form.TryGetValue(AcroFormNames.NeedAppearances, out var destinationNa) && AcroFormReader.Resolve(objects, destinationNa) is PdfBoolean { Value: true }))
        {
            plan.Sets.Add((form, AcroFormNames.NeedAppearances, PdfBoolean.True));
            MarkOwner(plan, formOwner);
        }

        const int SignaturesExist = 1;
        var flags = FormInt(objects, form, AcroFormNames.SigFlags) ?? 0;
        if (signatureFieldCame && (flags & SignaturesExist) == 0)
        {
            plan.Sets.Add((form, AcroFormNames.SigFlags, PdfNumber.Get(flags | SignaturesExist)));
            MarkOwner(plan, formOwner);
        }

        return plan;
    }

    /// <summary>
    /// The container under <paramref name="key"/> of <paramref name="parent"/>: resolved when it is
    /// there (its owner is the reference it is stored under, or <paramref name="parentOwner"/> when
    /// it is direct), otherwise created and set on the parent by the plan (owner <see langword="null"/>).
    /// </summary>
    private static T Container<T>(FormMergePlan plan, ObjectRegistry objects, PdfDictionary parent, PdfName key, IndirectReference? parentOwner, out IndirectReference? owner, Func<(T Value, PdfObject Stored)>? create = null)
        where T : PdfObject, new()
    {
        if (parent.TryGetValue(key, out var value))
        {
            if (value is PdfReference reference && objects[reference.Target] is T indirect)
            {
                owner = reference.Target;
                return indirect;
            }

            if (value is T direct)
            {
                owner = parentOwner;
                return direct;
            }
        }

        T created;
        PdfObject stored;
        if (create is null)
        {
            created = new T();
            stored = created;
        }
        else
        {
            (created, stored) = create();
        }

        plan.Sets.Add((parent, key, stored));
        MarkOwner(plan, parentOwner);
        owner = null;
        return created;
    }

    private static void MarkOwner(FormMergePlan plan, IndirectReference? owner)
    {
        if (owner is { } reference)
        {
            plan.Dirty.Add(reference);
        }
    }

    /// <summary>
    /// Plans adding the source's <c>/DR</c> resources to the destination's, and returns the fonts
    /// added under a fresh name (old name to new).
    /// </summary>
    private static Dictionary<string, string> MergeResourcesInto(FormMergePlan plan, ObjectRegistry objects, PdfDictionary form, IndirectReference? formOwner, ImportSource source, PdfDictionary sourceForm, PageGraphCopier copier)
    {
        var fontRenames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!sourceForm.TryGetValue(AcroFormNames.DR, out var sourceDrValue) || AcroFormReader.Resolve(source.Objects, sourceDrValue) is not PdfDictionary sourceDr || sourceDr.Count == 0)
        {
            return fontRenames;
        }

        var dr = Container<PdfDictionary>(plan, objects, form, AcroFormNames.DR, formOwner, out var drOwner);
        foreach (var (category, categoryValue) in sourceDr)
        {
            if (AcroFormReader.Resolve(source.Objects, categoryValue) is not PdfDictionary sourceCategory || sourceCategory.Count == 0)
            {
                continue;
            }

            var destinationCategory = Container<PdfDictionary>(plan, objects, dr, category, drOwner, out var categoryOwner);
            var taken = new HashSet<string>(destinationCategory.Select(static e => e.Key.Value), StringComparer.Ordinal);
            foreach (var (name, resource) in sourceCategory)
            {
                string chosen;
                if (!taken.Contains(name.Value))
                {
                    chosen = name.Value;
                }
                else if (ReferenceEquals(category, FontName) && SameStandardFont(objects, destinationCategory[name], source.Objects, resource))
                {
                    continue;
                }
                else
                {
                    var suffix = 1;
                    do
                    {
                        suffix++;
                        chosen = $"{name.Value}~{suffix}";
                    }
                    while (taken.Contains(chosen));

                    plan.ResourceRenames.Add((name.Value, chosen));
                    if (ReferenceEquals(category, FontName))
                    {
                        fontRenames[name.Value] = chosen;
                    }
                }

                taken.Add(chosen);
                plan.Sets.Add((destinationCategory, PdfName.Get(chosen), copier.ImportValue(source, resource)));
                MarkOwner(plan, categoryOwner);
            }
        }

        return fontRenames;
    }

    private static readonly PdfName FontName = PdfName.Get("Font");
    private static readonly PdfName SubtypeName = PdfName.Get("Subtype");
    private static readonly PdfName BaseFontName = PdfName.Get("BaseFont");
    private static readonly PdfName EncodingName = PdfName.Get("Encoding");
    private static readonly PdfName FontDescriptorName = PdfName.Get("FontDescriptor");
    private static readonly PdfName SignatureName = PdfName.Get("Sig");

    // Two non-embedded simple fonts naming the same base font and encoding draw the same glyphs.
    private static bool SameStandardFont(ObjectRegistry destinationObjects, PdfObject destinationValue, ObjectRegistry sourceObjects, PdfObject sourceValue)
    {
        if (AcroFormReader.Resolve(destinationObjects, destinationValue) is not PdfDictionary a || AcroFormReader.Resolve(sourceObjects, sourceValue) is not PdfDictionary b
            || a.ContainsKey(FontDescriptorName) || b.ContainsKey(FontDescriptorName))
        {
            return false;
        }

        return SameName(a, b, SubtypeName) && SameName(a, b, BaseFontName) && SameName(a, b, EncodingName);

        static bool SameName(PdfDictionary a, PdfDictionary b, PdfName key)
        {
            var hasA = a.TryGetValue(key, out var valueA);
            var hasB = b.TryGetValue(key, out var valueB);
            return hasA == hasB && (!hasA || (valueA is PdfName && ReferenceEquals(valueA, valueB)));
        }
    }

    private static string? FormString(ObjectRegistry objects, PdfDictionary form, PdfName key) =>
        form.TryGetValue(key, out var value) && AcroFormReader.Resolve(objects, value) is PdfString text ? Encoding.Latin1.GetString(text.Bytes.Span) : null;

    private static int? FormInt(ObjectRegistry objects, PdfDictionary form, PdfName key) =>
        form.TryGetValue(key, out var value) && AcroFormReader.Resolve(objects, value) is PdfNumber number && number.TryToInt32(out var result) ? result : null;

    private static PdfDictionary Copy(PdfDictionary original)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in original)
        {
            copy.Set(key, value);
        }

        return copy;
    }

    // dictionary (a copy the caller owns) with each renamed font in its /DA renamed.
    private static PdfDictionary RenameDaFonts(ObjectRegistry objects, PdfDictionary dictionary, Dictionary<string, string> fontRenames)
    {
        if (fontRenames.Count == 0 || !dictionary.TryGetValue(AcroFormNames.DA, out var daValue) || AcroFormReader.Resolve(objects, daValue) is not PdfString da)
        {
            return dictionary;
        }

        var text = Encoding.Latin1.GetString(da.Bytes.Span);
        var renamed = DaFontName.Replace(text, match => fontRenames.TryGetValue(match.Groups["name"].Value, out var to) ? "/" + to : match.Value);
        if (renamed != text)
        {
            dictionary.Set(AcroFormNames.DA, PdfString.FromLiteral(Encoding.Latin1.GetBytes(renamed)));
        }

        return dictionary;
    }

    // A name token in a /DA string: '/' then regular characters, up to a delimiter or whitespace.
    private static readonly System.Text.RegularExpressions.Regex DaFontName = new(@"/(?<name>[^\s/\[\]()<>{}%]+)", System.Text.RegularExpressions.RegexOptions.NonBacktracking);

    // Reserves every descendant field or widget of a field that came along whose own /DA names a
    // renamed font, with the rename applied, before the copier reaches it any other way.
    private static void ReserveDescendantsWithDa(ImportSource source, PdfDictionary field, PageGraphCopier copier, Dictionary<string, string> fontRenames, int depth, HashSet<int> visited)
    {
        if (depth > MaxReachabilityDepth || !field.TryGetValue(AcroFormNames.Kids, out var kidsValue) || AcroFormReader.Resolve(source.Objects, kidsValue) is not PdfArray kids)
        {
            return;
        }

        foreach (var kid in kids)
        {
            if (kid is not PdfReference kidRef || !visited.Add(kidRef.Target.Number) || copier.IsExcluded(source, kidRef.Target.Number)
                || AcroFormReader.Resolve(source.Objects, kidRef) is not PdfDictionary kidDict)
            {
                continue;
            }

            if (kidDict.ContainsKey(AcroFormNames.DA))
            {
                copier.Reserve(source, kidRef.Target, transform: original => RenameDaFonts(source.Objects, Copy(original), fontRenames));
            }

            ReserveDescendantsWithDa(source, kidDict, copier, fontRenames, depth + 1, visited);
        }
    }

    private static bool HasSignatureField(ImportSource source, PdfDictionary field, int depth, HashSet<int> visited)
    {
        if (field.TryGetValue(AcroFormNames.FT, out var type) && ReferenceEquals(type, SignatureName))
        {
            return true;
        }

        if (depth > MaxReachabilityDepth || !field.TryGetValue(AcroFormNames.Kids, out var kidsValue) || AcroFormReader.Resolve(source.Objects, kidsValue) is not PdfArray kids)
        {
            return false;
        }

        foreach (var kid in kids)
        {
            if (kid is PdfReference kidRef && visited.Add(kidRef.Target.Number) && !field.ContainsKey(AcroFormNames.FT)
                && AcroFormReader.Resolve(source.Objects, kidRef) is PdfDictionary kidDict && HasSignatureField(source, kidDict, depth + 1, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static string MakeUnique(string name, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        var suffix = 1;
        string candidate;
        do
        {
            suffix++;
            candidate = $"{name}~{suffix}";
        }
        while (!used.Add(candidate));

        return candidate;
    }

    private static PdfDictionary RenameTopLevelField(PdfDictionary original, string newName)
    {
        var copy = new PdfDictionary();
        var renamed = false;
        foreach (var (key, value) in original)
        {
            if (ReferenceEquals(key, AcroFormNames.T))
            {
                copy.Set(key, EncodeUtf16BeString(newName));
                renamed = true;
            }
            else
            {
                copy.Set(key, value);
            }
        }

        if (!renamed)
        {
            copy.Set(AcroFormNames.T, EncodeUtf16BeString(newName));
        }

        return copy;
    }

    private static void MergeResources(PdfDictionary mergedDr, ImportSource source, PdfDictionary sourceDr, Func<ImportSource, PdfObject, PdfObject> importValue)
    {
        foreach (var (categoryKey, categoryValue) in sourceDr)
        {
            if (AcroFormReader.Resolve(source.Objects, categoryValue) is not PdfDictionary sourceCategoryDict)
            {
                continue;
            }

            var mergedCategoryDict = mergedDr.TryGetValue(categoryKey, out var existingCategory) && existingCategory is PdfDictionary existing
                ? existing
                : new PdfDictionary();

            foreach (var (resourceName, resourceValue) in sourceCategoryDict)
            {
                // First-source-wins on a name collision (see Merge's remarks): never silently
                // drops a resource, never rewrites a field's own /DA to a renamed name.
                if (!mergedCategoryDict.ContainsKey(resourceName))
                {
                    mergedCategoryDict.Set(resourceName, importValue(source, resourceValue));
                }
            }

            mergedDr.Set(categoryKey, mergedCategoryDict);
        }
    }

    private static PdfString EncodeUtf16BeString(string text)
    {
        var encoded = Encoding.BigEndianUnicode.GetBytes(text);
        var withBom = new byte[encoded.Length + 2];
        withBom[0] = 0xFE;
        withBom[1] = 0xFF;
        encoded.CopyTo(withBom, 2);
        return PdfString.FromLiteral(withBom);
    }

    private static void CollectAnnotNumbers(ImportSource source, PdfDictionary pageDictionary, HashSet<int> into)
    {
        if (pageDictionary.TryGetValue(AcroFormNames.Annots, out var annotsValue)
            && AcroFormReader.Resolve(source.Objects, annotsValue) is PdfArray annots)
        {
            foreach (var entry in annots)
            {
                if (entry is PdfReference annotRef)
                {
                    into.Add(annotRef.Target.Number);
                }
            }
        }
    }

    private const int MaxReachabilityDepth = 64;

    /// <summary>
    /// Whether <paramref name="fieldDict"/> (or any of its <c>/Kids</c>, recursively and
    /// cycle-guarded) is itself a widget on an imported page — i.e. its object number appears
    /// in an imported page's <c>/Annots</c>. Merged field+widget dictionaries are the common
    /// case; a parent field qualifies through any qualifying leaf.
    /// </summary>
    private static bool FieldReachesImportedWidget(ImportSource source, IndirectReference reference, PdfDictionary fieldDict, HashSet<int> importedWidgets, int depth, HashSet<int> visited)
    {
        if (depth > MaxReachabilityDepth || !visited.Add(reference.Number))
        {
            return false;
        }

        if (importedWidgets.Contains(reference.Number))
        {
            return true;
        }

        if (fieldDict.TryGetValue(AcroFormNames.Kids, out var kidsValue)
            && AcroFormReader.Resolve(source.Objects, kidsValue) is PdfArray kids)
        {
            foreach (var kid in kids)
            {
                if (kid is PdfReference kidRef
                    && AcroFormReader.Resolve(source.Objects, kidRef) is PdfDictionary kidDict
                    && FieldReachesImportedWidget(source, kidRef.Target, kidDict, importedWidgets, depth + 1, visited))
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Widgets placed on any page the source was opened with — not just the pages it has now,
    // so a field whose page was removed before the merge counts as placed there (and does not
    // travel), rather than as placed nowhere.
    private static HashSet<int> PlacedWidgets(ImportSource source)
    {
        var placed = new HashSet<int>();
        foreach (var reference in source.OpenTimePages)
        {
            if (source.Objects[reference] is PdfDictionary page)
            {
                CollectAnnotNumbers(source, page, placed);
            }
        }

        return placed;
    }
}

/// <summary>
/// The edits <see cref="AcroFormMerger.MergeInto"/> plans for the destination's own objects:
/// values to set on dictionaries and items to append to arrays (the destination's, or new ones
/// the plan creates), and the destination objects that must be marked dirty once they're
/// applied. Values may hold provisional references; <see cref="Rewrite"/> replaces them.
/// </summary>
internal sealed class FormMergePlan
{
    /// <summary>Values to set, in order.</summary>
    public List<(PdfDictionary Dictionary, PdfName Key, PdfObject Value)> Sets { get; } = [];

    /// <summary>Items to append, in order.</summary>
    public List<(PdfArray Array, PdfObject Item)> Appends { get; } = [];

    /// <summary>The destination's indirect objects the edits change.</summary>
    public HashSet<IndirectReference> Dirty { get; } = [];

    /// <summary>Incoming top-level fields renamed because their name was taken: (from, to).</summary>
    public List<(string From, string To)> Renames { get; } = [];

    /// <summary>Incoming <c>/DR</c> resources added under a fresh name because theirs was taken: (from, to).</summary>
    public List<(string From, string To)> ResourceRenames { get; } = [];

    /// <summary>Replaces every value and item with <paramref name="rewrite"/>'s result.</summary>
    public void Rewrite(Func<PdfObject, PdfObject> rewrite)
    {
        for (var i = 0; i < Sets.Count; i++)
        {
            Sets[i] = Sets[i] with { Value = rewrite(Sets[i].Value) };
        }

        for (var i = 0; i < Appends.Count; i++)
        {
            Appends[i] = Appends[i] with { Item = rewrite(Appends[i].Item) };
        }
    }

    /// <summary>Applies the edits. Never throws for a plan <see cref="AcroFormMerger.MergeInto"/> built.</summary>
    public void Apply()
    {
        foreach (var (dictionary, key, value) in Sets)
        {
            dictionary.Set(key, value);
        }

        foreach (var (array, item) in Appends)
        {
            array.Add(item);
        }
    }
}
