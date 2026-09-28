using System.Text;

namespace PlumePdf.Documents.Redaction;

/// <summary>
/// Scrubs every non-content-stream surface a text/pattern <see cref="RedactionTarget"/> can
/// match (the enumerated scrub-surface list): the Document
/// Information Dictionary, the XMP metadata packet, annotation <c>/Contents</c>, outline
/// (bookmark) titles, embedded-file attachments (name-tree keys, filespec names/descriptions,
/// and the embedded <c>/EF</c> stream bytes themselves — flat and <c>/Kids</c>-branched name
/// trees alike), the source document's structure tree (<c>/ActualText</c>, <c>/Alt</c>, and
/// element <c>/T</c> titles — a ratified scrub surface), and the catalog- and page-level
/// <c>/PieceInfo</c> private-application dictionaries. Region-only targets never touch any of
/// these (they carry no text to match against). Whichever surface a match is found on is
/// wiped in full (the whole string, the whole packet, the whole dictionary, the whole
/// attachment) rather than surgically editing inside it — the same "cannot under-redact" bias
/// <see cref="Content.ContentStreamEditor"/> applies to a whole Form XObject.
/// </summary>
/// <remarks>
/// One documented v1.0 carve-out (maintainer-ratified): an embedded font
/// subset's own glyph-outline data can still carry the redacted text's shapes even after
/// every surface above is scrubbed — glyph outlines with no positioning information do not
/// reconstruct the redacted string on their own, and re-subsetting a font to drop unused
/// glyphs is 1.x work (no font-subset rewrite seam exists yet).
/// </remarks>
internal static class MetadataScrubber
{
    private static readonly PdfName TitleName = PdfName.Get("Title");
    private static readonly PdfName AuthorName = PdfName.Get("Author");
    private static readonly PdfName SubjectName = PdfName.Get("Subject");
    private static readonly PdfName KeywordsName = PdfName.Get("Keywords");
    private static readonly PdfName CreatorName = PdfName.Get("Creator");
    private static readonly PdfName ProducerName = PdfName.Get("Producer");
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName AnnotsName = PdfName.Get("Annots");
    private static readonly PdfName OutlinesName = PdfName.Get("Outlines");
    private static readonly PdfName FirstName = PdfName.Get("First");
    private static readonly PdfName NextName = PdfName.Get("Next");
    private static readonly PdfName NamesName = PdfName.Get("Names");
    private static readonly PdfName EmbeddedFilesName = PdfName.Get("EmbeddedFiles");
    private static readonly PdfName DescName = PdfName.Get("Desc");
    private static readonly PdfName UFName = PdfName.Get("UF");
    private static readonly PdfName FName = PdfName.Get("F");
    private static readonly PdfName PieceInfoName = PdfName.Get("PieceInfo");
    private static readonly PdfName KidsName = PdfName.Kids;
    private static readonly PdfName LimitsName = PdfName.Get("Limits");
    private static readonly PdfName EFName = PdfName.Get("EF");
    private static readonly PdfName RFName = PdfName.Get("RF");
    private static readonly PdfName StructTreeRootName = PdfName.Get("StructTreeRoot");
    private static readonly PdfName KName = PdfName.Get("K");
    private static readonly PdfName ActualTextName = PdfName.Get("ActualText");
    private static readonly PdfName AltName = PdfName.Get("Alt");
    private static readonly PdfName TName = PdfName.T;

    private static readonly PdfName[] DocInfoTextFields = [TitleName, AuthorName, SubjectName, KeywordsName, CreatorName, ProducerName];

    private static readonly PdfName[] StructureTextFields = [ActualTextName, AltName, TName];

    /// <summary>Scrubs every surface this type documents against <paramref name="targets"/>'s text/pattern entries, returning a per-surface match count (only non-zero surfaces appear).</summary>
    public static IReadOnlyDictionary<string, int> Scrub(PdfDocument document, IReadOnlyList<RedactionTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(targets);

        var matchers = targets.Where(static t => t.Kind != RedactionTargetKind.Region).ToList();
        var counts = new Dictionary<string, int>();
        if (matchers.Count == 0)
        {
            return counts;
        }

        ScrubDocInfo(document, matchers, counts);
        ScrubXmp(document, matchers, counts);
        ScrubAnnotationContents(document, matchers, counts);
        ScrubOutlineTitles(document, matchers, counts);
        ScrubEmbeddedFileNames(document, matchers, counts);
        ScrubStructureTree(document, matchers, counts);
        ScrubPieceInfo(document, matchers, counts);

        return counts;
    }

    private static void ScrubDocInfo(PdfDocument document, List<RedactionTarget> matchers, Dictionary<string, int> counts)
    {
        var trailer = document.Objects.Trailer;
        if (!trailer.TryGetValue(PdfName.Info, out var infoValue) || Resolve(document, infoValue) is not PdfDictionary info)
        {
            return;
        }

        var touched = 0;
        foreach (var field in DocInfoTextFields)
        {
            if (info.TryGetValue(field, out var value) && value is PdfString s && AnyMatches(matchers, s.GetText()))
            {
                info.Remove(field);
                touched++;
            }
        }

        if (touched > 0)
        {
            if (infoValue is PdfReference infoReference)
            {
                document.Objects.MarkDirty(infoReference.Target);
            }

            counts["DocInfo"] = touched;
        }
    }

    private static void ScrubXmp(PdfDocument document, List<RedactionTarget> matchers, Dictionary<string, int> counts)
    {
        if (document.Catalog is not { } catalog || !catalog.Dictionary.TryGetValue(PdfName.Metadata, out var metadataValue) || metadataValue is not PdfReference reference)
        {
            return;
        }

        if (document.Objects[reference.Target] is not PdfStream stream)
        {
            return;
        }

        byte[] decoded;
        try
        {
            decoded = stream.GetDecodedBytes(document.Options.Filters, document.Options, r => document.Objects[r]);
        }
        catch (PlumePdfException)
        {
            return;
        }

        var text = Encoding.UTF8.GetString(decoded);
        if (!AnyMatches(matchers, text))
        {
            return;
        }

        // Whole-packet wipe rather than XML surgery (this file's remarks) — a minimal empty XMP
        // packet is still well-formed RDF/XML so a caller reading it back sees an empty packet,
        // never truncated/invalid XML.
        var empty = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"/></x:xmpmeta><?xpacket end=\"w\"?>"u8.ToArray();
        document.Objects.RegisterNew(reference.Target, new PdfStream(stream.Dictionary, empty));
        counts["Xmp"] = 1;
    }

    private static void ScrubAnnotationContents(PdfDocument document, List<RedactionTarget> matchers, Dictionary<string, int> counts)
    {
        var touched = 0;
        foreach (var page in document.Pages)
        {
            if (!page.Dictionary.TryGetValue(AnnotsName, out var annotsValue) || Resolve(document, annotsValue) is not PdfArray annots)
            {
                continue;
            }

            foreach (var entry in annots)
            {
                if (entry is not PdfReference annotRef || document.Objects[annotRef.Target] is not PdfDictionary annot)
                {
                    continue;
                }

                if (annot.TryGetValue(ContentsName, out var contentsValue) && contentsValue is PdfString s && AnyMatches(matchers, s.GetText()))
                {
                    annot.Remove(ContentsName);
                    document.Objects.MarkDirty(annotRef.Target);
                    touched++;
                }
            }
        }

        if (touched > 0)
        {
            counts["AnnotationContents"] = touched;
        }
    }

    private static void ScrubOutlineTitles(PdfDocument document, List<RedactionTarget> matchers, Dictionary<string, int> counts)
    {
        if (document.Catalog is not { } catalog || !catalog.Dictionary.TryGetValue(OutlinesName, out var outlinesValue) || Resolve(document, outlinesValue) is not PdfDictionary outlines)
        {
            return;
        }

        if (!outlines.TryGetValue(FirstName, out var firstValue) || firstValue is not PdfReference firstRef)
        {
            return;
        }

        var touched = 0;
        var visited = new HashSet<IndirectReference>();

        // Explicit-stack depth-first walk visiting every node — both the /First child chain and
        // every /Next sibling at every level — bounded by the visited set against a malformed
        // or cyclic outline tree.
        var pending = new Stack<IndirectReference>();
        pending.Push(firstRef.Target);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current) || document.Objects[current] is not PdfDictionary node)
            {
                continue;
            }

            if (node.TryGetValue(TitleName, out var titleValue) && titleValue is PdfString titleString && AnyMatches(matchers, titleString.GetText()))
            {
                node.Set(TitleName, PdfString.FromLiteral([]));
                document.Objects.MarkDirty(current);
                touched++;
            }

            if (node.TryGetValue(NextName, out var nextValue) && nextValue is PdfReference nextRef)
            {
                pending.Push(nextRef.Target);
            }

            if (node.TryGetValue(FirstName, out var childValue) && childValue is PdfReference childRef)
            {
                pending.Push(childRef.Target);
            }
        }

        if (touched > 0)
        {
            counts["OutlineTitles"] = touched;
        }
    }

    private static void ScrubEmbeddedFileNames(PdfDocument document, List<RedactionTarget> matchers, Dictionary<string, int> counts)
    {
        if (document.Catalog is not { } catalog || !catalog.Dictionary.TryGetValue(PdfName.Get("Names"), out var namesValue) || Resolve(document, namesValue) is not PdfDictionary namesDict)
        {
            return;
        }

        if (!namesDict.TryGetValue(EmbeddedFilesName, out var efValue) || Resolve(document, efValue) is not PdfDictionary ef)
        {
            return;
        }

        // The /EmbeddedFiles name tree may be flat (a /Names leaf at the root) or branched
        // (/Kids of intermediate nodes, ISO 32000-1 §7.9.6) — walk every node, the same
        // cycle-guarded explicit-stack shape the outline walk above uses.
        var touched = 0;
        var visited = new HashSet<IndirectReference>();
        var pending = new Stack<(PdfDictionary Node, IndirectReference? Reference)>();
        pending.Push((ef, (efValue as PdfReference)?.Target));
        if (efValue is PdfReference efReference)
        {
            visited.Add(efReference.Target);
        }

        while (pending.TryPop(out var current))
        {
            var (node, nodeReference) = current;

            if (node.TryGetValue(KidsName, out var kidsValue) && Resolve(document, kidsValue) is PdfArray kids)
            {
                foreach (var kid in kids)
                {
                    if (kid is PdfReference kidReference && visited.Add(kidReference.Target)
                        && document.Objects[kidReference.Target] is PdfDictionary kidNode)
                    {
                        pending.Push((kidNode, kidReference.Target));
                    }
                }
            }

            touched += ScrubEmbeddedFilesLeaf(document, node, nodeReference, matchers);
        }

        if (touched > 0)
        {
            counts["EmbeddedFileNames"] = touched;
        }
    }

    private static int ScrubEmbeddedFilesLeaf(PdfDocument document, PdfDictionary node, IndirectReference? nodeReference, List<RedactionTarget> matchers)
    {
        if (!node.TryGetValue(NamesName, out var flatValue) || Resolve(document, flatValue) is not PdfArray flat)
        {
            return 0;
        }

        var touched = 0;
        var nodeChanged = false;
        for (var i = 1; i < flat.Count; i += 2)
        {
            if (flat[i] is not PdfReference fileSpecRef || document.Objects[fileSpecRef.Target] is not PdfDictionary fileSpec)
            {
                continue;
            }

            var matched = flat[i - 1] is PdfString nameKey && AnyMatches(matchers, nameKey.GetText());

            if (fileSpec.TryGetValue(UFName, out var ufValue) && ufValue is PdfString uf && AnyMatches(matchers, uf.GetText()))
            {
                matched = true;
            }

            if (fileSpec.TryGetValue(FName, out var fValue) && fValue is PdfString f && AnyMatches(matchers, f.GetText()))
            {
                matched = true;
            }

            if (fileSpec.TryGetValue(DescName, out var descValue) && descValue is PdfString desc && AnyMatches(matchers, desc.GetText()))
            {
                matched = true;
            }

            if (!matched)
            {
                continue;
            }

            // The name-tree key string itself carries the matched name — blank it too, or the
            // "scrubbed" name survives in the tree that indexed it.
            if (flat[i - 1] is PdfString)
            {
                flat[i - 1] = PdfString.FromLiteral([]);
                nodeChanged = true;
            }

            WipeEmbeddedFileStreams(document, fileSpec);
            fileSpec.Remove(UFName);
            fileSpec.Remove(FName);
            fileSpec.Remove(DescName);
            fileSpec.Remove(EFName);
            fileSpec.Remove(RFName);
            document.Objects.MarkDirty(fileSpecRef.Target);
            touched++;
        }

        // /Limits duplicates the node's least/greatest key strings (§7.9.6) — a blanked key
        // must not survive there either.
        if (touched > 0 && node.TryGetValue(LimitsName, out var limitsValue) && Resolve(document, limitsValue) is PdfArray limits)
        {
            for (var i = 0; i < limits.Count; i++)
            {
                if (limits[i] is PdfString limit && AnyMatches(matchers, limit.GetText()))
                {
                    limits[i] = PdfString.FromLiteral([]);
                    nodeChanged = true;
                }
            }
        }

        if (nodeChanged)
        {
            if (flatValue is PdfReference flatReference)
            {
                document.Objects.MarkDirty(flatReference.Target);
            }

            if (nodeReference is { } reference)
            {
                document.Objects.MarkDirty(reference);
            }
        }

        return touched;
    }

    // Drops the attachment bytes themselves: every stream the filespec's /EF dictionary
    // references (any key — /F, /UF, and the legacy platform variants) is shadowed with an
    // empty stream, so a full-rewrite Save can never serialize the original embedded bytes
    // even if some other object still references them.
    private static void WipeEmbeddedFileStreams(PdfDocument document, PdfDictionary fileSpec)
    {
        if (!fileSpec.TryGetValue(EFName, out var efValue) || Resolve(document, efValue) is not PdfDictionary ef)
        {
            return;
        }

        foreach (var (_, streamValue) in ef)
        {
            if (streamValue is PdfReference streamReference && document.Objects[streamReference.Target] is PdfStream)
            {
                document.Objects.RegisterNew(streamReference.Target, new PdfStream(new PdfDictionary(), Array.Empty<byte>()));
            }
        }
    }

    private static void ScrubPieceInfo(PdfDocument document, List<RedactionTarget> matchers, Dictionary<string, int> counts)
    {
        // /PieceInfo carries private, application-specific data (ISO 32000-1 Table 359) with no
        // guaranteed text-bearing shape to match against — v1.0's opportunistic policy is: if
        // any target matched anything else on this document, remove every /PieceInfo wholesale,
        // catalog-level and page-level alike (§14.5 allows both; private app data a redacted
        // document should not keep regardless of whether PlumePDF can parse its contents).
        if (matchers.Count == 0 || counts.Count == 0)
        {
            return;
        }

        var removed = 0;
        if (document.Catalog is { } catalog && catalog.Dictionary.Remove(PieceInfoName))
        {
            document.Objects.MarkDirty(catalog.Reference);
            removed++;
        }

        foreach (var page in document.Pages)
        {
            if (page.Dictionary.Remove(PieceInfoName))
            {
                document.Objects.MarkDirty(page.Reference);
                removed++;
            }
        }

        if (removed > 0)
        {
            counts["PieceInfo"] = removed;
        }
    }

    /// <summary>
    /// Scrubs matching <c>/ActualText</c>, <c>/Alt</c>, and element-<c>/T</c> (title) string
    /// values throughout the source document's structure tree (ISO 32000-1 §14.7.2) — a
    /// ratified scrub surface: a tagged source carries the redacted text's replacement/
    /// alternate strings in the tree even after the painted glyphs are gone from the content
    /// stream. Walks the raw <c>/K</c> graph defensively (never trusting a document-supplied
    /// tree shape): explicit stack, cycle guard on every indirect node, and the same
    /// <c>PdfOptions.MaxStructureTreeDepth</c>/<c>MaxStructureElementCount</c> caps the
    /// structure-tree reader uses — a cap hit is a coded refusal (<c>PLUME6076</c>), never a
    /// silent stop that would leave the rest of the tree unscrubbed.
    /// </summary>
    private static void ScrubStructureTree(PdfDocument document, List<RedactionTarget> matchers, Dictionary<string, int> counts)
    {
        if (document.Catalog is not { } catalog || !catalog.Dictionary.TryGetValue(StructTreeRootName, out var rootValue) || Resolve(document, rootValue) is not PdfDictionary root)
        {
            return;
        }

        var touched = 0;
        var visitedCount = 0;
        var visited = new HashSet<IndirectReference>();
        var pending = new Stack<(PdfDictionary Node, IndirectReference? Reference, int Depth)>();
        if (rootValue is PdfReference rootReference)
        {
            visited.Add(rootReference.Target);
        }

        pending.Push((root, (rootValue as PdfReference)?.Target, 0));

        while (pending.TryPop(out var current))
        {
            var (node, nodeReference, depth) = current;
            if (depth > document.Options.MaxStructureTreeDepth || ++visitedCount > document.Options.MaxStructureElementCount)
            {
                throw new PlumePdfException("PLUME6076", $"Redaction's structure-tree scrub exceeded the configured caps (PdfOptions.MaxStructureTreeDepth {document.Options.MaxStructureTreeDepth} / MaxStructureElementCount {document.Options.MaxStructureElementCount}); refusing to continue rather than leaving part of the tree unscrubbed (a silent stop here would be under-redaction).");
            }

            var nodeChanged = false;
            foreach (var field in StructureTextFields)
            {
                if (node.TryGetValue(field, out var value) && value is PdfString s && AnyMatches(matchers, s.GetText()))
                {
                    node.Set(field, PdfString.FromLiteral([]));
                    nodeChanged = true;
                    touched++;
                }
            }

            if (nodeChanged && nodeReference is { } reference)
            {
                document.Objects.MarkDirty(reference);
            }

            if (node.TryGetValue(KName, out var kidsValue))
            {
                PushStructureChildren(document, kidsValue, visited, pending, depth + 1);
            }
        }

        if (touched > 0)
        {
            counts["StructureTree"] = touched;
        }
    }

    // A /K value may be a marked-content id number (nothing to scrub), one child (dictionary,
    // directly or by reference), or an array of any mix of those (§14.7.2, Table 323) — every
    // shape is handled without trusting the document to pick one consistently.
    private static void PushStructureChildren(PdfDocument document, PdfObject kidsValue, HashSet<IndirectReference> visited, Stack<(PdfDictionary Node, IndirectReference? Reference, int Depth)> pending, int depth)
    {
        switch (kidsValue)
        {
            case PdfDictionary direct:
                pending.Push((direct, null, depth));
                break;

            case PdfReference reference when visited.Add(reference.Target) && document.Objects[reference.Target] is PdfObject resolved:
                if (resolved is PdfDictionary node)
                {
                    pending.Push((node, reference.Target, depth));
                }
                else if (resolved is PdfArray indirectArray)
                {
                    foreach (var element in indirectArray)
                    {
                        PushStructureChildren(document, element, visited, pending, depth);
                    }
                }

                break;

            case PdfArray array:
                foreach (var element in array)
                {
                    PushStructureChildren(document, element, visited, pending, depth);
                }

                break;
        }
    }

    private static bool AnyMatches(List<RedactionTarget> matchers, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var target in matchers)
        {
            if (target.Kind == RedactionTargetKind.Pattern)
            {
                if (target.MatchPattern!.IsMatch(text))
                {
                    return true;
                }
            }
            else if (target.MatchText is { Length: > 0 } needle && text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static PdfObject? Resolve(PdfDocument document, PdfObject value) =>
        value is PdfReference reference ? document.Objects[reference.Target] : value;
}
