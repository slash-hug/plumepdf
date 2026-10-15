using PlumePdf.Objects;

namespace PlumePdf.Documents.Compression;

/// <summary>
/// An image's decoded samples in its own colour space: interleaved, 8 bits per component, one
/// (gray) or three (RGB) channels, with no <c>/Decode</c> array or colour conversion applied.
/// </summary>
internal sealed record NativeSamples(int Width, int Height, int Channels, byte[] Samples);

/// <summary>
/// Decodes an image XObject to <see cref="NativeSamples"/> for resampling and re-encoding, or
/// says why compression must leave it alone.
/// </summary>
internal static class NativeImageSamples
{
    /// <summary>
    /// Decodes <paramref name="image"/>, resolving its dictionary entries through
    /// <paramref name="objects"/>. False, with <paramref name="skip"/> and
    /// <paramref name="detail"/> set, when the image is ineligible or fails to decode; never
    /// throws for a malformed image, even under <see cref="PdfOptions.Strict"/>.
    /// </summary>
    public static bool TryDecode(
        PdfStream image,
        ObjectRegistry objects,
        PdfOptions options,
        out NativeSamples? samples,
        out PlannedSkip skip,
        out string? detail) =>
        throw new NotImplementedException();
}
