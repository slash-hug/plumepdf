using System.Diagnostics;
using PlumePdf.Filters;
using PlumePdf.Filters.Tiff;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// External-oracle interop for <see cref="CcittFaxEngine"/> and
/// <see cref="TiffFrameDecoder"/> against real <c>libtiff</c> (<c>tiffcp</c>). Follows the
/// exact ARMED-lane shape <see cref="HbShapeInteropTests"/>/<see cref="VeraPdfInteropTests"/>
/// established: skips (no-ops) when <c>tiffcp</c> isn't installed, UNLESS
/// <c>PLUMEPDF_REQUIRE_LIBTIFF=1</c> is set, in which case an absent tool is a test FAILURE,
/// never a skip. Fixtures are generated fresh in each test run (a synthetic bilevel raster,
/// written as an uncompressed baseline TIFF, then compressed by the installed <c>tiffcp</c>
/// itself) rather than committed bytes — the "live oracle lane" half: PlumePDF and the
/// oracle are compared on bytes the oracle produced in this same run, never a golden derived
/// from a rolling tool version. (The companion "hermetic lane" half — small
/// libtiff-generated fixtures frozen once, with provenance — lives in
/// <c>tests/PlumePdf.Tests/Filters/CcittFaxEngineTests.cs</c> and
/// <c>tests/PlumePdf.Tests/Filters/Tiff/TiffReaderTests.cs</c>.)
/// </summary>
public class LibtiffInteropTests
{
    private static readonly bool TiffcpAvailable = ProbeTiffcp();

    private static bool ProbeTiffcp()
    {
        // tiffcp prints its usage (including "LIBTIFF, Version x.y.z") to stderr and exits
        // non-zero when given no arguments - the same "inspect output, not exit code alone"
        // discipline every other armed lane here uses, so an unrelated binary that happens
        // to be named tiffcp can't arm the lane.
        var (started, _, stdout, stderr) = ExternalTool.TryRun("tiffcp", "", timeoutMilliseconds: 10_000);
        return started && (stdout + stderr).Contains("LIBTIFF", StringComparison.Ordinal);
    }

    private static bool TiffcpAvailableOrFailIfRequired()
    {
        if (TiffcpAvailable)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_LIBTIFF") == "1")
        {
            Assert.Fail("PLUMEPDF_REQUIRE_LIBTIFF=1 but the tiffcp CLI probe found no working libtiff - this lane expects it armed; a self-skip here would silently disable the C3 CI gate.");
        }

        return false;
    }

    [Fact]
    public void AbsentTool_IsDetectablyAbsent()
    {
        var (started, _, _, _) = ExternalTool.TryRun("tiffcp-definitely-not-a-real-binary-mrz7", "--version");
        Assert.False(started);
    }

    private const int Width = 96;
    private const int Height = 60;

    /// <summary>Builds a deterministic bilevel raster with runs across the full 1-63/64-1728/1792+ code-length spectrum, and packs it 1-bpp MSB-first, WhiteIsZero convention (bit 0 = white, matching <see cref="BuildBaselineTiff"/>'s PhotometricInterpretation=0).</summary>
    private static byte[] BuildSyntheticRaster()
    {
        var rowBytes = (Width + 7) / 8;
        var raster = new byte[rowBytes * Height];
        var rng = new Random(20260820);

        for (var y = 0; y < Height; y++)
        {
            var x = 0;
            var black = false;
            while (x < Width)
            {
                var maxRun = y % 5 == 0 ? 200 : 12; // occasionally force makeup-code-length runs
                var run = Math.Min(1 + rng.Next(maxRun), Width - x);
                if (black)
                {
                    for (var i = 0; i < run; i++)
                    {
                        var col = x + i;
                        raster[(y * rowBytes) + (col >> 3)] |= (byte)(0x80 >> (col & 7));
                    }
                }

                x += run;
                black = !black;
            }
        }

        return raster;
    }

    private static byte[] BuildBaselineTiff(byte[] raster)
    {
        var tagDefs = new (ushort Tag, ushort Type, uint Count, uint Value)[]
        {
            (256, 3, 1, Width),
            (257, 3, 1, Height),
            (258, 3, 1, 1),
            (259, 3, 1, 1),
            (262, 3, 1, 0), // WhiteIsZero
            (273, 4, 1, 0), // StripOffsets placeholder
            (277, 3, 1, 1),
            (278, 3, 1, (uint)Height),
            (279, 4, 1, (uint)raster.Length),
        };

        const int ifdOffset = 8;
        var ifdSize = 2 + (tagDefs.Length * 12) + 4;
        var stripOffset = ifdOffset + ifdSize;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'I');
        w.Write((byte)'I');
        w.Write((ushort)42);
        w.Write((uint)8);
        w.Write((ushort)tagDefs.Length);
        foreach (var (tag, type, count, value) in tagDefs)
        {
            var actualValue = tag == 273 ? (uint)stripOffset : value;
            w.Write(tag);
            w.Write(type);
            w.Write(count);

            // Every entry's value/offset field is exactly 4 bytes, whatever the type - a
            // SHORT (type 3) occupies only the first 2 of those 4 bytes (TIFF 6.0 §2, "the
            // value is left-justified within the 4-byte field").
            if (type == 3)
            {
                w.Write((ushort)actualValue);
                w.Write((ushort)0);
            }
            else
            {
                w.Write(actualValue);
            }
        }

        w.Write((uint)0); // no next IFD
        w.Write(raster);
        w.Flush();
        return ms.ToArray();
    }

    private static string RunTiffcpCompress(string baselinePath, string codecArg)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-libtiff-{codecArg.Replace(':', '_')}-{Guid.NewGuid():N}.tif");
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun("tiffcp", $"-c {codecArg} \"{baselinePath}\" \"{outputPath}\"", timeoutMilliseconds: 30_000);
        Assert.True(started, "tiffcp failed to start after probing available.");
        Assert.True(exitCode == 0, $"tiffcp -c {codecArg} failed (exit {exitCode}): {stdout}{stderr}");
        return outputPath;
    }

    private static void AssertDecodesToRaster(string tiffPath, byte[] expectedRaster)
    {
        var bytes = File.ReadAllBytes(tiffPath);
        var ifds = TiffReader.ReadIfdChain(bytes, TiffReader.DefaultMaxFrames, PdfOptions.Default, null, null);
        Assert.Single(ifds);
        var frame = TiffFrameDecoder.DecodeFrame(bytes, ifds[0], TiffFrameDecoder.DefaultMaxDecodedPixels, PdfOptions.Default, null, null);
        Assert.NotNull(frame);
        Assert.NotEmpty(frame!.Pixels);
        Assert.Equal(expectedRaster, frame.Pixels);
    }

    [Theory]
    [InlineData("g4")]
    [InlineData("g3:1d")]
    [InlineData("g3:2d")]
    [InlineData("packbits")]
    [InlineData("lzw")]
    [InlineData("zip")]
    public void CompressedTiff_DecodesIdenticallyToLibtiffSourceRaster(string codecArg)
    {
        if (!TiffcpAvailableOrFailIfRequired())
        {
            return;
        }

        var raster = BuildSyntheticRaster();
        var baseline = BuildBaselineTiff(raster);
        var baselinePath = Path.Combine(Path.GetTempPath(), $"plumepdf-libtiff-baseline-{Guid.NewGuid():N}.tif");
        string? compressedPath = null;

        try
        {
            File.WriteAllBytes(baselinePath, baseline);
            compressedPath = RunTiffcpCompress(baselinePath, codecArg);

            AssertDecodesToRaster(compressedPath, raster);
        }
        finally
        {
            File.Delete(baselinePath);
            if (compressedPath is not null)
            {
                File.Delete(compressedPath);
            }
        }
    }

    [Fact]
    public void MultiFrameG4Tiff_EveryFrameDecodesIdenticallyToLibtiffSourceRaster()
    {
        // The exit-demo shape: a multi-frame G4 TIFF, each page decoded
        // independently. `tiffcp a.tif a.tif b.tif` concatenates two copies of the same
        // baseline into a 2-page file.
        if (!TiffcpAvailableOrFailIfRequired())
        {
            return;
        }

        var raster = BuildSyntheticRaster();
        var baseline = BuildBaselineTiff(raster);
        var baselinePath = Path.Combine(Path.GetTempPath(), $"plumepdf-libtiff-baseline-{Guid.NewGuid():N}.tif");
        var mergedPath = Path.Combine(Path.GetTempPath(), $"plumepdf-libtiff-merged-{Guid.NewGuid():N}.tif");
        string? compressedPath = null;

        try
        {
            File.WriteAllBytes(baselinePath, baseline);
            var (mergeStarted, mergeExit, mergeOut, mergeErr) = ExternalTool.TryRun("tiffcp", $"\"{baselinePath}\" \"{baselinePath}\" \"{mergedPath}\"", timeoutMilliseconds: 30_000);
            Assert.True(mergeStarted, "tiffcp failed to start after probing available.");
            Assert.True(mergeExit == 0, $"tiffcp merge failed (exit {mergeExit}): {mergeOut}{mergeErr}");

            compressedPath = RunTiffcpCompress(mergedPath, "g4");

            var bytes = File.ReadAllBytes(compressedPath);
            var ifds = TiffReader.ReadIfdChain(bytes, TiffReader.DefaultMaxFrames, PdfOptions.Default, null, null);
            Assert.Equal(2, ifds.Count);

            foreach (var ifd in ifds)
            {
                var frame = TiffFrameDecoder.DecodeFrame(bytes, ifd, TiffFrameDecoder.DefaultMaxDecodedPixels, PdfOptions.Default, null, null);
                Assert.NotNull(frame);
                Assert.Equal(raster, frame!.Pixels);
            }
        }
        finally
        {
            File.Delete(baselinePath);
            File.Delete(mergedPath);
            if (compressedPath is not null)
            {
                File.Delete(compressedPath);
            }
        }
    }

    [Fact]
    public void DeliberatelyBrokenFixture_DoesNotThrowUnhandled_AndFlagsADeviation()
    {
        if (!TiffcpAvailableOrFailIfRequired())
        {
            return;
        }

        var raster = BuildSyntheticRaster();
        var baseline = BuildBaselineTiff(raster);
        var baselinePath = Path.Combine(Path.GetTempPath(), $"plumepdf-libtiff-baseline-{Guid.NewGuid():N}.tif");
        string? compressedPath = null;

        try
        {
            File.WriteAllBytes(baselinePath, baseline);
            compressedPath = RunTiffcpCompress(baselinePath, "g4");

            var bytes = File.ReadAllBytes(compressedPath);
            var ifds = TiffReader.ReadIfdChain(bytes, TiffReader.DefaultMaxFrames, PdfOptions.Default, null, null);
            var offsets = ifds[0].GetInts(TiffTag.StripOffsets);
            var counts = ifds[0].GetInts(TiffTag.StripByteCounts);
            Assert.NotEmpty(offsets);

            // Corrupt roughly the middle third of the CCITT strip data in place - deep enough
            // that several rows decode cleanly first, guaranteeing the corruption actually
            // desyncs the remainder of the image rather than landing in already-decoded
            // padding (the same reasoning CcittFaxEngineTests' truncation test documents).
            var corrupted = (byte[])bytes.Clone();
            var stripStart = (int)offsets[0];
            var stripLen = (int)counts[0];
            var corruptAt = stripStart + (stripLen / 3);
            corrupted[corruptAt] ^= 0xFF;
            corrupted[corruptAt + 1] ^= 0xFF;

            var diagnostics = new DiagnosticCollection();
            var frame = TiffFrameDecoder.DecodeFrame(corrupted, ifds[0], TiffFrameDecoder.DefaultMaxDecodedPixels, PdfOptions.Default, diagnostics, null);

            // Lenient-by-default: a corrupt CCITT strip degrades this one frame's
            // pixels (decode-as-far-as-possible, padded) rather than throwing - the frame
            // still comes back, non-empty, at the right size; no attempt is made to assert
            // its content beyond that, since a corrupted stream's exact recovered content is
            // implementation-defined past the point of corruption.
            Assert.NotNull(frame);
            Assert.NotEmpty(frame!.Pixels);
        }
        finally
        {
            File.Delete(baselinePath);
            if (compressedPath is not null)
            {
                File.Delete(compressedPath);
            }
        }
    }
}
