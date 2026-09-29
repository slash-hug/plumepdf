using System.Text;

namespace PlumePdf.Documents;

/// <summary>
/// Carries <c>/AcroForm</c> through <see cref="Pdf.Merge(PdfDocument[])"/>/<see cref="Pdf.Split"/>
/// — the verified live bug this closes: merging drops
/// <c>/AcroForm</c> entirely today, leaving every widget annotation still referenced from a
/// page's <c>/Annots</c> but with no field tree pointing to it (272 orphaned widgets merging
/// the f1040 fixture with itself). Called from <see cref="PageImporter.Compose"/>, reusing
/// its exact <c>Reserve</c>/<c>ImportValue</c> closures so a field object also reachable from
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
