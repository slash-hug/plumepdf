namespace PlumePdf;

/// <summary>
/// The pixel layout a decoded <see cref="RasterImageFrame"/> normalizes to. Every codec this
/// phase ships (PNG, JPEG, TIFF) reduces its source's native bit depth/color model
/// down to one of these three 8-bits-per-channel shapes so downstream consumers
/// (<see cref="Elements.Image"/>, <c>Pdf.FromImages</c>, <c>RasterImageFrame.EncodePng</c>)
/// have exactly one code path per shape instead of one per source format. Higher bit depths
/// (PNG/TIFF 16-bit samples) are downsampled to 8 bits per channel at decode time, and a
/// 4-component (CMYK/YCCK) JPEG source is converted to <see cref="RasterPixelFormat.Rgb24"/> — a deliberate
/// v1.0 scope cut (bit-depth- and color-model-preserving raster storage is a 1.x concern for
/// this normalized decode-to-pixels facade; <c>ManuscriptRenderer</c>'s separate DCT
/// pass-through path preserves an original JPEG's bytes, CMYK included, unchanged when
/// embedding into a PDF).
/// </summary>
public enum RasterPixelFormat
{
    /// <summary>8 bits per pixel, one grayscale sample, no alpha.</summary>
    Gray8,

    /// <summary>24 bits per pixel, top-down, no row padding: R, G, B, R, G, B, ...</summary>
    Rgb24,

    /// <summary>32 bits per pixel, top-down, no row padding: R, G, B, A, R, G, B, A, ...</summary>
    Rgba32,
}

/// <summary>
/// One decoded frame of a <see cref="RasterImage"/> — a single still image (PNG and JPEG have
/// exactly one; a multi-frame TIFF has one per page/subfile). Pixels are always top-down with
/// no row padding, in <see cref="Format"/>'s layout.
/// </summary>
/// <example>
/// <code>
/// var image = RasterImage.Decode(File.ReadAllBytes("photo.png"));
/// RasterImageFrame frame = image.Frames[0];
/// Console.WriteLine($"{frame.Width}x{frame.Height} {frame.Format} @ {frame.XDpi:0}x{frame.YDpi:0} dpi");
/// </code>
/// </example>
public sealed class RasterImageFrame
{
    /// <summary>Creates a decoded frame. Public so a hand-rolled or third-party codec can hand PlumePDF pixels it decoded itself.</summary>
    /// <param name="pixels">Exactly <c>width * height * BytesPerPixel(format)</c> bytes, top-down, unpadded.</param>
    /// <param name="width">The frame's width in pixels. Must be positive.</param>
    /// <param name="height">The frame's height in pixels. Must be positive.</param>
    /// <param name="format">The pixel layout <paramref name="pixels"/> is arranged in.</param>
    /// <param name="xDpi">The horizontal resolution in pixels per inch, or <see langword="null"/> when the source declared none (e.g. a PNG with no <c>pHYs</c> chunk, or one whose unit specifier is "unknown").</param>
    /// <param name="yDpi">The vertical resolution in pixels per inch, or <see langword="null"/> when the source declared none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="pixels"/>'s length does not match <paramref name="width"/> * <paramref name="height"/> * the bytes-per-pixel <paramref name="format"/> implies.</exception>
    public RasterImageFrame(ReadOnlyMemory<byte> pixels, int width, int height, RasterPixelFormat format, double? xDpi = null, double? yDpi = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var bytesPerPixel = BytesPerPixel(format);
        var expected = (long)width * height * bytesPerPixel;
        if (pixels.Length != expected)
        {
            throw new ArgumentException($"Expected {expected} bytes of {format} pixel data for a {width}x{height} frame, got {pixels.Length}.", nameof(pixels));
        }

        Pixels = pixels;
        Width = width;
        Height = height;
        Format = format;
        XDpi = xDpi is > 0 ? xDpi : null;
        YDpi = yDpi is > 0 ? yDpi : null;
    }

    /// <summary>
    /// The original, still-JPEG-encoded file bytes this frame was decoded from, when the
    /// source was a JPEG — <see langword="null"/> for a PNG/TIFF source, or a
    /// caller-constructed frame via the public constructor above (only
    /// <see cref="RasterImage.Decode(ReadOnlyMemory{byte},PdfOptions?)"/>'s own JPEG dispatch
    /// sets this). Set together with <see cref="OriginalJpegComponentCount"/> and
    /// <see cref="OriginalJpegAdobeInverted"/>, internal-only: <see cref="Elements.Image"/>
    /// threads all three through so <c>ManuscriptRenderer</c> can emit a true <c>/DCTDecode</c>
    /// pass-through XObject instead of re-encoding <see cref="Pixels"/>' already
    /// color-converted view — pass-through needs the original bytes and component count
    /// exactly as the encoder wrote them, not this frame's normalized (CMYK-&gt;RGB-converted,
    /// for a 4-component source) pixel view.
    /// </summary>
    internal ReadOnlyMemory<byte>? OriginalJpegBytes { get; set; }

    /// <summary>The original JPEG's component count (1 gray, 3 RGB/YCbCr, 4 CMYK/YCCK) — meaningless unless <see cref="OriginalJpegBytes"/> is set.</summary>
    internal int OriginalJpegComponentCount { get; set; }

    /// <summary>
    /// Whether the original JPEG carried an Adobe <c>APP14</c> marker — the signal that its
    /// 4-component samples are stored inverted and a pass-through consumer needs to
    /// emit <c>/Decode [1 0 1 0 1 0 1 0]</c> to undo it. Meaningless unless
    /// <see cref="OriginalJpegBytes"/> is set and <see cref="OriginalJpegComponentCount"/> is 4.
    /// </summary>
    internal bool OriginalJpegAdobeInverted { get; set; }

    /// <summary>The decoded, top-down, unpadded pixel bytes in <see cref="Format"/>'s layout.</summary>
    public ReadOnlyMemory<byte> Pixels { get; }

    /// <summary>The frame's width in pixels.</summary>
    public int Width { get; }

    /// <summary>The frame's height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixel layout <see cref="Pixels"/> is arranged in.</summary>
    public RasterPixelFormat Format { get; }

    /// <summary>The horizontal resolution in pixels per inch the source declared, or <see langword="null"/> when none was declared.</summary>
    public double? XDpi { get; }

    /// <summary>The vertical resolution in pixels per inch the source declared, or <see langword="null"/> when none was declared.</summary>
    public double? YDpi { get; }

    /// <summary>The number of bytes one pixel occupies in <paramref name="format"/>'s layout.</summary>
    public static int BytesPerPixel(RasterPixelFormat format) => format switch
    {
        RasterPixelFormat.Gray8 => 1,
        RasterPixelFormat.Rgb24 => 3,
        RasterPixelFormat.Rgba32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>
    /// Encodes this frame as a complete PNG file (<see cref="Filters.Png.PngEncoder"/>) —
    /// grayscale, RGB, or RGBA depending on <see cref="Format"/>, always 8 bits per channel,
    /// with a <c>pHYs</c> chunk when <see cref="XDpi"/>/<see cref="YDpi"/> are known.
    /// </summary>
    /// <param name="options">Supplies the Flate encoder (<see cref="PdfOptions.Filters"/>) and any future encode-time knob. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <example>
    /// <code>
    /// var frame = RasterImage.Decode(File.ReadAllBytes("photo.png")).Frames[0];
    /// File.WriteAllBytes("copy.png", frame.EncodePng());
    /// </code>
    /// </example>
    public byte[] EncodePng(PdfOptions? options = null) => Filters.Png.PngEncoder.Encode(this, options ?? PdfOptions.Default);

    /// <summary>
    /// Encodes this frame as a baseline JPEG file (<see cref="Filters.JpegEncoder"/>) at
    /// <paramref name="quality"/> (1-100, IJG scaling), 4:2:0 chroma-subsampled. A
    /// <see cref="RasterPixelFormat.Rgba32"/> source has its alpha flattened by compositing
    /// over white first — baseline JPEG carries no alpha channel.
    /// </summary>
    /// <param name="quality">The IJG-scaled JPEG quality, 1-100. Defaults to 95.</param>
    /// <example>
    /// <code>
    /// var frame = RasterImage.Decode(File.ReadAllBytes("photo.png")).Frames[0];
    /// File.WriteAllBytes("copy.jpg", frame.EncodeJpeg(quality: 90));
    /// </code>
    /// </example>
    public byte[] EncodeJpeg(int quality = 95)
    {
        var (pixels, componentCount) = Format switch
        {
            RasterPixelFormat.Gray8 => (Pixels.ToArray(), 1),
            RasterPixelFormat.Rgb24 => (Pixels.ToArray(), 3),
            RasterPixelFormat.Rgba32 => (FlattenRgbaOverWhite(Pixels.Span), 3),
            _ => throw new ArgumentOutOfRangeException(null, Format, "RasterImageFrame.EncodeJpeg: unsupported pixel format."),
        };

        return Filters.JpegEncoder.Encode(pixels, Width, Height, componentCount, quality, subsampleChroma: true, xDpi: XDpi, yDpi: YDpi);
    }

    private byte[] FlattenRgbaOverWhite(ReadOnlySpan<byte> rgba)
    {
        var rgb = new byte[(long)Width * Height * 3];
        for (var i = 0; i < Width * Height; i++)
        {
            var srcOffset = i * 4;
            var alpha = rgba[srcOffset + 3];
            for (var c = 0; c < 3; c++)
            {
                var fg = rgba[srcOffset + c];
                rgb[(i * 3) + c] = (byte)((((fg * alpha) + (255 * (255 - alpha))) + 127) / 255);
            }
        }

        return rgb;
    }
}
