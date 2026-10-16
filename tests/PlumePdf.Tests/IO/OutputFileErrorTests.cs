using PlumePdf.Tests.Signing;
using Xunit;

namespace PlumePdf.Tests.IO;

/// <summary>
/// A file-system failure writing an output file — a missing directory, an output path that is
/// a directory, a directory the process can't write to — surfaces as a coded
/// <c>PLUME1007</c> carrying the operating system's exception, never a bare
/// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/>, and leaves no temporary
/// sibling behind. Covered for every verb that writes a path: <c>Save</c>,
/// <c>SaveIncremental</c>, <c>Pdf.Compress</c>, signing, and document timestamping (issue #54).
/// </summary>
public sealed class OutputFileErrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"plumepdf-outerr-{Guid.NewGuid():N}");
    private readonly string _source;

    public OutputFileErrorTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.pdf");
        SignVerifyRoundTripTests.CreateSampleDocument(_source);
    }

    public static TheoryData<string> Verbs => new() { "Save", "SaveIncremental", "Compress", "Sign", "Timestamp" };

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task MissingOutputDirectory_IsPlume1007(string verb)
    {
        var missing = Path.Combine(_root, "missing");
        var output = Path.Combine(missing, "out.pdf");

        var ex = await Assert.ThrowsAsync<PlumePdfException>(() => Write(verb, output));

        Assert.Equal("PLUME1007", ex.Code);
        Assert.IsAssignableFrom<IOException>(ex.InnerException);
        Assert.Contains(output, ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missing));
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task OutputPathIsADirectory_IsPlume1007_AndLeavesNoTemporarySibling(string verb)
    {
        var output = Path.Combine(_root, "a-directory");
        Directory.CreateDirectory(output);

        var ex = await Assert.ThrowsAsync<PlumePdfException>(() => Write(verb, output));

        Assert.Equal("PLUME1007", ex.Code);
        Assert.True(ex.InnerException is IOException or UnauthorizedAccessException, ex.InnerException?.GetType().FullName);
        Assert.True(Directory.Exists(output));
        Assert.Empty(TempSiblings(output));
    }

    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task ReadOnlyOutputDirectory_IsPlume1007_AndLeavesNothing(string verb)
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return; // POSIX permission bits don't apply on Windows, and root ignores them
        }

        var directory = Path.Combine(_root, "read-only");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var output = Path.Combine(directory, "out.pdf");

            var ex = await Assert.ThrowsAsync<PlumePdfException>(() => Write(verb, output));

            Assert.Equal("PLUME1007", ex.Code);
            Assert.True(ex.InnerException is IOException or UnauthorizedAccessException, ex.InnerException?.GetType().FullName);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void SaveIncremental_OntoAReadOnlySourceFile_IsPlume1007()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var before = File.ReadAllBytes(_source);
        File.SetUnixFileMode(_source, UnixFileMode.UserRead);
        try
        {
            using var document = PdfDocument.Open(_source);
            document.Pages.RemoveAt(0);

            var ex = Assert.Throws<PlumePdfException>(() => document.SaveIncremental(_source));

            Assert.Equal("PLUME1007", ex.Code);
            Assert.IsType<UnauthorizedAccessException>(ex.InnerException);
            Assert.Equal(before, File.ReadAllBytes(_source));
        }
        finally
        {
            File.SetUnixFileMode(_source, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private static string[] TempSiblings(string outputPath) =>
        Directory.GetFiles(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.plumepdf-tmp-*");

    private async Task Write(string verb, string output)
    {
        switch (verb)
        {
            case "Save":
                {
                    using var document = PdfDocument.Open(_source);
                    document.Save(output);
                    break;
                }

            case "SaveIncremental":
                {
                    using var document = PdfDocument.Open(_source);
                    document.SaveIncremental(output);
                    break;
                }

            case "Compress":
                Pdf.Compress(_source, output);
                break;

            case "Sign":
                {
                    using var ca = new TestCertificateAuthority();
                    Pdf.Sign(_source, output, new PdfSignOptions { Certificate = ca.LeafRsa });
                    break;
                }

            case "Timestamp":
                {
                    using var ca = new TestCertificateAuthority();
                    using var tsa = new FakeTimestampAuthority(ca.Intermediate);
                    using var document = PdfDocument.Open(_source);
                    await document.Signatures.AddDocumentTimestampAsync(output, tsa);
                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(verb), verb, null);
        }
    }
}
