namespace PlumePdf.IO;

/// <summary>
/// The temporary copy a non-seekable input is spooled to so it can be read with random access.
/// A file-system failure creating, writing or removing it is the coded <c>PLUME1008</c>, the
/// operating system's exception kept as the <see cref="Exception.InnerException"/>; failures
/// reading the caller's own input never pass through it and stay as the caller's stream threw them.
/// </summary>
internal static class SpoolFile
{
    /// <summary>
    /// Creates <paramref name="path"/> new for writing. It holds the caller's whole document in a
    /// shared temp directory, so on Unix it is created readable by its owner only (Windows'
    /// per-user temp directory already is).
    /// </summary>
    public static Stream Create(string path, bool asynchronous = false)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0, Options = asynchronous ? FileOptions.Asynchronous : FileOptions.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream file;
        try
        {
            file = new FileStream(path, options);
        }
        catch (Exception ex) when (CodedWriteStream.IsFileSystemFailure(ex))
        {
            throw Failed(path, ex);
        }

        return new CodedWriteStream(file, ex => Failed(path, ex));
    }

    /// <summary>
    /// Removes <paramref name="path"/> if it exists. While another exception is already
    /// propagating (<paramref name="failing"/>) a failure to remove it is swallowed so it can't
    /// mask that exception; otherwise it is <c>PLUME1008</c>.
    /// </summary>
    public static void Delete(string path, bool failing)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (CodedWriteStream.IsFileSystemFailure(ex))
        {
            if (!failing)
            {
                throw new PlumePdfException("PLUME1008", $"Could not remove the temporary copy of the non-seekable input at '{path}' (the output was written): {ex.Message}", ex);
            }
        }
    }

    private static PlumePdfException Failed(string path, Exception ex) =>
        new("PLUME1008", $"Could not create the temporary copy of the non-seekable input at '{path}' (a non-seekable input is spooled to the temp directory first): {ex.Message}", ex);
}
