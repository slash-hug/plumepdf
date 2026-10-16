using System.Globalization;
using System.Text;
using PlumePdf.Filters;
using Xunit;
using Xunit.Abstractions;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The visual floor for <c>Pdf.Compress</c>: each page of the input and of the compressed
/// output, rasterized in-house at 150 DPI, must score at least the governed per-setting floor
/// in <c>thresholds/ssim.json</c> (the <c>compress-*</c> keys under <c>perTestFloors</c>). One
/// floor per lever setting, each the lowest score its fixtures measured less 0.005. The
/// fixtures are generated scan-like pages, a photographic JPEG and a lossless gray
/// text-on-paper scan, each placed at 320, 260 and 200 DPI, so every downsampling setting has
/// images on both sides of its 1.5 × <c>MaxDpi</c> trigger. A PDFium leg repeats the comparison
/// with both pages rendered by the oracle, when the shim is installed.
/// </summary>
public class CompressSsimTests(ITestOutputHelper output)
{
    private const double RenderDpi = 150;
    private const int PageEdgePoints = 288; // 4 inches: a 600-pixel render at 150 DPI.

    private static readonly int[] PlacementDpis = [320, 260, 200];

    public static TheoryData<string> Settings() => new(SettingsByKey.Keys);

    [Theory]
    [MemberData(nameof(Settings))]
    public void CompressedPages_MeetTheSettingsFloor(string key)
    {
        var settings = SettingsByKey[key];
        var floor = RasterSsim.LoadPerTestFloor(key);
        var scores = new List<double>();
        foreach (var fixture in Fixtures())
        {
            WithCompressed(fixture, settings, (input, compressed, result) =>
            {
                AssertTheTriggerWasHonoured(key, settings, fixture, result);
                var options = PdfRasterizeOptions.Default with { Dpi = RenderDpi };
                var before = Pdf.Rasterize(input, options).Frames[0];
                var after = Pdf.Rasterize(compressed, options).Frames[0];
                var score = RasterSsim.ComputeFrames(before, after);
                scores.Add(score);
                output.WriteLine($"[SSIM] {key,-28} {fixture.Name,-14} score={score:0.0000} action={result.Images[0].Action}");
            });
        }

        Assert.Equal(Fixtures().Count(), scores.Count);
        var min = scores.Min();
        Assert.True(min >= floor, $"{key}: the lowest page SSIM {min:0.0000} is below the governed floor {floor:0.0000}.");
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void CompressedPages_MeetTheSettingsFloor_RenderedByPdfium(string key)
    {
        if (!PdfiumOracle.PdfiumAvailableOrFailIfRequired())
        {
            output.WriteLine($"SKIPPED (pdfium shim not installed at {PdfiumOracle.ShimPath})");
            return;
        }

        var settings = SettingsByKey[key];
        var floor = RasterSsim.LoadPerTestFloor(key);
        var pixelWidth = (int)(PageEdgePoints * RenderDpi / 72);
        foreach (var fixture in Fixtures())
        {
            WithCompressed(fixture, settings, (input, compressed, _) =>
            {
                var before = Path.Combine(Path.GetTempPath(), $"plumepdf-compress-ssim-{Guid.NewGuid():N}.png");
                var after = Path.Combine(Path.GetTempPath(), $"plumepdf-compress-ssim-{Guid.NewGuid():N}.png");
                try
                {
                    Assert.True(PdfiumOracle.TryRenderPage(input, 0, pixelWidth, before), $"pdfium failed to render the {fixture.Name} input.");
                    Assert.True(PdfiumOracle.TryRenderPage(compressed, 0, pixelWidth, after), $"pdfium failed to render the {fixture.Name} output under {key}.");
                    var score = RasterSsim.ComputeFrames(
                        RasterImage.Decode(File.ReadAllBytes(before)).Frames[0],
                        RasterImage.Decode(File.ReadAllBytes(after)).Frames[0]);
                    output.WriteLine($"[SSIM] {key,-28} {fixture.Name,-14} pdfium={score:0.0000}");
                    Assert.True(score >= floor, $"{key} / {fixture.Name}: PDFium-rendered SSIM {score:0.0000} is below the governed floor {floor:0.0000}.");
                }
                finally
                {
                    File.Delete(before);
                    File.Delete(after);
                }
            });
        }
    }

    [Fact]
    public void EverySetting_HasAGovernedFloor_AndTheGateRejectsADegradedPage()
    {
        foreach (var key in SettingsByKey.Keys)
        {
            var floor = RasterSsim.LoadPerTestFloor(key);
            Assert.InRange(floor, 0.5, 1.0);
        }

        // Anti-vacuity: an output with its image replaced by flat gray must fall below every floor.
        var fixture = Fixtures().First();
        var input = WriteTemp(fixture.Build());
        var flat = WriteTemp(new Fixture("flat", fixture.Dpi, Photo: true, Flat: true).Build());
        try
        {
            var options = PdfRasterizeOptions.Default with { Dpi = RenderDpi };
            var score = RasterSsim.ComputeFrames(Pdf.Rasterize(input, options).Frames[0], Pdf.Rasterize(flat, options).Frames[0]);
            var lowest = SettingsByKey.Keys.Min(RasterSsim.LoadPerTestFloor);
            Assert.True(score < lowest, $"A flat-gray page scored {score:0.0000}, not below the lowest compress floor {lowest:0.0000}.");
        }
        finally
        {
            File.Delete(input);
            File.Delete(flat);
        }
    }

    private static readonly Dictionary<string, PdfCompressOptions> SettingsByKey = new(StringComparer.Ordinal)
    {
        ["compress-levers-off"] = PdfCompressOptions.Default,
        ["compress-maxdpi-200"] = new() { Images = new PdfImageCompression { MaxDpi = 200 } },
        ["compress-maxdpi-150"] = new() { Images = new PdfImageCompression { MaxDpi = 150 } },
        ["compress-jpeg-85"] = new() { Images = new PdfImageCompression { JpegQuality = 85 } },
        ["compress-jpeg-70"] = new() { Images = new PdfImageCompression { JpegQuality = 70 } },
        ["compress-maxdpi-150-jpeg-70"] = new() { Images = new PdfImageCompression { MaxDpi = 150, JpegQuality = 70 } },
    };

    private static IEnumerable<Fixture> Fixtures() =>
        PlacementDpis.SelectMany(static dpi => new[] { new Fixture($"photo-{dpi}", dpi, Photo: true), new Fixture($"text-{dpi}", dpi, Photo: false) });

    // The decision must match the trigger, so the floor is measured on both sides of it.
    private static void AssertTheTriggerWasHonoured(string key, PdfCompressOptions settings, Fixture fixture, CompressionResult result)
    {
        var decision = Assert.Single(result.Images);
        var downsampled = decision.Action is ImageCompressionAction.Downsampled or ImageCompressionAction.DownsampledAndReencoded;
        var expected = settings.Images?.MaxDpi is double maxDpi && fixture.Dpi > maxDpi * 1.5;
        Assert.True(expected == downsampled, $"{key} / {fixture.Name}: action {decision.Action} at {fixture.Dpi} DPI.");
    }

    private static void WithCompressed(Fixture fixture, PdfCompressOptions settings, Action<string, string, CompressionResult> check)
    {
        var input = WriteTemp(fixture.Build());
        var compressed = Path.Combine(Path.GetTempPath(), $"plumepdf-compress-ssim-{Guid.NewGuid():N}.pdf");
        try
        {
            var result = Pdf.Compress(input, compressed, settings);
            check(input, compressed, result);
        }
        finally
        {
            File.Delete(input);
            File.Delete(compressed);
        }
    }

    private static string WriteTemp(byte[] pdf)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-compress-ssim-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    /// <summary>
    /// One 4-inch square page holding one image drawn over the page at <paramref name="Dpi"/>:
    /// a photographic RGB JPEG (gradients, soft shapes and sensor noise) or a lossless Flate gray
    /// scan of dark text-like strokes on noisy paper.
    /// </summary>
    private sealed record Fixture(string Name, int Dpi, bool Photo, bool Flat = false)
    {
        public byte[] Build()
        {
            var pixels = PageEdgePoints * Dpi / 72;
            byte[] payload;
            string dictionary;
            if (Photo)
            {
                var samples = Flat ? Enumerable.Repeat((byte)128, pixels * pixels * 3).ToArray() : PhotoSamples(pixels);
                payload = JpegEncoder.Encode(samples, pixels, pixels, 3, 90);
                dictionary = $"/Type /XObject /Subtype /Image /Width {pixels} /Height {pixels} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode";
            }
            else
            {
                payload = new FlateEncodingFilter().Encode(TextScanSamples(pixels), PdfOptions.Default);
                dictionary = $"/Type /XObject /Subtype /Image /Width {pixels} /Height {pixels} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode";
            }

            var content = $"q {PageEdgePoints} 0 0 {PageEdgePoints} 0 0 cm /Im0 Do Q";
            byte[] image = [.. Ascii($"<< {dictionary} /Length {payload.Length} >>\nstream\n"), .. payload, .. Ascii("\nendstream")];
            var objects = new List<byte[]>
            {
                Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
                Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageEdgePoints} {PageEdgePoints}] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>"),
                Ascii($"<< /Length {content.Length} >>\nstream\n{content}\nendstream"),
                image,
            };

            using var pdf = new MemoryStream();
            pdf.Write(Ascii("%PDF-1.7\n"));
            var offsets = new long[objects.Count];
            for (var i = 0; i < objects.Count; i++)
            {
                offsets[i] = pdf.Position;
                pdf.Write(Ascii($"{i + 1} 0 obj\n"));
                pdf.Write(objects[i]);
                pdf.Write(Ascii("\nendobj\n"));
            }

            var xref = pdf.Position;
            var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            pdf.Write(Ascii(table.ToString()));
            return pdf.ToArray();
        }

        private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

        // Diagonal colour gradients, a few soft discs, and texture at three scales (value noise
        // on 64-, 16- and 4-pixel grids at a 320 DPI placement, scaled with the DPI so every
        // placement shows the same picture) plus per-pixel sensor noise: detail a 150 DPI render
        // still resolves, so a lost or smeared image scores visibly lower.
        private static byte[] PhotoSamples(int size)
        {
            var random = new Random(7);
            var scale = size / 1280.0;
            var octaves = new[] { (Cell: 64 * scale, Amplitude: 45.0), (Cell: 16 * scale, Amplitude: 30.0), (Cell: 4 * scale, Amplitude: 20.0) }
                .Select(o => (o.Cell, o.Amplitude, Grid: RandomGrid(random, (int)Math.Ceiling(size / o.Cell) + 2)))
                .ToArray();
            var samples = new byte[size * size * 3];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var u = x / (double)size;
                    var v = y / (double)size;
                    var disc = 0.0;
                    foreach (var (cx, cy, r) in new[] { (0.3, 0.35, 0.18), (0.7, 0.6, 0.22), (0.45, 0.8, 0.1) })
                    {
                        var d = Math.Sqrt(((u - cx) * (u - cx)) + ((v - cy) * (v - cy)));
                        disc += Math.Clamp((r - d) / 0.03, 0, 1);
                    }

                    var texture = 0.0;
                    foreach (var (cell, amplitude, grid) in octaves)
                    {
                        texture += amplitude * ValueNoise(grid, x / cell, y / cell);
                    }

                    var i = ((y * size) + x) * 3;
                    samples[i] = Clamp((200 * u) + (50 * disc) + texture + random.Next(-8, 9));
                    samples[i + 1] = Clamp((120 + (100 * v)) - (60 * disc) + (0.8 * texture) + random.Next(-8, 9));
                    samples[i + 2] = Clamp((220 * (1 - u)) + (30 * v) - (0.6 * texture) + random.Next(-8, 9));
                }
            }

            return samples;
        }

        private static double[,] RandomGrid(Random random, int n)
        {
            var grid = new double[n, n];
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    grid[y, x] = (random.NextDouble() * 2) - 1;
                }
            }

            return grid;
        }

        // Bilinear interpolation of the grid at (gx, gy), in [-1, 1].
        private static double ValueNoise(double[,] grid, double gx, double gy)
        {
            int x0 = (int)gx, y0 = (int)gy;
            double fx = gx - x0, fy = gy - y0;
            var top = (grid[y0, x0] * (1 - fx)) + (grid[y0, x0 + 1] * fx);
            var bottom = (grid[y0 + 1, x0] * (1 - fx)) + (grid[y0 + 1, x0 + 1] * fx);
            return (top * (1 - fy)) + (bottom * fy);
        }

        // Paper (light, noisy) under rows of dark word-like stroke clusters, scaled with the DPI
        // so every placement shows the same 10-point-ish text.
        private static byte[] TextScanSamples(int size)
        {
            var random = new Random(11);
            var samples = new byte[size * size];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = Clamp(236 + random.Next(-10, 11));
            }

            var scale = size / 1280.0; // strokes sized for a 320 DPI placement, scaled down from there
            var lineHeight = (int)(56 * scale);
            var glyphHeight = (int)(30 * scale);
            for (var top = (int)(60 * scale); top + glyphHeight < size - (int)(40 * scale); top += lineHeight)
            {
                var x = (int)(50 * scale);
                while (x < size - (int)(80 * scale))
                {
                    var word = random.Next(3, 9);
                    for (var g = 0; g < word && x < size - (int)(60 * scale); g++)
                    {
                        var width = (int)(random.Next(14, 26) * scale);
                        var stroke = Math.Max(1, (int)(random.Next(3, 6) * scale));
                        DrawGlyph(samples, size, x, top, width, glyphHeight, stroke, random.Next(4));
                        x += width + (int)(5 * scale);
                    }

                    x += (int)(22 * scale);
                }
            }

            return samples;
        }

        // A glyph-like box: two vertical stems plus a horizontal bar at one of four heights.
        private static void DrawGlyph(byte[] samples, int size, int left, int top, int width, int height, int stroke, int shape)
        {
            void Fill(int x0, int y0, int w, int h)
            {
                for (var y = Math.Max(0, y0); y < Math.Min(size, y0 + h); y++)
                {
                    for (var x = Math.Max(0, x0); x < Math.Min(size, x0 + w); x++)
                    {
                        samples[(y * size) + x] = (byte)(30 + ((x * 7 + y * 13) % 25));
                    }
                }
            }

            Fill(left, top, stroke, height);
            if (shape != 3)
            {
                Fill(left + width - stroke, top, stroke, height);
            }

            var bar = shape switch { 0 => top, 1 => top + (height / 2), 2 => top + height - stroke, _ => top + (height / 3) };
            Fill(left, bar, width, stroke);
        }

        private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }
}
