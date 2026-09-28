using PlumePdf.Filters;
using PlumePdf.Filters.Jpx;
using PlumePdf.Filters.Png;
using PlumePdf.Tests.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// Coverage for the public <see cref="RasterImage"/> facade: container sniffing and
/// dispatch to all four codecs (PNG, JPEG, TIFF, JPEG 2000; anything else is <c>PLUME3600</c>),
/// the bytes and path overloads, and <see cref="RasterImageFrame.EncodePng"/>/
/// <see cref="RasterImageFrame.EncodeJpeg"/>. JPEG 2000 coverage lives at the
/// bottom of the file, alongside its own committed fixtures under
/// <c>tests/PlumePdf.CorpusTests/Fixtures/jpx/</c>.
/// </summary>
public class RasterImageTests
{
    private static byte[] SmallPng()
    {
        var frame = new RasterImageFrame(new byte[] { 10, 20, 30, 40 }, 2, 2, RasterPixelFormat.Gray8);
        return PngEncoder.Encode(frame, PdfOptions.Default);
    }

    /// <summary>Hand-assembles a minimal, valid, uncompressed classic TIFF (one strip) — the same hand-crafted-bytes approach <c>TiffReaderTests</c> uses for its cycle/frame-cap fixtures, kept self-contained here rather than exposing those internal fields cross-file.</summary>
    private static byte[] BuildUncompressedTiff(int width, int height, int samplesPerPixel, long photometric, byte[] pixels, int bitsPerSample = 8)
    {
        var bytes = new List<byte>();
        bytes.AddRange("II"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes((ushort)42));
        bytes.AddRange(BitConverter.GetBytes((uint)8)); // first IFD at offset 8.

        var entries = new (ushort Tag, ushort Type, uint Count, uint Value)[]
        {
            (256, 4, 1, (uint)width), // ImageWidth (LONG)
            (257, 4, 1, (uint)height), // ImageLength (LONG)
            (258, 3, 1, (uint)bitsPerSample), // BitsPerSample (SHORT)
            (259, 3, 1, 1), // Compression: none (SHORT)
            (262, 3, 1, (uint)photometric), // PhotometricInterpretation (SHORT)
            (277, 3, 1, (uint)samplesPerPixel), // SamplesPerPixel (SHORT)
            (273, 4, 1, 0), // StripOffsets (LONG) — patched below once the pixel offset is known.
            (279, 4, 1, (uint)pixels.Length), // StripByteCounts (LONG)
        };

        var ifdSize = 2 + (entries.Length * 12) + 4;
        var pixelOffset = (uint)(8 + ifdSize);
        entries[6] = (273, 4, 1, pixelOffset);

        bytes.AddRange(BitConverter.GetBytes((ushort)entries.Length));
        foreach (var (tag, type, count, value) in entries)
        {
            bytes.AddRange(BitConverter.GetBytes(tag));
            bytes.AddRange(BitConverter.GetBytes(type));
            bytes.AddRange(BitConverter.GetBytes(count));
            if (type == 3) // SHORT values occupy the low 2 bytes of the 4-byte value field.
            {
                bytes.AddRange(BitConverter.GetBytes((ushort)value));
                bytes.AddRange(BitConverter.GetBytes((ushort)0));
            }
            else
            {
                bytes.AddRange(BitConverter.GetBytes(value));
            }
        }

        bytes.AddRange(BitConverter.GetBytes((uint)0)); // next IFD offset: none.
        bytes.AddRange(pixels);
        return [.. bytes];
    }

    [Fact]
    public void Decode_Png_ReturnsOneFrame()
    {
        var image = RasterImage.Decode(SmallPng());

        Assert.Single(image.Frames);
        Assert.Equal(2, image.Frames[0].Width);
        Assert.Equal(2, image.Frames[0].Height);
        Assert.Empty(image.Diagnostics);
    }

    [Fact]
    public void Decode_PathOverload_ReadsFileAndDecodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-rasterimage-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, SmallPng());
            var image = RasterImage.Decode(path);
            Assert.Single(image.Frames);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Decode_MissingFile_ThrowsPlume3613()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"plumepdf-does-not-exist-{Guid.NewGuid():N}.png");
        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(missing));
        Assert.Equal("PLUME3613", ex.Code);
    }

    [Fact]
    public void Decode_Jpeg_DispatchesToJpegDecoder()
    {
        var jpeg = JpegEncoder.Encode(new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120 }, width: 4, height: 3, componentCount: 1, quality: 90);
        var image = RasterImage.Decode(jpeg);

        Assert.Single(image.Frames);
        var frame = image.Frames[0];
        Assert.Equal(4, frame.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
    }

    [Fact]
    public void Decode_CmykJpeg_ConvertsToRgb24()
    {
        // Adobe APP14-marked (present -> stored inverted, per convention): a stored byte of 0 on
        // every channel un-inverts to full ink (255) on every channel, which is pure black once
        // the CMYK->RGB formula runs.
        var jpeg = Jpeg.CmykJpegFixture.BuildFlatColor(0, 0, 0, 0);
        var image = RasterImage.Decode(jpeg);

        var frame = image.Frames[0];
        Assert.Equal(RasterPixelFormat.Rgb24, frame.Format);
        Assert.Equal(new byte[] { 0, 0, 0 }, frame.Pixels.ToArray()[..3]);
    }

    [Fact]
    public void Decode_TiffLittleEndianSignature_DispatchesToTiffDecoder()
    {
        var pixels = new byte[] { 10, 20, 30, 40 };
        var tiff = BuildUncompressedTiff(2, 2, samplesPerPixel: 1, photometric: 1 /* BlackIsZero */, pixels);
        var image = RasterImage.Decode(tiff);

        Assert.Single(image.Frames);
        var frame = image.Frames[0];
        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
        Assert.Equal(pixels, frame.Pixels.ToArray());
    }

    [Fact]
    public void Decode_TiffRgb_ProducesRgb24Frame()
    {
        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }; // 2x2 RGB.
        var tiff = BuildUncompressedTiff(2, 2, samplesPerPixel: 3, photometric: 2 /* RGB */, pixels);
        var image = RasterImage.Decode(tiff);

        var frame = image.Frames[0];
        Assert.Equal(RasterPixelFormat.Rgb24, frame.Format);
        Assert.Equal(pixels, frame.Pixels.ToArray());
    }

    /// <summary>
    /// Finding 3: a well-formed 12-bit grayscale TIFF is a real format (medical/scientific
    /// scanner output), not corruption — before the fix, <c>TiffReadSample</c>'s
    /// <c>samplesPerByte = 8 / bitDepth</c> divided by zero for any depth outside {8,16}.
    /// A single-frame source's only frame failing is the "nothing left to return" throw
    /// (<c>PLUME3603</c>), whose message names the underlying per-frame refusal
    /// (<c>PLUME3317</c>) rather than losing it.
    /// </summary>
    [Fact]
    public void Decode_Tiff12BitGrayscale_ThrowsCodedRefusalNotDivideByZero()
    {
        var pixels = new byte[6]; // rowBytes = ceil(2*1*12/8) = 3, * height 2 = 6.
        var tiff = BuildUncompressedTiff(2, 2, samplesPerPixel: 1, photometric: 1 /* BlackIsZero */, pixels, bitsPerSample: 12);

        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(tiff));

        Assert.Equal("PLUME3603", ex.Code);
        Assert.Contains("PLUME3317", ex.Message);
    }

    /// <summary>Same as <see cref="Decode_Tiff12BitGrayscale_ThrowsCodedRefusalNotDivideByZero"/> for a 32-bit (e.g. float) depth — also outside {8,16}, also a division-by-zero before the fix.</summary>
    [Fact]
    public void Decode_Tiff32BitGrayscale_ThrowsCodedRefusalNotDivideByZero()
    {
        var pixels = new byte[16]; // rowBytes = ceil(2*1*32/8) = 8, * height 2 = 16.
        var tiff = BuildUncompressedTiff(2, 2, samplesPerPixel: 1, photometric: 1 /* BlackIsZero */, pixels, bitsPerSample: 32);

        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(tiff));

        Assert.Equal("PLUME3603", ex.Code);
        Assert.Contains("PLUME3317", ex.Message);
    }

    /// <summary>Under <see cref="PdfOptions.Strict"/>, the per-frame diagnostic upgrades to a direct throw — this frame is the very first PLUME33xx-coded refusal encountered, so it surfaces as its own code rather than being folded into PLUME3603's aggregate message.</summary>
    [Fact]
    public void Decode_Tiff12BitGrayscale_Strict_ThrowsPlume3317Directly()
    {
        var pixels = new byte[6];
        var tiff = BuildUncompressedTiff(2, 2, samplesPerPixel: 1, photometric: 1, pixels, bitsPerSample: 12);

        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(tiff, PdfOptions.Default with { Strict = true }));

        Assert.Equal("PLUME3317", ex.Code);
    }

    /// <summary>Finding 3: SamplesPerPixel must match the frame's PhotometricInterpretation — RGB (photometric 2) with only 1 sample/pixel would misindex <c>ConvertTiffFrame</c>'s packed-row math instead of decoding correctly.</summary>
    [Fact]
    public void Decode_TiffRgbPhotometricWithMismatchedSamplesPerPixel_ThrowsCodedRefusal()
    {
        var pixels = new byte[4]; // rowBytes = ceil(2*1*8/8) = 2, * height 2 = 4.
        var tiff = BuildUncompressedTiff(2, 2, samplesPerPixel: 1, photometric: 2 /* RGB */, pixels);

        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(tiff));

        Assert.Equal("PLUME3603", ex.Code);
        Assert.Contains("PLUME3318", ex.Message);
    }

    /// <summary>
    /// Follow-up to finding 3: a frame whose width*height passes the pixel-count cap but whose
    /// packed row stride (width*samplesPerPixel*bitsPerSample) overflows a 32-bit int must be
    /// refused up front in <c>TiffFrameDecoder</c> (PLUME3319) — before the int narrowing that
    /// would otherwise corrupt the buffer allocation. Built as a bomb: a 300M-wide, 1-row RGBA
    /// 16-bit frame declared with a tiny strip, under a raised pixel cap so the stride guard is
    /// the one that fires.
    /// </summary>
    [Fact]
    public void Decode_TiffRowStrideOverflowsInt32_RefusesBeforeAllocatingUnderStrict()
    {
        var pixels = new byte[8]; // tiny declared strip — the guard fires before any read.
        var tiff = BuildUncompressedTiff(300_000_000, 1, samplesPerPixel: 4, photometric: 2 /* RGB */, pixels, bitsPerSample: 16);

        var options = PdfOptions.Default with { Strict = true, MaxImagePixels = 1_000_000_000 };
        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(tiff, options));

        Assert.Equal("PLUME3319", ex.Code);
    }

    [Fact]
    public void Decode_UnrecognizedContainer_ThrowsPlume3600()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8];
        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(garbage));
        Assert.Equal("PLUME3600", ex.Code);
    }

    [Fact]
    public void FrameEncodePng_RoundTripsPixelIdentical()
    {
        var image = RasterImage.Decode(SmallPng());
        var reencoded = image.Frames[0].EncodePng();
        var reDecoded = RasterImage.Decode(reencoded);

        Assert.Equal(image.Frames[0].Pixels.ToArray(), reDecoded.Frames[0].Pixels.ToArray());
    }

    [Fact]
    public void FrameEncodeJpeg_RoundTripsThroughJpegDecoder()
    {
        var image = RasterImage.Decode(SmallPng());
        var jpeg = image.Frames[0].EncodeJpeg();
        var reDecoded = RasterImage.Decode(jpeg);

        Assert.Equal(2, reDecoded.Frames[0].Width);
        Assert.Equal(2, reDecoded.Frames[0].Height);
    }

    // Regression fixtures: one 64x40 CCITT Group 4 payload (Pillow 11.3.0 / libtiff,
    // the independent writer — a white background with a black rectangle at (16,10)-(48,30),
    // drawn under the BlackIsZero reading), byte-committed twice with ONLY the
    // PhotometricInterpretation tag differing (1 = BlackIsZero as Pillow wrote it; 0 =
    // WhiteIsZero, the same bytes with tag 262 flipped). libtiff ground truth: the two files
    // decode to photometric inverses of each other — BlackIsZero shows the drawn image
    // (white corner, black rectangle), WhiteIsZero shows its negative. Before the fix the
    // CCITT path applied the polarity twice (BlackIs1 at the fax engine AND the WhiteIsZero
    // invert in the gray mapping), so both files decoded identically — the tag was ignored.
    private static readonly byte[] CcittG4BlackIsZeroTiff = Convert.FromHexString(
        "49492A002200000026A0786FFFFFC82E37FFFFFFFFFFFFFFFFFFF8FFFFF00100100009000001030001000000400000000101030001000000280000000201030001000000010000000301030001000000040000000601030001000000010000001101040001000000080000001601030001000000280000001701040001000000190000001C010300010000000100000000000000");

    private static readonly byte[] CcittG4WhiteIsZeroTiff = Convert.FromHexString(
        "49492A002200000026A0786FFFFFC82E37FFFFFFFFFFFFFFFFFFF8FFFFF00100100009000001030001000000400000000101030001000000280000000201030001000000010000000301030001000000040000000601030001000000000000001101040001000000080000001601030001000000280000001701040001000000190000001C010300010000000100000000000000");

    /// <summary>
    /// A CCITT G4 TIFF's <c>PhotometricInterpretation</c> must control display
    /// polarity — <c>BlackIsZero (1)</c> files decoded photometrically inverted (and the tag
    /// was ignored entirely: both polarities produced identical output), silently, because the
    /// fax-engine <c>BlackIs1</c> flag and the gray-mapping's WhiteIsZero invert each applied
    /// the same polarity flip and cancelled. Ground truth pinned against libtiff (Pillow),
    /// the fixture's own writer.
    /// </summary>
    [Theory]
    [InlineData(true, 255, 0)] // BlackIsZero: white corner, black rectangle (the drawn image).
    [InlineData(false, 0, 255)] // WhiteIsZero: same payload reads as the photographic negative.
    public void Decode_CcittG4Tiff_HonorsPhotometricPolarity(bool blackIsZero, byte expectedCorner, byte expectedRectCenter)
    {
        var image = RasterImage.Decode(blackIsZero ? CcittG4BlackIsZeroTiff : CcittG4WhiteIsZeroTiff);
        var frame = image.Frames[0];

        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
        Assert.Equal(64, frame.Width);
        Assert.Equal(40, frame.Height);
        Assert.Equal(expectedCorner, frame.Pixels.Span[0]); // (0,0)
        Assert.Equal(expectedRectCenter, frame.Pixels.Span[20 * 64 + 32]); // (32,20), inside the rectangle
    }

    /// <summary>
    /// The strongest form of the polarity contract: the two fixtures share one G4
    /// payload and differ only in tag 262, so a correct decoder MUST produce exact photographic
    /// negatives — the pre-fix decoder produced byte-identical output for both.
    /// </summary>
    [Fact]
    public void Decode_CcittG4Tiff_OppositePhotometricsDecodeAsExactNegatives()
    {
        var black = RasterImage.Decode(CcittG4BlackIsZeroTiff).Frames[0].Pixels.Span;
        var white = RasterImage.Decode(CcittG4WhiteIsZeroTiff).Frames[0].Pixels.Span;

        Assert.Equal(black.Length, white.Length);
        for (var i = 0; i < black.Length; i++)
        {
            Assert.True(black[i] == (byte)(255 - white[i]), $"Pixel {i}: BlackIsZero={black[i]}, WhiteIsZero={white[i]} — not photographic negatives; the photometric tag is being ignored on the CCITT path.");
        }
    }

    // ---- JPEG 2000 ----

    private static string JpxFixturesDirectory => Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "jpx");

    private static byte[] ReadJpxFixture(string name) => File.ReadAllBytes(Path.Combine(JpxFixturesDirectory, name));

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void Decode_RawJ2kCodestream_DecodesAsGray8()
    {
        var image = RasterImage.Decode(ReadJpxFixture("baseline-53.j2k"));
        var frame = image.Frames[0];

        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
        Assert.Equal(64, frame.Width);
        Assert.Equal(48, frame.Height);
    }

    [Fact]
    public void Decode_Jp2WrappedCodestream_DecodesTheSamePixelsAsTheRawCodestream()
    {
        var fromJp2 = RasterImage.Decode(ReadJpxFixture("baseline-53-jp2.jp2")).Frames[0];
        var fromJ2k = RasterImage.Decode(ReadJpxFixture("baseline-53.j2k")).Frames[0];

        Assert.Equal(fromJ2k.Format, fromJp2.Format);
        Assert.Equal(fromJ2k.Width, fromJp2.Width);
        Assert.Equal(fromJ2k.Height, fromJp2.Height);
        Assert.True(fromJ2k.Pixels.Span.SequenceEqual(fromJp2.Pixels.Span));
    }

    [Fact]
    public void Decode_ResMetadataFixture_DpiIs300()
    {
        // MANIFEST.json: res-metadata.jp2 pins 300 dpi (11811 px/m) via a `res `/`resd` box;
        // RasterImage.Decode converts pixels-per-metre to DPI via * 0.0254.
        var frame = RasterImage.Decode(ReadJpxFixture("res-metadata.jp2")).Frames[0];

        Assert.NotNull(frame.XDpi);
        Assert.NotNull(frame.YDpi);
        Assert.Equal(300.0, frame.XDpi!.Value, precision: 0);
        Assert.Equal(300.0, frame.YDpi!.Value, precision: 0);
    }

    [Fact]
    public void Decode_FixtureWithoutResBox_LeavesDpiUnset()
    {
        var frame = RasterImage.Decode(ReadJpxFixture("baseline-53.j2k")).Frames[0];

        Assert.Null(frame.XDpi);
        Assert.Null(frame.YDpi);
    }

    [Fact]
    public void Decode_GarbageThatDoesNotSniffAsJpx_StillThrowsPlume3600()
    {
        // Neither the JP2 signature box nor the raw FF 4F FF 51 marker -- falls through to the
        // same PLUME3600 every other unrecognized container hits (IsJpx returns false).
        byte[] garbage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(garbage));

        Assert.Equal("PLUME3600", ex.Code);
    }

    [Fact]
    public void Decode_MaxImagePixelsBelowFixtureSize_ThrowsPlume3718()
    {
        // baseline-53.j2k is 64x48 = 3072 reference-grid pixels (MANIFEST.json); a cap of 100
        // must refuse it via the decoder's own budget check, reachable from this public facade.
        var limited = PdfOptions.Default with { MaxImagePixels = 100 };

        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(ReadJpxFixture("baseline-53.j2k"), limited));

        Assert.Equal(JpxDiagnosticCodes.ImageTooLarge, ex.Code);
    }

    [Fact]
    public void Decode_TwelveBitFixture_FoldsToEightBitsPerChannel()
    {
        // RasterPixelFormat has no >8-bit representation; higher precision is folded down via
        // the full-scale rescale (JpxImage.ToInterleaved8Bit), never truncated to a wider
        // RasterImageFrame that doesn't exist.
        var frame = RasterImage.Decode(ReadJpxFixture("depth-12u.j2k")).Frames[0];

        Assert.Equal(RasterPixelFormat.Gray8, frame.Format);
        Assert.Equal(64 * 48, frame.Pixels.Length);
    }

    [Fact]
    public void Decode_PremultipliedAlphaFixture_KeepsAlphaAsRgba32()
    {
        var frame = RasterImage.Decode(ReadJpxFixture("premultiplied-alpha.jp2")).Frames[0];

        Assert.Equal(RasterPixelFormat.Rgba32, frame.Format);
    }

    /// <summary>
    /// A legal codestream whose colour-channel count is outside
    /// {1, 3, 4} — a raw <c>.j2k</c> gray+alpha pair with no <c>cdef</c> box (2 colour channels;
    /// built with <c>opj_compress -i two.raw -o two.j2k -F 64,64,2,8,u -r 1</c>), or a
    /// five-band capture — has no home in this facade's three pixel formats. It must refuse
    /// with the coded <c>PLUME3604</c>, never let <see cref="RasterImageFrame"/>'s constructor
    /// throw a BCL <see cref="ArgumentException"/> out of a public method over well-formed input
    /// (the exception policy in AGENTS.md). Built in-test with <see cref="J2kBuilder"/>: a 16x16
    /// stream of <paramref name="channels"/> 8-bit components whose body is one empty packet (a single
    /// zero bit, byte-aligned) per (layer, resolution, component) — <c>N_L = 1</c> gives two
    /// resolutions, hence <c>2 * channels</c> zero bytes — a legal, cleanly decodable all-zero
    /// image, as the first assertion proves before the facade is exercised.
    /// </summary>
    /// <summary>
    /// A 4-colour-channel (CMYK) JP2 with a <c>cdef</c> opacity
    /// channel has no home for its alpha in this facade (no format carries CMYK-derived RGB plus
    /// alpha), so the colour decodes to <see cref="RasterPixelFormat.Rgb24"/> and the opacity is
    /// dropped — recorded as an <c>Info</c> <c>PLUME3604</c> on <see cref="RasterImage.Diagnostics"/>,
    /// never silently. Built in-test: a five-component <see cref="J2kBuilder"/> codestream (one
    /// empty packet per resolution per component — a legal all-zero image) wrapped with
    /// <c>colr</c> EnumCS 12 and a <c>cdef</c> marking component 4 as opacity.
    /// </summary>
    [Fact]
    public void Decode_JpxCmykPlusOpacity_DecodesRgb24_AndRecordsInfoThatOpacityWasDropped()
    {
        var codestream = new J2kBuilder { Csiz = 5, Body = new byte[2 * 5] }.BuildCodestream();
        var jp2 = J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(16, 16, 5, 7), J2kBuilder.ColrEnum(12), J2kBuilder.Cdef((4, 1, 0)));

        var image = RasterImage.Decode(jp2);

        var frame = image.Frames[0];
        Assert.Equal(RasterPixelFormat.Rgb24, frame.Format);
        Assert.Equal(16 * 16 * 3, frame.Pixels.Length);
        var note = Assert.Single(image.Diagnostics);
        Assert.Equal("PLUME3604", note.Code);
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Contains("opacity channel was dropped", note.Message, StringComparison.Ordinal);
    }

    /// <summary>The control for the test above: the same CMYK codestream WITHOUT a <c>cdef</c> opacity channel records nothing — the Info is about the dropped alpha, not about CMYK.</summary>
    [Fact]
    public void Decode_JpxCmykWithoutOpacity_RecordsNoDiagnostic()
    {
        var codestream = new J2kBuilder { Csiz = 4, Body = new byte[2 * 4] }.BuildCodestream();
        var jp2 = J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(16, 16, 4, 7), J2kBuilder.ColrEnum(12));

        var image = RasterImage.Decode(jp2);

        Assert.Equal(RasterPixelFormat.Rgb24, image.Frames[0].Format);
        Assert.Empty(image.Diagnostics);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void Decode_JpxWithUnsupportedColourChannelCount_ThrowsPlume3604(int channels)
    {
        var codestream = new J2kBuilder { Csiz = channels, Body = new byte[2 * channels] }.BuildCodestream();

        var diagnostics = new DiagnosticCollection();
        var decoded = JpxImageDecoder.Decode(codestream, PdfOptions.Default, diagnostics);
        Assert.Equal(channels, decoded.ColourChannelCount);
        Assert.Empty(diagnostics); // sanity: the input is a legal codestream, so this is the facade's own refusal, not a codec one.

        var ex = Assert.Throws<PlumePdfException>(() => RasterImage.Decode(codestream));

        Assert.Equal("PLUME3604", ex.Code);
        Assert.Contains($"carries {channels} colour channel(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 (gray), 3 (RGB), or 4 (CMYK)", ex.Message, StringComparison.Ordinal);
    }
}
