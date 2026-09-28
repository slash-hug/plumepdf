using System.Text;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

public class ResourceLimitTests
{
    [Fact]
    public void Open_ObjectStreamWithHostileNOverATinyPayload_ClampsInsteadOfAllocatingGigabytes()
    {
        // The exact shape of the reported issue: a self-authored document whose /ObjStm
        // declares /N far beyond what its actual (tiny) decoded payload could hold. Resolving
        // the one real object inside it must return promptly, not hang or attempt a
        // multi-gigabyte allocation driven purely by the hostile /N value.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var obj1Offset = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        const string objStmPayload = "6 0 (hi)"; // header claims object 6 at offset 0, body "(hi)"
        var objStmOffset = sb.Length;
        sb.Append($"2 0 obj\n<< /Type /ObjStm /N 400000000 /First 3 /Length {objStmPayload.Length} >>\nstream\n{objStmPayload}\nendstream\nendobj\n");

        var xrefStreamOffset = sb.Length;
        var header = Encoding.ASCII.GetBytes(sb.ToString());

        var payload = new List<byte>();
        AppendEntry(payload, 0, 0, 65535);
        AppendEntry(payload, 1, obj1Offset, 0);
        AppendEntry(payload, 1, objStmOffset, 0); // object 2: the ObjStm itself, stored directly in the file
        AppendEntry(payload, type: 2, field2: 2, field3: 0); // object 6 lives in object stream 2, index 0
        var payloadBytes = payload.ToArray();

        var xrefDictText = $"3 0 obj\n<< /Type /XRef /Size 7 /Index [0 3 6 1] /W [1 4 2] /Root 1 0 R /Length {payloadBytes.Length} >>\nstream\n";
        var xrefHeaderBytes = Encoding.ASCII.GetBytes(xrefDictText);

        var fullBytes = new List<byte>();
        fullBytes.AddRange(header);
        fullBytes.AddRange(xrefHeaderBytes);
        fullBytes.AddRange(payloadBytes);
        fullBytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        fullBytes.AddRange(Encoding.ASCII.GetBytes($"startxref\n{xrefStreamOffset}\n%%EOF"));

        using var source = new StreamByteSource(fullBytes.ToArray());
        var diagnostics = new DiagnosticCollection();
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);
        var resolver = new ObjectResolver(source, table, PdfOptions.Default, diagnostics);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var value = resolver.Resolve(new IndirectReference(6, 0));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Resolving took {sw.Elapsed} - /N was not bounded.");
        var str = Assert.IsType<PdfString>(value);
        Assert.Equal("hi", str.GetText());
        Assert.Contains(diagnostics, d => d.Code == "PLUME2057");
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

    [Fact]
    public void PdfScanner_ClassifiesWhitespaceAndDelimiters()
    {
        Assert.True(PdfScanner.IsWhitespace((byte)' '));
        Assert.True(PdfScanner.IsWhitespace((byte)'\n'));
        Assert.True(PdfScanner.IsDelimiter((byte)'/'));
        Assert.True(PdfScanner.IsDelimiter((byte)'('));
        Assert.True(PdfScanner.IsRegular((byte)'A'));
        Assert.False(PdfScanner.IsRegular((byte)' '));
        Assert.False(PdfScanner.IsRegular((byte)'/'));
    }

    [Fact]
    public void FindAll_FindsEveryOccurrenceWithinTheCap()
    {
        var buffer = Encoding.ASCII.GetBytes("1 0 obj\n2 0 obj\n3 0 obj\n");
        var matches = PdfScanner.FindAll(buffer, "obj"u8, buffer.Length);

        Assert.Equal(3, matches.Count);
    }

    [Fact]
    public void FindAll_ScanCap_NeverExaminesBytesBeyondTheCap()
    {
        // A huge run of junk bytes that contain no match, followed by a marker placed
        // well past a deliberately small cap. Without the cap, this would be found;
        // with it, the scan must never reach that far — proving the resource limit
        // actually bounds the work rather than being decorative.
        const int junkSize = 8 * 1024 * 1024; // 8 MiB
        const int cap = 4096;

        var buffer = new byte[junkSize + 3];
        Array.Fill(buffer, (byte)'.');
        "obj"u8.CopyTo(buffer.AsSpan(junkSize));

        var matches = PdfScanner.FindAll(buffer, "obj"u8, cap);

        Assert.Empty(matches);
    }

    [Fact]
    public void FindAll_ScanCap_StillFindsMatchesBeforeTheCap()
    {
        var buffer = new byte[10_000];
        Array.Fill(buffer, (byte)'.');
        "obj"u8.CopyTo(buffer.AsSpan(10));

        var matches = PdfScanner.FindAll(buffer, "obj"u8, 100);

        Assert.Equal([10], matches);
    }

    [Fact]
    public void FindAll_EmptyPatternOrCap_ReturnsNoMatches()
    {
        var buffer = Encoding.ASCII.GetBytes("obj obj obj");
        Assert.Empty(PdfScanner.FindAll(buffer, ReadOnlySpan<byte>.Empty, buffer.Length));
        Assert.Empty(PdfScanner.FindAll(buffer, "obj"u8, 0));
    }

    [Fact]
    public void StreamByteSource_MemoryBacked_ReadsExactBytes()
    {
        byte[] data = Encoding.ASCII.GetBytes("0123456789");
        using var source = new StreamByteSource(data);

        Span<byte> destination = stackalloc byte[4];
        var read = source.Read(3, destination);

        Assert.Equal(4, read);
        Assert.Equal("3456"u8.ToArray(), destination.ToArray());
    }

    [Fact]
    public void StreamByteSource_StreamBacked_ServesReadsAcrossBufferRefills()
    {
        var data = new byte[200_000];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        using var stream = new MemoryStream(data);
        using var source = new StreamByteSource(stream);

        Assert.Equal(data.Length, source.Length);

        var destination = new byte[500];
        // A read that spans past the 64 KiB internal window forces a refill.
        var read = source.Read(65_000, destination);

        Assert.Equal(500, read);
        Assert.Equal(data.AsSpan(65_000, 500).ToArray(), destination);
    }

    [Fact]
    public void StreamByteSource_Read_PastEndOfSource_ReturnsFewerBytes()
    {
        byte[] data = Encoding.ASCII.GetBytes("short");
        using var source = new StreamByteSource(data);

        var destination = new byte[10];
        var read = source.Read(2, destination);

        Assert.Equal(3, read); // "ort"
    }

    [Fact]
    public void StreamByteSource_RejectsNonSeekableStream()
    {
        using var nonSeekable = new NonSeekableStream(new MemoryStream([1, 2, 3]));
        Assert.Throws<ArgumentException>(() => new StreamByteSource(nonSeekable));
    }

    [Fact]
    public void MemoryMappedByteSource_ReadsBytesFromRealFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-mmap-test-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("Hello, PlumePDF!"));

            using var source = new MemoryMappedByteSource(path);
            Assert.Equal(16, source.Length);

            var destination = new byte[5];
            var read = source.Read(7, destination);

            Assert.Equal(5, read);
            Assert.Equal("Plume", Encoding.ASCII.GetString(destination));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MemoryMappedByteSource_MissingFile_ThrowsCodedException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-does-not-exist-{Guid.NewGuid():N}.pdf");
        var ex = Assert.Throws<PlumePdfException>(() => new MemoryMappedByteSource(path));
        Assert.Equal("PLUME1002", ex.Code);
    }

    [Fact]
    public void MemoryMappedByteSource_EmptyFile_ThrowsCodedException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-empty-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, []);
            var ex = Assert.Throws<PlumePdfException>(() => new MemoryMappedByteSource(path));
            Assert.Equal("PLUME1003", ex.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PdfOptions_ResourceLimits_HaveConservativeDefaults()
    {
        var options = PdfOptions.Default;

        Assert.True(options.MaxDecompressedStreamBytes > 0);
        Assert.True(options.MaxObjectNestingDepth > 0);
        Assert.True(options.MaxCrossReferencePrevChainLength > 0);
        Assert.True(options.MaxObjectStreamExtendsDepth > 0);
        Assert.True(options.MaxObjectStreamEntries > 0);
        Assert.Equal(long.MaxValue, options.MaxBruteForceScanBytes);
    }

    /// <summary>Wraps a <see cref="MemoryStream"/> to deny seeking, for the non-seekable-stream test.</summary>
    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Resolve_ObjectStreamIndexOutOfRange_IsLenientNotFatal()
    {
        // A cross-reference entry claiming an object at index 5 of an
        // object stream declaring /N 1 threw PLUME2050 straight out of PdfDocument.Open,
        // while the analogous in-file failure resolved leniently to null (PLUME2061).
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var obj1Offset = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        const string objStmPayload = "6 0 (hi)";
        var objStmOffset = sb.Length;
        sb.Append($"2 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Length {objStmPayload.Length} >>\nstream\n{objStmPayload}\nendstream\nendobj\n");

        var xrefStreamOffset = sb.Length;
        var header = Encoding.ASCII.GetBytes(sb.ToString());

        var payload = new List<byte>();
        AppendEntry(payload, 0, 0, 65535);
        AppendEntry(payload, 1, obj1Offset, 0);
        AppendEntry(payload, 1, objStmOffset, 0);
        AppendEntry(payload, type: 2, field2: 2, field3: 5); // object 6 claimed at index 5 of a 1-entry stream
        var payloadBytes = payload.ToArray();

        var fullBytes = new List<byte>();
        fullBytes.AddRange(header);
        fullBytes.AddRange(Encoding.ASCII.GetBytes($"3 0 obj\n<< /Type /XRef /Size 7 /Index [0 3 6 1] /W [1 4 2] /Root 1 0 R /Length {payloadBytes.Length} >>\nstream\n"));
        fullBytes.AddRange(payloadBytes);
        fullBytes.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        fullBytes.AddRange(Encoding.ASCII.GetBytes($"startxref\n{xrefStreamOffset}\n%%EOF"));

        using var source = new StreamByteSource(fullBytes.ToArray());
        var diagnostics = new DiagnosticCollection();
        var table = CrossReferenceReader.Read(source, PdfOptions.Default, diagnostics);
        var resolver = new ObjectResolver(source, table, PdfOptions.Default, diagnostics);

        var value = resolver.Resolve(new IndirectReference(6, 0));

        Assert.Same(PdfNull.Instance, value);
        Assert.Contains(diagnostics, d => d.Code == "PLUME2063");
    }
}
