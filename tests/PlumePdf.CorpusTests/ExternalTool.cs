using System.Diagnostics;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The one process runner every external-oracle interop suite (<c>verapdf</c>, <c>qpdf</c>,
/// <c>pdfsig</c>/<c>certutil</c>) shares. Exists so the pipe-draining discipline is uniform:
/// stderr is drained <em>concurrently</em> with stdout (async read started first), because
/// reading the two redirected pipes sequentially deadlocks the moment a chatty child fills
/// the un-drained pipe's OS buffer — child blocks writing stderr, parent blocks reading
/// stdout, and the CI job burns its whole <c>timeout-minutes</c> doing nothing.
/// </summary>
internal static class ExternalTool
{
    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> and drains both
    /// output pipes without deadlocking. <c>Started == false</c> means the binary could not
    /// be launched at all (absent tool — the probe/self-skip signal); a started-but-hung
    /// child surfaces via <paramref name="timeoutMilliseconds"/>.
    /// </summary>
    internal static (bool Started, int ExitCode, string Stdout, string Stderr) TryRun(
        string fileName, string arguments, int timeoutMilliseconds = 60_000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return (false, -1, string.Empty, string.Empty);
            }

            // stderr first, async — see the type summary for why order matters here.
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = stderrTask.GetAwaiter().GetResult();
            process.WaitForExit(timeoutMilliseconds);
            return (true, process.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, -1, string.Empty, ex.Message);
        }
    }
}
