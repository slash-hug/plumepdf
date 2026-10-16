namespace PlumePdf.IO;

/// <summary>
/// Opens a source path for stream reading — <see cref="PdfOptions.PreferStreamIo"/>, and the
/// fallback after a memory-mapped open can't map the file — with the same codes
/// <see cref="MemoryMappedByteSource"/> uses: a missing file or directory is <c>PLUME1002</c>,
/// any other file-system failure <c>PLUME1001</c>, the operating system's exception kept as
/// the <see cref="Exception.InnerException"/>. Cancellation passes through unchanged.
/// </summary>
internal static class SourceFile
{
    /// <summary>Opens <paramref name="path"/> for shared reading.</summary>
    public static FileStream OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (CodedWriteStream.IsFileSystemFailure(ex))
        {
            throw OpenFailed(path, ex);
        }
    }

    /// <summary>Reads the whole of <paramref name="path"/> asynchronously.</summary>
    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (CodedWriteStream.IsFileSystemFailure(ex))
        {
            throw OpenFailed(path, ex);
        }
    }

    private static PlumePdfException OpenFailed(string path, Exception ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException
            ? new PlumePdfException("PLUME1002", $"File not found: '{path}'.", ex)
            : new PlumePdfException("PLUME1001", $"Could not access '{path}': {ex.Message}", ex);
}
