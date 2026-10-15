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
/// <remarks>
/// Only what can be re-encoded without changing how the image is interpreted is decoded: 8-bit
/// <c>/DeviceGray</c>, <c>/DeviceRGB</c>, or <c>/ICCBased</c> with one or three components,
/// through any chain of filters this library decodes other than the bilevel and wavelet codecs.
/// The samples are the filter chain's output and nothing more; the colour space, its ICC
/// profile and any <c>/Decode</c> array stay on the dictionary for the re-encoded stream to
/// carry over unchanged. Every dictionary entry is resolved through the registry before its
/// type is checked, and an ICC profile or a stencil <c>/Mask</c> is read for its dictionary
/// only, so classifying an image never keeps another stream's payload.
/// </remarks>
internal static class NativeImageSamples
{
    private static readonly PdfName WidthName = PdfName.Get("Width");
    private static readonly PdfName HeightName = PdfName.Get("Height");
    private static readonly PdfName MaskName = PdfName.Get("Mask");
    private static readonly PdfName MatteName = PdfName.Get("Matte");
    private static readonly PdfName DeviceGray = PdfName.Get("DeviceGray");
    private static readonly PdfName DeviceRgb = PdfName.Get("DeviceRGB");
    private static readonly PdfName IccBased = PdfName.Get("ICCBased");

    // Filters compression never decodes to re-encode: bilevel codecs (whose images are 1-bit
    // anyway) and JPEG 2000, whose re-encoding is out of scope. Abbreviated names included.
    private static readonly HashSet<string> UnsupportedFilters = ["JPXDecode", "JBIG2Decode", "CCITTFaxDecode", "CCF"];

    private static readonly HashSet<string> DctFilters = ["DCTDecode", "DCT"];

    // Decoder refusals that mean "too big for the configured caps", not "malformed".
    private static readonly HashSet<string> ResourceLimitCodes = ["PLUME3004", "PLUME3042", "PLUME3052", "PLUME3208"];

    /// <summary>
    /// Decodes <paramref name="image"/>, resolving its dictionary entries through
    /// <paramref name="objects"/>. False, with <paramref name="skip"/> and
    /// <paramref name="detail"/> set, when the image is ineligible or fails to decode; never
    /// throws for a malformed image, even under <see cref="PdfOptions.Strict"/>.
    /// </summary>
    /// <param name="image">The image XObject.</param>
    /// <param name="objects">The registry its entries resolve through.</param>
    /// <param name="options">The caps and filter registry decoding uses.</param>
    /// <param name="samples">The decoded samples, when the result is true.</param>
    /// <param name="skip">Why the image is left alone, when the result is false.</param>
    /// <param name="detail">A human-readable reason, when the result is false.</param>
    /// <param name="softMask">
    /// The image is another image's <c>/SMask</c>, which is always one gray channel and may omit
    /// <c>/ColorSpace</c>.
    /// </param>
    public static bool TryDecode(
        PdfStream image,
        ObjectRegistry objects,
        PdfOptions options,
        out NativeSamples? samples,
        out PlannedSkip skip,
        out string? detail,
        bool softMask = false)
    {
        samples = null;
        var dictionary = image.Dictionary;

        if (!TryClassify(dictionary, objects, softMask, options, out var width, out var height, out var channels, out var isDct, out skip, out detail))
        {
            return false;
        }

        var expected = (long)width * height * channels;
        byte[] decoded;
        try
        {
            decoded = image.GetDecodedBytes(options.Filters, options, r => objects[r]);
        }
        catch (PlumePdfException ex)
        {
            skip = ResourceLimitCodes.Contains(ex.Code) ? PlannedSkip.ResourceLimit : PlannedSkip.DecodeFailed;
            detail = $"{ex.Code}: {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            // Codec arithmetic on hostile, cap-legal sizes surfaces as a BCL exception rather
            // than a coded one; it is still this one image failing to decode.
            skip = PlannedSkip.DecodeFailed;
            detail = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }

        // A JPEG must decode to exactly the declared shape (a 4-component or differently sized
        // frame would be misread); other filters may legitimately carry trailing bytes.
        if (decoded.LongLength < expected || (isDct && decoded.LongLength != expected))
        {
            skip = PlannedSkip.DecodeFailed;
            detail = $"Decoded {decoded.LongLength} bytes; a {width}x{height} image with {channels} 8-bit channel(s) needs {expected}.";
            return false;
        }

        if (decoded.LongLength > expected)
        {
            decoded = decoded.AsSpan(0, (int)expected).ToArray();
        }

        samples = new NativeSamples(width, height, channels, decoded);
        skip = PlannedSkip.None;
        detail = null;
        return true;
    }

    /// <summary>Reads the image's <c>/Width</c> and <c>/Height</c>, resolved through <paramref name="objects"/>; false unless both are positive integers.</summary>
    public static bool TryReadSize(PdfDictionary dictionary, ObjectRegistry objects, out int width, out int height)
    {
        height = 0;
        return TryGetPositiveInt(dictionary, WidthName, objects, out width) && TryGetPositiveInt(dictionary, HeightName, objects, out height);
    }

    /// <summary>
    /// Whether the image's last filter is <c>/DCTDecode</c>, with <paramref name="jpegOnly"/>
    /// saying it is the only one (so the raw payload is the JPEG itself).
    /// </summary>
    public static bool IsDctEncoded(PdfDictionary dictionary, ObjectRegistry objects, out bool jpegOnly)
    {
        var filters = FilterNames(dictionary, objects);
        var isDct = filters.Count > 0 && DctFilters.Contains(filters[^1]);
        jpegOnly = isDct && filters.Count == 1;
        return isDct;
    }

    private static bool TryClassify(
        PdfDictionary dictionary,
        ObjectRegistry objects,
        bool softMask,
        PdfOptions options,
        out int width,
        out int height,
        out int channels,
        out bool isDct,
        out PlannedSkip skip,
        out string? detail)
    {
        width = height = channels = 0;
        isDct = false;

        if (Resolve(dictionary, PdfName.ImageMask, objects) is PdfBoolean { Value: true })
        {
            return Skip(PlannedSkip.StencilMask, "An /ImageMask stencil.", out skip, out detail);
        }

        var filters = FilterNames(dictionary, objects);
        if (filters.FirstOrDefault(UnsupportedFilters.Contains) is { } unsupported)
        {
            return Skip(PlannedSkip.UnsupportedFilter, $"Encoded with /{unsupported}, which compression does not re-encode.", out skip, out detail);
        }

        isDct = filters.Count > 0 && DctFilters.Contains(filters[^1]);

        if (!TryReadSize(dictionary, objects, out width, out height))
        {
            return Skip(PlannedSkip.DecodeFailed, "Missing or non-positive /Width or /Height.", out skip, out detail);
        }

        if ((long)width * height > options.MaxImagePixels)
        {
            return Skip(PlannedSkip.ResourceLimit, $"{width}x{height} exceeds PdfOptions.MaxImagePixels ({options.MaxImagePixels}).", out skip, out detail);
        }

        var bitsPerComponent = Resolve(dictionary, PdfName.BitsPerComponent, objects) is PdfNumber { IsInteger: true } bpc && bpc.TryToInt32(out var declared)
            ? declared
            : isDct ? 8 : 0;
        switch (bitsPerComponent)
        {
            case 1:
                return Skip(PlannedSkip.Bilevel, "A 1-bit image.", out skip, out detail);
            case 2 or 4 or 16:
                return Skip(PlannedSkip.UnsupportedBitDepth, $"{bitsPerComponent} bits per component.", out skip, out detail);
            case not 8:
                return Skip(PlannedSkip.DecodeFailed, "Missing or invalid /BitsPerComponent.", out skip, out detail);
        }

        if (ResolveDictionaryOnly(dictionary, MaskName, objects) is PdfArray)
        {
            return Skip(PlannedSkip.ColorKeyMask, "A colour-key /Mask array.", out skip, out detail);
        }

        // A soft mask with /Matte holds colour pre-blended against it and must keep its
        // parent's /Width and /Height, so neither side of such a pair is resized alone.
        if (softMask ? dictionary.ContainsKey(MatteName) : HasMatteSoftMask(dictionary, objects))
        {
            return Skip(PlannedSkip.MatteSoftMask, softMask ? "A /Matte soft mask, which must keep its parent's size." : "Its /SMask carries /Matte, which must keep this image's size.", out skip, out detail);
        }

        var colorSpace = Resolve(dictionary, PdfName.ColorSpace, objects);
        channels = softMask && colorSpace is null or PdfNull ? 1 : ComponentCount(colorSpace, objects);
        if (channels == 0 || (softMask && channels != 1))
        {
            return Skip(PlannedSkip.UnsupportedColorSpace, $"Colour space {Describe(colorSpace)} is not gray or RGB.", out skip, out detail);
        }

        if ((long)width * height * channels > Array.MaxLength)
        {
            return Skip(PlannedSkip.ResourceLimit, $"{width}x{height}x{channels} samples exceed the largest array this runtime allocates.", out skip, out detail);
        }

        skip = PlannedSkip.None;
        detail = null;
        return true;
    }

    private static bool HasMatteSoftMask(PdfDictionary dictionary, ObjectRegistry objects)
    {
        var softMask = ResolveDictionaryOnly(dictionary, PdfName.SMask, objects);
        var maskDictionary = softMask as PdfDictionary ?? (softMask as PdfStream)?.Dictionary;
        return maskDictionary?.ContainsKey(MatteName) == true;
    }

    // 1 or 3 for an eligible colour space, otherwise 0.
    private static int ComponentCount(PdfObject? colorSpace, ObjectRegistry objects)
    {
        if (colorSpace is PdfName name)
        {
            return name.Equals(DeviceGray) ? 1 : name.Equals(DeviceRgb) ? 3 : 0;
        }

        if (colorSpace is not PdfArray { Count: 2 } array
            || ResolveValue(array[0], objects) is not PdfName family
            || !family.Equals(IccBased)
            || array[1] is not PdfReference profileReference)
        {
            return 0;
        }

        // The profile's dictionary alone: its payload is never needed here.
        var profile = ((IObjectSource)objects).ResolveForDiscovery(profileReference.Target, out _);
        var profileDictionary = profile as PdfDictionary ?? (profile as PdfStream)?.Dictionary;
        return profileDictionary is not null
            && ResolveValue(profileDictionary.TryGetValue(PdfName.N, out var n) ? n : null, objects) is PdfNumber { IsInteger: true } count
            && count.TryToInt32(out var components)
            && components is 1 or 3
            ? components
            : 0;
    }

    private static List<string> FilterNames(PdfDictionary dictionary, ObjectRegistry objects)
    {
        var names = new List<string>();
        switch (Resolve(dictionary, PdfName.Filter, objects))
        {
            case PdfName name:
                names.Add(name.Value);
                break;
            case PdfArray array:
                foreach (var item in array)
                {
                    if (ResolveValue(item, objects) is PdfName element)
                    {
                        names.Add(element.Value);
                    }
                }

                break;
        }

        return names;
    }

    private static bool TryGetPositiveInt(PdfDictionary dictionary, PdfName key, ObjectRegistry objects, out int value)
    {
        value = 0;
        return Resolve(dictionary, key, objects) is PdfNumber { IsInteger: true } number && number.TryToInt32(out value) && value > 0;
    }

    private static PdfObject? Resolve(PdfDictionary dictionary, PdfName key, ObjectRegistry objects) =>
        dictionary.TryGetValue(key, out var value) ? ResolveValue(value, objects) : null;

    private static PdfObject? ResolveValue(PdfObject? value, ObjectRegistry objects) =>
        value is PdfReference reference ? objects[reference.Target] : value;

    // An entry that may name a stream whose payload is never needed here (a stencil /Mask, an
    // /SMask): a referenced stream resolves to its dictionary alone, so classifying the image
    // never caches the stream.
    private static PdfObject? ResolveDictionaryOnly(PdfDictionary dictionary, PdfName key, ObjectRegistry objects) =>
        !dictionary.TryGetValue(key, out var value) ? null
        : value is PdfReference reference ? ((IObjectSource)objects).ResolveForDiscovery(reference.Target, out _)
        : value;

    private static string Describe(PdfObject? colorSpace) => colorSpace switch
    {
        null or PdfNull => "(none)",
        PdfName name => "/" + name.Value,
        PdfArray { Count: > 0 } array when array[0] is PdfName family => "/" + family.Value,
        _ => colorSpace.GetType().Name,
    };

    private static bool Skip(PlannedSkip reason, string message, out PlannedSkip skip, out string? detail)
    {
        skip = reason;
        detail = message;
        return false;
    }
}
