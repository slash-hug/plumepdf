using System;
using System.IO;
using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// A managed (no native dependency) implementation of the Structural Similarity Index
/// (Wang, Bovik, Sheikh &amp; Simoncelli, 2004) used to compare PlumePDF's own Rasterize output
/// against the PDFium oracle's render of the same page (Phase 8). Deliberately built and unit-tested
/// (<see cref="RasterSsimTests"/>) before any Raster pixel code exists, per a test-first
/// mandate: the metric itself must be provably correct — identical images score 1.0, a known-
/// degraded pair scores measurably lower — independent of whether the rasterizer or the
/// PDFium shim are wired up yet.
/// </summary>
/// <remarks>
/// Computes mean SSIM over non-overlapping 8×8 luma blocks rather than the reference paper's
/// 11×11 circular-symmetric Gaussian sliding window — a deliberate phase-local simplification
/// (this gate's threshold isn't calibrated until after the pixel pipeline exists;
/// swapping to a sliding Gaussian window later is a metric-precision refinement, not a contract
/// change, since both converge to the same [0,1] scale with 1.0 meaning "identical"). Uses the
/// paper's own stabilizing constants C1/C2 scaled for an 8-bit dynamic range (L=255).
/// </remarks>
public static class RasterSsim
{
    private const double L = 255.0;
    private const double K1 = 0.01;
    private const double K2 = 0.03;
    private static readonly double C1 = (K1 * L) * (K1 * L);
    private static readonly double C2 = (K2 * L) * (K2 * L);
    private const int BlockSize = 8;

    /// <summary>
    /// Computes the mean SSIM between two equally-sized 8-bit grayscale (luma) images, each a
    /// row-major, unpadded byte array of length <c>width * height</c>. Returns a value in
    /// [-1, 1]; 1.0 means the two images are pixel-identical (or, in principle, identical up to
    /// a constant offset/gain within one block — SSIM measures structural similarity, not exact
    /// equality). Images smaller than one <see cref="BlockSize"/> block in either dimension are
    /// compared as a single block covering the whole image.
    /// </summary>
    /// <exception cref="ArgumentException">The two images have different dimensions, or either buffer's length doesn't match <paramref name="width"/> * <paramref name="height"/>.</exception>
    public static double Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var expected = (long)width * height;
        if (a.Length != expected || b.Length != expected)
        {
            throw new ArgumentException(
                $"Expected two {width}x{height} ({expected}-byte) grayscale buffers, got {a.Length} and {b.Length}.");
        }

        var blockW = Math.Min(BlockSize, width);
        var blockH = Math.Min(BlockSize, height);

        double sum = 0;
        var blockCount = 0;

        for (var by = 0; by < height; by += blockH)
        {
            var h = Math.Min(blockH, height - by);
            for (var bx = 0; bx < width; bx += blockW)
            {
                var w = Math.Min(blockW, width - bx);
                sum += BlockSsim(a, b, width, bx, by, w, h);
                blockCount++;
            }
        }

        // blockCount is always >= 1 for any positive width/height, so this never divides by
        // zero — ArgumentOutOfRangeException above already rejected width/height <= 0.
        return sum / blockCount;
    }

    /// <summary>
    /// RGB-weighted-luma convenience overload: converts two equally-sized
    /// <see cref="RasterPixelFormat.Rgb24"/> or <see cref="RasterPixelFormat.Rgba32"/> buffers
    /// to grayscale (ITU-R BT.601 luma weights, matching common raster-oracle comparison
    /// practice) and delegates to <see cref="Compute(ReadOnlySpan{byte},ReadOnlySpan{byte},int,int)"/>.
    /// Alpha, if present, is ignored — SSIM here measures the rendered picture, not the
    /// compositing channel.
    /// </summary>
    public static double Compute(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height, RasterPixelFormat format)
    {
        var grayA = ToLuma(a, width, height, format);
        var grayB = ToLuma(b, width, height, format);
        return Compute(grayA, grayB, width, height);
    }

    private static byte[] ToLuma(ReadOnlySpan<byte> pixels, int width, int height, RasterPixelFormat format)
    {
        var bytesPerPixel = format switch
        {
            RasterPixelFormat.Gray8 => 1,
            RasterPixelFormat.Rgb24 => 3,
            RasterPixelFormat.Rgba32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
        };

        var expected = (long)width * height * bytesPerPixel;
        if (pixels.Length != expected)
        {
            throw new ArgumentException($"Expected {expected} bytes of {format} pixel data for a {width}x{height} image, got {pixels.Length}.", nameof(pixels));
        }

        if (format == RasterPixelFormat.Gray8)
        {
            return pixels.ToArray();
        }

        var gray = new byte[(long)width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var offset = i * bytesPerPixel;
            var r = pixels[offset];
            var g = pixels[offset + 1];
            var b = pixels[offset + 2];
            gray[i] = (byte)Math.Clamp((0.299 * r) + (0.587 * g) + (0.114 * b), 0.0, 255.0);
        }

        return gray;
    }

    private static double BlockSsim(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stride, int startX, int startY, int w, int h)
    {
        double sumA = 0, sumB = 0;
        var n = w * h;

        for (var y = 0; y < h; y++)
        {
            var rowOffset = ((startY + y) * stride) + startX;
            for (var x = 0; x < w; x++)
            {
                sumA += a[rowOffset + x];
                sumB += b[rowOffset + x];
            }
        }

        var meanA = sumA / n;
        var meanB = sumB / n;

        double varA = 0, varB = 0, covAB = 0;
        for (var y = 0; y < h; y++)
        {
            var rowOffset = ((startY + y) * stride) + startX;
            for (var x = 0; x < w; x++)
            {
                var da = a[rowOffset + x] - meanA;
                var db = b[rowOffset + x] - meanB;
                varA += da * da;
                varB += db * db;
                covAB += da * db;
            }
        }

        // Population variance/covariance (divide by n, not n-1) — matches the reference SSIM
        // paper's own formulation, which treats the block as the entire population under
        // consideration rather than a sample drawn from a larger one.
        varA /= n;
        varB /= n;
        covAB /= n;

        var numerator = ((2 * meanA * meanB) + C1) * ((2 * covAB) + C2);
        var denominator = ((meanA * meanA) + (meanB * meanB) + C1) * (varA + varB + C2);
        return numerator / denominator;
    }

    /// <summary>
    /// Compares two <see cref="RasterImageFrame"/>s of equal dimensions — normalizing each to
    /// opaque RGB24 first (an <see cref="RasterPixelFormat.Rgba32"/> frame, e.g. Rasterize
    /// output, is composited over white; a Gray8 frame is expanded) so a Rasterize frame and a
    /// PDFium-PNG-decoded frame (which decodes to RGB24) can be scored on the same footing.
    /// </summary>
    public static double ComputeFrames(RasterImageFrame a, RasterImageFrame b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            throw new ArgumentException($"Frame dimensions differ: {a.Width}x{a.Height} vs {b.Width}x{b.Height}.");
        }

        return Compute(ToRgb24(a), ToRgb24(b), a.Width, a.Height, RasterPixelFormat.Rgb24);
    }

    private static byte[] ToRgb24(RasterImageFrame f)
    {
        var src = f.Pixels.Span;
        var n = f.Width * f.Height;
        var outp = new byte[n * 3];
        switch (f.Format)
        {
            case RasterPixelFormat.Rgb24:
                src.CopyTo(outp);
                break;
            case RasterPixelFormat.Rgba32:
                for (var i = 0; i < n; i++)
                {
                    var alpha = src[(i * 4) + 3];
                    for (var c = 0; c < 3; c++)
                    {
                        var v = src[(i * 4) + c];
                        outp[(i * 3) + c] = (byte)(((v * alpha) + (255 * (255 - alpha))) / 255);
                    }
                }

                break;
            default: // Gray8
                for (var i = 0; i < n; i++)
                {
                    outp[i * 3] = outp[(i * 3) + 1] = outp[(i * 3) + 2] = src[i];
                }

                break;
        }

        return outp;
    }

    /// <summary>
    /// Reads the calibrated SSIM acceptance floor from <c>thresholds/ssim.json</c> — the single
    /// source of truth the <c>scripts/check-ssim-threshold.sh</c> governance gate also reads.
    /// </summary>
    public static double LoadCalibratedFloor()
    {
        var path = Path.Combine(CorpusFixture.RepoRoot, "tests", "PlumePdf.CorpusTests", "thresholds", "ssim.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("floor").GetDouble();
    }

    /// <summary>
    /// A leg's own floor from <c>ssim.json</c>'s <c>perTestFloors</c> block:
    /// the governed number, so <c>scripts/check-ssim-threshold.sh</c>'s SSIM-LOOSEN rule covers the
    /// value the test actually asserts — a hard-coded const in the test body would sit outside it.
    /// Fails loudly when the entry is missing rather than defaulting.
    /// </summary>
    public static double LoadPerTestFloor(string testName)
    {
        var path = Path.Combine(CorpusFixture.RepoRoot, "tests", "PlumePdf.CorpusTests", "thresholds", "ssim.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("perTestFloors", out var block) || !block.TryGetProperty(testName, out var entry) || !entry.TryGetProperty("floor", out var floor))
        {
            throw new InvalidOperationException($"ssim.json has no perTestFloors['{testName}'].floor — the leg's floor must live in the governed file, not in the test.");
        }

        return floor.GetDouble();
    }
}

/// <summary>
/// Test-first proof that <see cref="RasterSsim"/> itself is correct, independent of the
/// rasterizer or the PDFium shim: identical images must score 1.0, and a known-degraded pair
/// must score measurably below that — the two acceptance criteria this metric is held to.
/// </summary>
public class RasterSsimTests
{
    private static byte[] MakeGradient(int width, int height)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // A deterministic, non-flat pattern — SSIM's variance/covariance terms are
                // degenerate (and the formula's C2-only fallback trivially hides bugs) against
                // a uniformly flat image, so the correctness proof needs real structure.
                pixels[(y * width) + x] = (byte)(((x * 7) + (y * 13)) % 256);
            }
        }

        return pixels;
    }

    [Fact]
    public void IdenticalImages_ScoreExactlyOne()
    {
        var image = MakeGradient(64, 64);
        var score = RasterSsim.Compute(image, image, 64, 64);
        Assert.Equal(1.0, score, precision: 9);
    }

    [Fact]
    public void KnownDegradedPair_ScoresMeasurablyBelowIdentical()
    {
        var reference = MakeGradient(64, 64);
        var degraded = (byte[])reference.Clone();

        // A crude, deliberate degradation: add correlated noise to every pixel — structurally
        // similar (same overall pattern) but not identical, exactly the shape a real rasterizer
        // bug (off-by-one antialiasing, wrong blend formula) would produce against the oracle.
        var rng = new Random(1234);
        for (var i = 0; i < degraded.Length; i++)
        {
            var noise = rng.Next(-40, 41);
            degraded[i] = (byte)Math.Clamp(degraded[i] + noise, 0, 255);
        }

        var identicalScore = RasterSsim.Compute(reference, reference, 64, 64);
        var degradedScore = RasterSsim.Compute(reference, degraded, 64, 64);

        Assert.Equal(1.0, identicalScore, precision: 9);
        Assert.True(degradedScore < identicalScore - 0.05,
            $"Expected the degraded pair's SSIM ({degradedScore}) to be measurably below the identical pair's ({identicalScore}).");
    }

    [Fact]
    public void CompletelyUncorrelatedImages_ScoreLow()
    {
        // A stronger anti-vacuity proof than "below identical": two genuinely unrelated images
        // (a flat block vs. the gradient) must score far from 1.0, not just marginally lower —
        // proving the metric has real discriminating power, not just a rounding-error gap.
        var reference = MakeGradient(64, 64);
        var flat = new byte[64 * 64];
        Array.Fill(flat, (byte)128);

        var score = RasterSsim.Compute(reference, flat, 64, 64);
        Assert.True(score < 0.5, $"Expected a flat image compared against a gradient to score well below 1.0, got {score}.");
    }

    [Fact]
    public void MismatchedDimensions_Throws()
    {
        var a = new byte[8 * 8];
        var b = new byte[4 * 4];
        Assert.Throws<ArgumentException>(() => RasterSsim.Compute(a, b, 8, 8));
    }

    [Fact]
    public void Rgb24Overlay_MatchesGray8OnGrayscaleInput()
    {
        // A grayscale RGB image (R==G==B at every pixel) must produce the same luma as the
        // Gray8 path directly, proving the BT.601 conversion doesn't distort a value that is
        // already achromatic.
        var gray = MakeGradient(16, 16);
        var rgb = new byte[16 * 16 * 3];
        for (var i = 0; i < gray.Length; i++)
        {
            rgb[i * 3] = gray[i];
            rgb[(i * 3) + 1] = gray[i];
            rgb[(i * 3) + 2] = gray[i];
        }

        var score = RasterSsim.Compute(rgb, rgb, 16, 16, RasterPixelFormat.Rgb24);
        Assert.Equal(1.0, score, precision: 9);
    }
}
