using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Proves the committed JPX fixture matrix is what the pinned OpenJPEG 2.5.4 oracle produces (spec
/// §3.5): probes <c>~/jpx-oracle/bin/opj_decompress</c> (self-skips when absent unless
/// <c>PLUMEPDF_REQUIRE_OPJ=1</c>, the <see cref="PdfiumOracle"/> armed-lane shape), re-decodes every
/// fixture with a committed PGX reference into the scratch directory and byte-compares with it, and
/// separately re-runs <c>scripts/generate-jpx-fixtures.sh --check</c>'s structural assertions — that
/// half needs only python3 (no OpenJPEG invocation), so it always runs.
/// </summary>
/// <remarks>
/// The unarmed self-skip reports as Passed with a <c>SKIPPED</c> console line, not as an xUnit
/// Skipped result: xUnit 2 has no dynamic skip without the <c>Xunit.SkippableFact</c> package, which
/// this repository does not take (the same shape every <see cref="PdfiumOracle"/>-gated test uses).
/// The lane that matters — CI's <c>corpus</c> job — sets <c>PLUMEPDF_REQUIRE_OPJ=1</c>, where an
/// absent oracle is a failure, never a pass; locally, the console line is the tell.
/// </remarks>
public class JpxFixtureFreshnessTests
{
    private static readonly string FixturesDir = Path.Combine(CorpusFixture.FixturesRoot, "jpx");

    private static string OpjDecompressPath =>
        Environment.GetEnvironmentVariable("PLUMEPDF_OPJ_DECOMPRESS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "jpx-oracle", "bin", "opj_decompress");

    private static readonly bool OpjAvailable = ProbeOpj();

    private static bool ProbeOpj()
    {
        var path = OpjDecompressPath;
        if (!File.Exists(path) && !LooksLikeBareCommand(path))
        {
            return false;
        }

        var (started, _, stdout, stderr) = ExternalTool.TryRun(path, "-h", timeoutMilliseconds: 10_000);
        return started && (stdout + stderr).Contains("opj_decompress", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBareCommand(string path) => !path.Contains(Path.DirectorySeparatorChar);

    private static bool OpjAvailableOrFailIfRequired()
    {
        if (OpjAvailable)
        {
            return true;
        }

        if (Environment.GetEnvironmentVariable("PLUMEPDF_REQUIRE_OPJ") == "1")
        {
            Assert.Fail($"PLUMEPDF_REQUIRE_OPJ=1 but no working opj_decompress was found at {OpjDecompressPath} — this lane installs the pinned oracle (scripts/install-jpx-oracle.sh) and expects it armed; a self-skip here would silently disable the fixture-freshness gate.");
        }

        return false;
    }

    [Fact]
    public void CommittedReferences_MatchPinnedOracle()
    {
        if (!OpjAvailableOrFailIfRequired())
        {
            Console.WriteLine("SKIPPED (requires opj_decompress — run scripts/install-jpx-oracle.sh, or install OpenJPEG 2.5.4)");
            return;
        }

        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "MANIFEST.json")));
        var fixtures = manifestDoc.RootElement.GetProperty("fixtures");
        Assert.True(fixtures.GetArrayLength() > 0, "MANIFEST.json's fixture list is empty.");

        var scratchDir = Path.Combine(Path.GetTempPath(), "plumepdf-jpx-freshness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchDir);
        try
        {
            var checkedAny = false;
            var failures = new List<string>();

            foreach (var fixture in fixtures.EnumerateArray())
            {
                var file = fixture.GetProperty("file").GetString()!;
                var references = fixture.GetProperty("references").EnumerateArray().Select(static e => e.GetString()!).ToArray();
                if (references.Length == 0)
                {
                    continue;
                }

                var fixturePath = Path.Combine(FixturesDir, file);
                if (fixture.TryGetProperty("referenceSource", out var source) && source.GetString() == "codestream")
                {
                    // MANIFEST "referenceSource": "codestream" — the references were decoded from
                    // the bare codestream inside the JP2, not the JP2 file (opj_decompress applies
                    // its own colour conversion to some JP2 colour boxes — CMYK → RGB for EnumCS
                    // 12 — which would leave fewer planes than the codestream has components).
                    // Extract the same jp2c payload the generator used and decode that instead.
                    var container = PlumePdf.Filters.Jpx.Jp2Boxes.Parse(File.ReadAllBytes(fixturePath), PdfOptions.Default, null);
                    fixturePath = Path.Combine(scratchDir, Path.GetFileNameWithoutExtension(file) + ".codestream.j2k");
                    File.WriteAllBytes(fixturePath, container.Codestream.ToArray());
                }

                var outputTemplate = Path.Combine(scratchDir, Path.GetFileNameWithoutExtension(file) + ".pgx");
                // -upsample matches scripts/jpx-fixtures/generate.py's own invocation: without
                // it, a sub-sampled fixture's freshly-decoded PGX components come out at their
                // native (smaller) size instead of the committed references' reference-grid
                // size, byte-comparing as "stale" even when nothing has actually changed.
                var (started, exitCode, stdout, stderr) = ExternalTool.TryRun(
                    OpjDecompressPath,
                    $"-i \"{fixturePath}\" -o \"{outputTemplate}\" -upsample",
                    timeoutMilliseconds: 30_000);

                if (!started || exitCode != 0)
                {
                    failures.Add($"{file}: opj_decompress failed (started={started}, exit={exitCode}): {stdout}{stderr}");
                    continue;
                }

                checkedAny = true;

                // An under-committed reference list (fewer PGX files in MANIFEST than components
                // opj_decompress writes) would silently compare fewer planes than the decode
                // produces — the count must match exactly, not just "every listed one exists".
                var producedCount = Directory.GetFiles(scratchDir, $"{Path.GetFileNameWithoutExtension(file)}_*.pgx").Length;
                if (producedCount != references.Length)
                {
                    failures.Add($"{file}: opj_decompress produced {producedCount} PGX plane(s) but MANIFEST.json lists {references.Length} reference(s) — the committed reference list is incomplete or stale (re-run scripts/generate-jpx-fixtures.sh).");
                }

                for (var p = 0; p < references.Length; p++)
                {
                    // opj_decompress always names PGX output "<template>_<n>.pgx", even for a
                    // single-component image (verified against the pinned oracle: never bare
                    // "<template>.pgx") — the same "_0", "_1", ... suffix the committed
                    // references (Fixtures/jpx/*.ref_N.pgx) encode in their own names.
                    var producedPath = Path.Combine(scratchDir, $"{Path.GetFileNameWithoutExtension(file)}_{p}.pgx");

                    if (!File.Exists(producedPath))
                    {
                        failures.Add($"{file}: expected opj_decompress to produce '{Path.GetFileName(producedPath)}' but it was not found (stdout/stderr: {stdout}{stderr}).");
                        continue;
                    }

                    var committedPath = Path.Combine(FixturesDir, references[p]);
                    var toleranceClass = fixture.GetProperty("toleranceClass").GetString() ?? string.Empty;
                    if (toleranceClass == "bit-exact")
                    {
                        // Reversible 5/3 output is integer-exact in every conforming decoder, so the
                        // committed reference must match the pinned oracle byte for byte on any platform.
                        if (!BytesEqual(producedPath, committedPath))
                        {
                            failures.Add($"{file}: freshly-decoded '{Path.GetFileName(producedPath)}' differs byte-for-byte from committed '{references[p]}' — the fixture matrix is stale (re-run scripts/generate-jpx-fixtures.sh).");
                        }
                    }
                    else
                    {
                        // Irreversible (9/7, ICT, sYCC) output is float arithmetic inside OpenJPEG itself
                        // and is NOT byte-stable across platforms/compilers: the committed references were
                        // produced on macOS arm64 and the CI runner is linux x86_64 — the first armed CI
                        // run (main b8e4322) showed exactly the five irreversible fixtures differing by a
                        // few LSBs. That is the reason Tolerance A exists at all, so freshness for these
                        // is "the oracle still agrees with the committed reference within the class
                        // tolerance", never byte identity.
                        var mine = PgxReader.Read(producedPath);
                        var theirs = PgxReader.Read(committedPath);
                        if (mine.Width != theirs.Width || mine.Height != theirs.Height || mine.Precision != theirs.Precision || mine.Signed != theirs.Signed)
                        {
                            failures.Add($"{file}: '{Path.GetFileName(producedPath)}' shape {mine.Width}x{mine.Height}/{mine.Precision}{(mine.Signed ? "s" : "u")} differs from committed '{references[p]}' {theirs.Width}x{theirs.Height}/{theirs.Precision}{(theirs.Signed ? "s" : "u")} — the fixture matrix is stale.");
                            continue;
                        }

                        var fullScale = (1 << mine.Precision) - 1;
                        var maxAllowed = Math.Max(1, (int)Math.Ceiling(4.0 / 255.0 * fullScale)); // Tolerance A's maxAbsFraction
                        var tight = Math.Max(1, (int)Math.Ceiling(2.0 / 255.0 * fullScale));      // Tolerance A's tightAbsFraction
                        var worst = 0;
                        var outliers = 0;
                        for (var i = 0; i < mine.Samples.Length; i++)
                        {
                            var d = Math.Abs(mine.Samples[i] - theirs.Samples[i]);
                            worst = Math.Max(worst, d);
                            if (d > tight)
                            {
                                outliers++;
                            }
                        }

                        if (worst > maxAllowed || outliers > mine.Samples.Length * 0.001)
                        {
                            failures.Add($"{file}: freshly-decoded '{Path.GetFileName(producedPath)}' vs committed '{references[p]}': worst |Δ| {worst} (limit {maxAllowed}), {outliers} of {mine.Samples.Length} samples beyond {tight} — beyond the cross-platform variance Tolerance A allows; the fixture matrix is stale or the oracle build changed.");
                        }
                    }
                }
            }

            Assert.True(checkedAny, "No fixture with a committed reference was actually re-decoded.");
            Assert.True(failures.Count == 0, $"{failures.Count} stale/mismatched JPX reference(s):\n" + string.Join("\n", failures));
        }
        finally
        {
            try
            {
                Directory.Delete(scratchDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort scratch cleanup; a leftover temp directory is not a test failure.
            }
        }
    }

    [Fact]
    public void FixtureMatrix_PassesStructuralSelfCheck()
    {
        // "--check" re-verifies every MANIFEST.json 'assert' block and every sha256 against the
        // already-committed fixture bytes — no OpenJPEG invocation, so this always runs
        // regardless of PLUMEPDF_REQUIRE_OPJ.
        var (started, exitCode, stdout, stderr) = ExternalTool.TryRun(
            "bash",
            $"\"{Path.Combine(CorpusFixture.RepoRoot, "scripts", "generate-jpx-fixtures.sh")}\" --check",
            timeoutMilliseconds: 60_000);

        Assert.True(started, "Failed to start scripts/generate-jpx-fixtures.sh --check.");
        Assert.True(exitCode == 0, $"scripts/generate-jpx-fixtures.sh --check failed (exit {exitCode}):\n{stdout}\n{stderr}");
    }

    private static bool BytesEqual(string pathA, string pathB)
    {
        using var hashA = SHA256.Create();
        using var hashB = SHA256.Create();
        var a = hashA.ComputeHash(File.ReadAllBytes(pathA));
        var b = hashB.ComputeHash(File.ReadAllBytes(pathB));
        return a.AsSpan().SequenceEqual(b);
    }
}
