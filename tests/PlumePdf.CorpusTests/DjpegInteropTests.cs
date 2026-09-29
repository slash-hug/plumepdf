using PlumePdf.Filters;
using PlumePdf.Tests.Filters.Jpeg;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// External-oracle interop for the JPEG codec (the djpeg oracle
/// landed together with the first baseline decoder): runs libjpeg-turbo's
/// <c>djpeg</c>/<c>cjpeg</c> CLIs — an
/// independent reference implementation, never a porting source (the clean-room policy
/// in AGENTS.md; these are executed-only) — against every hermetic JPEG fixture
/// (<c>Fixtures/jpeg/</c>, provenance in that folder's own <c>README.md</c>) and compares
/// decoded pixels channel-for-channel within the survey's documented ≤1/255 tolerance.
/// </summary>
/// <remarks>
/// Follows the exact armed-lane shape <see cref="VeraPdfInteropTests"/>/<see cref="HbShapeInteropTests"/>
/// established: skips (no-ops) when neither <c>djpeg</c> nor <c>cjpeg</c> is installed, UNLESS
/// <c>PLUMEPDF_REQUIRE_DJPEG=1</c> is set (the corpus CI job's own contract, installing
/// <c>libjpeg-turbo-progs</c> and exporting that variable), in which case an
/// absent tool is a test FAILURE, never a skip - so an unarmed lane can never read as green.
/// </remarks>
public class DjpegInteropTests
{
    private static readonly bool DjpegAvailable = ProbeTool("djpeg", "-version");
    private static readonly bool CjpegAvailable = ProbeTool("cjpeg", "-version");

    private static bool ProbeTool(string tool, string args)
    {
        // Same "inspect output, not exit code" discipline as every other armed-lane probe in
        // this project: both djpeg and cjpeg print "libjpeg-turbo version X.Y.Z" (not their
        // own binary name) on `-version`, so that is the substring that rules out an
        // unrelated binary happening to share the name.
        var (started, _, stdout, stderr) = ExternalTool.TryRun(tool, args, timeoutMilliseconds: 10_000);
        return started && (stdout + stderr).Contains("libjpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ToolsAvailableOrFailIfRequired()
    {
        if (DjpegAvailable && CjpegAvailable)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_DJPEG") == "1")
        {
            Assert.Fail("PLUMEPDF_REQUIRE_DJPEG=1 but the djpeg/cjpeg CLI probe found no working tool - this lane installed libjpeg-turbo-progs and expects it armed; a self-skip here would silently disable the charter's decoder+oracle gate.");
        }

        return false;
    }

    [Fact]
    public void AbsentTool_IsDetectablyAbsent()
    {
        var (started, _, _, _) = ExternalTool.TryRun("djpeg-definitely-not-a-real-binary-mrz7", "-version");
        Assert.False(started);
    }

    private static string FixturesDir => Path.Combine(CorpusFixture.FixturesRoot, "jpeg");

    public static TheoryData<string> DecodeParityFixtures()
    {
        var data = new TheoryData<string>();
        if (Directory.Exists(FixturesDir))
        {
            foreach (var file in Directory.EnumerateFiles(FixturesDir, "*.jpg").OrderBy(static f => f, StringComparer.Ordinal))
            {
                if (!Path.GetFileName(file).StartsWith("corrupt-", StringComparison.Ordinal))
                {
                    data.Add(file);
                }
            }
        }

        return data;
    }

    [Fact]
    public void DecodeParityFixtures_IsNonEmpty()
    {
        // Non-vacuity discipline: the hermetic fixtures are always present (committed
        // bytes, nothing to fetch), so this must never be empty regardless of tool armament.
        Assert.NotEmpty(DecodeParityFixtures());
    }

    [Theory]
    [MemberData(nameof(DecodeParityFixtures))]
    public void Decode_MatchesDjpegPixelForPixel(string fixturePath)
    {
        if (!ToolsAvailableOrFailIfRequired())
        {
            Console.WriteLine($"SKIPPED (requires djpeg/cjpeg - run brew/apt install libjpeg-turbo)");
            return;
        }

        var bytes = File.ReadAllBytes(fixturePath);
        var result = JpegDecoder.Decode(bytes, PdfOptions.Default, diagnostics: null, subject: null);

        byte[] reference;
        try
        {
            reference = DecodeWithDjpeg(fixturePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{fixturePath}: {ex.Message}", ex);
        }

        AssertPixelsMatch(reference, result.Pixels, result.Width, result.Height, result.ComponentCount, fixturePath);
    }

    [Fact]
    public void CorruptTruncatedHeader_FailsInBothDecoders()
    {
        var fixturePath = Path.Combine(FixturesDir, "corrupt-truncated-header.jpg");
        if (!File.Exists(fixturePath))
        {
            Console.WriteLine("SKIPPED (fixture not found)");
            return;
        }

        var bytes = File.ReadAllBytes(fixturePath);
        Assert.Throws<PlumePdfException>(() => JpegDecoder.Decode(bytes, PdfOptions.Default, diagnostics: null, subject: null));

        if (!ToolsAvailableOrFailIfRequired())
        {
            Console.WriteLine("SKIPPED djpeg cross-check (requires djpeg)");
            return;
        }

        var (started, exitCode, _, _) = ExternalTool.TryRun("djpeg", $"-pnm \"{fixturePath}\"", timeoutMilliseconds: 10_000);
        Assert.True(started, "djpeg failed to start after probing available.");
        Assert.NotEqual(0, exitCode); // djpeg also refuses this fixture - both independent decoders agree it is undecodable.
    }

    [Fact]
    public void CorruptTruncatedScan_DecodesAsFarAsPossibleWithDiagnostic()
    {
        var fixturePath = Path.Combine(FixturesDir, "corrupt-truncated-scan.jpg");
        if (!File.Exists(fixturePath))
        {
            Console.WriteLine("SKIPPED (fixture not found)");
            return;
        }

        var bytes = File.ReadAllBytes(fixturePath);
        var diagnostics = new DiagnosticCollection();
        var result = JpegDecoder.Decode(bytes, PdfOptions.Default, diagnostics, subject: null);

        Assert.True(result.Width > 0 && result.Height > 0);
        Assert.Contains(diagnostics, d => d.Code is "PLUME3205" or "PLUME3207");
    }

    [Fact]
    public void Cmyk_DecodesWithoutThrowing_AndDjpegAgreesOnDimensions()
    {
        var bytes = CmykJpegFixture.BuildFlatColor(200, 120, 60, 40);
        var result = JpegDecoder.Decode(bytes, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(8, result.Width);
        Assert.Equal(8, result.Height);
        Assert.Equal(4, result.ComponentCount);
        Assert.Equal(JpegAdobeTransform.Unknown, result.AdobeTransform);

        // Flat block => DC-only coefficients => exact reconstruction of the source samples.
        Assert.Equal(200, result.Pixels[0]);
        Assert.Equal(120, result.Pixels[1]);
        Assert.Equal(60, result.Pixels[2]);
        Assert.Equal(40, result.Pixels[3]);

        if (!ToolsAvailableOrFailIfRequired())
        {
            Console.WriteLine("SKIPPED djpeg cross-check (requires djpeg)");
            return;
        }

        // libjpeg-turbo's PNM writer does not support 4-component (CMYK) output, so the
        // achievable cross-check here is that djpeg's own decoder accepts the fixture as a
        // structurally valid JPEG (matching PlumePDF's own successful decode) rather than a
        // pixel comparison - pixel parity for CMYK is exercised through the standard
        // grayscale/RGB fixtures above, which cover the same Huffman/IDCT/dequantization
        // machinery this fixture's flat blocks intentionally keep simple.
        var tempPath = Path.Combine(Path.GetTempPath(), $"plumepdf-cmyk-{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            var (started, exitCode, _, stderr) = ExternalTool.TryRun("djpeg", $"-bmp -outfile \"{tempPath}.bmp\" \"{tempPath}\"", timeoutMilliseconds: 10_000);
            Assert.True(started, "djpeg failed to start after probing available.");
            Assert.True(exitCode == 0, $"djpeg rejected the CMYK fixture as structurally invalid (exit {exitCode}): {stderr}");
        }
        finally
        {
            File.Delete(tempPath);
            File.Delete(tempPath + ".bmp");
        }
    }

    [Fact]
    public void Encode_Quality95_MeetsPsnrFloorAgainstCjpeg()
    {
        if (!ToolsAvailableOrFailIfRequired())
        {
            Console.WriteLine("SKIPPED (requires djpeg/cjpeg)");
            return;
        }

        const int width = 64, height = 48;
        var source = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 3;
                source[offset] = (byte)((x * 255) / width);
                source[offset + 1] = (byte)((y * 255) / height);
                source[offset + 2] = 128;
            }
        }

        // subsampleChroma: false to match cjpeg's `-sample 1x1` below (4:4:4) - comparing
        // PlumePDF's default 4:2:0 encode against a 4:4:4 reference would measure subsampling
        // loss, not encoder quality.
        var plumeJpeg = JpegEncoder.Encode(source, width, height, componentCount: 3, quality: 95, subsampleChroma: false);
        var plumeDecodedByDjpeg = DecodeWithDjpeg(WriteTemp(plumeJpeg, ".jpg"));
        var plumePsnr = ComputePsnr(source, plumeDecodedByDjpeg, width, height, 3);

        var ppmPath = WriteTemp(BuildPpm(source, width, height), ".ppm");
        var cjpegPath = ppmPath + ".cjpeg.jpg";
        var (started, exitCode, _, stderr) = ExternalTool.TryRun("cjpeg", $"-quality 95 -sample 1x1 -outfile \"{cjpegPath}\" \"{ppmPath}\"", timeoutMilliseconds: 10_000);
        Assert.True(started, "cjpeg failed to start after probing available.");
        Assert.True(exitCode == 0, $"cjpeg failed (exit {exitCode}): {stderr}");
        var cjpegDecodedByDjpeg = DecodeWithDjpeg(cjpegPath);
        var cjpegPsnr = ComputePsnr(source, cjpegDecodedByDjpeg, width, height, 3);

        // Not required to beat cjpeg (an unoptimized-table baseline encoder reasonably trails
        // a mature, tuned reference implementation) - required to be in the same quality
        // class, not silently broken (e.g. an off-by-one quant/zigzag bug that still "looks
        // like" a JPEG but is visibly wrong would show up as a multi-dB PSNR collapse).
        Assert.True(plumePsnr >= cjpegPsnr - 4.0, $"PlumePDF q95 PSNR {plumePsnr:F2}dB is more than 4dB below cjpeg q95's {cjpegPsnr:F2}dB.");
        Assert.True(plumePsnr >= 30.0, $"PlumePDF q95 PSNR {plumePsnr:F2}dB is below the 30dB floor.");
    }

    private static byte[] DecodeWithDjpeg(string jpegPath)
    {
        // A unique temp path, never beside the fixture: both target frameworks' test processes can
        // decode the same fixture at the same moment, and one's cleanup would delete the other's
        // output mid-read.
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-djpeg-{Guid.NewGuid():N}.pnm");
        try
        {
            // -nosmooth: matches PlumePDF's own simple nearest-neighbor chroma upsampling
            // rather than djpeg's default triangle-filter "fancy" upsampling (see JpegDecoder's
            // SampleComponent remarks). -dct float: djpeg's *default* is an accurate fixed-point
            // integer IDCT, which legitimately differs from any float IDCT (JpegIdct's included)
            // by up to a couple of levels per the survey's own tolerance discussion - forcing
            // both sides onto a float IDCT isolates genuine decode bugs from this well-understood,
            // implementation-choice source of divergence between two independently correct
            // decoders.
            var (started, exitCode, _, stderr) = ExternalTool.TryRun("djpeg", $"-dct float -nosmooth -pnm -outfile \"{outputPath}\" \"{jpegPath}\"", timeoutMilliseconds: 15_000);
            Assert.True(started, "djpeg failed to start after probing available.");
            Assert.True(exitCode == 0, $"djpeg failed to decode {jpegPath} (exit {exitCode}): {stderr}");
            return ParsePnm(File.ReadAllBytes(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string WriteTemp(byte[] bytes, string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-jpeg-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] BuildPpm(byte[] rgbPixels, int width, int height)
    {
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        var result = new byte[header.Length + rgbPixels.Length];
        header.CopyTo(result, 0);
        rgbPixels.CopyTo(result, header.Length);
        return result;
    }

    // Parses a binary PBMPLUS PNM (P5 = grayscale PGM, P6 = RGB PPM; djpeg's -pnm default,
    // -nosmooth disabling the fancy chroma upsampling PlumePDF's own simple nearest-neighbor
    // upsampler does not replicate) - the header is whitespace-separated tokens (comments
    // starting with '#' skipped) followed by exactly width*height*components raw bytes.
    private static byte[] ParsePnm(byte[] data)
    {
        var pos = 0;
        var magic = ReadToken(data, ref pos);
        var componentCount = magic switch
        {
            "P5" => 1,
            "P6" => 3,
            _ => throw new InvalidOperationException($"Unsupported PNM magic '{magic}'."),
        };

        var width = int.Parse(ReadToken(data, ref pos));
        var height = int.Parse(ReadToken(data, ref pos));
        _ = ReadToken(data, ref pos); // maxval, always 255 for djpeg's 8-bit output.
        pos++; // single whitespace byte separating the header from raw data.

        var pixelBytes = width * height * componentCount;
        return data[pos..(pos + pixelBytes)];
    }

    private static string ReadToken(byte[] data, ref int pos)
    {
        while (true)
        {
            while (pos < data.Length && char.IsWhiteSpace((char)data[pos]))
            {
                pos++;
            }

            if (data[pos] != (byte)'#')
            {
                break;
            }

            while (pos < data.Length && data[pos] != (byte)'\n')
            {
                pos++;
            }
        }

        var start = pos;
        while (pos < data.Length && !char.IsWhiteSpace((char)data[pos]))
        {
            pos++;
        }

        return System.Text.Encoding.ASCII.GetString(data, start, pos - start);
    }

    private static void AssertPixelsMatch(byte[] reference, byte[] actual, int width, int height, int componentCount, string fixturePath)
    {
        Assert.True(reference.Length == actual.Length, $"{fixturePath}: djpeg produced {reference.Length} bytes, PlumePDF produced {actual.Length} (width={width}, height={height}, components={componentCount}).");

        var maxDiff = 0;
        var maxDiffIndex = -1;
        for (var i = 0; i < reference.Length; i++)
        {
            var diff = Math.Abs(reference[i] - actual[i]);
            if (diff > maxDiff)
            {
                maxDiff = diff;
                maxDiffIndex = i;
            }
        }

        // The survey's documented tolerance: at most 1/255 per channel, accounting for float
        // rounding differences between two independently-implemented IDCTs/upsamplers, never
        // more. The message is built only on failure (maxDiffIndex stays -1, an invalid array
        // index, on a perfect match) - Assert.True's message argument is eagerly evaluated
        // regardless of the condition, so building it unconditionally would throw on the
        // common all-pixels-matched case instead of ever reaching the assertion.
        if (maxDiff > 1)
        {
            Assert.Fail($"{fixturePath}: pixel byte {maxDiffIndex} differs by {maxDiff} (djpeg={reference[maxDiffIndex]}, plumepdf={actual[maxDiffIndex]}) - exceeds the 1/255 tolerance.");
        }
    }

    private static double ComputePsnr(byte[] a, byte[] b, int width, int height, int componentCount)
    {
        var count = width * height * componentCount;
        double sumSquaredError = 0;
        for (var i = 0; i < count; i++)
        {
            var diff = a[i] - (double)b[i];
            sumSquaredError += diff * diff;
        }

        var meanSquaredError = sumSquaredError / count;
        if (meanSquaredError <= 0)
        {
            return 100.0; // Effectively lossless - avoid a divide-by-zero/log(0).
        }

        return 10.0 * Math.Log10((255.0 * 255.0) / meanSquaredError);
    }
}
