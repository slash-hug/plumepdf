using PlumePdf.Filters;
using PlumePdf.Filters.Imaging;

namespace PlumePdf.Documents.Compression;

/// <summary>What <see cref="ImageReencoder"/> did to one image.</summary>
internal enum ImageReencodeOutcome
{
    /// <summary>Nothing was asked of it: the original stream, not decoded.</summary>
    Unchanged,

    /// <summary>Resized, keeping its encoding family.</summary>
    Downsampled,

    /// <summary>Re-encoded as JPEG at its own size.</summary>
    Reencoded,

    /// <summary>Resized and re-encoded as JPEG.</summary>
    DownsampledAndReencoded,

    /// <summary>The new encoding was not smaller, so the original stream stands.</summary>
    KeptOriginal,

    /// <summary>Ineligible or undecodable: the original stream, with the reason.</summary>
    Skipped,
}

/// <summary>What to do with one image.</summary>
/// <param name="TargetWidth">The width to resize to; at or above the image's own width keeps it.</param>
/// <param name="TargetHeight">The height to resize to; at or above the image's own height keeps it.</param>
/// <param name="JpegQuality">Re-encode as JPEG at this quality (1–100), or null to keep the image's encoding family.</param>
/// <param name="SoftMask">The image is another image's <c>/SMask</c>: always one gray channel, always Flate, never JPEG.</param>
internal sealed record ImageReencodeRequest(int TargetWidth, int TargetHeight, int? JpegQuality, bool SoftMask = false);

/// <summary>The stream to write for one image, and how it came to be.</summary>
/// <param name="Stream">The re-encoded stream, or the original one for every outcome that leaves it alone.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Skip">Why the image was left alone, when <paramref name="Outcome"/> is <see cref="ImageReencodeOutcome.Skipped"/>.</param>
/// <param name="Detail">A human-readable reason, for a skip.</param>
/// <param name="Width">The written image's width.</param>
/// <param name="Height">The written image's height.</param>
/// <param name="EstimatedSourceQuality">A JPEG source's estimated quality, clamped to 50–95, or null when it isn't a JPEG or its tables aren't IJG-scaled.</param>
/// <param name="UsedFallbackQuality">The JPEG was written at <see cref="ImageReencoder.FallbackJpegQuality"/> because the source's quality couldn't be estimated.</param>
/// <param name="TrialBytes">The new encoding's length when one was made — written, or dropped under the never-larger rule (<see cref="ImageReencodeOutcome.KeptOriginal"/>); null when nothing was encoded.</param>
internal sealed record ImageReencodeResult(
    PdfStream Stream,
    ImageReencodeOutcome Outcome,
    PlannedSkip Skip,
    string? Detail,
    int Width,
    int Height,
    int? EstimatedSourceQuality,
    bool UsedFallbackQuality,
    long? TrialBytes = null);

/// <summary>
/// Resizes and re-encodes one image XObject: decode to native samples, box-downsample, release
/// the source samples, encode. Everything that says how the samples are interpreted stays on
/// the dictionary as it was.
/// </summary>
/// <remarks>
/// The family rule: with a JPEG quality requested, the image becomes a baseline JPEG at that
/// quality, colour with 4:2:0 chroma subsampling; otherwise a Flate (or other lossless) image
/// stays Flate and a JPEG stays JPEG at its own estimated quality, keeping full-resolution
/// chroma when the source had it (4:2:0 otherwise). A soft mask is always written as gray Flate. The never-larger rule:
/// when the new encoding is not smaller than the original stream's encoded bytes, the original
/// is kept. The rewritten dictionary differs from the original only in <c>/Width</c>,
/// <c>/Height</c> and <c>/Filter</c>, and drops <c>/DecodeParms</c> and <c>/Length</c>;
/// <c>/ColorSpace</c> (an ICC profile reference included), <c>/Intent</c>,
/// <c>/Interpolate</c>, <c>/SMask</c>, <c>/OC</c> and every other entry carry over unchanged,
/// so it references a subset of what the original did. <c>/Decode</c> carries over too, except
/// when a non-JPEG image becomes a JPEG: renderers don't all apply a JPEG's <c>/Decode</c>
/// array (PlumePDF's rasterizer reads it only for CMYK polarity), so its mapping is applied to
/// the samples before encoding and the entry is dropped, and the image looks the same
/// everywhere. A dropped <c>/DecodeParms</c> or <c>/Decode</c> that was an indirect object is
/// still written, unreferenced: the writer has already numbered it by the time the image is
/// offered. It is a few bytes.
/// </remarks>
internal static class ImageReencoder
{
    /// <summary>The quality a JPEG is re-encoded at when its own can't be estimated.</summary>
    public const int FallbackJpegQuality = 85;

    /// <summary>The lowest estimated source quality a JPEG keeps; a lower estimate is raised to it.</summary>
    public const int MinEstimatedQuality = 50;

    /// <summary>The highest estimated source quality a JPEG keeps; a higher estimate is lowered to it.</summary>
    public const int MaxEstimatedQuality = 95;

    private static readonly FlateEncodingFilter FlateEncoder = new();
    private static readonly PdfName WidthName = PdfName.Get("Width");
    private static readonly PdfName HeightName = PdfName.Get("Height");

    /// <summary>
    /// Applies <paramref name="request"/> to <paramref name="image"/>, resolving its entries
    /// through <paramref name="objects"/>. Never throws for a malformed image: a decode failure
    /// comes back as <see cref="ImageReencodeOutcome.Skipped"/>.
    /// </summary>
    public static ImageReencodeResult Reencode(PdfStream image, ObjectRegistry objects, ImageReencodeRequest request, PdfOptions options)
    {
        var dictionary = image.Dictionary;
        int width, height;
        bool sourceIsJpeg, jpegOnly;
        PdfArray? decodeArray;
        try
        {
            if (!NativeImageSamples.TryReadSize(dictionary, objects, out width, out height))
            {
                return Unchanged(image, ImageReencodeOutcome.Skipped, PlannedSkip.DecodeFailed, "Missing or non-positive /Width or /Height.", 0, 0, null);
            }

            sourceIsJpeg = NativeImageSamples.IsDctEncoded(dictionary, objects, out jpegOnly);
            decodeArray = dictionary.TryGetValue(PdfName.Decode, out var decode)
                ? (decode is PdfReference reference ? objects[reference.Target] : decode) as PdfArray
                : null;
        }
        catch (PlumePdfException ex)
        {
            // A dangling entry under Strict: this image fails to decode, the run carries on.
            return Unchanged(image, ImageReencodeOutcome.Skipped, PlannedSkip.DecodeFailed, $"{ex.Code}: {ex.Message}", 0, 0, null);
        }

        var targetWidth = Math.Clamp(request.TargetWidth, 1, width);
        var targetHeight = Math.Clamp(request.TargetHeight, 1, height);
        var resize = targetWidth < width || targetHeight < height;
        var toJpeg = !request.SoftMask && request.JpegQuality is not null;
        if (!resize && !toJpeg)
        {
            return Unchanged(image, ImageReencodeOutcome.Unchanged, PlannedSkip.None, null, width, height, null);
        }

        var estimated = sourceIsJpeg && jpegOnly && JpegQualityEstimator.TryEstimate(image.RawBytes.Span, out var raw)
            ? Math.Clamp(raw, MinEstimatedQuality, MaxEstimatedQuality)
            : (int?)null;

        // A JPEG kept as JPEG at its own quality keeps its own chroma resolution too: a
        // downsample alone must not also subsample a 4:4:4 source's colour.
        var keepFullChroma = request.JpegQuality is null && sourceIsJpeg && jpegOnly && JpegQualityEstimator.HasFullResolutionChroma(image.RawBytes.Span);

        if (!NativeImageSamples.TryDecode(image, objects, options, out var decoded, out var skip, out var detail, request.SoftMask))
        {
            return Unchanged(image, ImageReencodeOutcome.Skipped, skip, detail, width, height, estimated);
        }

        // Decode, resize, then drop the full-size samples before encoding: the encoder's own
        // working set is several times its input, so the two must not overlap.
        var channels = decoded!.Channels;
        var samples = resize
            ? BoxResampler.Resample(decoded.Samples, width, height, channels, targetWidth, targetHeight)
            : decoded.Samples;
        decoded = null;

        var writeJpeg = toJpeg || (!request.SoftMask && sourceIsJpeg);
        var bakeDecode = writeJpeg && !sourceIsJpeg && decodeArray is not null && TryApplyDecode(samples, channels, decodeArray);
        var usedFallback = false;
        byte[] encoded;
        if (writeJpeg)
        {
            var quality = request.JpegQuality ?? estimated ?? FallbackJpegQuality;
            usedFallback = request.JpegQuality is null && estimated is null;

            // No resolution argument: the JFIF header records aspect ratio only, so it can't
            // contradict the placement the page gives the image. The encoder drops the samples
            // once its planes are built, so they don't live through the entropy coding.
            encoded = JpegEncoder.EncodeReleasingPixels(ref samples, targetWidth, targetHeight, channels, quality, subsampleChroma: !keepFullChroma);
        }
        else
        {
            encoded = FlateEncoder.Encode(samples, options);
        }

        samples = null;

        if (encoded.Length >= image.RawBytes.Length)
        {
            return Unchanged(image, ImageReencodeOutcome.KeptOriginal, PlannedSkip.None, null, width, height, estimated) with { TrialBytes = encoded.Length };
        }

        var outcome = (resize, toJpeg) switch
        {
            (true, true) => ImageReencodeOutcome.DownsampledAndReencoded,
            (true, false) => ImageReencodeOutcome.Downsampled,
            _ => ImageReencodeOutcome.Reencoded,
        };

        var rewritten = Rewrite(dictionary, targetWidth, targetHeight, PdfName.Get(writeJpeg ? "DCTDecode" : "FlateDecode"), dropDecode: bakeDecode);
        return new ImageReencodeResult(new PdfStream(rewritten, encoded), outcome, PlannedSkip.None, null, targetWidth, targetHeight, estimated, usedFallback, encoded.Length);
    }

    // Applies a /Decode array (one Dmin Dmax pair per channel, over 8-bit samples) in place,
    // as a renderer would: v maps to Dmin + v * (Dmax - Dmin) / 255, rounded and clamped back
    // to a byte. False, with the samples untouched, for an array that isn't 2 numbers per
    // channel, which renderers ignore, so the entry stays as it was.
    private static bool TryApplyDecode(byte[] samples, int channels, PdfArray decode)
    {
        if (decode.Count != 2 * channels || decode.Any(static v => v is not PdfNumber))
        {
            return false;
        }

        var tables = new byte[channels][];
        for (var c = 0; c < channels; c++)
        {
            var min = ((PdfNumber)decode[2 * c]).Value;
            var max = ((PdfNumber)decode[(2 * c) + 1]).Value;
            var table = new byte[256];
            for (var v = 0; v < 256; v++)
            {
                table[v] = (byte)Math.Clamp(Math.Round((min + (v * (max - min) / 255.0)) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
            }

            tables[c] = table;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = tables[i % channels][samples[i]];
        }

        return true;
    }

    // The original entries in their original order, with /Width, /Height and /Filter replaced
    // in place (/Filter appended when the source had none) and the entries that described the
    // old encoding dropped (and /Decode, when it was applied to the samples). /Length is the
    // writer's to set.
    private static PdfDictionary Rewrite(PdfDictionary original, int width, int height, PdfName filter, bool dropDecode)
    {
        var rewritten = new PdfDictionary();
        foreach (var (key, value) in original)
        {
            if (ReferenceEquals(key, PdfName.DecodeParms) || ReferenceEquals(key, PdfName.Length) || (dropDecode && ReferenceEquals(key, PdfName.Decode)))
            {
                continue;
            }

            rewritten.Set(
                key,
                ReferenceEquals(key, WidthName) ? PdfNumber.Get(width)
                : ReferenceEquals(key, HeightName) ? PdfNumber.Get(height)
                : ReferenceEquals(key, PdfName.Filter) ? filter
                : value);
        }

        if (!rewritten.ContainsKey(PdfName.Filter))
        {
            rewritten.Set(PdfName.Filter, filter);
        }

        return rewritten;
    }

    private static ImageReencodeResult Unchanged(PdfStream image, ImageReencodeOutcome outcome, PlannedSkip skip, string? detail, int width, int height, int? estimated) =>
        new(image, outcome, skip, detail, width, height, estimated, UsedFallbackQuality: false);
}
