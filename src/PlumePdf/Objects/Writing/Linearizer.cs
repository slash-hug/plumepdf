using System.Globalization;
using System.Text;

namespace PlumePdf.Objects;

/// <summary>
/// The linearized ("fast web view") save path (<see cref="PdfOptions.Linearize"/>,
/// ISO 32000-1 Annex F): a full rewrite — the same reachability GC, page-tree
/// flattening, and renumbering contract as <see cref="FullRewriteWriter"/>, whose copy/walk
/// helpers it shares — whose output is laid out first-page-first in Annex F's eleven parts,
/// opened by a linearization parameter dictionary inside the first 1024 bytes and indexed by
/// a primary hint stream (<see cref="LinearizationHintBuilder"/>).
/// </summary>
/// <remarks>
/// Two-pass by measurement, not buffering: pass one serializes every object into a counting
/// sink to learn its exact byte length (per §F.4's positioning rule, every hint-table value
/// is computed in <em>hint-stream-elided</em> coordinates, so nothing in the hint tables
/// depends on the hint stream's own length); pass two writes the finished file front to back
/// in one sweep with every offset — <c>/L</c>, <c>/H</c>, <c>/E</c>, <c>/T</c>, both
/// cross-reference tables — already final. The handful of linearization-dictionary and
/// first-page-trailer integers that would otherwise be circular (they sit <em>before</em>
/// the bytes they measure) are written zero-padded to a fixed 10-digit width, the same
/// spec-legal leading-zeros device <c>SigningWriteSession</c>'s <c>/ByteRange</c>
/// placeholders rely on (§7.3.3 permits an integer any number of leading zeros). The caller
/// (<c>PdfDocument.Save</c>) stages through its existing temp file — the same staging
/// carve-out <see cref="FullRewriteWriter"/> uses — so nothing here ever holds the whole
/// document in memory.
///
/// Annex F object grouping: the second group (parts 7–9 — remaining pages with their
/// nonshared objects, shared objects, then everything else including the fresh page tree
/// root and <c>/Info</c>) is numbered from 1; the first group (the linearization dictionary,
/// the catalogue and F.3.5's document-level objects, then the first page and everything it
/// references) continues from there, with the hint stream taking the file's last object
/// number (§F.3.6). An object referenced by the first page stays in the first-page section
/// no matter who else references it (§F.4.2); an object referenced by two or more
/// <em>remaining</em> pages lands in the shared objects section; one referenced by exactly
/// one remaining page travels with that page.
/// </remarks>
internal static class Linearizer
{
    private static readonly PdfName KidsName = PdfName.Get("Kids");
    private static readonly PdfName CountName = PdfName.Get("Count");
    private static readonly PdfName PagesName = PdfName.Get("Pages");
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName OutlinesName = PdfName.Get("Outlines");
    private static readonly PdfName PageModeName = PdfName.Get("PageMode");
    private static readonly PdfName UseOutlinesName = PdfName.Get("UseOutlines");
    private static readonly PdfName SName = PdfName.Get("S");
    private static readonly PdfName OName = PdfName.Get("O");

    // F.3.5's document-level catalogue entries whose immediate target objects belong in
    // part 4 (the top-level dictionaries only — everything they reference goes to part 9).
    private static readonly PdfName[] DocumentLevelKeys =
    [
        PdfName.Get("ViewerPreferences"),
        PdfName.Get("OpenAction"),
        PdfName.AcroForm,
        PdfName.Get("Threads"),
    ];

    /// <summary>Writes a complete linearized document to <paramref name="output"/>.</summary>
    /// <param name="output">The destination stream — <c>PdfDocument.Save</c>'s temp-file staging stream.</param>
    /// <param name="objects">The source document's object graph.</param>
    /// <param name="catalogReference">The source catalog's identity, or <see langword="null"/> when unresolvable.</param>
    /// <param name="catalogDictionary">The source catalog dictionary, or <see langword="null"/> when unresolvable.</param>
    /// <param name="pages">The pages to include, in final order — never empty (the caller refuses a zero-page linearize with <c>PLUME5020</c>).</param>
    /// <param name="options">Options controlling the write, notably <see cref="PdfOptions.Deterministic"/>.</param>
    public static void Write(
        Stream output,
        ObjectRegistry objects,
        IndirectReference? catalogReference,
        PdfDictionary? catalogDictionary,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfZero(pages.Count);

        // ---- Phase 1: classify every reachable object into its Annex F part, in the -------
        // ---- source's original number space. ----------------------------------------------
        var placeholderParent = new PdfReference(new IndirectReference(0, 65535));
        var pageNumbers = new HashSet<int>(pages.Select(static p => p.Reference.Number));
        var catalogNumber = catalogReference?.Number;

        var classifyPageCopies = new Dictionary<int, PdfDictionary>();
        foreach (var (reference, dictionary) in pages)
        {
            classifyPageCopies[reference.Number] = FullRewriteWriter.BuildPageCopy(dictionary, placeholderParent);
        }

        PdfObject OriginalValue(int number) =>
            classifyPageCopies.TryGetValue(number, out var copy) ? copy : objects[new IndirectReference(number, 0)];

        var classifyCatalogCopy = FullRewriteWriter.BuildCatalogCopy(catalogDictionary ?? new PdfDictionary(), placeholderParent);

        void Collect(PdfObject root, HashSet<int> set, List<int> order, HashSet<int>? extraBarriers = null)
        {
            var queue = new Queue<PdfObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                foreach (var reference in FullRewriteWriter.FindReferences(queue.Dequeue()))
                {
                    if (ReferenceEquals(reference, placeholderParent))
                    {
                        continue;
                    }

                    var number = reference.Target.Number;
                    if (pageNumbers.Contains(number) || number == catalogNumber
                        || extraBarriers?.Contains(number) == true || !set.Add(number))
                    {
                        continue;
                    }

                    order.Add(number);
                    queue.Enqueue(OriginalValue(number));
                }
            }
        }

        // The interactive form field hierarchy belongs in part 9 with the document-level
        // objects, never in a page's section (§F.3.10; only the top-level /AcroForm
        // dictionary travels with the catalogue, §F.3.5) — so its closure is claimed before
        // any page's, and page closures treat it as a barrier. The qpdf oracle computes page
        // object groups the same way.
        var formHierarchySet = new HashSet<int>();
        var formHierarchyOrder = new List<int>();
        if (classifyCatalogCopy.TryGetValue(PdfName.AcroForm, out var acroFormValue))
        {
            if (acroFormValue is PdfReference acroFormReference && !ReferenceEquals(acroFormReference, placeholderParent))
            {
                var number = acroFormReference.Target.Number;
                if (!pageNumbers.Contains(number) && number != catalogNumber)
                {
                    Collect(OriginalValue(number), formHierarchySet, formHierarchyOrder);
                }
            }
            else if (acroFormValue is PdfDictionary directAcroForm)
            {
                Collect(directAcroForm, formHierarchySet, formHierarchyOrder);
            }
        }

        // The outline hierarchy also lives in part 9 (§F.3.10 — this writer never sets
        // /PageMode /UseOutlines, so §F.3.7's part-6 placement never applies) and is kept
        // contiguous so the /O generic hint table (Table F.9) can describe it as one group.
        var outlineSet = new HashSet<int>();
        var outlineOrder = new List<int>();
        if (classifyCatalogCopy.TryGetValue(OutlinesName, out var outlinesValue)
            && outlinesValue is PdfReference outlinesReference
            && !ReferenceEquals(outlinesReference, placeholderParent))
        {
            var number = outlinesReference.Target.Number;
            if (!pageNumbers.Contains(number) && number != catalogNumber && !formHierarchySet.Contains(number))
            {
                outlineSet.Add(number);
                outlineOrder.Add(number);
                Collect(OriginalValue(number), outlineSet, outlineOrder, formHierarchySet);
            }
        }

        // §F.3.7: when the catalogue's /PageMode is /UseOutlines, the outline hierarchy
        // belongs in the first-page section (part 6) rather than part 9 — the reader needs it
        // to draw the initial view. The group stays contiguous either way, at the end of the
        // section it lands in, so the /O hint table can describe it.
        var outlinesInFirstPage = classifyCatalogCopy.TryGetValue(PageModeName, out var pageModeValue)
            && ReferenceEquals(pageModeValue, UseOutlinesName)
            && outlineOrder.Count > 0;

        var pageBarriers = new HashSet<int>(formHierarchySet);
        pageBarriers.UnionWith(outlineSet);

        // Part 6: the first page's full closure (page tree nodes, the catalogue, and the
        // hierarchies claimed above excluded).
        var firstPageSet = new HashSet<int>();
        var firstPageOrder = new List<int>();
        Collect(classifyPageCopies[pages[0].Reference.Number], firstPageSet, firstPageOrder, pageBarriers);
        if (outlinesInFirstPage)
        {
            firstPageOrder.AddRange(outlineOrder);
            firstPageSet.UnionWith(outlineSet);
        }

        // Parts 7/8: each remaining page's closure; an object hit by two or more remaining
        // pages (and not already first-page-owned) is shared. `owner` maps an object to its
        // single owning remaining page, or -1 once it proves shared.
        var remainingOrders = new List<int>[pages.Count];
        var owner = new Dictionary<int, int>();
        var part8Order = new List<int>();
        for (var k = 1; k < pages.Count; k++)
        {
            var set = new HashSet<int>();
            var order = new List<int>();
            Collect(classifyPageCopies[pages[k].Reference.Number], set, order, pageBarriers);
            remainingOrders[k] = order;
            foreach (var number in order)
            {
                if (firstPageSet.Contains(number))
                {
                    continue;
                }

                if (owner.TryGetValue(number, out var existing))
                {
                    if (existing != -1 && existing != k)
                    {
                        owner[number] = -1;
                        part8Order.Add(number);
                    }
                }
                else
                {
                    owner[number] = k;
                }
            }
        }

        // Part 4: the catalogue plus F.3.5's document-level targets (top-level objects only).
        var docLevelOrder = new List<int>();
        foreach (var key in DocumentLevelKeys)
        {
            if (classifyCatalogCopy.TryGetValue(key, out var value)
                && value is PdfReference reference
                && !ReferenceEquals(reference, placeholderParent))
            {
                var number = reference.Target.Number;
                if (!pageNumbers.Contains(number) && number != catalogNumber && !firstPageSet.Contains(number)
                    && !owner.ContainsKey(number) && !formHierarchySet.Contains(number)
                    && !outlineSet.Contains(number) && !docLevelOrder.Contains(number))
                {
                    docLevelOrder.Add(number);
                }
            }
        }

        // Part 9: everything else reachable — the contiguous outline group first (so the /O
        // hint can span it), then everything found from the catalogue's remaining entries,
        // the document-level objects' own contents (the form field hierarchy arrives here,
        // through the /AcroForm root), and the trailer's /Info.
        var classified = new HashSet<int>(firstPageSet);
        classified.UnionWith(owner.Keys);
        classified.UnionWith(docLevelOrder);
        classified.UnionWith(outlineSet);
        var part9Order = outlinesInFirstPage ? [] : new List<int>(outlineOrder);
        {
            var queue = new Queue<PdfObject>();
            queue.Enqueue(classifyCatalogCopy);
            foreach (var number in docLevelOrder)
            {
                queue.Enqueue(OriginalValue(number));
            }

            if (objects.Trailer.TryGetValue(PdfName.Info, out var infoValue) && infoValue is PdfReference)
            {
                queue.Enqueue(infoValue);
            }

            while (queue.Count > 0)
            {
                foreach (var reference in FullRewriteWriter.FindReferences(queue.Dequeue()))
                {
                    if (ReferenceEquals(reference, placeholderParent))
                    {
                        continue;
                    }

                    var number = reference.Target.Number;
                    if (pageNumbers.Contains(number) || number == catalogNumber || !classified.Add(number))
                    {
                        continue;
                    }

                    part9Order.Add(number);
                    queue.Enqueue(OriginalValue(number));
                }
            }
        }

        // ---- Phase 2: assign final numbers — second group (parts 7-9) from 1, first -------
        // ---- group (parts 2/4/6) after it, hint stream last (§F.3.1/§F.3.6). ---------------
        var finalNumber = new Dictionary<int, int>();
        var next = 1;

        var part7Groups = new List<List<int>>();
        for (var k = 1; k < pages.Count; k++)
        {
            var group = new List<int> { pages[k].Reference.Number };
            foreach (var number in remainingOrders[k])
            {
                if (!firstPageSet.Contains(number) && owner[number] == k)
                {
                    group.Add(number);
                }
            }

            part7Groups.Add(group);
            foreach (var number in group)
            {
                finalNumber[number] = next++;
            }
        }

        foreach (var number in part8Order)
        {
            finalNumber[number] = next++;
        }

        foreach (var number in part9Order)
        {
            finalNumber[number] = next++;
        }

        var pagesRootFinal = next++;
        var secondGroupCount = next - 1;

        var linearizationDictionaryNumber = next++;
        var catalogFinal = next++;
        if (catalogNumber is int realCatalogNumber)
        {
            finalNumber[realCatalogNumber] = catalogFinal;
        }

        foreach (var number in docLevelOrder)
        {
            finalNumber[number] = next++;
        }

        finalNumber[pages[0].Reference.Number] = next++;
        var firstPageFinal = finalNumber[pages[0].Reference.Number];
        foreach (var number in firstPageOrder)
        {
            finalNumber[number] = next++;
        }

        var hintStreamNumber = next;
        var totalObjects = hintStreamNumber;
        var trailerSize = totalObjects + 1;
        var firstGroupCount = totalObjects - secondGroupCount;

        // ---- Phase 3: final object values (fresh /Pages root, final page/catalog copies). --
        var syntheticReferences = new HashSet<PdfReference>();
        var pagesRootReference = new PdfReference(new IndirectReference(pagesRootFinal, 0));
        syntheticReferences.Add(pagesRootReference);

        var kids = new PdfArray();
        foreach (var (reference, _) in pages)
        {
            var kidReference = new PdfReference(new IndirectReference(finalNumber[reference.Number], 0));
            syntheticReferences.Add(kidReference);
            kids.Add(kidReference);
        }

        var pagesRootDict = new PdfDictionary();
        pagesRootDict.Set(PdfName.Type, PagesName);
        pagesRootDict.Set(KidsName, kids);
        pagesRootDict.Set(CountName, PdfNumber.Get(pages.Count));

        var overrides = new Dictionary<int, PdfObject>();
        foreach (var (reference, dictionary) in pages)
        {
            overrides[reference.Number] = FullRewriteWriter.BuildPageCopy(dictionary, pagesRootReference);
        }

        var catalogValue = FullRewriteWriter.BuildCatalogCopy(catalogDictionary ?? new PdfDictionary(), pagesRootReference);

        PdfObject FinalValue(int originalNumber) =>
            overrides.TryGetValue(originalNumber, out var overridden) ? overridden : objects[new IndirectReference(originalNumber, 0)];

        IndirectReference Translate(PdfReference reference) =>
            syntheticReferences.Contains(reference)
                ? reference.Target
                : finalNumber.TryGetValue(reference.Target.Number, out var translated)
                    ? new IndirectReference(translated, 0)
                    : throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: a reference to object {reference.Target.Number} was encountered outside the discovered graph while linearizing.");

        // File-order body sequences (part 5, the hint stream, is inserted between parts 4
        // and 6 at write time — its object number is already reserved as the last).
        var part4Sequence = new List<(int Number, PdfObject Value)> { (catalogFinal, catalogValue) };
        part4Sequence.AddRange(docLevelOrder.Select(n => (finalNumber[n], FinalValue(n))));

        var part6Sequence = new List<(int Number, PdfObject Value)> { (firstPageFinal, FinalValue(pages[0].Reference.Number)) };
        part6Sequence.AddRange(firstPageOrder.Select(n => (finalNumber[n], FinalValue(n))));

        var part7Sequences = part7Groups
            .Select(group => group.Select(n => (Number: finalNumber[n], Value: FinalValue(n))).ToList())
            .ToList();

        var part8Sequence = part8Order.Select(n => (Number: finalNumber[n], Value: FinalValue(n))).ToList();

        var part9Sequence = part9Order.Select(n => (Number: finalNumber[n], Value: FinalValue(n))).ToList();
        part9Sequence.Add((pagesRootFinal, pagesRootDict));

        // ---- Phase 4: measure every object's exact serialized length. ----------------------
        var objectLength = new long[trailerSize];

        long Measure(int number, PdfObject value)
        {
            var counter = new CountingStream();
            ObjectSerializer.WriteIndirectObject(counter, number, 0, value, Translate);
            return counter.Position;
        }

        foreach (var (number, value) in part4Sequence.Concat(part6Sequence).Concat(part7Sequences.SelectMany(static s => s)).Concat(part8Sequence).Concat(part9Sequence))
        {
            objectLength[number] = Measure(number, value);
        }

        // ---- Phase 5: layout arithmetic (hint-stream-elided first, actual after). ----------
        var headerBytes = BuildHeader(options);
        var documentId = DeterministicContext.CreateDocumentId(options.Deterministic);
        int? infoFinal = objects.Trailer.TryGetValue(PdfName.Info, out var trailerInfo)
            && trailerInfo is PdfReference trailerInfoRef
            && finalNumber.TryGetValue(trailerInfoRef.Target.Number, out var mappedInfo)
            ? mappedInfo
            : null;

        var linearizationDictionaryLength = BuildLinearizationDictionary(linearizationDictionaryNumber, 0, 0, 0, firstPageFinal, 0, pages.Count, 0).Length;
        var firstXrefOffset = headerBytes.Length + linearizationDictionaryLength;
        var firstXrefHeaderLength = Encoding.ASCII.GetByteCount($"xref\n{linearizationDictionaryNumber} {firstGroupCount}\n");
        var firstTrailerLength = BuildFirstTrailer(trailerSize, 0, catalogFinal, infoFinal, documentId).Length;
        var part4Offset = firstXrefOffset + firstXrefHeaderLength + (20L * firstGroupCount) + firstTrailerLength;

        var elidedOffsets = new long[trailerSize];
        var cursor = part4Offset;
        foreach (var (number, _) in part4Sequence)
        {
            elidedOffsets[number] = cursor;
            cursor += objectLength[number];
        }

        var hintStreamOffset = cursor; // part 5 sits here; everything below is elided (as if it were absent).
        var part6Start = cursor;
        foreach (var (number, _) in part6Sequence)
        {
            elidedOffsets[number] = cursor;
            cursor += objectLength[number];
        }

        var part6End = cursor;
        var part7Starts = new long[part7Sequences.Count];
        var part7Lengths = new long[part7Sequences.Count];
        for (var i = 0; i < part7Sequences.Count; i++)
        {
            part7Starts[i] = cursor;
            foreach (var (number, _) in part7Sequences[i])
            {
                elidedOffsets[number] = cursor;
                cursor += objectLength[number];
            }

            part7Lengths[i] = cursor - part7Starts[i];
        }

        var sharedSectionLocation = cursor;
        foreach (var (number, _) in part8Sequence)
        {
            elidedOffsets[number] = cursor;
            cursor += objectLength[number];
        }

        foreach (var (number, _) in part9Sequence)
        {
            elidedOffsets[number] = cursor;
            cursor += objectLength[number];
        }

        var elidedBodyEnd = cursor;

        // ---- Phase 6: hint tables (elided coordinates only, §F.4). --------------------------
        var sharedIndexByOriginal = new Dictionary<int, int>();
        for (var i = 0; i < firstPageOrder.Count; i++)
        {
            sharedIndexByOriginal[firstPageOrder[i]] = i + 1; // index 0 is the first page object itself
        }

        for (var i = 0; i < part8Order.Count; i++)
        {
            sharedIndexByOriginal[part8Order[i]] = 1 + firstPageOrder.Count + i;
        }

        int? ContentStreamFinalNumber(int pageOriginalNumber)
        {
            if (overrides[pageOriginalNumber] is not PdfDictionary pageCopy
                || !pageCopy.TryGetValue(ContentsName, out var contentsValue))
            {
                return null;
            }

            var contentsReference = contentsValue as PdfReference
                ?? (contentsValue is PdfArray { Count: > 0 } contentsArray ? contentsArray[0] as PdfReference : null);
            return contentsReference is not null
                && !syntheticReferences.Contains(contentsReference)
                && finalNumber.TryGetValue(contentsReference.Target.Number, out var content)
                ? content
                : null;
        }

        var pageHints = new LinearizationHintBuilder.PageHint[pages.Count];
        pageHints[0] = BuildPageHint(part6Sequence, part6End - part6Start, objectLength, [], ContentStreamFinalNumber(pages[0].Reference.Number));
        for (var k = 1; k < pages.Count; k++)
        {
            var identifiers = remainingOrders[k]
                .Where(sharedIndexByOriginal.ContainsKey)
                .Select(n => sharedIndexByOriginal[n])
                .Order()
                .ToArray();
            pageHints[k] = BuildPageHint(part7Sequences[k - 1], part7Lengths[k - 1], objectLength, identifiers, ContentStreamFinalNumber(pages[k].Reference.Number));
        }

        // Table F.5 item 1 — with an empty shared objects section, the number its first
        // object would have carried (the next one after the last remaining-page object).
        var firstSharedObjectNumber = part8Sequence.Count > 0
            ? part8Sequence[0].Number
            : part7Groups.Sum(static g => g.Count) + 1;

        LinearizationHintBuilder.OutlineGroup? outlineGroup = null;
        if (outlineOrder.Count > 0)
        {
            var firstOutlineFinal = finalNumber[outlineOrder[0]];
            outlineGroup = new LinearizationHintBuilder.OutlineGroup(
                firstOutlineFinal,
                elidedOffsets[firstOutlineFinal],
                outlineOrder.Count,
                outlineOrder.Sum(n => objectLength[finalNumber[n]]));
        }

        var (hintPayload, sharedTableOffset, outlineTableOffset) = LinearizationHintBuilder.Build(
            pageHints,
            part6Start,
            [objectLength[firstPageFinal], .. firstPageOrder.Select(n => objectLength[finalNumber[n]])],
            part8Sequence.Select(pair => objectLength[pair.Number]).ToList(),
            firstSharedObjectNumber,
            sharedSectionLocation,
            outlineGroup);

        var hintDictionary = new PdfDictionary();
        hintDictionary.Set(SName, PdfNumber.Get(sharedTableOffset));
        if (outlineTableOffset is int outlineOffset)
        {
            hintDictionary.Set(OName, PdfNumber.Get(outlineOffset));
        }

        hintDictionary.Set(PdfName.Length, PdfNumber.Get(hintPayload.Length));
        var hintStream = new PdfStream(hintDictionary, hintPayload);
        var hintStreamLength = Measure(hintStreamNumber, hintStream);

        // ---- Phase 7: actual offsets and the linearization parameters. ---------------------
        // The hint stream itself sits AT hintStreamOffset (its own offset is never shifted);
        // every elided position at-or-after the insertion point belongs to a later part and
        // shifts by the hint stream's full serialized length.
        long Actual(long elided) => elided >= hintStreamOffset ? elided + hintStreamLength : elided;

        var mainXrefOffset = elidedBodyEnd + hintStreamLength;
        var mainXrefHeader = $"xref\n0 {secondGroupCount + 1}\n";
        var mainTrailer = $"trailer\n<< /Size {secondGroupCount + 1} >>\nstartxref\n{firstXrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF";
        var fileLength = mainXrefOffset + mainXrefHeader.Length + (20L * (secondGroupCount + 1)) + mainTrailer.Length;
        var endOfFirstPage = Actual(part6End);
        var mainTableFirstEntryOffset = mainXrefOffset + mainXrefHeader.Length - 1; // the white-space character preceding the first entry (Table F.1, /T)

        // ---- Phase 8: the single front-to-back write, with drift assertions. ---------------
        void AssertPosition(long expected, string what)
        {
            if (output.Position != expected)
            {
                throw new PlumePdfException("PLUME5010", $"Internal writer invariant violated: {what} landed at offset {output.Position}, but the linearizer's layout computed {expected} — measurement and serialization disagree.");
            }
        }

        output.Write(headerBytes);
        output.Write(BuildLinearizationDictionary(linearizationDictionaryNumber, fileLength, hintStreamOffset, hintStreamLength, firstPageFinal, endOfFirstPage, pages.Count, mainTableFirstEntryOffset));
        AssertPosition(firstXrefOffset, "the first-page cross-reference table");

        WriteAscii(output, $"xref\n{linearizationDictionaryNumber} {firstGroupCount}\n");
        WriteXrefEntry(output, headerBytes.Length); // the linearization parameter dictionary
        foreach (var (number, _) in part4Sequence)
        {
            WriteXrefEntry(output, Actual(elidedOffsets[number]));
        }

        foreach (var (number, _) in part6Sequence)
        {
            WriteXrefEntry(output, Actual(elidedOffsets[number]));
        }

        WriteXrefEntry(output, hintStreamOffset); // the hint stream carries the last object number (§F.3.6)
        output.Write(BuildFirstTrailer(trailerSize, mainXrefOffset, catalogFinal, infoFinal, documentId));
        AssertPosition(part4Offset, "part 4 (the document catalogue)");

        foreach (var (number, value) in part4Sequence)
        {
            ObjectSerializer.WriteIndirectObject(output, number, 0, value, Translate);
        }

        AssertPosition(hintStreamOffset, "part 5 (the primary hint stream)");
        ObjectSerializer.WriteIndirectObject(output, hintStreamNumber, 0, hintStream, Translate);

        AssertPosition(Actual(part6Start), "part 6 (the first-page section)");
        foreach (var (number, value) in part6Sequence.Concat(part7Sequences.SelectMany(static s => s)).Concat(part8Sequence).Concat(part9Sequence))
        {
            AssertPosition(Actual(elidedOffsets[number]), $"object {number}");
            ObjectSerializer.WriteIndirectObject(output, number, 0, value, Translate);
        }

        AssertPosition(mainXrefOffset, "part 11 (the main cross-reference table)");
        WriteAscii(output, mainXrefHeader);
        WriteAscii(output, "0000000000 65535 f \n");
        for (var number = 1; number <= secondGroupCount; number++)
        {
            WriteXrefEntry(output, Actual(elidedOffsets[number]));
        }

        WriteAscii(output, mainTrailer);
        AssertPosition(fileLength, "the end of the file (/L)");
    }

    private static LinearizationHintBuilder.PageHint BuildPageHint(
        List<(int Number, PdfObject Value)> group,
        long groupLength,
        long[] objectLength,
        int[] sharedIdentifiers,
        int? contentStreamFinalNumber)
    {
        long contentOffset = 0;
        long contentLength = 0;
        if (contentStreamFinalNumber is int contentNumber)
        {
            long offset = 0;
            foreach (var (number, _) in group)
            {
                if (number == contentNumber)
                {
                    contentOffset = offset;
                    contentLength = objectLength[number];
                    break;
                }

                offset += objectLength[number];
            }
        }

        return new LinearizationHintBuilder.PageHint(group.Count, groupLength, contentOffset, contentLength, sharedIdentifiers);
    }

    private static byte[] BuildHeader(PdfOptions options)
    {
        using var buffer = new MemoryStream();
        var version = string.IsNullOrWhiteSpace(options.PdfVersion) ? "1.7" : options.PdfVersion;
        WriteAscii(buffer, $"%PDF-{version}\n%");
        buffer.Write([0xE2, 0xE3, 0xCF, 0xD3]);
        buffer.Write("\n"u8);
        return buffer.ToArray();
    }

    // The five values measured against bytes that sit after this dictionary (/L, /H's two
    // integers, /E, /T) are zero-padded to a fixed 10-digit width so the dictionary's own
    // byte length never depends on them (§7.3.3 permits an integer any number of leading
    // zeros); /O and /N are known before layout and written plainly. All values are direct
    // and the whole object sits well inside Annex F's first-1024-bytes requirement (§F.3.3).
    private static byte[] BuildLinearizationDictionary(int number, long fileLength, long hintOffset, long hintLength, int firstPageObjectNumber, long endOfFirstPage, int pageCount, long mainTableFirstEntryOffset) =>
        Encoding.ASCII.GetBytes(
            $"{number} 0 obj\n<< /Linearized 1 /L {Pad(fileLength)} /H [ {Pad(hintOffset)} {Pad(hintLength)} ] /O {firstPageObjectNumber} /E {Pad(endOfFirstPage)} /N {pageCount} /T {Pad(mainTableFirstEntryOffset)} >>\nendobj\n");

    // /Prev is the one first-page-trailer value measured against bytes after it — padded for
    // the same reason as the linearization dictionary's offsets. The trailing
    // "startxref 0 %%EOF" is Annex F's dummy (§F.3.4: "shall be ignored").
    private static byte[] BuildFirstTrailer(int size, long mainXrefOffset, int catalogNumber, int? infoNumber, byte[] documentId)
    {
        var id = Convert.ToHexString(documentId);
        var info = infoNumber is int i ? $" /Info {i} 0 R" : string.Empty;
        return Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {size} /Prev {Pad(mainXrefOffset)} /Root {catalogNumber} 0 R{info} /ID [<{id}> <{id}>] >>\nstartxref\n0\n%%EOF\n");
    }

    private static string Pad(long value) => value.ToString("D10", CultureInfo.InvariantCulture);

    private static void WriteXrefEntry(Stream output, long offset) =>
        WriteAscii(output, $"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");

    private static void WriteAscii(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));

    /// <summary>A write-only sink that discards bytes and counts them — the measurement pass's stream.</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position
        {
            get => _length;
            set => throw new NotSupportedException("CountingStream is forward-only.");
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("CountingStream is write-only.");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("CountingStream is forward-only.");

        public override void SetLength(long value) => throw new NotSupportedException("CountingStream is forward-only.");

        public override void Write(byte[] buffer, int offset, int count) => _length += count;

        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;

        public override void WriteByte(byte value) => _length++;
    }
}
