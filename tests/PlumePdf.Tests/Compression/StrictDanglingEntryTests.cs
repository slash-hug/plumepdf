using PlumePdf.Documents.Compression;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// An image dictionary entry that refers to an object the document can't produce: a Strict
/// document's registry throws when the entry is resolved. Compression treats that as this one
/// image failing to decode, never as a failure of the whole run.
/// </summary>
public class StrictDanglingEntryTests
{
    private const int Dangling = 99;

    [Theory]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("ColorSpace")]
    [InlineData("BitsPerComponent")]
    [InlineData("Filter")]
    [InlineData("SMask")]
    [InlineData("Mask")]
    public void TryDecode_ReportsADecodeFailure(string key)
    {
        var image = ImageWithDanglingEntry(key);

        var ok = NativeImageSamples.TryDecode(image, StrictRegistry(), PdfOptions.Default with { Strict = true }, out var samples, out var skip, out var detail);

        Assert.False(ok);
        Assert.Null(samples);
        Assert.Equal(PlannedSkip.DecodeFailed, skip);
        Assert.Contains("PLUME2060", detail);
    }

    [Theory]
    [InlineData("Width")]
    [InlineData("Height")]
    [InlineData("ColorSpace")]
    [InlineData("Filter")]
    [InlineData("SMask")]
    [InlineData("Mask")]
    public void Reencode_LeavesTheImageAloneAsASkip(string key)
    {
        var image = ImageWithDanglingEntry(key);

        var result = ImageReencoder.Reencode(image, StrictRegistry(), new ImageReencodeRequest(4, 4, JpegQuality: 70), PdfOptions.Default with { Strict = true });

        Assert.Equal(ImageReencodeOutcome.Skipped, result.Outcome);
        Assert.Equal(PlannedSkip.DecodeFailed, result.Skip);
        Assert.Same(image, result.Stream);
    }

    private static PdfStream ImageWithDanglingEntry(string key)
    {
        var image = FlateImage(Samples(8, 8, 3), 8, 8, 3);
        image.Dictionary.Set(PdfName.Get(key), new PdfReference(new IndirectReference(Dangling, 0)));
        return image;
    }

    private static ObjectRegistry StrictRegistry() => new(new StrictSource(), nextObjectNumber: 100);

    // Resolves nothing: every lookup is the Strict refusal an opened document gives for an
    // object its cross-reference data doesn't hold.
    private sealed class StrictSource : IObjectSource
    {
        public PdfDictionary Trailer { get; } = new();

        public PdfObject Resolve(IndirectReference reference) =>
            throw new PlumePdfException("PLUME2060", $"Object {reference.Number} is not in the cross-reference table.");
    }
}
