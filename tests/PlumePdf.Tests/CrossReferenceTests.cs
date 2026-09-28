using System.Text;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>Builds minimal, hand-crafted PDF byte buffers for the cross-reference and recovery tests — no third-party fixtures involved.</summary>
internal static class PdfFixtureBuilder
{
    public static byte[] BuildClassicXrefPdf(IReadOnlyList<(int Number, string Body)> objects, int rootObjectNumber, IReadOnlyDictionary<int, string>? badEntryFlags = null)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();

        foreach (var (number, body) in objects)
        {
            offsets[number] = sb.Length;
            sb.Append($"{number} 0 obj\n{body}\nendobj\n");
        }

        var xrefOffset = sb.Length;
        var maxNumber = objects.Count == 0 ? 0 : objects.Max(o => o.Number);
        sb.Append($"xref\n0 {maxNumber + 1}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i <= maxNumber; i++)
        {
            if (badEntryFlags is not null && badEntryFlags.TryGetValue(i, out var flag))
            {
                sb.Append($"0000000000 00000 {flag} \n");
            }
            else if (offsets.TryGetValue(i, out var offset))
            {
                sb.Append($"{offset:D10} 00000 n \n");
            }
            else
            {
                sb.Append("0000000000 00000 f \n");
            }
        }

        sb.Append($"trailer\n<< /Size {maxNumber + 1} /Root {rootObjectNumber} 0 R >>\n");
        sb.Append($"startxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    public static byte[] BuildObjectsOnlyNoXref(IReadOnlyList<(int Number, string Body)> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        foreach (var (number, body) in objects)
        {
            sb.Append($"{number} 0 obj\n{body}\nendobj\n");
        }

        sb.Append("%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

public class CrossReferenceTests
{
    private static readonly (int Number, string Body)[] SimpleObjects =
    [
        (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        (3, "<< /Type /Page /Parent 2 0 R >>"),
    ];

    [Fact]
    public void Read_ClassicTable_ResolvesRootAndFollowsReferences()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(SimpleObjects, rootObjectNumber: 1);
        using var source = new StreamByteSource(bytes);

        var table = CrossReferenceReader.Read(source, PdfOptions.Default, null);

        Assert.True(table.Trailer.TryGetValue(PdfName.Root, out var rootValue));
        var rootRef = Assert.IsType<PdfReference>(rootValue);
        Assert.Equal(1, rootRef.Target.Number);

        var resolver = new ObjectResolver(source, table, PdfOptions.Default, null);
        var registry = new ObjectRegistry(resolver);

        var catalog = Assert.IsType<PdfDictionary>(registry[rootRef.Target]);
        var pagesRef = Assert.IsType<PdfReference>(catalog[PdfName.Get("Pages")]);
        var pages = Assert.IsType<PdfDictionary>(registry[pagesRef.Target]);
        Assert.Equal(1, ((PdfNumber)pages[PdfName.Get("Count")]).ToInt32());
    }

    [Fact]
    public void Read_MissingStartxref_ThrowsCoded()
    {
        var bytes = PdfFixtureBuilder.BuildObjectsOnlyNoXref(SimpleObjects);
        using var source = new StreamByteSource(bytes);

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.Read(source, PdfOptions.Default, null));
        Assert.Equal("PLUME2040", ex.Code);
    }

    [Fact]
    public void Read_MalformedEntry_IsSkippedWithDiagnosticButTableStillUsable()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(SimpleObjects, rootObjectNumber: 1, badEntryFlags: new Dictionary<int, string> { [3] = "q" });
        using var source = new StreamByteSource(bytes);
        var diagnostics = new DiagnosticCollection();

        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME2037");
        Assert.True(table.EntriesByObjectNumber.ContainsKey(1));
        Assert.True(table.EntriesByObjectNumber.ContainsKey(2));
        Assert.False(table.EntriesByObjectNumber.ContainsKey(3)); // the malformed entry was skipped, not guessed at
    }

    [Fact]
    public void Read_MalformedEntry_UnderStrict_Throws()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(SimpleObjects, rootObjectNumber: 1, badEntryFlags: new Dictionary<int, string> { [3] = "q" });
        using var source = new StreamByteSource(bytes);
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.Read(source, strict, null));
        Assert.Equal("PLUME2037", ex.Code);
    }

    [Fact]
    public void Read_PrevChain_NewerEntryWinsOverOlder()
    {
        // Revision 1: object 1 says "v1". Revision 2 (appended): object 1 says "v2" and
        // chains back to revision 1's xref via /Prev.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var obj1v1Offset = sb.Length;
        sb.Append("1 0 obj\n(v1)\nendobj\n");

        var xref1Offset = sb.Length;
        sb.Append("xref\n0 2\n0000000000 65535 f \n");
        sb.Append($"{obj1v1Offset:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 2 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref1Offset}\n%%EOF\n");

        var obj1v2Offset = sb.Length;
        sb.Append("1 0 obj\n(v2)\nendobj\n");

        var xref2Offset = sb.Length;
        sb.Append("xref\n0 2\n0000000000 65535 f \n");
        sb.Append($"{obj1v2Offset:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size 2 /Root 1 0 R /Prev {xref1Offset} >>\n");
        sb.Append($"startxref\n{xref2Offset}\n%%EOF");

        using var source = new StreamByteSource(Encoding.ASCII.GetBytes(sb.ToString()));
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, null);
        var resolver = new ObjectResolver(source, table, PdfOptions.Default, null);

        var value = Assert.IsType<PdfString>(resolver.Resolve(new IndirectReference(1, 0)));
        Assert.Equal("v2", value.GetText());
    }

    [Fact]
    public void Read_PrevChainCycle_StopsInsteadOfLoopingForever()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var objOffset = sb.Length;
        sb.Append("1 0 obj\n(v)\nendobj\n");

        var xrefOffset = sb.Length;
        sb.Append("xref\n0 2\n0000000000 65535 f \n");
        sb.Append($"{objOffset:D10} 00000 n \n");
        // /Prev points at itself - a direct cycle.
        sb.Append($"trailer\n<< /Size 2 /Root 1 0 R /Prev {xrefOffset} >>\n");
        sb.Append($"startxref\n{xrefOffset}\n%%EOF");

        using var source = new StreamByteSource(Encoding.ASCII.GetBytes(sb.ToString()));
        var diagnostics = new DiagnosticCollection();

        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME2030");
        Assert.True(table.EntriesByObjectNumber.ContainsKey(1));
    }

    [Fact]
    public void Read_CrossReferenceStream_DecodesEntries()
    {
        // A minimal xref stream: one subsection [0 2], /W [1 4 2], uncompressed (no /Filter).
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var obj1Offset = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        var xrefStreamOffset = sb.Length;
        var header = Encoding.ASCII.GetBytes(sb.ToString());

        // Entry for object 0 (free): type 0, next-free 0, gen 65535.
        // Entry for object 1 (in file): type 1, offset obj1Offset, gen 0.
        var payload = new List<byte>();
        AppendEntry(payload, 0, 0, 65535);
        AppendEntry(payload, 1, obj1Offset, 0);
        var payloadBytes = payload.ToArray();

        var xrefDictText = $"2 0 obj\n<< /Type /XRef /Size 2 /W [1 4 2] /Root 1 0 R /Length {payloadBytes.Length} >>\nstream\n";
        var xrefHeaderBytes = Encoding.ASCII.GetBytes(xrefDictText);

        var fullBytes = new List<byte>();
        fullBytes.AddRange(header);
        fullBytes.AddRange(xrefHeaderBytes);
        fullBytes.AddRange(payloadBytes);
        fullBytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        var startxrefText = $"startxref\n{xrefStreamOffset}\n%%EOF";
        fullBytes.AddRange(Encoding.ASCII.GetBytes(startxrefText));

        using var source = new StreamByteSource(fullBytes.ToArray());
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, null);

        Assert.True(table.EntriesByObjectNumber.TryGetValue(1, out var entry));
        Assert.Equal(CrossReferenceEntryKind.InFile, entry.Kind);
        Assert.Equal(obj1Offset, entry.ByteOffset);
    }

    [Fact]
    public void Read_HybridFile_XRefStmEntryOverridesSameRevisionClassicFreeEntry()
    {
        // Object 2 is stored in an object stream (a compressed type-2 entry only /XRefStm
        // can express); the classic table - kept for pre-1.5 reader compatibility - marks
        // object 2 free, the shape real-world producers (Acrobat) emit. /XRefStm must win.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var obj1Offset = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        var objStmHeaderText = "2 0 ";
        var objStmBodyText = "(compressed)";
        var objStmDataText = objStmHeaderText + objStmBodyText;
        var objStmOffset = sb.Length;
        sb.Append($"3 0 obj\n<< /Type /ObjStm /N 1 /First {objStmHeaderText.Length} /Length {objStmDataText.Length} >>\nstream\n{objStmDataText}\nendstream\nendobj\n");

        var xrefStmOffset = sb.Length;
        var header = Encoding.ASCII.GetBytes(sb.ToString());

        var payload = new List<byte>();
        AppendEntry(payload, 2, 3, 0); // object 2: type 2, in object stream 3, index 0.
        var payloadBytes = payload.ToArray();
        var xrefDictText = $"4 0 obj\n<< /Type /XRef /Size 5 /Index [2 1] /W [1 4 2] /Length {payloadBytes.Length} >>\nstream\n";
        var xrefHeaderBytes = Encoding.ASCII.GetBytes(xrefDictText);

        var streamBytes = new List<byte>();
        streamBytes.AddRange(xrefHeaderBytes);
        streamBytes.AddRange(payloadBytes);
        streamBytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));

        var classicTableOffset = header.Length + streamBytes.Count;
        var classicLines = new StringBuilder();
        classicLines.Append("xref\n0 5\n");
        classicLines.Append("0000000000 65535 f \n");
        classicLines.Append($"{obj1Offset:D10} 00000 n \n");
        classicLines.Append("0000000000 00000 f \n"); // object 2 marked FREE in the classic table.
        classicLines.Append($"{objStmOffset:D10} 00000 n \n");
        classicLines.Append($"{xrefStmOffset:D10} 00000 n \n");
        classicLines.Append($"trailer\n<< /Size 5 /Root 1 0 R /XRefStm {xrefStmOffset} >>\n");
        classicLines.Append($"startxref\n{classicTableOffset}\n%%EOF");

        var fullBytes = new List<byte>();
        fullBytes.AddRange(header);
        fullBytes.AddRange(streamBytes);
        fullBytes.AddRange(Encoding.ASCII.GetBytes(classicLines.ToString()));

        using var source = new StreamByteSource(fullBytes.ToArray());
        var diagnostics = new DiagnosticCollection();
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);

        Assert.True(table.EntriesByObjectNumber.TryGetValue(2, out var entry));
        Assert.Equal(CrossReferenceEntryKind.InObjectStream, entry.Kind);
        Assert.Equal(3, entry.StreamObjectNumber);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME2060");
    }

    [Fact]
    public void Read_ClassicTable_SubsectionCountFarExceedingRemainingBytesIsClampedNotHung()
    {
        // A hostile subsection header claims far more entries than could possibly fit in
        // what's left of the buffer - this must clamp and return promptly rather than
        // looping "count" times (which, for a count beyond int.MaxValue, would otherwise wrap
        // the classic int loop counter negative and never terminate).
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var obj1Offset = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        var xrefOffset = sb.Length;
        sb.Append("xref\n0 2000000000\n"); // declares two billion entries over a ~90-byte file.
        sb.Append("0000000000 65535 f \n");
        sb.Append($"{obj1Offset:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 2 /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xrefOffset}\n%%EOF");

        using var source = new StreamByteSource(Encoding.ASCII.GetBytes(sb.ToString()));
        var diagnostics = new DiagnosticCollection();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Read took {sw.Elapsed} - the subsection count was not bounded.");
        Assert.Contains(diagnostics, d => d.Code == "PLUME2037");
        Assert.True(table.EntriesByObjectNumber.ContainsKey(1));
    }

    private static void AppendEntry(List<byte> payload, int type, long field2, int field3)
    {
        payload.Add((byte)type);
        payload.Add((byte)(field2 >> 24));
        payload.Add((byte)(field2 >> 16));
        payload.Add((byte)(field2 >> 8));
        payload.Add((byte)field2);
        payload.Add((byte)(field3 >> 8));
        payload.Add((byte)field3);
    }

    [Theory]
    [InlineData("/W [0 0 0] /Index [0 2000000000]")] // entryWidth 0: the exhaustion bound never advances -> unbounded allocation before the fix
    [InlineData("/W [1 -5 0]")] // negative width: negative buffer indexing (bare IndexOutOfRangeException) before the fix
    public void Read_CrossReferenceStream_HostileW_ThrowsCoded(string wAndIndex)
    {
        using var source = new StreamByteSource(BuildHostileXrefStreamDocument(wAndIndex));

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.Read(source, PdfOptions.Default, null));

        Assert.Equal("PLUME2038", ex.Code);
    }

    [Fact]
    public void Open_HostileWXrefStream_RecoversOrThrowsCoded_NeverABareException()
    {
        // End-to-end shape of the review repro: PdfDocument.Open on a /W [1 -5 0] file threw
        // a bare IndexOutOfRangeException and skipped the recovery ladder entirely.
        var bytes = BuildHostileXrefStreamDocument("/W [1 -5 0]");

        var ex = Record.Exception(() =>
        {
            using var document = PdfDocument.Open(new ReadOnlyMemory<byte>(bytes));
            _ = document.Pages.Count;
        });

        Assert.True(ex is null or PlumePdfException, $"Expected recovery or a coded error, got {ex?.GetType().Name}: {ex?.Message}");
    }

    private static byte[] BuildHostileXrefStreamDocument(string wAndIndex)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var obj1Offset = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        var xrefStreamOffset = sb.Length;
        var header = Encoding.ASCII.GetBytes(sb.ToString());

        var payload = new List<byte>();
        AppendEntry(payload, 1, obj1Offset, 0);
        var payloadBytes = payload.ToArray();

        var fullBytes = new List<byte>();
        fullBytes.AddRange(header);
        fullBytes.AddRange(Encoding.ASCII.GetBytes($"2 0 obj\n<< /Type /XRef /Size 2 {wAndIndex} /Root 1 0 R /Length {payloadBytes.Length} >>\nstream\n"));
        fullBytes.AddRange(payloadBytes);
        fullBytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        fullBytes.AddRange(Encoding.ASCII.GetBytes($"startxref\n{xrefStreamOffset}\n%%EOF"));
        return fullBytes.ToArray();
    }

    [Theory]
    [InlineData("/W [1 4000000000000 0]")] // width past int range: checked ToInt32 threw a bare OverflowException before the fix (pdf.js REDHAT-1531897-0.pdf shape)
    [InlineData("/W [1 99999999999999999999 0]")] // width past long range too
    public void Read_CrossReferenceStream_OverflowingW_ThrowsCoded(string wAndIndex)
    {
        using var source = new StreamByteSource(BuildHostileXrefStreamDocument(wAndIndex));

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.Read(source, PdfOptions.Default, null));

        Assert.Equal("PLUME2038", ex.Code);
    }

    [Fact]
    public void Read_CrossReferenceStream_OverflowingIndex_SkipsSubsectionWithDiagnostic()
    {
        using var source = new StreamByteSource(BuildHostileXrefStreamDocument("/W [1 4 2] /Index [0 4000000000000]"));
        var diagnostics = new DiagnosticCollection();

        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == "PLUME2039");
        Assert.Empty(table.EntriesByObjectNumber);
    }
}
