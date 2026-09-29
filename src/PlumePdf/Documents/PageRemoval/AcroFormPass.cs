using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// The form: widgets on removed pages leave their field's /Kids; a field left with no widget is
/// removed from its parent or from /AcroForm /Fields, value included, recursively upward; radio
/// and checkbox /Opt entries stay aligned with /Kids and a /V naming a removed widget's
/// appearance state becomes /Off; /CO drops removed fields; /XFA is dropped when anything was
/// removed; removed fields (and their indirect /V and /RV) are excluded, and /Perms /DocMDP is
/// dropped when its signature was.
/// </summary>
/// <remarks>
/// Every change is a copy: a changed field becomes a replacement value for its own object
/// number, and a changed <c>/AcroForm</c> becomes a replacement (indirect) or part of a copied
/// catalog (direct). An array that loses entries is written directly into the copy of its owner,
/// so an indirect <c>/Fields</c> or <c>/Kids</c> array is simply no longer referenced. A form
/// whose every field went keeps its dictionary, with <c>/Fields []</c>.
/// </remarks>
internal static class AcroFormPass
{
    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Catalog is not { } catalog || !catalog.TryGetValue(PdfName.AcroForm, out var acroFormValue)
            || FormObjects.Resolve(context.Objects, acroFormValue) is not PdfDictionary acroForm)
        {
            DropDocMdp(context);
            return;
        }

        var walk = new FieldTreeRewrite(context);
        PdfDictionary? form = null;
        if (acroForm.TryGetValue(PdfName.Fields, out var fields) && FormObjects.Resolve(context.Objects, fields) is PdfArray fieldArray)
        {
            var rewritten = walk.RewriteKids(fieldArray, inheritedType: null, topLevel: true, depth: 0);
            walk.ExcludeUnsharedValues();
            if (rewritten.Changed)
            {
                form = FormObjects.Copy(acroForm);
                form.Set(PdfName.Fields, rewritten.Kids);
            }
        }

        if (acroForm.TryGetValue(PdfName.CO, out var co) && FormObjects.Resolve(context.Objects, co) is PdfArray order
            && order.Any(entry => entry is PdfReference reference && context.Excluded.Contains(reference.Target.Number)))
        {
            form ??= FormObjects.Copy(acroForm);
            var kept = new PdfArray(order.Where(entry => entry is not PdfReference reference || !context.Excluded.Contains(reference.Target.Number)));
            if (kept.Count > 0)
            {
                form.Set(PdfName.CO, kept);
            }
            else
            {
                form.Remove(PdfName.CO);
            }
        }

        // XFA datasets repeat every field's value; once a field is gone they would carry the
        // removed value (and disagree with the AcroForm), so they go too.
        if (walk.FieldsRemoved && acroForm.TryGetValue(PdfName.XFA, out var xfa))
        {
            form ??= FormObjects.Copy(acroForm);
            form.Remove(PdfName.XFA);
            ExcludeXfa(context, xfa);
            context.Counts.XfaForms = 1;
        }

        if (form is not null)
        {
            if (acroFormValue is PdfReference acroFormReference)
            {
                context.Replacements[acroFormReference.Target.Number] = form;
            }
            else
            {
                var catalogCopy = FormObjects.Copy(catalog);
                catalogCopy.Set(PdfName.AcroForm, form);
                context.Catalog = catalogCopy;
            }
        }

        DropDocMdp(context);
    }

    // /Perms /DocMDP names the certifying signature dictionary; once that is excluded the entry
    // would be written as null, so it is dropped (and an emptied /Perms with it).
    private static void DropDocMdp(SaveCleanupContext context)
    {
        if (context.Catalog is not { } catalog || !catalog.TryGetValue(PdfName.Perms, out var permsValue)
            || FormObjects.Resolve(context.Objects, permsValue) is not PdfDictionary perms
            || !perms.TryGetValue(PdfName.DocMDP, out var docMdp) || docMdp is not PdfReference docMdpReference
            || !context.Excluded.Contains(docMdpReference.Target.Number))
        {
            return;
        }

        var permsCopy = FormObjects.Copy(perms);
        permsCopy.Remove(PdfName.DocMDP);
        var catalogCopy = FormObjects.Copy(catalog);
        if (permsCopy.Count == 0)
        {
            catalogCopy.Remove(PdfName.Perms);
            if (permsValue is PdfReference emptied)
            {
                context.Excluded.Add(emptied.Target.Number);
            }
        }
        else if (permsValue is PdfReference permsReference)
        {
            context.Replacements[permsReference.Target.Number] = permsCopy;
            return;
        }
        else
        {
            catalogCopy.Set(PdfName.Perms, permsCopy);
        }

        context.Catalog = catalogCopy;
    }

    // /XFA is one stream or an array of (name, stream) pairs; every indirect part is dropped.
    private static void ExcludeXfa(SaveCleanupContext context, PdfObject xfa)
    {
        if (xfa is PdfReference reference)
        {
            context.Excluded.Add(reference.Target.Number);
        }

        if (FormObjects.Resolve(context.Objects, xfa) is PdfArray parts)
        {
            foreach (var part in parts)
            {
                if (part is PdfReference partReference)
                {
                    context.Excluded.Add(partReference.Target.Number);
                }
            }
        }
    }

    private sealed class FieldTreeRewrite(SaveCleanupContext context)
    {
        private static readonly PdfName[] AppearanceKeys = [PdfName.N, PdfName.Get("D")];
        private static readonly PdfName[] ButtonValueKeys = [PdfName.V, PdfName.DV];
        private static readonly PdfName[] ValueKeys = [PdfName.V, PdfName.Get("RV")];

        private readonly HashSet<int> _visited = [];
        private readonly HashSet<int> _counted = [];
        private readonly List<(int Field, List<int> Values)> _removedValues = [];
        private readonly HashSet<int> _keptValues = [];

        public bool FieldsRemoved { get; private set; }

        // Rewrites one /Fields or /Kids array: excluded entries leave it (as do dangling ones),
        // kept fields are rewritten (as replacements) and a field whose every kid went is removed
        // too. Returns the kept entries, whether any entry left, and whether any left because it
        // was removed with a page.
        public (PdfArray Kids, bool Changed, bool AnyRemoved) RewriteKids(PdfArray kids, PdfName? inheritedType, bool topLevel, int depth)
        {
            var result = new PdfArray();
            var dropped = 0;
            var removed = 0;
            foreach (var entry in kids)
            {
                if (entry is PdfNull)
                {
                    dropped++;
                    continue;
                }

                if (entry is not PdfReference reference)
                {
                    result.Add(entry);
                    continue;
                }

                var number = reference.Target.Number;
                var node = context.Objects[reference.Target] as PdfDictionary;
                if (context.Excluded.Contains(number))
                {
                    dropped++;
                    removed++;
                    if (node is not null && (topLevel || node.ContainsKey(PdfName.T)))
                    {
                        CountRemovedSubtree(number, node, depth);
                    }

                    continue;
                }

                if (node is null)
                {
                    // A dangling entry (a free or non-dictionary object) is written as null.
                    if (context.Objects[reference.Target] is PdfNull)
                    {
                        dropped++;
                        continue;
                    }

                    result.Add(entry);
                    continue;
                }

                if (!_visited.Add(number) || depth >= context.Options.MaxFieldTreeDepth)
                {
                    RecordKeptValues(node);
                    result.Add(entry);
                    continue;
                }

                if (RewriteField(number, node, inheritedType, depth))
                {
                    dropped++;
                    removed++;
                    continue;
                }

                result.Add(entry);
            }

            return (result, dropped > 0, removed > 0);
        }

        // Rewrites one kept-so-far field node; returns whether the node itself is now removed.
        private bool RewriteField(int number, PdfDictionary node, PdfName? inheritedType, int depth)
        {
            var type = node.TryGetValue(PdfName.FT, out var ft) && ft is PdfName ownType ? ownType : inheritedType;
            if (!node.TryGetValue(PdfName.Kids, out var kidsValue) || FormObjects.Resolve(context.Objects, kidsValue) is not PdfArray kids)
            {
                RecordKeptValues(node);
                return false;
            }

            var rewritten = RewriteKids(kids, type, topLevel: false, depth + 1);
            if (rewritten.Kids.Count == 0 && rewritten.AnyRemoved)
            {
                context.Excluded.Add(number);
                context.RemovedFields.Add(number);
                if (_counted.Add(number))
                {
                    context.Counts.Fields++;
                }

                FieldsRemoved = true;
                _removedValues.Add((number, ValueReferences(node)));
                return true;
            }

            RecordKeptValues(node);
            if (!rewritten.Changed)
            {
                return false;
            }

            // Pure widgets (kids without /T) this kept field lost.
            var widgetsLost = kids.Count(kid => kid is PdfReference kidReference && context.Excluded.Contains(kidReference.Target.Number)
                && context.Objects[kidReference.Target] is PdfDictionary widget && !widget.ContainsKey(PdfName.T));
            context.Counts.Widgets += widgetsLost;

            var copy = FormObjects.Copy(node);
            copy.Set(PdfName.Kids, rewritten.Kids);
            if (ReferenceEquals(type, PdfName.Btn))
            {
                AlignButtonGroup(copy, node, kids);
            }

            context.Replacements[number] = copy;
            return false;
        }

        // ISO 32000-1 12.7.4.2.3/12.7.4.2.4: a check box or radio group's /Opt holds one export
        // value per /Kids entry, by index, so an entry leaves /Opt with its widget. A /V (or /DV)
        // naming an appearance state only the removed widgets had now names nothing: /Off.
        private void AlignButtonGroup(PdfDictionary copy, PdfDictionary original, PdfArray originalKids)
        {
            var removedIndexes = new HashSet<int>();
            var keptStates = new HashSet<PdfName>();
            var removedStates = new HashSet<PdfName>();
            for (var i = 0; i < originalKids.Count; i++)
            {
                var kid = originalKids[i];
                var removed = kid is PdfNull
                    || (kid is PdfReference kidReference && (context.Excluded.Contains(kidReference.Target.Number) || context.Objects[kidReference.Target] is PdfNull));
                if (removed)
                {
                    removedIndexes.Add(i);
                }

                foreach (var state in AppearanceStates(kid))
                {
                    (removed ? removedStates : keptStates).Add(state);
                }
            }

            if (original.TryGetValue(PdfName.Opt, out var optValue) && FormObjects.Resolve(context.Objects, optValue) is PdfArray opt
                && opt.Count == originalKids.Count)
            {
                copy.Set(PdfName.Opt, new PdfArray(opt.Where((_, index) => !removedIndexes.Contains(index))));
            }

            foreach (var key in ButtonValueKeys)
            {
                if (original.TryGetValue(key, out var value) && value is PdfName state && !ReferenceEquals(state, PdfName.Off)
                    && removedStates.Contains(state) && !keptStates.Contains(state))
                {
                    copy.Set(key, PdfName.Off);
                }
            }
        }

        private IEnumerable<PdfName> AppearanceStates(PdfObject kid)
        {
            if (kid is not PdfReference reference || context.Objects[reference.Target] is not PdfDictionary widget
                || !widget.TryGetValue(PdfName.AP, out var ap) || FormObjects.Resolve(context.Objects, ap) is not PdfDictionary appearances)
            {
                return [];
            }

            var states = new List<PdfName>();
            foreach (var key in AppearanceKeys)
            {
                if (appearances.TryGetValue(key, out var sub) && FormObjects.Resolve(context.Objects, sub) is PdfDictionary subDictionary)
                {
                    states.AddRange(subDictionary.Keys);
                }
            }

            return states;
        }

        // A field the removed set already excluded: counted once, with every descendant field.
        private void CountRemovedSubtree(int number, PdfDictionary node, int depth)
        {
            FieldsRemoved = true;
            if (!_counted.Add(number))
            {
                return;
            }

            context.Counts.Fields++;
            if (depth >= context.Options.MaxFieldTreeDepth || !node.TryGetValue(PdfName.Kids, out var kidsValue)
                || FormObjects.Resolve(context.Objects, kidsValue) is not PdfArray kids)
            {
                return;
            }

            foreach (var kid in kids)
            {
                if (kid is PdfReference kidReference && context.Objects[kidReference.Target] is PdfDictionary child && child.ContainsKey(PdfName.T))
                {
                    CountRemovedSubtree(kidReference.Target.Number, child, depth + 1);
                }
            }
        }

        private void RecordKeptValues(PdfDictionary node) => _keptValues.UnionWith(ValueReferences(node));

        // The removed set's rule for the fields this pass removed: an indirect /V or /RV goes
        // with its field unless a surviving field shares it.
        public void ExcludeUnsharedValues()
        {
            foreach (var (_, values) in _removedValues)
            {
                foreach (var value in values)
                {
                    if (!_keptValues.Contains(value))
                    {
                        context.Excluded.Add(value);
                    }
                }
            }
        }

        private static List<int> ValueReferences(PdfDictionary node)
        {
            var values = new List<int>();
            foreach (var key in ValueKeys)
            {
                if (node.TryGetValue(key, out var value) && value is PdfReference valueReference)
                {
                    values.Add(valueReference.Target.Number);
                }
            }

            return values;
        }
    }
}
