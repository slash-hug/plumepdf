using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks;

/// <summary>
/// PlumePdf-only raster codec throughput (the same maintainer/agent-run,
/// not-CI-gated convention every other suite in this project follows). Self-contained like
/// <see cref="OpenBenchmarks"/> — every fixture is built in-memory through PlumePDF's own
/// public API (<see cref="RasterImageFrame"/> + <c>EncodePng</c>/<c>EncodeJpeg</c>), not a
/// pinned corpus file, so <c>dotnet run</c> exercises this suite with no
/// <c>./scripts/fetch-corpora.sh</c> prerequisite.
/// </summary>
[MemoryDiagnoser]
public class RasterCodecBenchmarks
{
    private const int Width = 256;
    private const int Height = 256;

    // JP2_CODEC_256_B64 -- a 256x256 gradient (the SAME pixel pattern DecodePng/DecodeJpeg
    // below decode/encode, i.e. `(byte)((i * 37) % 256)` per RGB byte, so this suite compares
    // like-for-like across codecs) JP2, produced ONCE by:
    //   opj_compress -i codec256.ppm -o codec256.jp2 -r 20
    // (codec256.ppm: a raw 256x256 P6 PPM of the same gradient, OpenJPEG 2.5.4). Informational
    // only: unlike RasterizeBenchmarks.RasterizeJpxScanPage, this key is never referenced
    // by benchmarks/perf-baselines/raster-baselines.json and ci.yml's raster-perf-gate job
    // filters on `*RasterizeBenchmarks*` only, so this class's suite (like every other one here)
    // stays manual/per-phase, not CI-gated.
    private const string JP2_CODEC_256_B64 =
        "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" +
        "iP3NefdB7qcuq81595rz7oPdTl1XmvPvNefdB7qcuq81595rz7oPdTl1XmvPvNefdB7qcuq81595rz7oPdTl1XmvPvNefdB7qcuq" +
        "81595rz7oPdTl1XmvPvNefdB7qcuq81595rz7oPdTl1XmvPvNefdB7qcuq81595rz7oPdTl1XmvPvNefdB7qcuq81595rz7oPdTl" +
        "1XmvPvNefdB7qcuq81595rz7oPdJb0gRC8gHLClpDuPbeL6RU/TqB/9/6wcDdoj9zXnpA4Muekk5xa5rz7zXn72vuRJI8G1D7q3N" +
        "efuX8BIkdCjZzXn3mvP3x8I8SOhRs5rz7zXn74+EeJHQo2c1595rz98fCPEjoUbOa8+815++PhHiR0KNnNefea8/fHwjxI6FGzmv" +
        "PvNefvj4R4kdCjZzXn3mvP3x8I8SOhRs5rz7zXn74+EeJHQo2c1595rz98fCPEjoUbOa8+815++PhHiR0KNnNefea8/fHwjxI6FG" +
        "zmvPvNefvj4R4kdCjZzXn3mvP3x8I8SLfXMkBgcILkOcZLOVj8jLP4sK5XKgHeTB2tgBWUsWS4AaxNytwAvW7hrgAxLBdl/+9DBd" +
        "l/9ehguy/3vQwXZf/vQwXZf/XoYLsv970MF2X/70MF2X/16GC7L/e9DBdl/+9DBdmAB//3/Pv6HAFBBHWd+9V+UJd7vFYp7jatVh" +
        "fTNP/3//f/9/xrKCVea8+81595rz7zXn3mvPvNefea8+81595rz7zXn3mvPvNefea8+81595rz7zXn3mvPvNefea8+81595rz7zX" +
        "n3mvPvNefea8+81595rz7zXn3mvPvNefea8+81595rz7zXn3mvPvNefea8+81595rz7zXn3mvPvNefea8+81595rz7zXn3mvPvNe" +
        "fea8+81595rz7zXn3mvPvNefea8+81595rz7zXn3mvPvNefea8+81595ryktk+Sjwkw3muGNX7hVDlUHW8el/3//ewVCEq818RfG" +
        "8+82E/D3zXn3mvR0+V595zm6R5rz7zXk7r1Xn3oAp9ea8+815MY81595++inNefea8mMea8+8/fRTmvPvNeTGPNefefvopzXn3mv" +
        "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";

    private byte[] _rgbPixels = null!;
    private byte[] _pngBytes = null!;
    private byte[] _jpegBytes = null!;
    private byte[] _jp2Bytes = null!;
    private RasterImageFrame _frame = null!;
    private string _fromImagesOutputPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rgbPixels = new byte[Width * Height * 3];
        for (var i = 0; i < _rgbPixels.Length; i++)
        {
            // A gradient, not a flat fill — flat-color blocks are pathologically
            // easy for both PNG's predictor and JPEG's DCT, and would understate
            // real-world decode/encode cost.
            _rgbPixels[i] = (byte)((i * 37) % 256);
        }

        _frame = new RasterImageFrame(_rgbPixels, Width, Height, RasterPixelFormat.Rgb24);
        _pngBytes = _frame.EncodePng();
        _jpegBytes = _frame.EncodeJpeg(quality: 90);
        _jp2Bytes = Convert.FromBase64String(JP2_CODEC_256_B64);
        _fromImagesOutputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-fromimages-{Guid.NewGuid():N}.pdf");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (File.Exists(_fromImagesOutputPath))
        {
            File.Delete(_fromImagesOutputPath);
        }
    }

    [Benchmark(Baseline = true)]
    public int DecodePng() => RasterImage.Decode(_pngBytes).Frames[0].Pixels.Length;

    [Benchmark]
    public int DecodeJpeg() => RasterImage.Decode(_jpegBytes).Frames[0].Pixels.Length;

    /// <summary>
    /// JPEG 2000 / JPXDecode: informational only — NOT referenced by
    /// <c>benchmarks/perf-baselines/raster-baselines.json</c> or ci.yml's <c>raster-perf-gate</c>
    /// job filter (<c>RasterizeBenchmarks.RasterizeJpxScanPage</c> is the one gated scenario, since
    /// a <c>DecodeJpx…</c> key here would never fire under that filter). Same 256x256 gradient
    /// <see cref="DecodePng"/>/<see cref="DecodeJpeg"/> exercise, via
    /// <see cref="RasterImage.Decode(ReadOnlyMemory{byte},PdfOptions?)"/>'s JP2 signature sniff.
    /// </summary>
    [Benchmark]
    public int DecodeJpx() => RasterImage.Decode(_jp2Bytes).Frames[0].Pixels.Length;

    [Benchmark]
    public int EncodePng() => _frame.EncodePng().Length;

    [Benchmark]
    public int EncodeJpeg() => _frame.EncodeJpeg(quality: 90).Length;

    /// <summary>The end-to-end "scan -> PDF" verb: decode + compose + write, for a small multi-page batch.</summary>
    [Benchmark]
    public long FromImagesToFile()
    {
        using var document = Pdf.FromImages([_pngBytes, _pngBytes, _pngBytes]);
        document.Save(_fromImagesOutputPath, new PdfOptions { Deterministic = true });
        return new FileInfo(_fromImagesOutputPath).Length;
    }
}
