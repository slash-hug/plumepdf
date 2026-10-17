using PlumePdf.Filters;
using static PlumePdf.Tests.Compression.CompressFixtures;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// Source documents for the <c>Pdf.EstimateCompression</c> tests: a homogeneous many-page JPEG
/// scan, a mixed document (photos, text-like images, soft masks, duplicates, skips, an inline
/// image), and a document with more sampling groups than the per-call sample cap.
/// </summary>
internal static class CompressEstimateFixtures
{
    /// <summary>The levers the scan fixtures are estimated and compressed with.</summary>
    public static PdfCompressOptions ScanLevers { get; } = new() { Images = new PdfImageCompression { MaxDpi = 100, JpegQuality = 70 } };

    /// <summary>
    /// <paramref name="pages"/> pages, each drawing its own baseline JPEG "scan" (900 × 1100
    /// pixels on a 4 × 4.9 inch square: 225 DPI) whose noise and text-like bars vary by page,
    /// so the per-page sizes spread the way a real scan's do.
    /// </summary>
    public static ScanTestPdf JpegScan(int pages = 24)
    {
        var pdf = new ScanTestPdf();
        for (var page = 0; page < pages; page++)
        {
            var image = 10 + (page * 3);
            pdf.Stream(image, Image(900, 1100, filter: "/DCTDecode"), JpegEncoder.Encode(ScanSamples(900, 1100, 3, page), 900, 1100, 3, 85));
            pdf.Page(image + 1, Draw("Im0", 288), xobjects: $"/Im0 {image} 0 R");
        }

        return pdf;
    }

    /// <summary>
    /// A mixed document. Page 1: a large Flate photo (10), drawn at 300 DPI, with a soft mask
    /// (12); a JPEG photo (14). Page 2: a text-like gray Flate image (16) drawn at 300 DPI; a
    /// duplicate of 10 (18, merged into it); a bilevel image (20, skipped); an inline image.
    /// Page 3: an unfiltered RGB photo (22) at 300 DPI; a JPEG drawn at 72 DPI, under the
    /// trigger (24, re-encoded only); an unfiltered gray image under the trigger (26).
    /// </summary>
    public static ScanTestPdf Mixed() =>
        new ScanTestPdf()
            .Stream(10, Image(600, 600, extra: "/SMask 12 0 R"), FlateSamples(600, 600, seed: 5))
            .Stream(12, Image(600, 600, colorSpace: "/DeviceGray"), Flate(MaskSamples(600, 600)))
            .Stream(14, Image(600, 450, filter: "/DCTDecode"), JpegEncoder.Encode(ScanSamples(600, 450, 3, 7), 600, 450, 3, 90))
            .Stream(16, Image(600, 600, colorSpace: "/DeviceGray"), Flate(TextSamples(600, 600)))
            .Stream(18, Image(600, 600, extra: "/SMask 12 0 R"), FlateSamples(600, 600, seed: 5))
            .Stream(20, Image(Pixels, Pixels, colorSpace: "/DeviceGray", bits: 1), new byte[Pixels * Pixels / 8])
            .Stream(22, Image(600, 600, filter: null), Samples(600, 600, 3, seed: 9))
            .Stream(24, Image(200, 200, filter: "/DCTDecode"), JpegEncoder.Encode(ScanSamples(200, 200, 3, 11), 200, 200, 3, 92))
            .Stream(26, Image(150, 150, colorSpace: "/DeviceGray", filter: null), Samples(150, 150, 1, seed: 13))
            .Page(3, $"{Draw("Im0", 144)} q 144 0 0 108 200 0 cm /Im1 Do Q", xobjects: "/Im0 10 0 R /Im1 14 0 R")
            .Page(5, $"{Draw("Im0", 144)} q 144 0 0 144 200 0 cm /Im1 Do Q {Draw("Im2")} q 72 0 0 72 0 400 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI Q", xobjects: "/Im0 16 0 R /Im1 18 0 R /Im2 20 0 R")
            .Page(7, $"{Draw("Im0", 144)} q 200 0 0 200 200 0 cm /Im1 Do Q q 150 0 0 150 0 400 cm /Im2 Do Q", xobjects: "/Im0 22 0 R /Im1 24 0 R /Im2 26 0 R");

    /// <summary>
    /// More sampling groups than the cap allows: one page per image, every image distinct.
    /// Seven groups (Flate, JPEG and unfiltered images drawn above and under the downsample
    /// trigger, and the soft masks of four of the Flate images), 21 samples wanted and 16
    /// allowed, with the groups' total sizes deliberately unequal.
    /// </summary>
    public static ScanTestPdf ManyGroups()
    {
        var pdf = new ScanTestPdf();
        var number = 10;
        var groups = new (string Filter, int Pixels, int Edge)[]
        {
            ("/FlateDecode", 400, 72), // 400 DPI: downsampled and re-encoded
            ("/FlateDecode", 120, 120), // 72 DPI: re-encoded only
            ("/DCTDecode", 400, 72),
            ("/DCTDecode", 120, 120),
            ("none", 300, 72),
            ("none", 120, 120),
            ("masked", 300, 72), // Flate at 300 DPI, each with its own soft mask
        };
        foreach (var (filter, pixels, edge) in groups)
        {
            for (var i = 0; i < 4; i++)
            {
                var seed = number;
                var (dictionary, payload) = filter switch
                {
                    "/FlateDecode" => (Image(pixels, pixels), FlateSamples(pixels, pixels, seed: seed)),
                    "/DCTDecode" => (Image(pixels, pixels, filter: filter), JpegEncoder.Encode(ScanSamples(pixels, pixels, 3, seed), pixels, pixels, 3, 85)),
                    "masked" => (Image(pixels, pixels, extra: $"/SMask {number + 3} 0 R"), FlateSamples(pixels, pixels, seed: seed)),
                    _ => (Image(pixels, pixels, filter: null), Samples(pixels, pixels, 3, seed)),
                };
                pdf.Stream(number, dictionary, payload);
                pdf.Page(number + 1, Draw("Im0", edge), xobjects: $"/Im0 {number} 0 R");
                if (filter == "masked")
                {
                    pdf.Stream(number + 3, Image(pixels, pixels, colorSpace: "/DeviceGray"), FlateSamples(pixels, pixels, channels: 1, seed: seed + 1));
                }

                number += 4;
            }
        }

        return pdf;
    }

    /// <summary>Scan-like samples: a paper-coloured background, noise and dark text-like bars, varying with <paramref name="seed"/>.</summary>
    public static byte[] ScanSamples(int width, int height, int channels, int seed)
    {
        var random = new Random(seed);
        var noise = 3 + (seed % 5 * 4);
        var lineHeight = 14 + (seed % 3 * 6);
        var samples = new byte[width * height * channels];
        for (var y = 0; y < height; y++)
        {
            var inLine = y % lineHeight < lineHeight / 2 && y > height / 10 && y < height * 9 / 10;
            for (var x = 0; x < width; x++)
            {
                var ink = inLine && ((x / (7 + (seed % 4))) + (y / lineHeight)) % 5 != 0 && x > width / 12 && x < width * 11 / 12;
                var basis = ink ? 40 : 230 - (y * 20 / height);
                for (var c = 0; c < channels; c++)
                {
                    var value = basis + (c * 6) + random.Next(-noise, noise + 1);
                    samples[(((y * width) + x) * channels) + c] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }

        return samples;
    }

    /// <summary>Gray, text-like samples: black strokes on white, the kind JPEG handles badly.</summary>
    public static byte[] TextSamples(int width, int height)
    {
        var samples = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var stroke = y % 24 < 12 && (x / 5) % 3 != 0 && ((x * 7) + (y * 3)) % 11 > 2;
                samples[(y * width) + x] = stroke ? (byte)0 : (byte)255;
            }
        }

        return samples;
    }

    /// <summary>A soft mask: a smooth radial falloff.</summary>
    public static byte[] MaskSamples(int width, int height)
    {
        var samples = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var dx = x - (width / 2);
                var dy = y - (height / 2);
                samples[(y * width) + x] = (byte)Math.Clamp(255 - (int)(Math.Sqrt((dx * dx) + (dy * dy)) * 255 / (width / 2)), 0, 255);
            }
        }

        return samples;
    }
}
