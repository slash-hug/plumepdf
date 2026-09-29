using System.Diagnostics;
using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Byte-composed fixtures and read-back helpers for the form and structure-tree clean-up suites.
/// Every fixture has the same three pages as <see cref="RemovedPageFixtures"/> (page objects 3, 4
/// and 5, content streams 10-12, the shared Helvetica font 9), plus whatever objects a test adds.
/// </summary>
internal static class CleanupFixtures3b
{
    private static readonly byte[] Header = Encoding.Latin1.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
    private static readonly Lazy<bool> QpdfInstalled = new(ProbeQpdf);

    /// <summary>
    /// Composes a three-page document. <paramref name="catalogEntries"/> is appended to the
    /// catalog, <paramref name="pageEntries"/> (by page index) to each page, and pages listed in
    /// <paramref name="markedPages"/> wrap their text in a <c>/P &lt;&lt; /MCID 0 &gt;&gt;</c> sequence.
    /// </summary>
    public static byte[] ComposeDocument(string catalogEntries, IReadOnlyDictionary<int, string> objects, string[]? pageEntries = null, int[]? markedPages = null)
    {
        var all = new SortedDictionary<int, string>
        {
            [1] = $"<< /Type /Catalog /Pages 2 0 R {catalogEntries} >>",
            [2] = "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            [9] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        for (var i = 0; i < 3; i++)
        {
            var extras = pageEntries is not null && i < pageEntries.Length ? pageEntries[i] : string.Empty;
            all[3 + i] = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents {10 + i} 0 R /Resources << /Font << /F1 9 0 R >> >> {extras} >>";
            var text = $"BT /F1 12 Tf 20 100 Td ({RemovedPageFixtures.PageMarkers[i]}) Tj ET";
            var content = markedPages?.Contains(i) == true ? $"/P << /MCID 0 >> BDC {text} EMC" : text;
            all[10 + i] = $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        }

        foreach (var (number, body) in objects)
        {
            all[number] = body;
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

        xref.Append($"trailer\n<< /Size {size} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        buffer.AddRange(Encoding.Latin1.GetBytes(xref.ToString()));
        return [.. buffer];
    }

    /// <summary>Opens <paramref name="bytes"/>, removes the pages at <paramref name="pageIndexes"/> and saves in <paramref name="layout"/>.</summary>
    public static byte[] SaveAfterRemoval(byte[] bytes, int[] pageIndexes, SaveLayout layout)
    {
        using var document = PdfDocument.Open(bytes);
        RemovedPageFixtures.RemovePages(document, pageIndexes);
        return RemovedPageFixtures.SaveToBytes(document, layout);
    }

    /// <summary>Every <see cref="SaveLayout"/>, as theory data.</summary>
    public static TheoryData<SaveLayout> Layouts()
    {
        var data = new TheoryData<SaveLayout>();
        foreach (var layout in Enum.GetValues<SaveLayout>())
        {
            data.Add(layout);
        }

        return data;
    }

    /// <summary>Follows references until a direct value.</summary>
    public static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var hops = 0;
        while (value is PdfReference reference && hops++ < 8)
        {
            value = document.Objects[reference.Target];
        }

        return value;
    }

    /// <summary>The reopened catalog.</summary>
    public static PdfDictionary Catalog(PdfDocument document) =>
        Assert.IsType<PdfDictionary>(Resolve(document, document.Objects.Trailer[PdfName.Root]));

    /// <summary>The reopened <c>/AcroForm</c> dictionary.</summary>
    public static PdfDictionary AcroForm(PdfDocument document) =>
        Assert.IsType<PdfDictionary>(Resolve(document, Catalog(document)[PdfName.AcroForm]));

    /// <summary>The reopened <c>/StructTreeRoot</c> dictionary.</summary>
    public static PdfDictionary StructTreeRoot(PdfDocument document) =>
        Assert.IsType<PdfDictionary>(Resolve(document, Catalog(document)[PdfName.Get("StructTreeRoot")]));

    /// <summary>The entries of <paramref name="value"/> resolved to an array, asserting none is (or resolves to) <c>null</c>.</summary>
    public static IReadOnlyList<PdfObject> NullFreeArray(PdfDocument document, PdfObject value, string what)
    {
        var array = Assert.IsType<PdfArray>(Resolve(document, value));
        foreach (var entry in array)
        {
            Assert.False(Resolve(document, entry) is PdfNull, $"{what} holds a null entry.");
        }

        return array;
    }

    /// <summary>Walks <c>/Fields</c> and every <c>/Kids</c> beneath it, asserting no entry is <c>null</c>; returns every node reached.</summary>
    public static List<PdfDictionary> FieldTreeNodes(PdfDocument document)
    {
        var nodes = new List<PdfDictionary>();
        var pending = new Stack<PdfObject>(NullFreeArray(document, AcroForm(document)[PdfName.Fields], "/Fields").Reverse());
        while (pending.Count > 0 && nodes.Count < 1000)
        {
            var node = Assert.IsType<PdfDictionary>(Resolve(document, pending.Pop()));
            nodes.Add(node);
            if (node.TryGetValue(PdfName.Kids, out var kids))
            {
                foreach (var kid in NullFreeArray(document, kids, "a field's /Kids").Reverse())
                {
                    pending.Push(kid);
                }
            }
        }

        return nodes;
    }

    /// <summary>Walks the structure tree's <c>/K</c> values, asserting no entry is <c>null</c>; returns every element dictionary reached.</summary>
    public static List<PdfDictionary> StructureElements(PdfDocument document)
    {
        var k = PdfName.Get("K");
        var elements = new List<PdfDictionary>();
        var pending = new Stack<PdfObject>();
        if (StructTreeRoot(document).TryGetValue(k, out var rootKids))
        {
            pending.Push(rootKids);
        }

        while (pending.Count > 0 && elements.Count < 1000)
        {
            var value = Resolve(document, pending.Pop());
            Assert.False(value is PdfNull, "A structure /K holds a null entry.");
            if (value is PdfArray array)
            {
                foreach (var item in array.Reverse())
                {
                    Assert.False(Resolve(document, item) is PdfNull, "A structure /K array holds a null entry.");
                    pending.Push(item);
                }
            }
            else if (value is PdfDictionary element
                && !(element.TryGetValue(PdfName.Type, out var type) && (type == PdfName.Get("MCR") || type == PdfName.Get("OBJR"))))
            {
                elements.Add(element);
                if (element.TryGetValue(k, out var kids))
                {
                    pending.Push(kids);
                }
            }
        }

        return elements;
    }

    /// <summary>The flattened key/value pairs of the reopened <c>/ParentTree</c>.</summary>
    public static List<(int Key, PdfObject Value)> ParentTreeEntries(PdfDocument document)
    {
        var entries = new List<(int, PdfObject)>();
        var pending = new Stack<PdfObject>();
        pending.Push(StructTreeRoot(document)[PdfName.Get("ParentTree")]);
        while (pending.Count > 0)
        {
            var node = Assert.IsType<PdfDictionary>(Resolve(document, pending.Pop()));
            if (node.TryGetValue(PdfName.Get("Nums"), out var nums))
            {
                var array = Assert.IsType<PdfArray>(Resolve(document, nums));
                for (var i = 0; i + 1 < array.Count; i += 2)
                {
                    entries.Add((Assert.IsType<PdfNumber>(array[i]).ToInt32(), array[i + 1]));
                }
            }

            if (node.TryGetValue(PdfName.Kids, out var kids))
            {
                foreach (var kid in Assert.IsType<PdfArray>(Resolve(document, kids)))
                {
                    pending.Push(kid);
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Runs <c>qpdf --check</c> on <paramref name="bytes"/> and asserts a clean result (exit 0)
    /// when qpdf is installed; a no-op otherwise, unless <c>PLUMEPDF_REQUIRE_QPDF=1</c>.
    /// </summary>
    public static void AssertQpdfClean(byte[] bytes, string what)
    {
        if (!QpdfInstalled.Value)
        {
            Assert.False(Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_QPDF") == "1", "PLUMEPDF_REQUIRE_QPDF=1 but qpdf is not installed.");
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-cleanup-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, bytes);
            var (exitCode, output) = Run("qpdf", ["--check", path]);
            Assert.True(exitCode == 0, $"{what}: qpdf --check reported problems (exit {exitCode}):\n{output}");
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
            var (exitCode, output) = Run("qpdf", ["--version"]);
            return exitCode == 0 && output.Contains("qpdf", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static (int ExitCode, string Output) Run(string tool, string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.ExitCode, stdout.Result + stderr);
    }
}
