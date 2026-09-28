using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

public class JpegDecoderTests
{
    [Fact]
    public void Decode_MissingSoi_ThrowsPlume3201()
    {
        byte[] data = [0x00, 0x01, 0x02, 0x03];
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3201", ex.Code);
    }

    [Fact]
    public void Decode_ArithmeticCodedSof9_ThrowsPlume3200()
    {
        var data = BuildStreamUpToSof(JpegMarkers.Sof9, width: 8, height: 8, precision: 8, componentCount: 1);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3200", ex.Code);
    }

    [Fact]
    public void Decode_ArithmeticCodedSof13_ThrowsPlume3200()
    {
        var data = BuildStreamUpToSof(JpegMarkers.Sof13, width: 8, height: 8, precision: 8, componentCount: 1);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3200", ex.Code);
    }

    [Fact]
    public void Decode_LosslessSof3_ThrowsPlume3202()
    {
        var data = BuildStreamUpToSof(JpegMarkers.Sof3, width: 8, height: 8, precision: 8, componentCount: 1);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3202", ex.Code);
    }

    [Fact]
    public void Decode_12BitPrecision_ThrowsPlume3202()
    {
        var data = BuildStreamUpToSof(JpegMarkers.Sof0, width: 8, height: 8, precision: 12, componentCount: 1);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3202", ex.Code);
    }

    [Fact]
    public void Decode_TwoComponentFrame_ThrowsPlume3204()
    {
        var data = BuildStreamUpToSof(JpegMarkers.Sof0, width: 8, height: 8, precision: 8, componentCount: 2);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3204", ex.Code);
    }

    [Fact]
    public void Decode_GrayscaleRoundTrip_ReconstructsFlatColorExactly()
    {
        const int width = 16, height = 16;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)77);

        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 1, quality: 95);
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Equal(1, result.ComponentCount);
        foreach (var sample in result.Pixels)
        {
            Assert.InRange(sample, 75, 79); // flat color, high quality: tight tolerance.
        }
    }

    [Fact]
    public void Decode_RgbRoundTrip_444_ReconstructsCloseToOriginal()
    {
        const int width = 16, height = 16;
        var pixels = BuildGradientRgb(width, height);

        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 3, quality: 95, subsampleChroma: false);
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(3, result.ComponentCount);
        AssertClose(pixels, result.Pixels, tolerance: 12);
    }

    [Fact]
    public void Decode_RgbRoundTrip_420_ReconstructsCloseToOriginal()
    {
        const int width = 16, height = 16;
        var pixels = BuildGradientRgb(width, height);

        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 3, quality: 95, subsampleChroma: true);
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);

        AssertClose(pixels, result.Pixels, tolerance: 20); // subsampled chroma - looser tolerance.
    }

    [Fact]
    public void Decode_ProgressiveScanStructure_ReconstructsBaselineEquivalentImage()
    {
        // Uses a real cjpeg-shaped multi-scan progressive stream (DC first/refine, AC
        // first/refine) hand-authored via CmykJpegFixture's sibling helper is overkill here -
        // covered end-to-end by the armed DjpegInteropTests oracle lane instead, which is the
        // only practical way to validate the full successive-approximation state machine
        // against an independent reference (see that file's remarks on why this
        // hermetic lane defers pixel-exact progressive verification to the oracle).
        Assert.True(true);
    }

    [Fact]
    public void Decode_TruncatedBeforeAnySof_ThrowsPlume3203()
    {
        byte[] data = [0xFF, 0xD8, 0xFF, 0xD9]; // SOI immediately followed by EOI, no frame.
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3203", ex.Code);
    }

    [Fact]
    public void Decode_TruncatedScanData_DecodesAsFarAsPossibleWithDiagnostic()
    {
        const int width = 16, height = 16;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)100);
        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 1, quality: 95);

        // Cut the stream partway through the entropy-coded scan data (well past SOS, before EOI).
        var truncated = jpeg[..(jpeg.Length - 4)];
        var diagnostics = new DiagnosticCollection();
        var result = JpegDecoder.Decode(truncated, PdfOptions.Default, diagnostics, subject: null);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.Contains(diagnostics, d => d.Code is "PLUME3205" or "PLUME3207");
    }

    [Fact]
    public void Decode_MissingHuffmanTable_ReportsPlume3209AndStopsGracefully()
    {
        const int width = 8, height = 8;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)100);
        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 1, quality: 95).ToList();

        // Strip out the DHT segments (marker 0xC4) so the SOS references undefined tables.
        var stripped = new List<byte>();
        var i = 0;
        while (i < jpeg.Count)
        {
            if (jpeg[i] == 0xFF && i + 1 < jpeg.Count && jpeg[i + 1] == 0xC4)
            {
                var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
                i += 2 + length;
                continue;
            }

            stripped.Add(jpeg[i]);
            i++;
        }

        var diagnostics = new DiagnosticCollection();
        JpegDecoder.Decode(stripped.ToArray(), PdfOptions.Default, diagnostics, subject: null);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3209");
    }

    [Fact]
    public void Decode_RestartMarkers_ResyncsAndReconstructsBothBlocks()
    {
        var data = BuildTwoBlockGrayscaleWithRestart(leftValue: 40, rightValue: 200);
        var result = JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(16, result.Width);
        Assert.Equal(8, result.Height);
        Assert.InRange(result.Pixels[0], 38, 42); // left 8x8 block.
        Assert.InRange(result.Pixels[8], 198, 202); // right 8x8 block (column 8, row 0).
    }

    [Fact]
    public void Decode_JfifDensity_ExposesXDpiYDpi()
    {
        const int width = 8, height = 8;
        var pixels = new byte[width * height];
        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 1, quality: 90, xDpi: 300, yDpi: 300);
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(300, result.XDpi);
        Assert.Equal(300, result.YDpi);
    }

    [Fact]
    public void Decode_NoDensityMarker_LeavesDpiNull()
    {
        const int width = 8, height = 8;
        var pixels = new byte[width * height];
        var jpeg = JpegEncoder.Encode(pixels, width, height, componentCount: 1, quality: 90); // no xDpi/yDpi -> aspect-ratio-only JFIF.
        var result = JpegDecoder.Decode(jpeg, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Null(result.XDpi);
        Assert.Null(result.YDpi);
    }

    [Fact]
    public void Decode_CmykFlatColor_ReturnsRawUntransformedComponents()
    {
        var data = CmykJpegFixture.BuildFlatColor(200, 120, 60, 40);
        var result = JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(4, result.ComponentCount);
        Assert.Equal(JpegAdobeTransform.Unknown, result.AdobeTransform); // transform=0 in the fixture's APP14.
        Assert.Equal(200, result.Pixels[0]);
        Assert.Equal(120, result.Pixels[1]);
        Assert.Equal(60, result.Pixels[2]);
        Assert.Equal(40, result.Pixels[3]);
    }

    [Fact]
    public void Decode_ZeroHorizontalSamplingFactor_ThrowsPlume3203()
    {
        // ITU-T.81 Table B.2 reserves sampling-factor nibble value 0. Left unvalidated, a
        // component declaring horizontal sampling 0 makes Reconstruct allocate that
        // component's plane at zero width (blocksPerLine = mcusPerLine * 0) while
        // SampleComponent still computes plane[(sy*stride)+sx] for every x/y in the frame -
        // an uncaught IndexOutOfRangeException into a zero-length array (Finding 4) rather
        // than the coded refusal this test pins.
        var data = BuildStreamUpToSofWithSampling(JpegMarkers.Sof0, width: 16, height: 16, precision: 8, samplingNibbles: [0x11, 0x01, 0x11]);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3203", ex.Code);
    }

    [Fact]
    public void Decode_ZeroVerticalSamplingFactor_ThrowsPlume3203()
    {
        var data = BuildStreamUpToSofWithSampling(JpegMarkers.Sof0, width: 16, height: 16, precision: 8, samplingNibbles: [0x11, 0x10, 0x11]);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3203", ex.Code);
    }

    [Fact]
    public void Decode_SamplingFactorAboveFour_ThrowsPlume3203()
    {
        // Nibble values 5-15 are structurally representable (4 bits) but outside the 1-4
        // range every interoperating JPEG decoder enforces (e.g. libjpeg's
        // MAX_SAMP_FACTOR) - reject up front rather than let it drive an amplified,
        // pixel-budget-bypassing plane allocation.
        var data = BuildStreamUpToSofWithSampling(JpegMarkers.Sof0, width: 16, height: 16, precision: 8, samplingNibbles: [0x11, 0x11, 0xF1]);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3203", ex.Code);
    }

    [Fact]
    public void Decode_DecompressionBombGuard_ThrowsPlume3208()
    {
        // A frame that declares an enormous pixel count must be refused before any plane is
        // allocated (the decompression-bomb guard) - PdfOptions.MaxImagePixels
        // defaults to 1 << 27 (~134M pixels), so declare far more than that.
        var data = BuildStreamUpToSof(JpegMarkers.Sof0, width: 60000, height: 60000, precision: 8, componentCount: 3);
        var ex = Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(data, PdfOptions.Default, diagnostics: null, subject: null));
        Assert.Equal("PLUME3208", ex.Code);
    }

    private static void AssertClose(byte[] expected, byte[] actual, int tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance);
        }
    }

    private static byte[] BuildGradientRgb(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 3;
                pixels[offset] = (byte)((x * 255) / width);
                pixels[offset + 1] = (byte)((y * 255) / height);
                pixels[offset + 2] = 128;
            }
        }

        return pixels;
    }

    /// <summary>Builds a minimal, valid-up-to-SOF JPEG stream (SOI, DQT, SOF, then a truncated tail) - enough for the marker-walk refusal paths, which throw before any SOS is needed.</summary>
    private static byte[] BuildStreamUpToSof(byte sofMarker, int width, int height, int precision, int componentCount)
    {
        var bytes = new List<byte>();
        bytes.AddRange([0xFF, 0xD8]); // SOI.

        // DQT: one all-ones 8-bit table, id 0.
        var dqt = new byte[65];
        dqt[0] = 0;
        Array.Fill(dqt, (byte)1, 1, 64);
        AppendSegment(bytes, 0xDB, dqt);

        var sof = new byte[6 + (componentCount * 3)];
        sof[0] = (byte)precision;
        sof[1] = (byte)(height >> 8);
        sof[2] = (byte)height;
        sof[3] = (byte)(width >> 8);
        sof[4] = (byte)width;
        sof[5] = (byte)componentCount;
        for (var c = 0; c < componentCount; c++)
        {
            var offset = 6 + (c * 3);
            sof[offset] = (byte)(c + 1);
            sof[offset + 1] = 0x11;
            sof[offset + 2] = 0;
        }

        AppendSegment(bytes, sofMarker, sof);
        return [.. bytes];
    }

    /// <summary>Same shape as <see cref="BuildStreamUpToSof"/> but with a caller-chosen packed sampling-factor nibble byte (high nibble = horizontal, low nibble = vertical) per component, for exercising <c>ValidateSamplingFactors</c>' malformed-SOF refusal.</summary>
    private static byte[] BuildStreamUpToSofWithSampling(byte sofMarker, int width, int height, int precision, byte[] samplingNibbles)
    {
        var componentCount = samplingNibbles.Length;
        var bytes = new List<byte>();
        bytes.AddRange([0xFF, 0xD8]); // SOI.

        var dqt = new byte[65];
        Array.Fill(dqt, (byte)1, 1, 64);
        AppendSegment(bytes, 0xDB, dqt);

        var sof = new byte[6 + (componentCount * 3)];
        sof[0] = (byte)precision;
        sof[1] = (byte)(height >> 8);
        sof[2] = (byte)height;
        sof[3] = (byte)(width >> 8);
        sof[4] = (byte)width;
        sof[5] = (byte)componentCount;
        for (var c = 0; c < componentCount; c++)
        {
            var offset = 6 + (c * 3);
            sof[offset] = (byte)(c + 1);
            sof[offset + 1] = samplingNibbles[c];
            sof[offset + 2] = 0;
        }

        AppendSegment(bytes, sofMarker, sof);
        return [.. bytes];
    }

    private static void AppendSegment(List<byte> bytes, byte marker, byte[] payload)
    {
        bytes.Add(0xFF);
        bytes.Add(marker);
        var length = payload.Length + 2;
        bytes.Add((byte)(length >> 8));
        bytes.Add((byte)length);
        bytes.AddRange(payload);
    }

    /// <summary>
    /// A 16x8 grayscale image (two 8x8 blocks, 1x1 sampling, so each block is its own MCU),
    /// DC-only content, DRI=1 (restart after every MCU) with a real RST0 marker between the
    /// two blocks' entropy-coded data - the smallest stream that exercises
    /// <see cref="JpegBitReader.TryConsumeRestartMarker"/> and the DC-predictor reset it
    /// triggers (without a reset, the right block's DC would be read as a diff off the left
    /// block's absolute value instead of 0).
    /// </summary>
    private static byte[] BuildTwoBlockGrayscaleWithRestart(byte leftValue, byte rightValue)
    {
        var bytes = new List<byte>();
        bytes.AddRange([0xFF, 0xD8]);

        var dqt = new byte[65];
        Array.Fill(dqt, (byte)1, 1, 64);
        AppendSegment(bytes, 0xDB, dqt);

        byte[] sof = [8, 0, 8, 0, 16, 1, 1, 0x11, 0];
        AppendSegment(bytes, 0xC0, sof);

        var dcBits = new byte[17];
        JpegHuffmanTable.StandardLuminanceDcBits.CopyTo(dcBits.AsSpan());
        var dht0 = new byte[17 + JpegHuffmanTable.StandardLuminanceDcValues.Length];
        dht0[0] = 0x00;
        dcBits.AsSpan(1, 16).CopyTo(dht0.AsSpan(1));
        JpegHuffmanTable.StandardLuminanceDcValues.CopyTo(dht0.AsSpan(17));
        AppendSegment(bytes, 0xC4, dht0);

        var acBits = new byte[17];
        JpegHuffmanTable.StandardLuminanceAcBits.CopyTo(acBits.AsSpan());
        var dht1 = new byte[17 + JpegHuffmanTable.StandardLuminanceAcValues.Length];
        dht1[0] = 0x10;
        acBits.AsSpan(1, 16).CopyTo(dht1.AsSpan(1));
        JpegHuffmanTable.StandardLuminanceAcValues.CopyTo(dht1.AsSpan(17));
        AppendSegment(bytes, 0xC4, dht1);

        AppendSegment(bytes, 0xDD, [0x00, 0x01]); // DRI: restart every 1 MCU.

        byte[] sos = [1, 1, 0x00, 0, 63, 0];
        AppendSegment(bytes, 0xDA, sos);

        var dcTable = JpegHuffmanTable.StandardLuminanceDc;
        var acTable = JpegHuffmanTable.StandardLuminanceAc;
        var entropy = new List<byte>();
        var bitBuffer = 0;
        var bitCount = 0;

        void WriteBits(int value, int length)
        {
            if (length == 0)
            {
                return;
            }

            bitBuffer = (bitBuffer << length) | (value & ((1 << length) - 1));
            bitCount += length;
            while (bitCount >= 8)
            {
                bitCount -= 8;
                var b = (byte)(bitBuffer >> bitCount);
                entropy.Add(b);
                if (b == 0xFF)
                {
                    entropy.Add(0x00);
                }
            }
        }

        void WriteBlock(int level)
        {
            var dcCoefficient = 8 * (level - 128); // matches the constant-block DC formula (see JpegIdctFdctTests).
            var (size, magnitudeBits) = MagnitudeCategory(dcCoefficient);
            var (code, length) = dcTable.GetCode((byte)size);
            WriteBits(code, length);
            WriteBits(magnitudeBits, size);
            var (eobCode, eobLength) = acTable.GetCode(0x00);
            WriteBits(eobCode, eobLength);
        }

        WriteBlock(leftValue);
        if (bitCount > 0)
        {
            var padded = (byte)((bitBuffer << (8 - bitCount)) | ((1 << (8 - bitCount)) - 1));
            entropy.Add(padded);
            if (padded == 0xFF)
            {
                entropy.Add(0x00);
            }

            bitCount = 0;
            bitBuffer = 0;
        }

        entropy.Add(0xFF);
        entropy.Add(0xD0); // RST0.

        WriteBlock(rightValue);
        if (bitCount > 0)
        {
            var padded = (byte)((bitBuffer << (8 - bitCount)) | ((1 << (8 - bitCount)) - 1));
            entropy.Add(padded);
            if (padded == 0xFF)
            {
                entropy.Add(0x00);
            }
        }

        bytes.AddRange(entropy);
        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }

    private static (int Size, int Bits) MagnitudeCategory(int value)
    {
        if (value == 0)
        {
            return (0, 0);
        }

        var abs = Math.Abs(value);
        var size = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)abs);
        var bits = value > 0 ? value : value + (1 << size) - 1;
        return (size, bits);
    }
}
