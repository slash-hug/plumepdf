using PlumePdf.Filters;
using PlumePdf.Filters.Jpx;
using PlumePdf.Raster;
using PlumePdf.Raster.Color;

namespace PlumePdf.Documents;

/// <summary>
/// The render-time <see cref="RasterInterpreter.ImageResolver"/> production implementation:
/// decodes one <c>/Image</c> XObject
/// (or, via the same core, one inline <c>BI…ID…EI</c> image synthesized by
/// <c>RasterInterpreter</c>) to a colorspace-resolved <see cref="RasterImageFrame"/> — the piece
/// <c>PageRasterAdapter</c>'s remarks used to describe as "does not exist anywhere in this
/// codebase yet". <see cref="BuildResolver"/> mirrors <c>PageRasterAdapter.BuildWidgetAppearanceResolver</c>'s
/// own shape: a static factory that closes over this call's <see cref="ObjectRegistry"/>/
/// <see cref="PdfOptions"/>/<see cref="DiagnosticCollection"/> and a per-call decode cache, and
/// hands back a delegate with no signature change to <see cref="RasterInterpreter.ImageResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline, per image: declared <c>/Width</c>/<c>/Height</c>/component-count are validated
/// against <see cref="PdfOptions.MaxImagePixels"/> and an internal unpack-buffer guard BEFORE any
/// decode allocation runs (the caps-before-allocation discipline every Phase 7 codec already
/// follows). Two terminal filters are unwrapped directly rather than through the generic registry
/// path: <c>/DCTDecode</c> via <see cref="Filters.JpegDecoder.Decode"/> plus the shared
/// <see cref="Filters.JpegColorTransforms"/> helper (never a second, independently-authored
/// CMYK/YCCK inversion — the exact bug class this codebase has already shipped once for CCITT), and <c>/JPXDecode</c> via
/// <see cref="JpxImageDecoder.Decode"/> — both so the resolver can resolve
/// <c>/ColorSpace</c>/<c>/Mask</c>/<c>/SMaskInData</c> against the decoder's own facts instead of
/// an opaque interleaved-byte contract. A caller who registers a replacement <c>JPXDecode</c>
/// <see cref="IPdfFilter"/> (<c>PdfOptions.Filters</c>, detected via the registry's internal
/// <c>TryGetDecoder</c>) is honored instead: that image falls through to the generic path
/// below like any other filter, so a caller-supplied decoder is never shadowed by the built-in
/// one. <c>TryGetDecoder</c> answering "nothing registered" is treated exactly like "the built-in
/// decoder is registered" (both leave the direct unwrap in place) — a custom <see cref="PdfFilterRegistry"/>
/// that simply never adds a <c>JPXDecode</c> entry of its own still gets JPEG 2000 decoded
/// in-house, unlike every other filter name, where an unregistered decoder means <c>PLUME3010</c>.
/// Opting out of the built-in decoder therefore needs an explicit refusing <see cref="IPdfFilter"/>
/// registered under <c>JPXDecode</c> (the cookbook documents this shape), not simply omitting a
/// registration for it. Every other filter chain decodes through <see cref="PdfStream.GetDecodedBytes"/>. Samples are
/// then unpacked per <c>/BitsPerComponent</c> (1/2/4/8/16, 16-bit rows quantized to 8-bit output),
/// <c>/Decode</c>-array-interpolated, and colorspace-resolved: <c>Device*</c> fast paths avoid the
/// <see cref="RasterColorSpace.ToRgb"/> virtual span call per pixel entirely; a 1-component
/// non-device space (e.g. <c>CalGray</c>/<c>Separation</c>) precomputes a 256-entry lookup table
/// once per image instead of calling <see cref="RasterColorSpace.ToRgb"/> per pixel; only a
/// ≥2-component non-device space (<c>CalRGB</c>/<c>Lab</c>/multi-colorant <c>DeviceN</c>) pays the
/// per-pixel virtual-call cost. <c>/Indexed</c> palette lookup is resolver-local:
/// the base space's own possibly-throwing <c>ColorSpace.Parse</c> path (e.g. Indexed-as-Separation-
/// alternate hitting the deliberate <c>PLUME7717</c> refusal) is caught by this type's own
/// degrade-to-null-plus-<c>PLUME7744</c> posture, not grown into <c>ColorSpace.Parse</c> itself.
/// </para>
/// <para>
/// <c>/ImageMask true</c> stencils decode through the same 1-bit unpack/Decode pipeline into a
/// <see cref="RasterPixelFormat.Gray8"/> frame (painted samples land near 0, per §8.9.6.2's
/// default <c>[0 1]</c> Decode) — <c>ImagePainter</c> resolves the actual fill color at
/// paint time. <c>/SMask</c> and the stencil-stream form of <c>/Mask</c> (both
/// <c>/Mask</c> forms ship) decode
/// their own sub-image through this same core, nearest-neighbor-resampled to the base image's
/// dimensions if they differ, and merge as the alpha channel of a promoted
/// <see cref="RasterPixelFormat.Rgba32"/> frame; the color-key array form of <c>/Mask</c> is
/// tested against each pixel's own raw (pre-Decode-interpolation) component samples on EVERY
/// base-image path: inline during the non-DCT unpack pass (where an <c>/Indexed</c> image's raw
/// sample IS the index §8.9.6.4 defines the ranges over, so palette images are covered by the
/// same one-component test), and over the decoded 8-bit component buffer for a
/// terminal-<c>DCTDecode</c> (JPEG) base image — so the "both <c>/Mask</c> forms ship" claim
/// holds without carve-outs. (A terminal-DCT <c>/Decode</c> array is additionally read to
/// resolve a 4-component JPEG's CMYK polarity — never to re-interpolate sample values the way
/// the non-DCT path does, since JPEG output is already 8-bit.) <c>/SMask</c> takes precedence over <c>/Mask</c> when a
/// (malformed) document somehow carries both (§11.6.4.3). This resolver never re-derives
/// <c>/BlackIs1</c> polarity — <c>Filters.CcittFaxEngine</c> already bakes it into the bytes
/// <see cref="PdfStream.GetDecodedBytes"/> hands back, so re-deriving it here would be exactly the
/// double-inversion bug class reached from a second, independent call site.
/// </para>
/// <para>
/// Any <see cref="PlumePdfException"/> reached while resolving one image — an unresolvable
/// colorspace, a corrupt codec payload, a JPEG frame shape this codec doesn't support, a
/// malformed JPEG 2000 codestream (<c>PLUME370x</c>) — degrades that one image to "paint nothing"
/// plus a <c>PLUME7744</c> diagnostic (a skipped-not-approximated posture: never a gray
/// placeholder, never a page-wide failure); the same code also marks an image that decoded but
/// recorded its own recoverable deviation (a JBIG2 per-segment skip, a truncated JPX codestream)
/// as degraded-but-painted (<see cref="MarkDegradedIfDiagnosed"/>). <c>/ImageMask true</c>
/// combined with a terminal <c>JPXDecode</c> filter is refused outright as an illegal combination
/// (<c>PLUME7746</c>, ISO 32000-1 §7.4.9) before either pipeline runs. <c>PLUME7745</c>
/// (the old "register your own <c>JPXDecode</c> filter" diagnostic) is retired: JPEG 2000 decodes
/// in-house by default, and no site in this file mints it any more. A <c>/Separation</c> or
/// <c>/DeviceN</c> colorant of <c>/None</c> paints nothing with <em>no</em> diagnostic — §8.6.6.4
/// says plainly "no marks", not a decode failure.
/// </para>
/// </remarks>
internal static class ImageXObjectResolver
{
    /// <summary>
    /// The per-call decode-cache's total-pixel-bytes ceiling: an internal
    /// constant, deliberately not a <see cref="PdfOptions"/> cap (<c>PdfOptionsCapWiringTests</c>
    /// stays untouched). Once the cache's running total would exceed this, further frames are
    /// still decoded and painted — just not cached — so a document with many large images still
    /// renders correctly, only slower on a repeated <c>Do</c> of the same big XObject.
    /// </summary>
    internal const long DefaultDecodedBytesBudget = 256L * 1024 * 1024;

    private const int MaxComponentCount = 32;

    private static readonly PdfName WidthName = PdfName.Get("Width");
    private static readonly PdfName HeightName = PdfName.Get("Height");
    private static readonly PdfName BitsPerComponentName = PdfName.BitsPerComponent;
    private static readonly PdfName ColorSpaceName = PdfName.ColorSpace;
    private static readonly PdfName DecodeName = PdfName.Decode;
    private static readonly PdfName ImageMaskName = PdfName.ImageMask;
    private static readonly PdfName SMaskEntryName = PdfName.SMask;
    private static readonly PdfName MaskEntryName = PdfName.Get("Mask");
    private static readonly PdfName FilterName = PdfName.Filter;
    private static readonly PdfName SMaskInDataName = PdfName.Get("SMaskInData");

    /// <summary>
    /// Builds a production <see cref="RasterInterpreter.ImageResolver"/> — the single production
    /// wiring point, called from <see cref="PageRasterAdapter"/> (mirroring
    /// <c>BuildWidgetAppearanceResolver</c>'s own shape).
    /// </summary>
    /// <param name="objects">Resolves indirect references reached while decoding (<c>/SMask</c>, <c>/Mask</c>, an indirect <c>/ColorSpace</c> or <c>/Filter</c>/<c>/DecodeParms</c> entry).</param>
    /// <param name="options">Resource limits (<see cref="PdfOptions.MaxImagePixels"/>) and the active <see cref="PdfOptions.Filters"/> registry (a caller-registered <c>JPXDecode</c> override takes precedence over the built-in decoder).</param>
    /// <param name="diagnostics">Recoverable per-image deviations are appended here.</param>
    /// <param name="decodedBytesBudget">
    /// Overrides <see cref="DefaultDecodedBytesBudget"/> — internal-only, exercised by
    /// <c>ImageXObjectResolverTests</c>'s budget-eviction coverage; every production caller omits
    /// it and gets the real ~256 MB ceiling.
    /// </param>
    internal static RasterInterpreter.ImageResolver BuildResolver(ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics, long decodedBytesBudget = DefaultDecodedBytesBudget)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // Reference-identity keyed, never static — a fresh cache per Rasterize
        // call, exactly like RasterInterpreter's own DisplayListBudget/RenderFontFactory
        // instances. ObjectRegistry returns the same PdfStream instance for the same indirect
        // reference on every resolution within one call, so identity-keying is sufficient to
        // catch a repeated Do of the same XObject without ever comparing dictionary contents.
        var cache = new Dictionary<PdfStream, RasterImageFrame?>(ReferenceEqualityComparer.Instance);
        var cachedBytes = 0L;

        return (imageDictionary, stream) =>
        {
            if (cache.TryGetValue(stream, out var cached))
            {
                return cached;
            }

            var frame = ResolveImage(imageDictionary, stream, objects, options, diagnostics);
            var frameBytes = frame is null ? 0L : (long)frame.Pixels.Length;
            if (cachedBytes + frameBytes <= decodedBytesBudget)
            {
                cache[stream] = frame;
                cachedBytes += frameBytes;
            }

            return frame;
        };
    }

    private static RasterImageFrame? ResolveImage(PdfDictionary dict, PdfStream stream, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        try
        {
            return ResolveCore(dict, stream, objects, options, diagnostics);
        }
        catch (PlumePdfException ex)
        {
            // The one catch-all safety net every other per-image failure funnels through:
            // an unresolvable colorspace (ColorSpace.Parse's own PLUME771x-773x throws,
            // including Indexed-as-Separation-alternate's deliberate PLUME7717), a corrupt
            // JPEG frame this codec can't decode (PLUME32xx), a malformed palette lookup, or
            // an undecodable /SMask or /Mask sub-image. Never lets a RasterImageFrame ctor
            // throw either - every array this file hands the ctor is sized exactly
            // width*height*BytesPerPixel(format) by construction.
            diagnostics.Add(new PdfDiagnostic(
                "PLUME7744",
                DiagnosticSeverity.Warning,
                $"Image XObject could not be decoded ({ex.Code}: {ex.Message}); painting nothing for this image.",
                subject: null));
            return null;
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException)
        {
            // Same "never crash the page on untrusted input" net as the PlumePdfException catch
            // above, for the handful of internal byte-array-length/Span-slice computations still
            // done from document-supplied Width/Height/BitsPerComponent/component-count values
            // (UnpackSamples' bytesPerRow, BuildFrame's rgba buffer): a cap-legal-but-extreme
            // dimension pair reaches here as a BCL exception rather than a PLUME#### one. Same
            // degrade-this-one-image posture, no new diagnostic code - PLUME7744 already covers
            // "this image could not be decoded" generically.
            diagnostics.Add(new PdfDiagnostic(
                "PLUME7744",
                DiagnosticSeverity.Warning,
                $"Image XObject could not be decoded ({ex.GetType().Name}: {ex.Message}); painting nothing for this image.",
                subject: null));
            return null;
        }
    }

    private static RasterImageFrame? ResolveCore(PdfDictionary dict, PdfStream stream, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        if (!TryGetPositiveInt(dict, WidthName, objects, out var width) || !TryGetPositiveInt(dict, HeightName, objects, out var height))
        {
            Report7746(diagnostics, "missing or non-positive /Width or /Height");
            return null;
        }

        var pixelCount = (long)width * height;
        if (pixelCount > options.MaxImagePixels)
        {
            Report7746(diagnostics, $"declared {width}x{height} ({pixelCount} pixels) exceeds PdfOptions.MaxImagePixels ({options.MaxImagePixels})");
            return null;
        }

        var isImageMask = Resolve(dict, ImageMaskName, objects) is PdfBoolean { Value: true };
        var filterChain = ReadFilterChain(dict, objects);
        var isJpx = filterChain.Count > 0 && filterChain[^1] == "JPXDecode";

        if (isJpx && isImageMask)
        {
            // ISO 32000-1 §7.4.9 does not define a JPEG 2000-encoded stencil mask - a
            // stencil's samples select paint/skip per §8.9.6.2, a meaning JPEG 2000's own
            // wavelet/entropy-coded planes have no way to carry. Refused as a dictionary illegality
            // (PLUME7746) before either decode pipeline below ever runs, rather than
            // decoding padded garbage into a stencil.
            Report7746(diagnostics, "/ImageMask true is illegal on an image whose terminal filter is /JPXDecode (ISO 32000-1 §7.4.9)");
            return null;
        }

        // A caller-registered JPXDecode IPdfFilter (anything other than the built-in
        // JpxFilterAdapter PdfFilterRegistry.Default registers) takes precedence over the direct
        // unwrap below - that image instead falls through to the generic registry branch, same
        // as every other filter a caller can override. TryGetDecoder returning false (a custom
        // registry that never registered ANY JPXDecode entry) short-circuits this to false too -
        // unlike every other filter, omitting a JPXDecode registration does not disable JPX
        // support, it just leaves the built-in direct decode below in place. A caller who truly
        // wants JPX left alone must register an explicit refusing IPdfFilter for JPXDecode
        // (documented in the cookbook), not merely leave the name unregistered.
        var isJpxOverridden = isJpx && HasJpxOverride(options.Filters);
        var isJpxDirect = isJpx && !isJpxOverridden;
        var isDct = !isImageMask && filterChain.Count > 0 && filterChain[^1] is "DCTDecode" or "DCT";

        byte[] baseBytes;
        RasterPixelFormat baseFormat;
        int outWidth;
        int outHeight;
        byte[]? colorKeyAlpha = null;
        byte[]? jpxSmaskInDataAlpha = null;

        if (isDct)
        {
            var jpegBytes = filterChain.Count == 1
                ? stream.RawBytes.ToArray()
                : ImageExtractor.DecodePrefixFilters(dict, stream.RawBytes, filterChain.Count - 1, options, diagnostics, reference: null, objects);

            // Honor the terminal DCT stage's /DecodeParms /ColorTransform exactly as the
            // registry path (DctFilterAdapter) this branch bypasses does: a document may
            // explicitly override the file's own APP14 transform, and the two decode paths
            // this type unifies must never disagree on it.
            var colorTransformOverride = DctFilterAdapter.ReadColorTransform(ReadTerminalDecodeParms(dict, objects, filterChain.Count));
            var dctDiagnosticCountBeforeDecode = diagnostics.Count;
            var jpegResult = JpegDecoder.Decode(jpegBytes, options, diagnostics, subject: null, colorTransformOverride);
            MarkDegradedIfDiagnosed(diagnostics, dctDiagnosticCountBeforeDecode);
            outWidth = jpegResult.Width;
            outHeight = jpegResult.Height;
            if ((long)outWidth * outHeight > options.MaxImagePixels)
            {
                Report7746(diagnostics, $"decoded JPEG {outWidth}x{outHeight} exceeds PdfOptions.MaxImagePixels ({options.MaxImagePixels})");
                return null;
            }

            // Color-key /Mask ranges apply to the decoded source samples — for a DCT image, the
            // 8-bit component values straight out of the JPEG decoder, BEFORE any /Decode-driven
            // inversion or colorspace conversion (ISO 32000-1 §8.9.6.4) — matching the non-DCT
            // branch, which also honors color-key masks.
            if (Resolve(dict, MaskEntryName, objects) is PdfArray dctColorKeyArray
                && TryReadColorKeyRanges(dctColorKeyArray, objects, jpegResult.ComponentCount) is { } dctRanges)
            {
                colorKeyAlpha = ComputeColorKeyAlphaFromBytes(jpegResult.Pixels, outWidth * outHeight, jpegResult.ComponentCount, dctRanges);
            }

            switch (jpegResult.ComponentCount)
            {
                case 1:
                    // A 1-component JPEG's declared
                    // /ColorSpace may be a TINT, not gray — /Separation /Black maps sample 0 =
                    // no ink = paper-white, the exact inverse of DeviceGray's 0 = black — so
                    // treating every single-channel JPEG as DeviceGray photographically
                    // inverted the whole scan. Route non-gray 1-component spaces (Separation,
                    // DeviceN, CalGray, ICCBased-gray) through the same 256-entry
                    // tint-transform LUT the non-DCT path already uses; plain
                    // DeviceGray (and anything unparseable) keeps the fast path.
                    if (TryResolveDct1ComponentColorSpace(dict, objects, options, diagnostics) is { } dctTintSpace)
                    {
                        if (dctTintSpace is SeparationColorSpace { IsNone: true })
                        {
                            return null; // §8.6.6.4: /Separation /None paints nothing, no diagnostic.
                        }

                        var lut = Build1ComponentLut(dctTintSpace);
                        var tintRgb = new byte[(long)outWidth * outHeight * 3];
                        for (var i = 0; i < outWidth * outHeight; i++)
                        {
                            var (r, g, b) = lut[jpegResult.Pixels[i]];
                            tintRgb[i * 3] = r;
                            tintRgb[(i * 3) + 1] = g;
                            tintRgb[(i * 3) + 2] = b;
                        }

                        baseBytes = tintRgb;
                        baseFormat = RasterPixelFormat.Rgb24;
                    }
                    else
                    {
                        baseBytes = jpegResult.Pixels;
                        baseFormat = RasterPixelFormat.Gray8;
                    }

                    break;
                case 3:
                    baseBytes = jpegResult.Pixels;
                    baseFormat = RasterPixelFormat.Rgb24;
                    break;
                case 4:
                    // ISO 32000-1 §8.9.5.2: sample->colour polarity is governed by /Decode, for a
                    // DCT-decoded CMYK image exactly like every other filter - never the JPEG
                    // APP14 marker, which is a raw-standalone-JPEG storage convention with no PDF
                    // /Decode array to consult. Both the PDFium and poppler oracles ignore APP14
                    // here (measured against dct-cmyk-app14.pdf, which carries no /Decode and is
                    // NOT inverted by either). A document whose CMYK JPEG really is stored
                    // Photoshop-inverted says so explicitly via /Decode [1 0 1 0 1 0 1 0]; that is
                    // now what drives the inversion decision instead of guessing from APP14
                    // presence, so a document that supplies both (APP14 + /Decode) is no longer
                    // double-inverted either.
                    if (!TryReadDecodeArray(dict, objects, 4, diagnostics, out var explicitCmykDecode))
                    {
                        return null; // TryReadDecodeArray already recorded PLUME7746.
                    }

                    var invertCmyk = explicitCmykDecode is not null && explicitCmykDecode[0] > explicitCmykDecode[1];

                    // Measured: converting via JpegColorTransforms' naive §8.6.5.3 formula scored
                    // SSIM 0.8514 vs PDFium on dct-cmyk-app14.pdf, while routing through
                    // DeviceCmyk.ToSrgb — the PDFium-matched byte LUT the non-DCT CMYK path below
                    // and ImagePainter.StencilToRgb already use — scores 1.0000. One CMYK→RGB
                    // rendering path, everywhere in Raster-facing code; the naive helper remains
                    // RasterImage.Decode's own decode-facade contract (Phase 7, locked
                    // byte-for-byte by JpegColorTransformsTests), deliberately distinct.
                    baseBytes = ConvertCmykViaRenderLut(jpegResult.Pixels, outWidth, outHeight, invertCmyk);
                    baseFormat = RasterPixelFormat.Rgb24;
                    break;
                default:
                    throw new PlumePdfException("PLUME3204", $"JPEG frame declares {jpegResult.ComponentCount} components - only 1, 3, or 4 are supported.");
            }
        }
        else if (isJpxDirect)
        {
            // Direct unwrap mirroring the DCT branch's shape - a
            // prefix filter chain (e.g. [/ASCII85Decode /JPXDecode]) is unwrapped exactly like
            // the DCT branch's own prefix stage (the /JBIG2Globals lesson: a sub-stream
            // reaching a codec must go through its own filter chain first).
            var jpxSourceBytes = filterChain.Count == 1
                ? stream.RawBytes.ToArray()
                : ImageExtractor.DecodePrefixFilters(dict, stream.RawBytes, filterChain.Count - 1, options, diagnostics, reference: null, objects);

            var jpxDiagnosticCountBeforeDecode = diagnostics.Count;
            var jpxImage = JpxImageDecoder.Decode(jpxSourceBytes, options, diagnostics);
            MarkDegradedIfDiagnosed(diagnostics, jpxDiagnosticCountBeforeDecode);

            outWidth = jpxImage.Width;
            outHeight = jpxImage.Height;
            if ((long)outWidth * outHeight > options.MaxImagePixels)
            {
                Report7746(diagnostics, $"decoded JPEG 2000 image {outWidth}x{outHeight} exceeds PdfOptions.MaxImagePixels ({options.MaxImagePixels})");
                return null;
            }

            if (outWidth != width || outHeight != height)
            {
                // Tolerated, not refused (Info, not Warning) - the codestream's own SIZ geometry
                // is authoritative for what actually decoded; the dictionary's /Width//Height are
                // ignored for JPX exactly as /BitsPerComponent and /Decode are (below).
                diagnostics.Add(new PdfDiagnostic(
                    "PLUME7746",
                    DiagnosticSeverity.Info,
                    $"Image XObject declares {width}x{height} but its JPEG 2000 codestream is {outWidth}x{outHeight}; painting at the codestream's own dimensions.",
                    subject: null));
            }

            var colourPlanes = JpxColourPlaneIndices(jpxImage);
            var jpxColourCount = colourPlanes.Length;
            if (jpxColourCount == 0)
            {
                // Every component is the cdef opacity channel: there is no colour to paint, whatever
                // /ColorSpace claims. Checked BEFORE the /ColorSpace branch below — a declared
                // /DeviceGray used to reach ExtractFirstChannel over an empty buffer (a bare
                // IndexOutOfRangeException escaping PdfPage.Rasterize), and a declared /DeviceRGB
                // painted an opaque black rectangle over "the first 0 channels". Only the
                // no-/ColorSpace path reported this correctly before.
                Report7746(diagnostics, "JPEG 2000 codestream has no colour channels (every component is cdef opacity)");
                return null;
            }

            // Colour-key /Mask ranges (§8.9.6.4) apply to the JPX image's own raw, unsigned
            // source samples at the codestream's native precision - never the full-scale-rescaled
            // 8-bit values below, and never /BitsPerComponent (ignored unconditionally for JPX).
            if (Resolve(dict, MaskEntryName, objects) is PdfArray jpxColorKeyArray
                && TryReadColorKeyRanges(jpxColorKeyArray, objects, jpxColourCount) is { } jpxColorKeyRanges)
            {
                colorKeyAlpha = ComputeJpxColorKeyAlpha(jpxImage, colourPlanes, jpxColorKeyRanges);
            }

            (byte R, byte G, byte B)[]? jpxPalette = null;
            RasterColorSpace? jpxColorSpace = null;

            // /Decode is ignored unconditionally for JPX (the full-scale rescale already IS
            // the sample-to-[0,1] mapping; there is no bit-depth-relative range left for a
            // /Decode array to remap) - only /ColorSpace (device-space selection / /Indexed
            // palette / tint transform) is still consulted.
            if (dict.TryGetValue(ColorSpaceName, out var jpxCsRaw))
            {
                var jpxCsResolved = PageSpace.Resolve(jpxCsRaw, objects)!;
                if (TryParseIndexed(jpxCsResolved, objects, options, out var jpxBaseSpaceObject, out var jpxHival, out var jpxLookupBytes))
                {
                    var jpxBaseSpace = ColorSpace.Parse(jpxBaseSpaceObject!, r => objects[r], options.Filters, options, diagnostics);
                    jpxPalette = BuildPalette(jpxBaseSpace, jpxHival, jpxLookupBytes!);
                }
                else
                {
                    jpxColorSpace = ColorSpace.Parse(jpxCsResolved, r => objects[r], options.Filters, options, diagnostics);
                    if (jpxColorSpace is SeparationColorSpace { IsNone: true })
                    {
                        return null; // §8.6.6.4: /Separation /None paints nothing, no diagnostic.
                    }

                    if (jpxColorSpace.ComponentCount is <= 0 or > MaxComponentCount)
                    {
                        Report7746(diagnostics, $"unsupported /ColorSpace component count ({jpxColorSpace.ComponentCount}) for a JPEG 2000 image");
                        return null;
                    }

                    if (jpxColorSpace.ComponentCount != jpxColourCount)
                    {
                        // PlumePDF's own rule (no oracle corroborates a mismatched
                        // /ColorSpace) - the declared space wins, over as many of the codestream's
                        // own colour channels as it needs; a shortfall reads as 0, a surplus is
                        // dropped (ResolveJpxDeviceColour applies the same min()).
                        diagnostics.Add(new PdfDiagnostic(
                            "PLUME7753",
                            DiagnosticSeverity.Warning,
                            $"/ColorSpace declares {jpxColorSpace.ComponentCount} component(s) but the JPEG 2000 codestream has {jpxColourCount} colour channel(s); using the first {Math.Min(jpxColorSpace.ComponentCount, jpxColourCount)}.",
                            subject: null));
                    }
                }
            }

            if (jpxPalette is not null)
            {
                // /Indexed over JPX — UnpackSamples' palette lookup is bypassed by this branch
                // by construction: the index IS the (only) colour channel's raw unsigned sample
                // at the codestream's own precision.
                var indexPlane = colourPlanes[0]; // jpxColourCount > 0 was established above.
                var jpxPixelCount = (long)outWidth * outHeight;
                var indexed = new byte[jpxPixelCount * 3];
                for (var i = 0; i < jpxPixelCount; i++)
                {
                    var index = Math.Clamp(jpxImage.SampleUnsigned(indexPlane, (int)i), 0, jpxPalette.Length - 1);
                    var (r, g, b) = jpxPalette[index];
                    indexed[(i * 3) + 0] = r;
                    indexed[(i * 3) + 1] = g;
                    indexed[(i * 3) + 2] = b;
                }

                baseBytes = indexed;
                baseFormat = RasterPixelFormat.Rgb24;
            }
            else
            {
                // Every JPX sample this resolver paints is the full-scale rescale to 8
                // bits - JpxImage.ToInterleaved8Bit (JpxTypes.cs, frozen) is the one shared
                // implementation every consumer (this resolver, JpxFilterAdapter, RasterImage)
                // reuses, so none of them can disagree on the rescale rule.
                var colourBytes = jpxImage.ToInterleaved8Bit(dropAlpha: true);
                (baseBytes, baseFormat) = ResolveJpxDeviceColour(colourBytes, jpxColourCount, outWidth, outHeight, jpxColorSpace);
                if (baseBytes.Length == 0)
                {
                    Report7746(diagnostics, $"unsupported JPEG 2000 colour channel count ({jpxColourCount}) with no usable /ColorSpace override");
                    return null;
                }
            }

            // /SMaskInData (ISO 32000-1 Table 89, a JPXDecode-only entry): 0 (default) -
            // any wrapper opacity channel is just this image's own data, not a soft mask; 1 - the
            // cdef opacity channel IS this image's soft mask; 2 - as 1, but the colour channels
            // were stored premultiplied by it and must be un-premultiplied first.
            if (TryGetPositiveInt(dict, SMaskInDataName, objects, out var smaskInData) && smaskInData is 1 or 2)
            {
                if (jpxImage.Colour.AlphaChannelIndex is { } jpxAlphaPlaneIndex)
                {
                    jpxSmaskInDataAlpha = ExtractJpxPlane8Bit(jpxImage, jpxAlphaPlaneIndex);
                    if (smaskInData == 2)
                    {
                        UnpremultiplyJpxColour(baseBytes, baseFormat == RasterPixelFormat.Gray8 ? 1 : 3, jpxSmaskInDataAlpha);
                    }
                }
                else
                {
                    // Declaring /SMaskInData without a cdef
                    // opacity channel to actually use as one is a dictionary/codestream
                    // disagreement, not a legal "no soft mask" shape - record it (Info, matching
                    // this method's own byte-count-divergence posture just above) rather than
                    // silently painting the image fully opaque with no trace of the mismatch.
                    diagnostics.Add(new PdfDiagnostic(
                        "PLUME7746",
                        DiagnosticSeverity.Info,
                        $"Image XObject declares /SMaskInData {smaskInData} but its JPEG 2000 codestream has no cdef opacity channel to use as the soft mask; painting fully opaque.",
                        subject: null));
                }
            }
        }
        else
        {
            if (!TryResolveBpc(dict, objects, isImageMask, out var bpc))
            {
                Report7746(diagnostics, "missing or unsupported /BitsPerComponent (must be 1, 2, 4, 8, or 16)");
                return null;
            }

            RasterColorSpace? colorSpace = null;
            (byte R, byte G, byte B)[]? palette = null;
            var componentCount = 1;

            if (!isImageMask)
            {
                if (!dict.TryGetValue(ColorSpaceName, out var csRaw))
                {
                    Report7746(diagnostics, "missing /ColorSpace");
                    return null;
                }

                var csResolved = PageSpace.Resolve(csRaw, objects)!;
                if (TryParseIndexed(csResolved, objects, options, out var baseSpaceObject, out var hival, out var lookupBytes))
                {
                    var baseSpace = ColorSpace.Parse(baseSpaceObject!, r => objects[r], options.Filters, options, diagnostics);
                    palette = BuildPalette(baseSpace, hival, lookupBytes!);
                    componentCount = 1;
                }
                else
                {
                    colorSpace = ColorSpace.Parse(csResolved, r => objects[r], options.Filters, options, diagnostics);
                    if (colorSpace is SeparationColorSpace { IsNone: true })
                    {
                        return null; // §8.6.6.4: /Separation /None paints nothing, no diagnostic.
                    }

                    componentCount = colorSpace.ComponentCount;
                }
            }

            if (componentCount is <= 0 or > MaxComponentCount || pixelCount * componentCount > options.MaxImagePixels * 4L)
            {
                Report7746(diagnostics, $"unsupported component count ({componentCount}) for a {width}x{height} image");
                return null;
            }

            if (!TryReadDecodeArray(dict, objects, componentCount, diagnostics, out var explicitDecode))
            {
                return null; // TryReadDecodeArray already recorded the PLUME7746 diagnostic.
            }

            if (isImageMask)
            {
                explicitDecode = NormaliseStencilDecode(explicitDecode, diagnostics);
            }

            var decodeRange = explicitDecode ?? DefaultDecodeRange(componentCount, palette is not null, colorSpace, isImageMask, (bpc == 16 ? 65535 : (1 << bpc) - 1));

            byte[] rawDecoded;
            try
            {
                // Threaded directly through PdfFilterRegistry.Decode (ImageExtractor's own
                // precedent, ImageExtractor.cs) rather than the diagnostics-less
                // PdfStream.GetDecodedBytes convenience overload: a codec that degrades
                // in-place without throwing (e.g. Jbig2Decoder's PLUME3550/3554/3557
                // per-segment fallbacks - skip an unsupported segment, keep decoding) must
                // still surface something to this image's own Diagnostics, or the deviation
                // is silently swallowed even though the pixels it would have painted are
                // genuinely missing.
                var diagnosticCountBeforeDecode = diagnostics.Count;
                rawDecoded = options.Filters.Decode(WithCcittRowsBound(dict, objects, height, isCcittTerminal: filterChain.Count > 0 && filterChain[^1] is "CCITTFaxDecode" or "CCF", filterChain.Count), stream.RawBytes.Span, options, diagnostics, subject: null, resolver: r => objects[r]);
                MarkDegradedIfDiagnosed(diagnostics, diagnosticCountBeforeDecode);
            }
            catch (PlumePdfException ex)
            {
                diagnostics.Add(new PdfDiagnostic(
                    "PLUME7744",
                    DiagnosticSeverity.Warning,
                    $"Image XObject could not be decoded ({ex.Code}: {ex.Message}); painting nothing for this image.",
                    subject: null));
                return null;
            }

            var bytesPerRow = ((long)width * componentCount * bpc + 7) / 8;
            var expectedLength = bytesPerRow * height;
            if (rawDecoded.LongLength != expectedLength)
            {
                // Byte-count divergence between the decoded data and the declared geometry:
                // truncate the extra, or pad the missing tail toward the /Decode range's LIGHT
                // end, and keep painting. This was originally the CCITT-only posture
                // (decode-to-exhaustion, padding white), while every other filter
                // REFUSED the whole image - but PlumePDF's own filters are deliberately lenient
                // (FlateFilter returns partial output for a truncated stream, PLUME3003), and
                // both PDFium and poppler paint the partial image. Refusal turned any dark page
                // whose primary image arrived truncated into a solid-black render (a scanned
                // radiology-style page would paint a black canvas under the scan). Padding toward the
                // light end mirrors the CCITT paper-white rule: whichever sample the effective
                // /Decode maps to the lighter value, so inverted /Decode arrays pad to their own
                // notion of light.
                diagnostics.Add(new PdfDiagnostic(
                    "PLUME7746",
                    DiagnosticSeverity.Info,
                    $"Image decoded {rawDecoded.LongLength} of the {expectedLength} bytes the declared {width}x{height} at {bpc}bpc/{componentCount} components needs; extra data truncated / missing data padded toward the /Decode range's light end.",
                    subject: null));
                var padByte = decodeRange.Length >= 2 && decodeRange[1] >= decodeRange[0] ? (byte)0xFF : (byte)0x00;
                var adjusted = new byte[expectedLength];
                var copied = Math.Min(rawDecoded.LongLength, expectedLength);
                Array.Copy(rawDecoded, adjusted, copied);
                if (copied < expectedLength)
                {
                    Array.Fill(adjusted, padByte, (int)copied, (int)(expectedLength - copied));
                }

                rawDecoded = adjusted;
            }

            int[]? colorKeyRanges = null;
            if (!isImageMask && Resolve(dict, MaskEntryName, objects) is PdfArray colorKeyArray)
            {
                // For an /Indexed image the color-key range values are INDEX values
                // (ISO 32000-1 §8.9.6.4), and UnpackSamples' masked test already compares the
                // raw pre-Decode samples — which for indexed IS the index — so the same
                // one-component range test covers both plain and palette images; excluding the
                // palette case here would silently drop color-keyed /Indexed masks.
                colorKeyRanges = TryReadColorKeyRanges(colorKeyArray, objects, componentCount);
            }

            (baseBytes, baseFormat, colorKeyAlpha) = UnpackSamples(rawDecoded, width, height, bpc, componentCount, decodeRange, isImageMask, colorSpace, palette, colorKeyRanges);
            outWidth = width;
            outHeight = height;
        }

        // /SMask (or the stencil-stream form of /Mask) takes precedence when present and
        // resolvable; a JPX image's own /SMaskInData channel is the next fallback, but ONLY when
        // /SMask is genuinely absent (smaskEntryPresent guards against a fallback-on-
        // failure bug: a present-but-broken /SMask must stay opaque, never fall through); a
        // color-key /Mask is the last resort.
        var alpha = ComputeAlpha(dict, objects, options, diagnostics, outWidth, outHeight, isImageMask, out var smaskEntryPresent)
            ?? (smaskEntryPresent ? null : jpxSmaskInDataAlpha)
            ?? colorKeyAlpha;
        return BuildFrame(baseBytes, baseFormat, outWidth, outHeight, alpha);
    }

    private static RasterImageFrame BuildFrame(byte[] baseBytes, RasterPixelFormat baseFormat, int width, int height, byte[]? alpha)
    {
        if (alpha is null)
        {
            return new RasterImageFrame(baseBytes, width, height, baseFormat);
        }

        // long throughout (defense-in-depth): width*height as a plain int product can overflow
        // to a negative array length for a document-supplied dimension pair once
        // PdfOptions.MaxImagePixels is raised above ~536M - see this method's own remarks.
        var pixelCount = (long)width * height;
        var rgba = new byte[pixelCount * 4];
        if (baseFormat == RasterPixelFormat.Gray8)
        {
            for (var i = 0L; i < pixelCount; i++)
            {
                var g = baseBytes[i];
                rgba[i * 4] = g;
                rgba[(i * 4) + 1] = g;
                rgba[(i * 4) + 2] = g;
                rgba[(i * 4) + 3] = alpha[i];
            }
        }
        else
        {
            for (var i = 0L; i < pixelCount; i++)
            {
                rgba[i * 4] = baseBytes[i * 3];
                rgba[(i * 4) + 1] = baseBytes[(i * 3) + 1];
                rgba[(i * 4) + 2] = baseBytes[(i * 3) + 2];
                rgba[(i * 4) + 3] = alpha[i];
            }
        }

        return new RasterImageFrame(rgba, width, height, RasterPixelFormat.Rgba32);
    }

    // -- /SMask, color-key /Mask, and stencil-stream /Mask -----------------------------------

    // smaskEntryPresent (an absent-vs-unresolvable distinction): true when /SMask is a
    // genuine, non-/None entry, whether or not it actually resolved to a usable image. A caller
    // with its own alpha fallback (a JPX image's /SMaskInData channel) must consult it ONLY when
    // this is false - a present-but-unresolvable /SMask keeps its own "stays opaque, PLUME7744"
    // behaviour below and must never fall through to a different alpha source, the exact
    // fallback-on-"lookup failed" (instead of "key absent") bug this guards against for a different
    // alpha path.
    private static byte[]? ComputeAlpha(PdfDictionary dict, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics, int outWidth, int outHeight, bool isImageMask, out bool smaskEntryPresent)
    {
        smaskEntryPresent = false;
        if (isImageMask)
        {
            return null; // A stencil mask's own samples ARE the paint/skip signal; §8.9.6 defines no SMask/Mask on an ImageMask.
        }

        // §11.6.4.3: /SMask takes precedence over /Mask when a document carries both.
        if (dict.TryGetValue(SMaskEntryName, out var smaskRaw) && smaskRaw is not PdfNull
            && (smaskRaw is not PdfName smaskName || smaskName.Value != "None"))
        {
            smaskEntryPresent = true;
            if (Resolve(dict, SMaskEntryName, objects) is PdfStream smaskStream)
            {
                if (DecodeGraySubImage(smaskStream, objects, options, diagnostics) is var (samples, w, h))
                {
                    return ResampleGray(samples, w, h, outWidth, outHeight);
                }

                // The sub-image resolved to a stream but carries no usable soft-mask samples (a
                // JPEG 2000 codestream whose every component is cdef opacity - PLUME7746 recorded
                // by DecodeJpxGraySubImage): the same present-but-unresolvable posture as the
                // non-stream branch below - alpha unavailable, paint opaque, record it.
                diagnostics.Add(new PdfDiagnostic(
                    "PLUME7744",
                    DiagnosticSeverity.Warning,
                    "Image carries an /SMask entry whose JPEG 2000 codestream has no colour channel to use as the soft mask (recorded above); the soft-mask alpha was ignored and the image painted fully opaque.",
                    subject: null));
                return null;
            }

            // An /SMask entry that is PRESENT but does not resolve to an image
            // stream (a dangling/reused object number, an xref quirk resolving it to null or
            // a non-stream) used to be ignored SILENTLY — the base image then paints fully
            // opaque, which for a dark scan is a solid-black page with zero diagnostics, the
            // exact triage-hostile shape the issue reports. The alpha is still unavailable
            // (paint opaque, matching what a reader that can't load the mask shows), but the
            // deviation is now recorded.
            diagnostics.Add(new PdfDiagnostic(
                "PLUME7744",
                DiagnosticSeverity.Warning,
                "Image carries an /SMask entry that did not resolve to an image stream; the soft-mask alpha was ignored and the image painted fully opaque.",
                subject: null));
            return null;
        }

        if (Resolve(dict, MaskEntryName, objects) is PdfStream maskStream)
        {
            if (DecodeGraySubImage(maskStream, objects, options, diagnostics) is not var (samples, w, h))
            {
                diagnostics.Add(new PdfDiagnostic(
                    "PLUME7744",
                    DiagnosticSeverity.Warning,
                    "Image carries a /Mask stream whose JPEG 2000 codestream has no colour channel to use as the mask (recorded above); the mask was ignored and the image painted fully opaque.",
                    subject: null));
                return null;
            }

            var alpha = new byte[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                // §8.9.6.4: sample 1 (decoded near 255) masks the pixel OUT; sample 0 leaves it
                // painted - the inverse convention of the direct SMask alpha channel above.
                alpha[i] = (byte)(255 - samples[i]);
            }

            return ResampleGray(alpha, w, h, outWidth, outHeight);
        }

        return null; // A color-key (array) /Mask, if present, was already applied inline during the base unpack pass.
    }

    /// <summary>
    /// Decodes one <c>/SMask</c> or stencil-stream <c>/Mask</c> sub-image to raw, un-inverted
    /// Gray8 samples (§11.6.5.3/§8.9.6.4's DCTDecode-included generic filter path — no
    /// direct-JPEG CMYK special case is needed here since a single-component source carries no
    /// CMYK/YCCK ambiguity). A non-stencil sub-image whose terminal filter is a non-overridden
    /// <c>/JPXDecode</c> instead takes <see cref="DecodeJpxGraySubImage"/>'s direct path — the
    /// same one the base image takes — because §7.4.9 makes <c>/BitsPerComponent</c> optional
    /// for JPEG 2000 and the generic path's mandatory-bpc check would otherwise refuse a legal,
    /// common soft mask. Any decode failure throws, degrading the
    /// WHOLE base image via the caller's own <see cref="PlumePdfException"/> catch (the
    /// "degrades that one image" posture) rather than silently dropping just the alpha
    /// channel; <see langword="null"/> is the one non-throwing "no samples" answer (a JPEG 2000
    /// codestream with zero colour channels, <c>PLUME7746</c> already recorded), which the caller
    /// treats as a present-but-unresolvable mask (paint opaque, <c>PLUME7744</c>).
    /// </summary>
    private static (byte[] Samples, int Width, int Height)? DecodeGraySubImage(PdfStream subStream, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        var subDict = subStream.Dictionary;
        if (!TryGetPositiveInt(subDict, WidthName, objects, out var width) || !TryGetPositiveInt(subDict, HeightName, objects, out var height))
        {
            throw new PlumePdfException("PLUME7746", "/SMask or /Mask sub-image has a missing or non-positive /Width or /Height.");
        }

        if ((long)width * height > options.MaxImagePixels)
        {
            throw new PlumePdfException("PLUME7746", $"/SMask or /Mask sub-image {width}x{height} exceeds PdfOptions.MaxImagePixels ({options.MaxImagePixels}).");
        }

        var isStencil = Resolve(subDict, ImageMaskName, objects) is PdfBoolean { Value: true };
        var subFilterChain = ReadFilterChain(subDict, objects);

        // A JPEG 2000 stencil (/ImageMask true + /JPXDecode) is NOT routed here: this refuses
        // that shape on the base image, and for a sub-image the generic path below refuses it
        // the same way (an 8-bit decode can never match a 1-bpc stencil's byte count - PLUME7746).
        if (!isStencil && subFilterChain.Count > 0 && subFilterChain[^1] == "JPXDecode" && !HasJpxOverride(options.Filters))
        {
            return DecodeJpxGraySubImage(subDict, subStream, subFilterChain, width, height, objects, options, diagnostics);
        }

        if (!TryResolveBpc(subDict, objects, isStencil, out var bpc))
        {
            throw new PlumePdfException("PLUME7746", "/SMask or /Mask sub-image has a missing or unsupported /BitsPerComponent.");
        }

        if (!TryReadDecodeArray(subDict, objects, 1, diagnostics, out var explicitDecode))
        {
            throw new PlumePdfException("PLUME7746", "/SMask or /Mask sub-image has a hostile or malformed /Decode array.");
        }

        if (isStencil)
        {
            explicitDecode = NormaliseStencilDecode(explicitDecode, diagnostics);
        }

        var decodeRange = explicitDecode ?? DefaultDecodeRange(1, isIndexed: false, colorSpace: null, isImageMask: isStencil, maxSampleValue: bpc == 16 ? 65535 : (1 << bpc) - 1);

        // Same diagnostics-preserving shape as the base-image decode: a codec that degrades in
        // place without throwing (the Jbig2Decoder per-segment fallbacks) must surface on the
        // page's diagnostics for a soft/stencil mask exactly as it does for the base image — the
        // diagnostics-less convenience overload would swallow it and ship wrong alpha with no
        // record.
        var subIsCcitt = subFilterChain.Count > 0 && subFilterChain[^1] is "CCITTFaxDecode" or "CCF";
        var rawDecoded = options.Filters.Decode(WithCcittRowsBound(subDict, objects, height, subIsCcitt, subFilterChain.Count), subStream.RawBytes.Span, options, diagnostics, subject: null, resolver: r => objects[r]);

        var bytesPerRow = ((long)width * bpc + 7) / 8;
        var expectedLength = bytesPerRow * height;
        if (rawDecoded.LongLength != expectedLength)
        {
            if (!subIsCcitt)
            {
                throw new PlumePdfException("PLUME7746", $"/SMask or /Mask sub-image decoded {rawDecoded.LongLength} bytes but {width}x{height} at {bpc}bpc needs {expectedLength}.");
            }

            // Same pad-polarity rule as the base image: the missing region pads to
            // the sample the effective /Decode maps HIGHER — fax paper-white, matching how the
            // reference codecs fill missing fax data. Semantic consequence: an /SMask pads
            // opaque; a stencil /Mask pads to sample 1 = masked (§8.9.6.4), so the base image's
            // missing-mask rows hide and the page background shows — benign, and identical to
            // what a reference renderer's own white-padded fax decode produces.
            var subPadByte = decodeRange.Length >= 2 && decodeRange[1] >= decodeRange[0] ? (byte)0xFF : (byte)0x00;
            var adjusted = new byte[expectedLength];
            var copied = Math.Min(rawDecoded.LongLength, expectedLength);
            Array.Copy(rawDecoded, adjusted, copied);
            if (copied < expectedLength)
            {
                Array.Fill(adjusted, subPadByte, (int)copied, (int)(expectedLength - copied));
            }

            rawDecoded = adjusted;
        }

        // isImageMask: true here is not "this is really a /ImageMask stencil" (isStencil above
        // already governs that, for bpc resolution) - it's UnpackSamples' own signal to take the
        // plain single-channel Gray8 output path unconditionally, since a sub-image's decoded
        // samples ARE the alpha source directly and there is no RasterColorSpace to resolve
        // through (colorSpace: null below would otherwise reach the generic ToRgb fallback and
        // null-reference).
        var (samples, _, _) = UnpackSamples(rawDecoded, width, height, bpc, 1, decodeRange, isImageMask: true, colorSpace: null, palette: null, colorKeyRanges: null);
        return (samples, width, height);
    }

    /// <summary>
    /// The JPEG 2000 direct path for a soft-mask (or non-stencil <c>/Mask</c>) sub-image — the
    /// sub-image twin of the base image's <c>isJpxDirect</c> branch: prefix filters unwrapped
    /// first (a sub-stream must go through its own filter chain), <see cref="JpxImageDecoder.Decode"/>, then the FIRST
    /// colour plane full-scale rescaled to 8-bit gray (the same rule
    /// <see cref="JpxImage.ToInterleaved8Bit"/> applies). A soft mask is one channel by
    /// definition, so a codestream with more colour channels records <c>PLUME7753</c> and uses its
    /// first (the base image's own declared-vs-codestream rule); one with none returns
    /// <see langword="null"/> after <c>PLUME7746</c>. The codestream's own dimensions are
    /// authoritative (Info <c>PLUME7746</c> when the dictionary disagrees, as for the base image)
    /// and the caller resamples to the base image as for every other sub-image; the dictionary's
    /// <c>/BitsPerComponent</c> and <c>/Decode</c> are ignored exactly as they are for a JPX base
    /// image (§7.4.9 makes the first optional; the rescale already IS the [0,1] mapping the
    /// second would remap).
    /// </summary>
    private static (byte[] Samples, int Width, int Height)? DecodeJpxGraySubImage(PdfDictionary subDict, PdfStream subStream, List<string> subFilterChain, int declaredWidth, int declaredHeight, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        var sourceBytes = subFilterChain.Count == 1
            ? subStream.RawBytes.ToArray()
            : ImageExtractor.DecodePrefixFilters(subDict, subStream.RawBytes, subFilterChain.Count - 1, options, diagnostics, reference: null, objects);

        var diagnosticCountBeforeDecode = diagnostics.Count;
        var jpxImage = JpxImageDecoder.Decode(sourceBytes, options, diagnostics);
        MarkDegradedIfDiagnosed(diagnostics, diagnosticCountBeforeDecode);

        if ((long)jpxImage.Width * jpxImage.Height > options.MaxImagePixels)
        {
            throw new PlumePdfException("PLUME7746", $"/SMask or /Mask sub-image's JPEG 2000 codestream {jpxImage.Width}x{jpxImage.Height} exceeds PdfOptions.MaxImagePixels ({options.MaxImagePixels}).");
        }

        if (jpxImage.Width != declaredWidth || jpxImage.Height != declaredHeight)
        {
            diagnostics.Add(new PdfDiagnostic(
                "PLUME7746",
                DiagnosticSeverity.Info,
                $"/SMask or /Mask sub-image declares {declaredWidth}x{declaredHeight} but its JPEG 2000 codestream is {jpxImage.Width}x{jpxImage.Height}; using the codestream's own dimensions.",
                subject: null));
        }

        var colourPlanes = JpxColourPlaneIndices(jpxImage);
        if (colourPlanes.Length == 0)
        {
            // Not Report7746: that helper's "painting nothing for this image" tail would be
            // false here - the caller keeps painting the base image, fully opaque.
            diagnostics.Add(new PdfDiagnostic(
                "PLUME7746",
                DiagnosticSeverity.Warning,
                "/SMask or /Mask sub-image's JPEG 2000 codestream has no colour channels (every component is cdef opacity), so it carries no mask samples; the mask is unresolvable.",
                subject: null));
            return null;
        }

        if (colourPlanes.Length > 1)
        {
            diagnostics.Add(new PdfDiagnostic(
                "PLUME7753",
                DiagnosticSeverity.Warning,
                $"/SMask or /Mask sub-image is a single-component mask but its JPEG 2000 codestream has {colourPlanes.Length} colour channel(s); using the first.",
                subject: null));
        }

        return (ExtractJpxPlane8Bit(jpxImage, colourPlanes[0]), jpxImage.Width, jpxImage.Height);
    }

    private static byte[] ResampleGray(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (sourceWidth == targetWidth && sourceHeight == targetHeight)
        {
            return source;
        }

        var result = new byte[(long)targetWidth * targetHeight];
        for (var y = 0; y < targetHeight; y++)
        {
            var sy = Math.Clamp((y * sourceHeight) / targetHeight, 0, sourceHeight - 1);
            for (var x = 0; x < targetWidth; x++)
            {
                var sx = Math.Clamp((x * sourceWidth) / targetWidth, 0, sourceWidth - 1);
                result[(y * targetWidth) + x] = source[(sy * sourceWidth) + sx];
            }
        }

        return result;
    }

    // -- bpc unpack + /Decode + colorspace resolution -----------------------------------------

    /// <summary>
    /// Unpacks <paramref name="raw"/> per <paramref name="bpc"/>, applies
    /// <paramref name="decodeRange"/>, and resolves color: a Gray8 buffer for an
    /// <see cref="RasterInterpreter.ImageResolver"/>-facing stencil mask or a pure single-component
    /// non-mask/non-palette DeviceGray image, otherwise an interleaved Rgb24 buffer. Also
    /// evaluates <paramref name="colorKeyRanges"/> (if any) against each pixel's own raw,
    /// pre-Decode-interpolation samples inline during this same pass (§8.9.6.3).
    /// </summary>
    private static (byte[] Bytes, RasterPixelFormat Format, byte[]? ColorKeyAlpha) UnpackSamples(
        byte[] raw, int width, int height, int bpc, int componentCount, double[] decodeRange,
        bool isImageMask, RasterColorSpace? colorSpace, (byte R, byte G, byte B)[]? palette, int[]? colorKeyRanges)
    {
        var pixelCount = width * height;
        // long intermediate, checked narrowing (defense-in-depth): width/componentCount/bpc are
        // all document-supplied, and this int product can overflow well within
        // PdfOptions.MaxImagePixels's default cap (ResolveCore already computes the equivalent
        // expectedLength in long - see its own bytesPerRow). A silent wrap here would hand
        // AsSpan a garbage or negative length below; checked turns that into an OverflowException
        // instead, caught by ResolveImage's own catch alongside every other per-image failure.
        var bytesPerRow = checked((int)((((long)width * componentCount * bpc) + 7) / 8));
        var maxSampleValue = bpc == 16 ? 65535 : (1 << bpc) - 1;

        var useGray = isImageMask || (palette is null && colorSpace == DeviceGrayColorSpace.Instance && componentCount == 1);
        var useRgbFast = !useGray && palette is null && colorSpace == DeviceRgbColorSpace.Instance && componentCount == 3;
        var useCmyk = !useGray && palette is null && colorSpace == DeviceCmykColorSpace.Instance && componentCount == 4;
        var useIndexed = !useGray && palette is not null;
        (byte R, byte G, byte B)[]? lut1 = null;
        if (!useGray && !useRgbFast && !useCmyk && !useIndexed && componentCount == 1 && colorSpace is not null)
        {
            lut1 = Build1ComponentLut(colorSpace);
        }

        var gray = useGray ? new byte[pixelCount] : null;
        var rgb = useGray ? null : new byte[(long)pixelCount * 3];
        var colorKeyAlpha = colorKeyRanges is null ? null : new byte[pixelCount];

        // Fast path for the dominant scanned-page shape: single-component gray,
        // no color-key mask, bpc <= 8. The per-pixel double interpolate+round is memoized into a
        // per-sample-value table built with the IDENTICAL expression, so output bytes cannot
        // differ; bpc == 1 (the whole CCITT class) additionally expands 8 pixels per source byte
        // through a 256-entry table instead of 8 bit-shift calls.
        if (useGray && colorKeyAlpha is null && componentCount == 1 && bpc <= 8)
        {
            var sampleLut = new byte[maxSampleValue + 1];
            for (var s = 0; s <= maxSampleValue; s++)
            {
                sampleLut[s] = QuantizeByte(decodeRange[0] + (s * (decodeRange[1] - decodeRange[0]) / maxSampleValue));
            }

            UnpackGrayFast(raw, width, height, bpc, bytesPerRow, sampleLut, gray!);
            return (gray!, RasterPixelFormat.Gray8, null);
        }

        var rawComponents = new int[componentCount];
        Span<double> decoded = componentCount <= 8 ? stackalloc double[componentCount] : new double[componentCount];

        for (var y = 0; y < height; y++)
        {
            var rowStart = y * bytesPerRow;
            var row = raw.AsSpan(rowStart, bytesPerRow);
            for (var x = 0; x < width; x++)
            {
                var pixelIndex = (y * width) + x;
                for (var c = 0; c < componentCount; c++)
                {
                    var bitOffset = ((x * componentCount) + c) * bpc;
                    var sample = ReadSample(row, bitOffset, bpc);
                    rawComponents[c] = sample;
                    decoded[c] = decodeRange[c * 2] + (sample * (decodeRange[(c * 2) + 1] - decodeRange[c * 2]) / maxSampleValue);
                }

                if (colorKeyAlpha is not null)
                {
                    var masked = true;
                    for (var c = 0; c < componentCount; c++)
                    {
                        if (rawComponents[c] < colorKeyRanges![c * 2] || rawComponents[c] > colorKeyRanges[(c * 2) + 1])
                        {
                            masked = false;
                            break;
                        }
                    }

                    colorKeyAlpha[pixelIndex] = masked ? (byte)0 : (byte)255;
                }

                if (useGray)
                {
                    gray![pixelIndex] = QuantizeByte(decoded[0]);
                    continue;
                }

                byte r, g, b;
                if (useRgbFast)
                {
                    r = QuantizeByte(decoded[0]);
                    g = QuantizeByte(decoded[1]);
                    b = QuantizeByte(decoded[2]);
                }
                else if (useCmyk)
                {
                    (r, g, b) = DeviceCmyk.ToSrgb(QuantizeByte(decoded[0]), QuantizeByte(decoded[1]), QuantizeByte(decoded[2]), QuantizeByte(decoded[3]));
                }
                else if (useIndexed)
                {
                    var index = Math.Clamp((int)Math.Round(decoded[0]), 0, palette!.Length - 1);
                    (r, g, b) = palette[index];
                }
                else if (lut1 is not null)
                {
                    (r, g, b) = lut1[QuantizeByte(decoded[0])];
                }
                else
                {
                    (r, g, b) = colorSpace!.ToRgb(decoded);
                }

                var o = pixelIndex * 3;
                rgb![o] = r;
                rgb[o + 1] = g;
                rgb[o + 2] = b;
            }
        }

        return useGray
            ? (gray!, RasterPixelFormat.Gray8, colorKeyAlpha)
            : (rgb!, RasterPixelFormat.Rgb24, colorKeyAlpha);
    }

    private static byte QuantizeByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255.0), 0, 255);

    /// <summary>
    /// The single-component-gray unpack loop behind <see cref="UnpackSamples"/>'s fast path:
    /// every raw sample value maps through <paramref name="sampleLut"/> (precomputed with the
    /// same /Decode interpolation the general loop applies per pixel). For 1 bpc, a 256-entry
    /// expansion table turns each source byte into its 8 output pixels in one lookup each.
    /// </summary>
    private static void UnpackGrayFast(byte[] raw, int width, int height, int bpc, int bytesPerRow, byte[] sampleLut, byte[] gray)
    {
        if (bpc == 8)
        {
            for (var y = 0; y < height; y++)
            {
                var row = raw.AsSpan(y * bytesPerRow, width);
                var outRow = gray.AsSpan(y * width, width);
                for (var x = 0; x < width; x++)
                {
                    outRow[x] = sampleLut[row[x]];
                }
            }

            return;
        }

        if (bpc == 1)
        {
            var zero = sampleLut[0];
            var one = sampleLut[1];
            var fullBytes = width / 8;
            for (var y = 0; y < height; y++)
            {
                var row = raw.AsSpan(y * bytesPerRow, bytesPerRow);
                var outRow = gray.AsSpan(y * width, width);
                var o = 0;
                for (var i = 0; i < fullBytes; i++)
                {
                    var bits = row[i];
                    outRow[o] = (bits & 0x80) != 0 ? one : zero;
                    outRow[o + 1] = (bits & 0x40) != 0 ? one : zero;
                    outRow[o + 2] = (bits & 0x20) != 0 ? one : zero;
                    outRow[o + 3] = (bits & 0x10) != 0 ? one : zero;
                    outRow[o + 4] = (bits & 0x08) != 0 ? one : zero;
                    outRow[o + 5] = (bits & 0x04) != 0 ? one : zero;
                    outRow[o + 6] = (bits & 0x02) != 0 ? one : zero;
                    outRow[o + 7] = (bits & 0x01) != 0 ? one : zero;
                    o += 8;
                }

                for (var x = fullBytes * 8; x < width; x++)
                {
                    outRow[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? one : zero;
                }
            }

            return;
        }

        // 2/4 bpc: per-sample bit extraction (same as ReadSample's arithmetic for sub-byte
        // sizes), still through the LUT so the /Decode math stays memoized.
        for (var y = 0; y < height; y++)
        {
            var row = raw.AsSpan(y * bytesPerRow, bytesPerRow);
            var outRow = gray.AsSpan(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var bitOffset = x * bpc;
                var sample = ReadSample(row, bitOffset, bpc);
                outRow[x] = sampleLut[sample];
            }
        }
    }

    /// <summary>
    /// Confirmed across a real-world corpus slice (/Rows
    /// absent on 76% of real CCITT images): when a terminal <c>CCITTFaxDecode</c> stage's
    /// <c>/DecodeParms</c> declares no positive <c>/Rows</c>, the image's <c>/Height</c> IS the
    /// row count — a fax filter left unbounded decodes to exhaustion, walks into trailing
    /// fill/EOFB bytes, and ends on a spurious row error. Returns a shallow clone of
    /// <paramref name="dict"/> whose terminal parms carry <c>/Rows = height</c>; the original
    /// dictionary (and the real document) are never touched. Anything already bounded, or not
    /// terminal-CCITT, passes through unchanged.
    /// </summary>
    private static PdfDictionary WithCcittRowsBound(PdfDictionary dict, ObjectRegistry objects, int height, bool isCcittTerminal, int filterCount)
    {
        if (!isCcittTerminal || height <= 0)
        {
            return dict;
        }

        var terminalParms = ReadTerminalDecodeParms(dict, objects, filterCount);
        if (terminalParms is not null && terminalParms.TryGetValue(PdfName.Get("Rows"), out var rowsValue)
            && PageSpace.Resolve(rowsValue, objects) is PdfNumber { IsInteger: true } rows && rows.Value > 0)
        {
            return dict; // Already bounded — nothing to inject.
        }

        var newParms = new PdfDictionary();
        if (terminalParms is not null)
        {
            foreach (var (key, value) in terminalParms)
            {
                newParms.Set(key, value);
            }
        }

        newParms.Set(PdfName.Get("Rows"), PdfNumber.Get(height));

        var clone = new PdfDictionary();
        foreach (var (key, value) in dict)
        {
            clone.Set(key, value);
        }

        if (filterCount <= 1)
        {
            clone.Set(PdfName.DecodeParms, newParms);
        }
        else
        {
            // Rebuild the parallel /DecodeParms array with only the terminal slot replaced.
            var newArray = new PdfArray();
            var existing = dict.TryGetValue(PdfName.DecodeParms, out var parmsRaw) ? PageSpace.Resolve(parmsRaw, objects) as PdfArray : null;
            for (var i = 0; i < filterCount; i++)
            {
                if (i == filterCount - 1)
                {
                    newArray.Add(newParms);
                }
                else
                {
                    newArray.Add(existing is not null && i < existing.Count ? existing[i] : PdfNull.Instance);
                }
            }

            clone.Set(PdfName.DecodeParms, newArray);
        }

        return clone;
    }

    /// <summary>
    /// The terminal filter stage's <c>/DecodeParms</c> dictionary (or <see langword="null"/>):
    /// the single dictionary form when the chain has one stage, or the last element of the
    /// parallel <c>/DecodeParms</c> array otherwise — the same pairing rule the filter registry
    /// applies (ISO 32000-1 §7.4).
    /// </summary>
    private static PdfDictionary? ReadTerminalDecodeParms(PdfDictionary dict, ObjectRegistry objects, int filterCount)
    {
        if (!dict.TryGetValue(PdfName.DecodeParms, out var parmsRaw) || PageSpace.Resolve(parmsRaw, objects) is not { } parms)
        {
            return null;
        }

        return parms switch
        {
            PdfDictionary single when filterCount <= 1 => single,
            PdfArray array when array.Count >= filterCount && filterCount >= 1 => PageSpace.Resolve(array[filterCount - 1], objects) as PdfDictionary,
            _ => null,
        };
    }

    /// <summary>
    /// Color-key <c>/Mask</c> alpha for an already-byte-decoded (8-bit interleaved) sample
    /// buffer — the DCT path's counterpart of <c>UnpackSamples</c>' in-loop masked test: a pixel
    /// whose every component falls inside its <c>[min, max]</c> range becomes fully transparent.
    /// </summary>
    private static byte[] ComputeColorKeyAlphaFromBytes(byte[] samples, int pixelCount, int componentCount, int[] ranges)
    {
        var alpha = new byte[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var masked = true;
            for (var c = 0; c < componentCount; c++)
            {
                int sample = samples[(i * componentCount) + c];
                if (sample < ranges[c * 2] || sample > ranges[(c * 2) + 1])
                {
                    masked = false;
                    break;
                }
            }

            alpha[i] = masked ? (byte)0 : (byte)255;
        }

        return alpha;
    }

    /// <summary>
    /// CMYK→RGB for the terminal-DCT render path via <see cref="DeviceCmyk.ToSrgb(byte,byte,byte,byte)"/>
    /// — the PDFium-matched byte LUT every other render-side CMYK conversion uses — with the
    /// <paramref name="inverted"/> (/Decode-driven) un-inversion applied first, exactly like the
    /// shared decode-facade helper. Measured vs the armed PDFium oracle: 1.0000 SSIM on
    /// <c>dct-cmyk-app14.pdf</c>, vs 0.8514 through the naive §8.6.5.3 formula.
    /// </summary>
    private static byte[] ConvertCmykViaRenderLut(byte[] cmyk, int width, int height, bool inverted)
    {
        var rgb = new byte[(long)width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            byte c = cmyk[i * 4], m = cmyk[(i * 4) + 1], y = cmyk[(i * 4) + 2], k = cmyk[(i * 4) + 3];
            if (inverted)
            {
                c = (byte)(255 - c);
                m = (byte)(255 - m);
                y = (byte)(255 - y);
                k = (byte)(255 - k);
            }

            var (r, g, b) = DeviceCmyk.ToSrgb(c, m, y, k);
            rgb[i * 3] = r;
            rgb[(i * 3) + 1] = g;
            rgb[(i * 3) + 2] = b;
        }

        return rgb;
    }

    private static int ReadSample(ReadOnlySpan<byte> row, int bitOffset, int bpc)
    {
        if (bpc == 8)
        {
            return row[bitOffset / 8];
        }

        if (bpc == 16)
        {
            var byteOffset = bitOffset / 8;
            return (row[byteOffset] << 8) | row[byteOffset + 1];
        }

        var byteIndex = bitOffset / 8;
        var bitInByte = bitOffset % 8;
        var shift = 8 - bitInByte - bpc;
        var mask = (1 << bpc) - 1;
        return (row[byteIndex] >> shift) & mask;
    }

    /// <summary>
    /// The declared <c>/ColorSpace</c> of a 1-component terminal-DCT image, when — and only
    /// when — it is a parseable single-component space OTHER than plain DeviceGray (a
    /// Separation/DeviceN tint, CalGray, an ICCBased gray with an alternate, …), i.e. exactly
    /// the cases whose sample→color mapping differs from the raw-gray fast path. Returns
    /// <see langword="null"/> for DeviceGray, for an absent/multi-component/unparseable
    /// declaration (the fast path stays byte-identical to the shipped behavior for those), so
    /// this can never turn a previously-rendering image into a refusal.
    /// </summary>
    private static RasterColorSpace? TryResolveDct1ComponentColorSpace(PdfDictionary dict, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        if (!dict.TryGetValue(ColorSpaceName, out var csRaw) || PageSpace.Resolve(csRaw, objects) is not { } csResolved)
        {
            return null;
        }

        try
        {
            var space = ColorSpace.Parse(csResolved, r => objects[r], options.Filters, options, diagnostics);
            return space.ComponentCount == 1 && space != DeviceGrayColorSpace.Instance ? space : null;
        }
        catch (PlumePdfException)
        {
            return null; // Unparseable (e.g. /Indexed) — keep the gray fast path rather than refusing.
        }
    }

    private static (byte R, byte G, byte B)[] Build1ComponentLut(RasterColorSpace colorSpace)
    {
        var lut = new (byte, byte, byte)[256];
        Span<double> one = stackalloc double[1];
        for (var i = 0; i < 256; i++)
        {
            one[0] = i / 255.0;
            lut[i] = colorSpace.ToRgb(one);
        }

        return lut;
    }

    private static double[] DefaultDecodeRange(int componentCount, bool isIndexed, RasterColorSpace? colorSpace, bool isImageMask, int maxSampleValue)
    {
        var range = new double[componentCount * 2];
        if (isIndexed)
        {
            range[0] = 0;
            range[1] = maxSampleValue;
            return range;
        }

        if (!isImageMask && colorSpace is LabColorSpace)
        {
            // §8.9.5.2 Table 90: [0 100 amin amax bmin bmax]. This resolver cannot see the
            // space's own possibly-customized /Range (LabColorSpace keeps it private), so it
            // falls back to the same [-100,100] default LabColorSpace.Parse itself uses when no
            // /Range is declared - correct for the common case, a documented simplification for
            // a Lab image that both customizes /Range AND omits its own /Decode array.
            range[0] = 0;
            range[1] = 100;
            for (var i = 1; i < componentCount; i++)
            {
                range[i * 2] = -100;
                range[(i * 2) + 1] = 100;
            }

            return range;
        }

        for (var i = 0; i < componentCount; i++)
        {
            range[i * 2] = 0;
            range[(i * 2) + 1] = 1;
        }

        return range;
    }

    private static bool TryReadDecodeArray(PdfDictionary dict, ObjectRegistry objects, int componentCount, DiagnosticCollection diagnostics, out double[]? decodeRange)
    {
        decodeRange = null;
        if (!dict.TryGetValue(DecodeName, out var raw))
        {
            return true; // Absent - caller applies the colorspace's own default. Not a deviation.
        }

        if (PageSpace.Resolve(raw, objects) is not PdfArray array || array.Count != componentCount * 2)
        {
            Report7746(diagnostics, $"/Decode array has the wrong shape for {componentCount} component(s)");
            return false;
        }

        var result = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (PageSpace.Resolve(array[i], objects) is not PdfNumber number || !double.IsFinite(number.Value) || Math.Abs(number.Value) > 1_000_000)
            {
                Report7746(diagnostics, "/Decode array has a non-finite or absurd range");
                return false;
            }

            result[i] = number.Value;
        }

        decodeRange = result;
        return true;
    }

    private static int[]? TryReadColorKeyRanges(PdfArray array, ObjectRegistry objects, int componentCount)
    {
        if (array.Count != componentCount * 2)
        {
            return null; // Malformed color-key /Mask - ignored rather than failing the whole image.
        }

        var ranges = new int[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (PageSpace.Resolve(array[i], objects) is not PdfNumber number || !number.TryToInt32(out var value))
            {
                return null;
            }

            ranges[i] = value;
        }

        return ranges;
    }

    // -- /Indexed palette (resolver-local, ColorSpace.Parse unchanged) ----------

    private static bool TryParseIndexed(PdfObject colorSpaceValue, ObjectRegistry objects, PdfOptions options, out PdfObject? baseSpaceObject, out int hival, out byte[]? lookupBytes)
    {
        baseSpaceObject = null;
        hival = 0;
        lookupBytes = null;

        if (colorSpaceValue is not PdfArray array || array.Count < 4 || PageSpace.Resolve(array[0], objects) is not PdfName { Value: "Indexed" })
        {
            return false;
        }

        baseSpaceObject = array[1];
        if (PageSpace.Resolve(array[2], objects) is not PdfNumber hivalNumber || !hivalNumber.TryToInt32(out hival))
        {
            return false;
        }

        hival = Math.Clamp(hival, 0, 255);

        lookupBytes = PageSpace.Resolve(array[3], objects) switch
        {
            PdfString s => s.Bytes.ToArray(),
            PdfStream st => st.GetDecodedBytes(options.Filters, options, r => objects[r]),
            _ => null,
        };

        return lookupBytes is not null;
    }

    private static (byte R, byte G, byte B)[] BuildPalette(RasterColorSpace baseSpace, int hival, byte[] lookupBytes)
    {
        var entries = hival + 1;
        var componentCount = baseSpace.ComponentCount;
        var palette = new (byte, byte, byte)[entries];
        Span<double> components = componentCount <= 8 ? stackalloc double[componentCount] : new double[componentCount];

        for (var i = 0; i < entries; i++)
        {
            for (var c = 0; c < componentCount; c++)
            {
                var offset = (i * componentCount) + c;
                var raw = offset < lookupBytes.Length ? lookupBytes[offset] : (byte)0;
                components[c] = raw / 255.0;
            }

            palette[i] = baseSpace.ToRgb(components);
        }

        return palette;
    }

    // -- small shared helpers ------------------------------------------------------------------

    private static bool TryResolveBpc(PdfDictionary dict, ObjectRegistry objects, bool isImageMask, out int bpc)
    {
        if (isImageMask)
        {
            bpc = 1; // §8.9.6.2: implicit, regardless of any declared /BitsPerComponent.
            return true;
        }

        if (TryGetPositiveInt(dict, BitsPerComponentName, objects, out bpc) && bpc is 1 or 2 or 4 or 8 or 16)
        {
            return true;
        }

        bpc = 0;
        return false;
    }

    private static bool TryGetPositiveInt(PdfDictionary dict, PdfName key, ObjectRegistry objects, out int value)
    {
        value = 0;
        if (!dict.TryGetValue(key, out var raw) || PageSpace.Resolve(raw, objects) is not PdfNumber number || !number.TryToInt32(out var converted) || converted <= 0)
        {
            return false;
        }

        value = converted;
        return true;
    }

    private static PdfObject? Resolve(PdfDictionary dict, PdfName key, ObjectRegistry objects) =>
        dict.TryGetValue(key, out var value) ? PageSpace.Resolve(value, objects) : null;

    private static List<string> ReadFilterChain(PdfDictionary dict, ObjectRegistry objects)
    {
        if (!dict.TryGetValue(FilterName, out var value))
        {
            return [];
        }

        return PageSpace.Resolve(value, objects) switch
        {
            PdfName single => [single.Value],
            PdfArray array => [.. array.Select(item => PageSpace.Resolve(item, objects)).OfType<PdfName>().Select(static n => n.Value)],
            _ => [],
        };
    }

    /// <summary>
    /// Whether <paramref name="filters"/> carries a caller-registered <c>JPXDecode</c>
    /// override (anything other than the built-in <see cref="JpxFilterAdapter"/>). "Nothing
    /// registered" answers <see langword="false"/> — omitting a <c>JPXDecode</c> entry never
    /// disables the built-in direct decode; only an explicit replacement does.
    /// Shared by the base-image and sub-image (soft mask) JPX branches so they can never disagree.
    /// </summary>
    private static bool HasJpxOverride(PdfFilterRegistry filters) =>
        filters.TryGetDecoder("JPXDecode", out var jpxOverrideFilter) && jpxOverrideFilter is not JpxFilterAdapter;

    /// <summary>
    /// ISO 32000-1 §8.9.6.2 allows a stencil mask's <c>/Decode</c> to be only <c>[0 1]</c> or
    /// <c>[1 0]</c>: its samples select paint/skip, nothing in between. Any other array (a
    /// producer writing <c>[0.4 0.6]</c>, say) would unpack to mid-range samples that neither
    /// paint nor skip — and, since a stencil composites by covered fraction, would
    /// paint as flat mid-greys where PDFium renders the malformed file exactly like the conforming
    /// one. Normalised to the default (an Info-severity <c>PLUME7746</c>, the image still paints);
    /// the inverted form is kept as written.
    /// </summary>
    private static double[]? NormaliseStencilDecode(double[]? explicitDecode, DiagnosticCollection diagnostics)
    {
        if (explicitDecode is null || explicitDecode.Length != 2)
        {
            return explicitDecode;
        }

        var (d0, d1) = (explicitDecode[0], explicitDecode[1]);
        if ((d0 == 0 && d1 == 1) || (d0 == 1 && d1 == 0))
        {
            return explicitDecode;
        }

        diagnostics.Add(new PdfDiagnostic(
            "PLUME7746",
            DiagnosticSeverity.Info,
            $"/ImageMask /Decode [{d0} {d1}] is not [0 1] or [1 0] (ISO 32000-1 §8.9.6.2); painting the stencil with the default /Decode.",
            subject: null));
        return null;
    }

    private static void Report7746(DiagnosticCollection diagnostics, string detail) =>
        diagnostics.Add(new PdfDiagnostic(
            "PLUME7746",
            DiagnosticSeverity.Warning,
            $"Image XObject dictionary is adversarial or malformed ({detail}); painting nothing for this image.",
            subject: null));

    // -- Shared "this image decoded but recorded its own recoverable deviation" marker ---

    /// <summary>
    /// Marks THIS image as degraded (<c>PLUME7744</c>) when decoding it recorded at least one
    /// recoverable deviation of its own — a codec's own coded diagnostic (a JBIG2
    /// <c>PLUME3550</c> per-segment skip, a JPEG 2000 <c>PLUME370x</c> truncation) between
    /// <paramref name="countBefore"/> and now — added here so a caller scanning only for
    /// <c>PLUME7744</c> (the documented posture) still sees every degraded-but-painted image, not
    /// only the ones whose decode failed outright. Applied identically to the registry, DCT and
    /// JPX branches (the DCT branch had no such parity before this — a latent gap this closes).
    /// </summary>
    private static void MarkDegradedIfDiagnosed(DiagnosticCollection diagnostics, int countBefore)
    {
        if (diagnostics.Count > countBefore)
        {
            diagnostics.Add(new PdfDiagnostic(
                "PLUME7744",
                DiagnosticSeverity.Warning,
                "Image XObject decoded with one or more unsupported or degraded features recorded above; the painted image may be incomplete.",
                subject: null));
        }
    }

    // -- JPX direct-unwrap helpers --------------------------------------------------------------

    /// <summary>
    /// <paramref name="image"/>'s colour-channel plane indices in ascending order, excluding the
    /// <c>cdef</c> opacity channel if any — the same selection <see cref="JpxImage.ToInterleaved8Bit"/>
    /// makes internally (that method's own selection is private; small enough to duplicate here
    /// rather than widen <c>JpxTypes.cs</c>'s frozen surface for one caller).
    /// </summary>
    private static int[] JpxColourPlaneIndices(JpxImage image)
    {
        if (image.Colour.AlphaChannelIndex is not { } alpha)
        {
            var all = new int[image.Planes.Length];
            for (var i = 0; i < all.Length; i++)
            {
                all[i] = i;
            }

            return all;
        }

        var result = new int[image.Planes.Length - 1];
        var n = 0;
        for (var i = 0; i < image.Planes.Length; i++)
        {
            if (i != alpha)
            {
                result[n++] = i;
            }
        }

        return result;
    }

    /// <summary>
    /// Colour-key <c>/Mask</c> alpha (§8.9.6.4) evaluated against a JPX image's own raw unsigned
    /// source samples — <paramref name="colourPlanes"/> in order, at the codestream's native
    /// precision — never the full-scale-rescaled 8-bit values <see cref="ResolveJpxDeviceColour"/> paints from.
    /// </summary>
    private static byte[] ComputeJpxColorKeyAlpha(JpxImage image, int[] colourPlanes, int[] ranges)
    {
        var pixelCount = image.Width * image.Height;
        var alpha = new byte[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var masked = true;
            for (var c = 0; c < colourPlanes.Length; c++)
            {
                var sample = image.SampleUnsigned(colourPlanes[c], i);
                if (sample < ranges[c * 2] || sample > ranges[(c * 2) + 1])
                {
                    masked = false;
                    break;
                }
            }

            alpha[i] = masked ? (byte)0 : (byte)255;
        }

        return alpha;
    }

    /// <summary>One plane of <paramref name="image"/> at <paramref name="planeIndex"/>, full-scale-rescaled to 8 bits (255 = opaque when the plane is used as alpha, per the same convention <see cref="ComputeAlpha"/>'s direct-/SMask path uses) — the <c>cdef</c> opacity plane for <c>/SMaskInData</c>, or a JPX soft mask's own first colour plane.</summary>
    private static byte[] ExtractJpxPlane8Bit(JpxImage image, int planeIndex)
    {
        var plane = image.Planes[planeIndex];
        var pixelCount = image.Width * image.Height;
        var alpha = new byte[pixelCount];
        var half = plane.Signed ? 1 << (plane.Precision - 1) : 0;
        for (var i = 0; i < pixelCount; i++)
        {
            alpha[i] = (byte)JpxImage.Rescale(plane.Sample(i) + half, plane.MaxValue, 255);
        }

        return alpha;
    }

    /// <summary>
    /// <c>/SMaskInData 2</c>: the colour channels were stored premultiplied by the opacity
    /// channel and must be divided back out. A fully transparent pixel (<paramref name="alpha"/>
    /// sample 0) forces its colour to 0 rather than dividing by zero — the premultiplied encoding
    /// of "fully transparent" already implies colour 0, so this never invents new colour data.
    /// </summary>
    private static void UnpremultiplyJpxColour(byte[] colourBytes, int channelCount, byte[] alpha)
    {
        for (var i = 0; i < alpha.Length; i++)
        {
            var a = alpha[i];
            var baseIndex = (long)i * channelCount;
            if (a == 0)
            {
                for (var c = 0; c < channelCount; c++)
                {
                    colourBytes[baseIndex + c] = 0;
                }

                continue;
            }

            for (var c = 0; c < channelCount; c++)
            {
                var idx = baseIndex + c;
                colourBytes[idx] = (byte)Math.Clamp(((colourBytes[idx] * 255) + (a / 2)) / a, 0, 255);
            }
        }
    }

    /// <summary>
    /// Resolves <paramref name="colourBytes"/> (interleaved, full-scale-rescaled 8-bit, <paramref name="channelCount"/>
    /// channels/pixel, in ascending plane order) to a paintable Gray8/Rgb24 buffer. When
    /// <paramref name="declaredColorSpace"/> is <see langword="null"/> the device space is chosen
    /// purely by <paramref name="channelCount"/>: the count is authoritative, and the JP2 wrapper's
    /// own EnumCS/ICC facts are deliberately NOT consulted here — sYCC (EnumCS 18) has already been
    /// converted to RGB by <c>JpxImageDecoder</c>, and no layer cross-checks a <c>colr</c> box's
    /// implied channel count against the decoded plane count (a JP2 declaring EnumCS 17 over three
    /// planes simply paints as 3-channel RGB, with no diagnostic anywhere; <c>PLUME3715</c> covers
    /// unrecognised/inconsistent colour boxes, not that disagreement). Returns an empty array when
    /// nothing renderable applies (the caller reports <c>PLUME7746</c>).
    /// </summary>
    private static (byte[] Bytes, RasterPixelFormat Format) ResolveJpxDeviceColour(byte[] colourBytes, int channelCount, int width, int height, RasterColorSpace? declaredColorSpace)
    {
        var pixelCount = width * height;

        if (declaredColorSpace is null)
        {
            return channelCount switch
            {
                1 => (colourBytes, RasterPixelFormat.Gray8),
                3 => (colourBytes, RasterPixelFormat.Rgb24),
                4 => (ConvertCmykViaRenderLut(colourBytes, width, height, inverted: false), RasterPixelFormat.Rgb24),
                _ => (Array.Empty<byte>(), RasterPixelFormat.Gray8),
            };
        }

        if (declaredColorSpace == DeviceRgbColorSpace.Instance && channelCount == 3)
        {
            return (colourBytes, RasterPixelFormat.Rgb24);
        }

        if (declaredColorSpace == DeviceCmykColorSpace.Instance && channelCount == 4)
        {
            return (ConvertCmykViaRenderLut(colourBytes, width, height, inverted: false), RasterPixelFormat.Rgb24);
        }

        if (declaredColorSpace == DeviceGrayColorSpace.Instance)
        {
            return (ExtractFirstChannel(colourBytes, channelCount, pixelCount), RasterPixelFormat.Gray8);
        }

        if (declaredColorSpace.ComponentCount == 1)
        {
            // 1-component tint transform (Separation/DeviceN/CalGray/ICCBased-gray) - the same
            // 256-entry LUT the DCT and generic branches already build for this exact shape.
            var lut = Build1ComponentLut(declaredColorSpace);
            var rgb = new byte[(long)pixelCount * 3];
            for (var i = 0; i < pixelCount; i++)
            {
                var sample = channelCount > 0 ? colourBytes[(long)i * channelCount] : (byte)0;
                var (r, g, b) = lut[sample];
                rgb[(i * 3) + 0] = r;
                rgb[(i * 3) + 1] = g;
                rgb[(i * 3) + 2] = b;
            }

            return (rgb, RasterPixelFormat.Rgb24);
        }

        // Generic ≥2-component declared space (CalRGB/Lab/DeviceN/ICCBased/…): per-pixel ToRgb
        // over up to Math.Min(declared, actual) real channels (a mismatch already recorded
        // PLUME7753 above); a shortfall's missing trailing channels read as 0.
        var used = Math.Min(declaredColorSpace.ComponentCount, channelCount);
        var declaredCount = declaredColorSpace.ComponentCount;
        var outRgb = new byte[(long)pixelCount * 3];
        Span<double> components = declaredCount <= 8 ? stackalloc double[declaredCount] : new double[declaredCount];
        for (var i = 0; i < pixelCount; i++)
        {
            components.Clear();
            for (var c = 0; c < used; c++)
            {
                components[c] = colourBytes[((long)i * channelCount) + c] / 255.0;
            }

            var (r, g, b) = declaredColorSpace.ToRgb(components);
            outRgb[(i * 3) + 0] = r;
            outRgb[(i * 3) + 1] = g;
            outRgb[(i * 3) + 2] = b;
        }

        return (outRgb, RasterPixelFormat.Rgb24);
    }

    /// <summary>The first of <paramref name="channelCount"/> interleaved channels, one byte per pixel — identity when there is exactly one channel already.</summary>
    private static byte[] ExtractFirstChannel(byte[] colourBytes, int channelCount, int pixelCount)
    {
        if (channelCount == 1)
        {
            return colourBytes;
        }

        var gray = new byte[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            gray[i] = colourBytes[(long)i * channelCount];
        }

        return gray;
    }
}
