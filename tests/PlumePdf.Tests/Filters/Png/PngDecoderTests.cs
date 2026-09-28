using PlumePdf.Filters.Png;
using Xunit;

namespace PlumePdf.Tests.Filters.Png;

/// <summary>
/// D1 conformance coverage for <see cref="PngDecoder"/>: every color type (0/2/3/4/6), a
/// representative bit depth per type (1/2/4/8/16 where the spec allows it), <c>tRNS</c>
/// transparency, <c>pHYs</c> DPI, Adam7 interlacing (checked against the same image's
/// non-interlaced encoding — they must decode pixel-identical), and every <c>PLUME325x</c>
/// coded refusal. Fixtures are built by <see cref="RawPngBuilder"/>, an independent
/// from-scratch chunk writer — never <see cref="PlumePdf.Filters.Png.PngEncoder"/> — so a
/// decoder bug and a matching encoder bug can't hide each other (see that type's remarks).
/// </summary>
public class PngDecoderTests
{
    private static byte[] Pack(int[] samples, int bitDepth)
    {
        if (bitDepth == 16)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                bytes[i * 2] = (byte)(samples[i] >> 8);
                bytes[(i * 2) + 1] = (byte)samples[i];
            }

            return bytes;
        }

        if (bitDepth == 8)
        {
            var bytes = new byte[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                bytes[i] = (byte)samples[i];
            }

            return bytes;
        }

        var samplesPerByte = 8 / bitDepth;
        var byteCount = (samples.Length + samplesPerByte - 1) / samplesPerByte;
        var packed = new byte[byteCount];
        for (var i = 0; i < samples.Length; i++)
        {
            var byteIndex = i / samplesPerByte;
            var withinByte = i % samplesPerByte;
            var shift = 8 - bitDepth - (withinByte * bitDepth);
            packed[byteIndex] |= (byte)(samples[i] << shift);
        }

        return packed;
    }

    [Fact]
    public void ColorType0_BitDepth8_DecodesToGray8()
    {
        var row0 = Pack([0, 64], 8);
        var row1 = Pack([128, 255], 8);
        var png = RawPngBuilder.Build(2, 2, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row0, row1]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(new byte[] { 0, 64, 128, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType0_BitDepth1_ExpandsToFullRange()
    {
        var row = Pack([1, 0, 1, 0], 1);
        var png = RawPngBuilder.Build(4, 1, 1, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
        Assert.Equal(new byte[] { 255, 0, 255, 0 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType0_BitDepth4_ScalesProportionally()
    {
        // 4-bit max sample is 15; PngDecoder scales raw*255/15, so 15 -> 255, 0 -> 0, 8 -> 136.
        var row = Pack([0, 8, 15], 4);
        var png = RawPngBuilder.Build(3, 1, 4, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(new byte[] { 0, 136, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType2_BitDepth8_DecodesToRgb24()
    {
        var row = Pack([10, 20, 30, 200, 210, 220], 8);
        var png = RawPngBuilder.Build(2, 1, 8, colorType: 2, RawPngBuilder.NoneFilterScanlines([row]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Rgb24, frame.Format);
        Assert.Equal(new byte[] { 10, 20, 30, 200, 210, 220 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType2_BitDepth16_DownsamplesToHighByte()
    {
        var row = Pack([0x1234, 0xABCD, 0xFFFF], 16);
        var png = RawPngBuilder.Build(1, 1, 16, colorType: 2, RawPngBuilder.NoneFilterScanlines([row]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(new byte[] { 0x12, 0xAB, 0xFF }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType3_PaletteWithoutTrns_DecodesToRgb24()
    {
        var palette = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 }; // red, green, blue
        var row = Pack([0, 1, 2], 8);
        var png = RawPngBuilder.Build(3, 1, 8, colorType: 3, RawPngBuilder.NoneFilterScanlines([row]), palette: palette);

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Rgb24, frame.Format);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType3_PaletteWithTrns_DecodesToRgba32()
    {
        var palette = new byte[] { 255, 0, 0, 0, 255, 0 }; // red, green
        var transparency = new byte[] { 128, 255 }; // red is half-transparent, green opaque
        var row = Pack([0, 1], 8);
        var png = RawPngBuilder.Build(2, 1, 8, colorType: 3, RawPngBuilder.NoneFilterScanlines([row]), palette: palette, transparency: transparency);

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);
        Assert.Equal(new byte[] { 255, 0, 0, 128, 0, 255, 0, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType0_WithMatchingTrns_MakesThatSampleTransparent()
    {
        var row = Pack([50, 100], 8);
        var transparency = new byte[] { 0, 50 }; // 16-bit-encoded gray sample value 50 is transparent
        var png = RawPngBuilder.Build(2, 1, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]), transparency: transparency);

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);
        var pixels = frame.Pixels.ToArray();
        Assert.Equal(0, pixels[3]); // first pixel (gray 50) is transparent
        Assert.Equal(255, pixels[7]); // second pixel (gray 100) stays opaque
    }

    [Fact]
    public void ColorType4_GrayAlpha_DecodesToRgba32()
    {
        var row = Pack([200, 100, 50, 255], 8); // gray=200,alpha=100 ; gray=50,alpha=255
        var png = RawPngBuilder.Build(2, 1, 8, colorType: 4, RawPngBuilder.NoneFilterScanlines([row]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);
        Assert.Equal(new byte[] { 200, 200, 200, 100, 50, 50, 50, 255 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void ColorType6_Rgba_DecodesToRgba32()
    {
        var row = Pack([1, 2, 3, 4, 5, 6, 7, 8], 8);
        var png = RawPngBuilder.Build(2, 1, 8, colorType: 6, RawPngBuilder.NoneFilterScanlines([row]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, frame.Pixels.ToArray());
    }

    [Fact]
    public void PhysChunk_PopulatesDpi()
    {
        var row = Pack([1], 8);
        var png = RawPngBuilder.Build(1, 1, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]), dpi: (300, 300));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.NotNull(frame.XDpi);
        Assert.NotNull(frame.YDpi);
        Assert.Equal(300, frame.XDpi!.Value, precision: 0);
        Assert.Equal(300, frame.YDpi!.Value, precision: 0);
    }

    [Fact]
    public void NoPhysChunk_LeavesDpiNull()
    {
        var row = Pack([1], 8);
        var png = RawPngBuilder.Build(1, 1, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]));

        var frame = PngDecoder.Decode(png, long.MaxValue);

        Assert.Null(frame.XDpi);
        Assert.Null(frame.YDpi);
    }

    [Fact]
    public void Adam7Interlaced_DecodesPixelIdenticalToNonInterlaced()
    {
        // An 8x8 gradient RGB image, encoded once plain and once Adam7-interlaced; both must
        // decode to the exact same pixel bytes.
        const int size = 8;
        var pixels = new byte[size * size * 3];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = ((y * size) + x) * 3;
                pixels[i] = (byte)(x * 30);
                pixels[i + 1] = (byte)(y * 30);
                pixels[i + 2] = (byte)((x + y) * 15);
            }
        }

        var plainRows = new byte[size][];
        for (var y = 0; y < size; y++)
        {
            plainRows[y] = pixels.AsSpan(y * size * 3, size * 3).ToArray();
        }

        var plainPng = RawPngBuilder.Build(size, size, 8, colorType: 2, RawPngBuilder.NoneFilterScanlines(plainRows));
        var interlacedPng = BuildAdam7Rgb8(pixels, size, size);

        var plainFrame = PngDecoder.Decode(plainPng, long.MaxValue);
        var interlacedFrame = PngDecoder.Decode(interlacedPng, long.MaxValue);

        Assert.Equal(RasterPixelFormat.Rgb24, interlacedFrame.Format);
        Assert.Equal(plainFrame.Pixels.ToArray(), interlacedFrame.Pixels.ToArray());
    }

    /// <summary>Builds an Adam7-interlaced 8-bit RGB PNG for <paramref name="pixels"/> — an independent re-implementation of the pass table <see cref="PngDecoder"/> itself decodes against, so this test doesn't just check the decoder agrees with itself.</summary>
    private static byte[] BuildAdam7Rgb8(byte[] pixels, int width, int height)
    {
        (int XStart, int YStart, int XStep, int YStep)[] passes =
        [
            (0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2),
        ];

        using var scanlines = new MemoryStream();
        foreach (var (xStart, yStart, xStep, yStep) in passes)
        {
            var passWidth = xStart >= width ? 0 : ((width - xStart + xStep - 1) / xStep);
            var passHeight = yStart >= height ? 0 : ((height - yStart + yStep - 1) / yStep);
            for (var py = 0; py < passHeight; py++)
            {
                scanlines.WriteByte(0); // filter type None
                var srcY = yStart + (py * yStep);
                for (var px = 0; px < passWidth; px++)
                {
                    var srcX = xStart + (px * xStep);
                    var srcIndex = ((srcY * width) + srcX) * 3;
                    scanlines.WriteByte(pixels[srcIndex]);
                    scanlines.WriteByte(pixels[srcIndex + 1]);
                    scanlines.WriteByte(pixels[srcIndex + 2]);
                }
            }
        }

        return RawPngBuilder.Build(width, height, 8, colorType: 2, scanlines.ToArray(), interlace: 1);
    }

    [Fact]
    public void InvalidSignature_ThrowsPlume3250()
    {
        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode([1, 2, 3, 4, 5, 6, 7, 8, 9], long.MaxValue));
        Assert.Equal("PLUME3250", ex.Code);
    }

    [Fact]
    public void MissingIhdr_ThrowsPlume3251()
    {
        var bytes = new byte[]
        {
            137, 80, 78, 71, 13, 10, 26, 10, // signature
            0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130, // IEND only
        };

        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(bytes, long.MaxValue));
        Assert.Equal("PLUME3251", ex.Code);
    }

    [Fact]
    public void UnsupportedBitDepthColorTypeCombination_ThrowsPlume3252()
    {
        var row = Pack([1], 4);
        var png = RawPngBuilder.Build(1, 1, 4, colorType: 2, RawPngBuilder.NoneFilterScanlines([row]));

        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(png, long.MaxValue));
        Assert.Equal("PLUME3252", ex.Code);
    }

    [Fact]
    public void PaletteColorTypeWithoutPlte_ThrowsPlume3253()
    {
        var row = Pack([0], 8);
        var png = RawPngBuilder.Build(1, 1, 8, colorType: 3, RawPngBuilder.NoneFilterScanlines([row]));

        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(png, long.MaxValue));
        Assert.Equal("PLUME3253", ex.Code);
    }

    [Fact]
    public void PixelCountExceedsCap_ThrowsPlume3255()
    {
        var row = Pack([1, 2], 8);
        var png = RawPngBuilder.Build(2, 1, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]));

        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(png, maxPixels: 1));
        Assert.Equal("PLUME3255", ex.Code);
    }

    [Fact]
    public void UnsupportedInterlaceMethod_ThrowsPlume3256()
    {
        var row = Pack([1], 8);
        var png = RawPngBuilder.Build(1, 1, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]), interlace: 2);

        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(png, long.MaxValue));
        Assert.Equal("PLUME3256", ex.Code);
    }

    [Fact]
    public void TruncatedIdat_ThrowsPlume3254()
    {
        // A 4x4 gray8 image whose IDAT payload is cut in half — the zlib stream still opens
        // (valid header, FlateFilter's own tolerant retry logic returns whatever decoded
        // before the cut per its own contract), but the resulting byte count is short of the
        // 4 full rows this image needs, which PngDecoder refuses rather than guess at a
        // partial scanline's predictor reconstruction.
        var fullRows = new byte[4][];
        for (var y = 0; y < 4; y++)
        {
            fullRows[y] = Pack([y, y, y, y], 8);
        }

        var full = RawPngBuilder.Build(4, 4, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines(fullRows));

        // Locate IDAT: signature (8) + IHDR chunk (4 length + 4 type + 13 data + 4 crc = 25).
        const int idatLengthOffset = 8 + 25;
        var idatDataLength = (full[idatLengthOffset] << 24) | (full[idatLengthOffset + 1] << 16) | (full[idatLengthOffset + 2] << 8) | full[idatLengthOffset + 3];
        var idatDataOffset = idatLengthOffset + 8; // past length(4) + type(4)
        var newIdatLength = idatDataLength / 2;

        var truncated = new byte[idatDataOffset + newIdatLength];
        Array.Copy(full, 0, truncated, 0, idatDataOffset + newIdatLength);
        // Rewrite IDAT's declared length to match the truncated data actually present.
        truncated[idatLengthOffset] = (byte)(newIdatLength >> 24);
        truncated[idatLengthOffset + 1] = (byte)(newIdatLength >> 16);
        truncated[idatLengthOffset + 2] = (byte)(newIdatLength >> 8);
        truncated[idatLengthOffset + 3] = (byte)newIdatLength;

        // Append a fake 4-byte CRC (unchecked by this decoder, see its remarks) plus IEND.
        byte[] fakeCrcThenIend = [0, 0, 0, 0, 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130];
        var withIend = new byte[truncated.Length + fakeCrcThenIend.Length];
        truncated.CopyTo(withIend, 0);
        fakeCrcThenIend.CopyTo(withIend, truncated.Length);

        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(withIend, long.MaxValue));
        Assert.Equal("PLUME3254", ex.Code);
    }

    [Fact]
    public void CorruptIdatZLibHeaderTripsZLibException_ThrowsPlume3254()
    {
        // A zlib header (CMF/FLG) with the FDICT bit (0x20) set but no dictionary ever
        // supplied trips a corrupt-stream shape that .NET's managed inflater surfaces as a
        // bare System.IO.Compression.ZLibException from Stream.Read — a type that derives
        // from IOException, NOT InvalidDataException, so FlateFilter's
        // catch (InvalidDataException) around that same Read call does not see it and it
        // escapes FlateFilter.Decode entirely uncoded (Finding 5). PngDecoder's own catch
        // around the FlateFilter.Decode call must translate it into the coded PLUME3254
        // rather than let a raw BCL exception reach RasterImage.Decode/Pdf.FromImages.
        // CMF=0x78 (deflate, 32K window), FLG=0x20 (FDICT set; (0x78*256+0x20) % 31 == 0,
        // satisfying the header's own checksum, so the corruption is only in the FDICT flag).
        byte[] rawIdat = [0x78, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var row = Pack([0], 8);
        var png = RawPngBuilder.Build(1, 1, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]), rawIdatOverride: rawIdat);

        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(png, long.MaxValue));
        Assert.Equal("PLUME3254", ex.Code);

        // System.IO.Compression.ZLibException is public at runtime but, oddly, not part of
        // the net8.0 reference-assembly surface used at compile time (confirmed empirically —
        // catchable only via its IOException base, never nameable directly in source), so the
        // inner-exception type is asserted by name/namespace via reflection instead of
        // Assert.IsType<ZLibException>.
        Assert.NotNull(ex.InnerException);
        Assert.Equal("System.IO.Compression.ZLibException", ex.InnerException!.GetType().FullName);
    }

    [Fact]
    public void OversizedIhdrWithTinyIdat_ThrowsBeforeAllocatingFullBuffer()
    {
        // A ~1KB (declared) IHDR of 11585x11585 (11585*11585 ≈ 134,212,225 pixels, just under
        // PdfOptions.MaxImagePixels' 1<<27 default of ~134,217,728) paired with a single-row
        // honestly-deflated IDAT: the pixel-count cap alone does not reject this input, so the
        // decoder must compare the inflated IDAT's length against the 11585-row requirement
        // BEFORE allocating the width*height*4 (~536MB) working RGBA buffer (the
        // caps-before-allocation rule). This test's oracle is that it throws the coded
        // PLUME3254 quickly rather than allocating hundreds of megabytes first.
        const int side = 11585;
        var row = Pack([0], 8); // one 1-byte gray8 pixel per row — nowhere near `side` rows.
        var png = RawPngBuilder.Build(side, side, 8, colorType: 0, RawPngBuilder.NoneFilterScanlines([row]));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(png, PngDecoder.DefaultMaxPixels));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("PLUME3254", ex.Code);
        // The refused-before-allocating RGBA buffer alone would be side*side*4 ≈ 536MB; a
        // generous 8MB ceiling proves that buffer was never allocated on the failing path.
        Assert.True(allocated < 8 * 1024 * 1024, $"expected the {(long)side * side * 4:N0}-byte RGBA buffer to never be allocated, but this thread allocated {allocated:N0} bytes before throwing.");
    }
}
