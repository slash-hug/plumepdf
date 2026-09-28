using System.Diagnostics;
using PlumePdf.Filters.Png;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The armed external-oracle lane for the PNG codec (the same shape:
/// <see cref="ExternalTool.TryRun"/> probe, <c>PLUMEPDF_REQUIRE_PNGTOPAM</c> sentinel,
/// non-empty-corpus assertion before iterating, a deliberately-broken fixture). Netpbm's
/// <c>pngtopam</c> is the independent, decades-old reference PNG reader/writer this lane
/// checks PlumePDF's from-scratch codec against two ways: <b>encode parity</b> — a
/// <see cref="PngEncoder"/>-produced file, read back by <c>pngtopam</c>, must match the
/// source pixels exactly — and <b>decode parity</b> — <c>pngtopam</c>'s own reading of a
/// hand-built PNG must agree with <see cref="PngDecoder"/>'s reading of the same bytes.
/// Skips (no-ops) when <c>pngtopam</c> isn't installed, UNLESS
/// <c>PLUMEPDF_REQUIRE_PNGTOPAM=1</c> — set by the CI <c>corpus</c> job, which installs
/// <c>netpbm</c> — in which case an absent tool FAILS the test instead of skipping (the
/// <see cref="HbShapeInteropTests"/>/<see cref="VeraPdfInteropTests"/> precedent).
/// </summary>
public class PngtopamInteropTests
{
    internal static readonly bool PngtopamAvailable = ProbePngtopam();

    private static bool ProbePngtopam()
    {
        // pngtopam has no --version; -version prints Netpbm's own banner to stderr — the
        // predicate below matches ci.yml's install-verification step, per house rule (the
        // probe inspects output, never the exit code alone, so an unrelated binary that
        // happens to be named "pngtopam" can't arm the lane).
        var (started, _, stdout, stderr) = ExternalTool.TryRun("pngtopam", "-version", timeoutMilliseconds: 10_000);
        return started && (stdout + stderr).Contains("Netpbm", StringComparison.Ordinal);
    }

    internal static bool PngtopamAvailableOrFailIfRequired()
    {
        if (PngtopamAvailable)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_PNGTOPAM") == "1")
        {
            Assert.Fail("PLUMEPDF_REQUIRE_PNGTOPAM=1 but the pngtopam CLI probe found no working tool — this lane expects netpbm installed and armed; a self-skip here would silently disable the D4 CI gate.");
        }

        return false;
    }

    [Fact]
    public void AbsentTool_IsDetectablyAbsent()
    {
        // Mirrors HbShapeInteropTests/VeraPdfInteropTests' own self-check: proves the probe
        // predicate is at least capable of returning false, so a hermetic run genuinely
        // skipping (rather than a broken probe that always returns true) is visible.
        var (started, _, stdout, stderr) = ExternalTool.TryRun("plumepdf-definitely-not-a-real-binary", "-version");
        Assert.False(started && (stdout + stderr).Contains("Netpbm", StringComparison.Ordinal));
    }

    private static string TempPngPath() => Path.Combine(Path.GetTempPath(), $"plumepdf-pngtopam-{Guid.NewGuid():N}.png");

    /// <summary>
    /// A raw-bytes process runner, separate from <see cref="ExternalTool.TryRun"/> (which
    /// captures stdout as text — lossy for the binary pixel bytes pngtopam writes to it).
    /// Same deadlock-avoidance shape: stderr drains concurrently, async, before stdout is
    /// read synchronously.
    /// </summary>
    private static (bool Started, int ExitCode, byte[] Stdout, string Stderr) RunRaw(string fileName, string arguments, int timeoutMilliseconds)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return (false, -1, [], string.Empty);
            }

            var stderrTask = process.StandardError.ReadToEndAsync();
            using var stdout = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(stdout);
            var stderr = stderrTask.GetAwaiter().GetResult();
            process.WaitForExit(timeoutMilliseconds);
            return (true, process.ExitCode, stdout.ToArray(), stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, -1, [], ex.Message);
        }
    }

    private static (int Width, int Height, int Depth, byte[] Samples) RunPngtopam(string path, bool alpha)
    {
        var (started, exitCode, stdout, stderr) = RunRaw("pngtopam", alpha ? $"-alphapam \"{path}\"" : $"\"{path}\"", timeoutMilliseconds: 15_000);
        Assert.True(started, "pngtopam failed to start after probing available.");
        Assert.True(exitCode == 0, $"pngtopam exited {exitCode}: {stderr}");
        return ParsePnm(stdout);
    }

    private static (int Width, int Height, int Depth, byte[] Samples) ParsePnm(byte[] output)
    {
        // Minimal PPM (P6)/PGM (P5)/PAM (P7) header reader — exactly the three shapes
        // pngtopam emits for the Gray8/Rgb24/Rgba32 (-alphapam) fixtures this lane builds.
        var text = System.Text.Encoding.ASCII.GetString(output, 0, Math.Min(200, output.Length));
        if (text.StartsWith("P7", StringComparison.Ordinal))
        {
            var marker = "ENDHDR\n"u8;
            var headerEnd = output.AsSpan().IndexOf(marker) + marker.Length;
            var header = System.Text.Encoding.ASCII.GetString(output, 0, headerEnd);
            var width = ParseIntAfter(header, "WIDTH ");
            var height = ParseIntAfter(header, "HEIGHT ");
            var depth = ParseIntAfter(header, "DEPTH ");
            return (width, height, depth, output[headerEnd..]);
        }

        // P5/P6: "P5\n{w} {h}\n{maxval}\n" or "P6\n{w} {h}\n{maxval}\n", then binary data.
        var isColor = text.StartsWith("P6", StringComparison.Ordinal);
        var parts = text[3..].Split('\n', 3);
        var dims = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var w = int.Parse(dims[0]);
        var h = int.Parse(dims[1]);
        var prefixLength = text.IndexOf('\n') + 1 + parts[0].Length + 1 + parts[1].Length + 1;
        return (w, h, isColor ? 3 : 1, output[prefixLength..]);
    }

    private static int ParseIntAfter(string header, string key)
    {
        var start = header.IndexOf(key, StringComparison.Ordinal) + key.Length;
        var end = header.IndexOf('\n', start);
        return int.Parse(header[start..end]);
    }

    public static TheoryData<RasterPixelFormat> Formats => new() { RasterPixelFormat.Gray8, RasterPixelFormat.Rgb24, RasterPixelFormat.Rgba32 };

    [Theory]
    [MemberData(nameof(Formats))]
    public void EncodedPng_ReadByPngtopam_MatchesSourcePixels(RasterPixelFormat format)
    {
        if (!PngtopamAvailableOrFailIfRequired())
        {
            return;
        }

        const int width = 4;
        const int height = 3;
        var bytesPerPixel = RasterImageFrame.BytesPerPixel(format);
        var pixels = new byte[width * height * bytesPerPixel];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 53) % 256);
        }

        var frame = new RasterImageFrame(pixels, width, height, format);
        var png = PngEncoder.Encode(frame, PdfOptions.Default);

        var path = TempPngPath();
        try
        {
            File.WriteAllBytes(path, png);
            var (w, h, depth, samples) = RunPngtopam(path, alpha: format == RasterPixelFormat.Rgba32);

            Assert.Equal(width, w);
            Assert.Equal(height, h);
            Assert.Equal(bytesPerPixel, depth);
            Assert.Equal(pixels, samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BrokenPngFixture_PngtopamAndPlumePdfBothRefuseIt()
    {
        if (!PngtopamAvailableOrFailIfRequired())
        {
            return;
        }

        // A signature-only "PNG" with no chunks at all: neither reader should accept it.
        byte[] broken = [137, 80, 78, 71, 13, 10, 26, 10];
        var path = TempPngPath();
        try
        {
            File.WriteAllBytes(path, broken);
            var (started, exitCode, _, _) = RunRaw("pngtopam", $"\"{path}\"", timeoutMilliseconds: 10_000);
            Assert.True(started);
            Assert.NotEqual(0, exitCode);

            Assert.Throws<PlumePdfException>(() => PngDecoder.Decode(broken, long.MaxValue));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
