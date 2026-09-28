using PlumePdf.Content;
using PlumePdf.Filters.Jpx;
using PlumePdf.Objects;

namespace PlumePdf.Documents;

/// <summary>
/// Enumerates the image XObjects reachable from a page's <c>/Resources</c> <c>/XObject</c>
/// dictionary (ISO 32000-1 §8.9.5), recursing into Form XObjects (§8.10) so an image nested
/// inside a reusable form is still found. Decodes each image at the same extraction/rasterization
/// boundary (see <see cref="ExtractedImage"/>'s remarks): DCT passes through as intact JPEG bytes; a terminal,
/// non-overridden <c>JPXDecode</c> decodes directly via <see cref="JpxImageDecoder"/> so <see cref="ExtractedImage.Width"/>/<see cref="ExtractedImage.Height"/>/
/// <see cref="ExtractedImage.BitsPerComponent"/>/<see cref="ExtractedImage.ColorSpaceName"/> match
/// what <see cref="ExtractedImage.Data"/> actually carries (the JPEG 2000 codestream's own facts,
/// not the dictionary's, which this decoder never trusts for geometry); Flate/LZW/
/// RunLength/a caller-overridden filter (whichever <see cref="PdfOptions.Filters"/> has a decoder
/// for — <see cref="PdfFilterRegistry.Default"/> unless the caller supplied a custom registry)
/// decode to raw samples against the dictionary's own declared geometry; anything
/// else returns the still-encoded bytes with a diagnostic.
/// Inline images (<c>BI…ID…EI</c>) are never extracted — the content stream is scanned only to
/// record one diagnostic per occurrence, since <see cref="ContentStreamReader"/> already
/// has to skip past them safely for text extraction to survive.
/// </summary>
internal static class ImageExtractor
{
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName XObjectName = PdfName.Get("XObject");
    private static readonly PdfName SubtypeName = PdfName.Subtype;
    private static readonly PdfName WidthName = PdfName.Get("Width");
    private static readonly PdfName HeightName = PdfName.Get("Height");
    private static readonly PdfName ColorSpaceName = PdfName.ColorSpace;
    private static readonly PdfName DecodeName = PdfName.Decode;
    private static readonly PdfName SMaskName = PdfName.SMask;
    private static readonly PdfName ContentsName = PdfName.Get("Contents");

    /// <summary>Extracts every image XObject reachable from <paramref name="page"/>.</summary>
    public static (IReadOnlyList<ExtractedImage> Images, DiagnosticCollection Diagnostics) Extract(PdfPage page, ObjectRegistry objects, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new DiagnosticCollection();
        var images = new List<ExtractedImage>();
        var visited = new HashSet<IndirectReference>();

        var resources = PageSpace.ResolveDictionary(page.Dictionary.TryGetValue(ResourcesName, out var r) ? r : null, objects);
        WalkResources(resources, objects, options, diagnostics, images, visited, depth: 0);

        RecordInlineImageDiagnostics(page.Dictionary, objects, options, diagnostics);

        return (images, diagnostics);
    }

    // The resolver PdfFilterRegistry.Decode/PdfStream.GetDecodedBytes accept so
    // an indirect /Filter or /DecodeParms entry resolves instead of being treated as absent
    // (the retired PLUME3011 degradation). ObjectRegistry's indexer never returns null (a
    // dangling reference degrades to PdfNull.Instance, per its own lenient-by-default
    // contract), so this adapter needs no null handling of its own.
    private static Func<IndirectReference, object?> ResolverFor(ObjectRegistry objects) => reference => objects[reference];

    private static void WalkResources(PdfDictionary? resources, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics, List<ExtractedImage> images, HashSet<IndirectReference> visited, int depth)
    {
        if (resources is null || depth > options.MaxXObjectNestingDepth)
        {
            if (depth > options.MaxXObjectNestingDepth)
            {
                diagnostics.Add(new PdfDiagnostic("PLUME6025", DiagnosticSeverity.Warning, $"Form XObject recursion during image extraction exceeded the configured depth limit ({options.MaxXObjectNestingDepth}); stopping the walk."));
            }

            return;
        }

        var xobjects = PageSpace.ResolveDictionary(resources.TryGetValue(XObjectName, out var xo) ? xo : null, objects);
        if (xobjects is null)
        {
            return;
        }

        foreach (var (_, entry) in xobjects)
        {
            IndirectReference? reference = entry is PdfReference r ? r.Target : null;
            if (reference is IndirectReference id && !visited.Add(id))
            {
                continue;
            }

            if (PageSpace.Resolve(entry, objects) is not PdfStream stream)
            {
                continue;
            }

            var subtype = stream.Dictionary.TryGetValue(SubtypeName, out var st) ? (st as PdfName)?.Value : null;
            if (subtype == "Image")
            {
                images.Add(BuildImage(reference, stream, objects, options, diagnostics));
            }
            else if (subtype == "Form")
            {
                var formResources = PageSpace.ResolveDictionary(stream.Dictionary.TryGetValue(ResourcesName, out var fr) ? fr : null, objects) ?? resources;
                WalkResources(formResources, objects, options, diagnostics, images, visited, depth + 1);
            }
        }
    }

    private static ExtractedImage BuildImage(IndirectReference? reference, PdfStream stream, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        var dict = stream.Dictionary;
        var width = GetInt(dict, WidthName);
        var height = GetInt(dict, HeightName);
        var bpc = GetInt(dict, PdfName.BitsPerComponent);
        var colorSpace = ResolveColorSpaceName(dict.TryGetValue(ColorSpaceName, out var csValue) ? csValue : null, objects);
        var decode = ReadDecodeArray(dict);
        var softMask = dict.TryGetValue(SMaskName, out var smValue) && smValue is PdfReference smRef ? smRef.Target : (IndirectReference?)null;
        var filters = GetFilterChain(dict);

        // JPEG pass-through applies only when DCTDecode is the FINAL filter and every filter
        // before it decodes cleanly: for the common Distiller chains [/ASCII85Decode /DCTDecode]
        // and [/FlateDecode /DCTDecode], RawBytes are ASCII85 text / Flate bytes — returning
        // them as "an intact JPEG file" hands the caller silently corrupt output (proven in
        // review). DCT anywhere else in the chain falls through to the generic path below,
        // which degrades to raw-encoded-plus-diagnostic.
        var dctIndex = filters.FindIndex(static f => f is "DCTDecode" or "DCT");
        if (dctIndex >= 0 && dctIndex == filters.Count - 1)
        {
            if (dctIndex == 0)
            {
                return new ExtractedImage(reference, width, height, bpc, colorSpace, decode, softMask, filters, stream.RawBytes, isJpeg: true, isRawEncoded: false);
            }

            try
            {
                var jpegBytes = DecodePrefixFilters(dict, stream.RawBytes, dctIndex, options, diagnostics, reference, objects);
                return new ExtractedImage(reference, width, height, bpc, colorSpace, decode, softMask, filters, jpegBytes, isJpeg: true, isRawEncoded: false);
            }
            catch (PlumePdfException ex)
            {
                diagnostics.Add(new PdfDiagnostic("PLUME6023", DiagnosticSeverity.Warning, $"Image{(reference is { } jid ? $" {jid}" : string.Empty)} chains {filters[0]} before DCTDecode but the prefix could not be decoded ({ex.Message}); returning its still-encoded bytes.", subject: reference));
                return new ExtractedImage(reference, width, height, bpc, colorSpace, decode, softMask, filters, stream.RawBytes, isJpeg: false, isRawEncoded: true);
            }
        }

        // A terminal, non-overridden JPXDecode decodes directly via
        // JpxImageDecoder, mirroring the DCT special-case above - the codestream's own Width/
        // Height/precision/colour facts are authoritative for what Data actually contains, unlike
        // the generic branch below (which trusts the dictionary's declared geometry for every
        // other filter). A caller-registered override falls through to that generic branch
        // instead, so the override's own bytes are returned exactly as any other registered
        // filter's would be.
        var jpxIndex = filters.FindIndex(static f => f == "JPXDecode");
        var jpxOverridden = options.Filters.TryGetDecoder("JPXDecode", out var jpxOverrideFilter) && jpxOverrideFilter is not JpxFilterAdapter;
        if (jpxIndex >= 0 && jpxIndex == filters.Count - 1 && !jpxOverridden)
        {
            try
            {
                var jpxSourceBytes = jpxIndex == 0
                    ? stream.RawBytes.ToArray()
                    : DecodePrefixFilters(dict, stream.RawBytes, jpxIndex, options, diagnostics, reference, objects);

                var jpxImage = JpxImageDecoder.Decode(jpxSourceBytes, options, diagnostics);

                if (jpxImage.ColourChannelCount == 0)
                {
                    // Every component is the cdef opacity channel: there are no colour samples to
                    // return, so a "decoded" ExtractedImage would carry an empty Data over the
                    // dictionary's own declared geometry. Take
                    // the same degrade-to-raw-bytes branch a failed decode takes, with the same
                    // code, so a caller checking IsRawEncoded/PLUME6023 sees one consistent story.
                    diagnostics.Add(new PdfDiagnostic("PLUME6023", DiagnosticSeverity.Warning, $"Image{(reference is { } axid ? $" {axid}" : string.Empty)} uses JPXDecode but its JPEG 2000 codestream has no colour channels (every component is cdef opacity); returning its still-encoded bytes.", subject: reference));
                    return new ExtractedImage(reference, width, height, bpc, colorSpace, decode, softMask, filters, stream.RawBytes, isJpeg: false, isRawEncoded: true);
                }

                var maxColourPrecision = 0;
                for (var p = 0; p < jpxImage.Planes.Length; p++)
                {
                    if (p == jpxImage.Colour.AlphaChannelIndex)
                    {
                        continue;
                    }

                    maxColourPrecision = Math.Max(maxColourPrecision, jpxImage.Planes[p].Precision);
                }

                PdfObject? jpxCsResolved = dict.TryGetValue(ColorSpaceName, out var jpxCsRaw) ? PageSpace.Resolve(jpxCsRaw, objects) : null;
                var isIndexed = jpxCsResolved is PdfArray { Count: > 0 } jpxCsArray && PageSpace.Resolve(jpxCsArray[0], objects) is PdfName { Value: "Indexed" };

                // The same "≤8 bits -> 8-bit contract, else 16-bit" rule JpxFilterAdapter
                // applies - both call sites reuse JpxImage's own frozen implementation
                // rather than each other, so a caller comparing this path against a registered
                // JPXDecode IPdfFilter's output never sees the two disagree.
                var jpxBitsPerComponent = maxColourPrecision <= 8 ? 8 : 16;
                byte[] jpxData;
                if (isIndexed)
                {
                    // /Indexed over JPX: the samples ARE palette indices, so they must reach the
                    // caller at their native value — never the full-scale rescale, which would
                    // turn index 8 of a 4-bit plane into 136 and every 12-bit index into a
                    // 16-bit-scaled number that indexes nothing.
                    // The rasterizer's own ImageXObjectResolver reads the same raw SampleUnsigned
                    // value as the index; this mirrors it in ExtractedImage's own byte layout
                    // (one byte per sample at ≤8 bits, two big-endian bytes above — the container
                    // width BitsPerComponent describes; the VALUES are the raw indices).
                    jpxData = JpxIndexSamples(jpxImage, jpxBitsPerComponent);
                }
                else
                {
                    jpxData = jpxBitsPerComponent == 8 ? jpxImage.ToInterleaved8Bit(dropAlpha: true) : jpxImage.ToInterleaved16BitBigEndian(dropAlpha: true);
                }

                var jpxColorSpaceName = colorSpace ?? DeviceColorSpaceNameByChannelCount(jpxImage.ColourChannelCount);

                // Data is always packed at the codestream's
                // OWN channel count (ColourChannelCount, dropping alpha) regardless of what a
                // declared /ColorSpace says - ExtractedImage's contract promises Data is laid
                // out per BitsPerComponent/ColorSpaceName, so a caller trusting a mismatched
                // declared name to compute Data's size would be wrong. The rasterizer's own
                // ImageXObjectResolver already records this exact disagreement as PLUME7753;
                // mirror that here rather than leaving this seam with no diagnostic twin.
                // /Indexed is skipped (as the resolver's own equivalent check is): its one real
                // sample channel never matches a multi-component base space's count, and that
                // is not a disagreement - it is how /Indexed works.
                if (!isIndexed && jpxCsResolved is not null)
                {
                    try
                    {
                        var declaredSpace = Raster.Color.ColorSpace.Parse(jpxCsResolved, r => objects[r], options.Filters, options, diagnostics: null);
                        if (declaredSpace.ComponentCount != jpxImage.ColourChannelCount)
                        {
                            diagnostics.Add(new PdfDiagnostic(
                                "PLUME7753",
                                DiagnosticSeverity.Warning,
                                $"Image{(reference is { } csid ? $" {csid}" : string.Empty)}'s /ColorSpace declares {declaredSpace.ComponentCount} component(s) but its JPEG 2000 codestream has {jpxImage.ColourChannelCount} colour channel(s); Data is packed at the codestream's own channel count, not the declared one.",
                                subject: reference));
                        }
                    }
                    catch (PlumePdfException)
                    {
                        // The declared space isn't one RasterColorSpace can parse on its own
                        // (e.g. /Pattern, an unsupported family) - nothing to compare against;
                        // ColorSpaceName below still carries the raw declared name regardless.
                    }
                }

                return new ExtractedImage(reference, jpxImage.Width, jpxImage.Height, jpxBitsPerComponent, jpxColorSpaceName, decode: null, softMask, filters, jpxData, isJpeg: false, isRawEncoded: false);
            }
            catch (PlumePdfException ex)
            {
                diagnostics.Add(new PdfDiagnostic("PLUME6023", DiagnosticSeverity.Warning, $"Image{(reference is { } jxid ? $" {jxid}" : string.Empty)} uses JPXDecode but could not be decoded ({ex.Code}: {ex.Message}); returning its still-encoded bytes.", subject: reference));
                return new ExtractedImage(reference, width, height, bpc, colorSpace, decode, softMask, filters, stream.RawBytes, isJpeg: false, isRawEncoded: true);
            }
        }

        // Try decoding through the registry for every other filter chain (Flate/LZW/RunLength, a
        // caller-overridden JPXDecode, or any other registered codec) - an unregistered filter
        // name (CCITT/JBIG2 without a registration, or any community codec a caller hasn't
        // registered via PdfFilterRegistry's extension seam) throws PLUME3010, caught below
        // and degraded to raw bytes + a diagnostic.
        try
        {
            var decoded = options.Filters.Decode(dict, stream.RawBytes.Span, options, diagnostics, reference, ResolverFor(objects));
            return new ExtractedImage(reference, width, height, bpc, colorSpace, decode, softMask, filters, decoded, isJpeg: false, isRawEncoded: false);
        }
        catch (PlumePdfException ex)
        {
            diagnostics.Add(new PdfDiagnostic("PLUME6023", DiagnosticSeverity.Warning, $"Image{(reference is { } id ? $" {id}" : string.Empty)} uses a filter chain PlumePDF cannot decode ({ex.Message}); returning its still-encoded bytes. See PLUME3xxx for the underlying codec gap.", subject: reference));
            return new ExtractedImage(reference, width, height, bpc, colorSpace, decode, softMask, filters, stream.RawBytes, isJpeg: false, isRawEncoded: true);
        }
    }

    private static void RecordInlineImageDiagnostics(PdfDictionary pageDictionary, ObjectRegistry objects, PdfOptions options, DiagnosticCollection diagnostics)
    {
        if (!pageDictionary.TryGetValue(ContentsName, out var contents))
        {
            return;
        }

        var streams = PageContents.ResolveStreams(contents, objects);
        foreach (var stream in streams)
        {
            byte[] decoded;
            try
            {
                decoded = stream.GetDecodedBytes(options.Filters, options, ResolverFor(objects));
            }
            catch (PlumePdfException)
            {
                continue;
            }

            var operations = ContentStreamReader.Read(decoded, options, diagnostics: null, options.MaxContentStreamOperators);
            foreach (var op in operations)
            {
                if (op.IsInlineImage)
                {
                    diagnostics.Add(new PdfDiagnostic("PLUME6022", DiagnosticSeverity.Info, $"An inline image (BI…ID…EI) at content-stream offset {op.Offset} was skipped — PlumePDF does not extract inline images in Phase 3."));
                }
            }
        }
    }

    private static string? ResolveColorSpaceName(PdfObject? value, ObjectRegistry objects)
    {
        var resolved = PageSpace.Resolve(value, objects);
        return resolved switch
        {
            PdfName name => name.Value,
            PdfArray { Count: > 0 } array when array[0] is PdfName first => first.Value,
            _ => null,
        };
    }

    private static IReadOnlyList<double>? ReadDecodeArray(PdfDictionary dict)
    {
        if (!dict.TryGetValue(DecodeName, out var value) || value is not PdfArray array)
        {
            return null;
        }

        var result = new List<double>(array.Count);
        foreach (var item in array)
        {
            if (item is PdfNumber n)
            {
                result.Add(n.Value);
            }
        }

        return result;
    }

    /// <summary>
    /// Decodes the first <paramref name="prefixLength"/> filters of a stream's chain by
    /// handing the registry a synthetic dictionary holding just that prefix (names plus the
    /// matching <c>/DecodeParms</c> entries) — used to unwrap e.g. <c>[/FlateDecode /DCTDecode]</c>
    /// down to the intact JPEG bytes the terminal DCT stage wraps. Promoted to
    /// <c>internal</c> so the render-time image resolver
    /// (<c>ImageXObjectResolver</c>) reuses this exact unwrap for its own terminal-DCT direct
    /// decode instead of authoring a second one; behavior unchanged.
    /// </summary>
    internal static byte[] DecodePrefixFilters(PdfDictionary dict, ReadOnlyMemory<byte> raw, int prefixLength, PdfOptions options, DiagnosticCollection diagnostics, IndirectReference? reference, ObjectRegistry objects)
    {
        var prefixDict = new PdfDictionary();

        var filterNames = dict[PdfName.Filter] is PdfArray filterArray
            ? filterArray.OfType<PdfName>().Take(prefixLength).Cast<PdfObject>().ToList()
            : [dict[PdfName.Filter]];
        prefixDict.Set(PdfName.Filter, new PdfArray(filterNames));

        if (dict.TryGetValue(PdfName.DecodeParms, out var parmsValue))
        {
            prefixDict.Set(PdfName.DecodeParms, parmsValue is PdfArray parmsArray
                ? new PdfArray(parmsArray.Take(prefixLength))
                : parmsValue);
        }

        return options.Filters.Decode(prefixDict, raw.Span, options, diagnostics, reference, ResolverFor(objects));
    }

    private static List<string> GetFilterChain(PdfDictionary dict)
    {
        if (!dict.TryGetValue(PdfName.Filter, out var value))
        {
            return [];
        }

        return value switch
        {
            PdfName single => [single.Value],
            PdfArray array => [.. array.OfType<PdfName>().Select(static n => n.Value)],
            _ => [],
        };
    }

    /// <summary>The device colorspace name implied by a JPEG 2000 image's own colour-channel count, used only when the dictionary declares no <c>/ColorSpace</c> of its own.</summary>
    private static string? DeviceColorSpaceNameByChannelCount(int channelCount) => channelCount switch
    {
        1 => "DeviceGray",
        3 => "DeviceRGB",
        4 => "DeviceCMYK",
        _ => null,
    };

    /// <summary>
    /// The raw, unrescaled palette-index samples of an <c>/Indexed</c> JPX image: the first colour
    /// plane's <see cref="JpxImage.SampleUnsigned"/> values (the same value the rasterizer's
    /// <c>ImageXObjectResolver</c> looks up in the palette), one byte per sample when
    /// <paramref name="bitsPerComponent"/> is 8, two big-endian bytes when it is 16.
    /// </summary>
    private static byte[] JpxIndexSamples(JpxImage image, int bitsPerComponent)
    {
        var indexPlane = 0;
        while (indexPlane == image.Colour.AlphaChannelIndex)
        {
            indexPlane++;
        }

        var pixels = image.Width * image.Height;
        if (bitsPerComponent == 8)
        {
            var bytes = new byte[pixels];
            for (var i = 0; i < pixels; i++)
            {
                bytes[i] = (byte)image.SampleUnsigned(indexPlane, i);
            }

            return bytes;
        }

        var wide = new byte[pixels * 2];
        for (var i = 0; i < pixels; i++)
        {
            var v = image.SampleUnsigned(indexPlane, i);
            wide[i * 2] = (byte)(v >> 8);
            wide[(i * 2) + 1] = (byte)v;
        }

        return wide;
    }

    private static int GetInt(PdfDictionary dict, PdfName key) =>
        dict.TryGetValue(key, out var value) && value is PdfNumber number && number.TryToInt32(out var converted) ? converted : 0;
}
