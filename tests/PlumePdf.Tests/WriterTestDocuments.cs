using System.Text;

namespace PlumePdf.Tests;

/// <summary>
/// Hand-rolled classic-xref PDF construction for the writer test suites (WriterTests,
/// IncrementalSaveTests, DeterministicTests, RoundTripTests, MergeSplitTests,
/// PageReorderTests, OpenSaveContractTests) — deliberately independent of PlumePDF's own
/// writer (the thing under test), written directly from ISO 32000-1's object/xref-table
/// grammar (a source allowed under the clean-room policy in AGENTS.md), the same approach as
/// <c>benchmarks/PlumePdf.Benchmarks/OpenBenchmarks.cs</c>'s <c>SampleDocuments</c> and
/// <c>tests/PlumePdf.CorpusTests/Fixtures/generate_fixtures.py</c>, just local to this
/// project so it has no cross-project dependency.
/// </summary>
internal static class WriterTestDocuments
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

    /// <summary>A minimal, well-formed classic-xref document with <paramref name="pageCount"/> pages, each with its own content stream and a shared font.</summary>
    public static byte[] BuildDocument(int pageCount, bool includeInfo = false)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int fontNum = 3;
        var nextObjNum = 4;
        var pageNums = new List<int>();
        var contentNums = new List<int>();
        for (var i = 0; i < pageCount; i++)
        {
            pageNums.Add(nextObjNum++);
            contentNums.Add(nextObjNum++);
        }

        var infoNum = includeInfo ? nextObjNum++ : (int?)null;
        var totalObjects = nextObjNum;

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");

        var kids = string.Join(" ", pageNums.ConvertAll(n => $"{n} 0 R"));
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        WriteObject(fontNum, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (var i = 0; i < pageCount; i++)
        {
            WriteObject(
                pageNums[i],
                $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 100] " +
                $"/Resources << /Font << /F1 {fontNum} 0 R >> >> /Contents {contentNums[i]} 0 R >>");

            var content = $"BT /F1 12 Tf 20 50 Td (Page {i + 1} of {pageCount}) Tj ET";
            offsets[contentNums[i]] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{contentNums[i]} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));
        }

        if (infoNum is int info)
        {
            WriteObject(info, "<< /Title (Test Document) >>");
        }

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        var trailerInfo = infoNum is int i2 ? $" /Info {i2} 0 R" : string.Empty;
        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R{trailerInfo} /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// A single-page document where <c>/MediaBox</c>, <c>/Resources</c>, <c>/CropBox</c>, and
    /// <c>/Rotate</c> are set only on the <c>/Pages</c> root (ISO 32000-1 §7.7.3.4's
    /// inheritable attributes) — the page itself carries only <c>/Type</c>, <c>/Parent</c>,
    /// and <c>/Contents</c>, exactly the legal, common real-world layout PageTreeReaderTests
    /// and the writer round-trip tests use to guard against dropping inherited attributes.
    /// </summary>
    public static byte[] BuildDocumentWithInheritedAttributes()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int fontNum = 3;
        const int pageNum = 4;
        const int contentNum = 5;
        const int totalObjects = 6;

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        WriteObject(
            pagesNum,
            $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 /MediaBox [0 0 200 100] " +
            $"/CropBox [0 0 200 100] /Rotate 90 /Resources << /Font << /F1 {fontNum} 0 R >> >> >>");
        WriteObject(fontNum, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        WriteObject(pageNum, $"<< /Type /Page /Parent {pagesNum} 0 R /Contents {contentNum} 0 R >>");

        var content = "BT /F1 12 Tf 20 50 Td (Inherited attributes) Tj ET";
        offsets[contentNum] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"{contentNum} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// A single-page document whose whole <c>[0 0 200 100]</c> <c>/MediaBox</c> is filled
    /// opaque red (<c>1 0 0 rg 0 0 200 100 re f</c>) — for pixel-level
    /// <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c> verb tests (RasterizeTests), where a
    /// known, unambiguous fill color proves the verb actually painted this page's content
    /// rather than just returning a correctly-sized blank surface. <paramref name="rotate"/>
    /// sets the page's own <c>/Rotate</c> directly (0/omitted means none).
    /// </summary>
    public static byte[] BuildDocumentWithFilledRect(int rotate = 0)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int contentNum = 4;
        const int totalObjects = 5;

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        var rotateEntry = rotate != 0 ? $" /Rotate {rotate}" : string.Empty;
        WriteObject(pageNum, $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 100]{rotateEntry} /Contents {contentNum} 0 R >>");

        var content = "1 0 0 rg 0 0 200 100 re f";
        offsets[contentNum] = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"{contentNum} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>
    /// A one-page document whose <c>/Contents</c> is an INDIRECT REFERENCE to an array of two
    /// content streams (a red full-page fill, then Helvetica text) — the legal §7.7.3.3 shape
    /// LiveCycle/AEM AcroForms use that used to render/extract as a silently
    /// blank page (every pre-fix consumer only recognized "reference to a stream" and "direct
    /// array"). Pass <paramref name="unusableContents"/> to point <c>/Contents</c> at a plain
    /// number instead — the genuinely-unusable shape that must surface PLUME6083.
    /// </summary>
    public static byte[] BuildDocumentWithIndirectContentsArray(bool unusableContents = false)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int arrayNum = 4;
        const int fillContentNum = 5;
        const int textContentNum = 6;
        const int fontNum = 7;
        const int totalObjects = 8;

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        void WriteStream(int num, string content)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));
        }

        WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        WriteObject(pageNum, $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 {fontNum} 0 R >> >> /Contents {arrayNum} 0 R >>");
        WriteObject(arrayNum, unusableContents ? "42" : $"[{fillContentNum} 0 R {textContentNum} 0 R]");
        WriteStream(fillContentNum, "1 0 0 rg 0 0 200 100 re f");
        WriteStream(textContentNum, "BT /F1 12 Tf 10 40 Td (Hello) Tj ET");
        WriteObject(fontNum, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects}\n0000000000 65535 f \n"));
        for (var n = 1; n < totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects} /Root {catalogNum} 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    /// <summary>Writes <paramref name="bytes"/> to a fresh temp file and returns its path.</summary>
    public static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-writertest-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
