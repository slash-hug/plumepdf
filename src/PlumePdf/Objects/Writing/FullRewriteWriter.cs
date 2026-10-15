namespace PlumePdf.Objects;

/// <summary>
/// The full-rewrite save path (<c>PdfDocument.Save</c>, docs/architecture.md "Writing
/// pipeline"): streams every reachable object to the output with unreferenced objects
/// garbage-collected and every object renumbered from 1. Deliberately layer-2 (Objects):
/// it knows nothing about <c>PdfDocument</c>/<c>PdfPage</c> — the caller (the
/// <c>PlumePdf</c>-namespace facade, a higher layer) unwraps its own state into the plain
/// <see cref="ObjectRegistry"/>/<see cref="PdfDictionary"/>/<see cref="IndirectReference"/>
/// primitives this type accepts, per <c>tests/PlumePdf.ArchitectureTests/LayeringTests</c>'s
/// facade rule (a layer-2 type may never depend on the layer-4 <c>PdfDocument</c>/<c>PdfPage</c>
/// facade types).
/// </summary>
/// <remarks>
/// Phase 1 simplification (documented, like <see cref="CrossReferenceReader"/>'s own):
/// the page tree is always flattened to a single fresh <c>/Pages</c> node listing exactly
/// the given pages in order, with every retained page's <c>/Parent</c> rewritten to match —
/// regardless of whether the source tree was already flat or deeply nested, and regardless
/// of whether pages were actually reordered. This keeps renumbering/GC uniform and is always
/// semantically correct; a future phase can preserve an unmodified tree's exact shape when
/// nothing changed, for smaller diffs.
///
/// Renumbering never follows a page's original <c>/Parent</c> (the same "don't climb the page
/// tree" rule <c>PageImporter</c> uses for <c>Pdf.Merge</c>/<c>Pdf.Split</c>): a page's
/// <c>/Parent</c> is always replaced with a reference to the fresh <c>/Pages</c> node, and
/// that replacement reference is tracked by its own object identity (not by the object number
/// it happens to carry) so it can never be confused with an unrelated original object that
/// happens to share the same number — small documents renumbered from 1 make that collision
/// the common case, not a rare one.
///
/// Objects registered via <see cref="ObjectRegistry.RegisterNew"/> need no special handling
/// here at all: discovery resolves every reference it finds through
/// <c>objects[...]</c>, which transparently serves a brand-new object exactly like an
/// original one (<see cref="ObjectRegistry"/>'s overlay), so a new object reachable from the
/// catalog/page graph — e.g. referenced from a mutated, <see cref="ObjectRegistry.MarkDirty"/>-marked
/// dictionary — is discovered, renumbered, and serialized in the same deterministic
/// assignment-order walk as everything else. An object that is registered but never wired
/// into the reachable graph is, correctly, garbage-collected away like any other unreferenced
/// object — <c>PdfDocument.Save</c> is a GC, not a dump of everything ever touched.
/// </remarks>
internal static class FullRewriteWriter
{
    private static readonly PdfName KidsName = PdfName.Get("Kids");
    private static readonly PdfName CountName = PdfName.Get("Count");
    private static readonly PdfName PagesName = PdfName.Get("Pages");
    private static readonly PdfName ParentName = PdfName.Get("Parent");
    private static readonly PdfName CatalogName = PdfName.Get("Catalog");

    /// <summary>Writes a complete, freshly-renumbered document to <paramref name="output"/>.</summary>
    /// <param name="output">The destination stream.</param>
    /// <param name="objects">The source document's object graph.</param>
    /// <param name="catalogReference">The source document's catalog's own identity, or <see langword="null"/> when unresolvable.</param>
    /// <param name="catalogDictionary">The source document's catalog dictionary, or <see langword="null"/> when unresolvable.</param>
    /// <param name="pages">The pages to include, in final order.</param>
    /// <param name="options">Options controlling the write, notably <see cref="PdfOptions.Deterministic"/>.</param>
    /// <param name="excluded">
    /// Original object numbers that must not reach the output (the pages removed before this
    /// save, the original page-tree nodes, and what belonged only to those pages): a reference
    /// to one is never followed and is written as <c>null</c>. <see langword="null"/> excludes
    /// nothing.
    /// </param>
    /// <param name="replacements">
    /// Replacement values for original object numbers other than the catalog and the retained
    /// pages (those arrive as <paramref name="catalogDictionary"/> and <paramref name="pages"/>),
    /// and values for numbers above the registry's range that the caller allocated for this save
    /// only. Consulted before <paramref name="objects"/> during discovery and serialization.
    /// </param>
    /// <param name="transform">
    /// A write-time replacement for individual streams, offered every original stream as it
    /// is written that <paramref name="replacements"/> does not already replace (an excluded
    /// number is never written, so never offered). Its result may reference only a subset of
    /// what the original references (<c>PLUME5010</c> otherwise). <see langword="null"/>
    /// writes every stream as resolved.
    /// </param>
    /// <param name="aliases">
    /// Original object numbers to write as another original object (a duplicate's number
    /// mapped to its canonical, never-aliased, non-excluded original): every reference to a
    /// duplicate is written as one to its canonical, and the duplicate itself is never
    /// numbered, walked or written. <see langword="null"/> aliases nothing.
    /// </param>
    public static void Write(
        Stream output,
        ObjectRegistry objects,
        IndirectReference? catalogReference,
        PdfDictionary? catalogDictionary,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        PdfOptions options,
        IReadOnlySet<int>? excluded = null,
        IReadOnlyDictionary<int, PdfObject>? replacements = null,
        IStreamWriteTransform? transform = null,
        IReadOnlyDictionary<int, int>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);

        excluded ??= EmptyExclusions;
        var originalNumberToNew = new Dictionary<int, int>();
        var pendingOriginal = new Queue<int>();
        var extraObjects = new List<(int Number, PdfObject Value)>();
        var overrides = SeedOverrides(catalogReference, pages, excluded, replacements);
        ValidateAliases(objects, aliases, catalogReference, pages, excluded, overrides);

        // A duplicate's references land on its canonical original.
        int Canonical(int originalNumber) =>
            aliases is not null && aliases.TryGetValue(originalNumber, out var canonical) ? canonical : originalNumber;

        // References this writer constructs itself, already carrying a final object number
        // (a /Pages node's /Kids entries, every retained page's replacement /Parent, the
        // catalog's replacement /Pages) — tracked by instance identity so ObjectSerializer's
        // translateReference callback can recognize and pass them through unchanged, never by
        // the number they carry (which can and does collide with unrelated original object
        // numbers once everything is renumbered from 1).
        var syntheticReferences = new HashSet<PdfReference>();

        var nextNumber = 1;

        int AssignOriginal(int originalNumber)
        {
            originalNumber = Canonical(originalNumber);
            if (originalNumberToNew.TryGetValue(originalNumber, out var existing))
            {
                return existing;
            }

            var assigned = nextNumber++;
            originalNumberToNew[originalNumber] = assigned;
            pendingOriginal.Enqueue(originalNumber);
            return assigned;
        }

        int AllocateExtra(PdfObject value)
        {
            var assigned = nextNumber++;
            extraObjects.Add((assigned, value));
            return assigned;
        }

        // 1. Retained pages: assign each a fresh number keyed by its original identity, so
        //    anything else in the graph that references a page by its original number
        //    (outlines, /Dest targets, ...) still resolves to the right object.
        foreach (var (reference, _) in pages)
        {
            AssignOriginal(reference.Number);
        }

        // 2. A fresh /Pages root listing exactly the retained pages, in order.
        var kids = new PdfArray();
        foreach (var (reference, _) in pages)
        {
            var kidReference = new PdfReference(new IndirectReference(originalNumberToNew[reference.Number], 0));
            syntheticReferences.Add(kidReference);
            kids.Add(kidReference);
        }

        var pagesRootDict = new PdfDictionary();
        pagesRootDict.Set(PdfName.Type, PagesName);
        pagesRootDict.Set(KidsName, kids);
        pagesRootDict.Set(CountName, PdfNumber.Get(pages.Count));
        var pagesRootNumber = AllocateExtra(pagesRootDict);

        var pagesRootReference = new PdfReference(new IndirectReference(pagesRootNumber, 0));
        syntheticReferences.Add(pagesRootReference);

        // 3. Catalog: reuse the source catalog's original identity when it has one (so any
        //    other original-space reference to it, e.g. a rare self-referential /Perms setup,
        //    still resolves), rebuilding it with /Pages replaced.
        int catalogNumber;
        if (catalogReference is { } realCatalogReference && catalogDictionary is not null)
        {
            overrides[realCatalogReference.Number] = BuildCatalogCopy(catalogDictionary, pagesRootReference);
            catalogNumber = AssignOriginal(realCatalogReference.Number);
        }
        else
        {
            catalogNumber = AllocateExtra(BuildCatalogCopy(catalogDictionary ?? new PdfDictionary(), pagesRootReference));
        }

        // 4. Every retained page, with /Parent rewritten to the fresh /Pages root.
        foreach (var (reference, dictionary) in pages)
        {
            overrides[reference.Number] = BuildPageCopy(dictionary, pagesRootReference);
        }

        // 5. /Info, if present — carried over via ordinary discovery (not overridden).
        int? infoNumber = null;
        if (objects.Trailer.TryGetValue(PdfName.Info, out var infoValue) && infoValue is PdfReference infoRef
            && !excluded.Contains(infoRef.Target.Number))
        {
            infoNumber = AssignOriginal(infoRef.Target.Number);
        }

        // 6. Discovery: walk every pending original object's content for further references
        //    (fonts, XObjects, annotations, ...), assigning each a fresh number the first
        //    time it is seen. Synthetic replacement references (found inside an override's
        //    content) are skipped — they already carry a final number and were never original
        //    content to begin with. Cycle-safe: AssignOriginal registers before recursing.
        //    A stream is walked by its dictionary alone where the source allows it, and its
        //    payload read only when it's written (ValueToWrite), so the save holds one
        //    payload at a time rather than every stream it reaches.
        var streams = new HashSet<int>();
        while (pendingOriginal.Count > 0)
        {
            var originalNumber = pendingOriginal.Dequeue();
            var value = ValueToWalk(objects, overrides, originalNumber, out var isStream);
            if (isStream)
            {
                streams.Add(originalNumber);
            }

            var references = value is PdfDictionary streamDictionary && isStream ? FindStreamDictionaryReferences(streamDictionary) : FindReferences(value);
            foreach (var reference in references)
            {
                if (!syntheticReferences.Contains(reference) && !excluded.Contains(reference.Target.Number))
                {
                    AssignOriginal(reference.Target.Number);
                }
            }
        }

        IndirectReference? Translate(PdfReference reference) =>
            syntheticReferences.Contains(reference)
                ? reference.Target
                : originalNumberToNew.TryGetValue(Canonical(reference.Target.Number), out var newNumber)
                    ? new IndirectReference(newNumber, 0)
                    : excluded.Contains(reference.Target.Number)
                        ? null
                        : throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: a reference to object {reference.Target.Number} was encountered outside the discovered graph.");

        // 7. Serialize: header, the small fixed set of synthetic objects, then every
        //    discovered original object in assignment order. The optimized path
        //    writes the same objects in the same assignment order, but packs the
        //    compressible ones into object streams and closes with a cross-reference stream
        //    instead of a classic table — the caller (PdfDocument.Save) has already refused
        //    the option below PDF 1.5 (PLUME5017), so no version check is repeated here.
        WriteHeader(output, options);

        if (options.Optimize)
        {
            WriteOptimizedBody(output, objects, overrides, transform, extraObjects, originalNumberToNew, streams, nextNumber, catalogNumber, infoNumber, Translate, options);
            return;
        }

        var offsets = new long[nextNumber];
        foreach (var (number, value) in extraObjects)
        {
            offsets[number] = output.Position;
            ObjectSerializer.WriteIndirectObject(output, number, 0, value, Translate);
        }

        foreach (var (originalNumber, newNumber) in originalNumberToNew.OrderBy(static kv => kv.Value))
        {
            var value = ValueToWrite(objects, overrides, transform, originalNumber);
            offsets[newNumber] = output.Position;
            ObjectSerializer.WriteIndirectObject(output, newNumber, 0, value, Translate);
        }

        WriteXrefAndTrailer(output, offsets, nextNumber, catalogNumber, infoNumber, options);
    }

    // The PdfOptions.Optimize body (ISO 32000-1 §7.5.7/§7.5.8): every non-stream object is
    // packed into fixed-size object-stream batches in assignment order (ObjectStreamWriter's
    // deterministic ObjectsPerStream constant); stream objects — the only
    // non-compressible kind this writer can encounter, since it renumbers everything to
    // generation 0 and an encrypted source never reaches Save (PLUME5001) — stay direct, and
    // the closing cross-reference stream indexes both kinds.
    private static void WriteOptimizedBody(
        Stream output,
        ObjectRegistry objects,
        Dictionary<int, PdfObject> overrides,
        IStreamWriteTransform? transform,
        List<(int Number, PdfObject Value)> extraObjects,
        Dictionary<int, int> originalNumberToNew,
        IReadOnlySet<int> streams,
        int nextNumber,
        int catalogNumber,
        int? infoNumber,
        Func<PdfReference, IndirectReference?> translate,
        PdfOptions options)
    {
        // Every final non-stream object, indexed by its final number; a stream is resolved
        // only when it's written, after the object streams, so it is never held alongside the rest.
        var finalValues = new PdfObject?[nextNumber];
        var streamOriginals = new int?[nextNumber];
        foreach (var (number, value) in extraObjects)
        {
            finalValues[number] = value;
        }

        foreach (var (originalNumber, newNumber) in originalNumberToNew)
        {
            if (streams.Contains(originalNumber))
            {
                streamOriginals[newNumber] = originalNumber;
            }
            else
            {
                finalValues[newNumber] = ValueToWrite(objects, overrides, transform, originalNumber);
            }
        }

        var compressible = new List<int>();
        for (var number = 1; number < nextNumber; number++)
        {
            if (streamOriginals[number] is null && finalValues[number] is not PdfStream)
            {
                compressible.Add(number);
            }
        }

        // Object-stream numbers continue after every content object; the cross-reference
        // stream takes the number after the last object stream.
        var batchCount = (compressible.Count + ObjectStreamWriter.ObjectsPerStream - 1) / ObjectStreamWriter.ObjectsPerStream;
        var firstObjectStreamNumber = nextNumber;
        var xrefStreamNumber = nextNumber + batchCount;

        var entries = new XRefStreamWriter.Entry[xrefStreamNumber];
        entries[0] = new XRefStreamWriter.Entry(0, 0, 65535);

        for (var batch = 0; batch < batchCount; batch++)
        {
            var objectStreamNumber = firstObjectStreamNumber + batch;
            var writer = new ObjectStreamWriter(translate);
            var start = batch * ObjectStreamWriter.ObjectsPerStream;
            var end = Math.Min(start + ObjectStreamWriter.ObjectsPerStream, compressible.Count);
            for (var i = start; i < end; i++)
            {
                var number = compressible[i];
                entries[number] = new XRefStreamWriter.Entry(2, objectStreamNumber, i - start);
                writer.Add(number, finalValues[number]!);
            }

            entries[objectStreamNumber] = new XRefStreamWriter.Entry(1, output.Position, 0);
            ObjectSerializer.WriteIndirectObject(output, objectStreamNumber, 0, writer.Build(options));
        }

        for (var number = 1; number < nextNumber; number++)
        {
            var value = streamOriginals[number] is int originalNumber ? ValueToWrite(objects, overrides, transform, originalNumber) : finalValues[number];
            if (value is PdfStream stream)
            {
                entries[number] = new XRefStreamWriter.Entry(1, output.Position, 0);
                ObjectSerializer.WriteIndirectObject(output, number, 0, stream, translate);
            }
        }

        XRefStreamWriter.Write(output, entries, xrefStreamNumber, catalogNumber, infoNumber, options);
    }

    // Shared with Linearizer (the other full-rewrite-shaped writer): both
    // writers rebuild the catalog and every retained page around a fresh /Pages node the
    // same way, and both discover the reachable graph with the same reference walk.
    internal static PdfDictionary BuildCatalogCopy(PdfDictionary original, PdfReference pagesRootReference)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in original)
        {
            if (!ReferenceEquals(key, PagesName))
            {
                copy.Set(key, value);
            }
        }

        if (!copy.ContainsKey(PdfName.Type))
        {
            copy.Set(PdfName.Type, CatalogName);
        }

        copy.Set(PagesName, pagesRootReference);
        return copy;
    }

    internal static PdfDictionary BuildPageCopy(PdfDictionary original, PdfReference pagesRootReference)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in original)
        {
            if (!ReferenceEquals(key, ParentName))
            {
                copy.Set(key, value);
            }
        }

        copy.Set(ParentName, pagesRootReference);
        return copy;
    }

    internal static readonly IReadOnlySet<int> EmptyExclusions = new HashSet<int>();

    // The writer's own override table, seeded with the caller's replacements. The catalog and
    // the retained pages are never replaced this way (they arrive through their own parameters
    // and the writer rebuilds them around the fresh /Pages node), and neither may be excluded.
    internal static Dictionary<int, PdfObject> SeedOverrides(
        IndirectReference? catalogReference,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        IReadOnlySet<int> excluded,
        IReadOnlyDictionary<int, PdfObject>? replacements)
    {
        var reserved = new HashSet<int>();
        if (catalogReference is { } catalog)
        {
            reserved.Add(catalog.Number);
        }

        foreach (var (reference, _) in pages)
        {
            reserved.Add(reference.Number);
        }

        foreach (var number in reserved)
        {
            if (excluded.Contains(number))
            {
                throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: object {number} is the catalog or a retained page but was excluded from the output.");
            }
        }

        var overrides = new Dictionary<int, PdfObject>();
        if (replacements is not null)
        {
            foreach (var (number, value) in replacements)
            {
                if (reserved.Contains(number))
                {
                    throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: object {number} is the catalog or a retained page and cannot be supplied as a replacement.");
                }

                overrides[number] = value;
            }
        }

        return overrides;
    }

    private static PdfObject ValueToWalk(ObjectRegistry objects, Dictionary<int, PdfObject> overrides, int originalNumber, out bool isStream)
    {
        if (overrides.TryGetValue(originalNumber, out var overridden))
        {
            isStream = overridden is PdfStream;
            return overridden;
        }

        return ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(originalNumber, 0), out isStream);
    }

    // The single point every original object's written value comes from: a caller override
    // when there is one, otherwise the resolved value, which a transform may replace when it
    // is a stream. Called once per object, so the transform's result is written and let go.
    private static PdfObject ValueToWrite(ObjectRegistry objects, Dictionary<int, PdfObject> overrides, IStreamWriteTransform? transform, int originalNumber)
    {
        if (overrides.TryGetValue(originalNumber, out var overridden))
        {
            return overridden;
        }

        var value = ((IObjectSource)objects).ResolveForWrite(new IndirectReference(originalNumber, 0));
        return transform is not null && value is PdfStream stream && transform.Handles(originalNumber)
            ? Transformed(transform, originalNumber, stream)
            : value;
    }

    // Discovery numbered what the original stream's dictionary references, so a replacement
    // may keep or drop those references but not add one: a new target would either have no
    // output number or silently gain a reference the original never carried.
    private static PdfStream Transformed(IStreamWriteTransform transform, int originalNumber, PdfStream original)
    {
        var replacement = transform.Transform(originalNumber, original);
        if (ReferenceEquals(replacement, original))
        {
            return original;
        }

        HashSet<int>? allowed = null;
        foreach (var reference in FindStreamDictionaryReferences(replacement.Dictionary))
        {
            allowed ??= [.. FindStreamDictionaryReferences(original.Dictionary).Select(static r => r.Target.Number)];
            if (!allowed.Contains(reference.Target.Number))
            {
                throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: the write-time replacement for object {originalNumber} references object {reference.Target.Number}, which the original stream does not.");
            }
        }

        return replacement;
    }

    // An alias maps a duplicate to the original written in its place, so the canonical side
    // must itself be written as itself: a stream that exists, not excluded, not aliased in
    // turn, not the catalog or a retained page (which the writer rebuilds itself), and not
    // replaced by a caller override (whose value is not the content the duplicate was found
    // equal to). The duplicate side must be an ordinary object too, never excluded (it is
    // written as null wherever referenced), the catalog, a retained page or an override.
    private static void ValidateAliases(
        ObjectRegistry objects,
        IReadOnlyDictionary<int, int>? aliases,
        IndirectReference? catalogReference,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        IReadOnlySet<int> excluded,
        Dictionary<int, PdfObject> overrides)
    {
        if (aliases is null || aliases.Count == 0)
        {
            return;
        }

        var reserved = new HashSet<int>(pages.Select(static p => p.Reference.Number));
        if (catalogReference is { } catalog)
        {
            reserved.Add(catalog.Number);
        }

        foreach (var (duplicate, canonical) in aliases)
        {
            var problem =
                duplicate == canonical ? "is aliased to itself"
                : excluded.Contains(duplicate) ? "is excluded from the output"
                : reserved.Contains(duplicate) ? "is the catalog or a retained page"
                : overrides.ContainsKey(duplicate) ? "has a replacement value"
                : excluded.Contains(canonical) ? $"is aliased to object {canonical}, which is excluded from the output"
                : aliases.ContainsKey(canonical) ? $"is aliased to object {canonical}, which is itself aliased"
                : reserved.Contains(canonical) ? $"is aliased to object {canonical}, which is the catalog or a retained page"
                : overrides.ContainsKey(canonical) ? $"is aliased to object {canonical}, which has a replacement value"
                : !IsStream(objects, canonical) ? $"is aliased to object {canonical}, which is not a stream"
                : null;

            if (problem is not null)
            {
                throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: object {duplicate} {problem}.");
            }
        }
    }

    // Dictionary-only where the source allows, so validating a canonical never reads its payload.
    private static bool IsStream(ObjectRegistry objects, int number)
    {
        ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(number, 0), out var isStream);
        return isStream;
    }

    internal static IEnumerable<PdfReference> FindReferences(PdfObject value)
    {
        switch (value)
        {
            case PdfReference reference:
                yield return reference;
                break;

            case PdfArray array:
                foreach (var item in array)
                {
                    foreach (var nested in FindReferences(item))
                    {
                        yield return nested;
                    }
                }

                break;

            case PdfDictionary dict:
                foreach (var (_, item) in dict)
                {
                    foreach (var nested in FindReferences(item))
                    {
                        yield return nested;
                    }
                }

                break;

            case PdfStream stream:
                foreach (var nested in FindStreamDictionaryReferences(stream.Dictionary))
                {
                    yield return nested;
                }

                break;
        }
    }

    /// <summary>The references a stream with this dictionary carries into the output.</summary>
    internal static IEnumerable<PdfReference> FindStreamDictionaryReferences(PdfDictionary dictionary)
    {
        foreach (var (key, item) in dictionary)
        {
            // A stream's /Length is never carried over as a reference —
            // ObjectSerializer.WriteStream always recomputes it from the actual
            // payload and writes it direct — so a source's indirect-/Length integer
            // object is unreferenced in the output and following it here would only
            // resurrect it as garbage (found via the qpdf linearization oracle, which
            // flags the orphan as an unattributable object).
            if (ReferenceEquals(key, PdfName.Length))
            {
                continue;
            }

            foreach (var nested in FindReferences(item))
            {
                yield return nested;
            }
        }
    }

    // PDF/A-1b requires a "%PDF-1.4" header (object streams and
    // cross-reference streams, introduced in 1.5, are forbidden at that conformance level) —
    // PdfOptions.PdfVersion is the save-time knob a PDF/A-1b save (or any other caller) sets
    // to override the "1.7" every version PlumePDF has ever written before this knob existed.
    private static void WriteHeader(Stream output, PdfOptions options)
    {
        var version = string.IsNullOrWhiteSpace(options.PdfVersion) ? "1.7" : options.PdfVersion;
        WriteAscii(output, $"%PDF-{version}\n%");
        output.Write([0xE2, 0xE3, 0xCF, 0xD3]);
        output.Write("\n"u8);
    }

    private static void WriteXrefAndTrailer(Stream output, long[] offsets, int size, int catalogNumber, int? infoNumber, PdfOptions options)
    {
        var xrefOffset = output.Position;
        WriteAscii(output, "xref\n");
        WriteAscii(output, $"0 {size}\n");
        WriteAscii(output, "0000000000 65535 f \n");
        for (var number = 1; number < size; number++)
        {
            WriteAscii(output, $"{offsets[number]:D10} 00000 n \n");
        }

        var id = DeterministicContext.CreateDocumentId(options.Deterministic);
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(size));
        trailer.Set(PdfName.Root, new PdfReference(new IndirectReference(catalogNumber, 0)));
        if (infoNumber is int info)
        {
            trailer.Set(PdfName.Info, new PdfReference(new IndirectReference(info, 0)));
        }

        trailer.Set(PdfName.Id, new PdfArray([PdfString.FromHex(id), PdfString.FromHex(id)]));

        WriteAscii(output, "trailer\n");
        ObjectSerializer.WriteValue(output, trailer);
        WriteAscii(output, $"\nstartxref\n{xrefOffset}\n%%EOF");
    }

    private static void WriteAscii(Stream output, string text) => output.Write(System.Text.Encoding.ASCII.GetBytes(text));
}
