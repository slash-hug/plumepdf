using System.Buffers.Binary;

namespace PlumePdf.Filters.Png;

/// <summary>
/// A from-scratch PNG decoder (ISO/IEC 15948, W3C PNG 1.2) covering every chunk
/// <see cref="RasterImage"/> needs: the 8-byte signature, <c>IHDR</c>, <c>PLTE</c>,
/// <c>tRNS</c>, <c>pHYs</c>, <c>IDAT</c>, <c>IEND</c>. All five color types (0 gray, 2
/// truecolor, 3 palette, 4 gray+alpha, 6 truecolor+alpha) and all five bit depths (1, 2, 4,
/// 8, 16) decode; both interlace methods (0 none, 1 Adam7) decode. Row de-filtering reuses
/// <see cref="Predictor"/> — a PNG scanline's own per-row filter-type byte is exactly the
/// "PNG predictor" shape <see cref="Predictor.Undo"/> already implements for
/// <c>/DecodeParms /Predictor 10-15</c> streams (the same algorithm, ISO 32000-1 §7.4.4.4
/// Table 8 is itself a citation of the PNG spec). Every source sample, regardless of bit
/// depth or color type, is normalized to one of <see cref="RasterPixelFormat"/>'s three
/// 8-bit shapes (<see cref="RasterImageFrame"/>'s XML doc explains why).
/// </summary>
internal static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    // A generous but finite decoded-pixel ceiling (the decompression-bomb guard) —
    // matches PdfOptions.MaxImagePixels' own default: 1 << 27 = ~134M pixels, roughly an A0
    // sheet at 600 dpi. Production callers (RasterImage.Decode) pass options.MaxImagePixels
    // directly rather than this constant; it exists as the fallback default for this method's
    // maxPixels parameter and for standalone/test callers with no PdfOptions in hand.
    internal const long DefaultMaxPixels = 1L << 27;

    /// <summary>Decodes a complete PNG file's bytes into one normalized <see cref="RasterImageFrame"/>.</summary>
    /// <param name="data">The complete PNG file bytes, starting with the 8-byte signature.</param>
    /// <param name="maxPixels">The maximum <c>width * height</c> this call will decode before refusing (decompression-bomb guard).</param>
    /// <param name="diagnostics">Where recoverable deviations (e.g. a malformed <c>tRNS</c> chunk, ignored; a zlib-header fallback; a truncated <c>IDAT</c> stream) are recorded, or <see langword="null"/> to discard them.</param>
    /// <param name="options">
    /// Supplies <see cref="PdfOptions.MaxDecompressedStreamBytes"/> — the second,
    /// independent decompression-bomb guard <c>FlateFilter.Decode</c> enforces incrementally
    /// while inflating the concatenated <c>IDAT</c> stream. <c>IHDR</c>'s <paramref name="maxPixels"/>
    /// check runs first and rejects most hostile inputs before any inflation starts, but a
    /// small, honestly-sized <c>IHDR</c> paired with a maliciously well-compressed <c>IDAT</c>
    /// (a classic zip-bomb shape) would otherwise slip past it. Also supplies
    /// <see cref="PdfOptions.Strict"/> for the tRNS/pHYs deviation diagnostics below. Defaults
    /// to <see cref="PdfOptions.Default"/>.
    /// </param>
    /// <exception cref="PlumePdfException">The bytes are not a well-formed PNG this decoder can decode — see <c>docs/errors/PLUME325x</c>.</exception>
    public static RasterImageFrame Decode(ReadOnlySpan<byte> data, long maxPixels, DiagnosticCollection? diagnostics = null, PdfOptions? options = null)
    {
        var effectiveOptions = options ?? PdfOptions.Default;
        if (data.Length < Signature.Length || !data[..Signature.Length].SequenceEqual(Signature))
        {
            throw new PlumePdfException("PLUME3250", "Not a PNG file: the 8-byte PNG signature (89 50 4E 47 0D 0A 1A 0A) is missing.");
        }

        var offset = Signature.Length;
        IhdrInfo? ihdr = null;
        byte[]? palette = null;
        byte[]? transparency = null;
        double? xDpi = null;
        double? yDpi = null;
        using var idat = new MemoryStream();
        var sawIend = false;

        while (offset < data.Length)
        {
            if (offset + 8 > data.Length)
            {
                throw new PlumePdfException("PLUME3251", $"Malformed PNG: a chunk header at byte offset {offset} runs past the end of the file.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            var type = System.Text.Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
            var dataStart = offset + 8;
            if (length > int.MaxValue || dataStart + length + 4 > data.Length)
            {
                throw new PlumePdfException("PLUME3251", $"Malformed PNG: chunk '{type}' declares a length ({length}) that runs past the end of the file.");
            }

            var chunkData = data.Slice(dataStart, (int)length);

            switch (type)
            {
                case "IHDR":
                    ihdr = ParseIhdr(chunkData);
                    break;
                case "PLTE":
                    palette = chunkData.ToArray();
                    break;
                case "tRNS":
                    if (ihdr is { } tRnsHeader && !IsWellFormedTrns(tRnsHeader.ColorType, chunkData.Length))
                    {
                        FilterDiagnostics.ReportDeviation("PLUME3257", $"PNG: tRNS chunk (length {chunkData.Length}) is not valid for color type {tRnsHeader.ColorType} — ignoring it (image decodes fully opaque).", effectiveOptions, diagnostics, subject: null);
                    }
                    else
                    {
                        transparency = chunkData.ToArray();
                    }

                    break;
                case "pHYs":
                    if (chunkData.Length == 9 && chunkData[8] == 1)
                    {
                        var ppuX = BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                        var ppuY = BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                        // pixels-per-meter -> pixels-per-inch (1 inch = 0.0254 meters).
                        xDpi = ppuX * 0.0254;
                        yDpi = ppuY * 0.0254;
                    }
                    else if (chunkData.Length != 9)
                    {
                        FilterDiagnostics.ReportDeviation("PLUME3257", $"PNG: pHYs chunk has length {chunkData.Length}, expected 9 — ignoring it (no dpi metadata recorded).", effectiveOptions, diagnostics, subject: null);
                    }

                    // chunkData[8] != 1 ("unknown" unit) is not malformed, just unusable for a
                    // dpi figure — silently skipped per spec, no diagnostic warranted.
                    break;
                case "IDAT":
                    idat.Write(chunkData);
                    break;
                case "IEND":
                    sawIend = true;
                    break;
                default:
                    // Ancillary or unrecognized chunk (tEXt, gAMA, iCCP, private, ...): every
                    // chunk this decoder needs is enumerated above; anything else is skippable
                    // by construction (a *critical* unrecognized chunk would have an uppercase
                    // first letter and would matter, but PLUME325x's scope is the six chunks
                    // named in this type's summary — no critical chunk beyond them is minted).
                    break;
            }

            offset = dataStart + (int)length + 4; // +4 for the CRC this decoder does not verify.
            if (sawIend)
            {
                break;
            }
        }

        if (ihdr is not { } header)
        {
            throw new PlumePdfException("PLUME3251", "Malformed PNG: no IHDR chunk was found.");
        }

        var pixelCount = (long)header.Width * header.Height;
        if (pixelCount <= 0 || pixelCount > maxPixels)
        {
            throw new PlumePdfException("PLUME3255", $"PNG decode refused: {header.Width}x{header.Height} ({pixelCount} pixels) exceeds the {maxPixels}-pixel decode cap (possible decompression bomb).");
        }

        if (header.ColorType == 3 && palette is null)
        {
            throw new PlumePdfException("PLUME3253", "Malformed PNG: color type 3 (palette) requires a PLTE chunk, none was found.");
        }

        if (palette is not null && palette.Length % 3 != 0)
        {
            throw new PlumePdfException("PLUME3253", $"Malformed PNG: PLTE chunk length ({palette.Length}) is not a multiple of 3.");
        }

        byte[] inflated;
        try
        {
            inflated = FlateFilter.Decode(idat.ToArray(), effectiveOptions.MaxDecompressedStreamBytes, diagnostics: diagnostics, subject: null);
        }
        catch (Exception ex) when (ex is PlumePdfException or IOException or InvalidDataException)
        {
            // FlateFilter.TryDecode only catches InvalidDataException around the inflater's
            // Read call; the native zlib inflater can also throw
            // System.IO.Compression.ZLibException (derives from IOException, not
            // InvalidDataException, and is not part of the net8.0 reference-assembly surface
            // - callers can only catch it via its IOException base, never by its own name)
            // for some corrupt-stream shapes, and that escapes FlateFilter.Decode uncoded.
            // Contained here at the PngDecoder boundary (FlateFilter.cs itself is out of
            // scope) so a corrupt IDAT always surfaces as the coded PLUME3254, never a raw
            // System.IO.Compression/System.IO exception.
            throw new PlumePdfException("PLUME3254", $"PNG decode refused: the concatenated IDAT stream failed to decompress ({ex.Message}).", ex);
        }

        var samplesPerPixel = header.ColorType switch
        {
            0 => 1, // gray
            2 => 3, // RGB
            3 => 1, // palette index
            4 => 2, // gray + alpha
            6 => 4, // RGBA
            _ => throw new PlumePdfException("PLUME3252", $"Unsupported PNG color type {header.ColorType}."),
        };

        // Decode into a working RGBA8888 buffer; collapse to the minimal RasterPixelFormat
        // once every pixel is known (see this type's summary). The width*height*4 allocation
        // is deferred until after each interlace path's own inflated-length check (mirroring
        // the Adam7 per-pass check below) so a tiny, honestly-sized IDAT paired with a huge
        // declared IHDR fails that check before the big buffer is ever allocated.
        var sawNonOpaqueAlpha = false;
        byte[] rgba;

        if (header.Interlace == 0)
        {
            var rowBytes = SampleRowBytes(samplesPerPixel, header.BitDepth, header.Width);
            var rows = Predictor.Undo(inflated, 15, samplesPerPixel, header.BitDepth, header.Width);
            ExpectedRowsOrThrow(rows.Length, rowBytes, header.Height);
            rgba = new byte[(long)header.Width * header.Height * 4];
            for (var y = 0; y < header.Height; y++)
            {
                var row = rows.AsSpan(y * rowBytes, rowBytes);
                WriteRow(row, header, samplesPerPixel, palette, transparency, rgba, y * header.Width, header.Width, ref sawNonOpaqueAlpha);
            }
        }
        else if (header.Interlace == 1)
        {
            rgba = new byte[(long)header.Width * header.Height * 4];
            DecodeAdam7(inflated, header, samplesPerPixel, palette, transparency, rgba, ref sawNonOpaqueAlpha);
        }
        else
        {
            throw new PlumePdfException("PLUME3256", $"Unsupported PNG interlace method {header.Interlace} (only 0 'none' and 1 'Adam7' are defined).");
        }

        var isGrayscaleSource = header.ColorType is 0 or 4;
        return CollapseFormat(rgba, header.Width, header.Height, isGrayscaleSource, sawNonOpaqueAlpha || header.ColorType is 4 or 6, xDpi, yDpi);
    }

    private static void ExpectedRowsOrThrow(int actualLength, int rowBytes, int height)
    {
        var expected = rowBytes * height;
        if (actualLength < expected)
        {
            throw new PlumePdfException("PLUME3254", $"PNG decode refused: the de-filtered IDAT stream ({actualLength} bytes) is shorter than the {expected} bytes {height} row(s) of {rowBytes} bytes each require (truncated file).");
        }
    }

    private readonly record struct IhdrInfo(int Width, int Height, int BitDepth, int ColorType, int Interlace);

    private static IhdrInfo ParseIhdr(ReadOnlySpan<byte> data)
    {
        if (data.Length != 13)
        {
            throw new PlumePdfException("PLUME3251", $"Malformed PNG: IHDR chunk must be 13 bytes, got {data.Length}.");
        }

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        int bitDepth = data[8];
        int colorType = data[9];
        var compression = data[10];
        var filter = data[11];
        int interlace = data[12];

        if (width <= 0 || height <= 0)
        {
            throw new PlumePdfException("PLUME3251", $"Malformed PNG: IHDR declares non-positive dimensions {width}x{height}.");
        }

        if (compression != 0 || filter != 0)
        {
            throw new PlumePdfException("PLUME3251", $"Malformed PNG: IHDR declares unsupported compression method {compression} or filter method {filter} (only 0 is defined).");
        }

        var validDepths = colorType switch
        {
            0 => new[] { 1, 2, 4, 8, 16 },
            2 => new[] { 8, 16 },
            3 => new[] { 1, 2, 4, 8 },
            4 => new[] { 8, 16 },
            6 => new[] { 8, 16 },
            _ => throw new PlumePdfException("PLUME3252", $"Unsupported PNG color type {colorType}."),
        };

        if (Array.IndexOf(validDepths, bitDepth) < 0)
        {
            throw new PlumePdfException("PLUME3252", $"Unsupported PNG bit depth {bitDepth} for color type {colorType} (valid: {string.Join(", ", validDepths)}).");
        }

        return new IhdrInfo(width, height, bitDepth, colorType, interlace);
    }

    /// <summary>Whether a tRNS chunk of <paramref name="length"/> bytes is structurally valid for <paramref name="colorType"/> (PNG spec §11.3.2.1) — color types 4/6 already carry a full alpha channel and may never have one at all.</summary>
    private static bool IsWellFormedTrns(int colorType, int length) => colorType switch
    {
        0 => length == 2, // one 16-bit gray sample value.
        2 => length == 6, // three 16-bit RGB sample values.
        3 => length is > 0 and <= 256, // up to one alpha byte per palette entry.
        _ => false, // 4 (gray+alpha) and 6 (truecolor+alpha) never carry tRNS.
    };

    private static int SampleRowBytes(int samplesPerPixel, int bitDepth, int columns) =>
        ((samplesPerPixel * bitDepth * columns) + 7) / 8;

    /// <summary>Unpacks one de-filtered scanline's raw samples and writes RGBA8888 pixels into <paramref name="rgba"/> starting at pixel index <paramref name="destPixelOffset"/>.</summary>
    private static void WriteRow(ReadOnlySpan<byte> row, IhdrInfo header, int samplesPerPixel, byte[]? palette, byte[]? transparency, byte[] rgba, int destPixelOffset, int columns, ref bool sawNonOpaqueAlpha)
    {
        var maxSample = (1 << header.BitDepth) - 1;
        Span<int> samples = stackalloc int[4];

        for (var x = 0; x < columns; x++)
        {
            for (var s = 0; s < samplesPerPixel; s++)
            {
                samples[s] = ReadSample(row, header.BitDepth, (x * samplesPerPixel) + s);
            }

            var destIndex = (destPixelOffset + x) * 4;
            switch (header.ColorType)
            {
                case 0: // grayscale
                    {
                        var gray8 = Scale(samples[0], maxSample);
                        byte alpha = 255;
                        if (transparency is { Length: >= 2 })
                        {
                            var trnsValue = (transparency[0] << 8) | transparency[1];
                            if (samples[0] == trnsValue)
                            {
                                alpha = 0;
                                sawNonOpaqueAlpha = true;
                            }
                        }

                        rgba[destIndex] = gray8;
                        rgba[destIndex + 1] = gray8;
                        rgba[destIndex + 2] = gray8;
                        rgba[destIndex + 3] = alpha;
                        break;
                    }

                case 2: // truecolor
                    {
                        var r = Scale(samples[0], maxSample);
                        var g = Scale(samples[1], maxSample);
                        var b = Scale(samples[2], maxSample);
                        byte alpha = 255;
                        if (transparency is { Length: >= 6 })
                        {
                            var trnsR = (transparency[0] << 8) | transparency[1];
                            var trnsG = (transparency[2] << 8) | transparency[3];
                            var trnsB = (transparency[4] << 8) | transparency[5];
                            if (samples[0] == trnsR && samples[1] == trnsG && samples[2] == trnsB)
                            {
                                alpha = 0;
                                sawNonOpaqueAlpha = true;
                            }
                        }

                        rgba[destIndex] = r;
                        rgba[destIndex + 1] = g;
                        rgba[destIndex + 2] = b;
                        rgba[destIndex + 3] = alpha;
                        break;
                    }

                case 3: // palette
                    {
                        var index = samples[0];
                        var paletteBytes = palette ?? [];
                        if (index * 3 + 2 >= paletteBytes.Length)
                        {
                            throw new PlumePdfException("PLUME3253", $"Malformed PNG: palette index {index} has no matching PLTE entry ({paletteBytes.Length / 3} entries available).");
                        }

                        rgba[destIndex] = paletteBytes[index * 3];
                        rgba[destIndex + 1] = paletteBytes[(index * 3) + 1];
                        rgba[destIndex + 2] = paletteBytes[(index * 3) + 2];
                        byte alpha = 255;
                        if (transparency is not null && index < transparency.Length)
                        {
                            alpha = transparency[index];
                            if (alpha != 255)
                            {
                                sawNonOpaqueAlpha = true;
                            }
                        }

                        rgba[destIndex + 3] = alpha;
                        break;
                    }

                case 4: // gray + alpha
                    {
                        var gray8 = Scale(samples[0], maxSample);
                        var alpha8 = Scale(samples[1], maxSample);
                        rgba[destIndex] = gray8;
                        rgba[destIndex + 1] = gray8;
                        rgba[destIndex + 2] = gray8;
                        rgba[destIndex + 3] = alpha8;
                        if (alpha8 != 255)
                        {
                            sawNonOpaqueAlpha = true;
                        }

                        break;
                    }

                case 6: // truecolor + alpha
                    {
                        rgba[destIndex] = Scale(samples[0], maxSample);
                        rgba[destIndex + 1] = Scale(samples[1], maxSample);
                        rgba[destIndex + 2] = Scale(samples[2], maxSample);
                        var alpha8 = Scale(samples[3], maxSample);
                        rgba[destIndex + 3] = alpha8;
                        if (alpha8 != 255)
                        {
                            sawNonOpaqueAlpha = true;
                        }

                        break;
                    }
            }
        }
    }

    /// <summary>Reads the <paramref name="sampleIndex"/>-th <paramref name="bitDepth"/>-bit sample from a de-filtered row, MSB-first (PNG's packing order).</summary>
    private static int ReadSample(ReadOnlySpan<byte> row, int bitDepth, int sampleIndex)
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

        // 1, 2, or 4 bits per sample, packed MSB-first within each byte, no sample crosses a byte boundary.
        var samplesPerByte = 8 / bitDepth;
        var byteIndex = sampleIndex / samplesPerByte;
        var withinByte = sampleIndex % samplesPerByte;
        var shift = 8 - bitDepth - (withinByte * bitDepth);
        var mask = (1 << bitDepth) - 1;
        return (row[byteIndex] >> shift) & mask;
    }

    /// <summary>Scales a <paramref name="maxSample"/>-max raw sample to the 0-255 range (identity at 8-bit, high-byte downsample at 16-bit, proportional at 1/2/4-bit).</summary>
    private static byte Scale(int raw, int maxSample) =>
        maxSample switch
        {
            255 => (byte)raw,
            65535 => (byte)(raw >> 8),
            _ => (byte)(((raw * 255) + (maxSample / 2)) / maxSample),
        };

    private static void DecodeAdam7(byte[] inflated, IhdrInfo header, int samplesPerPixel, byte[]? palette, byte[]? transparency, byte[] rgba, ref bool sawNonOpaqueAlpha)
    {
        // ISO/IEC 15948 §8.2 / PNG spec Adam7: 7 passes, each a regularly-subsampled
        // sub-image of the full raster, interleaved by starting offset and step.
        Span<(int XStart, int YStart, int XStep, int YStep)> passes =
        [
            (0, 0, 8, 8),
            (4, 0, 8, 8),
            (0, 4, 4, 8),
            (2, 0, 4, 4),
            (0, 2, 2, 4),
            (1, 0, 2, 2),
            (0, 1, 1, 2),
        ];

        var cursor = 0;
        var tempRgbaRow = new byte[header.Width * 4]; // reused scratch: WriteRow writes contiguous pixels starting at 0

        foreach (var (xStart, yStart, xStep, yStep) in passes)
        {
            var passWidth = xStart >= header.Width ? 0 : ((header.Width - xStart + xStep - 1) / xStep);
            var passHeight = yStart >= header.Height ? 0 : ((header.Height - yStart + yStep - 1) / yStep);
            if (passWidth == 0 || passHeight == 0)
            {
                continue;
            }

            var rowBytes = SampleRowBytes(samplesPerPixel, header.BitDepth, passWidth);
            var stride = rowBytes + 1;
            var passByteLength = stride * passHeight;
            if (cursor + passByteLength > inflated.Length)
            {
                throw new PlumePdfException("PLUME3254", $"PNG decode refused: the de-filtered Adam7 stream is shorter than its passes require (truncated file at pass starting {xStart},{yStart}).");
            }

            var passData = inflated.AsSpan(cursor, passByteLength);
            cursor += passByteLength;

            var rows = Predictor.Undo(passData, 15, samplesPerPixel, header.BitDepth, passWidth);
            for (var py = 0; py < passHeight; py++)
            {
                var row = rows.AsSpan(py * rowBytes, rowBytes);
                WriteRow(row, header, samplesPerPixel, palette, transparency, tempRgbaRow, 0, passWidth, ref sawNonOpaqueAlpha);

                var destY = yStart + (py * yStep);
                for (var px = 0; px < passWidth; px++)
                {
                    var destX = xStart + (px * xStep);
                    var destIndex = ((destY * header.Width) + destX) * 4;
                    var srcIndex = px * 4;
                    rgba[destIndex] = tempRgbaRow[srcIndex];
                    rgba[destIndex + 1] = tempRgbaRow[srcIndex + 1];
                    rgba[destIndex + 2] = tempRgbaRow[srcIndex + 2];
                    rgba[destIndex + 3] = tempRgbaRow[srcIndex + 3];
                }
            }
        }
    }

    private static RasterImageFrame CollapseFormat(byte[] rgba, int width, int height, bool isGrayscaleSource, bool needsAlpha, double? xDpi, double? yDpi)
    {
        if (needsAlpha)
        {
            return new RasterImageFrame(rgba, width, height, RasterPixelFormat.Rgba32, xDpi, yDpi);
        }

        if (isGrayscaleSource)
        {
            var gray = new byte[(long)width * height];
            for (var i = 0; i < gray.Length; i++)
            {
                gray[i] = rgba[i * 4]; // R == G == B for every grayscale-sourced pixel
            }

            return new RasterImageFrame(gray, width, height, RasterPixelFormat.Gray8, xDpi, yDpi);
        }

        var rgb = new byte[(long)width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            rgb[i * 3] = rgba[(i * 4) + 0];
            rgb[(i * 3) + 1] = rgba[(i * 4) + 1];
            rgb[(i * 3) + 2] = rgba[(i * 4) + 2];
        }

        return new RasterImageFrame(rgb, width, height, RasterPixelFormat.Rgb24, xDpi, yDpi);
    }
}
