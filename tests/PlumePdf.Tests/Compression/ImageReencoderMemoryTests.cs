using PlumePdf.Documents.Compression;
using PlumePdf.Tests.TestSupport;
using Xunit;
using static PlumePdf.Tests.Compression.CompressionTestImages;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// The re-encoder's working set for one large image: decode, encode and the encoded result
/// together stay within a small multiple of the decoded image, so a document's peak memory is
/// bounded by its largest image rather than by the document. Runs in the non-parallel
/// collection because it reads the whole process's managed heap.
/// </summary>
[Collection(TimingSensitiveCollection.Name)]
public class ImageReencoderMemoryTests
{
    private const int Size = 6000;
    private const int Channels = 3;
    private const long DecodedBytes = (long)Size * Size * Channels;

    [Fact]
    public void FullSizeRgbJpegReencode_PeaksUnderSixTimesTheDecodedImage()
    {
        var image = BuildImage();
        var objects = Registry();
        var request = new ImageReencodeRequest(Size, Size, JpegQuality: 75);

        ImageReencodeResult? result = null;
        var peak = PeakManagedBytesAbove(() => result = ImageReencoder.Reencode(image, objects, request, PdfOptions.Default));

        Assert.Equal(ImageReencodeOutcome.Reencoded, result!.Outcome);
        Assert.True(peak < 6 * DecodedBytes, $"Re-encoding a {Size}x{Size} RGB image ({DecodedBytes} decoded bytes) peaked {peak} bytes above the starting heap ({(double)peak / DecodedBytes:F2}x).");
    }

    private static PdfStream BuildImage()
    {
        // Built in its own frame so the full-size sample array is garbage before measuring.
        var samples = Samples(Size, Size, Channels);
        return FlateImage(samples, Size, Size, Channels);
    }

    // The live managed heap's high-water mark across 'action', above the heap it started from.
    // Each sample forces a full collection, so it measures what the re-encode actually holds
    // rather than garbage the GC hasn't got round to: for the same allocations, the
    // garbage-inclusive peak measured 7.1x on net8.0 and 5.2x on net10.0 (the difference is the
    // decode's discarded copy of a 100 MB payload), while the live peak is 5.0x on both.
    // Garbage is reclaimed under memory pressure; the live working set is what bounds a run.
    private static long PeakManagedBytesAbove(Action action)
    {
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        using var done = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            while (!done.Wait(1))
            {
                var now = GC.GetTotalMemory(forceFullCollection: true);
                if (now > Volatile.Read(ref peak))
                {
                    Volatile.Write(ref peak, now);
                }
            }
        })
        { IsBackground = true };
        sampler.Start();
        try
        {
            action();
        }
        finally
        {
            done.Set();
            sampler.Join();
        }

        return Math.Max(Volatile.Read(ref peak), GC.GetTotalMemory(forceFullCollection: true)) - baseline;
    }
}
