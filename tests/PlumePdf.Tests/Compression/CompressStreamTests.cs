using PlumePdf.Documents.Compression;
using PlumePdf.Tests.PageRemoval;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <c>Pdf.Compress(Stream, Stream, ...)</c>: seekable inputs read in place, non-seekable ones
/// spooled to a temporary file that never outlives the call, any writable output (seekable or
/// not, already positioned or not) given a valid PDF whose offsets start where writing began,
/// byte-identical output to the path overload, argument errors and refusals before a byte is
/// written, and both streams left open for their owner.
/// </summary>
public class CompressStreamTests
{
    [Fact]
    public void SeekableInputAndOutput_WriteTheCompressedDocument()
    {
        var source = OneImage(Image(), FlateSamples()).Build();
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();

        var result = Pdf.Compress(input, output, AllLevers);

        Assert.Equal(source.Length, result.InputBytes);
        Assert.Equal(output.Length, result.OutputBytes);
        Assert.Equal(ImageCompressionAction.DownsampledAndReencoded, Assert.Single(result.Images).Action);
        var bytes = output.ToArray();
        using var reopened = PdfDocument.Open(bytes);
        Assert.Single(reopened.Pages);
        CleanupFixtures3b.AssertQpdfClean(bytes, "stream compress, seekable output");
    }

    [Fact]
    public void NonSeekableInput_IsSpooledToATemporaryFile_ThatIsGoneAfterwards()
    {
        var source = OneImage(Image(), FlateSamples()).Build();
        using var input = new ForwardOnlyStream(new MemoryStream(source), writable: false);
        using var output = new MemoryStream();
        string? spool = null;
        var spoolExistedWhilePlanning = false;
        var hooks = new CompressionHooks
        {
            Spooled = path => spool = path,
            Planning = () => spoolExistedWhilePlanning = spool is not null && File.Exists(spool),
        };

        var result = CompressionEngine.Compress(input, output, AllLevers, null, CancellationToken.None, hooks);

        Assert.True(spoolExistedWhilePlanning);
        Assert.False(File.Exists(spool));
        Assert.Equal(source.Length, result.InputBytes);
        Assert.Equal(output.Length, result.OutputBytes);
        using var reopened = PdfDocument.Open(output.ToArray());
        Assert.Single(reopened.Pages);
    }

    [Fact]
    public void NonSeekableInput_SpoolIsDeleted_WhenTheWriteFails()
    {
        using var input = new ForwardOnlyStream(new MemoryStream(TwoImages().Build()), writable: false);
        using var output = new MemoryStream();
        string? spool = null;
        var hooks = new CompressionHooks { Spooled = path => spool = path, Transforming = _ => throw new IOException("disk full") };

        Assert.Throws<IOException>(() => CompressionEngine.Compress(input, output, AllLevers, null, CancellationToken.None, hooks));

        Assert.NotNull(spool);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public void NonSeekableInput_SpoolIsDeleted_WhenCancelledWhileWriting()
    {
        using var input = new ForwardOnlyStream(new MemoryStream(TwoImages().Build()), writable: false);
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        string? spool = null;
        var hooks = new CompressionHooks { Spooled = path => spool = path, Transforming = _ => cancellation.Cancel() };

        Assert.ThrowsAny<OperationCanceledException>(() => CompressionEngine.Compress(input, output, AllLevers, null, cancellation.Token, hooks));

        Assert.NotNull(spool);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public async Task CompressAsync_NonSeekableInput_CancelledWhileSpooling_DeletesTheSpool()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new ForwardOnlyStream(new MemoryStream(TwoImages().Build()), writable: false) { OnRead = cancellation.Cancel };
        using var output = new MemoryStream();
        string? spool = null;
        var planned = false;
        var hooks = new CompressionHooks { Spooled = path => spool = path, Planning = () => planned = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompressionEngine.CompressAsync(input, output, AllLevers, null, cancellation.Token, hooks));

        Assert.False(planned);
        Assert.NotNull(spool);
        Assert.False(File.Exists(spool));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task CompressAsync_NonSeekableInputAndOutput_Completes()
    {
        var source = OneImage(Image(), FlateSamples()).Build();
        using var input = new ForwardOnlyStream(new MemoryStream(source), writable: false);
        using var sink = new MemoryStream();
        using var output = new ForwardOnlyStream(sink, readable: false);

        var result = await Pdf.CompressAsync(input, output, AllLevers);

        Assert.Equal(source.Length, result.InputBytes);
        Assert.Equal(sink.Length, result.OutputBytes);
        Assert.Equal(ImageCompressionAction.DownsampledAndReencoded, Assert.Single(result.Images).Action);
        using var reopened = PdfDocument.Open(sink.ToArray());
        Assert.Single(reopened.Pages);
    }

    [Fact]
    public void NonSeekableOutput_GetsAValidPdf()
    {
        using var input = new MemoryStream(OneImage(Image(), FlateSamples()).Build());
        using var sink = new MemoryStream();
        using var output = new ForwardOnlyStream(sink, readable: false);

        var result = Pdf.Compress(input, output, AllLevers);

        Assert.Equal(sink.Length, result.OutputBytes);
        Assert.True(output.Flushes > 0);
        var bytes = sink.ToArray();
        using var reopened = PdfDocument.Open(bytes);
        Assert.Single(reopened.Pages);
        CleanupFixtures3b.AssertQpdfClean(bytes, "stream compress, non-seekable output");
    }

    [Fact]
    public void OutputAlreadyPastZero_GetsAPdfWhoseOffsetsStartWhereWritingBegan()
    {
        var prefix = "an envelope header the caller wrote first\n"u8.ToArray();
        using var input = new MemoryStream(OneImage(Image(), FlateSamples()).Build());
        using var output = new MemoryStream();
        output.Write(prefix);

        var result = Pdf.Compress(input, output, AllLevers);

        var all = output.ToArray();
        Assert.Equal(prefix, all[..prefix.Length]);
        Assert.Equal(all.Length - prefix.Length, result.OutputBytes);
        var pdf = all[prefix.Length..];
        using var reopened = PdfDocument.Open(pdf);
        Assert.Single(reopened.Pages);
        Assert.DoesNotContain(reopened.Diagnostics, static d => d.Severity == DiagnosticSeverity.Warning);
        CleanupFixtures3b.AssertQpdfClean(pdf, "stream compress, output past position 0");
    }

    [Fact]
    public void StreamOutput_IsByteIdenticalToPathOutput_UnderDeterministic()
    {
        var pdf = new ScanTestPdf { MinimumStreamBytes = 2048 }
            .Stream(10, Image(extra: "/SMask 11 0 R"), FlateSamples())
            .Stream(11, Image(colorSpace: "/DeviceGray"), FlateSamples(channels: 1))
            .Stream(12, Image(filter: "/DCTDecode"), JpegSamples())
            .Stream(13, Image(filter: "/DCTDecode"), JpegSamples())
            .Page(3, $"{Draw("A")} {Draw("B")}", xobjects: "/A 10 0 R /B 12 0 R")
            .Page(5, Draw("B"), xobjects: "/B 13 0 R");
        var deterministic = PdfOptions.Default with { Deterministic = true };
        var (pathResult, pathBytes) = Compress(pdf, AllLevers, deterministic);

        using var input = new ForwardOnlyStream(new MemoryStream(pdf.Build()), writable: false);
        using var sink = new MemoryStream();
        using var output = new ForwardOnlyStream(sink, readable: false);
        var streamResult = Pdf.Compress(input, output, AllLevers, deterministic);

        Assert.Equal(pathBytes, sink.ToArray());
        Assert.Equal(pathResult.InputBytes, streamResult.InputBytes);
        Assert.Equal(pathResult.OutputBytes, streamResult.OutputBytes);
        Assert.Equal(pathResult.Images.Select(Describe), streamResult.Images.Select(Describe));
        Assert.Equal(pathResult.Diagnostics.Select(static d => (d.Code, d.Severity, d.Message)), streamResult.Diagnostics.Select(static d => (d.Code, d.Severity, d.Message)));
    }

    [Fact]
    public void NullStreams_AreArgumentErrors()
    {
        using var stream = new MemoryStream();

        Assert.Throws<ArgumentNullException>(() => Pdf.Compress(null!, stream));
        Assert.Throws<ArgumentNullException>(() => Pdf.Compress(stream, (Stream)null!));
    }

    [Fact]
    public async Task CompressAsync_NullStreams_AreArgumentErrors()
    {
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Pdf.CompressAsync(null!, stream));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Pdf.CompressAsync(stream, (Stream)null!));
    }

    [Fact]
    public void TheSameStreamForInputAndOutput_IsAnArgumentError()
    {
        using var stream = new MemoryStream(OneImage(Image(), FlateSamples()).Build());
        var before = stream.ToArray();

        Assert.Throws<ArgumentException>(() => Pdf.Compress(stream, stream));

        Assert.Equal(before, stream.ToArray());
    }

    [Fact]
    public void AnUnreadableInput_IsAnArgumentError()
    {
        using var input = new ForwardOnlyStream(new MemoryStream(), readable: false);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => Pdf.Compress(input, output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AnUnwritableOutput_IsAnArgumentError()
    {
        using var input = new MemoryStream(OneImage(Image(), FlateSamples()).Build());
        using var output = new MemoryStream([], writable: false);

        Assert.Throws<ArgumentException>(() => Pdf.Compress(input, output));
    }

    [Fact]
    public void ADisposedStream_IsAnArgumentError()
    {
        var input = new MemoryStream(OneImage(Image(), FlateSamples()).Build());
        input.Dispose();
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => Pdf.Compress(input, output));
    }

    [Fact]
    public void NeitherStreamIsDisposed()
    {
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false);
        using var output = new ForwardOnlyStream(new MemoryStream(), readable: false);

        Pdf.Compress(input, output, AllLevers);

        Assert.False(input.Disposed);
        Assert.False(output.Disposed);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public void Linearize_IsRefusedBeforeTheInputIsRead()
    {
        var read = false;
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false) { OnRead = () => read = true };
        using var output = new MemoryStream();
        var hooks = new CompressionHooks { Spooled = _ => Assert.Fail("The input was spooled before the refusal.") };

        var ex = Assert.Throws<PlumePdfException>(() => CompressionEngine.Compress(input, output, PdfCompressOptions.Default, PdfOptions.Default with { Linearize = true }, CancellationToken.None, hooks));

        Assert.Equal("PLUME5018", ex.Code);
        Assert.False(read);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void InvalidImageLevers_AreRefusedBeforeTheInputIsRead()
    {
        var read = false;
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false) { OnRead = () => read = true };
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(() => Pdf.Compress(input, output, new PdfCompressOptions { Images = new PdfImageCompression { JpegQuality = 0 } }));

        Assert.False(read);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AnEncryptedSource_IsRefused_AndNothingIsWritten()
    {
        var path = Path.Combine(RepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "AES-128.pdf");
        using var input = new MemoryStream(File.ReadAllBytes(path));
        using var output = new MemoryStream();

        var ex = Assert.Throws<PlumePdfException>(() => Pdf.Compress(input, output));

        Assert.Equal("PLUME5001", ex.Code);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ASignedSource_IsRefused_AndNothingIsWritten()
    {
        var signed = CompressSignatureTests.SignedFormSource();
        try
        {
            using var input = new ForwardOnlyStream(new MemoryStream(File.ReadAllBytes(signed)), writable: false);
            using var output = new MemoryStream();
            string? spool = null;
            var hooks = new CompressionHooks { Spooled = p => spool = p };

            var ex = Assert.Throws<PlumePdfException>(() => CompressionEngine.Compress(input, output, PdfCompressOptions.Default, null, CancellationToken.None, hooks));

            Assert.Equal("PLUME6084", ex.Code);
            Assert.Equal(0, output.Length);
            Assert.False(File.Exists(spool));
        }
        finally
        {
            Delete(signed);
        }
    }

    [Fact]
    public void ASignedSource_WithAllowInvalidatingSignatures_IsStripped()
    {
        var signed = CompressSignatureTests.SignedFormSource();
        try
        {
            using var input = new MemoryStream(File.ReadAllBytes(signed));
            using var output = new MemoryStream();

            var result = Pdf.Compress(input, output, new PdfCompressOptions { AllowInvalidatingSignatures = true });

            Assert.Equal(1, result.SignaturesStripped);
            using var compressed = PdfDocument.Open(output.ToArray());
            Assert.Empty(compressed.Signatures);
        }
        finally
        {
            Delete(signed);
        }
    }

    [Fact]
    public void CancelledWhileWriting_ThrowsOperationCanceled()
    {
        using var input = new MemoryStream(TwoImages().Build());
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var hooks = new CompressionHooks { Transforming = _ => cancellation.Cancel() };

        Assert.ThrowsAny<OperationCanceledException>(() => CompressionEngine.Compress(input, output, AllLevers, null, cancellation.Token, hooks));

        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task CompressAsync_CancelledBeforeItStarts_WritesNothing()
    {
        var read = false;
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false) { OnRead = () => read = true };
        using var output = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pdf.CompressAsync(input, output, cancellationToken: new CancellationToken(canceled: true)));

        Assert.False(read);
        Assert.Equal(0, output.Length);
    }

    private static string Describe(ImageCompressionDecision d) =>
        $"{d.SourceReference} {d.PageIndex} {d.Action} {d.SkipReason} {d.DuplicateOf} {d.BytesBefore} {d.BytesAfter} " +
        $"{d.WidthBefore}x{d.HeightBefore} {d.WidthAfter}x{d.HeightAfter} {d.EffectiveDpiBefore} {d.EffectiveDpiAfter} {d.EstimatedSourceQuality}";

    private static ScanTestPdf TwoImages() =>
        new ScanTestPdf()
            .Stream(10, Image(), FlateSamples())
            .Stream(11, Image(), FlateSamples(seed: 2))
            .Page(3, $"{Draw("A")} {Draw("B")}", xobjects: "/A 10 0 R /B 11 0 R");
}
