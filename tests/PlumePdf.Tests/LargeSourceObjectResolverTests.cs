using System.Text;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Resolving stream objects by their declared length: an indirect <c>/Length</c> is resolved
/// through the cross-reference table and the object read once at its exact size, so neither
/// a source over 2 GiB nor a large payload below it reads (or allocates) the rest of the file.
/// </summary>
public class LargeSourceObjectResolverTests
{
    private const long ThreeGiB = 3L * 1024 * 1024 * 1024;
    private const long TwoHundredMiB = 200L * 1024 * 1024;
    private const int StreamObject = 5;
    private const int StreamOffset = 9; // right after "%PDF-1.7\n"

    [Fact]
    public void Resolve_IndirectLengthNearStartOfSourceOver2GiB_ReadsTheExactPayload()
    {
        const int payloadLength = 100_000;
        using var source = StreamAtStart(ThreeGiB, payloadLength, indirectLength: true, out var payload);
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_LargeStream_AllocatesAboutThePayloadNotTheRestOfTheFile(bool indirectLength)
    {
        const int payloadLength = 8 * 1024 * 1024;
        using var source = StreamAtStart(TwoHundredMiB, payloadLength, indirectLength, out _);
        var resolver = Resolver(source, null);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var stream = Assert.IsType<PdfStream>(resolver.Resolve(new IndirectReference(StreamObject, 0)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(payloadLength, stream.RawBytes.Length);

        // One read buffer plus the payload copy the parsed stream keeps; the old growing
        // probe allocated ~8x the payload, or the whole rest of the file for an indirect length.
        Assert.True(allocated < (2L * payloadLength) + (1024 * 1024), $"Resolving a {payloadLength}-byte stream allocated {allocated} bytes.");
    }

    [Fact]
    public void Resolve_IndirectLengthUnderStrict_ParsesWithoutTheRecoveryScan()
    {
        using var source = StreamAtStart(TwoHundredMiB, 10_000, indirectLength: true, out var payload);

        var stream = Assert.IsType<PdfStream>(Resolver(source, null, PdfOptions.Default with { Strict = true }).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
    }

    [Fact]
    public void Resolve_IndirectLengthOfWrongValue_RecoversByScanningForEndstream()
    {
        using var source = StreamAtStart(TwoHundredMiB, 10_000, indirectLength: true, out var payload, lengthObjectValue: 7_000);
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.Contains(diagnostics, d => d.Code == "PLUME2019");
    }

    [Fact]
    public void Resolve_LengthReferringToItsOwnStream_RecoversWithoutRecursing()
    {
        var payload = Payload(10_000);
        var head = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< /Length {StreamObject} 0 R >>\nstream\n")
            .Concat(payload).Concat("\nendstream\nendobj\n"u8.ToArray()).ToArray();
        using var source = new SparseByteSource(TwoHundredMiB, (0, head));
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics, entries: new() { [StreamObject] = 9 }).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.Contains(diagnostics, d => d.Code == "PLUME2019");
    }

    [Fact]
    public void Resolve_LengthReferringToAMissingObject_RecoversByScanningForEndstream()
    {
        var payload = Payload(10_000);
        var head = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< /Length 40 0 R >>\nstream\n")
            .Concat(payload).Concat("\nendstream\nendobj\n"u8.ToArray()).ToArray();
        using var source = new SparseByteSource(TwoHundredMiB, (0, head));
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics, entries: new() { [StreamObject] = 9 }).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME2060");
    }

    [Theory]
    [InlineData(TwoHundredMiB)] // the declared length runs past the end of the source
    [InlineData(ThreeGiB)] // ... or needs more than one array to read
    public void Resolve_ImpossibleDirectLength_RecoversByScanningForEndstream(long sourceLength)
    {
        var payload = Payload(10_000);
        var head = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< /Length 2147483000 >>\nstream\n")
            .Concat(payload).Concat("\nendstream\nendobj\n"u8.ToArray()).ToArray();
        using var source = new SparseByteSource(sourceLength, (0, head));
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics, entries: new() { [StreamObject] = 9 }).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.Contains(diagnostics, d => d.Code == "PLUME2019");
    }

    [Fact]
    public void Resolve_IndirectLengthWithADictionaryLargerThanTheFirstWindow_ReadsTheExactPayload()
    {
        var entries = string.Concat(Enumerable.Range(0, 600).Select(i => $"/K{i} /Value{i} "));
        var payload = Payload(100_000);
        var bytes = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< {entries}/Length 6 0 R >>\nstream\n")
            .Concat(payload).Concat("\nendstream\nendobj\n"u8.ToArray()).ToList();
        var lengthOffset = bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes($"6 0 obj\n{payload.Length}\nendobj\n"));
        using var source = new SparseByteSource(ThreeGiB, (0, bytes.ToArray()));
        var diagnostics = new DiagnosticCollection();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics, entries: new() { [StreamObject] = StreamOffset, [6] = lengthOffset }).Resolve(new IndirectReference(StreamObject, 0)));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.Empty(diagnostics);
        Assert.True(allocated < 2L * 1024 * 1024, $"Resolving a {payload.Length}-byte stream allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Resolve_IndirectLengthInTheLastBytesOfTheSource_UsesTheLengthNotTheScan(int bytesAfter)
    {
        // A payload that contains "endstream": only the declared length reads it whole.
        var payload = Encoding.ASCII.GetBytes("AAAA endstream BBBB " + new string('C', 200));
        var bytes = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< /Length 6 0 R >>\nstream\n")
            .Concat(payload).Concat("\nendstream\nendobj\n"u8.ToArray()).ToList();
        var lengthOffset = bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes($"6 0 obj\n{payload.Length}\nendobj\n"));
        bytes.AddRange(Enumerable.Repeat((byte)' ', bytesAfter));
        using var source = new SparseByteSource(bytes.Count, (0, bytes.ToArray()));
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics, PdfOptions.Default with { Strict = true }, new() { [StreamObject] = StreamOffset, [6] = lengthOffset }).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Resolve_WideGapBetweenEndstreamAndEndobj_ReportsNoMissingEndobj()
    {
        var payload = Payload(10_000);
        var bytes = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< /Length 6 0 R >>\nstream\n")
            .Concat(payload).Concat(Encoding.ASCII.GetBytes("\nendstream" + new string(' ', 1500) + "\nendobj\n")).ToList();
        var lengthOffset = bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes($"6 0 obj\n{payload.Length}\nendobj\n"));
        using var source = new SparseByteSource(TwoHundredMiB, (0, bytes.ToArray()));
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics, entries: new() { [StreamObject] = StreamOffset, [6] = lengthOffset }).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(payload, stream.RawBytes.ToArray());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Resolve_LengthsReferringToEachOther_ReportEachStreamOnce()
    {
        var first = Payload(10_000);
        var second = Payload(5_000);
        var bytes = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< /Length 6 0 R >>\nstream\n")
            .Concat(first).Concat("\nendstream\nendobj\n"u8.ToArray()).ToList();
        var secondOffset = bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes($"6 0 obj\n<< /Length {StreamObject} 0 R >>\nstream\n"));
        bytes.AddRange(second);
        bytes.AddRange("\nendstream\nendobj\n"u8.ToArray());
        using var source = new SparseByteSource(TwoHundredMiB, (0, bytes.ToArray()));
        var diagnostics = new DiagnosticCollection();

        var stream = Assert.IsType<PdfStream>(Resolver(source, diagnostics, entries: new() { [StreamObject] = StreamOffset, [6] = secondOffset }).Resolve(new IndirectReference(StreamObject, 0)));

        Assert.Equal(first, stream.RawBytes.ToArray());
        Assert.Equal(2, diagnostics.Count(d => d.Code == "PLUME2019"));
    }

    [Theory]
    [InlineData(4096L, 3L * 1024 * 1024 * 1024, 16384)]
    [InlineData(4096L, 10_000L, 10_000)]
    [InlineData(1024L * 1024 * 1024, 3L * 1024 * 1024 * 1024, -1)]
    [InlineData(1024L * 1024 * 1024, 1500L * 1024 * 1024, 1500 * 1024 * 1024)]
    public void GrowProbeWindow_NeverOverflowsAndRefusesPastOneArray(long window, long remaining, int expected)
    {
        if (expected < 0)
        {
            var ex = Assert.Throws<PlumePdfException>(() => ObjectResolver.GrowProbeWindow((int)window, remaining));
            Assert.Equal("PLUME1005", ex.Code);
        }
        else
        {
            Assert.Equal(expected, ObjectResolver.GrowProbeWindow((int)window, remaining));
        }
    }

    private static byte[] Payload(int length)
    {
        var payload = new byte[length];
        new Random(34).NextBytes(payload);

        // Keep the payload free of an accidental "endstream" so the recovery-scan
        // assertions measure only the length logic.
        return payload;
    }

    private static ObjectResolver Resolver(ByteSource source, DiagnosticCollection? diagnostics, PdfOptions? options = null, Dictionary<int, long>? entries = null)
    {
        entries ??= new() { [StreamObject] = StreamOffset, [6] = ((SparseByteSource)source).Tag };
        var table = new CrossReferenceTable(new PdfDictionary(), entries.ToDictionary(e => e.Key, e => CrossReferenceEntry.InFile(e.Value, 0)));
        return new ObjectResolver(source, table, options ?? PdfOptions.Default, diagnostics);
    }

    /// <summary>
    /// Object 5, a stream of <paramref name="payloadLength"/> random bytes, right after the
    /// header; object 6 — its <c>/Length</c> when <paramref name="indirectLength"/> — after it;
    /// the rest of the source reads as spaces. The source's <see cref="SparseByteSource.Tag"/> carries object 6's offset.
    /// </summary>
    private static SparseByteSource StreamAtStart(long length, int payloadLength, bool indirectLength, out byte[] payload, int? lengthObjectValue = null)
    {
        payload = Payload(payloadLength);
        var lengthEntry = indirectLength ? "6 0 R" : payloadLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bytes = Encoding.ASCII.GetBytes($"%PDF-1.7\n{StreamObject} 0 obj\n<< /Filter /DCTDecode /Length {lengthEntry} >>\nstream\n")
            .Concat(payload).Concat("\nendstream\nendobj\n"u8.ToArray()).ToList();
        var lengthOffset = bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes($"6 0 obj\n{lengthObjectValue ?? payloadLength}\nendobj\n"));
        return new SparseByteSource(length, (0, bytes.ToArray())) { Tag = lengthOffset };
    }
}
