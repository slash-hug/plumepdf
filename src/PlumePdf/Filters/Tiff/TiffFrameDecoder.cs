namespace PlumePdf.Filters.Tiff;

/// <summary>
/// One TIFF frame's decoded pixel data plus the metadata a caller needs to interpret it:
/// dimensions, sample layout, and how to map samples to color (photometric interpretation
/// and, for palette images, the color map). Rows are tightly packed, MSB-first,
/// <see cref="SamplesPerPixel"/> interleaved samples of <see cref="BitsPerSample"/> bits
/// each per pixel — i.e. exactly the on-disk sample layout, before any photometric-to-RGB
/// expansion (that expansion is a <c>RasterImage</c> concern, not this reader's).
/// </summary>
internal sealed class TiffFrame(int width, int height, int bitsPerSample, int samplesPerPixel, long photometric, ushort[]? colorMap, double xDpi, double yDpi, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int BitsPerSample { get; } = bitsPerSample;

    public int SamplesPerPixel { get; } = samplesPerPixel;

    public long Photometric { get; } = photometric;

    /// <summary>The RGB triples for a <see cref="TiffPhotometric.Palette"/> frame's <c>/ColorMap</c> (TIFF 6.0 §4), or <see langword="null"/> otherwise. Three planes of <c>2^BitsPerSample</c> 16-bit entries each: all reds, then all greens, then all blues.</summary>
    public ushort[]? ColorMap { get; } = colorMap;

    public double XDpi { get; } = xDpi;

    public double YDpi { get; } = yDpi;

    /// <summary>Row-major, MSB-first packed samples: <c>ceil(Width * SamplesPerPixel * BitsPerSample / 8)</c> bytes per row.</summary>
    public byte[] Pixels { get; } = pixels;
}

/// <summary>
/// Decodes one <see cref="TiffIfd"/>'s pixel data: strips and tiles, both
/// byte orders, <see cref="TiffTag.FillOrder"/> bit reversal, the baseline compressions
/// (None, PackBits, LZW via the existing <see cref="LzwFilter"/> core under TIFF's
/// always-early-change convention, CCITT MH/G3/G4 via <see cref="CcittFaxEngine"/>, Deflate
/// via <see cref="FlateFilter"/>), and the horizontal-differencing <see cref="Predictor"/>.
/// Bilevel/gray/RGB/palette photometrics are supported; CMYK, YCbCr, old-style JPEG(6),
/// and planar (non-chunky) samples are coded refusals (PLUME33xx). New-style JPEG(7) is a
/// deliberate scope decision, not a merge-timing gap, despite <see cref="JpegDecoder"/>
/// having merged: TIFF's new-style JPEG embedding (TIFF Technical Note 2) is its own distinct
/// binary shape — a <c>JPEGTables</c> tag holding shared DQT/DHT segments, separate from each
/// strip/tile's own abbreviated JPEG stream — not simply "hand a strip to
/// <see cref="JpegDecoder"/>", and almost every real-world producer of this compression pairs
/// it with <see cref="TiffPhotometric.YCbCr"/>, which is already refused above (PLUME3311)
/// before compression is even inspected. Per-frame degradation already makes this a
/// skip-and-continue rather than a whole-document failure, so it was left out of this pass
/// rather than risk an under-verified binary-layout implementation (the lesson from a similar
/// GSUB Format-3 parsing bug: a parser that assumes a layout without checking it against a second
/// independent source fails silently, not loudly) — support is deferred to a later release
/// pending a safer verification path.
/// </summary>
internal static class TiffFrameDecoder
{
    /// <summary>
    /// The per-frame pixel-budget cap fallback for callers that don't have a
    /// <see cref="PdfOptions"/> instance's <see cref="PdfOptions.MaxImagePixels"/> in hand
    /// already (<see cref="RasterImage.Decode(ReadOnlyMemory{byte},PdfOptions?)"/> passes
    /// <c>options.MaxImagePixels</c> directly, not this constant).
    /// </summary>
    internal const long DefaultMaxDecodedPixels = 1L << 27;

    /// <summary>
    /// Decodes one frame. Returns <see langword="null"/> (with a <c>PLUME33xx</c>
    /// diagnostic recorded) for a coded refusal or a malformed frame — the R8 per-frame
    /// degradation model: the caller (a future multi-frame <c>RasterImage</c>/TIFF-in-PDF
    /// path) skips that frame and keeps going rather than failing the whole document. This
    /// method itself never throws for a frame-level refusal; it only throws for the
    /// resource-limit cap (a decompression-bomb-shaped refusal, consistent with every other
    /// codec's cap enforcement in this phase).
    /// </summary>
    public static TiffFrame? DecodeFrame(ReadOnlyMemory<byte> fileData, TiffIfd ifd, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var width = (int)ifd.GetInt(TiffTag.ImageWidth, 0);
        var height = (int)ifd.GetInt(TiffTag.ImageLength, 0);
        if (width <= 0 || height <= 0)
        {
            FilterDiagnostics.ReportDeviation("PLUME3315", $"TIFF: frame at IFD offset {ifd.Offset} has an invalid ImageWidth/ImageLength ({width}x{height}); skipping it.", options, diagnostics, subject);
            return null;
        }

        var samplesPerPixel = (int)ifd.GetInt(TiffTag.SamplesPerPixel, 1);
        var bitsPerSampleEntry = ifd.GetInts(TiffTag.BitsPerSample);
        var bitsPerSample = bitsPerSampleEntry.Length > 0 ? (int)bitsPerSampleEntry[0] : 1;
        var photometric = ifd.GetInt(TiffTag.PhotometricInterpretation, TiffPhotometric.WhiteIsZero);
        var compression = ifd.GetInt(TiffTag.Compression, TiffCompression.None);
        var planar = ifd.GetInt(TiffTag.PlanarConfiguration, 1);
        var fillOrder = ifd.GetInt(TiffTag.FillOrder, 1);
        var predictor = (int)ifd.GetInt(TiffTag.Predictor, 1);

        if ((long)width * height > maxDecodedPixels)
        {
            throw new PlumePdfException("PLUME3304", $"TIFF: frame {width}x{height} exceeds the {maxDecodedPixels}-pixel decode cap - refusing to continue (possible decompression bomb).");
        }

        if (planar != 1)
        {
            FilterDiagnostics.ReportDeviation("PLUME3312", $"TIFF: frame at IFD offset {ifd.Offset} uses PlanarConfiguration {planar} (separate planes) - not supported; skipping it.", options, diagnostics, subject);
            return null;
        }

        if (photometric is TiffPhotometric.Cmyk or TiffPhotometric.YCbCr)
        {
            FilterDiagnostics.ReportDeviation("PLUME3311", $"TIFF: frame at IFD offset {ifd.Offset} uses PhotometricInterpretation {photometric} (CMYK/YCbCr) - not supported; skipping it.", options, diagnostics, subject);
            return null;
        }

        if (compression == TiffCompression.OldJpeg)
        {
            FilterDiagnostics.ReportDeviation("PLUME3313", $"TIFF: frame at IFD offset {ifd.Offset} uses old-style JPEG compression (6) - not supported; skipping it.", options, diagnostics, subject);
            return null;
        }

        if (compression == TiffCompression.NewJpeg)
        {
            // Deliberate 1.x scope decision (see this type's own remarks) - real-world
            // producers of this compression pair it with YCbCr photometric, already refused
            // above (PLUME3311); the rarer non-YCbCr case would need TIFF Technical Note 2's
            // distinct JPEGTables/per-strip-abbreviated-stream layout implemented and
            // independently verified, not a one-line call into JpegDecoder.
            FilterDiagnostics.ReportDeviation("PLUME3314", $"TIFF: frame at IFD offset {ifd.Offset} uses new-style JPEG compression (7) - not supported (1.x scope); skipping it.", options, diagnostics, subject);
            return null;
        }

        ushort[]? colorMap = null;
        if (photometric == TiffPhotometric.Palette)
        {
            if (ifd.TryGet(TiffTag.ColorMap, out var cmEntry) && cmEntry.Integers.Length == 3 * (1 << bitsPerSample))
            {
                colorMap = new ushort[cmEntry.Integers.Length];
                for (var i = 0; i < cmEntry.Integers.Length; i++)
                {
                    colorMap[i] = (ushort)cmEntry.Integers[i];
                }
            }
            else
            {
                FilterDiagnostics.ReportDeviation("PLUME3315", $"TIFF: frame at IFD offset {ifd.Offset} is Palette photometric but /ColorMap is missing or the wrong size; skipping it.", options, diagnostics, subject);
                return null;
            }
        }

        // The width*height pixel-count cap above does not bound the packed byte size: a frame
        // that is very wide but short (large width, height 1) passes the pixel cap yet its row
        // stride width*samplesPerPixel*bitsPerSample overflows a 32-bit int before it is ever
        // narrowed, corrupting the allocation. Compute the stride and the total in long
        // arithmetic and refuse up front (PLUME3319) if either exceeds a single .NET array.
        var rowBytesLong = ((long)width * samplesPerPixel * bitsPerSample + 7) / 8;
        if (rowBytesLong > int.MaxValue || rowBytesLong * height > int.MaxValue)
        {
            FilterDiagnostics.ReportDeviation("PLUME3319", $"TIFF: frame at IFD offset {ifd.Offset} would need a {rowBytesLong}-byte-per-row buffer exceeding a single allocation - refusing it (possible malformed dimensions/depth).", options, diagnostics, subject);
            return null;
        }

        var rowBytes = (int)rowBytesLong;
        byte[] pixels;

        try
        {
            var t4Options = ifd.GetInt(TiffTag.T4Options, 0);
            pixels = ifd.Has(TiffTag.TileWidth)
                ? DecodeTiled(fileData.Span, ifd, width, height, samplesPerPixel, bitsPerSample, rowBytes, compression, fillOrder, t4Options, photometric, predictor, options, diagnostics, subject)
                : DecodeStripped(fileData.Span, ifd, width, height, samplesPerPixel, bitsPerSample, rowBytes, compression, fillOrder, t4Options, photometric, predictor, options, diagnostics, subject);
        }
        catch (PlumePdfException ex) when (ex.Code is not "PLUME3304")
        {
            FilterDiagnostics.ReportDeviation("PLUME3316", $"TIFF: frame at IFD offset {ifd.Offset} failed to decode ({ex.Message}); skipping it.", options, diagnostics, subject);
            return null;
        }

        var xDpi = ResolveDpi(ifd, TiffTag.XResolution);
        var yDpi = ResolveDpi(ifd, TiffTag.YResolution);

        return new TiffFrame(width, height, bitsPerSample, samplesPerPixel, photometric, colorMap, xDpi, yDpi, pixels);
    }

    private static double ResolveDpi(TiffIfd ifd, ushort resolutionTag)
    {
        var value = ifd.GetRational(resolutionTag, 0);
        if (value <= 0)
        {
            return 96; // The image->PDF default DPI fallback, reused here absent /ResolutionUnit=2 (inch) confirmation - centimeters (unit 3) are rare in the wild and this is a metadata best-effort, not a correctness concern.
        }

        var unit = ifd.GetInt(TiffTag.ResolutionUnit, 2);
        return unit == 3 ? value * 2.54 : value; // unit 3 = centimeters; 2 (default) = inches; 1 = "no absolute unit" also treated as-is.
    }

    // --- Strip-based frames -----------------------------------------------------------------

    private static byte[] DecodeStripped(ReadOnlySpan<byte> file, TiffIfd ifd, int width, int height, int samplesPerPixel, int bitsPerSample, int rowBytes, long compression, long fillOrder, long t4Options, long photometric, int predictor, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var offsets = ifd.GetInts(TiffTag.StripOffsets);
        var counts = ifd.GetInts(TiffTag.StripByteCounts);
        if (offsets.Length == 0 || counts.Length == 0)
        {
            throw new PlumePdfException("PLUME3315", "no StripOffsets/StripByteCounts.");
        }

        var rowsPerStrip = (int)ifd.GetInt(TiffTag.RowsPerStrip, height);
        if (rowsPerStrip <= 0)
        {
            rowsPerStrip = height;
        }

        var output = new byte[rowBytes * height];
        var stripCount = Math.Min(offsets.Length, counts.Length);

        for (var s = 0; s < stripCount; s++)
        {
            var stripFirstRow = s * rowsPerStrip;
            if (stripFirstRow >= height)
            {
                break;
            }

            var stripRows = Math.Min(rowsPerStrip, height - stripFirstRow);
            var offset = offsets[s];
            var count = counts[s];
            if (offset < 0 || count < 0 || offset + count > file.Length)
            {
                FilterDiagnostics.ReportDeviation("PLUME3316", $"TIFF: strip {s} (offset {offset}, {count} bytes) is out of range for the file; leaving those rows blank.", options, diagnostics, subject);
                continue;
            }

            var stripData = file.Slice((int)offset, (int)count);
            var decodedStrip = DecodeChunk(stripData, width, stripRows, samplesPerPixel, bitsPerSample, rowBytes, compression, fillOrder, t4Options, photometric, predictor, options, diagnostics, subject);
            var copyRows = Math.Min(stripRows, decodedStrip.Length / rowBytes);
            decodedStrip.AsSpan(0, copyRows * rowBytes).CopyTo(output.AsSpan(stripFirstRow * rowBytes, copyRows * rowBytes));
        }

        return output;
    }

    // --- Tiled frames ------------------------------------------------------------------------

    private static byte[] DecodeTiled(ReadOnlySpan<byte> file, TiffIfd ifd, int width, int height, int samplesPerPixel, int bitsPerSample, int rowBytes, long compression, long fillOrder, long t4Options, long photometric, int predictor, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var tileWidth = (int)ifd.GetInt(TiffTag.TileWidth, 0);
        var tileLength = (int)ifd.GetInt(TiffTag.TileLength, 0);
        var offsets = ifd.GetInts(TiffTag.TileOffsets);
        var counts = ifd.GetInts(TiffTag.TileByteCounts);
        if (tileWidth <= 0 || tileLength <= 0 || offsets.Length == 0)
        {
            throw new PlumePdfException("PLUME3315", "no TileWidth/TileLength/TileOffsets.");
        }

        var tilesAcross = (width + tileWidth - 1) / tileWidth;
        var tilesDown = (height + tileLength - 1) / tileLength;
        var tileRowBytes = ((tileWidth * samplesPerPixel * bitsPerSample) + 7) / 8;
        var output = new byte[rowBytes * height];

        var tileIndex = 0;
        for (var ty = 0; ty < tilesDown; ty++)
        {
            for (var tx = 0; tx < tilesAcross; tx++, tileIndex++)
            {
                if (tileIndex >= offsets.Length || tileIndex >= counts.Length)
                {
                    continue;
                }

                var offset = offsets[tileIndex];
                var count = counts[tileIndex];
                if (offset < 0 || count < 0 || offset + count > file.Length)
                {
                    FilterDiagnostics.ReportDeviation("PLUME3316", $"TIFF: tile {tileIndex} (offset {offset}, {count} bytes) is out of range for the file; leaving it blank.", options, diagnostics, subject);
                    continue;
                }

                var tileData = file.Slice((int)offset, (int)count);
                var decodedTile = DecodeChunk(tileData, tileWidth, tileLength, samplesPerPixel, bitsPerSample, tileRowBytes, compression, fillOrder, t4Options, photometric, predictor, options, diagnostics, subject);

                var pixelRowStart = ty * tileLength;
                var pixelColStart = tx * tileWidth;
                var rowsToCopy = Math.Min(tileLength, height - pixelRowStart);
                var bytesPerPixelGroup = (samplesPerPixel * bitsPerSample + 7) / 8; // only exact for byte-aligned samples; sub-byte tiles copy whole rows only when tile spans full width, the common case.
                var colBytes = Math.Min(tileRowBytes, rowBytes - ((pixelColStart * samplesPerPixel * bitsPerSample) / 8));

                for (var r = 0; r < rowsToCopy; r++)
                {
                    var srcRow = decodedTile.AsSpan(r * tileRowBytes, tileRowBytes);
                    var destByteOffset = ((pixelRowStart + r) * rowBytes) + ((pixelColStart * samplesPerPixel * bitsPerSample) / 8);
                    var n = Math.Min(colBytes, output.Length - destByteOffset);
                    if (n > 0)
                    {
                        srcRow[..n].CopyTo(output.AsSpan(destByteOffset, n));
                    }
                }

                _ = bytesPerPixelGroup;
            }
        }

        return output;
    }

    // --- Compression dispatch ----------------------------------------------------------------

    private static byte[] DecodeChunk(ReadOnlySpan<byte> data, int columns, int rows, int samplesPerPixel, int bitsPerSample, int rowBytes, long compression, long fillOrder, long t4Options, long photometric, int predictor, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        // LZW codes are always packed MSB-first regardless of FillOrder (TIFF 6.0 §13); every
        // other compression's raw bytes ARE subject to FillOrder=2 (LSB-first) and need
        // reversing before anything else touches them.
        var effective = compression == TiffCompression.Lzw || fillOrder != 2 ? data : ReverseBits(data);

        // CCITT-compressed TIFF strips carry no independent bit-polarity flag of their own
        // (unlike PDF's /BlackIs1): fax coding always decodes to the same canonical
        // white/black runs, and the frame's PhotometricInterpretation alone decides how those
        // display. Emit the packed samples in one FIXED convention - fax-black = 1, fax-white
        // = 0 (BlackIs1 = true) - which is exactly the WhiteIsZero sample layout the TIFF spec
        // expects for compressed fax data; the downstream sample-to-gray mapping then applies
        // the frame's declared photometric (WhiteIsZero inverts, BlackIsZero doesn't), matching
        // libtiff. A past bug: conditioning BlackIs1 on the photometric here applied the same
        // polarity flip TWICE (once at the engine, once in the gray mapping), so the two flips
        // cancelled and the tag was silently ignored - a BlackIsZero G4 frame decoded as its
        // photographic negative. Regression: RasterImageTests.Decode_CcittG4Tiff_*.
        const bool ccittBlackIs1 = true;

        byte[] decoded = compression switch
        {
            TiffCompression.None => effective.ToArray(),
            TiffCompression.PackBits => PackBitsDecode(effective, rowBytes * rows),
            TiffCompression.Lzw => LzwFilter.Decode(effective, earlyChange: true, options, diagnostics, subject),
            TiffCompression.Deflate or TiffCompression.DeflateAdobeAlias => FlateFilter.Decode(effective, options.MaxDecompressedStreamBytes, diagnostics, subject),
            TiffCompression.CcittMh => CcittFaxEngine.Decode(effective, new CcittFaxParameters(K: 0, Columns: columns, Rows: rows, BlackIs1: ccittBlackIs1), options, diagnostics, subject),
            TiffCompression.CcittG3 => CcittFaxEngine.Decode(
                effective,
                new CcittFaxParameters(
                    K: (t4Options & 0x1) != 0 ? 1 : 0, // T4Options bit 0: 0 = pure 1D (MH), 1 = mixed 1D/2D (each row tagged).
                    Columns: columns,
                    Rows: rows,
                    EncodedByteAlign: (t4Options & 0x4) != 0, // T4Options bit 2: fill bits added to byte-align each row (TIFF 6.0 §3, "T4Options").
                    BlackIs1: ccittBlackIs1),
                options, diagnostics, subject),
            TiffCompression.CcittG4 => CcittFaxEngine.Decode(effective, new CcittFaxParameters(K: -1, Columns: columns, Rows: rows, BlackIs1: ccittBlackIs1), options, diagnostics, subject),
            _ => throw new PlumePdfException("PLUME3310", $"TIFF: unsupported Compression value {compression}."),
        };

        if (predictor == 2 && bitsPerSample == 8 && compression != TiffCompression.None)
        {
            decoded = Predictor.Undo(decoded, predictor: 2, colors: samplesPerPixel, bitsPerComponent: bitsPerSample, columns: columns);
        }

        if (decoded.Length < rowBytes * rows)
        {
            var padded = new byte[rowBytes * rows];
            decoded.CopyTo(padded, 0);
            decoded = padded;
        }

        return decoded;
    }


    private static byte[] ReverseBits(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            result[i] = BitReverseTable[data[i]];
        }

        return result;
    }

    private static readonly byte[] BitReverseTable = BuildBitReverseTable();

    private static byte[] BuildBitReverseTable()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            byte b = (byte)i;
            byte r = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                r = (byte)((r << 1) | (b & 1));
                b >>= 1;
            }

            table[i] = r;
        }

        return table;
    }

    /// <summary>PackBits decode (TIFF 6.0 Apple Note): a run-length scheme over signed control bytes - <c>n in [0,127]</c> copies the next n+1 literal bytes, <c>n in [-127,-1]</c> repeats the following byte <c>1-n</c> times, and <c>-128</c> is a no-op.</summary>
    private static byte[] PackBitsDecode(ReadOnlySpan<byte> data, int expectedLength)
    {
        var output = new List<byte>(Math.Max(expectedLength, 16));
        var i = 0;
        while (i < data.Length)
        {
            var control = unchecked((sbyte)data[i++]);
            if (control >= 0)
            {
                var count = control + 1;
                for (var k = 0; k < count && i < data.Length; k++)
                {
                    output.Add(data[i++]);
                }
            }
            else if (control != -128)
            {
                if (i >= data.Length)
                {
                    break;
                }

                var repeatByte = data[i++];
                var count = 1 - control;
                for (var k = 0; k < count; k++)
                {
                    output.Add(repeatByte);
                }
            }

            // control == -128: no-op, per spec.
        }

        return [.. output];
    }
}
