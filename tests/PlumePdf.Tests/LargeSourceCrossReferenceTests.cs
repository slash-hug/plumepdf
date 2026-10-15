using System.Text;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// A source that reports an arbitrary <see cref="Length"/> but stores only a few byte
/// segments (the head and tail of a document); every other byte reads as a space. Lets a
/// test exercise reading a multi-gigabyte document's structure without the disk or memory
/// that a real file that size would need.
/// </summary>
internal sealed class SparseByteSource(long length, params (long Offset, byte[] Bytes)[] segments) : ByteSource
{
    public override long Length { get; } = length;

    /// <summary>A test's own note about the layout it built (an object offset, say).</summary>
    public long Tag { get; init; }

    public override int Read(long position, Span<byte> destination)
    {
        var count = (int)Math.Min(destination.Length, Math.Max(0, Length - position));
        var target = destination[..count];
        target.Fill((byte)' ');
        foreach (var (offset, bytes) in segments)
        {
            var start = Math.Max(offset, position);
            var end = Math.Min(offset + bytes.Length, position + count);
            if (start < end)
            {
                bytes.AsSpan((int)(start - offset), (int)(end - start)).CopyTo(target[(int)(start - position)..]);
            }
        }

        return count;
    }

    public override void Dispose()
    {
    }
}

/// <summary>
/// Cross-reference reading on sources too large to read into one array: a linearized file's
/// first cross-reference section sits near the start, so reading "from the section to the end
/// of the file" is effectively the whole file.
/// </summary>
public class LargeSourceCrossReferenceTests
{
    private const long ThreeGiB = 3L * 1024 * 1024 * 1024;
    private const long TwoHundredMiB = 200L * 1024 * 1024;

    [Fact]
    public void Read_LinearizedShapeOver2GiB_ReadsBothSectionsAndResolvesRoot()
    {
        using var source = LinearizedShape(ThreeGiB, out _);

        var table = CrossReferenceReader.Read(source, PdfOptions.Default, null);

        var root = Assert.IsType<PdfReference>(table.Trailer[PdfName.Root]);
        Assert.Equal(1, root.Target.Number);
        Assert.Equal(CrossReferenceEntryKind.InFile, table.EntriesByObjectNumber[4].Kind);
        Assert.True(table.EntriesByObjectNumber[4].ByteOffset > int.MaxValue);

        var registry = new ObjectRegistry(new ObjectResolver(source, table, PdfOptions.Default, null));
        var tailObject = Assert.IsType<PdfDictionary>(registry[new IndirectReference(4, 0)]);
        Assert.Equal("TailMarker", ((PdfName)tailObject[PdfName.Type]).Value);
    }

    [Fact]
    public void Read_LinearizedShapeBelow2GiB_AllocatesABoundedWindowNotTheWholeFile()
    {
        using var source = LinearizedShape(TwoHundredMiB, out _);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(table.Trailer.ContainsKey(PdfName.Root));
        Assert.True(allocated < 8L * 1024 * 1024, $"Reading the cross-reference data of a {TwoHundredMiB}-byte file allocated {allocated} bytes.");
    }

    [Fact]
    public void Read_CrossReferenceStreamNearStartOfLargeSource_IsRead()
    {
        var head = new StringBuilder("%PDF-1.7\n");
        var catalogOffset = head.Length;
        head.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var pagesOffset = head.Length;
        head.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var xrefOffset = head.Length;

        // /W [1 4 2], uncompressed: one 7-byte row per object 0..3.
        var rows = new List<byte>();
        void Row(byte type, long field1, int field2)
        {
            rows.Add(type);
            for (var shift = 24; shift >= 0; shift -= 8)
            {
                rows.Add((byte)(field1 >> shift));
            }

            rows.Add((byte)(field2 >> 8));
            rows.Add((byte)field2);
        }

        Row(0, 0, 65535);
        Row(1, catalogOffset, 0);
        Row(1, pagesOffset, 0);
        Row(1, xrefOffset, 0);
        head.Append($"3 0 obj\n<< /Type /XRef /Size 4 /W [1 4 2] /Root 1 0 R /Length {rows.Count} >>\nstream\n");
        var headBytes = Encoding.Latin1.GetBytes(head.ToString()).Concat(rows).Concat("\nendstream\nendobj\n"u8.ToArray()).ToArray();

        var tail = Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n");
        using var source = new SparseByteSource(ThreeGiB, (0, headBytes), (ThreeGiB - tail.Length, tail));

        var table = CrossReferenceReader.Read(source, PdfOptions.Default, null);

        Assert.Equal(catalogOffset, table.EntriesByObjectNumber[1].ByteOffset);
        Assert.Equal(pagesOffset, table.EntriesByObjectNumber[2].ByteOffset);
        Assert.True(table.Trailer.ContainsKey(PdfName.Root));
    }

    [Fact]
    public void Read_ClassicSectionLargerThanTheFirstWindow_ReadsEveryEntry()
    {
        // 200,000 entries is ~4 MB of table: several times any small first read window, so a
        // window that ends mid-entry or mid-trailer must grow rather than truncate the table.
        const int entryCount = 200_000;
        var head = new StringBuilder("%PDF-1.7\n");
        var catalogOffset = head.Length;
        head.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var pagesOffset = head.Length;
        head.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var xrefOffset = head.Length;
        head.Append($"xref\n0 {entryCount}\n0000000000 65535 f \n{catalogOffset:D10} 00000 n \n{pagesOffset:D10} 00000 n \n");
        for (var i = 3; i < entryCount; i++)
        {
            head.Append($"{catalogOffset:D10} 00000 n \n");
        }

        head.Append($"trailer\n<< /Size {entryCount} /Root 1 0 R >>\n");
        var tail = Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n");
        using var source = new SparseByteSource(ThreeGiB, (0, Encoding.ASCII.GetBytes(head.ToString())), (ThreeGiB - tail.Length, tail));

        var diagnostics = new DiagnosticCollection();
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);

        Assert.Equal(entryCount, table.EntriesByObjectNumber.Count);
        Assert.Equal(CrossReferenceEntryKind.InFile, table.EntriesByObjectNumber[entryCount - 1].Kind);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// Every position of the first read window's end from inside the last entries, through the
    /// 'trailer' keyword and each byte of the trailer dictionary, to past it — lenient and strict.
    /// </summary>
    public static TheoryData<int, bool> WindowEndsAroundTheTrailer()
    {
        var data = new TheoryData<int, bool>();
        for (var k = -60; k <= 40; k++)
        {
            data.Add(k, false);
            data.Add(k, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WindowEndsAroundTheTrailer))]
    public void Read_ClassicSectionCutByTheFirstWindowAnywhere_ReadsTheWholeSection(int trailerStartRelativeToWindowEnd, bool strict)
    {
        const int firstWindow = 64 * 1024;
        const int entryCount = 3000;
        var head = new StringBuilder("%PDF-1.7\n");
        var catalogOffset = head.Length;
        head.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var xrefOffset = head.Length;

        var sectionStart = $"xref\n0 {entryCount}";
        var padding = firstWindow + trailerStartRelativeToWindowEnd - (sectionStart.Length + 1 + (20 * entryCount));
        head.Append(sectionStart).Append(' ', padding).Append('\n');
        head.Append("0000000000 65535 f \n");
        for (var i = 1; i < entryCount; i++)
        {
            head.Append($"{catalogOffset:D10} 00000 n \n");
        }

        Assert.Equal(xrefOffset + firstWindow + trailerStartRelativeToWindowEnd, head.Length);
        head.Append($"trailer\n<< /Size {entryCount} /Root 1 0 R /Info 1 0 R >>\n");
        var tail = Encoding.ASCII.GetBytes($"startxref\n{xrefOffset}\n%%EOF\n");
        using var source = new SparseByteSource(ThreeGiB, (0, Encoding.ASCII.GetBytes(head.ToString())), (ThreeGiB - tail.Length, tail));

        var diagnostics = new DiagnosticCollection();
        var table = CrossReferenceReader.Read(source, PdfOptions.Default with { Strict = strict }, diagnostics);

        Assert.Equal(entryCount, table.EntriesByObjectNumber.Count);
        Assert.True(table.Trailer.ContainsKey(PdfName.Root));
        Assert.True(table.Trailer.ContainsKey(PdfName.Info));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ReadWithRecovery_UnreadableSourceLargerThanOneArray_ThrowsCodedNotOutOfMemory()
    {
        // No startxref anywhere: the clean read fails and lenient reading falls back to the
        // brute-force scan, which cannot hold a 3 GiB source in one array.
        using var source = new SparseByteSource(ThreeGiB, (0, "%PDF-1.7\n1 0 obj\n<< /Type /Catalog >>\nendobj\n"u8.ToArray()));

        var ex = Assert.Throws<PlumePdfException>(() => CrossReferenceReader.ReadWithRecovery(source, PdfOptions.Default, new DiagnosticCollection()));
        Assert.Equal("PLUME1005", ex.Code);
    }

    /// <summary>
    /// The shape of a linearized file: objects 1–3 and the first-page cross-reference section
    /// at the start, its trailer's <c>/Prev</c> pointing at the main section at the end, and
    /// the final <c>startxref</c> pointing back at the first-page section.
    /// </summary>
    private static SparseByteSource LinearizedShape(long length, out long tailObjectOffset)
    {
        var head = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[4];
        offsets[1] = head.Length;
        head.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = head.Length;
        head.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = head.Length;
        head.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        var headXrefOffset = head.Length;

        var tailStart = length - 1024;
        tailObjectOffset = tailStart;
        var tail = new StringBuilder();
        tail.Append("4 0 obj\n<< /Type /TailMarker >>\nendobj\n");
        var mainXrefOffset = tailStart + tail.Length;
        tail.Append($"xref\n4 1\n{tailObjectOffset:D10} 00000 n \ntrailer\n<< /Size 5 >>\n");
        tail.Append($"startxref\n{headXrefOffset}\n%%EOF\n");

        head.Append("xref\n0 4\n0000000000 65535 f \n");
        for (var i = 1; i <= 3; i++)
        {
            head.Append($"{offsets[i]:D10} 00000 n \n");
        }

        head.Append($"trailer\n<< /Size 5 /Root 1 0 R /Prev {mainXrefOffset} >>\n");

        return new SparseByteSource(length, (0, Encoding.ASCII.GetBytes(head.ToString())), (tailStart, Encoding.ASCII.GetBytes(tail.ToString())));
    }
}
