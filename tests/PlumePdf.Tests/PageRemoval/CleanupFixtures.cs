using System.Diagnostics;
using System.Globalization;
using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Shared plumbing for the save-time clean-up suites (outlines, links, open action, named
/// destinations, destination resolution): free-form three-page documents composed from raw
/// object bodies, save-after-removal in every layout, and read-back helpers that resolve
/// before type-checking and validate the reopened outline's links as they walk it.
/// </summary>
/// <remarks>
/// The composed documents follow <see cref="RemovedPageFixtures"/>' layout — catalog 1, page
/// tree 2, pages 3/4/5 with content streams 10/11/12 carrying the page markers, shared
/// Helvetica 9 — so object numbers read the same across both builders.
/// </remarks>
internal static class CleanupFixtures
{
    private static readonly byte[] Header = Encoding.Latin1.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
    private static readonly Lazy<bool> QpdfAvailable = new(ProbeQpdf);

    /// <summary>Removal set × layout, for theories that run both removals in every layout.</summary>
    public static TheoryData<string, SaveLayout> RemovalsByLayout()
    {
        var data = new TheoryData<string, SaveLayout>();
        foreach (var removal in RemovedPageFixtures.Removals)
        {
            foreach (var layout in Enum.GetValues<SaveLayout>())
            {
                data.Add(removal, layout);
            }
        }

        return data;
    }

    /// <summary>Every layout, for theories whose fixture needs one particular removal.</summary>
    public static TheoryData<SaveLayout> Layouts() => [.. Enum.GetValues<SaveLayout>()];

    /// <summary>
    /// Composes a three-page document. <paramref name="catalogExtras"/> is appended to the
    /// catalog, <paramref name="pageExtras"/> (by page index) to each page dictionary, and
    /// <paramref name="objects"/> are written as-is (numbers 1-5 and 9-12 are the builder's).
    /// </summary>
    public static byte[] Compose(string catalogExtras, IReadOnlyDictionary<int, string> objects, params string[] pageExtras)
    {
        var all = new SortedDictionary<int, string>(objects.ToDictionary(static p => p.Key, static p => p.Value))
        {
            [1] = $"<< /Type /Catalog /Pages 2 0 R {catalogExtras} >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            [9] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        for (var i = 0; i < 3; i++)
        {
            var extras = i < pageExtras.Length ? pageExtras[i] : string.Empty;
            all[3 + i] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents {10 + i} 0 R /Resources << /Font << /F1 9 0 R >> >> {extras} >>";
            var content = $"BT /F1 12 Tf 20 100 Td ({RemovedPageFixtures.PageMarkers[i]}) Tj ET";
            all[10 + i] = $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        }

        var buffer = new List<byte>(Header);
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in all)
        {
            offsets[number] = buffer.Count;
            buffer.AddRange(Encoding.Latin1.GetBytes($"{number} 0 obj\n{body}\nendobj\n"));
        }

        var size = all.Keys.Max() + 1;
        var xrefOffset = buffer.Count;
        var xref = new StringBuilder($"xref\n0 {size}\n0000000000 65535 f \n");
        for (var n = 1; n < size; n++)
        {
            xref.Append(offsets.TryGetValue(n, out var offset) ? $"{offset:D10} 00000 n \n" : "0000000000 65535 f \n");
        }

        xref.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        buffer.AddRange(Encoding.Latin1.GetBytes(xref.ToString()));
        return [.. buffer];
    }

    /// <summary>Opens <paramref name="source"/>, removes the pages at <paramref name="removal"/> (a <see cref="RemovedPageFixtures.Removals"/> entry), and saves in <paramref name="layout"/>.</summary>
    public static byte[] SaveAfterRemoval(byte[] source, string removal, SaveLayout layout)
    {
        using var document = PdfDocument.Open(source);
        RemovedPageFixtures.RemovePages(document, RemovedPageFixtures.PageIndexes(removal));
        var saved = RemovedPageFixtures.SaveToBytes(document, layout);
        AssertQpdfCleanIfAvailable(saved);
        return saved;
    }

    /// <summary>Resolves <paramref name="value"/> through any chain of references.</summary>
    public static PdfObject? Resolve(PdfDocument document, PdfObject? value)
    {
        for (var hops = 0; value is PdfReference reference && hops < 8; hops++)
        {
            value = document.Objects[reference.Target];
        }

        return value;
    }

    /// <summary>The reopened document's catalog.</summary>
    public static PdfDictionary Catalog(PdfDocument document) =>
        Assert.IsType<PdfDictionary>(Resolve(document, document.Objects.Trailer[PdfName.Root]));

    /// <summary>The resolved value of <paramref name="key"/> in <paramref name="dictionary"/>, or <see langword="null"/> when absent.</summary>
    public static PdfObject? Get(PdfDocument document, PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(PdfName.Get(key), out var value) ? Resolve(document, value) : null;

    /// <summary>
    /// Describes the reopened outline as <c>Count=n: Title(+c)[child, child], Title</c> after
    /// validating every link on the way: each item's <c>/Parent</c> is the node whose chain it
    /// is on, <c>/Prev</c> mirrors <c>/Next</c>, <c>/Last</c> is the chain's end, and
    /// <c>/Count</c> is present exactly when the item has children. Returns
    /// <see langword="null"/> when the catalog has no <c>/Outlines</c>.
    /// </summary>
    public static string? OutlineShape(PdfDocument document)
    {
        var catalog = Catalog(document);
        if (!catalog.TryGetValue(PdfName.Get("Outlines"), out var rootValue))
        {
            return null;
        }

        var rootReference = Assert.IsType<PdfReference>(rootValue);
        var root = Assert.IsType<PdfDictionary>(Resolve(document, rootReference));
        var count = root.TryGetValue(PdfName.Get("Count"), out var c) ? Assert.IsType<PdfNumber>(Resolve(document, c)).ToInt32() : 0;
        return $"Count={count}: {Chain(document, rootReference.Target, root, depth: 0)}";
    }

    private static string Chain(PdfDocument document, IndirectReference parent, PdfDictionary parentDictionary, int depth)
    {
        Assert.True(depth < 32, "Outline deeper than any fixture builds - a cycle?");
        var parts = new List<string>();
        if (!parentDictionary.TryGetValue(PdfName.First, out var firstValue))
        {
            Assert.False(parentDictionary.ContainsKey(PdfName.Get("Last")), "/Last without /First.");
            return string.Empty;
        }

        var current = Assert.IsType<PdfReference>(firstValue);
        IndirectReference? previous = null;
        while (true)
        {
            var item = Assert.IsType<PdfDictionary>(Resolve(document, current));
            Assert.Equal(parent, Assert.IsType<PdfReference>(item[PdfName.Parent]).Target);
            if (previous is { } expectedPrev)
            {
                Assert.Equal(expectedPrev, Assert.IsType<PdfReference>(item[PdfName.Prev]).Target);
            }
            else
            {
                Assert.False(item.ContainsKey(PdfName.Prev), "The first item of a chain has a /Prev.");
            }

            var title = Assert.IsType<PdfString>(Resolve(document, item[PdfName.Get("Title")])).GetText();
            var children = Chain(document, current.Target, item, depth + 1);
            if (children.Length > 0)
            {
                var count = Assert.IsType<PdfNumber>(Resolve(document, item[PdfName.Get("Count")])).ToInt32();
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{title}({count:+0;-0})[{children}]"));
            }
            else
            {
                Assert.False(item.ContainsKey(PdfName.Get("Count")), $"Childless item {title} has a /Count.");
                parts.Add(title);
            }

            previous = current.Target;
            if (!item.TryGetValue(PdfName.Get("Next"), out var next))
            {
                break;
            }

            current = Assert.IsType<PdfReference>(next);
            Assert.True(parts.Count < 64, "Outline chain longer than any fixture builds - a cycle?");
        }

        Assert.Equal(previous, Assert.IsType<PdfReference>(parentDictionary[PdfName.Get("Last")]).Target);
        return string.Join(", ", parts);
    }

    /// <summary>The resolved <c>/Annots</c> of the reopened page at <paramref name="index"/>, asserting it holds no <c>null</c> entries; empty when the page has none.</summary>
    public static IReadOnlyList<PdfDictionary> Annotations(PdfDocument document, int index)
    {
        var page = document.Pages[index].Dictionary;
        if (!page.TryGetValue(PdfName.Annots, out var annots))
        {
            return [];
        }

        var array = Assert.IsType<PdfArray>(Resolve(document, annots));
        return [.. array.Select(entry => Assert.IsType<PdfDictionary>(Resolve(document, entry)))];
    }

    /// <summary>The text of an annotation's <c>/Contents</c>.</summary>
    public static string ContentsText(PdfDocument document, PdfDictionary annotation) =>
        Assert.IsType<PdfString>(Resolve(document, annotation[PdfName.Get("Contents")])).GetText();

    /// <summary>
    /// Runs <c>qpdf --check</c> on <paramref name="bytes"/> when qpdf is on the PATH (the corpus
    /// lane's armed qpdf suites are the gate; this is a local early warning).
    /// </summary>
    public static void AssertQpdfCleanIfAvailable(byte[] bytes)
    {
        if (!QpdfAvailable.Value)
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-cleanup-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, bytes);
            var (exitCode, output) = RunQpdf($"--check \"{path}\"");
            Assert.True(exitCode == 0, $"qpdf --check reported problems (exit {exitCode}):\n{output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool ProbeQpdf()
    {
        try
        {
            var (exitCode, output) = RunQpdf("--version");
            return exitCode == 0 && output.Contains("qpdf", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static (int ExitCode, string Output) RunQpdf(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("qpdf", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.ExitCode, stdout.Result + stderr);
    }
}
