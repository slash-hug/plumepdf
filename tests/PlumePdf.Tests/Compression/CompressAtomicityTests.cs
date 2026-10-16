using PlumePdf.Documents.Compression;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// <c>Pdf.Compress</c> writes atomically: a cancelled or failed run leaves no output file (and
/// no temporary sibling), an existing output survives a failure untouched, and saving over a
/// source too large to buffer is refused before any work.
/// </summary>
public class CompressAtomicityTests
{
    [Fact]
    public async Task CompressAsync_CancelledBeforeItStarts_WritesNothing()
    {
        var source = Write(OneImage(Image(), FlateSamples()));
        var output = TempPath();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pdf.CompressAsync(source, output, cancellationToken: new CancellationToken(canceled: true)));

            Assert.False(File.Exists(output));
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void CancelledWhileWriting_LeavesNoOutputAndNoTemporaryFile()
    {
        var source = Write(TwoImages());
        var output = TempPath();
        using var cancellation = new CancellationTokenSource();
        var hooks = new CompressionHooks { Transforming = _ => cancellation.Cancel() };
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => CompressionEngine.Compress(source, output, AllLevers, null, cancellation.Token, hooks));

            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(File.Exists(output));
            Assert.Empty(TempSiblings(output));
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public async Task CompressAsync_Completes()
    {
        var source = Write(OneImage(Image(), FlateSamples()));
        var output = TempPath();
        try
        {
            var result = await Pdf.CompressAsync(source, output, AllLevers);

            Assert.Equal(ImageCompressionAction.DownsampledAndReencoded, Assert.Single(result.Images).Action);
            Assert.Equal(new FileInfo(output).Length, result.OutputBytes);
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void FailureWhileWriting_LeavesAnExistingOutputIntact()
    {
        var source = Write(TwoImages());
        var output = TempPath();
        var previous = "an earlier output"u8.ToArray();
        File.WriteAllBytes(output, previous);
        var hooks = new CompressionHooks { Transforming = _ => throw new IOException("disk full") };
        try
        {
            Assert.Throws<IOException>(() => CompressionEngine.Compress(source, output, AllLevers, null, CancellationToken.None, hooks));

            Assert.Equal(previous, File.ReadAllBytes(output));
            Assert.Empty(TempSiblings(output));
        }
        finally
        {
            Delete(source, output);
        }
    }

    [Fact]
    public void SamePathOverASourceTooLargeToBuffer_IsRefusedUpFront()
    {
        var source = Write(OneImage(Image(), FlateSamples()));
        var before = File.ReadAllBytes(source);
        var planned = false;
        var hooks = new CompressionHooks { SamePathSourceLimit = 100, Planning = () => planned = true };
        try
        {
            var ex = Assert.Throws<PlumePdfException>(() => CompressionEngine.Compress(source, source, PdfCompressOptions.Default, null, CancellationToken.None, hooks));

            Assert.Equal("PLUME1006", ex.Code);
            Assert.False(planned);
            Assert.Equal(before, File.ReadAllBytes(source));
        }
        finally
        {
            Delete(source);
        }
    }

    [Fact]
    public void SamePath_UnderTheLimit_ReplacesTheSource()
    {
        var source = Write(new ScanTestPdf { MinimumStreamBytes = 4096 }.Stream(10, Image(), FlateSamples()).Page(3, Draw(), xobjects: "/Im0 10 0 R"));
        try
        {
            var result = Pdf.Compress(source, source);

            Assert.Equal(new FileInfo(source).Length, result.OutputBytes);
            Assert.True(result.OutputBytes < result.InputBytes);
            using var reopened = PdfDocument.Open(source);
            Assert.Single(reopened.Pages);
        }
        finally
        {
            Delete(source);
        }
    }

    private static ScanTestPdf TwoImages() =>
        new ScanTestPdf()
            .Stream(10, Image(), FlateSamples())
            .Stream(11, Image(), FlateSamples(seed: 2))
            .Page(3, $"{Draw("A")} {Draw("B")}", xobjects: "/A 10 0 R /B 11 0 R");
}
