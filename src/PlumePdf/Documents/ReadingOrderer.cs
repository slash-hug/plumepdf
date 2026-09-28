using PlumePdf.Documents.Structure;

namespace PlumePdf.Documents;

// Provenance (the clean-room policy in AGENTS.md; NOTICE): the content-order-with-geometric-line-
// grouping baseline is adapted from the shape of PdfPig's ContentOrderTextExtractor
// (UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor, Apache-2.0,
// https://github.com/UglyToad/PdfPig) - words sharing a baseline band become a line, lines
// order top-to-bottom. The two-column gutter detector below is PlumePDF's own (a
// deliberate commitment to one fixed default heuristic, not a ported page-segmenter family).

/// <summary>
/// Orders a page's words into lines and lines into reading order:
/// words sharing a baseline band become one <see cref="ExtractedLine"/>, ordered
/// in the page's dominant writing direction; lines order from the "leading" edge of that
/// direction backward, with a simple two-column gutter detector splitting first-column lines
/// from second-column lines when the page's word layout shows a persistent empty band through
/// the horizontal middle third of the direction the lines run along.
/// </summary>
/// <remarks>
/// Every projection below is expressed relative to the page's dominant text-writing direction
/// (<see cref="Letter.DirectionX"/>/<see cref="Letter.DirectionY"/> — <c>(1,0)</c> for ordinary
/// unrotated horizontal text, something else under a page's <c>/Rotate</c> normalization)
/// rather than hardcoding the page's raw X/Y axes: "along" is the coordinate advancing in
/// reading order (word order within a line, and the column-split axis), "perpendicular" is the
/// coordinate that separates one line from the next. For ordinary horizontal text this
/// degenerates to exactly the axis-aligned X/Y math the geometry always used; for a page
/// rotated 90°/180°/270° (<c>PageSpace.NormalizationMatrix</c> is always a proper, orientation-
/// preserving rotation for those four angles) the same formulas keep working because the
/// direction vector rotates along with the content, so "along"/"perpendicular" always mean the
/// same thing relative to the text, whichever way the page itself is turned. This is one fixed
/// default heuristic, not a pluggable segmenter family (deliberately out of scope) — a caller
/// who disagrees rebuilds ordering from <see cref="ExtractedText.Letters"/> or
/// <see cref="ExtractedText.Words"/> directly.
/// </remarks>
internal static class ReadingOrderer
{
    /// <summary>
    /// Structure-tree-order variant of <see cref="Order(IReadOnlyList{ExtractedWord})"/>:
    /// when a page has both a usable structure-tree reading order
    /// (<paramref name="structureTreeMcidOrder"/>, from <see cref="PageMcidOrder"/>) and every
    /// word's owning MCID (<paramref name="mcidPerWord"/>), lines are grouped exactly as the
    /// geometric path does (same baseline-band clustering — MCID order says nothing about which
    /// words share a line, only which lines/words come first) but then ordered by the
    /// <em>structure tree's</em> reading order instead of top-to-bottom/gutter-detected
    /// geometry — the authoritative order for a document whose tags were authored or verified by
    /// a human, wherever it disagrees with PlumePDF's geometric guess (e.g. a genuinely unusual
    /// multi-column layout the gutter heuristic gets wrong).
    /// </summary>
    /// <param name="contentOrderWords">As <see cref="Order(IReadOnlyList{ExtractedWord})"/>.</param>
    /// <param name="mcidPerWord">
    /// Parallel to <paramref name="contentOrderWords"/>: each word's owning MCID
    /// (<see cref="ExtractedWord.Mcid"/>, threaded from the content stream's
    /// <c>BDC</c>/<c>EMC</c> nesting by <c>TextExtractor</c>), or an entry that is itself
    /// <see langword="null"/> for a word painted outside any MCID-carrying sequence
    /// (<c>/Artifact</c> pagination furniture, untagged content). A word with no usable rank
    /// orders <em>after</em> every structure-ranked word, preserving content order among its
    /// peers — an artifact page number never disqualifies the whole page from tag-authored
    /// ordering. Only a page where <em>no</em> word ranks falls back geometrically.
    /// </param>
    /// <param name="structureTreeMcidOrder">This page's MCIDs in structure-tree reading order (<see cref="PageMcidOrder"/>), or <see langword="null"/>/empty when no usable structure tree exists for this page.</param>
    /// <param name="diagnostics">Where the fallback reason, if any, is recorded (result-scoped — pass the caller's own <c>Diagnostics</c>, or <see langword="null"/> to discard it).</param>
    /// <example>
    /// <code>
    /// var order = ReadingOrderer.PageMcidOrder(structureRoot, pageIndex);
    /// var (lines, words, text) = ReadingOrderer.Order(contentOrderWords, mcidPerWord, order, diagnostics);
    /// </code>
    /// </example>
    public static (IReadOnlyList<ExtractedLine> Lines, IReadOnlyList<ExtractedWord> Words, string Text) Order(
        IReadOnlyList<ExtractedWord> contentOrderWords,
        IReadOnlyList<int?>? mcidPerWord,
        IReadOnlyList<int>? structureTreeMcidOrder,
        DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(contentOrderWords);

        if (contentOrderWords.Count == 0)
        {
            return ([], [], string.Empty);
        }

        if (!TryGetStructureRank(mcidPerWord, structureTreeMcidOrder, contentOrderWords.Count, out var rankByWordIndex))
        {
            diagnostics?.Add(new PdfDiagnostic(
                "PLUME6070",
                DiagnosticSeverity.Info,
                "No usable structure-tree reading order for this page (untagged document, no parseable /StructTreeRoot covering this page, or no word on the page carries a structure-referenced MCID); falling back to the geometric heuristic."));
            return Order(contentOrderWords);
        }

        var rankByWord = new Dictionary<ExtractedWord, int>(contentOrderWords.Count);
        for (var i = 0; i < contentOrderWords.Count; i++)
        {
            rankByWord[contentOrderWords[i]] = rankByWordIndex[i];
        }

        var (dirX, dirY) = DominantDirection(contentOrderWords);
        var lines = GroupIntoLines(contentOrderWords, dirX, dirY);
        var orderedLines = lines.OrderBy(line => line.Min(w => rankByWord[w])).ToList();

        var extractedLines = new List<ExtractedLine>(orderedLines.Count);
        var allWords = new List<ExtractedWord>();
        var textLines = new List<string>(orderedLines.Count);

        foreach (var line in orderedLines)
        {
            var words = line.OrderBy(w => rankByWord[w]).ThenBy(w => AlongStart(w, dirX, dirY)).ToList();
            var box = words[0].BoundingBox;
            for (var i = 1; i < words.Count; i++)
            {
                box = PdfRectangle.Union(box, words[i].BoundingBox);
            }

            var text = string.Join(" ", words.Select(static w => w.Text));
            extractedLines.Add(new ExtractedLine(text, box, words));
            allWords.AddRange(words);
            textLines.Add(text);
        }

        return (extractedLines, allWords, string.Join("\n", textLines));
    }

    /// <summary>
    /// Extracts one page's marked-content reading order directly from a parsed structure tree —
    /// a pre-order walk collecting every <see cref="MarkedContentReference.Mcid"/> whose
    /// <see cref="MarkedContentReference.PageIndex"/> matches <paramref name="pageIndex"/>, in
    /// the order the tree lists them (document/reading order, which need not match geometric or
    /// content-stream order — that is the entire point of authoring a tag tree). Feeds
    /// <see cref="Order(IReadOnlyList{ExtractedWord},IReadOnlyList{int?}?,IReadOnlyList{int}?,DiagnosticCollection?)"/>'s
    /// <c>structureTreeMcidOrder</c> parameter.
    /// </summary>
    public static IReadOnlyList<int> PageMcidOrder(StructureElement root, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(root);
        var order = new List<int>();
        CollectPageMcids(root, pageIndex, order);
        return order;
    }

    private static void CollectPageMcids(StructureTreeNode node, int pageIndex, List<int> order)
    {
        switch (node)
        {
            case MarkedContentReference { PageIndex: var page, Mcid: var mcid } when page == pageIndex:
                order.Add(mcid);
                break;

            case StructureElement element:
                foreach (var child in element.Children)
                {
                    CollectPageMcids(child, pageIndex, order);
                }

                break;
        }
    }

    private static bool TryGetStructureRank(IReadOnlyList<int?>? mcidPerWord, IReadOnlyList<int>? structureTreeMcidOrder, int wordCount, out int[] rankByWordIndex)
    {
        rankByWordIndex = [];
        if (structureTreeMcidOrder is not { Count: > 0 } || mcidPerWord is not { Count: > 0 } || mcidPerWord.Count != wordCount)
        {
            return false;
        }

        var mcidRank = new Dictionary<int, int>(structureTreeMcidOrder.Count);
        for (var i = 0; i < structureTreeMcidOrder.Count; i++)
        {
            mcidRank.TryAdd(structureTreeMcidOrder[i], i);
        }

        var result = new int[wordCount];
        var anyRanked = false;
        for (var i = 0; i < wordCount; i++)
        {
            if (mcidPerWord[i] is { } mcid && mcidRank.TryGetValue(mcid, out var rank))
            {
                result[i] = rank;
                anyRanked = true;
            }
            else
            {
                // No usable rank — an /Artifact word (page number, running header) or an MCID
                // the tree never references. Ordered after every structure-ranked word, in
                // content order among themselves (the +i keeps the sort stable), rather than
                // letting one artifact word disqualify the whole page's tag-authored order.
                result[i] = structureTreeMcidOrder.Count + i;
            }
        }

        if (!anyRanked)
        {
            return false; // the structure tree references nothing on this page — geometric fallback.
        }

        rankByWordIndex = result;
        return true;
    }

    /// <summary>Orders <paramref name="contentOrderWords"/> (as produced by <c>WordAssembler</c>, still in content-stream order) into lines and reading order.</summary>
    public static (IReadOnlyList<ExtractedLine> Lines, IReadOnlyList<ExtractedWord> Words, string Text) Order(IReadOnlyList<ExtractedWord> contentOrderWords)
    {
        if (contentOrderWords.Count == 0)
        {
            return ([], [], string.Empty);
        }

        var (dirX, dirY) = DominantDirection(contentOrderWords);
        var orderedLines = OrderIntoLines(contentOrderWords, dirX, dirY);

        var extractedLines = new List<ExtractedLine>(orderedLines.Count);
        var allWords = new List<ExtractedWord>();
        var textLines = new List<string>(orderedLines.Count);

        foreach (var line in orderedLines)
        {
            var words = line.OrderBy(w => AlongStart(w, dirX, dirY)).ToList();
            var box = words[0].BoundingBox;
            for (var i = 1; i < words.Count; i++)
            {
                box = PdfRectangle.Union(box, words[i].BoundingBox);
            }

            var text = string.Join(" ", words.Select(static w => w.Text));
            extractedLines.Add(new ExtractedLine(text, box, words));
            allWords.AddRange(words);
            textLines.Add(text);
        }

        return (extractedLines, allWords, string.Join("\n", textLines));
    }

    // The page's dominant writing direction, snapped to one of the four cardinal directions
    // (the only ones ISO 32000-1 /Rotate can produce - PageSpace.GetRotation normalizes to
    // 0/90/180/270) and chosen by majority vote weighted by letter count, so a page's dominant
    // orientation wins even when a handful of words (rotated annotations, a stray watermark)
    // point a different way. Falls back to ordinary horizontal (1,0) when nothing votes (every
    // word came in with zero letters, which WordAssembler never actually produces).
    private static (double X, double Y) DominantDirection(IReadOnlyList<ExtractedWord> words)
    {
        var votes = new Dictionary<(double X, double Y), int>();

        foreach (var word in words)
        {
            if (word.Letters.Count == 0)
            {
                continue;
            }

            var letter = word.Letters[0];
            var cardinal = SnapToCardinal(letter.DirectionX, letter.DirectionY);
            votes[cardinal] = votes.GetValueOrDefault(cardinal) + word.Letters.Count;
        }

        return votes.Count == 0 ? (1.0, 0.0) : votes.OrderByDescending(static kv => kv.Value).First().Key;
    }

    private static (double X, double Y) SnapToCardinal(double directionX, double directionY) =>
        Math.Abs(directionX) >= Math.Abs(directionY)
            ? (directionX >= 0 ? (1.0, 0.0) : (-1.0, 0.0))
            : (directionY >= 0 ? (0.0, 1.0) : (0.0, -1.0));

    // Same-baseline-band clustering: words are sorted by perpendicular midpoint and swept
    // once — a word joins the current band when it is within half a line-height of the
    // band's reference (first) word, else it opens a new band. "Midpoint" and "line-height"
    // are both taken relative to the dominant direction rather than hardcoded to Y, so this
    // clusters correctly for rotated text too. The sorted sweep is O(n log n): the previous
    // compare-against-every-line loop was O(words x lines), which a hostile page of
    // non-clustering baselines drove to ~24 minutes of CPU at the MaxLettersPerPage default
    // (proven in review). Each band's members are restored to their original word order so
    // downstream in-line ordering is unaffected; band emission order is irrelevant (both
    // callers re-order lines by LineLeadingPerp).
    private static List<List<ExtractedWord>> GroupIntoLines(IReadOnlyList<ExtractedWord> words, double dirX, double dirY)
    {
        var indexed = new (double Perp, int Index)[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            indexed[i] = (PerpMid(words[i], dirX, dirY), i);
        }

        Array.Sort(indexed, static (a, b) => a.Perp.CompareTo(b.Perp));

        var bands = new List<List<int>>();
        List<int>? current = null;
        var referencePerp = 0.0;
        var referenceIndex = -1;

        foreach (var (perp, index) in indexed)
        {
            if (current is not null)
            {
                var tolerance = Math.Max(WordSize(words[referenceIndex]), WordSize(words[index])) * 0.5;
                if (Math.Abs(perp - referencePerp) <= Math.Max(tolerance, 0.01))
                {
                    current.Add(index);
                    continue;
                }
            }

            current = [index];
            bands.Add(current);
            referencePerp = perp;
            referenceIndex = index;
        }

        var lines = new List<List<ExtractedWord>>(bands.Count);
        foreach (var band in bands)
        {
            band.Sort();
            var line = new List<ExtractedWord>(band.Count);
            foreach (var index in band)
            {
                line.Add(words[index]);
            }

            lines.Add(line);
        }

        return lines;
    }

    // Column partitioning happens on WORDS, before line grouping - grouping into lines first
    // (by perpendicular position alone) would merge same-row words from both columns into one
    // cross-page "line", which no gutter split afterwards could undo. Once a gutter is found
    // and both sides have real content, each column is independently grouped into lines and
    // ordered from the direction's leading edge; the first column's lines all precede the
    // second column's.
    private static List<List<ExtractedWord>> OrderIntoLines(IReadOnlyList<ExtractedWord> words, double dirX, double dirY)
    {
        var minAlong = words.Min(w => AlongSpan(w, dirX, dirY).Start);
        var maxAlong = words.Max(w => AlongSpan(w, dirX, dirY).End);

        var gutter = FindColumnGutter(words, dirX, dirY, minAlong, maxAlong);
        if (gutter is double gutterAlong)
        {
            var first = words.Where(w => Along(w, dirX, dirY) < gutterAlong).ToList();
            var second = words.Where(w => Along(w, dirX, dirY) >= gutterAlong).ToList();

            var firstLines = GroupIntoLines(first, dirX, dirY).OrderByDescending(l => LineLeadingPerp(l, dirX, dirY)).ToList();
            var secondLines = GroupIntoLines(second, dirX, dirY).OrderByDescending(l => LineLeadingPerp(l, dirX, dirY)).ToList();

            // Only accept the split when BOTH sides resolve to multiple lines of their own -
            // a single line with an unusually wide gap between two words (ordinary running
            // text, a right-aligned page number, ...) is not a two-column layout, and without
            // this bar the gutter detector's "empty band in the middle third" test alone would
            // false-positive on exactly that case. A real two-column layout, by definition, has
            // more than one line of content in each column.
            if (firstLines.Count >= 2 && secondLines.Count >= 2)
            {
                var result = new List<List<ExtractedWord>>(firstLines.Count + secondLines.Count);
                result.AddRange(firstLines);
                result.AddRange(secondLines);
                return result;
            }
        }

        return [.. GroupIntoLines(words, dirX, dirY).OrderByDescending(l => LineLeadingPerp(l, dirX, dirY))];
    }

    private static double WordCenterX(ExtractedWord word) => (word.BoundingBox.Left + word.BoundingBox.Right) / 2;

    private static double WordCenterY(ExtractedWord word) => (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;

    // The tolerance basis for "same line" / "same word" clustering - the font size of the
    // word's own letters when known (rotation-invariant, unlike BoundingBox.Height/Width which
    // swap under a 90°/270° rotation), else the bounding box's height as a last resort.
    private static double WordSize(ExtractedWord word) =>
        word.Letters.Count > 0 ? Math.Max(word.Letters[0].FontSize, 0.01) : Math.Max(word.BoundingBox.Height, 0.01);

    // "Along" a word's center: how far forward, in the dominant reading direction, this word's
    // midpoint sits - degenerates to WordCenterX for ordinary horizontal text.
    private static double Along(ExtractedWord word, double dirX, double dirY) =>
        (WordCenterX(word) * dirX) + (WordCenterY(word) * dirY);

    // "Perpendicular" of a word's center, relative to the dominant direction - degenerates to
    // WordCenterY for ordinary horizontal text. Used for same-line clustering (a midpoint
    // comparison, matching how baseline-band tolerance naturally works).
    private static double PerpMid(ExtractedWord word, double dirX, double dirY) =>
        (WordCenterX(word) * -dirY) + (WordCenterY(word) * dirX);

    // The bounding box's leading corner projected onto the perpendicular axis (the corner
    // furthest in the +perpendicular direction) - degenerates to BoundingBox.Top for ordinary
    // horizontal text. Used only for ordering lines against each other (not clustering), so a
    // taller word's leading edge - not its center - decides which line reads "first".
    private static double LeadingPerp(ExtractedWord word, double dirX, double dirY)
    {
        var perpX = -dirY;
        var perpY = dirX;
        var l = word.BoundingBox.Left;
        var r = word.BoundingBox.Right;
        var b = word.BoundingBox.Bottom;
        var t = word.BoundingBox.Top;

        var c1 = (l * perpX) + (b * perpY);
        var c2 = (l * perpX) + (t * perpY);
        var c3 = (r * perpX) + (b * perpY);
        var c4 = (r * perpX) + (t * perpY);
        return Math.Max(Math.Max(c1, c2), Math.Max(c3, c4));
    }

    private static double LineLeadingPerp(List<ExtractedWord> line, double dirX, double dirY) => line.Max(w => LeadingPerp(w, dirX, dirY));

    // The bounding box's projected extent along the dominant direction (all four corners, so
    // this is correct regardless of which cardinal direction dirX/dirY names) - degenerates to
    // (Left, Right) for ordinary horizontal text.
    private static (double Start, double End) AlongSpan(ExtractedWord word, double dirX, double dirY)
    {
        var l = word.BoundingBox.Left;
        var r = word.BoundingBox.Right;
        var b = word.BoundingBox.Bottom;
        var t = word.BoundingBox.Top;

        var c1 = (l * dirX) + (b * dirY);
        var c2 = (l * dirX) + (t * dirY);
        var c3 = (r * dirX) + (b * dirY);
        var c4 = (r * dirX) + (t * dirY);
        return (Math.Min(Math.Min(c1, c2), Math.Min(c3, c4)), Math.Max(Math.Max(c1, c2), Math.Max(c3, c4)));
    }

    // The word's own "start" edge along the dominant direction (the minimal along-projected
    // corner) - degenerates to BoundingBox.Left for ordinary horizontal text. Used to sort
    // words within a line into reading order.
    private static double AlongStart(ExtractedWord word, double dirX, double dirY) => AlongSpan(word, dirX, dirY).Start;

    // Buckets every word's along-axis extent into a fixed-resolution histogram and looks for
    // a persistently empty run through the page's middle third (along that axis) - a real
    // column gutter, as opposed to ordinary inter-word spacing (which never spans a meaningful
    // fraction of the page's extent).
    private static double? FindColumnGutter(IReadOnlyList<ExtractedWord> words, double dirX, double dirY, double minAlong, double maxAlong)
    {
        const int buckets = 200;
        var width = maxAlong - minAlong;
        if (width <= 0)
        {
            return null;
        }

        var bucketWidth = width / buckets;
        var occupied = new bool[buckets];

        foreach (var word in words)
        {
            var (start, end) = AlongSpan(word, dirX, dirY);
            var startBucket = Math.Clamp((int)((start - minAlong) / bucketWidth), 0, buckets - 1);
            var endBucket = Math.Clamp((int)((end - minAlong) / bucketWidth), 0, buckets - 1);
            for (var b = startBucket; b <= endBucket; b++)
            {
                occupied[b] = true;
            }
        }

        var midStart = buckets / 3;
        var midEnd = buckets - (buckets / 3);
        var minRunLength = Math.Max(2, buckets / 40); // at least ~2.5% of the page's along-axis extent

        var runStart = -1;
        for (var b = midStart; b < midEnd; b++)
        {
            if (!occupied[b])
            {
                if (runStart < 0)
                {
                    runStart = b;
                }

                continue;
            }

            if (runStart >= 0 && b - runStart >= minRunLength)
            {
                return minAlong + (((runStart + b) / 2.0) * bucketWidth);
            }

            runStart = -1;
        }

        if (runStart >= 0 && midEnd - runStart >= minRunLength)
        {
            return minAlong + (((runStart + midEnd) / 2.0) * bucketWidth);
        }

        return null;
    }
}
