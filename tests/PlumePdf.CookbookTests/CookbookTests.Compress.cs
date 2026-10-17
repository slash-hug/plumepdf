using System.Text;
using PlumePdf.Documents.PdfA;
using PlumePdf.Elements;
using VerifyXunit;
using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// Cookbook recipe backing tests for <c>docs/cookbook/compress.md</c>: a lossless-only call,
/// then the image levers with the result read to tune them, the async door with cancellation,
/// progress reporting through a synchronous sink, streams in and out (a non-seekable download
/// to an upload), and what a compressed tagged PDF/A source keeps. The sources are composed here — a
/// 900-pixel noisy image drawn two inches wide (450 DPI) — so the recipes have no external
/// fixture. Byte counts vary with the zlib build, so the snapshots record comparisons, not sizes.
/// </summary>
public partial class CookbookTests
{
    [Fact]
    public Task Compress()
    {
        var report = new StringBuilder();
        BuildScanLikeSource("output/scan.pdf");

        // begin-snippet: compress-lossless
        // No options: lossless only. Unfiltered streams are Flated, objects packed, identical
        // streams written once; no image's samples change.
        var lossless = Pdf.Compress("output/scan.pdf", "output/scan-lossless.pdf");

        report.AppendLine($"Lossless smaller: {lossless.OutputBytes < lossless.InputBytes}");
        report.AppendLine($"Lossless image actions: {string.Join(", ", lossless.Images.Select(static i => i.Action))}");
        report.AppendLine($"No-lever hint (PLUME6090): {lossless.Diagnostics.Any(static d => d.Code == "PLUME6090")}");
        // end-snippet

        // begin-snippet: compress-images
        // Opt in to the lossy levers: downsample images drawn above 1.5 x 200 DPI to 200 DPI,
        // and re-encode them as JPEG at quality 70. A new encoding that isn't smaller is
        // dropped (KeptOriginal), and every image's fate is in result.Images.
        var result = Pdf.Compress("output/scan.pdf", "output/scan-small.pdf", new PdfCompressOptions
        {
            Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
        });

        report.AppendLine($"Saved bytes: {result.SavedBytes > 0}");
        foreach (var group in result.Images.GroupBy(static i => (i.Action, i.SkipReason)))
        {
            report.AppendLine($"{group.Key.Action} {group.Key.SkipReason}: {group.Count()}");
        }

        // EncodingBefore/EncodingAfter say what each image was stored as and what it became:
        // a JPEG source only shrinks further with a lower JpegQuality or MaxDpi.
        report.AppendLine($"Already JPEG: {result.Images.Count(static i => i.EncodingBefore == ImageEncoding.Jpeg)}");
        var image = result.Images[0];
        report.AppendLine($"{image.EncodingBefore} -> {image.EncodingAfter}, " +
            $"{image.WidthBefore}x{image.HeightBefore} at {image.EffectiveDpiBefore:F0} DPI -> " +
            $"{image.WidthAfter}x{image.HeightAfter} at {image.EffectiveDpiAfter:F0} DPI");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    [Fact]
    public async Task CompressAsync()
    {
        var report = new StringBuilder();
        BuildScanLikeSource("output/scan-for-async.pdf");
        File.Delete("output/scan-cancelled.pdf");

        // begin-snippet: compress-async
        // CompressAsync runs on the thread pool and checks the token between pages while
        // planning and between streams while writing, so a deadline stops a long run.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var result = await Pdf.CompressAsync("output/scan-for-async.pdf", "output/scan-async.pdf", new PdfCompressOptions
        {
            Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
        }, cancellationToken: deadline.Token);
        report.AppendLine($"Async saved bytes: {result.SavedBytes > 0}");

        // A cancelled call throws OperationCanceledException. The output is written to a
        // temporary file and moved into place only on success, so nothing is left behind.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await Pdf.CompressAsync("output/scan-for-async.pdf", "output/scan-cancelled.pdf", cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            report.AppendLine($"Cancelled; output file exists: {File.Exists("output/scan-cancelled.pdf")}");
        }
        // end-snippet

        await Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task CompressProgress()
    {
        var report = new StringBuilder();
        BuildScanLikeSource("output/scan-for-progress.pdf");

        // begin-snippet: compress-progress
        // Progress reports each stage as it runs: pages while planning, images while writing
        // (and bytes, with no total, while a non-seekable input stream is spooled). Each stage
        // starts at 0 and, when it has a total, ends at it. Reporting never changes the output.
        var log = new ProgressLog();
        var result = Pdf.Compress("output/scan-for-progress.pdf", "output/scan-progress.pdf", new PdfCompressOptions
        {
            Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
            Progress = log,
        });
        // end-snippet

        report.AppendLine($"Saved bytes: {result.SavedBytes > 0}");
        foreach (var line in log.Lines)
        {
            report.AppendLine(line);
        }

        return Verifier.Verify(report.ToString());
    }

    // begin-snippet: compress-progress-sink
    // Your own IProgress is called synchronously, on the thread doing the work: keep it fast
    // and thread-safe. Progress<T> instead posts each report to the context it was created on
    // (a UI thread), so it suits a UI. An exception thrown here ends the call: to stop a run,
    // cancel its token instead.
    private sealed class ProgressLog : IProgress<CompressionProgress>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public void Report(CompressionProgress value)
        {
            var line = value.Total is long total
                ? $"{value.Stage}: {value.Completed} of {total}"
                : $"{value.Stage}: {value.Completed:N0} bytes";
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
    // end-snippet

    [Fact]
    public async Task CompressStream()
    {
        var report = new StringBuilder();
        BuildScanLikeSource("output/scan-for-stream.pdf");

        // begin-snippet: compress-stream
        // Streams in, streams out: a download and an upload, neither of which needs to be
        // seekable. A non-seekable input is copied to a temporary file (never into memory),
        // deleted when the call ends; the output is written from its current position.
        using var download = OpenDownload("output/scan-for-stream.pdf");
        using var upload = new MemoryStream();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            var result = await Pdf.CompressAsync(download, upload, new PdfCompressOptions
            {
                Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
            }, cancellationToken: deadline.Token);

            report.AppendLine($"Saved bytes: {result.SavedBytes > 0}");
            report.AppendLine($"OutputBytes is what was written: {result.OutputBytes == upload.Length}");
            report.AppendLine($"Both streams left open: {download.CanRead && upload.CanWrite}");
        }
        catch (Exception e) when (e is PlumePdfException or OperationCanceledException or IOException)
        {
            // Refusals come before the first byte, but a failure or cancellation while
            // writing can leave a partial PDF in the output: discard it (abort the upload).
            // PlumePDF's own file failures are coded (PLUME1008: the temporary copy couldn't
            // be made); an IOException here comes from your own streams.
            upload.SetLength(0);
            throw;
        }
        // end-snippet

        using var reopened = PdfDocument.Open(upload.ToArray());
        report.AppendLine($"Output reopens: {reopened.Pages.Count} page(s)");
        await Verifier.Verify(report.ToString());
    }

    [Fact]
    public Task CompressEstimate()
    {
        var report = new StringBuilder();
        BuildMultiPageScan("output/scans.pdf", pages: 8);

        // begin-snippet: compress-estimate
        // A dry run: the plan Compress would make, a few images trial-encoded exactly as
        // Compress would write them, the rest extrapolated, and nothing written. It takes
        // about as long as Compress's planning pass, where the write can take minutes.
        var levers = new PdfCompressOptions { Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 } };
        var estimate = Pdf.EstimateCompression("output/scans.pdf", levers);

        report.AppendLine($"Images: {estimate.Images.Count}, trial-encoded: {estimate.SampledImages}");
        report.AppendLine($"Estimated saving: {estimate.EstimatedSavedBytes > 0}");
        foreach (var group in estimate.Images.GroupBy(static i => (i.PlannedAction, i.Sampled)))
        {
            report.AppendLine($"{group.Key.PlannedAction}, sampled {group.Key.Sampled}: {group.Count()}");
        }

        // Worth it? Then compress for real: the result says what actually happened.
        if (estimate.EstimatedOutputBytes < estimate.InputBytes / 2)
        {
            var result = Pdf.Compress("output/scans.pdf", "output/scans-small.pdf", levers);
            var error = (double)(estimate.EstimatedOutputBytes - result.OutputBytes) / result.OutputBytes;
            report.AppendLine($"Estimate within 10% of the output: {Math.Abs(error) < 0.10}");
        }
        // end-snippet

        // begin-snippet: compress-estimate-exact
        // An exact size means running Compress itself: to Stream.Null, which keeps nothing,
        // OutputBytes is still the number of bytes written.
        using (var input = File.OpenRead("output/scans.pdf"))
        {
            var counted = Pdf.Compress(input, Stream.Null, levers);
            report.AppendLine($"Exact size, nothing kept: {counted.OutputBytes == new FileInfo("output/scans-small.pdf").Length}");
        }
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    // 'pages' pages, each with its own noisy 900-pixel image drawn two inches wide (450 DPI).
    private static void BuildMultiPageScan(string path, int pages)
    {
        using var document = new Manuscript
        {
            Sections = [.. Enumerable.Range(0, pages).Select(static page => new Section
            {
                Body = new Image(ScanLikeSamples(seed: page, noise: 6 + (page % 4 * 4)), pixelWidth: ScanPixels, pixelHeight: ScanPixels) { Width = 144, Height = 144 },
            })],
        }.Render();
        document.Save(path);
    }

    // An HTTP response body stand-in: readable, not seekable.
    private static Stream OpenDownload(string path) => new ForwardOnlyReadStream(File.OpenRead(path));

    [Fact]
    public Task CompressTaggedPdfA()
    {
        var report = new StringBuilder();
        using (var source = new Manuscript
        {
            Language = "en-US",
            Title = "Scanned record",
            Sections = [new Section { Body = new Image(ScanLikeSamples(), pixelWidth: ScanPixels, pixelHeight: ScanPixels) { Width = 144, Height = 144, AltText = "Scanned page" } }],
        }.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b }))
        {
            source.Save("output/tagged-a2b.pdf");
        }

        // begin-snippet: compress-tagged-pdfa
        // Compress rewrites image streams, not content operators or the structure tree,
        // so tagging survives. It doesn't re-validate PDF/A: check the output yourself.
        var result = Pdf.Compress("output/tagged-a2b.pdf", "output/tagged-a2b-small.pdf", new PdfCompressOptions
        {
            Images = new PdfImageCompression { MaxDpi = 200, JpegQuality = 70 },
        });

        using var compressed = PdfDocument.Open("output/tagged-a2b-small.pdf");
        var structure = PdfStructureInfo.For(compressed);
        var pdfa = PdfAValidator.Validate(compressed);
        report.AppendLine($"Image actions: {string.Join(", ", result.Images.Select(static i => i.Action))}");
        report.AppendLine($"Still tagged: {structure.IsTagged}, language: {structure.Language}");
        report.AppendLine($"Declared: PDF/A-{pdfa.DeclaredPart}{pdfa.DeclaredConformance}, conformant (self-check): {pdfa.IsConformant}");
        // end-snippet

        return Verifier.Verify(report.ToString());
    }

    private const int ScanPixels = 900;

    private static byte[] ScanLikeSamples(int seed = 7, int noise = 12)
    {
        const int pixels = ScanPixels;
        var random = new Random(seed);
        var samples = new byte[pixels * pixels * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)Math.Clamp(((i / 3 % pixels) * 255 / pixels) + random.Next(-noise, noise + 1), 0, 255);
        }

        return samples;
    }

    private static void BuildScanLikeSource(string path)
    {
        const int pixels = ScanPixels;
        var samples = ScanLikeSamples();

        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Image(new Image(samples, pixelWidth: pixels, pixelHeight: pixels) { Width = 144, Height = 144 });
        });
        document.Save(path);
    }

    private sealed class ForwardOnlyReadStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
