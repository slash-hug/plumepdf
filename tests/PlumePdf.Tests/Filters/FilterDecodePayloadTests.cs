using System.Runtime.InteropServices;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// Decoding a stream hands its encoded payload to the first filter as-is, rather than copying
/// it first, so a large image stream isn't held twice while it decodes.
/// </summary>
public sealed class FilterDecodePayloadTests
{
    [Fact]
    public void GetDecodedBytes_PassesTheStreamsOwnPayloadToTheFirstFilter()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var capture = new CapturingFilter();
        var registry = PdfFilterRegistry.CreateDefault();
        registry.Register("Capture", capture);
        var stream = new PdfStream(FilterDictionary("Capture"), payload);

        stream.GetDecodedBytes(registry);

        Assert.True(MemoryMarshal.TryGetArray(capture.Received, out var segment));
        Assert.Same(payload, segment.Array);
    }

    [Fact]
    public void GetDecodedBytes_WithNoFilter_ReturnsACopyTheCallerCanChange()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var stream = new PdfStream(new PdfDictionary(), payload);

        var decoded = stream.GetDecodedBytes(PdfFilterRegistry.Default);
        decoded[0] = 9;

        Assert.Equal(1, payload[0]);
        Assert.Equal([1, 2, 3, 4], stream.RawBytes.ToArray());
    }

    [Fact]
    public void GetDecodedBytes_FilterThatReturnsItsInput_StillLeavesThePayloadUnshared()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var registry = PdfFilterRegistry.CreateDefault();
        registry.Register("PassThrough", new PassThroughFilter());
        var stream = new PdfStream(FilterDictionary("PassThrough"), payload);

        var decoded = stream.GetDecodedBytes(registry);

        Assert.NotSame(payload, decoded);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void GetDecodedBytes_SlicedPayload_NextStageSeesOnlyTheSlice()
    {
        // An inline image's stream is a slice of its page's content bytes. A filter that hands
        // back the whole backing array must not leak the bytes around the slice into the next stage.
        var content = "44414243>"u8.ToArray();
        var registry = PdfFilterRegistry.CreateDefault();
        registry.Register("PassThrough", new PassThroughFilter());
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Filter, new PdfArray([PdfName.Get("PassThrough"), PdfName.Get("ASCIIHexDecode")]));
        var stream = new PdfStream(dictionary, content.AsMemory(2));

        Assert.Equal("ABC"u8.ToArray(), stream.GetDecodedBytes(registry));
    }

    [Fact]
    public void GetDecodedBytes_ChainedFilters_StillDecodeInOrder()
    {
        var stream = new PdfStream(FilterArray("ASCIIHexDecode", "RunLengthDecode"), "0241424380>"u8.ToArray());

        Assert.Equal("ABC"u8.ToArray(), stream.GetDecodedBytes(PdfFilterRegistry.Default));
    }

    private static PdfDictionary FilterDictionary(string name)
    {
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Filter, PdfName.Get(name));
        return dictionary;
    }

    private static PdfDictionary FilterArray(params string[] names)
    {
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Filter, new PdfArray([.. names.Select(static n => (PdfObject)PdfName.Get(n))]));
        return dictionary;
    }

    /// <summary>
    /// Returns the array behind what it was given, ignoring any slice bounds: a careless
    /// caller-registered filter that worked when every filter was handed an exact-length copy.
    /// </summary>
    private sealed class PassThroughFilter : IPdfFilter
    {
        public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
            MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is { } array ? array : data.ToArray();
    }

    private sealed class CapturingFilter : IPdfFilter
    {
        public ReadOnlyMemory<byte> Received { get; private set; }

        public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
        {
            Received = data;
            return data.ToArray();
        }
    }
}
