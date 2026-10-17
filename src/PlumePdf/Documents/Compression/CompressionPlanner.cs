namespace PlumePdf.Documents.Compression;

/// <summary>
/// The planning pass that runs before a compressing save: placement scan, classification and
/// duplicate detection, producing a <see cref="CompressionPlan"/> that holds numbers only.
/// </summary>
/// <remarks>
/// <para>
/// Duplicates are looked for only when the lossless levers are on. Since only an alias's
/// canonical copy is written, a duplicate image's placements are folded into its canonical's
/// before classifying, so the written copy is sized for every place either copy is drawn.
/// </para>
/// <para>
/// An alias then stands only when both copies would be written the same way. A copy can still
/// be planned on its own when it is another image's soft mask (masks are planned from their
/// parents, not from placements): two identical masks of differently sized parents, or a mask
/// identical to a plain image that is re-encoded, would otherwise share one wrongly written
/// stream. Such an alias is dropped and the images are classified again, which ends because each
/// round drops at least one alias; a document without such a pair classifies once.
/// </para>
/// </remarks>
internal static class CompressionPlanner
{
    /// <summary>Plans compressing <paramref name="document"/> with <paramref name="settings"/>.</summary>
    /// <param name="document">The source document, after any signature stripping.</param>
    /// <param name="settings">The levers this run applies.</param>
    /// <param name="saveOptions">The effective options the save will run with.</param>
    /// <param name="cancellationToken">Checked once per page and once per hashed payload.</param>
    /// <param name="pageScanned">Called with the number of pages scanned so far after each page, or null.</param>
    public static CompressionPlan Plan(
        PdfDocument document,
        CompressionSettings settings,
        PdfOptions saveOptions,
        CancellationToken cancellationToken,
        Action<int>? pageScanned = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scan = PlacementScanner.Scan(document, saveOptions, cancellationToken, pageScanned);
        var aliases = settings.Lossless
            ? new Dictionary<int, int>(StreamDeduplicator.FindAliases(document, saveOptions, cancellationToken))
            : [];

        while (true)
        {
            var folded = scan with { Images = Fold(scan.Images, aliases) };
            var images = ImageClassifier.Classify(document.Objects, folded, settings, saveOptions, cancellationToken);
            var conflicts = aliases
                .Where(alias => !WrittenAlike(alias.Key, alias.Value, images, scan.Images))
                .Select(static alias => alias.Key)
                .ToList();
            if (conflicts.Count == 0)
            {
                return new CompressionPlan(WrittenOnly(images, aliases), aliases, scan.InlineImages, scan.Diagnostics);
            }

            foreach (var duplicate in conflicts)
            {
                aliases.Remove(duplicate);
            }
        }
    }

    // The plans of the streams that are written: a merged-away copy (only a soft mask can still
    // be here, since placed duplicates were folded) is dropped, and a parent naming one names
    // its canonical instead, as the written reference will.
    private static Dictionary<int, PlannedImage> WrittenOnly(
        IReadOnlyDictionary<int, PlannedImage> images,
        Dictionary<int, int> aliases)
    {
        var written = new Dictionary<int, PlannedImage>(images.Count);
        foreach (var (number, plan) in images)
        {
            if (aliases.ContainsKey(number))
            {
                continue;
            }

            written[number] = plan.SoftMaskNumber is int mask && aliases.TryGetValue(mask, out var canonical)
                ? plan with { SoftMaskNumber = canonical }
                : plan;
        }

        return written;
    }

    // The scan's placements with every aliased image's folded into its canonical's.
    private static Dictionary<int, ImagePlacementSummary> Fold(
        IReadOnlyDictionary<int, ImagePlacementSummary> placements,
        Dictionary<int, int> aliases)
    {
        var folded = new Dictionary<int, ImagePlacementSummary>(placements.Count);
        foreach (var (number, placement) in placements.OrderBy(static p => p.Key))
        {
            var target = aliases.TryGetValue(number, out var canonical) ? canonical : number;
            folded[target] = folded.TryGetValue(target, out var existing) ? Merge(existing, placement) : placement;
        }

        return folded;
    }

    // Two placement summaries as one image's: the lower DPI per axis, with the page of the lower
    // min(DpiX, DpiY) (the earlier page on a tie); measured only when both are, and drawn only in
    // tiling patterns only when both are. A copy only listed, never drawn, adds nothing.
    private static ImagePlacementSummary Merge(ImagePlacementSummary a, ImagePlacementSummary b)
    {
        if (a.Kind == PlacementKind.NotPlaced || b.Kind == PlacementKind.NotPlaced)
        {
            return a.Kind != PlacementKind.NotPlaced ? a
                : b.Kind != PlacementKind.NotPlaced ? b
                : a with { PageIndex = Math.Min(a.PageIndex, b.PageIndex) };
        }

        if (a is { Kind: PlacementKind.Measured, DpiX: double ax, DpiY: double ay }
            && b is { Kind: PlacementKind.Measured, DpiX: double bx, DpiY: double by })
        {
            var (aLow, bLow) = (Math.Min(ax, ay), Math.Min(bx, by));
            var page = aLow < bLow ? a.PageIndex : bLow < aLow ? b.PageIndex : Math.Min(a.PageIndex, b.PageIndex);
            return new ImagePlacementSummary(Math.Min(ax, bx), Math.Min(ay, by), page, PlacementKind.Measured);
        }

        var kind = a.Kind == PlacementKind.TilingPatternOnly && b.Kind == PlacementKind.TilingPatternOnly
            ? PlacementKind.TilingPatternOnly
            : PlacementKind.Unmeasured;
        return new ImagePlacementSummary(null, null, Math.Min(a.PageIndex, b.PageIndex), kind);
    }

    // Whether aliasing 'duplicate' onto 'canonical' writes what each would have been written as.
    // A placed duplicate the classifier didn't plan was folded into its canonical, whose plan
    // already covers it; any other unplanned stream is written unchanged.
    private static bool WrittenAlike(
        int duplicate,
        int canonical,
        IReadOnlyDictionary<int, PlannedImage> images,
        IReadOnlyDictionary<int, ImagePlacementSummary> placements)
    {
        if (!images.TryGetValue(duplicate, out var duplicatePlan) && placements.ContainsKey(duplicate))
        {
            return true;
        }

        images.TryGetValue(canonical, out var canonicalPlan);
        var duplicateKept = duplicatePlan is null || duplicatePlan.Action == PlannedImageAction.Keep;
        var canonicalKept = canonicalPlan is null || canonicalPlan.Action == PlannedImageAction.Keep;
        if (duplicateKept || canonicalKept)
        {
            return duplicateKept && canonicalKept;
        }

        return duplicatePlan!.Action == canonicalPlan!.Action
            && duplicatePlan.TargetWidth == canonicalPlan.TargetWidth
            && duplicatePlan.TargetHeight == canonicalPlan.TargetHeight
            && duplicatePlan.IsSoftMask == canonicalPlan.IsSoftMask;
    }
}
