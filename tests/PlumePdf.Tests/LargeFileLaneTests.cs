using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// The opt-in large-file lane: a generated document over 2 GiB on disk, opened from its path
/// (memory-mapped), saved with bounded memory, every image extracted, and the saved copy read
/// back. Writes about 4.5 GB to the temp directory and needs several GB of memory, so it
/// self-skips unless <c>PLUMEPDF_LARGE_FILE_TESTS=1</c>. Run it on its own
/// (<c>--filter LargeFileLaneTests</c>): the memory assertions read the whole process's heap.
/// </summary>
public class LargeFileLaneTests
{
    private const int PageCount = 280;
    private const int ImageBytes = 8 * 1024 * 1024; // 280 x 8 MiB ~ 2.2 GiB

    // Save holds one stream at a time, so its heap peaks at a few payloads plus garbage not
    // yet collected, not the 2.2 GiB document every stream it wrote used to be kept in.
    private const long MaxSavePeakBytes = 1L << 30;
    private const long MaxRetainedAfterSaveBytes = 256L << 20;

    private static bool Enabled => Environment.GetEnvironmentVariable("PLUMEPDF_LARGE_FILE_TESTS") == "1";

    [Fact]
    public void IndirectLengthImagesPast2GiB_SaveWithBoundedMemoryThenExtractAndReadBack()
    {
        if (!Enabled)
        {
            return;
        }

        var input = Path.Combine(Path.GetTempPath(), $"plumepdf-large-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"plumepdf-large-saved-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            {
                ImageDocumentFixture.Write(file, PageCount, ImageBytes, indirectLength: true);
            }

            Assert.True(new FileInfo(input).Length > int.MaxValue);

            using (var document = PdfDocument.Open(input))
            {
                var peak = PeakManagedBytes(() => document.Save(output));
                var retained = GC.GetTotalMemory(forceFullCollection: true);
                Assert.True(peak < MaxSavePeakBytes, $"Saving the {PageCount}-page document peaked at {peak} bytes of managed heap.");
                Assert.True(retained < MaxRetainedAfterSaveBytes, $"The open document retained {retained} bytes after Save.");

                for (var i = 0; i < PageCount; i++)
                {
                    ImageDocumentFixture.AssertImageIsPage(document.Pages[i].ExtractImages(), i, ImageBytes);
                }
            }

            using var saved = PdfDocument.Open(output);
            Assert.Equal(PageCount, saved.Pages.Count);
            ImageDocumentFixture.AssertImageIsPage(saved.Pages[0].ExtractImages(), 0, ImageBytes);
            ImageDocumentFixture.AssertImageIsPage(saved.Pages[PageCount - 1].ExtractImages(), PageCount - 1, ImageBytes);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    // The managed heap's high-water mark across 'action', sampled every millisecond. Resident
    // memory would count the memory-mapped source's pages, which Save reads through once and
    // the OS reclaims; the heap is what the document and the save actually hold.
    private static long PeakManagedBytes(Action action)
    {
        GC.Collect();
        long peak = GC.GetTotalMemory(forceFullCollection: true);
        using var done = new ManualResetEventSlim();
        var sampler = new Thread(() =>
        {
            while (!done.Wait(1))
            {
                var now = GC.GetTotalMemory(forceFullCollection: false);
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

        return Volatile.Read(ref peak);
    }
}
