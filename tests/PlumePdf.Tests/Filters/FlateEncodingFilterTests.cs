using System.Text;
using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// The write-side <c>FlateDecode</c> seam (<see cref="IPdfEncodingFilter"/>,
/// <see cref="FlateEncodingFilter"/>, <see cref="PdfFilterRegistry.TryGetEncoder"/>) —
/// encode round-trips through the existing decode path, and encoding is deterministic.
/// </summary>
public class FlateEncodingFilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("The quick brown fox jumps over the lazy dog.")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Encode_RoundTripsThroughRegistryDecode(string text)
    {
        var original = Encoding.UTF8.GetBytes(text);
        var filter = new FlateEncodingFilter();

        var encoded = filter.Encode(original, PdfOptions.Default);

        var streamDictionary = new PdfDictionary();
        streamDictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        var decoded = PdfFilterRegistry.Default.Decode(streamDictionary, encoded, PdfOptions.Default);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Encode_BinaryPayload_RoundTrips()
    {
        var original = new byte[4096];
        new Random(42).NextBytes(original);
        var filter = new FlateEncodingFilter();

        var encoded = filter.Encode(original, PdfOptions.Default);

        var streamDictionary = new PdfDictionary();
        streamDictionary.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        var decoded = PdfFilterRegistry.Default.Decode(streamDictionary, encoded, PdfOptions.Default);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Encode_SameInput_ProducesIdenticalBytes()
    {
        var original = Encoding.UTF8.GetBytes("Determinism matters for byte-identical output under PdfOptions.Deterministic.");
        var filter = new FlateEncodingFilter();

        var first = filter.Encode(original, PdfOptions.Default);
        var second = filter.Encode(original, PdfOptions.Default);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Encode_SameInput_ProducesIdenticalBytes_AcrossSeparateFilterInstances()
    {
        var original = Encoding.UTF8.GetBytes("Fixed compression level, not adapted to input, on every call.");

        var first = new FlateEncodingFilter().Encode(original, PdfOptions.Default);
        var second = new FlateEncodingFilter().Encode(original, PdfOptions.Default);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Default_Registry_HasFlateEncoderRegisteredUnderBothNames()
    {
        Assert.True(PdfFilterRegistry.Default.TryGetEncoder("FlateDecode", out var byFullName));
        Assert.NotNull(byFullName);

        Assert.True(PdfFilterRegistry.Default.TryGetEncoder("Fl", out var byAbbreviation));
        Assert.NotNull(byAbbreviation);
    }

    [Fact]
    public void TryGetEncoder_UnregisteredFilterName_ReturnsFalse()
    {
        var registry = new PdfFilterRegistry();

        var found = registry.TryGetEncoder("DCTDecode", out var encoder);

        Assert.False(found);
        Assert.Null(encoder);
    }

    [Fact]
    public void RegisterEncoder_ThenTryGetEncoder_FindsIt()
    {
        var registry = new PdfFilterRegistry();
        var encoder = new FlateEncodingFilter();

        registry.RegisterEncoder("FlateDecode", encoder);

        Assert.True(registry.TryGetEncoder("FlateDecode", out var found));
        Assert.Same(encoder, found);
    }
}
