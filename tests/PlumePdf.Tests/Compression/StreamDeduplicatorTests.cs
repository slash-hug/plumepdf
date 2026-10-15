using System.Globalization;
using System.Text;
using PlumePdf.Documents;
using PlumePdf.Documents.Compression;
using PlumePdf.Objects;
using PlumePdf.Tests.PageRemoval;
using PlumePdf.Tests.Writing;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <see cref="StreamDeduplicator"/>: byte-identical streams a full-rewrite save would write are
/// mapped onto one canonical copy (the lowest object number), bottom-up so that parents whose
/// children merged compare equal too, and every alias it produces is one the writer accepts.
/// </summary>
public class StreamDeduplicatorTests
{
    private static IReadOnlyDictionary<int, int> Find(PdfDocument document, PdfOptions? options = null) =>
        StreamDeduplicator.FindAliases(document, options ?? PdfOptions.Default, CancellationToken.None);

    [Fact]
    public void IdenticalImagesOnSeveralPages_AliasOntoTheLowestNumber()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3, identicalImages: true));

        var aliases = Find(document);

        Assert.Equal(
            new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0), [ImagePagesDocument.Image(2)] = ImagePagesDocument.Image(0) },
            aliases);
    }

    [Fact]
    public void DistinctImages_AndDistinctContentStreams_NeverMerge()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3));

        Assert.Empty(Find(document));
    }

    // The removed page's image is not written by the save, so it is not a candidate: the kept
    // pages' copies merge onto the lowest of themselves, the writer accepts that (no PLUME5010),
    // and the shared payload lands in the output once.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedPagesDuplicate_IsNotACandidate_AndTheKeptCopiesAreWrittenOnce(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3, identicalImages: true));
        document.Pages.RemoveAt(0);
        var options = PdfOptions.Default with { Optimize = optimize };

        var aliases = Find(document, options);

        Assert.Equal(new Dictionary<int, int> { [ImagePagesDocument.Image(2)] = ImagePagesDocument.Image(1) }, aliases);
        var output = Save(document, options, aliases);
        using var saved = PdfDocument.Open(output);
        Assert.Equal(2, saved.Pages.Count);
        Assert.Equal(ImagePagesDocument.PageImageReference(saved, 0, "Im0"), ImagePagesDocument.PageImageReference(saved, 1, "Im0"));
        Assert.Equal(1, Occurrences(output, "PIXELS-SAME"));
        CleanupFixtures3b.AssertQpdfClean(output, $"deduplicated save over a removed page (Optimize={optimize})");
    }

    // Two images that differ only in which (separate, byte-identical) soft mask they name: the
    // masks merge first, after which the images' dictionaries are equal and they merge too.
    [Fact]
    public void ImagesWithSeparateButIdenticalSoftMasks_MergeAtBothLevels()
    {
        using var document = PdfDocument.Open(Fixtures.SoftMaskedPair());

        var aliases = Find(document);

        Assert.Equal(new Dictionary<int, int> { [Fixtures.SecondImage] = Fixtures.FirstImage, [Fixtures.SecondMask] = Fixtures.FirstMask }, aliases);
    }

    // A group whose lowest member only became equal in a later round takes over as canonical,
    // and an alias found earlier onto the old canonical is repointed: the writer refuses an
    // alias onto an object that is itself aliased.
    [Fact]
    public void ACanonicalDisplacedInALaterRound_HasItsAliasesRepointed()
    {
        var pdf = new RawPdf();
        pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /A 4 0 R /B 5 0 R /C 9 0 R >> >> >>");
        pdf.Stream(4, $"{Fixtures.GrayImage(2, 2)} /SMask 21 0 R", "SAME-PIXELS");
        pdf.Stream(5, $"{Fixtures.GrayImage(2, 2)} /SMask 20 0 R", "SAME-PIXELS");
        pdf.Stream(9, $"{Fixtures.GrayImage(2, 2)} /SMask 20 0 R", "SAME-PIXELS");
        pdf.Stream(20, Fixtures.GrayImage(2, 2), "MASK");
        pdf.Stream(21, Fixtures.GrayImage(2, 2), "MASK");
        using var document = PdfDocument.Open(pdf.Build());

        var aliases = Find(document);

        Assert.Equal(new Dictionary<int, int> { [5] = 4, [9] = 4, [21] = 20 }, aliases);
        Assert.NotEmpty(Save(document, PdfOptions.Default, aliases));
    }

    // /Length is recomputed on write, so a direct and an indirect /Length over the same bytes
    // describe the same stream.
    [Fact]
    public void DirectAndIndirectLength_OverTheSameBytes_Merge()
    {
        var pdf = Fixtures.TwoImagePage(out var first, out var second);
        pdf.Stream(first, Fixtures.GrayImage(2, 2), "PIXELS!");
        pdf.Stream(second, Fixtures.GrayImage(2, 2), "PIXELS!", length: "30 0 R");
        pdf.Object(30, "7");
        using var document = PdfDocument.Open(pdf.Build());

        Assert.Equal(new Dictionary<int, int> { [second] = first }, Find(document));
    }

    [Fact]
    public void NearIdenticalPayloads_OfTheSameLength_NeverMerge()
    {
        var pdf = Fixtures.TwoImagePage(out var first, out var second);
        pdf.Stream(first, Fixtures.GrayImage(2, 2), "PIXELS-A");
        pdf.Stream(second, Fixtures.GrayImage(2, 2), "PIXELS-B");
        using var document = PdfDocument.Open(pdf.Build());

        Assert.Empty(Find(document));
    }

    [Fact]
    public void DictionariesThatDiffer_NeverMerge_EvenOverTheSameBytes()
    {
        var pdf = Fixtures.TwoImagePage(out var first, out var second);
        pdf.Stream(first, Fixtures.GrayImage(2, 2), "PIXELS");
        pdf.Stream(second, $"{Fixtures.GrayImage(2, 2)} /Interpolate true", "PIXELS");
        using var document = PdfDocument.Open(pdf.Build());

        Assert.Empty(Find(document));
    }

    // Key order is not meaning: two dictionaries holding the same entries in a different order
    // describe the same stream.
    [Fact]
    public void DictionaryKeyOrder_DoesNotPreventAMerge()
    {
        var pdf = Fixtures.TwoImagePage(out var first, out var second);
        pdf.Stream(first, "/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8", "PIXELS");
        pdf.Stream(second, "/BitsPerComponent 8 /ColorSpace /DeviceGray /Height 2 /Width 2 /Subtype /Image /Type /XObject", "PIXELS");
        using var document = PdfDocument.Open(pdf.Build());

        Assert.Equal(new Dictionary<int, int> { [second] = first }, Find(document));
    }

    [Theory]
    [InlineData("/Type /Metadata /Subtype /XML")]
    [InlineData("/Type /XObject /Subtype /Form /BBox [0 0 10 10]")]
    [InlineData("/F (external.bin)")]
    [InlineData("/Type /XRef /W [1 1 1] /Size 1")]
    [InlineData("/Type /ObjStm /N 0 /First 0")]
    public void ExcludedKinds_NeverMerge(string dictionary)
    {
        var pdf = new RawPdf();
        pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /PieceInfo [4 0 R 5 0 R] >>");
        pdf.Stream(4, dictionary, "SAME-BYTES");
        pdf.Stream(5, dictionary, "SAME-BYTES");
        using var document = PdfDocument.Open(pdf.Build());

        Assert.Empty(Find(document));
    }

    // The save never writes an object nothing reaches, so an unreferenced copy is no candidate.
    [Fact]
    public void UnreachableDuplicate_IsNotACandidate()
    {
        var pdf = Fixtures.TwoImagePage(out var first, out var second);
        pdf.Stream(first, Fixtures.GrayImage(2, 2), "PIXELS-A");
        pdf.Stream(second, Fixtures.GrayImage(2, 2), "PIXELS-B");
        pdf.Stream(40, Fixtures.GrayImage(2, 2), "PIXELS-A"); // orphan, after both
        pdf.Stream(41, Fixtures.GrayImage(2, 2), "PIXELS-B"); // orphan
        using var document = PdfDocument.Open(pdf.Build());

        Assert.Empty(Find(document));
    }

    // The canonical is the lowest number whatever order the save discovers the copies in, and
    // the same document always yields the same map.
    [Fact]
    public void CanonicalChoice_IsTheLowestNumber_AndDeterministic()
    {
        var pdf = new RawPdf();
        pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /A 12 0 R /B 8 0 R /C 10 0 R >> >> >>");
        foreach (var number in new[] { 8, 10, 12 })
        {
            pdf.Stream(number, Fixtures.GrayImage(2, 2), "SAME");
        }

        var bytes = pdf.Build();
        IReadOnlyDictionary<int, int> Run()
        {
            using var document = PdfDocument.Open(bytes);
            return Find(document);
        }

        var first = Run();
        Assert.Equal(new Dictionary<int, int> { [10] = 8, [12] = 8 }, first);
        Assert.Equal(first, Run());
    }

    // An excluded number is never followed, so a copy only the exclusion set holds is no
    // candidate and the canonical is the lowest of what the save writes.
    [Fact]
    public void ExcludedCopy_IsNeverTheCanonical()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3, identicalImages: true));
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var excluded = new HashSet<int> { ImagePagesDocument.Image(0) };

        var aliases = StreamDeduplicator.FindAliases(document.Objects, document.Catalog!.Reference, document.Catalog.Dictionary, pages, excluded, replacements: null, CancellationToken.None);

        Assert.Equal(new Dictionary<int, int> { [ImagePagesDocument.Image(2)] = ImagePagesDocument.Image(1) }, aliases);
        Assert.NotEmpty(StreamWriteTransformTests.Write(document, PdfOptions.Default, transform: null, excluded: excluded, aliases: aliases));
    }

    // A save-time replacement is written in place of the original, so an overridden stream is
    // never merged on either side, and a stream only a replacement value reaches is discovered.
    [Fact]
    public void ReplacedStreams_NeverMerge_AndWhatAReplacementReachesIsDiscovered()
    {
        var pdf = Fixtures.TwoImagePage(out var first, out var second);
        pdf.Stream(first, Fixtures.GrayImage(2, 2), "SAME");
        pdf.Stream(second, Fixtures.GrayImage(2, 2), "SAME");
        pdf.Object(6, "<< /Unused true >>"); // unreferenced in the file; the replacement catalog names it
        pdf.Stream(8, Fixtures.GrayImage(2, 2), "SAME"); // reached only through the replacement for 6
        using var document = PdfDocument.Open(pdf.Build());
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var image = Assert.IsType<PdfStream>(document.Objects[new IndirectReference(first, 0)]);
        var holder = new PdfDictionary();
        holder.Set(PdfName.Get("Keep"), new PdfReference(new IndirectReference(8, 0)));
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalog.Set(PdfName.Get("Holder"), new PdfReference(new IndirectReference(6, 0)));
        var replacements = new Dictionary<int, PdfObject>
        {
            [first] = new PdfStream(image.Dictionary, image.RawBytes),
            [6] = holder,
        };

        var aliases = StreamDeduplicator.FindAliases(document.Objects, document.Catalog!.Reference, catalog, pages, new HashSet<int>(), replacements, CancellationToken.None);

        Assert.Equal(new Dictionary<int, int> { [8] = second }, aliases);
        using var output = new MemoryStream();
        FullRewriteWriter.Write(output, document.Objects, document.Catalog.Reference, catalog, pages, PdfOptions.Default, excluded: null, replacements, transform: null, aliases);
        Assert.True(output.Length > 0);
    }

    // Planning reads payloads without keeping them: streams too large to be served from the
    // first bytes read are walked by their dictionaries and hashed through the read-once path,
    // so the registry's cache is exactly what opening the document put there.
    [Fact]
    public void Planning_DoesNotCacheStreams()
    {
        const int pageCount = 6;
        using var document = PdfDocument.Open(Fixtures.LargeStreamPages(pageCount));
        var before = document.Objects.CachedObjectCount;

        var aliases = Find(document);

        Assert.Equal(pageCount - 1, aliases.Count);
        Assert.Equal(before, document.Objects.CachedObjectCount);
    }

    [Fact]
    public void Cancellation_IsHonoured()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2, identicalImages: true));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => StreamDeduplicator.FindAliases(document, PdfOptions.Default, cancelled.Token));
    }

    // End to end: the aliases go straight into the hooked save, which accepts them, writes each
    // shared payload once and reopens to the same pages and the same decoded images.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveWithTheAliases_ReopensWithTheSamePagesAndImages(bool optimize)
    {
        var bytes = Fixtures.DecodableDocument();
        var options = PdfOptions.Default with { Optimize = optimize, Deterministic = true };
        using var document = PdfDocument.Open(bytes);

        var aliases = Find(document, options);
        Assert.Equal(
            new Dictionary<int, int>
            {
                [Fixtures.DecodableImage(1)] = Fixtures.DecodableImage(0),
                [Fixtures.DecodableMask(1)] = Fixtures.DecodableMask(0),
                [Fixtures.DecodableImage(3)] = Fixtures.DecodableImage(2),
            },
            aliases);

        var deduplicated = Save(document, options, aliases);
        var baseline = Save(document, options, aliases: null);
        Assert.True(deduplicated.Length < baseline.Length, "the deduplicated save is smaller than the plain one");

        using var expected = PdfDocument.Open(baseline);
        using var actual = PdfDocument.Open(deduplicated);
        Assert.Equal(expected.Pages.Count, actual.Pages.Count);
        for (var page = 0; page < expected.Pages.Count; page++)
        {
            // Extraction lists each image object once per page, so two names for what is now
            // one object are one entry: compare the distinct images each page shows.
            var want = Distinct(expected.Pages[page].ExtractImages());
            var got = Distinct(actual.Pages[page].ExtractImages());
            Assert.Equal(want.Count, got.Count);
            for (var i = 0; i < want.Count; i++)
            {
                Assert.Equal((want[i].Width, want[i].Height, want[i].ColorSpaceName), (got[i].Width, got[i].Height, got[i].ColorSpaceName));
                Assert.Equal(want[i].SoftMaskReference is null, got[i].SoftMaskReference is null);
                Assert.Equal(want[i].Data.ToArray(), got[i].Data.ToArray());
            }
        }

        CleanupFixtures3b.AssertQpdfClean(deduplicated, $"deduplicated save (Optimize={optimize})");
    }

    private static byte[] Save(PdfDocument document, PdfOptions options, IReadOnlyDictionary<int, int>? aliases)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-dedupe-{Guid.NewGuid():N}.pdf");
        try
        {
            document.SaveWithWriteHooks(path, options, transform: null, aliases);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<ExtractedImage> Distinct(IReadOnlyList<ExtractedImage> images) =>
        [.. images.DistinctBy(static i => Convert.ToHexString(i.Data.Span))];

    private static int Occurrences(byte[] haystack, string needle)
    {
        var text = Encoding.Latin1.GetString(haystack);
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The fixture documents these tests open, built from the object/xref grammar.</summary>
    private static class Fixtures
    {
        public const int FirstImage = 4;
        public const int SecondImage = 5;
        public const int FirstMask = 6;
        public const int SecondMask = 7;

        public static string GrayImage(int width, int height) =>
            $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8";

        /// <summary>One page naming two images, numbers 4 and 5, which the caller adds.</summary>
        public static RawPdf TwoImagePage(out int first, out int second)
        {
            first = FirstImage;
            second = SecondImage;
            var pdf = new RawPdf();
            pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
            pdf.Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
            pdf.Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /A 4 0 R /B 5 0 R >> >> >>");
            return pdf;
        }

        // Pages whose content stream and image are each larger than the resolver's first read,
        // so the save (and the planner) walk them by dictionary alone. Every image is the same.
        public static byte[] LargeStreamPages(int pageCount)
        {
            var pdf = new RawPdf();
            pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
            var kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(p => $"{10 + (3 * p)} 0 R"));
            pdf.Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
            var pixels = new byte[16 * 1024];
            new Random(7).NextBytes(pixels);
            for (var p = 0; p < pageCount; p++)
            {
                var page = 10 + (3 * p);
                pdf.Object(page, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents {page + 1} 0 R /Resources << /XObject << /Im0 {page + 2} 0 R >> >> >>");
                pdf.Stream(page + 1, string.Empty, $"q 50 0 0 50 10 10 cm /Im0 Do Q % page {p} {new string('x', 8 * 1024)}");
                pdf.Stream(page + 2, GrayImage(128, 128), pixels);
            }

            return pdf.Build();
        }

        public static byte[] SoftMaskedPair()
        {
            var pdf = TwoImagePage(out _, out _);
            pdf.Stream(FirstImage, $"{GrayImage(2, 2)} /SMask {FirstMask} 0 R", "PIXELS");
            pdf.Stream(SecondImage, $"{GrayImage(2, 2)} /SMask {SecondMask} 0 R", "PIXELS");
            pdf.Stream(FirstMask, GrayImage(2, 2), "MASK");
            pdf.Stream(SecondMask, GrayImage(2, 2), "MASK");
            return pdf.Build();
        }

        // Two pages. Page 0 draws images 0 and 1 (identical Flate gray samples, each with its own
        // byte-identical soft mask); page 1 draws images 2 and 3 (identical to each other, no
        // mask, different samples from page 0's). Every page has its own content stream.
        public static int DecodableImage(int index) => 10 + index;

        public static int DecodableMask(int index) => 20 + index;

        public static byte[] DecodableDocument()
        {
            var pdf = new RawPdf();
            pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
            pdf.Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
            pdf.Object(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /XObject << /Im0 {DecodableImage(0)} 0 R /Im1 {DecodableImage(1)} 0 R >> >> >>");
            pdf.Object(4, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R /Resources << /XObject << /Im0 {DecodableImage(2)} 0 R /Im1 {DecodableImage(3)} 0 R >> >> >>");
            pdf.Stream(5, string.Empty, "q 80 0 0 80 10 10 cm /Im0 Do Q q 80 0 0 80 100 100 cm /Im1 Do Q");
            pdf.Stream(6, string.Empty, "q 90 0 0 90 10 10 cm /Im0 Do Q q 60 0 0 60 120 120 cm /Im1 Do Q");

            var pixels = CompressionTestImages.Flate(CompressionTestImages.Samples(16, 16, 1, seed: 3));
            var mask = CompressionTestImages.Flate(CompressionTestImages.Samples(16, 16, 1, seed: 4));
            var other = CompressionTestImages.Flate(CompressionTestImages.Samples(16, 16, 1, seed: 5));
            pdf.Stream(DecodableImage(0), $"{GrayImage(16, 16)} /Filter /FlateDecode /SMask {DecodableMask(0)} 0 R", pixels);
            pdf.Stream(DecodableImage(1), $"{GrayImage(16, 16)} /Filter /FlateDecode /SMask {DecodableMask(1)} 0 R", pixels);
            pdf.Stream(DecodableImage(2), $"{GrayImage(16, 16)} /Filter /FlateDecode", other);
            pdf.Stream(DecodableImage(3), $"{GrayImage(16, 16)} /Filter /FlateDecode", other);
            pdf.Stream(DecodableMask(0), $"{GrayImage(16, 16)} /Filter /FlateDecode", mask);
            pdf.Stream(DecodableMask(1), $"{GrayImage(16, 16)} /Filter /FlateDecode", mask);
            return pdf.Build();
        }
    }

    /// <summary>
    /// A classic-xref PDF written straight from the object grammar: objects are added by
    /// number (gaps become free entries) and serialized in number order.
    /// </summary>
    private sealed class RawPdf
    {
        private readonly SortedDictionary<int, byte[]> _objects = [];

        public void Object(int number, string body) => _objects[number] = Encoding.Latin1.GetBytes($"{number} 0 obj\n{body}\nendobj\n");

        public void Stream(int number, string dictionary, string payload, string? length = null) =>
            Stream(number, dictionary, Encoding.Latin1.GetBytes(payload), length);

        public void Stream(int number, string dictionary, byte[] payload, string? length = null)
        {
            using var output = new MemoryStream();
            output.Write(Encoding.Latin1.GetBytes($"{number} 0 obj\n<< {dictionary} /Length {length ?? payload.Length.ToString(CultureInfo.InvariantCulture)} >>\nstream\n"));
            output.Write(payload);
            output.Write("\nendstream\nendobj\n"u8);
            _objects[number] = output.ToArray();
        }

        public byte[] Build()
        {
            var size = _objects.Keys.Max() + 1;
            var offsets = new long[size];
            using var output = new MemoryStream();
            output.Write("%PDF-1.7\n"u8);
            foreach (var (number, bytes) in _objects)
            {
                offsets[number] = output.Position;
                output.Write(bytes);
            }

            var xref = output.Position;
            var table = new StringBuilder().Append(CultureInfo.InvariantCulture, $"xref\n0 {size}\n");
            for (var n = 0; n < size; n++)
            {
                table.Append(_objects.ContainsKey(n) ? $"{offsets[n]:D10} 00000 n \n" : "0000000000 65535 f \n");
            }

            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            output.Write(Encoding.ASCII.GetBytes(table.ToString()));
            return output.ToArray();
        }
    }
}
