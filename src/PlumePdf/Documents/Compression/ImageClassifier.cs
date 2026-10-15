using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>
/// Turns the placement scan and each image's dictionary into a <see cref="PlannedImage"/>:
/// the skip classes, the downsample trigger and per-axis targets, and soft-mask pairing.
/// Reads dictionaries only.
/// </summary>
/// <remarks>
/// <para>
/// An image's own dictionary decides first (the same eligibility rules the decoder applies, so
/// a planned image is one the write-time re-encoder can decode); then its placements: an image
/// drawn only in tiling patterns, or anywhere the scan couldn't measure, is left alone whichever
/// lever is set. An eligible, measured image is downsampled when the lower of its two DPIs is
/// strictly above 1.5 times the maximum, each axis to the maximum independently, and
/// re-encoded whenever a JPEG quality is set.
/// </para>
/// <para>
/// A soft mask is planned on its own, after its parents: its size follows each parent's
/// planned reduction (scaled by the parent's factor, so a mask at a different resolution keeps
/// its ratio), and when parents disagree the larger target wins on each axis. A mask is never
/// re-encoded as JPEG, and one also drawn as a plain image is left alone.
/// </para>
/// <para>
/// An image a page lists but nothing draws is kept with <see cref="PlannedSkip.NoPlacement"/>
/// before anything else is considered, and as a parent it sizes no soft mask.
/// </para>
/// </remarks>
internal static class ImageClassifier
{
    /// <summary>The downsample trigger's margin over the maximum DPI.</summary>
    private const double TriggerFactor = 1.5;

    private const string NotPlacedDetail = "Listed in a page's resources but never drawn, so no placement sizes it.";

    /// <summary>
    /// Plans every image in <paramref name="scan"/> plus every soft mask those images
    /// reference, keyed by original object number.
    /// </summary>
    public static IReadOnlyDictionary<int, PlannedImage> Classify(
        ObjectRegistry objects,
        PlacementScan scan,
        CompressionSettings settings,
        PdfOptions options,
        CancellationToken cancellationToken)
    {
        var plans = new SortedDictionary<int, PlannedImage>();
        var parentsOfMask = new SortedDictionary<int, List<PlannedImage>>();

        foreach (var (number, placement) in scan.Images.OrderBy(static i => i.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dictionary = DictionaryOf(objects, number);
            var softMask = dictionary is null ? null : SoftMaskNumber(dictionary, objects);
            var plan = PlanImage(number, dictionary, placement, settings, objects, options, softMask);
            plans[number] = plan;
            if (softMask is int mask)
            {
                if (!parentsOfMask.TryGetValue(mask, out var parents))
                {
                    parents = [];
                    parentsOfMask[mask] = parents;
                }

                parents.Add(plan);
            }
        }

        foreach (var (mask, parents) in parentsOfMask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A plain image the pages only list, never draw, is planned as the mask it is.
            if (plans.TryGetValue(mask, out var plain) && plain.Skip != PlannedSkip.NoPlacement)
            {
                plans[mask] = plain with
                {
                    TargetWidth = plain.Width,
                    TargetHeight = plain.Height,
                    Action = PlannedImageAction.Keep,
                    Skip = PlannedSkip.SharedAsMask,
                    SkipDetail = "Drawn as an image of its own and also used as another image's /SMask.",
                    IsSoftMask = true,
                };
                continue;
            }

            plans[mask] = PlanSoftMask(mask, DictionaryOf(objects, mask), parents, objects, options);
        }

        return new Dictionary<int, PlannedImage>(plans);
    }

    /// <summary>
    /// The downsample trigger: true when <c>min(dpiX, dpiY)</c> is strictly above
    /// <c><paramref name="maxDpi"/> × 1.5</c>.
    /// </summary>
    public static bool ShouldDownsample(double dpiX, double dpiY, double maxDpi) =>
        Math.Min(dpiX, dpiY) > maxDpi * TriggerFactor;

    /// <summary>
    /// The planned size for one image: each axis scaled so its DPI becomes
    /// <paramref name="maxDpi"/>, never above the source size and never below 1.
    /// </summary>
    public static (int Width, int Height) TargetSize(int width, int height, double dpiX, double dpiY, double maxDpi) =>
        (TargetAxis(width, dpiX, maxDpi), TargetAxis(height, dpiY, maxDpi));

    private static int TargetAxis(int size, double dpi, double maxDpi) =>
        dpi > maxDpi && maxDpi > 0
            ? Math.Clamp((int)Math.Round(size * maxDpi / dpi, MidpointRounding.AwayFromZero), 1, size)
            : size;

    private static PlannedImage PlanImage(int number, PdfDictionary? dictionary, ImagePlacementSummary placement, CompressionSettings settings, ObjectRegistry objects, PdfOptions options, int? softMask)
    {
        var measured = placement.Kind == PlacementKind.Measured;
        double? dpiX = measured ? placement.DpiX : null;
        double? dpiY = measured ? placement.DpiY : null;

        PlannedImage Keep(int width, int height, PlannedSkip skip, string? detail) =>
            new(number, placement.PageIndex, dpiX, dpiY, width, height, width, height, PlannedImageAction.Keep, skip, detail, softMask, IsSoftMask: false);

        var eligible = TryClassify(dictionary, objects, softMask: false, options, out var width, out var height, out var skip, out var detail);
        if (placement.Kind == PlacementKind.NotPlaced)
        {
            // Nothing draws it, so no placement sizes it and its encoding doesn't matter.
            return new PlannedImage(number, null, null, null, width, height, width, height, PlannedImageAction.Keep, PlannedSkip.NoPlacement, NotPlacedDetail, softMask, IsSoftMask: false);
        }

        if (!eligible)
        {
            return Keep(width, height, skip, detail);
        }

        switch (placement.Kind)
        {
            case PlacementKind.TilingPatternOnly:
                return Keep(width, height, PlannedSkip.TilingPatternOnly, "Drawn only inside tiling patterns, where its device size isn't measured.");
            case PlacementKind.Unmeasured:
                return Keep(width, height, PlannedSkip.UnmeasuredPlacement, "Drawn somewhere its device size can't be measured (a Type 3 glyph, a tiling pattern as well as the page, or content the scan couldn't read to the end).");
        }

        var (targetWidth, targetHeight) = (width, height);
        if (settings.MaxDpi is double maxDpi && dpiX is double x && dpiY is double y && ShouldDownsample(x, y, maxDpi))
        {
            (targetWidth, targetHeight) = TargetSize(width, height, x, y, maxDpi);
        }

        var downsample = targetWidth < width || targetHeight < height;
        var reencode = settings.JpegQuality is not null;
        var action = (downsample, reencode) switch
        {
            (true, true) => PlannedImageAction.DownsampleAndReencode,
            (true, false) => PlannedImageAction.Downsample,
            (false, true) => PlannedImageAction.Reencode,
            _ => PlannedImageAction.Keep,
        };

        return new PlannedImage(number, placement.PageIndex, dpiX, dpiY, width, height, targetWidth, targetHeight, action, PlannedSkip.None, null, softMask, IsSoftMask: false);
    }

    private static PlannedImage PlanSoftMask(int number, PdfDictionary? dictionary, List<PlannedImage> parents, ObjectRegistry objects, PdfOptions options)
    {
        // The parents are in object-number order; the lowest DPI a parent implies per mask axis
        // is the mask's own, and its page goes with it. A parent nothing draws sizes nothing.
        var eligible = TryClassify(dictionary, objects, softMask: true, options, out var width, out var height, out var skip, out var detail);
        parents = parents.Where(static p => p.Skip != PlannedSkip.NoPlacement).ToList();
        if (parents.Count == 0)
        {
            return new PlannedImage(number, null, null, null, width, height, width, height, PlannedImageAction.Keep, PlannedSkip.NoPlacement, NotPlacedDetail, null, IsSoftMask: true);
        }

        var first = parents[0];
        if (!eligible)
        {
            return new PlannedImage(number, first.PageIndex, null, null, width, height, width, height, PlannedImageAction.Keep, skip, detail, null, IsSoftMask: true);
        }

        int? pageIndex = first.PageIndex;
        double? dpiX = null, dpiY = null;
        int targetWidth = 0, targetHeight = 0;
        foreach (var parent in parents)
        {
            var reduced = parent.Action is PlannedImageAction.Downsample or PlannedImageAction.DownsampleAndReencode && parent.Width > 0 && parent.Height > 0;
            targetWidth = Math.Max(targetWidth, reduced ? Follow(width, parent.TargetWidth, parent.Width) : width);
            targetHeight = Math.Max(targetHeight, reduced ? Follow(height, parent.TargetHeight, parent.Height) : height);

            if (parent.DpiX is double px && parent.DpiY is double py && parent.Width > 0 && parent.Height > 0)
            {
                var maskX = px * width / parent.Width;
                var maskY = py * height / parent.Height;
                if (dpiX is null || Math.Min(maskX, maskY) < Math.Min(dpiX.Value, dpiY!.Value))
                {
                    pageIndex = parent.PageIndex;
                }

                dpiX = dpiX is null ? maskX : Math.Min(dpiX.Value, maskX);
                dpiY = dpiY is null ? maskY : Math.Min(dpiY.Value, maskY);
            }
        }

        var action = targetWidth < width || targetHeight < height ? PlannedImageAction.Downsample : PlannedImageAction.Keep;
        return new PlannedImage(number, pageIndex, dpiX, dpiY, width, height, targetWidth, targetHeight, action, PlannedSkip.None, null, null, IsSoftMask: true);
    }

    // A mask axis reduced by its parent's factor, never grown and never below one pixel.
    private static int Follow(int maskSize, int parentTarget, int parentSize) =>
        Math.Clamp((int)Math.Round((double)maskSize * parentTarget / parentSize, MidpointRounding.AwayFromZero), 1, maskSize);

    // The decoder's own eligibility rules, from the dictionary; the size is read even for a
    // skipped image so the result can report it. A dangling entry under Strict is this image
    // failing to decode, not the run failing.
    private static bool TryClassify(PdfDictionary? dictionary, ObjectRegistry objects, bool softMask, PdfOptions options, out int width, out int height, out PlannedSkip skip, out string? detail)
    {
        width = height = 0;
        if (dictionary is null)
        {
            skip = PlannedSkip.DecodeFailed;
            detail = "Not an image stream.";
            return false;
        }

        try
        {
            NativeImageSamples.TryReadSize(dictionary, objects, out width, out height);
            return NativeImageSamples.TryClassify(dictionary, objects, softMask, options, out _, out _, out _, out _, out skip, out detail);
        }
        catch (PlumePdfException ex)
        {
            skip = PlannedSkip.DecodeFailed;
            detail = $"{ex.Code}: {ex.Message}";
            return false;
        }
    }

    // A stream's dictionary alone, never its payload.
    private static PdfDictionary? DictionaryOf(ObjectRegistry objects, int number)
    {
        var resolved = ((IObjectSource)objects).ResolveForDiscovery(new IndirectReference(number, 0), out var isStream);
        return isStream ? resolved as PdfDictionary ?? (resolved as PdfStream)?.Dictionary : null;
    }

    private static int? SoftMaskNumber(PdfDictionary dictionary, ObjectRegistry objects) =>
        dictionary.TryGetValue(PdfName.SMask, out var value)
        && value is PdfReference reference
        && DictionaryOf(objects, reference.Target.Number) is not null
            ? reference.Target.Number
            : null;
}
