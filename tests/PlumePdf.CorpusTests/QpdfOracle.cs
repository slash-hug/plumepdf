using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The shared <c>qpdf --check</c> oracle: one probe, one runner and the two assertions the
/// qpdf suites make. qpdf is an independent, reference-grade implementation that is executed
/// only, never a porting source (the clean-room policy in AGENTS.md).
/// </summary>
/// <remarks>
/// Follows the armed-lane shape <see cref="DjpegInteropTests"/> established: a suite no-ops
/// when qpdf is not installed, UNLESS <c>PLUMEPDF_REQUIRE_QPDF=1</c> is set (the corpus CI job
/// installs qpdf and exports it), in which case an absent tool is a test FAILURE, never a
/// skip — so an unarmed lane can never read as green.
/// </remarks>
internal static class QpdfOracle
{
    private static readonly bool Available = Probe();

    private static bool Probe()
    {
        // House rule (the VeraPdfInteropTests/PdfsigInteropTests precedent): the probe inspects
        // output, never the exit code alone — an unrelated binary that happens to be named
        // "qpdf" must not arm the lane. `qpdf --version` prints "qpdf version <x.y.z>".
        var (started, _, stdout, stderr) = ExternalTool.TryRun("qpdf", "--version", timeoutMilliseconds: 10_000);
        return started && (stdout + stderr).Contains("qpdf", StringComparison.Ordinal);
    }

    /// <summary>Whether qpdf is installed; fails the calling test instead when <c>PLUMEPDF_REQUIRE_QPDF=1</c> and it is not.</summary>
    internal static bool AvailableOrFailIfRequired()
    {
        if (Available)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_QPDF") == "1")
        {
            Assert.Fail("PLUMEPDF_REQUIRE_QPDF=1 but the qpdf probe found no working tool - this lane installed qpdf and expects it armed; a self-skip here would silently disable the qpdf --check gates.");
        }

        return false;
    }

    /// <summary>Runs <c>qpdf --check</c> on <paramref name="path"/>.</summary>
    internal static (int ExitCode, string Output) Check(string path)
    {
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun("qpdf", $"--check \"{path}\"", timeoutMilliseconds: 30_000);
        Assert.True(started, "qpdf failed to start after probing available.");
        return (exitCode, stdout + stderr);
    }

    /// <summary>qpdf reports no problems: exit code 0 (qpdf exits 3 on warnings, 2 on errors).</summary>
    internal static void AssertClean(string path)
    {
        var (exitCode, output) = Check(path);
        Assert.True(exitCode == 0, $"qpdf --check reported problems (exit {exitCode}):\n{output}");
    }

    /// <summary>
    /// The linearization bar: exit code 0, an explicit "File is linearized" line, and not a
    /// single WARNING/ERROR. qpdf recomputes the whole layout (page object groups, <c>/E</c>,
    /// <c>/T</c>, hint-table contents) and any disagreement with the hint stream surfaces as a
    /// warning, so "no warnings" asserts the hint tables are semantically right, not merely
    /// parseable.
    /// </summary>
    internal static void AssertValidLinearization(string path)
    {
        var (exitCode, output) = Check(path);
        Assert.Contains("File is linearized", output, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", output, StringComparison.Ordinal);
        Assert.True(exitCode == 0, $"qpdf --check reported problems (exit {exitCode}):\n{output}");
    }
}
