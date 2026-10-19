using PlumePdf.Documents.Compression;
using PlumePdf.Filters;
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

    // A Flate image re-encoded as JPEG at full size: the decoded samples (1x), then the
    // encoder's planes (0.5x at 4:2:0) once the samples are released. Measured 1.55x; it was
    // 4.2x while the encoder built two full-resolution float chroma planes (2.7x) and held the
    // samples through entropy coding.
    [Fact]
    public void FullSizeRgbJpegReencode_PeaksUnderTwiceTheDecodedImage()
    {
        var image = BuildImage(jpeg: false);
        var objects = Registry();
        var request = new ImageReencodeRequest(Size, Size, JpegQuality: 75);

        ImageReencodeResult? result = null;
        var peak = PeakManagedBytesAbove(() => result = ImageReencoder.Reencode(image, objects, request, PdfOptions.Default));

        Assert.Equal(ImageReencodeOutcome.Reencoded, result!.Outcome);
        Assert.True(peak < 2 * DecodedBytes, $"Re-encoding a {Size}x{Size} RGB image ({DecodedBytes} decoded bytes) peaked {peak} bytes above the starting heap ({(double)peak / DecodedBytes:F2}x).");
    }

    // A 4:4:4 JPEG downsampled and kept as JPEG: the decoder's coefficients (2 bytes a sample)
    // are released a component at a time as its planes are built, so they, the planes and the
    // interleaved output are never all live. Measured 2.8x; it was 4.1x.
    [Fact]
    public void FullResolutionChromaJpegDownsample_PeaksUnderThreeAndAHalfTimesTheDecodedImage()
    {
        var image = BuildImage(jpeg: true);
        var objects = Registry();
        var request = new ImageReencodeRequest(Size / 2, Size / 2, JpegQuality: null);

        ImageReencodeResult? result = null;
        var peak = PeakManagedBytesAbove(() => result = ImageReencoder.Reencode(image, objects, request, PdfOptions.Default));

        Assert.Equal(ImageReencodeOutcome.Downsampled, result!.Outcome);
        Assert.True(peak < 3.5 * DecodedBytes, $"Downsampling a {Size}x{Size} 4:4:4 JPEG ({DecodedBytes} decoded bytes) peaked {peak} bytes above the starting heap ({(double)peak / DecodedBytes:F2}x).");
    }

    private static PdfStream BuildImage(bool jpeg)
    {
        // Built in its own frame so the full-size sample array is garbage before measuring.
        var samples = Samples(Size, Size, Channels);
        if (!jpeg)
        {
            return FlateImage(samples, Size, Size, Channels);
        }

        var dictionary = ImageDictionary(Size, Size, ColorSpaceFor(Channels));
        dictionary.Set(PdfName.Filter, PdfName.Get("DCTDecode"));
        return new PdfStream(dictionary, JpegEncoder.Encode(samples, Size, Size, Channels, quality: 90, subsampleChroma: false));
    }

    // The live managed heap's high-water mark across 'action', above the heap it started from.
    // Each sample forces a full collection, so it measures what the re-encode actually holds
    // rather than garbage the GC hasn't got round to: for the same allocations, the
    // garbage-inclusive peak measured 7.1x on net8.0 and 5.2x on net10.0 before the encoder's
    // planes shrank (the difference was the decode's discarded copy of a 100 MB payload), while
    // the live peak was 5.0x on both.
    // Garbage is reclaimed under memory pressure; the live working set is what bounds a run.
    //
    // A first Flate decode in the process also fills the shared ArrayPool with the inflater's
    // chunks, which the pool keeps; that one-time cost (up to the size of the image) is paid
    // before the baseline so it isn't counted against whichever test runs first.
    private static long PeakManagedBytesAbove(Action action)
    {
        FlateImage(new byte[DecodedBytes], Size, Size, Channels).GetDecodedBytes(PdfFilterRegistry.Default);
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
