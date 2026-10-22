using PlumePdf.Filters;
using PlumePdf.Tests.Compression;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpeg;

/// <summary>
/// The decoder reads a whole-array input in place rather than copying it first, so a large
/// JPEG isn't held twice while it decodes; a slice of a larger array is still copied, so the
/// decoder never sees the bytes around it.
/// </summary>
public class JpegDecoderInputTests
{
    private const int Width = 640;
    private const int Height = 480;

    [Fact]
    public void Decode_WholeArrayInput_IsNotCopied()
    {
        var jpeg = Encode();
        var sliced = new byte[jpeg.Length + 1];
        jpeg.CopyTo(sliced, 1);

        var wholeArray = LeastAllocatedBy(() => JpegDecoder.Decode(jpeg, PdfOptions.Default, null, null));
        var slice = LeastAllocatedBy(() => JpegDecoder.Decode(sliced.AsMemory(1), PdfOptions.Default, null, null));

        // Only the slice pays for a copy of the input. The runtime can allocate a few KB of its
        // own on this thread during a decode (tier-up, for one), so the bound is half the input:
        // well clear of that noise, and nowhere near met if the whole-array input were copied too.
        Assert.True(slice - wholeArray >= jpeg.Length / 2, $"A {jpeg.Length}-byte whole-array input allocated {wholeArray} bytes to decode, a slice of the same bytes {slice}.");
    }

    [Fact]
    public void Decode_SliceOfALargerArray_DecodesOnlyTheSlice()
    {
        var jpeg = Encode();
        var padded = new byte[jpeg.Length + 64];
        padded.AsSpan().Fill(0xFF);
        jpeg.CopyTo(padded, 32);

        var expected = JpegDecoder.Decode(jpeg, PdfOptions.Default, null, null).Pixels;
        var diagnostics = new DiagnosticCollection();
        var actual = JpegDecoder.Decode(padded.AsMemory(32, jpeg.Length), PdfOptions.Default, diagnostics, null).Pixels;

        Assert.Equal(expected, actual);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Decode_LeavesTheInputUnchanged()
    {
        var jpeg = Encode();
        var before = jpeg.ToArray();

        JpegDecoder.Decode(jpeg, PdfOptions.Default, null, null);

        Assert.Equal(before, jpeg);
    }

    private static byte[] Encode() =>
        JpegEncoder.Encode(CompressionTestImages.Samples(Width, Height, 3), Width, Height, 3, quality: 90);

    // The least of several runs, after a warm-up, so a one-off runtime allocation doesn't count.
    private static long LeastAllocatedBy(Action action)
    {
        action();
        var least = long.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return least;
    }
}
