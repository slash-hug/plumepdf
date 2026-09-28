using System.IO.Compression;
using System.Text;
using BenchmarkDotNet.Attributes;
using PlumePdf.Elements;

namespace PlumePdf.Benchmarks;

/// <summary>
/// Phase 9 rasterizer-completeness benchmark suite: PlumePDF-only
/// throughput for <see cref="Pdf.Rasterize(string,PdfRasterizeOptions?)"/>/
/// <see cref="PdfPage.Rasterize"/> across the three content shapes the phase's completeness work
/// actually touches (dense text, embedded raster images, alpha-compositing watermark/stamp
/// overlays) plus a multi-document parallel-throughput suite proving the per-call scratch
/// <c>ScratchObjectRegistry</c> read-only-invariant mechanism doesn't cost
/// parallel throughput. Self-contained like <see cref="CreationBenchmarks"/>/
/// <see cref="RasterCodecBenchmarks"/> — every fixture is built in-memory through PlumePDF's own
/// public creation API, no corpus fixture or <c>fetch-corpora.sh</c> prerequisite. Results are
/// compared against <c>benchmarks/perf-baselines/raster-baselines.json</c> by
/// <c>scripts/check-raster-perf-baseline.sh</c> — a per-benchmark % tolerance band, same-platform
/// runner only, NOT a per-commit CI gate on this suite's raw numbers (macro
/// suites run manually/per-phase, mirroring every other suite here). JPEG
/// 2000/JPXDecode support adds <see cref="RasterizeJpxScanPage"/>: unlike the three
/// suites above, the CI <c>raster-perf-gate</c> job runs THIS class's benchmarks on every
/// push (<c>ci.yml</c>'s <c>--filter '*RasterizeBenchmarks*'</c>), so it is a real
/// per-commit gate for this one suite, not the manual/per-phase convention every other
/// benchmark class in this project follows.
/// </summary>
/// <remarks>
/// Every page renders at a fixed 2048px width via
/// <see cref="TargetDpi"/>, a DPI computed once from <see cref="PageSize.A4"/>'s own point width
/// so every suite in this class rasterizes at the same effective pixel width regardless of page
/// content.
/// </remarks>
[MemoryDiagnoser]
public class RasterizeBenchmarks
{
    private const int TargetPixelWidth = 2048;

    /// <summary>The DPI that renders an A4-width page (595.28pt = 8.2678in) at exactly <see cref="TargetPixelWidth"/> pixels wide.</summary>
    private static readonly double TargetDpi = TargetPixelWidth * 72.0 / PageSize.A4.Width;

    private static readonly PdfRasterizeOptions RasterizeOptions = PdfRasterizeOptions.Default with { Dpi = TargetDpi };

    // JP2_SCAN_B64 -- 640x480 /DeviceRGB scan-like page (near-white paper, dark text-like
    // strokes, ruled boxes, sensor noise), 9/7 wavelet + ICT at a real ~20x compression ratio --
    // the shape and rate of real-world W-9 scans. The IDENTICAL literal
    // tests/PlumePdf.CorpusTests/Fixtures/generate_image_fixtures.py embeds for jpx-scan.pdf
    // (this benchmark stays self-contained, no filesystem walk-up to that fixture),
    // produced ONCE by:
    //   python3 -c "import sys; sys.path.insert(0,'scripts/jpx-fixtures'); \
    //               from generate import write_scan_ppm; from pathlib import Path; \
    //               write_scan_ppm(Path('scan.ppm'), 640, 480, seed=53)"
    //   opj_compress -i scan.ppm -o scan.jp2 -I -r 20 -n 6 -t 320,240 -p RPCL
    // (write_scan_ppm is scripts/jpx-fixtures/generate.py's deterministic scan-source generator,
    // reused here with the SAME seed=53 it already uses for its own scan-97-ict-640x480 unit
    // fixture -- OpenJPEG 2.5.4, no PIL/PNG library involved). Base64: 46,044 source bytes ->
    // 61,392 base64 characters. A single tile, 320x240 tiles -> 2x2 grid; RPCL progression, 6
    // resolution levels.
    private const string JP2_SCAN_B64 =
        "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";

    private string _textHeavyPath = null!;
    private string _imageHeavyPath = null!;
    private string _transparencyHeavyPath = null!;
    private byte[] _parallelThroughputDocBytes = null!;
    private byte[] _jpxScanPdfBytes = null!;
    private byte[] _scanMinifyPdfBytes = null!;
    private byte[] _scanMagnifyPdfBytes = null!;

    /// <summary><see cref="RasterizeScanPageMinify"/>'s options — <c>Dpi = 150</c> on a Letter page carrying a 2550x3300 source image is exactly a 2x minify.</summary>
    private static readonly PdfRasterizeOptions ScanMinifyOptions = PdfRasterizeOptions.Default with { Dpi = 150 };

    /// <summary><see cref="RasterizeScanPageMagnify"/>'s options — a caller rendering to a fixed 2048px square target against a 1275x1650 source is a ~1.6x magnify on the dominant axis.</summary>
    private static readonly PdfRasterizeOptions ScanMagnifyOptions = PdfRasterizeOptions.Default with { PixelWidth = 2048, PixelHeight = 2048, Dpi = null };

    [GlobalSetup]
    public void Setup()
    {
        _textHeavyPath = SaveToTempFile(BuildTextHeavyManuscript().Render());
        _imageHeavyPath = SaveToTempFile(BuildImageHeavyManuscript().Render());
        _transparencyHeavyPath = SaveToTempFile(BuildTransparencyHeavyManuscript().Render());

        // Saved once here (not per-iteration) so the parallel-throughput benchmark below
        // measures N independent PdfDocument.Open + Rasterize calls racing against each other —
        // the shape the per-call scratch-registry concurrency proof is
        // actually meant to keep cheap — not N redundant Manuscript.Render/Save calls.
        var parallelDocPath = SaveToTempFile(BuildTextHeavyManuscript().Render());
        _parallelThroughputDocBytes = File.ReadAllBytes(parallelDocPath);
        File.Delete(parallelDocPath);

        _jpxScanPdfBytes = ComposeJpxScanPdfBytes();
        _scanMinifyPdfBytes = ComposeScanMinifyPdfBytes();
        _scanMagnifyPdfBytes = ComposeScanMagnifyPdfBytes();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var path in new[] { _textHeavyPath, _imageHeavyPath, _transparencyHeavyPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Dense multi-paragraph text — glyph-shaping and glyph-rasterization throughput.</summary>
    [Benchmark(Baseline = true)]
    public int RasterizeTextHeavyPage() => Pdf.Rasterize(_textHeavyPath, RasterizeOptions).Frames[0].Pixels.Length;

    /// <summary>Several embedded raster images — image-XObject decode/blit throughput.</summary>
    [Benchmark]
    public int RasterizeImageHeavyPage() => Pdf.Rasterize(_imageHeavyPath, RasterizeOptions).Frames[0].Pixels.Length;

    /// <summary>A watermark plus several corner stamps, all partially transparent — alpha-compositing throughput.</summary>
    [Benchmark]
    public int RasterizeTransparencyHeavyPage() => Pdf.Rasterize(_transparencyHeavyPath, RasterizeOptions).Frames[0].Pixels.Length;

    /// <summary>
    /// The gated JPEG 2000/JPXDecode perf scenario. Decodes and paints
    /// the same 640x480 <see cref="JP2_SCAN_B64"/> payload <c>generate_image_fixtures.py</c>
    /// embeds for <c>jpx-scan.pdf</c> (9/7 irreversible, ICT, <c>-r 20</c>, tiled RPCL — the
    /// exact producer shape real-world scanned W-9s use), placed full-page. Unlike
    /// every other benchmark in this class, the CI <c>raster-perf-gate</c> job actually gates on
    /// this one every push (<c>ci.yml</c>'s <c>--filter '*RasterizeBenchmarks*'</c> plus
    /// <c>benchmarks/perf-baselines/raster-baselines.json</c>'s
    /// <c>RasterizeBenchmarks.RasterizeJpxScanPage</c> key), so this is a perf gate that
    /// actually fires from day one — a
    /// <c>DecodeJpx…</c> key inside <see cref="RasterCodecBenchmarks"/> alone would never fire
    /// under that filter.
    /// </summary>
    [Benchmark]
    public int RasterizeJpxScanPage()
    {
        using var document = PdfDocument.Open(_jpxScanPdfBytes);
        return document.Pages[0].Rasterize(RasterizeOptions).Frames[0].Pixels.Length;
    }

    /// <summary>
    /// A real-world image-minify shape — a Letter page
    /// (612x792pt) carrying one full-page 2550x3300 1-bit <c>DeviceGray</c> Flate image (a 300
    /// dpi scan), rasterized at <see cref="ScanMinifyOptions"/>'s <c>Dpi = 150</c> (1275x1650
    /// target) — a clean 2x minify on both axes, so <see cref="ImagePainter.Decide"/> resolves
    /// <see cref="AxisMode.Box"/> on both regardless of <c>ImageResampling</c> mode. Joins
    /// <see cref="RasterizeJpxScanPage"/> as a CI <c>raster-perf-gate</c> key
    /// (<c>raster-baselines.json</c>) once baselined, pairing
    /// with <see cref="RasterizeScanPageMagnify"/> so every resampling path
    /// gets a minify AND a magnify fixture.
    /// </summary>
    [Benchmark]
    public int RasterizeScanPageMinify()
    {
        using var document = PdfDocument.Open(_scanMinifyPdfBytes);
        return document.Pages[0].Rasterize(ScanMinifyOptions).Frames[0].Pixels.Length;
    }

    /// <summary>
    /// A magnify shape a caller rendering to a fixed square target hits — a 1275x1650pt
    /// page carrying one full-page 1275x1650 8-bit <c>DeviceGray</c> Flate image, rasterized at
    /// an explicit 2048x2048 pixel target (<see cref="ScanMagnifyOptions"/>) — ~1.6x magnify on
    /// the dominant axis, squarely inside the <c>Auto</c> mode's PDFium-parity interpolation
    /// cut-off, so this is the scenario the dedicated axis-aligned bilinear-magnify loop
    /// (<see cref="ImagePainter"/>) exists for. The local before/after this benchmark's PR body
    /// records (nearest vs the new bilinear path) is the ≤1.5x acceptance check this feature
    /// required.
    /// </summary>
    [Benchmark]
    public int RasterizeScanPageMagnify()
    {
        using var document = PdfDocument.Open(_scanMagnifyPdfBytes);
        return document.Pages[0].Rasterize(ScanMagnifyOptions).Frames[0].Pixels.Length;
    }

    /// <summary>
    /// Four independent documents (each its own <c>PdfDocument.Open</c>, per the read-only,
    /// scratch-registry-per-call contract) rasterized concurrently via
    /// <see cref="Parallel.For(int,int,Action{int})"/>. Throughput-focused counterpart to
    /// <c>tests/PlumePdf.CorpusTests/ConcurrencyStressTests.cs</c>'s correctness proof (that
    /// class asserts pixel-identity; this one only measures wall-clock cost).
    /// </summary>
    [Benchmark]
    public int RasterizeFourDocumentsInParallel()
    {
        var totalPixelBytes = new int[4];
        Parallel.For(0, 4, i =>
        {
            using var document = PdfDocument.Open(_parallelThroughputDocBytes);
            totalPixelBytes[i] = document.Pages[0].Rasterize(RasterizeOptions).Frames[0].Pixels.Length;
        });

        var sum = 0;
        foreach (var b in totalPixelBytes)
        {
            sum += b;
        }

        return sum;
    }

    /// <summary>
    /// Composes a minimal, self-contained one-page PDF entirely byte-wise — no
    /// <c>PdfDocument</c>/<c>Manuscript</c> writer API, no filesystem walk-up, no corpus
    /// prerequisite (this class's own invariant, <see cref="RasterizeBenchmarks"/>'s summary
    /// remarks) — placing <see cref="JP2_SCAN_B64"/>'s 640x480 <c>/JPXDecode</c> payload
    /// full-page via a classic-xref PDF: objects 1-3 the page tree, 4 the content stream, 5 the
    /// image XObject. Mirrors <c>tests/PlumePdf.CorpusTests/Fixtures/generate_image_fixtures.py</c>'s
    /// <c>build_pdf</c>/<c>image_stream</c> shape (same image dictionary entries jpx-scan.pdf
    /// uses) without depending on that Python tool or its output file.
    /// </summary>
    private static byte[] ComposeJpxScanPdfBytes()
    {
        var jp2Bytes = Convert.FromBase64String(JP2_SCAN_B64);

        var buffer = new List<byte>(jp2Bytes.Length + 4096);
        var offsets = new int[6]; // index 0 unused; 1..5 hold each object's byte offset.

        void Append(string ascii) => buffer.AddRange(Encoding.ASCII.GetBytes(ascii));
        void AppendBytes(byte[] bytes) => buffer.AddRange(bytes);
        void StartObject(int number)
        {
            offsets[number] = buffer.Count;
            Append($"{number} 0 obj\n");
        }

        Append("%PDF-1.7\n");
        AppendBytes([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // Binary-content marker comment (ISO 32000-1 §7.5.2).

        StartObject(1);
        Append("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        StartObject(2);
        Append("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        StartObject(3);
        Append(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 640 480] " +
            "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");

        var content = Encoding.ASCII.GetBytes("q 640 0 0 480 0 0 cm\n/Im0 Do\nQ");
        StartObject(4);
        Append($"<< /Length {content.Length} >>\nstream\n");
        AppendBytes(content);
        Append("\nendstream\nendobj\n");

        StartObject(5);
        Append(
            "<< /Type /XObject /Subtype /Image /Width 640 /Height 480 /ColorSpace /DeviceRGB " +
            $"/BitsPerComponent 8 /Filter /JPXDecode /Length {jp2Bytes.Length} >>\nstream\n");
        AppendBytes(jp2Bytes);
        Append("\nendstream\nendobj\n");

        var xrefOffset = buffer.Count;
        Append("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
        {
            Append($"{offsets[i]:D10} 00000 n \n");
        }

        Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n");
        Append(xrefOffset.ToString());
        Append("\n%%EOF");

        return [.. buffer];
    }

    /// <summary>
    /// The shared classic-xref, one-page, one-full-page-image PDF shape
    /// <see cref="ComposeScanMinifyPdfBytes"/>/<see cref="ComposeScanMagnifyPdfBytes"/> both use
    /// (the same no-writer-API, no-filesystem-prerequisite pattern <see cref="ComposeJpxScanPdfBytes"/>
    /// establishes, factored out since two more scenarios now share it).
    /// </summary>
    private static byte[] ComposeFullPageImagePdfBytes(int mediaWidth, int mediaHeight, string imageDictEntries, byte[] imageBytes)
    {
        var buffer = new List<byte>(imageBytes.Length + 4096);
        var offsets = new int[6];

        void Append(string ascii) => buffer.AddRange(Encoding.ASCII.GetBytes(ascii));
        void AppendBytes(byte[] bytes) => buffer.AddRange(bytes);
        void StartObject(int number)
        {
            offsets[number] = buffer.Count;
            Append($"{number} 0 obj\n");
        }

        Append("%PDF-1.7\n");
        AppendBytes([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // Binary-content marker comment (ISO 32000-1 §7.5.2).

        StartObject(1);
        Append("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        StartObject(2);
        Append("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        StartObject(3);
        Append(
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {mediaWidth} {mediaHeight}] " +
            "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");

        var content = Encoding.ASCII.GetBytes($"q {mediaWidth} 0 0 {mediaHeight} 0 0 cm\n/Im0 Do\nQ");
        StartObject(4);
        Append($"<< /Length {content.Length} >>\nstream\n");
        AppendBytes(content);
        Append("\nendstream\nendobj\n");

        StartObject(5);
        Append($"<< /Type /XObject /Subtype /Image {imageDictEntries} /Length {imageBytes.Length} >>\nstream\n");
        AppendBytes(imageBytes);
        Append("\nendstream\nendobj\n");

        var xrefOffset = buffer.Count;
        Append("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
        {
            Append($"{offsets[i]:D10} 00000 n \n");
        }

        Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n");
        Append(xrefOffset.ToString());
        Append("\n%%EOF");

        return [.. buffer];
    }

    /// <summary><see cref="RasterizeScanPageMinify"/>'s fixture — a Letter page, one full-page 2550x3300 1-bit DeviceGray Flate image.</summary>
    private static byte[] ComposeScanMinifyPdfBytes()
    {
        var raw = GenerateSyntheticScanBitonal(2550, 3300);
        var flate = DeflateZLib(raw);
        const string dict = "/Width 2550 /Height 3300 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /FlateDecode";
        return ComposeFullPageImagePdfBytes(612, 792, dict, flate);
    }

    /// <summary><see cref="RasterizeScanPageMagnify"/>'s fixture — a 1275x1650pt page, one full-page 1275x1650 8-bit DeviceGray Flate image.</summary>
    private static byte[] ComposeScanMagnifyPdfBytes()
    {
        var raw = GenerateSyntheticScanGray8(1275, 1650);
        var flate = DeflateZLib(raw);
        const string dict = "/Width 1275 /Height 1650 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode";
        return ComposeFullPageImagePdfBytes(1275, 1650, dict, flate);
    }

    /// <summary>RFC 1950 zlib-wrapped deflate — exactly the byte shape PDF's <c>/FlateDecode</c> expects, with no dependency on the Python fixture generator.</summary>
    private static byte[] DeflateZLib(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// A deterministic synthetic "scanned form" bitonal page (1 bpc DeviceGray, default
    /// <c>/Decode [0 1]</c> so sample 1 = white, 0 = black): horizontal/vertical rule lines, a
    /// grid of short LCG-jittered "text-like" strokes, and sparse LCG speckle noise — no
    /// System.Random dependency (matches the corpus fixture generator's own determinism
    /// convention), no filesystem prerequisite.
    /// </summary>
    private static byte[] GenerateSyntheticScanBitonal(int width, int height)
    {
        var stride = (width + 7) / 8;
        var packed = new byte[stride * height];
        Array.Fill(packed, (byte)0xFF); // Default white; content clears bits to black.

        void SetBlack(int x, int y)
        {
            if ((uint)x >= (uint)width || (uint)y >= (uint)height)
            {
                return;
            }

            packed[(y * stride) + (x / 8)] &= (byte)~(0x80 >> (x % 8));
        }

        for (var y = 100; y < height; y += 120)
        {
            for (var x = 60; x < width - 60; x++)
            {
                SetBlack(x, y);
            }
        }

        for (var y = 60; y < height - 60; y++)
        {
            SetBlack(80, y);
        }

        uint seed = 0x2545F491u;
        uint Next()
        {
            seed = (seed * 1664525u) + 1013904223u;
            return seed;
        }

        for (var row = 150; row < height - 150; row += 40)
        {
            var x = 100;
            while (x < width - 200)
            {
                var runLength = 4 + (int)(Next() % 20);
                var jitter = (int)(Next() % 3);
                for (var i = 0; i < runLength; i++)
                {
                    SetBlack(x + i, row + jitter);
                }

                x += runLength + 4 + (int)(Next() % 12);
            }
        }

        var noiseCount = (width * height) / 400;
        for (var i = 0; i < noiseCount; i++)
        {
            SetBlack((int)(Next() % (uint)width), (int)(Next() % (uint)height));
        }

        return packed;
    }

    /// <summary>The 8-bit-per-sample counterpart of <see cref="GenerateSyntheticScanBitonal"/>: a near-white paper background with faint per-pixel LCG noise, dark rule lines, and mid-gray LCG-jittered text-like strokes — <see cref="RasterizeScanPageMagnify"/>'s fixture content.</summary>
    private static byte[] GenerateSyntheticScanGray8(int width, int height)
    {
        var pixels = new byte[width * height];
        uint seed = 0x9E3779B9u;
        uint Next()
        {
            seed = (seed * 1664525u) + 1013904223u;
            return seed;
        }

        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(250 - (Next() % 8));
        }

        void SetGray(int x, int y, byte value)
        {
            if ((uint)x < (uint)width && (uint)y < (uint)height)
            {
                pixels[(y * width) + x] = value;
            }
        }

        for (var y = 80; y < height; y += 100)
        {
            for (var x = 50; x < width - 50; x++)
            {
                SetGray(x, y, 30);
            }
        }

        for (var row = 130; row < height - 130; row += 34)
        {
            var x = 90;
            while (x < width - 160)
            {
                var runLength = 4 + (int)(Next() % 18);
                var shade = (byte)(20 + (Next() % 60));
                var jitter = (int)(Next() % 3);
                for (var i = 0; i < runLength; i++)
                {
                    SetGray(x + i, row + jitter, shade);
                }

                x += runLength + 4 + (int)(Next() % 10);
            }
        }

        return pixels;
    }

    private static string SaveToTempFile(PdfDocument document)
    {
        using (document)
        {
            var path = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-rasterize-{Guid.NewGuid():N}.pdf");
            document.Save(path, new PdfOptions { Deterministic = true });
            return path;
        }
    }

    private static Manuscript BuildTextHeavyManuscript()
    {
        // ~55 short paragraphs of varied-length text — enough glyph runs on one A4 page to make
        // shaping/glyph-rasterization the dominant cost, without spilling onto a second page
        // (Pdf.Rasterize/PdfPage.Rasterize only ever renders the requested single page).
        const string paragraph =
            "PlumePDF renders vector text, raster images, and transparency groups directly onto " +
            "an in-process raster surface without shelling out to an external renderer.";

        var children = new Element[55];
        for (var i = 0; i < children.Length; i++)
        {
            children[i] = new Text($"{i + 1}. {paragraph}") { FontSize = 9 };
        }

        return new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = PageSize.A4,
                    Margins = Margins.Uniform(36),
                    Body = new Column(children) { Spacing = 4 },
                },
            ],
        };
    }

    private static Manuscript BuildImageHeavyManuscript()
    {
        // Six distinct in-memory-decoded gradient images (no two identical, so no rasterizer
        // image cache could make this suite silently measure something else entirely) stacked
        // top to bottom, each large enough (300x200) that decode + blit dominates over layout.
        var children = new Element[6];
        for (var i = 0; i < children.Length; i++)
        {
            children[i] = new Image(MakeGradientRgbPixels(300, 200, seed: i), pixelWidth: 300, pixelHeight: 200) { Width = 500 };
        }

        return new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = PageSize.A4,
                    Margins = Margins.Uniform(36),
                    Body = new Column(children) { Spacing = 8 },
                },
            ],
        };
    }

    private static Manuscript BuildTransparencyHeavyManuscript()
    {
        // Body content plus a full-page rotated watermark and four corner stamps, each at a
        // different partial opacity — exercises the transparency-group/soft-compositing path on
        // top of ordinary content, not just a single translucent rectangle.
        var body = new Column(
            new Text("Transparency-heavy benchmark page") { FontSize = 16, Bold = true },
            new Text("Body content composites underneath a semi-transparent watermark and four corner stamps."))
        {
            Spacing = 12,
        };

        return new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = PageSize.A4,
                    Margins = Margins.Uniform(36),
                    Body = body,
                    Watermark = new Watermark { Text = "SAMPLE", Opacity = 0.2 },
                    Stamps =
                    [
                        new Stamp { Text = "DRAFT", Position = StampPosition.TopLeft, Opacity = 0.8 },
                        new Stamp { Text = "REVIEW", Position = StampPosition.TopRight, Opacity = 0.6 },
                        new Stamp { Text = "CONFIDENTIAL", Position = StampPosition.BottomLeft, Opacity = 0.4 },
                        new Stamp { Text = "COPY", Position = StampPosition.BottomRight, Opacity = 0.25 },
                    ],
                },
            ],
        };
    }

    private static byte[] MakeGradientRgbPixels(int width, int height, int seed)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < pixels.Length; i++)
        {
            // A per-image-distinct, non-flat pattern (flat fills are pathologically cheap to
            // blit and would understate real-world image-paint cost).
            pixels[i] = (byte)(((i * 37) + (seed * 53)) % 256);
        }

        return pixels;
    }
}
