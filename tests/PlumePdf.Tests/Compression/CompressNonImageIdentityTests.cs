using System.Globalization;
using System.Text;
using PlumePdf.Objects;
using Xunit;
using Xunit.Abstractions;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// Compress changes images and nothing else it isn't asked to. With the lossless levers off and
/// only image levers on, every non-image object in the output is byte for byte the object a
/// <see cref="PdfOptions.Deterministic"/> <c>Save</c> of the same input writes (a full rewrite
/// renumbers and reserializes everything, so the input's own bytes can't be the baseline). With
/// the lossless levers on, objects are packed, Flated and deduplicated, so the gate is decoded
/// equivalence against that same deterministic save instead (a save repairs what it reads, so
/// the raw input is not the baseline here either): the whole object graph reachable from the
/// catalog, images aside, decodes to the same values. Both run over a generated document with fonts, an annotation appearance,
/// a colour profile and soft-masked images, over the committed fixtures, and over the fetched
/// pdf.js corpus subset when it is present.
/// </summary>
public class CompressNonImageIdentityTests(ITestOutputHelper output)
{
    private static readonly PdfName SubtypeName = PdfName.Get("Subtype");
    private static readonly PdfName ImageName = PdfName.Get("Image");
    private static readonly PdfName LengthName = PdfName.Get("Length");
    private static readonly PdfName FilterName = PdfName.Get("Filter");
    private static readonly PdfName DecodeParmsName = PdfName.Get("DecodeParms");

    private static readonly PdfOptions Deterministic = PdfOptions.Default with { Deterministic = true };

    private static readonly PdfCompressOptions ImageLeversOnly = new()
    {
        Lossless = false,
        Images = new PdfImageCompression { MaxDpi = 100, JpegQuality = 80 },
    };

    private static readonly PdfCompressOptions AllLeversLossless = new()
    {
        Lossless = true,
        Images = new PdfImageCompression { MaxDpi = 100, JpegQuality = 80 },
    };

    [Fact]
    public void ImageLeversOnly_EveryNonImageObjectMatchesTheDeterministicSave()
    {
        var source = Write(RichDocument());
        try
        {
            var (compared, result) = AssertNonImageObjectsByteIdentical(source);

            // Not vacuous: images really changed, and there were non-image objects to compare.
            Assert.Contains(result.Images, static i => i.Action == ImageCompressionAction.DownsampledAndReencoded);
            Assert.True(compared >= 8, $"Only {compared} non-image objects were compared.");
        }
        finally
        {
            Delete(source);
        }
    }

    [Fact]
    public void AllLevers_EveryNonImageObjectDecodesTheSame()
    {
        var source = Write(RichDocument());
        try
        {
            var (compared, result) = AssertDecodedEquivalent(source);

            Assert.Contains(result.Images, static i => i.Action == ImageCompressionAction.Deduplicated);
            Assert.Contains(result.Images, static i => i.Action == ImageCompressionAction.DownsampledAndReencoded);
            Assert.True(compared >= 8, $"Only {compared} non-image objects were compared.");
        }
        finally
        {
            Delete(source);
        }
    }

    [Fact]
    public void BothChecks_CatchAChangedNonImageObject()
    {
        // Each comparison must fail when a non-image object differs: compare the
        // deterministic save of one document against that of a copy with different page text.
        var a = Write(RichDocument());
        var b = Write(RichDocument(text: "Changed"));
        var savedA = TempPath("identity-a");
        var savedB = TempPath("identity-b");
        try
        {
            using (var document = PdfDocument.Open(a))
            {
                document.Save(savedA, Deterministic);
            }

            using (var document = PdfDocument.Open(b))
            {
                document.Save(savedB, Deterministic);
            }

            Assert.NotNull(Record.Exception(() => CompareNonImageObjects(savedA, savedB, "self-check")));

            using var left = PdfDocument.Open(savedA);
            using var right = PdfDocument.Open(savedB);
            var comparer = new GraphComparer(left, right, "self-check");
            Assert.NotNull(Record.Exception(() => comparer.Compare(left.Objects.Trailer[PdfName.Root], right.Objects.Trailer[PdfName.Root], "/Root")));
        }
        finally
        {
            Delete(a, b, savedA, savedB);
        }
    }

    [Fact]
    public void CommittedFixtures_NonImageObjectsAreUnchanged()
    {
        var fixtures = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures"), "*.pdf", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(fixtures);

        var checkedCount = RunOver(fixtures);

        // Most fixtures save; a handful (encrypted, deliberately broken) refuse in both paths.
        Assert.True(checkedCount >= fixtures.Count / 2, $"Only {checkedCount} of {fixtures.Count} fixtures were compared.");
    }

    [Fact]
    public void Corpus_NonImageObjectsAreUnchanged()
    {
        var corpora = Path.Combine(RepoRoot(), "corpora");
        var subset = Directory.Exists(corpora) ? Directory.EnumerateDirectories(corpora, "pdfjs-subset-*").FirstOrDefault() : null;
        if (subset is null)
        {
            output.WriteLine($"SKIPPED (corpus not fetched; run scripts/fetch-corpora.sh to populate {corpora})");
            return;
        }

        var files = Directory.EnumerateFiles(subset, "*.pdf").Order(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(files);

        var checkedCount = RunOver(files);
        Assert.True(checkedCount > 0, $"None of the {files.Count} corpus documents could be compared.");
    }

    // Runs both gates over every file a plain deterministic Save accepts; a file the save
    // refuses is skipped (Compress refuses it too, or refuses earlier). Returns how many were compared.
    private int RunOver(IReadOnlyList<string> files)
    {
        var compared = 0;
        foreach (var file in files)
        {
            if (!PlainSaveSucceeds(file, out var why))
            {
                output.WriteLine($"skip {Path.GetFileName(file)}: {why}");
                continue;
            }

            var copy = TempPath("identity-source");
            File.Copy(file, copy);
            try
            {
                AssertNonImageObjectsByteIdentical(copy);
                AssertDecodedEquivalent(copy);
                compared++;
            }
            catch (PlumePdfException e) when (e.Code is "PLUME6084")
            {
                output.WriteLine($"skip {Path.GetFileName(file)}: signed ({e.Code})");
            }
            catch (Exception e) when (e is not PlumePdfException)
            {
                throw new InvalidOperationException($"{Path.GetFileName(file)}: {e.Message}", e);
            }
            finally
            {
                Delete(copy);
            }
        }

        output.WriteLine($"compared {compared} of {files.Count}");
        return compared;
    }

    private static bool PlainSaveSucceeds(string file, out string? why)
    {
        var saved = TempPath("identity-probe");
        try
        {
            using var document = PdfDocument.Open(file);
            document.Save(saved, Deterministic);
            why = null;
            return true;
        }
        catch (PlumePdfException e)
        {
            why = e.Code;
            return false;
        }
        finally
        {
            Delete(saved);
        }
    }

    private static (int Compared, CompressionResult Result) AssertNonImageObjectsByteIdentical(string source)
    {
        var baseline = TempPath("identity-baseline");
        var compressed = TempPath("identity-compressed");
        try
        {
            using (var document = PdfDocument.Open(source))
            {
                document.Save(baseline, Deterministic);
            }

            var result = Pdf.Compress(source, compressed, ImageLeversOnly, Deterministic);
            return (CompareNonImageObjects(baseline, compressed, Path.GetFileName(source)), result);
        }
        finally
        {
            Delete(baseline, compressed);
        }
    }

    // Both files are classic-xref full rewrites; every object that is not an image XObject must
    // have the same number and the same bytes in both. Returns the count compared.
    private static int CompareNonImageObjects(string baselinePath, string compressedPath, string what)
    {
        var baseline = File.ReadAllBytes(baselinePath);
        var compressed = File.ReadAllBytes(compressedPath);
        var baselineSlices = ObjectSlices(baseline);
        var compressedSlices = ObjectSlices(compressed);
        Assert.True(baselineSlices.Count == compressedSlices.Count, $"{what}: the compressed output has {compressedSlices.Count} objects, the deterministic save {baselineSlices.Count}.");

        using var baselineDocument = PdfDocument.Open(baseline);
        using var compressedDocument = PdfDocument.Open(compressed);
        var compared = 0;
        foreach (var (number, slice) in baselineSlices)
        {
            var reference = new IndirectReference(number, 0);
            var baselineIsImage = IsImage(baselineDocument.Objects[reference]);
            Assert.True(baselineIsImage == IsImage(compressedDocument.Objects[reference]), $"{what}: object {number} is an image in only one of the two outputs.");
            if (baselineIsImage)
            {
                continue;
            }

            Assert.True(compressedSlices.TryGetValue(number, out var other), $"{what}: object {number} is missing from the compressed output.");
            var expected = baseline.AsSpan(slice.Start, slice.Length);
            var actual = compressed.AsSpan(other.Start, other.Length);
            Assert.True(expected.SequenceEqual(actual), $"{what}: non-image object {number} differs from the deterministic save.\nexpected: {Preview(expected)}\nactual:   {Preview(actual)}");
            compared++;
        }

        return compared;
    }

    private static (int Compared, CompressionResult Result) AssertDecodedEquivalent(string source)
    {
        var baseline = TempPath("identity-baseline");
        var compressed = TempPath("identity-lossless");
        try
        {
            using (var document = PdfDocument.Open(source))
            {
                document.Save(baseline, Deterministic);
            }

            var result = Pdf.Compress(source, compressed, AllLeversLossless, Deterministic);
            using var input = PdfDocument.Open(baseline);
            using var outputDocument = PdfDocument.Open(compressed);
            var comparer = new GraphComparer(input, outputDocument, Path.GetFileName(source));
            comparer.Compare(input.Objects.Trailer[PdfName.Root], outputDocument.Objects.Trailer[PdfName.Root], "/Root");
            Assert.Equal(input.Pages.Count, outputDocument.Pages.Count);
            return (comparer.ObjectsCompared, result);
        }
        finally
        {
            Delete(baseline, compressed);
        }
    }

    private static bool IsImage(PdfObject value) =>
        value is PdfStream stream && stream.Dictionary.TryGetValue(SubtypeName, out var subtype) && subtype is PdfName name && name == ImageName;

    // Each object's byte range, from its classic cross-reference offset to the next object's
    // offset (or the table itself).
    private static Dictionary<int, (int Start, int Length)> ObjectSlices(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var tableOffset = int.Parse(text.AsSpan(startxref + 9).Trim().ToString().Split('\n')[0], CultureInfo.InvariantCulture);
        Assert.True(text.AsSpan(tableOffset).StartsWith("xref\n"), "Expected a classic cross-reference table.");

        var lines = text[tableOffset..].Split('\n');
        var size = int.Parse(lines[1].Split(' ')[1], CultureInfo.InvariantCulture);
        var offsets = new List<(int Number, int Offset)>();
        for (var number = 1; number < size; number++)
        {
            var entry = lines[2 + number];
            if (entry.EndsWith("n ", StringComparison.Ordinal))
            {
                offsets.Add((number, int.Parse(entry[..10], CultureInfo.InvariantCulture)));
            }
        }

        offsets.Sort(static (x, y) => x.Offset.CompareTo(y.Offset));
        var slices = new Dictionary<int, (int, int)>();
        for (var i = 0; i < offsets.Count; i++)
        {
            var end = i + 1 < offsets.Count ? offsets[i + 1].Offset : tableOffset;
            slices[offsets[i].Number] = (offsets[i].Offset, end - offsets[i].Offset);
        }

        return slices;
    }

    private static string Preview(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 160)]).Replace("\n", "\\n", StringComparison.Ordinal);

    // A page with text in a Standard 14 font with an unfiltered ToUnicode map, a Flate RGB image
    // with a soft mask, a JPEG gray image drawn twice through two identical objects, an image in
    // an ICC colour space, and a square annotation with an unfiltered appearance stream. Every
    // image sits at 300 DPI.
    private static ScanTestPdf RichDocument(string text = "Hello") =>
        new ScanTestPdf()
            .Stream(10, Image(extra: "/SMask 11 0 R"), FlateSamples())
            .Stream(11, Image(colorSpace: "/DeviceGray"), FlateSamples(channels: 1, seed: 2))
            .Stream(12, Image(colorSpace: "/DeviceGray", filter: "/DCTDecode"), JpegSamples(channels: 1))
            .Stream(13, Image(colorSpace: "/DeviceGray", filter: "/DCTDecode"), JpegSamples(channels: 1))
            .Stream(14, Image(colorSpace: "[/ICCBased 40 0 R]"), FlateSamples(seed: 3))
            .Object(20, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding /ToUnicode 21 0 R >>")
            .Stream(21, string.Empty, "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfrange <20> <7E> <0020> endbfrange endcmap CMapName currentdict /CMap defineresource pop end end")
            .Object(30, "<< /Type /Annot /Subtype /Square /Rect [300 300 400 400] /C [1 0 0] /Contents (Note) /AP << /N 31 0 R >> >>")
            .Stream(31, "/Type /XObject /Subtype /Form /BBox [0 0 100 100]", "1 0 0 RG 2 w 1 1 98 98 re S")
            .Stream(40, "/N 3 /Alternate /DeviceRGB", new string('p', 512))
            .Page(
                3,
                $"{Draw("A")} q 72 0 0 72 100 0 cm /B Do Q q 72 0 0 72 200 0 cm /C Do Q q 72 0 0 72 300 0 cm /D Do Q BT /F1 12 Tf 100 700 Td ({text}) Tj ET",
                xobjects: "/A 10 0 R /B 12 0 R /C 13 0 R /D 14 0 R",
                resources: "/Font << /F1 20 0 R >>",
                extra: "/Annots [30 0 R]");

    /// <summary>
    /// Walks two object graphs in step and asserts they hold the same values: references are
    /// followed (by value, not number), streams compare by decoded bytes with their
    /// <c>/Length</c>, <c>/Filter</c> and <c>/DecodeParms</c> ignored, and image XObjects are
    /// skipped entirely.
    /// </summary>
    private sealed class GraphComparer(PdfDocument left, PdfDocument right, string what)
    {
        private readonly HashSet<(IndirectReference, IndirectReference)> _visited = [];

        public int ObjectsCompared { get; private set; }

        public void Compare(PdfObject a, PdfObject b, string path)
        {
            if (a is PdfReference ra || b is PdfReference)
            {
                Assert.True(a is PdfReference && b is PdfReference, $"{what}: {path} is a reference on one side only.");
                var rb = (PdfReference)b;
                ra = (PdfReference)a;
                if (!_visited.Add((ra.Target, rb.Target)))
                {
                    return;
                }

                var va = left.Objects[ra.Target];
                var vb = right.Objects[rb.Target];
                if (IsImage(va) && IsImage(vb))
                {
                    return;
                }

                ObjectsCompared++;
                Compare(va, vb, path);
                return;
            }

            switch (a)
            {
                case PdfStream sa:
                    var sb = Assert.IsType<PdfStream>(b);
                    CompareDictionaries(sa.Dictionary, sb.Dictionary, path, stream: true);
                    var da = sa.GetDecodedBytes(left.Options.Filters, left.Options, r => left.Objects[r]);
                    var db = sb.GetDecodedBytes(right.Options.Filters, right.Options, r => right.Objects[r]);
                    Assert.True(da.AsSpan().SequenceEqual(db), $"{what}: the stream at {path} decodes differently ({da.Length} vs {db.Length} bytes).");
                    break;
                case PdfDictionary dictA:
                    CompareDictionaries(dictA, Assert.IsType<PdfDictionary>(b), path, stream: false);
                    break;
                case PdfArray arrayA:
                    var arrayB = Assert.IsType<PdfArray>(b);
                    Assert.True(arrayA.Count == arrayB.Count, $"{what}: {path} has {arrayA.Count} vs {arrayB.Count} items.");
                    for (var i = 0; i < arrayA.Count; i++)
                    {
                        Compare(arrayA[i], arrayB[i], $"{path}[{i}]");
                    }

                    break;
                case PdfNumber na:
                    Assert.True(b is PdfNumber nb && na.Value == nb.Value, $"{what}: {path} is {na.Value} vs {b}.");
                    break;
                case PdfString strA:
                    Assert.True(b is PdfString strB && strA.Bytes.Span.SequenceEqual(strB.Bytes.Span), $"{what}: the string at {path} differs.");
                    break;
                default:
                    Assert.True(a.Equals(b), $"{what}: {path} is {a} vs {b}.");
                    break;
            }
        }

        private void CompareDictionaries(PdfDictionary a, PdfDictionary b, string path, bool stream)
        {
            var keysA = Keys(a, stream);
            var keysB = Keys(b, stream);
            Assert.True(keysA.SetEquals(keysB), $"{what}: {path} has keys [{string.Join(' ', keysA.Order())}] vs [{string.Join(' ', keysB.Order())}].");
            foreach (var key in keysA)
            {
                Compare(a[PdfName.Get(key)], b[PdfName.Get(key)], $"{path}/{key}");
            }
        }

        private static HashSet<string> Keys(PdfDictionary dictionary, bool stream) =>
            dictionary.Keys
                .Where(k => !stream || (k != LengthName && k != FilterName && k != DecodeParmsName))
                .Select(static k => k.Value)
                .ToHashSet(StringComparer.Ordinal);
    }
}
