using PlumePdf.Tests.Signing;
using Xunit;

namespace PlumePdf.Tests.IO;

/// <summary>
/// Opening a path with <see cref="PdfOptions.PreferStreamIo"/> (and the stream fallback the
/// default memory-mapped open takes) reports a missing file as <c>PLUME1002</c> and any other
/// file-system failure as <c>PLUME1001</c> — the same codes the memory-mapped open uses — rather
/// than a bare <see cref="FileNotFoundException"/>/<see cref="UnauthorizedAccessException"/> (issue #54).
/// </summary>
public sealed class SourceFileErrorTests : IDisposable
{
    private static readonly PdfOptions StreamIo = new() { PreferStreamIo = true };
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"plumepdf-srcerr-{Guid.NewGuid():N}");

    public SourceFileErrorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void PreferStreamIo_MissingFile_IsPlume1002()
    {
        var ex = Assert.Throws<PlumePdfException>(() => PdfDocument.Open(Path.Combine(_root, "missing.pdf"), StreamIo));

        Assert.Equal("PLUME1002", ex.Code);
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    [Fact]
    public void PreferStreamIo_MissingDirectory_IsPlume1002()
    {
        var ex = Assert.Throws<PlumePdfException>(() => PdfDocument.Open(Path.Combine(_root, "missing", "missing.pdf"), StreamIo));

        Assert.Equal("PLUME1002", ex.Code);
        Assert.IsType<DirectoryNotFoundException>(ex.InnerException);
    }

    [Fact]
    public async Task PreferStreamIo_OpenAsync_MissingFile_IsPlume1002()
    {
        var ex = await Assert.ThrowsAsync<PlumePdfException>(() => PdfDocument.OpenAsync(Path.Combine(_root, "missing.pdf"), StreamIo));

        Assert.Equal("PLUME1002", ex.Code);
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    [Fact]
    public async Task PreferStreamIo_OpenAsync_Cancelled_StaysCancelled()
    {
        var path = Path.Combine(_root, "source.pdf");
        SignVerifyRoundTripTests.CreateSampleDocument(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfDocument.OpenAsync(path, StreamIo, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableFile_IsPlume1001(bool preferStreamIo)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var path = Path.Combine(_root, "unreadable.pdf");
        SignVerifyRoundTripTests.CreateSampleDocument(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        var options = new PdfOptions { PreferStreamIo = preferStreamIo };

        var sync = Assert.Throws<PlumePdfException>(() => PdfDocument.Open(path, options));
        var async = await Assert.ThrowsAsync<PlumePdfException>(() => PdfDocument.OpenAsync(path, options));

        Assert.Equal("PLUME1001", sync.Code);
        Assert.IsType<UnauthorizedAccessException>(sync.InnerException);
        Assert.Equal("PLUME1001", async.Code);
    }
}
