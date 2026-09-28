using PlumePdf.Filters;
using PlumePdf.Filters.Jpx;
using PlumePdf.Filters.Png;
using PlumePdf.Filters.Tiff;

namespace PlumePdf;

/// <summary>
/// Decodes a raster image file (PNG, JPEG, TIFF, or JPEG 2000) into one or more <see cref="RasterImageFrame"/>s,
/// entirely independent of any <see cref="PdfDocument"/> (<see cref="RasterImage"/>
/// lives in the <c>Filters</c> layer, folder and namespace both, mirroring
/// <see cref="PdfFilterRegistry"/> — the seam that makes a PDF-free imaging surface possible
/// at all). A 4-component (CMYK/YCCK) JPEG source is converted to <see cref="RasterPixelFormat.Rgb24"/>
/// for this normalized view (undoing Adobe's storage inversion first when an <c>APP14</c>
/// marker was present) — <see cref="RasterPixelFormat"/> has no CMYK shape; a caller that needs
/// the original CMYK samples byte-for-byte wants <c>DCTDecode</c> pass-through
/// (<c>ManuscriptRenderer</c>'s image-embedding path), not this decode-to-pixels facade.
/// Per-frame degradation: a multi-frame source (TIFF) whose frame N fails decodes every
/// other frame and records a diagnostic naming N; a single-frame source whose only frame fails
/// throws instead, since there would be nothing left to return.
/// </summary>
/// <example>
/// <code>
/// var image = RasterImage.Decode(File.ReadAllBytes("photo.png"));
/// var frame = image.Frames[0];
/// Console.WriteLine($"{frame.Width}x{frame.Height} {frame.Format}");
/// File.WriteAllBytes("copy.png", frame.EncodePng());
/// </code>
/// </example>
public sealed class RasterImage
{
    private RasterImage(IReadOnlyList<RasterImageFrame> frames, DiagnosticCollection diagnostics)
    {
        Frames = frames;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Wraps already-decoded/rendered frames as a <see cref="RasterImage"/> — the factory
    /// <c>Documents.PageRasterAdapter</c> uses for <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c>
    /// (Phase 8), whose frames come from <c>Raster.Rasterizer.Rasterize</c>
    /// rather than one of this type's own codec-dispatching <see cref="Decode(ReadOnlyMemory{byte},PdfOptions?)"/>
    /// overloads. Internal: every public construction path stays either <c>Decode</c> or the
    /// per-frame public constructor.
    /// </summary>
    internal static RasterImage FromFrames(IReadOnlyList<RasterImageFrame> frames, DiagnosticCollection diagnostics) => new(frames, diagnostics);

    /// <summary>
    /// Every frame this source decoded, in source order. A well-formed PNG always yields
    /// exactly one. Never empty for a call that returned normally — a source whose only frame
    /// is undecodable throws instead.
    /// </summary>
    public IReadOnlyList<RasterImageFrame> Frames { get; }

    /// <summary>Recoverable deviations encountered while decoding — e.g. a skipped frame in a multi-frame source. Result-scoped, like <see cref="PdfPage.ExtractImagesWithDiagnostics"/>, not folded into a shared document-wide collection (there is no document here).</summary>
    public DiagnosticCollection Diagnostics { get; }

    /// <summary>
    /// Decodes <paramref name="data"/> by sniffing its container (PNG signature; JPEG SOI;
    /// TIFF byte-order mark; JP2 signature box or raw J2K <c>SOC</c>+<c>SIZ</c>) and dispatching
    /// to the matching codec.
    /// </summary>
    /// <param name="data">The complete, encoded image file bytes.</param>
    /// <param name="options">
    /// Carries the library-wide resource caps and <see cref="PdfOptions.Strict"/> switch — no
    /// PDF is involved (a naming wart, accepted deliberately: one cap story, one IL gate,
    /// rather than forking a PDF-free options type for this one surface). Defaults to
    /// <see cref="PdfOptions.Default"/>.
    /// </param>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME3600</c> — the bytes don't start with a recognized PNG/JPEG/TIFF/JPEG 2000
    /// signature; <c>PLUME3603</c> — a TIFF container's only frame (or every frame) failed to
    /// decode, so there is nothing to return; <c>PLUME3604</c> — a JPEG 2000 source decoded but
    /// carries a colour-channel count this facade's pixel formats cannot represent (anything
    /// other than 1, 3, or 4 colour channels, optionally plus one <c>cdef</c> opacity channel);
    /// or a codec-specific <c>PLUME32xx</c>-<c>PLUME35xx</c> / <c>PLUME37xx</c> (JPEG 2000)
    /// refusal from the dispatched decoder itself.
    /// </exception>
    /// <example>
    /// <code>
    /// var image = RasterImage.Decode(File.ReadAllBytes("photo.png"));
    /// </code>
    /// </example>
    public static RasterImage Decode(ReadOnlyMemory<byte> data, PdfOptions? options = null)
    {
        var effectiveOptions = options ?? PdfOptions.Default;
        var span = data.Span;
        var diagnostics = new DiagnosticCollection();

        if (IsPng(span))
        {
            var frame = PngDecoder.Decode(span, effectiveOptions.MaxImagePixels, diagnostics, effectiveOptions);
            return new RasterImage([frame], diagnostics);
        }

        if (IsJpeg(span))
        {
            var frame = DecodeJpegFrame(data, effectiveOptions, diagnostics);
            return new RasterImage([frame], diagnostics);
        }

        if (IsTiff(span))
        {
            return DecodeTiff(data, effectiveOptions, diagnostics);
        }

        if (JpxImageDecoder.IsJpx(span))
        {
            var frame = DecodeJpxFrame(data, effectiveOptions, diagnostics);
            return new RasterImage([frame], diagnostics);
        }

        throw new PlumePdfException("PLUME3600", "RasterImage.Decode: unrecognized container — the bytes don't start with a known PNG signature (89 50 4E 47 0D 0A 1A 0A), JPEG SOI marker (FF D8), TIFF byte-order mark ('II*\\0' / 'MM\\0*'), or JPEG 2000 JP2 signature box / raw codestream marker (FF 4F FF 51).");
    }

    private static RasterImageFrame DecodeJpegFrame(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection diagnostics)
    {
        var result = JpegDecoder.Decode(data, options, diagnostics, subject: null);
        var adobeInverted = result.AdobeTransform is not null;
        (byte[] Pixels, RasterPixelFormat Format) normalized = result.ComponentCount switch
        {
            1 => (result.Pixels, RasterPixelFormat.Gray8),
            3 => (result.Pixels, RasterPixelFormat.Rgb24),
            4 => (ConvertCmykToRgb(result.Pixels, result.Width, result.Height, adobeInverted), RasterPixelFormat.Rgb24),
            _ => throw new PlumePdfException("PLUME3204", $"JPEG frame declares {result.ComponentCount} components - only 1, 3, or 4 are supported."),
        };

        return new RasterImageFrame(normalized.Pixels, result.Width, result.Height, normalized.Format, result.XDpi, result.YDpi)
        {
            // Carried alongside the normalized Pixels view so a caller that only wants pixels
            // (this type's headline contract) is unaffected; Elements.Image threads these
            // through so ManuscriptRenderer can pass the original bytes straight into a
            // /DCTDecode XObject instead of re-encoding the (for CMYK, already color-converted)
            // Pixels view.
            OriginalJpegBytes = data,
            OriginalJpegComponentCount = result.ComponentCount,
            OriginalJpegAdobeInverted = adobeInverted,
        };
    }

    /// <summary>
    /// Decodes a JP2/J2K source into one normalized frame: <see cref="RasterPixelFormat.Gray8"/>
    /// for a single colour channel, <see cref="RasterPixelFormat.Rgb24"/> for three (or four —
    /// CMYK — converted through the same <see cref="JpegColorTransforms.ConvertCmykToRgb"/> naive
    /// path <see cref="DecodeJpegFrame"/> uses, with no Adobe <c>APP14</c> inversion since JPEG
    /// 2000 has no such marker), and <see cref="RasterPixelFormat.Rgba32"/> whenever a <c>cdef</c>
    /// opacity channel is present (a grayscale colour is replicated across R/G/B so alpha always
    /// has an RGBA home — this facade has no gray+alpha format). Every precision is folded to
    /// 8 bits per channel via <see cref="JpxImage.Rescale"/> regardless of the
    /// codestream's own bit depth — <see cref="RasterPixelFormat"/> has no wider representation,
    /// unlike <see cref="JpxFilterAdapter"/>'s PDF-facing byte contract, which preserves
    /// precision above 8 bits as two bytes per sample. DPI comes from the JP2 <c>res </c> box's
    /// pixels-per-metre (converted via <c>* 0.0254</c>, ISO 32000-1's own metre-to-inch factor),
    /// or is left unset (falling back to <see cref="RasterImageFrame"/>'s constructor default)
    /// when the box is absent — <see cref="RasterImageFrame"/>'s own <c>null</c> convention, not
    /// a hardcoded 96.
    /// </summary>
    private static RasterImageFrame DecodeJpxFrame(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection diagnostics)
    {
        var image = JpxImageDecoder.Decode(data, options, diagnostics);
        var width = image.Width;
        var height = image.Height;
        var pixelCount = width * height;

        double? xDpi = null;
        double? yDpi = null;
        if (image.Colour.PixelsPerMetre is { } pixelsPerMetre)
        {
            xDpi = pixelsPerMetre.X * 0.0254;
            yDpi = pixelsPerMetre.Y * 0.0254;
        }

        var colourChannelCount = image.ColourChannelCount;
        if (colourChannelCount is not (1 or 3 or 4))
        {
            // A legal codestream can carry any component count (a raw .j2k gray+alpha pair
            // without a cdef box reads as two colour channels; multispectral captures carry
            // five or more), but this facade's three pixel formats have no home for them.
            // Refuse with a code rather than let RasterImageFrame's constructor throw an
            // ArgumentException out of a public method over well-formed input.
            throw new PlumePdfException("PLUME3604", $"RasterImage.Decode: unsupported JPEG 2000 channel layout — the codestream carries {colourChannelCount} colour channel(s){(image.Colour.AlphaChannelIndex is null ? string.Empty : " plus one cdef opacity channel")}; supported layouts are 1 (gray), 3 (RGB), or 4 (CMYK) colour channels, optionally plus one cdef opacity channel.");
        }

        if (colourChannelCount == 4)
        {
            // CMYK: no Adobe APP14 marker in JPEG 2000, so no inversion. This facade's CMYK path
            // emits Rgb24 only — it does not thread a cdef opacity channel through to an Rgba32
            // frame the way the 1/3-channel path below does (a v1 scope cut: no fixture in the
            // matrix has CMYK + alpha) — so that opacity channel is dropped. Recorded, never
            // silent: Info under PLUME3604's "unsupported JPEG
            // 2000 channel layout" page, since the colour DID decode and only the alpha is lost.
            if (image.Colour.AlphaChannelIndex is not null)
            {
                diagnostics.Add(new PdfDiagnostic("PLUME3604", DiagnosticSeverity.Info, "RasterImage.Decode: JPEG 2000 source carries 4 (CMYK) colour channels plus a cdef opacity channel; the colour decoded to Rgb24 and the opacity channel was dropped (this facade's CMYK path emits Rgb24 only; its alpha is not carried into an Rgba32 frame)."));
            }

            var cmyk = image.ToInterleaved8Bit(dropAlpha: true);
            var rgbFromCmyk = JpegColorTransforms.ConvertCmykToRgb(cmyk, width, height, adobeInverted: false);
            return new RasterImageFrame(rgbFromCmyk, width, height, RasterPixelFormat.Rgb24, xDpi, yDpi);
        }

        if (image.Colour.AlphaChannelIndex is not { } alphaIndex)
        {
            var bytes = image.ToInterleaved8Bit(dropAlpha: true);
            var format = colourChannelCount == 1 ? RasterPixelFormat.Gray8 : RasterPixelFormat.Rgb24;
            return new RasterImageFrame(bytes, width, height, format, xDpi, yDpi);
        }

        // Alpha kept: built sample-by-sample rather than through ToInterleaved8Bit(dropAlpha:
        // false), whose plane order is whatever JpxImageDecoder produced — cdef (I.5.3.6) names
        // the opacity channel by index, not by position, so it need not be the last plane.
        var colourPlaneIndices = new List<int>(colourChannelCount);
        for (var p = 0; p < image.Planes.Length; p++)
        {
            if (p != alphaIndex)
            {
                colourPlaneIndices.Add(p);
            }
        }

        var rgba = new byte[pixelCount * 4];
        for (var i = 0; i < pixelCount; i++)
        {
            byte r, g, b;
            if (colourPlaneIndices.Count >= 3)
            {
                r = JpxSampleToByte(image, colourPlaneIndices[0], i);
                g = JpxSampleToByte(image, colourPlaneIndices[1], i);
                b = JpxSampleToByte(image, colourPlaneIndices[2], i);
            }
            else
            {
                r = g = b = JpxSampleToByte(image, colourPlaneIndices[0], i);
            }

            var o = i * 4;
            rgba[o] = r;
            rgba[o + 1] = g;
            rgba[o + 2] = b;
            rgba[o + 3] = JpxSampleToByte(image, alphaIndex, i);
        }

        return new RasterImageFrame(rgba, width, height, RasterPixelFormat.Rgba32, xDpi, yDpi);
    }

    /// <summary>One <paramref name="planeIndex"/> sample of <paramref name="image"/> at <paramref name="index"/>, full-scale rescaled to a byte — the per-sample version of <see cref="JpxImage.ToInterleaved8Bit"/>'s own loop, used here because <see cref="DecodeJpxFrame"/> reorders planes around an alpha channel that need not be last.</summary>
    private static byte JpxSampleToByte(JpxImage image, int planeIndex, int index)
    {
        var plane = image.Planes[planeIndex];
        var half = plane.Signed ? 1 << (plane.Precision - 1) : 0;
        return (byte)JpxImage.Rescale(plane.Sample(index) + half, plane.MaxValue, 255);
    }

    /// <summary>
    /// Delegates to the shared <see cref="JpegColorTransforms.ConvertCmykToRgb"/> helper
    /// — one Adobe-APP14-aware inversion + CMYK→RGB path consumed by both this
    /// decode facade and the render-time image resolver, so the two can never drift apart
    /// (this codebase has shipped the double-inversion bug class before, for TIFF/CCITT polarity).
    /// </summary>
    private static byte[] ConvertCmykToRgb(byte[] cmyk, int width, int height, bool adobeInverted) =>
        JpegColorTransforms.ConvertCmykToRgb(cmyk, width, height, adobeInverted);

    private static RasterImage DecodeTiff(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection diagnostics)
    {
        var ifds = TiffReader.ReadIfdChain(data, options.MaxImageFrames, options, diagnostics, subject: null);
        var frames = new List<RasterImageFrame>(ifds.Count);

        foreach (var ifd in ifds)
        {
            var tiffFrame = TiffFrameDecoder.DecodeFrame(data, ifd, options.MaxImagePixels, options, diagnostics, subject: null);
            if (tiffFrame is null)
            {
                // A per-frame refusal (unsupported compression/photometric, malformed tags,
                // ...) — TiffFrameDecoder already recorded the PLUME33xx diagnostic naming
                // this IFD; the per-frame degradation model skips it and keeps going.
                continue;
            }

            // TiffFrameDecoder validates Compression/PlanarConfiguration/Photometric but not
            // BitsPerSample/SamplesPerPixel themselves: a well-formed 12-bit or
            // 32-bit-float TIFF is a real format, not corruption, and must not reach
            // ConvertTiffFrame's packed-sample math, which only handles the {1,2,4,8,16}
            // depths it was written for. Validate here, before any pixel-buffer arithmetic,
            // so an unsupported-but-well-formed layout degrades per the per-frame model (this
            // frame skipped, coded PLUME33xx diagnostic recorded, Strict upgrades to a throw)
            // instead of a bare DivideByZeroException/OverflowException escaping.
            if (!TryValidateTiffSampleLayout(tiffFrame, ifd.Offset, options, diagnostics))
            {
                continue;
            }

            frames.Add(ConvertTiffFrame(tiffFrame));
        }

        if (frames.Count == 0)
        {
            // The RasterImage that would normally carry these diagnostics is never
            // constructed on this path (there is nothing to return), so the per-frame
            // PLUME33xx reasons TiffFrameDecoder already recorded would otherwise be
            // silently lost — folded into the thrown message instead.
            var reasons = string.Join("; ", diagnostics.Select(static d => $"{d.Code}: {d.Message}"));
            throw new PlumePdfException("PLUME3603", $"RasterImage.Decode: every frame of this TIFF ({ifds.Count} IFD(s)) failed to decode — nothing to return. Reasons: {reasons}");
        }

        return new RasterImage(frames, diagnostics);
    }

    /// <summary>The only <c>BitsPerSample</c> values <see cref="TiffReadSample"/>'s packed-sample math and <see cref="ConvertTiffFrame"/>'s row layout actually handle — 8 and 16 are special-cased, and 1/2/4 divide 8 evenly for the generic packed path. A well-formed but unsupported depth (12-bit, 32-bit-float, ...) is a coded refusal (finding 3), not a bare <see cref="DivideByZeroException"/>.</summary>
    private static bool IsSupportedTiffBitsPerSample(int bitsPerSample) => bitsPerSample is 1 or 2 or 4 or 8 or 16;

    /// <summary>Cross-checks <c>SamplesPerPixel</c> against the frame's <c>PhotometricInterpretation</c> — <see cref="ConvertTiffFrame"/> only knows how to unpack Palette/gray as 1 sample and RGB as (at least) 3, so anything else would misindex the packed row.</summary>
    private static bool IsSupportedTiffSampleLayout(int samplesPerPixel, long photometric) =>
        photometric switch
        {
            TiffPhotometric.Palette => samplesPerPixel == 1,
            TiffPhotometric.WhiteIsZero or TiffPhotometric.BlackIsZero => samplesPerPixel == 1,
            _ => samplesPerPixel is >= 3 and <= 4, // RGB (3) or RGB + one extra sample (4, e.g. alpha) — the RGB unpack path reads only the first 3 of either.
        };

    /// <summary>
    /// Validates a decoded <see cref="TiffFrame"/>'s sample layout before <see cref="ConvertTiffFrame"/>
    /// does any pixel-buffer arithmetic with it: an unsupported <c>BitsPerSample</c>,
    /// a <c>SamplesPerPixel</c> that doesn't match the photometric, or a row-buffer size that
    /// would overflow a 32-bit allocation all refuse here — coded PLUME33xx, per-frame,
    /// rather than a bare <see cref="DivideByZeroException"/>/<see cref="OverflowException"/>
    /// surfacing out of the unpack loops.
    /// </summary>
    private static bool TryValidateTiffSampleLayout(TiffFrame frame, long ifdOffset, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (!IsSupportedTiffBitsPerSample(frame.BitsPerSample))
        {
            FilterDiagnostics.ReportDeviation("PLUME3317", $"TIFF: frame at IFD offset {ifdOffset} declares BitsPerSample {frame.BitsPerSample} - only 1, 2, 4, 8, and 16 are supported; skipping it.", options, diagnostics, subject: null);
            return false;
        }

        if (!IsSupportedTiffSampleLayout(frame.SamplesPerPixel, frame.Photometric))
        {
            FilterDiagnostics.ReportDeviation("PLUME3318", $"TIFF: frame at IFD offset {ifdOffset} declares SamplesPerPixel {frame.SamplesPerPixel} which is not valid for PhotometricInterpretation {frame.Photometric}; skipping it.", options, diagnostics, subject: null);
            return false;
        }

        var rowBytes = ((long)frame.Width * frame.SamplesPerPixel * frame.BitsPerSample + 7) / 8;
        if (rowBytes <= 0 || rowBytes > int.MaxValue || rowBytes * frame.Height > int.MaxValue)
        {
            FilterDiagnostics.ReportDeviation("PLUME3319", $"TIFF: frame at IFD offset {ifdOffset} ({frame.Width}x{frame.Height}, SamplesPerPixel={frame.SamplesPerPixel}, BitsPerSample={frame.BitsPerSample}) would require a pixel row/buffer larger than this platform can allocate; skipping it.", options, diagnostics, subject: null);
            return false;
        }

        return true;
    }

    /// <summary>Unpacks one decoded <see cref="TiffFrame"/>'s raw MSB-first packed samples into a normalized <see cref="RasterImageFrame"/> (grayscale/bilevel, RGB, or palette-expanded-to-RGB — CMYK/YCbCr TIFF frames are already refused upstream by <see cref="TiffFrameDecoder"/>, and unsupported <c>BitsPerSample</c>/<c>SamplesPerPixel</c> layouts by <see cref="TryValidateTiffSampleLayout"/>).</summary>
    private static RasterImageFrame ConvertTiffFrame(TiffFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        var bitsPerSample = frame.BitsPerSample;
        var samplesPerPixel = frame.SamplesPerPixel;
        // Long-computed then narrowed: TryValidateTiffSampleLayout already proved this fits an
        // int, but the multiplication itself must not run in 32-bit arithmetic first (finding
        // 3b) or an extreme-but-technically-capped width/samples/depth combination could wrap
        // before the validated long comparison ever gets a chance to catch it.
        var rowBytes = (int)((((long)width * samplesPerPixel * bitsPerSample) + 7) / 8);
        var maxSample = (1 << bitsPerSample) - 1;

        if (frame.Photometric == TiffPhotometric.Palette && frame.ColorMap is { } colorMap)
        {
            var paletteSize = 1 << bitsPerSample;
            var rgb = new byte[(long)width * height * 3];
            for (var y = 0; y < height; y++)
            {
                var row = frame.Pixels.AsSpan(y * rowBytes, rowBytes);
                for (var x = 0; x < width; x++)
                {
                    var index = Math.Min(TiffReadSample(row, bitsPerSample, x), paletteSize - 1);
                    var destIndex = ((y * width) + x) * 3;
                    rgb[destIndex] = (byte)(colorMap[index] >> 8);
                    rgb[destIndex + 1] = (byte)(colorMap[paletteSize + index] >> 8);
                    rgb[destIndex + 2] = (byte)(colorMap[(2 * paletteSize) + index] >> 8);
                }
            }

            return new RasterImageFrame(rgb, width, height, RasterPixelFormat.Rgb24, frame.XDpi, frame.YDpi);
        }

        if (samplesPerPixel == 1)
        {
            var invert = frame.Photometric == TiffPhotometric.WhiteIsZero;
            var gray = new byte[(long)width * height];
            for (var y = 0; y < height; y++)
            {
                var row = frame.Pixels.AsSpan(y * rowBytes, rowBytes);
                for (var x = 0; x < width; x++)
                {
                    var sample = TiffReadSample(row, bitsPerSample, x);
                    if (invert)
                    {
                        sample = maxSample - sample;
                    }

                    gray[(y * width) + x] = TiffScale(sample, maxSample);
                }
            }

            return new RasterImageFrame(gray, width, height, RasterPixelFormat.Gray8, frame.XDpi, frame.YDpi);
        }

        var rgbOut = new byte[(long)width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var row = frame.Pixels.AsSpan(y * rowBytes, rowBytes);
            for (var x = 0; x < width; x++)
            {
                var destIndex = ((y * width) + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    var sample = TiffReadSample(row, bitsPerSample, (x * samplesPerPixel) + c);
                    rgbOut[destIndex + c] = TiffScale(sample, maxSample);
                }
            }
        }

        return new RasterImageFrame(rgbOut, width, height, RasterPixelFormat.Rgb24, frame.XDpi, frame.YDpi);
    }

    /// <summary>Reads the <paramref name="sampleIndex"/>-th <paramref name="bitDepth"/>-bit sample from a packed TIFF row, MSB-first (TIFF 6.0's packing order — the same convention PNG uses).</summary>
    private static int TiffReadSample(ReadOnlySpan<byte> row, int bitDepth, int sampleIndex)
    {
        if (bitDepth == 16)
        {
            var byteOffset = sampleIndex * 2;
            return (row[byteOffset] << 8) | row[byteOffset + 1];
        }

        if (bitDepth == 8)
        {
            return row[sampleIndex];
        }

        var samplesPerByte = 8 / bitDepth;
        var byteIndex = sampleIndex / samplesPerByte;
        var withinByte = sampleIndex % samplesPerByte;
        var shift = 8 - bitDepth - (withinByte * bitDepth);
        var mask = (1 << bitDepth) - 1;
        return (row[byteIndex] >> shift) & mask;
    }

    /// <summary>Scales a <paramref name="maxSample"/>-max raw sample to the 0-255 range.</summary>
    private static byte TiffScale(int raw, int maxSample) =>
        maxSample switch
        {
            255 => (byte)raw,
            65535 => (byte)(raw >> 8),
            _ => (byte)(((raw * 255) + (maxSample / 2)) / Math.Max(1, maxSample)),
        };

    /// <summary>The path overload of <see cref="Decode(ReadOnlyMemory{byte},PdfOptions?)"/> — reads the whole file, then decodes it.</summary>
    /// <param name="path">The path to the image file.</param>
    /// <param name="options">See <see cref="Decode(ReadOnlyMemory{byte},PdfOptions?)"/>. Defaults to <see cref="PdfOptions.Default"/>.</param>
    /// <exception cref="PlumePdfException"><c>PLUME3613</c> — the file could not be read; or any exception <see cref="Decode(ReadOnlyMemory{byte},PdfOptions?)"/> can throw.</exception>
    /// <example>
    /// <code>
    /// var image = RasterImage.Decode("photo.png");
    /// </code>
    /// </example>
    public static RasterImage Decode(string path, PdfOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PlumePdfException("PLUME3613", $"RasterImage.Decode: could not read '{path}' ({ex.Message}).", ex);
        }

        return Decode(bytes, options);
    }

    private static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8
        && data[0] == 137 && data[1] == 80 && data[2] == 78 && data[3] == 71
        && data[4] == 13 && data[5] == 10 && data[6] == 26 && data[7] == 10;

    private static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;

    private static bool IsTiff(ReadOnlySpan<byte> data) =>
        data.Length >= 4
        && ((data[0] == (byte)'I' && data[1] == (byte)'I' && data[2] == 42 && data[3] == 0)
            || (data[0] == (byte)'M' && data[1] == (byte)'M' && data[2] == 0 && data[3] == 42));
}
