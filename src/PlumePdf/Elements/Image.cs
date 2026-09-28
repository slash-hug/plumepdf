namespace PlumePdf.Elements;

/// <summary>
/// A raster image, painted as a PDF image XObject (ISO 32000-1 §8.9.5). Phase 2 accepts raw,
/// already-decoded top-down 24-bit RGB pixel data directly (the
/// <see cref="Image(ReadOnlyMemory{byte},int,int)"/> constructor below) — decoding a
/// compressed file format was not this layer's job at the time, since no image codec was a
/// Layout-layer dependency yet. Phase 7 adds <see cref="Image(RasterImageFrame)"/>:
/// the RGB-24 constructor and its invariant are untouched — a public API a
/// caller already depends on stays exactly as it was — and the new constructor widens what an
/// <see cref="Image"/> can carry internally (grayscale, or RGB with an alpha channel) so
/// <c>PlumePdf.Layout.ManuscriptRenderer</c> can paint it without expanding every source to
/// 24-bit RGB first (a 300-dpi grayscale scan page is a third the size, uncompressed, of the
/// RGB expansion of the same page).
/// </summary>
/// <example>
/// <code>
/// byte[] pixels = new byte[4 * 4 * 3]; // a 4x4 placeholder square, decoded elsewhere
/// var logo = new Image(pixels, pixelWidth: 4, pixelHeight: 4) { Height = 40 };
///
/// var frame = RasterImage.Decode("photo.png").Frames[0];
/// var photo = new Image(frame); // sized from the PNG's own DPI by default
/// </code>
/// </example>
public sealed class Image : Element
{
    /// <summary>Creates an image from raw, top-down, 24-bit RGB pixel data (3 bytes per pixel, rows packed with no padding).</summary>
    /// <param name="rgbPixels">Exactly <c>pixelWidth * pixelHeight * 3</c> bytes.</param>
    /// <param name="pixelWidth">The image's width in pixels. Must be positive.</param>
    /// <param name="pixelHeight">The image's height in pixels. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pixelWidth"/> or <paramref name="pixelHeight"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="rgbPixels"/>'s length does not match <paramref name="pixelWidth"/> * <paramref name="pixelHeight"/> * 3.</exception>
    public Image(ReadOnlyMemory<byte> rgbPixels, int pixelWidth, int pixelHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        var expected = (long)pixelWidth * pixelHeight * 3;
        if (rgbPixels.Length != expected)
        {
            throw new ArgumentException($"Expected {expected} bytes of raw 24-bit RGB pixel data for a {pixelWidth}x{pixelHeight} image, got {rgbPixels.Length}.", nameof(rgbPixels));
        }

        _eagerRgbPixels = rgbPixels;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    /// <summary>
    /// Creates an image from an already-decoded <see cref="RasterImageFrame"/> (e.g.
    /// <c>RasterImage.Decode(...).Frames[0]</c>) — the Phase 7 door.
    /// <see cref="RgbPixels"/> is always available (a grayscale source is expanded to RGB on
    /// first access — see its own remarks; an RGBA source has its alpha channel split off
    /// eagerly) so every existing consumer of the Phase 2 invariant keeps working unchanged;
    /// the renderer paints the frame's own layout — gray or RGB(A) — via the internal payload
    /// this constructor also records, never the expanded view, so the emitted XObject stays
    /// exactly as small as the source frame implies.
    /// <see cref="Width"/>/<see cref="Height"/> default from
    /// <paramref name="frame"/>'s own <see cref="RasterImageFrame.XDpi"/>/
    /// <see cref="RasterImageFrame.YDpi"/> (96 dpi when the source declared none) rather than
    /// the base constructor's always-96-dpi fallback — an object initializer
    /// (<c>new Image(frame) { Width = ... }</c>) overrides either independently, exactly as
    /// with the RGB-24 constructor.
    /// </summary>
    /// <param name="frame">The decoded frame to paint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public Image(RasterImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        PixelWidth = frame.Width;
        PixelHeight = frame.Height;

        switch (frame.Format)
        {
            case RasterPixelFormat.Gray8:
                // RgbPixels' expansion is deferred to first access (see its own remarks) — a
                // grayscale-scan-heavy Pdf.FromImages batch whose renderer only ever reads
                // GrayPixels would otherwise pay for a 3x expansion nothing reads.
                GrayPixels = frame.Pixels;
                break;
            case RasterPixelFormat.Rgb24:
                _eagerRgbPixels = frame.Pixels;
                break;
            case RasterPixelFormat.Rgba32:
                (_eagerRgbPixels, _, AlphaPixels) = SplitRgba(frame.Pixels, frame.Width, frame.Height);
                break;
            default:
                throw new PlumePdfException("PLUME3602", $"Image(RasterImageFrame): pixel format {frame.Format} is not supported.");
        }

        if (frame.OriginalJpegBytes is { } originalJpeg)
        {
            OriginalJpegBytes = originalJpeg;
            OriginalJpegComponentCount = frame.OriginalJpegComponentCount;
            OriginalJpegAdobeInverted = frame.OriginalJpegAdobeInverted;
        }

        var dpiX = frame.XDpi ?? 96.0;
        var dpiY = frame.YDpi ?? 96.0;
        Width = frame.Width * 72.0 / dpiX;
        Height = frame.Height * 72.0 / dpiY;
    }

    /// <summary>
    /// Copies <paramref name="source"/> with <see cref="Width"/>/<see cref="Height"/>
    /// overridden — every other payload (RGB/gray/alpha pixels, JPEG pass-through bytes,
    /// <see cref="AltText"/>) carries over unchanged. Internal: <c>Compose.ImageDescriptor</c>'s
    /// seam for applying its own fluent <c>Width()</c>/<c>Height()</c> calls without
    /// reconstructing a fresh <see cref="Image"/> through the lossiest (RGB-24-only)
    /// constructor — which used to silently discard a <see cref="RasterImageFrame"/>-sourced
    /// image's gray/alpha/JPEG-pass-through payload for anyone going through
    /// <c>PdfDocument.Compose</c> rather than <c>Manuscript.Render</c> directly.
    /// </summary>
    internal Image(Image source, double? width, double? height)
    {
        ArgumentNullException.ThrowIfNull(source);
        PixelWidth = source.PixelWidth;
        PixelHeight = source.PixelHeight;
        _eagerRgbPixels = source._eagerRgbPixels;
        GrayPixels = source.GrayPixels;
        AlphaPixels = source.AlphaPixels;
        OriginalJpegBytes = source.OriginalJpegBytes;
        OriginalJpegComponentCount = source.OriginalJpegComponentCount;
        OriginalJpegAdobeInverted = source.OriginalJpegAdobeInverted;
        AltText = source.AltText;
        Align = source.Align;
        Role = source.Role;
        Width = width;
        Height = height;
    }

    private static byte[] ExpandGrayToRgb(ReadOnlyMemory<byte> gray, int width, int height)
    {
        var span = gray.Span;
        var rgb = new byte[(long)width * height * 3];
        for (var i = 0; i < span.Length; i++)
        {
            rgb[i * 3] = span[i];
            rgb[(i * 3) + 1] = span[i];
            rgb[(i * 3) + 2] = span[i];
        }

        return rgb;
    }

    private static (ReadOnlyMemory<byte> Rgb, ReadOnlyMemory<byte>? Gray, ReadOnlyMemory<byte>? Alpha) SplitRgba(ReadOnlyMemory<byte> rgba, int width, int height)
    {
        var span = rgba.Span;
        var pixelCount = width * height;
        var rgb = new byte[(long)pixelCount * 3];
        var alpha = new byte[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            rgb[i * 3] = span[i * 4];
            rgb[(i * 3) + 1] = span[(i * 4) + 1];
            rgb[(i * 3) + 2] = span[(i * 4) + 2];
            alpha[i] = span[(i * 4) + 3];
        }

        return (rgb, null, alpha);
    }

    /// <summary>The bytes handed to the RGB-24 constructor, or the RGB-24/RGBA-32-sourced frame's own pixels — set eagerly; <see langword="null"/> only when this <see cref="Image"/> was built from a <see cref="RasterPixelFormat.Gray8"/> frame, in which case <see cref="RgbPixels"/> computes and caches the expansion lazily instead.</summary>
    private readonly ReadOnlyMemory<byte>? _eagerRgbPixels;

    // Guards _lazyRgbFromGray: Image instances are read-only after construction but may be
    // shared across a multi-threaded render (docs/architecture.md "Threading & mutation"),
    // so the lazy expansion below has to be safe for two threads to race into RgbPixels'
    // getter simultaneously the first time. `volatile` is load-bearing here, not decorative
    // (finding 8): without it, the lock only serializes the two writers - a *reader* outside
    // the lock (the `if (_lazyRgbFromGray is null)` check below and the final `return`) can,
    // on a weaker memory model (ARM64), observe the non-null array reference before
    // ExpandGrayToRgb's element writes have become visible to that thread, handing back a
    // partially-zeroed buffer with no error. `volatile` gives the field's reads/writes the
    // acquire/release semantics this double-checked-locking pattern depends on.
    private readonly object _rgbExpansionLock = new();
    private volatile byte[]? _lazyRgbFromGray;

    /// <summary>
    /// The raw, top-down, 24-bit RGB pixel data — always available, regardless of which
    /// constructor built this <see cref="Image"/>. For a <see cref="RasterPixelFormat.Gray8"/>-sourced
    /// <see cref="Image(RasterImageFrame)"/>, this expansion is computed on first access and
    /// cached, not performed eagerly in the constructor (a 300-dpi grayscale scan
    /// page is a third the size, uncompressed, of its RGB expansion, and the renderer's own
    /// gray-native emission path — see <see cref="GrayPixels"/> — never needs this view at
    /// all, so a caller who only renders never pays for the expansion).
    /// </summary>
    public ReadOnlyMemory<byte> RgbPixels
    {
        get
        {
            if (_eagerRgbPixels is { } eager)
            {
                return eager;
            }

            if (_lazyRgbFromGray is null)
            {
                lock (_rgbExpansionLock)
                {
                    _lazyRgbFromGray ??= ExpandGrayToRgb(GrayPixels!.Value, PixelWidth, PixelHeight);
                }
            }

            return _lazyRgbFromGray;
        }
    }

    /// <summary>
    /// The source frame's original 8-bit grayscale samples, one byte per pixel, when this
    /// <see cref="Image"/> was built from a <see cref="RasterPixelFormat.Gray8"/>
    /// <see cref="RasterImageFrame"/> — <see langword="null"/> otherwise (including for the
    /// RGB-24 constructor). Internal: the renderer's payload-selection seam, not a
    /// public surface — <see cref="RgbPixels"/> is the public, always-populated view.
    /// </summary>
    internal ReadOnlyMemory<byte>? GrayPixels { get; }

    /// <summary>
    /// The source frame's original 8-bit alpha channel, one byte per pixel, when this
    /// <see cref="Image"/> was built from a <see cref="RasterPixelFormat.Rgba32"/>
    /// <see cref="RasterImageFrame"/> — <see langword="null"/> otherwise. Internal: the
    /// renderer uses this to emit a <c>/SMask</c> soft-mask XObject alongside the RGB image.
    /// </summary>
    internal ReadOnlyMemory<byte>? AlphaPixels { get; }

    /// <summary>The original, still-JPEG-encoded bytes when this <see cref="Image"/> was built from a JPEG-sourced <see cref="RasterImageFrame"/> (<see cref="RasterImageFrame.OriginalJpegBytes"/>) — <see langword="null"/> otherwise. Internal: <c>ManuscriptRenderer</c>'s DCT pass-through seam.</summary>
    internal ReadOnlyMemory<byte>? OriginalJpegBytes { get; private set; }

    /// <summary>The original JPEG's component count (1/3/4) — meaningless unless <see cref="OriginalJpegBytes"/> is set.</summary>
    internal int OriginalJpegComponentCount { get; private set; }

    /// <summary>Whether the original JPEG's 4-component samples are Adobe-inverted — meaningless unless <see cref="OriginalJpegBytes"/> is set and <see cref="OriginalJpegComponentCount"/> is 4.</summary>
    internal bool OriginalJpegAdobeInverted { get; private set; }

    /// <summary>The image's width in pixels.</summary>
    public int PixelWidth { get; }

    /// <summary>The image's height in pixels.</summary>
    public int PixelHeight { get; }

    /// <summary>
    /// The rendered width in points, or <see langword="null"/> to derive it from
    /// <see cref="Height"/> preserving the pixel aspect ratio, or (when both are
    /// <see langword="null"/>) from <see cref="PixelWidth"/> at 96 pixels per inch.
    /// </summary>
    public double? Width { get; init; }

    /// <summary>
    /// The rendered height in points, or <see langword="null"/> to derive it from
    /// <see cref="Width"/> preserving the pixel aspect ratio, or (when both are
    /// <see langword="null"/>) from <see cref="PixelHeight"/> at 96 pixels per inch.
    /// </summary>
    public double? Height { get; init; }

    /// <summary>
    /// A human-readable description of this image, for assistive technology (ISO 32000-1
    /// §14.9.3's <c>/Alt</c>) — required for every meaningful <see cref="Image"/> under PDF/UA.
    /// Only enforced when <see cref="Manuscript.Language"/> is set: rendering then throws a coded
    /// refusal (<c>PLUME9010</c>) for any <see cref="Image"/> that has neither
    /// <see cref="AltText"/> nor <see cref="Element.Role"/> set to <c>"Artifact"</c> — a
    /// tagged/PDF-UA document with an undescribed image is a document that fails conformance
    /// silently at the exact place an agent composing it can't see, so PlumePDF refuses to
    /// produce it instead (the creation path's fail-fast policy).
    /// </summary>
    /// <example>
    /// <code>
    /// var logo = new Image(pixels, 4, 4) { Height = 40, AltText = "Acme Corp logo" };
    /// var divider = new Image(pixels, 4, 1) { Role = "Artifact" }; // purely decorative — no AltText needed
    /// </code>
    /// </example>
    public string? AltText { get; init; }
}
