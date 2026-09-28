namespace PlumePdf.Documents;

/// <summary>
/// One image XObject found on a page (ISO 32000-1 §8.9.5), enumerated by
/// <c>PdfPage.ExtractImages</c>. This type draws the extraction/rasterization boundary at
/// <em>filter decode</em>: <see cref="Data"/> is either the intact original bytes (DCT — a
/// standard JPEG file, <see cref="IsJpeg"/>) or filter-decoded raw samples (Flate/LZW/
/// RunLength, in the row-major layout <see cref="BitsPerComponent"/>/<see cref="ColorSpaceName"/>
/// describe) or, for a filter PlumePDF has no decoder for, the still-encoded original bytes
/// (<see cref="IsRawEncoded"/>, with a diagnostic recorded on the extraction call).
/// <see cref="ExtractedImage"/> itself never resolves <see cref="ColorSpaceName"/>/
/// <see cref="Decode"/> into an RGB pixel buffer, applies <c>/Decode</c>, or composites
/// <see cref="SoftMaskReference"/> — that boundary is scoped to <em>this
/// extraction surface specifically</em>, not a permanent library-wide limitation: rasterizing
/// PDF-native image bytes into pixels is <c>RasterImage</c>'s job (Phase 7, the Pixels
/// expansion), and full-page
/// rasterization (compositing every painted element, not just one image XObject) is
/// <c>Pdf.Rasterize</c>'s job (Phase 8) — feed <see cref="Data"/> to <c>RasterImage.Decode</c>
/// when decoded pixels are what you need, rather than reading them from
/// <see cref="ExtractedImage"/> directly.
/// </summary>
/// <example>
/// <code>
/// foreach (ExtractedImage image in page.ExtractImages())
/// {
///     var extension = image.IsJpeg ? "jpg" : "bin";
///     File.WriteAllBytes($"image-{image.Reference?.Number}.{extension}", image.Data.ToArray());
/// }
/// </code>
/// </example>
public sealed class ExtractedImage
{
    internal ExtractedImage(
        IndirectReference? reference,
        int width,
        int height,
        int bitsPerComponent,
        string? colorSpaceName,
        IReadOnlyList<double>? decode,
        IndirectReference? softMaskReference,
        IReadOnlyList<string> filters,
        ReadOnlyMemory<byte> data,
        bool isJpeg,
        bool isRawEncoded)
    {
        Reference = reference;
        Width = width;
        Height = height;
        BitsPerComponent = bitsPerComponent;
        ColorSpaceName = colorSpaceName;
        Decode = decode;
        SoftMaskReference = softMaskReference;
        Filters = filters;
        Data = data;
        IsJpeg = isJpeg;
        IsRawEncoded = isRawEncoded;
    }

    /// <summary>The image XObject's own indirect-object identity, or <see langword="null"/> if it was embedded directly (nonconformant but tolerated).</summary>
    public IndirectReference? Reference { get; }

    /// <summary>
    /// The image's width in samples (<c>/Width</c>) — except for a <c>JPXDecode</c> (JPEG 2000)
    /// image decoded in-house, where this is the codestream's own width instead: the
    /// codestream's declared geometry is authoritative for what <see cref="Data"/>
    /// actually contains, and can disagree with a producer's <c>/Width</c>.
    /// </summary>
    public int Width { get; }

    /// <summary>The image's height in samples (<c>/Height</c>) — same JPX exception as <see cref="Width"/>.</summary>
    public int Height { get; }

    /// <summary>
    /// Bits per color component (<c>/BitsPerComponent</c>); meaningless when <see cref="IsJpeg"/>
    /// is <see langword="true"/> (the JPEG stream carries its own). For a JPX image this is
    /// derived from the codestream's own sample precision instead of the dictionary's value
    /// (8 when every colour plane is ≤8 bits, else 16).
    /// </summary>
    public int BitsPerComponent { get; }

    /// <summary>
    /// The <c>/ColorSpace</c> entry's name as data — <c>"DeviceRGB"</c>, <c>"DeviceGray"</c>,
    /// the first element of an <c>[/Indexed base hival lookup]</c> array, and so on — never
    /// resolved into an actual color model. <see langword="null"/> when absent or not a
    /// name (e.g. an <c>/ICCBased</c> stream reference), or when a JPX image declares no
    /// <c>/ColorSpace</c> of its own (the device-space-by-channel-count fallback). For a JPX
    /// image, this name is not guaranteed to describe how many channels <see cref="Data"/>
    /// actually carries — <see cref="Data"/> is always packed at the codestream's own channel
    /// count, and a declared name that disagrees with it is recorded as <c>PLUME7753</c> on the
    /// extraction call's diagnostics rather than silently trusted.
    /// </summary>
    public string? ColorSpaceName { get; }

    /// <summary>The <c>/Decode</c> array as data (component remapping ranges), or <see langword="null"/> when absent — never applied by PlumePDF.</summary>
    /// <remarks>
    /// Always <see langword="null"/> for a JPEG 2000 (<c>/JPXDecode</c>) image decoded in-house:
    /// ISO 32000-1 §7.4.9 says <c>/Decode</c> is ignored for JPX (the codestream's own sample
    /// range is authoritative), so a <c>/Decode</c> entry on such a dictionary is not surfaced
    /// here — reading it would suggest a remapping that no conforming reader applies.
    /// </remarks>
    public IReadOnlyList<double>? Decode { get; }

    /// <summary>The <c>/SMask</c> entry's indirect reference (a soft-mask image), or <see langword="null"/> when absent — resolve it via <c>doc.Objects</c> to extract the mask itself; never composited by PlumePDF.</summary>
    public IndirectReference? SoftMaskReference { get; }

    /// <summary>The image's <c>/Filter</c> chain, as declared (e.g. <c>["DCTDecode"]</c>, <c>["FlateDecode"]</c>) — empty when the image is unfiltered.</summary>
    public IReadOnlyList<string> Filters { get; }

    /// <summary>
    /// The image's bytes: intact JPEG file bytes when <see cref="IsJpeg"/>; filter-decoded raw
    /// samples when a supported non-DCT filter chain was fully decoded; still-encoded original
    /// bytes when <see cref="IsRawEncoded"/> (an unsupported filter — see the extraction call's
    /// diagnostics for which one). For a decoded <c>JPXDecode</c> (JPEG 2000) image, these are
    /// the codestream's own interleaved colour samples (alpha dropped) — laid out per
    /// <see cref="Width"/>/<see cref="Height"/>/<see cref="BitsPerComponent"/> and the
    /// codestream's own channel count, which is what to use for a size computation even when it
    /// disagrees with <see cref="ColorSpaceName"/> (see that property's own remarks and
    /// <c>PLUME7753</c>) — and, under <c>/Indexed</c>, exactly one index per pixel regardless of
    /// the codestream's channel count. Samples above 8 bits are full-scale rescaled to 16 bits —
    /// except under an <c>/Indexed</c> <see cref="ColorSpaceName"/>, where the samples are palette
    /// indices and are returned at their raw value (one byte each at ≤8-bit precision, two
    /// big-endian bytes above), never rescaled.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Whether <see cref="Data"/> is an intact JPEG file (the image's filter chain includes <c>DCTDecode</c>) — pass it through to any standard JPEG decoder/viewer as-is.</summary>
    public bool IsJpeg { get; }

    /// <summary>Whether <see cref="Data"/> is still filter-encoded because PlumePDF has no decoder registered for (one of) this image's filters — a diagnostic on the extraction call names which filter.</summary>
    public bool IsRawEncoded { get; }
}
