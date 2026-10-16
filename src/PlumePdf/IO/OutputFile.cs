namespace PlumePdf.IO;

/// <summary>
/// The output files PlumePDF writes to a caller's path: a full write to a temporary sibling
/// moved into place over the destination (<see cref="Replace"/>), or a file opened directly
/// (<see cref="Open"/>, for an incremental update's append or copy). A file-system failure of
/// any of these — creating the sibling or the file, writing, flushing or closing it, the move
/// into place — is the coded <c>PLUME1007</c>, naming the output path, with the operating
/// system's exception as its <see cref="Exception.InnerException"/>. Failures raised by the
/// code doing the writing (reading the source, cancellation, an already-coded failure) pass
/// through unchanged (see <see cref="CodedWriteStream"/>).
/// </summary>
internal static class OutputFile
{
    /// <summary>
    /// Writes <paramref name="path"/> through <paramref name="write"/> into a temporary file in
    /// the same directory, then moves it over <paramref name="path"/>: the destination is either
    /// the complete new file or untouched, and the temporary file never outlives the call.
    /// </summary>
    public static void Replace(string path, Action<Stream> write)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var tempPath = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, $".{Path.GetFileName(path)}.plumepdf-tmp-{Guid.NewGuid():N}");

        try
        {
            using (var stream = Create(tempPath, path, FileMode.Create, FileShare.None))
            {
                write(stream);
            }

            try
            {
                File.Move(tempPath, path, overwrite: true);
            }
            catch (Exception ex) when (CodedWriteStream.IsFileSystemFailure(ex))
            {
                throw WriteFailed(path, ex);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    /// <summary>Opens <paramref name="path"/> for writing with <paramref name="mode"/>, its failures coded <c>PLUME1007</c>.</summary>
    public static Stream Open(string path, FileMode mode, FileShare share) => Create(path, path, mode, share);

    private static CodedWriteStream Create(string filePath, string outputPath, FileMode mode, FileShare share)
    {
        FileStream file;
        try
        {
            file = new FileStream(filePath, mode, FileAccess.Write, share);
        }
        catch (Exception ex) when (CodedWriteStream.IsFileSystemFailure(ex))
        {
            throw WriteFailed(outputPath, ex);
        }

        return new CodedWriteStream(file, ex => WriteFailed(outputPath, ex));
    }

    /// <summary>The coded <c>PLUME1007</c> for a file-system failure writing <paramref name="path"/>.</summary>
    public static PlumePdfException WriteFailed(string path, Exception ex) =>
        new("PLUME1007", $"Could not write the output file '{path}': {ex.Message}", ex);
}
