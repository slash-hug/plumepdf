using PlumePdf.Documents.Compression;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <see cref="PdfCompressOptions.Progress"/>: the stages each overload reports and their order,
/// the unit and total of each, the first-zero / last-equals-total framing, which images count
/// toward the writing total, refusals reporting nothing, a throwing callback propagating (with
/// no output left at a path), cancellation still honored, and output bytes unchanged by
/// reporting.
/// </summary>
public class CompressProgressTests
{
    /// <summary>
    /// Three pages: page one draws image 10 and its duplicate 12 (same bytes, merged into 10),
    /// page two draws a bilevel image 14 (skipped) and an inline image, page three draws image
    /// 18, whose soft mask is 20. Image 22 is drawn by no page. The images the write handles are
    /// 10, 14, 18 and 20.
    /// </summary>
    private static ScanTestPdf MultiImage() =>
        new ScanTestPdf()
            .Stream(10, Image(), FlateSamples())
            .Stream(12, Image(), FlateSamples())
            .Stream(14, Image(colorSpace: "/DeviceGray", bits: 1), new byte[Pixels * Pixels / 8])
            .Stream(18, Image(extra: "/SMask 20 0 R"), FlateSamples(seed: 2))
            .Stream(20, Image(colorSpace: "/DeviceGray"), FlateSamples(channels: 1, seed: 3))
            .Stream(22, Image(), FlateSamples(seed: 4))
            .Page(3, $"{Draw("Im0")} {Draw("Im1")}", xobjects: "/Im0 10 0 R /Im1 12 0 R")
            .Page(5, $"{Draw("Im0")} q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EI Q", xobjects: "/Im0 14 0 R")
            .Page(7, Draw("Im0"), xobjects: "/Im0 18 0 R");

    [Fact]
    public void PathOverload_ReportsPlanningThenWriting_EachFramedFromZeroToTotal()
    {
        var progress = new RecordingProgress();
        var (result, _) = Compress(MultiImage(), AllLevers with { Progress = progress });

        Assert.Equal([CompressionStage.Planning, CompressionStage.Writing], Stages(progress));
        AssertFramedAndMonotonic(progress);

        Assert.Equal(
            [Report(CompressionStage.Planning, 0, 3), Report(CompressionStage.Planning, 1, 3), Report(CompressionStage.Planning, 2, 3), Report(CompressionStage.Planning, 3, 3)],
            Of(progress, CompressionStage.Planning));
        Assert.Equal(
            [Report(CompressionStage.Writing, 0, 4), Report(CompressionStage.Writing, 1, 4), Report(CompressionStage.Writing, 2, 4), Report(CompressionStage.Writing, 3, 4), Report(CompressionStage.Writing, 4, 4)],
            Of(progress, CompressionStage.Writing));

        // The writing total is the image XObjects the result reports, less the merged duplicates.
        Assert.Equal(ImageCompressionAction.Deduplicated, Decision(result, 12).Action);
        Assert.Equal(ImageCompressionAction.Skipped, Decision(result, 14).Action);
        Assert.DoesNotContain(result.Images, static i => i.SourceReference?.Number == 22);
        Assert.Equal(4, result.Images.Count(static i => i.SourceReference is not null && i.Action != ImageCompressionAction.Deduplicated));
    }

    [Fact]
    public void LosslessOnly_CountsTheSamePlannedImages()
    {
        var progress = new RecordingProgress();
        Compress(MultiImage(), new PdfCompressOptions { Progress = progress });

        var writing = Of(progress, CompressionStage.Writing);
        Assert.Equal(4, writing[^1].Total);
        Assert.Equal(4, writing[^1].Completed);
    }

    [Fact]
    public void ADocumentWithNoImages_ReportsZeroOfZeroForWriting()
    {
        var progress = new RecordingProgress();
        Compress(new ScanTestPdf().Page(3, "0 0 10 10 re f"), new PdfCompressOptions { Progress = progress });

        Assert.Equal([Report(CompressionStage.Writing, 0, 0)], Of(progress, CompressionStage.Writing));
        Assert.Equal([Report(CompressionStage.Planning, 0, 1), Report(CompressionStage.Planning, 1, 1)], Of(progress, CompressionStage.Planning));
    }

    [Fact]
    public void SeekableStreamInput_ReportsNoSpooling()
    {
        var progress = new RecordingProgress();
        using var input = new MemoryStream(MultiImage().Build());
        using var output = new MemoryStream();

        Pdf.Compress(input, output, AllLevers with { Progress = progress });

        Assert.Equal([CompressionStage.Planning, CompressionStage.Writing], Stages(progress));
        AssertFramedAndMonotonic(progress);
    }

    [Fact]
    public void NonSeekableInput_ReportsSpoolingInBytes_WithNoTotal_OncePerChunk()
    {
        var source = new ScanTestPdf { MinimumStreamBytes = (5 << 20) / 2 }.Page(3, "0 0 10 10 re f").Build();
        var progress = new RecordingProgress();
        using var input = new ForwardOnlyStream(new MemoryStream(source), writable: false);
        using var output = new MemoryStream();

        Pdf.Compress(input, output, new PdfCompressOptions { Progress = progress });

        AssertSpooled(progress, source.Length);
    }

    [Fact]
    public async Task NonSeekableInput_Async_ReportsSpoolingInBytes_WithNoTotal_OncePerChunk()
    {
        var source = new ScanTestPdf { MinimumStreamBytes = (5 << 20) / 2 }.Page(3, "0 0 10 10 re f").Build();
        var progress = new RecordingProgress();
        using var input = new ForwardOnlyStream(new MemoryStream(source), writable: false);
        using var output = new MemoryStream();

        await Pdf.CompressAsync(input, output, new PdfCompressOptions { Progress = progress });

        AssertSpooled(progress, source.Length);
    }

    [Fact]
    public async Task PathOverload_Async_ReportsTheSameEvents()
    {
        var progress = new RecordingProgress();
        var source = Write(MultiImage());
        var output = TempPath();
        try
        {
            await Pdf.CompressAsync(source, output, AllLevers with { Progress = progress });
        }
        finally
        {
            Delete(source, output);
        }

        var synchronous = new RecordingProgress();
        Compress(MultiImage(), AllLevers with { Progress = synchronous });
        Assert.Equal(synchronous.Reports, progress.Reports);
    }

    [Fact]
    public void ArgumentErrors_ReportNothing()
    {
        var progress = new RecordingProgress();
        var options = new PdfCompressOptions { Progress = progress, Images = new PdfImageCompression { JpegQuality = 0 } };
        using var input = new ForwardOnlyStream(new MemoryStream(MultiImage().Build()), writable: false);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf.Compress(input, output, options));
        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf.Compress("in.pdf", "out.pdf", options));

        Assert.Empty(progress.Reports);
    }

    [Fact]
    public void ALinearizeRefusal_ReportsNothing_EvenForANonSeekableInput()
    {
        var progress = new RecordingProgress();
        using var input = new ForwardOnlyStream(new MemoryStream(MultiImage().Build()), writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PlumePdfException>(() => Pdf.Compress(input, output, new PdfCompressOptions { Progress = progress }, PdfOptions.Default with { Linearize = true }));

        Assert.Equal("PLUME5018", ex.Code);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    public void AnEncryptedSource_ReportsNothing()
    {
        var progress = new RecordingProgress();
        var source = Path.Combine(RepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "AES-128.pdf");
        var output = TempPath();

        var ex = Assert.Throws<PlumePdfException>(() => Pdf.Compress(source, output, new PdfCompressOptions { Progress = progress }));

        Assert.Equal("PLUME5001", ex.Code);
        Assert.Empty(progress.Reports);
    }

    [Fact]
    public void ASignedSource_ReportsNothing()
    {
        var progress = new RecordingProgress();
        var signed = CompressSignatureTests.SignedFormSource();
        var output = TempPath();
        try
        {
            var ex = Assert.Throws<PlumePdfException>(() => Pdf.Compress(signed, output, new PdfCompressOptions { Progress = progress }));

            Assert.Equal("PLUME6084", ex.Code);
            Assert.Empty(progress.Reports);
        }
        finally
        {
            Delete(signed, output);
        }
    }

    [Fact]
    public void ASamePathRefusal_ReportsNothing()
    {
        var progress = new RecordingProgress();
        var source = Write(MultiImage());
        var hooks = new CompressionHooks { SamePathSourceLimit = 100 };
        try
        {
            var ex = Assert.Throws<PlumePdfException>(() => CompressionEngine.Compress(source, source, new PdfCompressOptions { Progress = progress }, null, CancellationToken.None, hooks));

            Assert.Equal("PLUME1006", ex.Code);
            Assert.Empty(progress.Reports);
        }
        finally
        {
            Delete(source);
        }
    }

    [Theory]
    [InlineData(CompressionStage.Planning)]
    [InlineData(CompressionStage.Writing)]
    public void ACallbackThatThrows_Propagates_AndLeavesNoOutputAtAPath(CompressionStage stage)
    {
        var progress = new RecordingProgress
        {
            OnReport = report =>
            {
                if (report.Stage == stage && report.Completed == 1)
                {
                    throw new ProgressCallbackException();
                }
            },
        };
        var source = Write(MultiImage());
        var output = TempPath();
        try
        {
            Assert.Throws<ProgressCallbackException>(() => Pdf.Compress(source, output, AllLevers with { Progress = progress }));

            Assert.False(File.Exists(output));
            Assert.Empty(TempSiblings(output));
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void ACallbackThatThrowsWhileSpooling_Propagates_AndRemovesTheSpool()
    {
        var progress = new RecordingProgress
        {
            OnReport = report =>
            {
                if (report is { Stage: CompressionStage.Spooling, Completed: > 0 })
                {
                    throw new ProgressCallbackException();
                }
            },
        };
        string? spool = null;
        var hooks = new CompressionHooks { Spooled = path => spool = path };
        using var input = new ForwardOnlyStream(new MemoryStream(MultiImage().Build()), writable: false);
        using var output = new MemoryStream();

        Assert.Throws<ProgressCallbackException>(() => CompressionEngine.Compress(input, output, new PdfCompressOptions { Progress = progress }, null, CancellationToken.None, hooks));

        Assert.NotNull(spool);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public void Progress_DoesNotChangeTheOutputBytes()
    {
        var deterministic = PdfOptions.Default with { Deterministic = true };

        var (_, without) = Compress(MultiImage(), AllLevers, deterministic);
        var (_, with) = Compress(MultiImage(), AllLevers with { Progress = new RecordingProgress() }, deterministic);

        Assert.Equal(without, with);
    }

    [Theory]
    [InlineData(CompressionStage.Planning)]
    [InlineData(CompressionStage.Writing)]
    public async Task Cancellation_IsStillHonored_WithProgressSet(CompressionStage stage)
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress
        {
            OnReport = report =>
            {
                if (report.Stage == stage && report.Completed == 1)
                {
                    cancellation.Cancel();
                }
            },
        };
        var source = Write(MultiImage());
        var output = TempPath();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pdf.CompressAsync(source, output, AllLevers with { Progress = progress }, cancellationToken: cancellation.Token));

            Assert.False(File.Exists(output));
            Assert.Equal(1, progress.Reports.Where(r => r.Stage == stage).Max(static r => r.Completed));
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public async Task Cancellation_WhileSpoolingAsync_IsStillHonored_AndRemovesTheSpool()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress
        {
            OnReport = report =>
            {
                if (report is { Stage: CompressionStage.Spooling, Completed: > 0 })
                {
                    cancellation.Cancel();
                }
            },
        };
        string? spool = null;
        var hooks = new CompressionHooks { Spooled = path => spool = path };
        var source = new ScanTestPdf { MinimumStreamBytes = (5 << 20) / 2 }.Page(3, "0 0 10 10 re f").Build();
        using var input = new ForwardOnlyStream(new MemoryStream(source), writable: false);
        using var output = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompressionEngine.CompressAsync(input, output, new PdfCompressOptions { Progress = progress }, null, cancellation.Token, hooks));

        Assert.Single(progress.Reports, static r => r.Completed > 0);
        Assert.False(File.Exists(spool));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void OptionsEquality_ComparesTheProgressReference()
    {
        var progress = new RecordingProgress();

        Assert.Equal(new PdfCompressOptions { Progress = progress }, new PdfCompressOptions { Progress = progress });
        Assert.NotEqual(new PdfCompressOptions { Progress = progress }, new PdfCompressOptions { Progress = new RecordingProgress() });
        Assert.Null(PdfCompressOptions.Default.Progress);
    }

    private static CompressionProgress Report(CompressionStage stage, long completed, long? total) => new(stage, completed, total);

    private static List<CompressionProgress> Of(RecordingProgress progress, CompressionStage stage) =>
        [.. progress.Reports.Where(r => r.Stage == stage)];

    // The distinct stages in the order they were first reported, after checking no stage resumes once another began.
    private static List<CompressionStage> Stages(RecordingProgress progress)
    {
        var stages = new List<CompressionStage>();
        foreach (var report in progress.Reports)
        {
            if (stages.Count == 0 || stages[^1] != report.Stage)
            {
                Assert.DoesNotContain(report.Stage, stages);
                stages.Add(report.Stage);
            }
        }

        return stages;
    }

    // Each stage starts at zero, never goes backward or repeats a report, and ends at its total when it has one.
    private static void AssertFramedAndMonotonic(RecordingProgress progress)
    {
        foreach (var stage in Stages(progress))
        {
            var reports = Of(progress, stage);
            Assert.Equal(0, reports[0].Completed);
            for (var i = 1; i < reports.Count; i++)
            {
                Assert.True(reports[i].Completed > reports[i - 1].Completed, $"{stage} went from {reports[i - 1].Completed} to {reports[i].Completed}.");
                Assert.Equal(reports[0].Total, reports[i].Total);
            }

            if (reports[^1].Total is long total)
            {
                Assert.Equal(total, reports[^1].Completed);
            }
        }
    }

    // Spooling comes first, counts bytes with no total, reports at most once per 1 MiB chunk, and ends at the input's length.
    private static void AssertSpooled(RecordingProgress progress, long length)
    {
        Assert.Equal([CompressionStage.Spooling, CompressionStage.Planning, CompressionStage.Writing], Stages(progress));
        AssertFramedAndMonotonic(progress);
        var spooling = Of(progress, CompressionStage.Spooling);
        Assert.All(spooling, static r => Assert.Null(r.Total));
        Assert.Equal(length, spooling[^1].Completed);
        var chunks = (length + (1 << 20) - 1) >> 20;
        Assert.True(chunks >= 3, $"The fixture should span several chunks; it spans {chunks}.");
        Assert.InRange(spooling.Count - 1, 1, chunks);
    }

    /// <summary>A synchronous <see cref="IProgress{T}"/> that records every report, in order, on the reporting thread.</summary>
    private sealed class RecordingProgress : IProgress<CompressionProgress>
    {
        private readonly List<CompressionProgress> _reports = [];

        /// <summary>Called with each report after it is recorded; a test throws or cancels from it.</summary>
        public Action<CompressionProgress>? OnReport { get; init; }

        public IReadOnlyList<CompressionProgress> Reports
        {
            get
            {
                lock (_reports)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(CompressionProgress value)
        {
            lock (_reports)
            {
                _reports.Add(value);
            }

            OnReport?.Invoke(value);
        }
    }

    private sealed class ProgressCallbackException : Exception
    {
        public ProgressCallbackException()
            : base("The progress callback threw.")
        {
        }
    }
}
