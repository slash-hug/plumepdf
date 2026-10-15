using System.Text;
using PlumePdf.IO;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// <see cref="IObjectSource.ReadPayloadOnce"/>: a pass over many stream payloads (hashing
/// candidates for deduplication, say) reads each one for that call and keeps nothing — not
/// the stream, not its indirect <c>/Length</c>, and not a stream whose declared length was
/// wrong and had to be recovered — so the document's object cache never grows with payloads.
/// </summary>
public class ReadPayloadOnceTests
{
    private const int PageCount = 50;
    private const int ImageBytes = 64 * 1024;
    private const int WrongLengthPage = 7;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadingEveryImagePayload_LeavesTheObjectCacheUnchanged(bool indirectLength)
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength, WrongLengthPage));
        var source = (IObjectSource)document.Objects;
        var before = document.Objects.CachedObjectCount;

        for (var page = 0; page < PageCount; page++)
        {
            var stream = source.ReadPayloadOnce(new IndirectReference(ImageDocumentFixture.ImageObjectNumber(page), 0));

            Assert.NotNull(stream);
            Assert.Equal(ImageBytes, stream.RawBytes.Length);
            Assert.Equal(page, BitConverter.ToInt32(stream.RawBytes.Span[..4]));
        }

        Assert.Equal(before, document.Objects.CachedObjectCount);
    }

    [Fact]
    public void RecoveredLengthStream_ReportsItsDeviationOnce()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false, WrongLengthPage));
        var source = (IObjectSource)document.Objects;
        var image = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(WrongLengthPage), 0);

        source.ReadPayloadOnce(image);
        source.ReadPayloadOnce(image);

        Assert.Single(document.Diagnostics, d => d.Code == "PLUME2019");
    }

    [Fact]
    public void AlreadyCachedStream_IsReturnedFromTheCache()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(2, ImageBytes, indirectLength: true));
        var image = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(1), 0);
        var cached = document.Objects[image];

        Assert.Same(cached, ((IObjectSource)document.Objects).ReadPayloadOnce(image));
    }

    [Fact]
    public void NonStream_ReadsAsNull()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(2, ImageBytes, indirectLength: true));

        Assert.Null(((IObjectSource)document.Objects).ReadPayloadOnce(document.Pages[0].Reference));
    }

    [Fact]
    public void StreamPast2GiB_WithAnIndirectLength_ReadsExactlyAndCachesNothing()
    {
        var (source, offsets, payload) = StreamPast2GiB(lengthValue: null);
        using (source)
        {
            var resolver = Resolver(source, offsets, PdfOptions.Default);

            var stream = ((IObjectSource)resolver).ReadPayloadOnce(new IndirectReference(5, 0));

            Assert.NotNull(stream);
            Assert.Equal(payload, stream.RawBytes.ToArray());
            Assert.Equal(0, resolver.CachedObjectCount);
        }
    }

    [Fact]
    public void StreamPast2GiB_WithAnUnusableLengthUnderStrict_RefusesWithACode()
    {
        // A /Length too large for any array, on a stream that never reaches 'endstream'
        // before the end of a source over 2 GiB: a coded refusal, never an overflow or an
        // attempt to allocate the length.
        var (source, offsets, _) = StreamPast2GiB(lengthValue: 3_000_000_000L, truncated: true);
        using (source)
        {
            var resolver = Resolver(source, offsets, PdfOptions.Default with { Strict = true });

            var ex = Assert.Throws<PlumePdfException>(() => ((IObjectSource)resolver).ReadPayloadOnce(new IndirectReference(5, 0)));
            Assert.StartsWith("PLUME", ex.Code, StringComparison.Ordinal);
            Assert.Equal(0, resolver.CachedObjectCount);
        }
    }

    // Object 5, a stream whose /Length is object 6, starts 2.5 GiB into the source; object 6
    // follows it. 'truncated' cuts the source off mid-payload, before 'endstream'.
    private static (SparseByteSource Source, Dictionary<int, long> Offsets, byte[] Payload) StreamPast2GiB(long? lengthValue, bool truncated = false)
    {
        const long streamOffset = (5L * 1024 * 1024 * 1024) / 2;
        var payload = new byte[100_000];
        new Random(42).NextBytes(payload);
        var bytes = Encoding.ASCII.GetBytes("5 0 obj\n<< /Length 6 0 R >>\nstream\n").Concat(payload).ToList();
        if (truncated)
        {
            bytes.RemoveRange(bytes.Count - 1000, 1000);
            var tail = bytes.Count;
            bytes.AddRange(Encoding.ASCII.GetBytes($"\n6 0 obj\n{lengthValue}\nendobj\n"));
            return (new SparseByteSource(streamOffset + bytes.Count, (streamOffset, bytes.ToArray())), new() { [5] = streamOffset, [6] = streamOffset + tail + 1 }, payload);
        }

        bytes.AddRange("\nendstream\nendobj\n"u8.ToArray());
        var lengthOffset = streamOffset + bytes.Count;
        bytes.AddRange(Encoding.ASCII.GetBytes($"6 0 obj\n{lengthValue ?? payload.Length}\nendobj\n"));
        return (new SparseByteSource(streamOffset + bytes.Count + 64, (streamOffset, bytes.ToArray())), new() { [5] = streamOffset, [6] = lengthOffset }, payload);
    }

    private static ObjectResolver Resolver(ByteSource source, Dictionary<int, long> offsets, PdfOptions options)
    {
        var table = new CrossReferenceTable(new PdfDictionary(), offsets.ToDictionary(e => e.Key, e => CrossReferenceEntry.InFile(e.Value, 0)));
        return new ObjectResolver(source, table, options, new DiagnosticCollection());
    }
}
