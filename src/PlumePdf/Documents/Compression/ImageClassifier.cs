namespace PlumePdf.Documents.Compression;

/// <summary>
/// Turns the placement scan and each image's dictionary into a <see cref="PlannedImage"/>:
/// the skip classes, the downsample trigger and per-axis targets, and soft-mask pairing.
/// Reads dictionaries only.
/// </summary>
internal static class ImageClassifier
{
    /// <summary>
    /// Plans every image in <paramref name="scan"/> plus every soft mask those images
    /// reference, keyed by original object number.
    /// </summary>
    public static IReadOnlyDictionary<int, PlannedImage> Classify(
        ObjectRegistry objects,
        PlacementScan scan,
        CompressionSettings settings,
        PdfOptions options,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    /// <summary>
    /// The downsample trigger: true when <c>min(dpiX, dpiY)</c> is strictly above
    /// <c><paramref name="maxDpi"/> × 1.5</c>.
    /// </summary>
    public static bool ShouldDownsample(double dpiX, double dpiY, double maxDpi) =>
        throw new NotImplementedException();

    /// <summary>
    /// The planned size for one image: each axis scaled so its DPI becomes
    /// <paramref name="maxDpi"/>, never above the source size and never below 1.
    /// </summary>
    public static (int Width, int Height) TargetSize(int width, int height, double dpiX, double dpiY, double maxDpi) =>
        throw new NotImplementedException();
}
