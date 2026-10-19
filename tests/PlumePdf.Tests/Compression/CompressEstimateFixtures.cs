using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Seven gray Flate images drawn at 72 DPI (re-encoded only, never downsampled), all of which
    /// JPEG makes larger, so Compress keeps every original. The three the estimate samples
    /// (indices 1, 3 and 5) are dense text, whose originals are tiny; the rest are speckle noise,
    /// whose originals are not.
    /// </summary>
    public static ScanTestPdf KeptTextGroup()
    {
        var pdf = new ScanTestPdf();
        for (var i = 0; i < 7; i++)
        {
            var number = 10 + (i * 3);
            var samples = i % 2 == 1 ? TextSamples(120, 120, density: 10, seed: i) : Speckle(120, 120, seed: i);
            pdf.Stream(number, Image(120, 120, colorSpace: "/DeviceGray"), Flate(samples));
            pdf.Page(number + 1, Draw("Im0", 120), xobjects: $"/Im0 {number} 0 R");
        }

        return pdf;
    }

    /// <summary>
    /// Nine JPEG scan pages like <see cref="JpegScan"/>'s, but the three the estimate samples
    /// (indices 1, 4 and 7) are blank: an unrepresentative sample that makes the rest look far
    /// more compressible than they are.
    /// </summary>
    public static ScanTestPdf BlankSamplesAmongNoisyPages()
    {
        var pdf = new ScanTestPdf();
        for (var page = 0; page < 9; page++)
        {
            var image = 10 + (page * 3);
            var samples = page % 3 == 1 ? Enumerable.Repeat((byte)250, 900 * 1100 * 3).ToArray() : ScanSamples(900, 1100, 3, (page * 7) + 4);
            pdf.Stream(image, Image(900, 1100, filter: "/DCTDecode"), JpegEncoder.Encode(samples, 900, 1100, 3, 85));
            pdf.Page(image + 1, Draw("Im0", 288), xobjects: $"/Im0 {image} 0 R");
        }

        return pdf;
    }

    /// <summary>
    /// A document whose 300 small dictionaries are packed into object streams with a
    /// cross-reference stream (written by Compress's lossless levers), so a save that doesn't
    /// pack has to write each one out as its own object, with a classic cross-reference table.
    /// </summary>
    public static byte[] ObjectStreamPackedSource()
    {
        var pdf = new ScanTestPdf();
        var annotations = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var number = 100 + i;
            pdf.Object(number, $"<< /Type /Annot /Subtype /Text /Rect [{i} {i} {i + 20} {i + 20}] /Contents (Note {i} on the page) /NM (note-{i}) >>");
            annotations.Add($"{number} 0 R");
        }

        pdf.Page(3, "0 0 10 10 re f", extra: $"/Annots [{string.Join(' ', annotations)}]");
        var source = Write(pdf);
        var packed = TempPath("packed");
        try
        {
            Pdf.Compress(source, packed);
            return File.ReadAllBytes(packed);
        }
        finally
        {
            Delete(source, packed);
        }
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

    /// <summary>Gray, text-like samples: black strokes on white, the kind JPEG handles badly; a higher <paramref name="density"/> (1–10) inks more.</summary>
    public static byte[] TextSamples(int width, int height, int density = 8, int seed = 0)
    {
        var samples = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var stroke = y % 24 < 12 && (x / 5) % 3 != 0 && ((x * 7) + (y * 3) + seed) % 11 > 10 - density;
                samples[(y * width) + x] = stroke ? (byte)0 : (byte)255;
            }
        }

        return samples;
    }

    /// <summary>White with about one pixel in twenty black, at random: hard for Flate, harder for JPEG.</summary>
    public static byte[] Speckle(int width, int height, int seed)
    {
        var random = new Random(seed);
        var samples = new byte[width * height];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = random.Next(20) == 0 ? (byte)0 : (byte)255;
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
    /// <summary>
    /// One page referencing a 40 KB data stream (incompressible, so no lever changes it) that
    /// <paramref name="revisions"/> incremental updates each replace: a full rewrite keeps only
    /// the last one, so most of the input is superseded bytes the output drops.
    /// </summary>
    public static byte[] SupersededRevisions(int revisions = 5)
    {
        var pdf = new ScanTestPdf().Stream(10, string.Empty, Noise(40_000, seed: 0));
        pdf.Page(3, "0 0 10 10 re f", extra: "/PieceData 10 0 R");
        var bytes = pdf.Build();
        for (var r = 1; r <= revisions; r++)
        {
            bytes = AppendRevision(bytes, (10, StreamObject(10, Noise(40_000, seed: r))));
        }

        return bytes;
    }

    /// <summary>A one-page document with an update adding ten 20 KB streams that nothing references.</summary>
    public static byte[] UnreachableObjects()
    {
        var pdf = new ScanTestPdf();
        pdf.Page(3, "0 0 10 10 re f");
        var bytes = pdf.Build();
        return AppendRevision(bytes, [.. Enumerable.Range(50, 10).Select(static n => (n, StreamObject(n, Noise(20_000, seed: n))))]);
    }

    /// <summary>300 small annotation dictionaries, unpacked, with a classic cross-reference table: the lossless levers pack them.</summary>
    public static ScanTestPdf ManySmallObjects()
    {
        var pdf = new ScanTestPdf();
        var annotations = new List<string>();
        for (var i = 0; i < 300; i++)
        {
            var number = 100 + i;
            pdf.Object(number, $"<< /Type /Annot /Subtype /Text /Rect [{i} {i} {i + 20} {i + 20}] /Contents (Note {i} on the page) /NM (note-{i}) >>");
            annotations.Add($"{number} 0 R");
        }

        return pdf.Page(3, "0 0 10 10 re f", extra: $"/Annots [{string.Join(' ', annotations)}]");
    }

    /// <summary>
    /// Appends an incremental update (ISO 32000-1 §7.5.6) to <paramref name="pdf"/>: the given
    /// objects, a classic cross-reference section for them and a trailer whose <c>/Prev</c> is
    /// the previous section.
    /// </summary>
    public static byte[] AppendRevision(byte[] pdf, params (int Number, byte[] Object)[] objects)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var previous = long.Parse(Regex.Matches(text, @"startxref\s+(\d+)")[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        var size = Math.Max(int.Parse(Regex.Matches(text, @"/Size (\d+)")[^1].Groups[1].Value, CultureInfo.InvariantCulture), objects.Max(static o => o.Number) + 1);

        using var output = new MemoryStream();
        output.Write(pdf);
        var offsets = new SortedDictionary<int, long>();
        foreach (var (number, bytes) in objects)
        {
            offsets[number] = output.Position;
            output.Write(bytes);
        }

        var xref = output.Position;
        var table = new StringBuilder("xref\n");
        foreach (var (number, offset) in offsets)
        {
            table.Append(CultureInfo.InvariantCulture, $"{number} 1\n{offset:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} /Root 1 0 R /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n");
        output.Write(Encoding.ASCII.GetBytes(table.ToString()));
        return output.ToArray();
    }

    private static byte[] StreamObject(int number, byte[] content) =>
        [.. Encoding.ASCII.GetBytes($"{number} 0 obj\n<< /Length {content.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n"), .. content, .. "\nendstream\nendobj\n"u8.ToArray()];

    // Bytes Flate can't shrink, so the lossless lever leaves them alone and the test measures structure only.
    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
