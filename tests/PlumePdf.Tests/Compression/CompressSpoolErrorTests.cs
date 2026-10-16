using PlumePdf.Documents.Compression;
using Xunit;
using static PlumePdf.Tests.Compression.CompressFixtures;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// A non-seekable input is spooled to a temporary file: a failure creating or writing that
/// file is a coded <c>PLUME1008</c> (the output untouched, no spool left), while a failure
/// reading the caller's own input stream propagates exactly as the stream threw it (issue #54).
/// </summary>
public sealed class CompressSpoolErrorTests
{
    private static readonly string MissingDirectory = Path.Combine(Path.GetTempPath(), $"plumepdf-nospool-{Guid.NewGuid():N}");

    [Fact]
    public void UnusableSpoolDirectory_IsPlume1008()
    {
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false);
        using var output = new MemoryStream();
        string? spool = null;
        var hooks = new CompressionHooks { SpoolDirectory = MissingDirectory, Spooled = path => spool = path };

        var ex = Assert.Throws<PlumePdfException>(() => CompressionEngine.Compress(input, output, AllLevers, null, CancellationToken.None, hooks));

        Assert.Equal("PLUME1008", ex.Code);
        Assert.IsType<DirectoryNotFoundException>(ex.InnerException);
        Assert.Equal(0, output.Length);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public async Task CompressAsync_UnusableSpoolDirectory_IsPlume1008()
    {
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false);
        using var output = new MemoryStream();
        string? spool = null;
        var hooks = new CompressionHooks { SpoolDirectory = MissingDirectory, Spooled = path => spool = path };

        var ex = await Assert.ThrowsAsync<PlumePdfException>(() => CompressionEngine.CompressAsync(input, output, AllLevers, null, CancellationToken.None, hooks));

        Assert.Equal("PLUME1008", ex.Code);
        Assert.IsType<DirectoryNotFoundException>(ex.InnerException);
        Assert.Equal(0, output.Length);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public void InputStreamFailingWhileSpooling_StaysTheCallersIOException()
    {
        var failure = new IOException("connection reset");
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false) { OnRead = () => throw failure };
        using var output = new MemoryStream();
        string? spool = null;
        var hooks = new CompressionHooks { Spooled = path => spool = path };

        var ex = Assert.Throws<IOException>(() => CompressionEngine.Compress(input, output, AllLevers, null, CancellationToken.None, hooks));

        Assert.Same(failure, ex);
        Assert.False(File.Exists(spool));
    }

    [Fact]
    public async Task CompressAsync_InputStreamFailingWhileSpooling_StaysTheCallersIOException()
    {
        var failure = new IOException("connection reset");
        using var input = new ForwardOnlyStream(new MemoryStream(OneImage(Image(), FlateSamples()).Build()), writable: false) { OnRead = () => throw failure };
        using var output = new MemoryStream();
        string? spool = null;
        var hooks = new CompressionHooks { Spooled = path => spool = path };

        var ex = await Assert.ThrowsAsync<IOException>(() => CompressionEngine.CompressAsync(input, output, AllLevers, null, CancellationToken.None, hooks));

        Assert.Same(failure, ex);
        Assert.False(File.Exists(spool));
    }
}
