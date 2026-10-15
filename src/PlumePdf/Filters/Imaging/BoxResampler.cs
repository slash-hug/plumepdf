namespace PlumePdf.Filters.Imaging;

/// <summary>
/// Downsamples interleaved 8-bit samples with a footprint-exact box filter: each destination
/// sample is the area-weighted mean of the source samples its footprint covers, with 16.16
/// fixed-point edge weights. Integer-only, so output is identical on every platform.
/// </summary>
internal static class BoxResampler
{
    /// <summary>
    /// Resamples a <paramref name="width"/>×<paramref name="height"/> image of
    /// <paramref name="channels"/> interleaved channels to
    /// <paramref name="destinationWidth"/>×<paramref name="destinationHeight"/>, which must
    /// not be larger on either axis.
    /// </summary>
    public static byte[] Resample(
        ReadOnlySpan<byte> source,
        int width,
        int height,
        int channels,
        int destinationWidth,
        int destinationHeight) =>
        throw new NotImplementedException();
}
