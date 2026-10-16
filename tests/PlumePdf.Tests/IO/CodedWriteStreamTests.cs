using PlumePdf.IO;
using Xunit;

namespace PlumePdf.Tests.IO;

/// <summary>
/// <see cref="CodedWriteStream"/> codes a file-system failure of the file it wraps — a write,
/// a flush, the final flush on close (a full disk) — as <c>PLUME1007</c> with the original
/// exception preserved, and passes every other exception, and every failure of the code
/// writing into it, through unchanged (issue #54).
/// </summary>
public class CodedWriteStreamTests
{
    private const string Output = "/some/output.pdf";

    public static TheoryData<string> Operations => new() { "Write", "WriteSpan", "WriteByte", "WriteAsync", "Flush", "FlushAsync", "Dispose", "DisposeAsync" };

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task InnerIOException_IsPlume1007_WithTheOriginalAsInner(string operation)
    {
        var failure = new IOException("No space left on device");
        var stream = new CodedWriteStream(new ThrowingStream(() => failure), ex => OutputFile.WriteFailed(Output, ex));

        var ex = await Assert.ThrowsAsync<PlumePdfException>(() => Run(stream, operation));

        Assert.Equal("PLUME1007", ex.Code);
        Assert.Same(failure, ex.InnerException);
        Assert.Contains(Output, ex.Message, StringComparison.Ordinal);
        Assert.Contains("No space left on device", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task InnerUnauthorizedAccess_IsPlume1007(string operation)
    {
        var failure = new UnauthorizedAccessException("denied");
        var stream = new CodedWriteStream(new ThrowingStream(() => failure), ex => OutputFile.WriteFailed(Output, ex));

        var ex = await Assert.ThrowsAsync<PlumePdfException>(() => Run(stream, operation));

        Assert.Same(failure, ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task OtherInnerExceptions_PassThroughUnchanged(string operation)
    {
        var failure = new InvalidOperationException("not a file-system failure");
        var stream = new CodedWriteStream(new ThrowingStream(() => failure), ex => OutputFile.WriteFailed(Output, ex));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(stream, operation));

        Assert.Same(failure, ex);
    }

    [Fact]
    public void Replace_AFailureOfTheWritingCode_IsNotCodedAsAnOutputFailure_AndLeavesNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plumepdf-replace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "out.pdf");
            var sourceReadFailure = new IOException("the source went away");

            var ex = Assert.Throws<IOException>(() => OutputFile.Replace(output, stream =>
            {
                stream.Write("%PDF-"u8);
                throw sourceReadFailure;
            }));

            Assert.Same(sourceReadFailure, ex);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task Run(Stream stream, string operation)
    {
        switch (operation)
        {
            case "Write":
                stream.Write(new byte[4], 0, 4);
                break;
            case "WriteSpan":
                stream.Write(new byte[4].AsSpan());
                break;
            case "WriteByte":
                stream.WriteByte(1);
                break;
            case "WriteAsync":
                await stream.WriteAsync(new byte[4].AsMemory());
                break;
            case "Flush":
                stream.Flush();
                break;
            case "FlushAsync":
                await stream.FlushAsync();
                break;
            case "Dispose":
                stream.Dispose();
                break;
            case "DisposeAsync":
                await stream.DisposeAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    // A writable stream whose every write, flush and close throws.
    private sealed class ThrowingStream(Func<Exception> failure) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw failure();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw failure();

        public override void Write(ReadOnlySpan<byte> buffer) => throw failure();

        public override void WriteByte(byte value) => throw failure();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw failure();

        public override Task FlushAsync(CancellationToken cancellationToken) => throw failure();

        protected override void Dispose(bool disposing) => throw failure();

        public override ValueTask DisposeAsync() => throw failure();
    }
}
