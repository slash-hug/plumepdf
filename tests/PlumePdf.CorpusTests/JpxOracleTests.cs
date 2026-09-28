using System.Text.Json;
using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The per-sample JPEG 2000 oracle gate: every fixture in
/// <c>Fixtures/jpx/MANIFEST.json</c> with committed <c>opj_decompress</c> PGX references is decoded by
/// <see cref="JpxImageDecoder"/> and compared per component at native precision under its tolerance
/// class (<c>jpx-tolerance.json</c>) — bit-exact for lossless-final-layer 5/3 streams with no
/// irreversible colour step, Tolerance A otherwise; refusal fixtures assert their code; truncated and
/// layer-truncated decodes are pinned by self-consistency, never oracle-compared. Hermetic (references
/// are committed), so it runs in <c>build-test</c>; the fixture enumeration asserts non-empty before
/// iterating (the 2026-08-19 vacuous-lane lesson).
/// </summary>
public class JpxOracleTests
{
    private static readonly string FixturesDir = Path.Combine(CorpusFixture.FixturesRoot, "jpx");

    /// <summary>
    /// The matrix's size, pinned: how many <c>MANIFEST.json</c> entries there are and how many
    /// component planes the oracle-compared ones sum to. A fixture or reference that silently
    /// disappears from the manifest (or a manifest that stops listing a component) would
    /// otherwise shrink this gate without a red run — the 2026-08-19 vacuous-lane lesson at the
    /// granularity of one plane. Regenerating the matrix updates these two numbers in the same
    /// commit as the generator change (<c>scripts/check-jpx-tolerance.sh</c> guards the shrink
    /// direction on the manifest itself).
    /// </summary>
    private const int ExpectedFixtureCount = 45;

    private const int ExpectedComparedPlaneCount = 63;

    /// <summary>
    /// One row of the per-fixture summary this gate prints (fixture, class, worst |Δ|, outlier
    /// fraction, verdict) so a red run reports every fixture's distance from its class at once —
    /// there is deliberately no exclusion list here or in <c>MANIFEST.json</c>: every fixture with
    /// references is compared and must pass its class outright.
    /// </summary>
    private sealed record FixtureResult(string File, string Class, int WorstDiff, double OutlierRate, bool Passed);

    [Fact]
    public void EveryFixture_MatchesReferenceUnderItsToleranceClass()
    {
        using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "MANIFEST.json")));
        var fixtures = manifestDoc.RootElement.GetProperty("fixtures");
        Assert.True(fixtures.GetArrayLength() > 0, "MANIFEST.json's fixture list is empty — the vacuous-lane lesson (2026-08-19).");

        using var toleranceDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixturesDir, "jpx-tolerance.json")));
        var classes = toleranceDoc.RootElement.GetProperty("classes");

        var failures = new List<string>();
        var results = new List<FixtureResult>();
        var planesCompared = 0;

        foreach (var fixture in fixtures.EnumerateArray())
        {
            var file = fixture.GetProperty("file").GetString()!;
            var toleranceClass = fixture.GetProperty("toleranceClass").GetString()!;
            Assert.False(fixture.TryGetProperty("knownGap", out _), $"{file}: MANIFEST.json carries a 'knownGap' entry — the oracle gate has no exclusion mechanism; fix the decoder or leave the fixture red.");
            var data = File.ReadAllBytes(Path.Combine(FixturesDir, file));
            var before = failures.Count;
            var worst = 0;
            var outlierRate = 0.0;

            if (toleranceClass.StartsWith("refusal:", StringComparison.Ordinal))
            {
                CheckRefusal(file, data, toleranceClass["refusal:".Length..], failures);
            }
            else if (toleranceClass == "not-compared:PLUME3701")
            {
                CheckTruncated(file, data, failures);
            }
            else if (toleranceClass == "metadata-only")
            {
                CheckMetadataOnly(file, data, fixture, failures);
            }
            else
            {
                int compared;
                (worst, outlierRate, compared) = CheckOracleCompared(file, data, fixture, toleranceClass, classes, failures);
                planesCompared += compared;
            }

            results.Add(new FixtureResult(file, toleranceClass, worst, outlierRate, failures.Count == before));
        }

        Assert.True(results.Count > 0, "No fixtures were actually exercised.");
        Assert.True(results.Count == ExpectedFixtureCount, $"MANIFEST.json lists {results.Count} fixtures; this gate pins {ExpectedFixtureCount}. A regenerated matrix updates the pin in the same commit; a fixture vanishing is a shrink of the gate.");
        Assert.True(planesCompared == ExpectedComparedPlaneCount, $"{planesCompared} component plane(s) were compared against the oracle; this gate pins {ExpectedComparedPlaneCount}. Fewer means a fixture or reference dropped out of the comparison silently.");

        Console.WriteLine($"{"fixture",-28} {"class",-24} {"worst",5} {"outliers",9} verdict");
        foreach (var r in results)
        {
            Console.WriteLine($"{r.File,-28} {r.Class,-24} {r.WorstDiff,5} {r.OutlierRate,9:P3} {(r.Passed ? "PASS" : "FAIL")}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} JPX oracle mismatch(es):\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// The PDF-side half of the "no spurious degradation" pin:
    /// every committed <c>Fixtures/images/jpx-*.pdf</c> rasterizes without <c>PLUME3710</c> and
    /// without a <c>PLUME7744</c> "degraded" rollup — except the three whose whole point is a
    /// broken payload or an illegal shape (<c>jpx-garbage</c>: not JPEG 2000 → <c>PLUME7744</c>;
    /// <c>jpx-inline-illegal</c>: <c>JPXDecode</c> on an inline image → refused, <c>PLUME7746</c>;
    /// <c>jpx-smask-broken</c>: an unresolvable <c>/SMask</c> → <c>PLUME7744</c>), which must still
    /// record their designed refusal/degradation code. Hermetic (no PDFium): the SSIM lane in
    /// <c>RasterImageOracleTests</c> checks pixels; this checks the verdict the decoder attached
    /// to them, which the SSIM lane never did. The three exclusions are asserted present so the
    /// enumeration cannot go vacuous.
    /// </summary>
    [Fact]
    public void EveryJpxPdfFixture_RasterizesWithoutSpuriousDegradation()
    {
        var imagesDir = Path.Combine(CorpusFixture.FixturesRoot, "images");
        var expectedToDegrade = new HashSet<string>(StringComparer.Ordinal) { "jpx-garbage.pdf", "jpx-inline-illegal.pdf", "jpx-smask-broken.pdf" };
        var fixtures = Directory.GetFiles(imagesDir, "jpx-*.pdf").Select(Path.GetFileName).Select(static n => n!).OrderBy(static n => n, StringComparer.Ordinal).ToList();
        Assert.True(fixtures.Count > expectedToDegrade.Count, "Fixtures/images has no jpx-*.pdf beyond the three degraded-by-design ones — the vacuous-lane lesson (2026-08-19).");
        Assert.True(expectedToDegrade.IsSubsetOf(fixtures), "One of the three degraded-by-design JPX fixtures is missing; the exclusion list would silently shrink this pin.");

        var failures = new List<string>();
        foreach (var name in fixtures)
        {
            using var document = PdfDocument.Open(Path.Combine(imagesDir, name));
            var image = document.Pages[0].Rasterize();
            var codes = image.Diagnostics.Select(static d => d.Code).ToList();
            var degraded = codes.Contains("PLUME7744");
            var truncated = codes.Contains("PLUME3710");

            if (expectedToDegrade.Contains(name))
            {
                if (!degraded && !codes.Contains("PLUME7746"))
                {
                    failures.Add($"{name}: expected PLUME7744 or PLUME7746 (degraded/refused by design) but neither was recorded; diagnostics: [{string.Join(", ", codes)}].");
                }
            }
            else if (degraded || truncated)
            {
                failures.Add($"{name}: well-formed JPX fixture recorded {(truncated ? "PLUME3710" : "PLUME7744")}; diagnostics: [{string.Join(", ", image.Diagnostics.Select(static d => $"{d.Code}/{d.Severity} {d.Message}"))}].");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} JPX PDF fixture(s) carry a spurious degradation verdict:\n" + string.Join("\n", failures));
    }

    private static void CheckRefusal(string file, byte[] data, string expectedCode, List<string> failures)
    {
        try
        {
            JpxImageDecoder.Decode(data, PdfOptions.Default, null);
            failures.Add($"{file}: expected a {expectedCode} refusal but the decode succeeded.");
        }
        catch (PlumePdfException ex) when (ex.Code == expectedCode)
        {
            // Expected.
        }
        catch (Exception ex)
        {
            failures.Add($"{file}: expected {expectedCode}, got {ex.GetType().Name} ({(ex as PlumePdfException)?.Code ?? "n/a"}): {ex.Message}");
        }
    }

    private static void CheckTruncated(string file, byte[] data, List<string> failures)
    {
        var diagnostics = new DiagnosticCollection();
        JpxImage image;
        try
        {
            image = JpxImageDecoder.Decode(data, PdfOptions.Default, diagnostics);
        }
        catch (Exception ex)
        {
            failures.Add($"{file}: expected a partial decode reporting PLUME3701, but threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!diagnostics.Any(d => d.Code == "PLUME3701"))
        {
            failures.Add($"{file}: expected PLUME3701 in diagnostics for a truncated codestream.");
        }

        // Self-consistency: the one complete tile (tile 0) must be bit-exact against
        // the un-truncated source fixture's own oracle reference over that same region. Every
        // plane is compared (one failure line per plane, never a return on the first), and the
        // number of planes actually compared is asserted, so a missing reference file cannot
        // quietly turn this check into a no-op.
        var header = JpxCodestream.Parse(data, PdfOptions.Default, null);
        var tileBounds = JpxGeometry.TileBounds(header.Siz, 0);
        var planesCompared = 0;

        for (var p = 0; p < image.Planes.Length; p++)
        {
            var refPath = Path.Combine(FixturesDir, $"tiles-3x2-partial.ref_{p}.pgx");
            if (!File.Exists(refPath))
            {
                failures.Add($"{file} plane {p}: no source reference '{Path.GetFileName(refPath)}' to compare the complete tile against.");
                continue;
            }

            planesCompared++;
            var pgx = PgxReader.Read(refPath);
            var plane = image.Planes[p];
            var mismatches = 0;
            for (var y = tileBounds.Y0; y < tileBounds.Y1; y++)
            {
                for (var x = tileBounds.X0; x < tileBounds.X1; x++)
                {
                    var index = (y * plane.Width) + x;
                    var mine = plane.Sample(index);
                    var theirs = pgx.Samples[index];
                    if (mine != theirs)
                    {
                        if (mismatches == 0)
                        {
                            failures.Add($"{file} plane {p}: tile 0 pixel ({x},{y}) mine={mine} theirs={theirs} (the one complete tile must be bit-exact).");
                        }

                        mismatches++;
                    }
                }
            }

            if (mismatches > 1)
            {
                failures.Add($"{file} plane {p}: {mismatches} tile-0 sample(s) differ in total.");
            }
        }

        if (planesCompared != image.Planes.Length || planesCompared == 0)
        {
            failures.Add($"{file}: only {planesCompared} of {image.Planes.Length} plane(s) had a source reference to compare against.");
        }
    }

    private static void CheckMetadataOnly(string file, byte[] data, JsonElement fixture, List<string> failures)
    {
        var assert = fixture.GetProperty("assert");
        Jp2Container container;
        try
        {
            container = Jp2Boxes.Parse(data, PdfOptions.Default, null);
        }
        catch (Exception ex)
        {
            failures.Add($"{file}: expected metadata-only parsing to succeed, but threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        switch (file)
        {
            case "res-metadata.jp2":
                if (assert.GetProperty("hasResBox").GetBoolean() && container.Colour.PixelsPerMetre is null)
                {
                    failures.Add($"{file}: expected a 'res ' box (PixelsPerMetre set) but none was recorded.");
                }

                CheckIhdrComponentCount(file, container, assert, failures);
                break;
            case "two-colr.jp2":
                var expectedFirst = assert.GetProperty("colorEnumCS").EnumerateArray().First().GetInt32();
                if (container.Colour.EnumeratedColourSpace != expectedFirst)
                {
                    failures.Add($"{file}: expected the first-recognised colr EnumCS ({expectedFirst}) to win; got {container.Colour.EnumeratedColourSpace?.ToString() ?? "none"}.");
                }

                CheckIhdrComponentCount(file, container, assert, failures);
                break;
            default:
                failures.Add($"{file}: unrecognised metadata-only fixture — no assertion wired for it.");
                break;
        }
    }

    private static void CheckIhdrComponentCount(string file, Jp2Container container, JsonElement assert, List<string> failures)
    {
        var expectedNc = assert.GetProperty("ihdrComponentCount").GetInt32();
        if (container.Ihdr is not { } ihdr || ihdr.NC != expectedNc)
        {
            failures.Add($"{file}: expected ihdr NC={expectedNc}, got {(container.Ihdr is { } h ? h.NC.ToString() : "none")}.");
        }
    }

    /// <summary>Decodes and compares one fixture; returns its worst per-sample |Δ| and worst per-plane outlier rate for the summary table, and how many planes were actually compared (for the gate-wide pin).</summary>
    private static (int WorstDiff, double OutlierRate, int PlanesCompared) CheckOracleCompared(string file, byte[] data, JsonElement fixture, string toleranceClass, JsonElement classes, List<string> failures)
    {
        JpxImage image;
        var diagnostics = new DiagnosticCollection();
        try
        {
            image = JpxImageDecoder.Decode(data, PdfOptions.Default, diagnostics);
        }
        catch (Exception ex)
        {
            failures.Add($"{file}: expected a successful decode ({toleranceClass}), but threw {ex.GetType().Name}: {ex.Message}");
            return (0, 0.0, 0);
        }

        // The "no spurious degradation" pin: every
        // oracle-compared fixture is a well-formed stream opj_decompress decodes silently, so the
        // decoder must not record ANY PLUME37xx deviation on it — the per-sample comparison alone
        // let a PLUME3710 false positive on every 12-bit fixture (and, through the resolver, a
        // PLUME7744 "degraded" rollup on every 12-bit JPX page) go undetected despite bit-exact pixel output.
        // An oracle lane that checks only pixels is blind to a decoder that gets the pixels right
        // and the verdict wrong; this lane now asserts the expected diagnostic set too (empty).
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Code.StartsWith("PLUME37", StringComparison.Ordinal))
            {
                failures.Add($"{file}: '{toleranceClass}' fixture recorded {diagnostic.Code} ({diagnostic.Severity}): {diagnostic.Message} — opj_decompress decodes this stream with no diagnostic; a well-formed fixture must not be flagged as degraded.");
            }
        }

        var referenceNames = fixture.GetProperty("references").EnumerateArray().Select(static e => e.GetString()!).ToArray();
        if (referenceNames.Length == 0)
        {
            failures.Add($"{file}: '{toleranceClass}' fixture has no committed references to compare against.");
            return (0, 0.0, 0);
        }

        if (referenceNames.Length != image.Planes.Length)
        {
            failures.Add($"{file}: {referenceNames.Length} reference(s) but the decode produced {image.Planes.Length} plane(s).");
            return (0, 0.0, 0);
        }

        var (maxAbsFraction, outlierFraction, tightAbsFraction) = ReadTolerance(classes, toleranceClass);
        var worstOverall = 0;
        var worstOutlierRate = 0.0;
        var planesCompared = 0;

        for (var p = 0; p < referenceNames.Length; p++)
        {
            var pgx = PgxReader.Read(Path.Combine(FixturesDir, referenceNames[p]));
            var plane = image.Planes[p];
            if (plane.Width != pgx.Width || plane.Height != pgx.Height)
            {
                failures.Add($"{file} plane {p}: decoded size {plane.Width}x{plane.Height} != reference size {pgx.Width}x{pgx.Height}.");
                continue;
            }

            planesCompared++;

            var maxValue = plane.MaxValue;
            var total = pgx.Samples.Length;
            var outliers = 0;
            var worst = 0;
            var reportedHardFailure = false;
            for (var i = 0; i < total; i++)
            {
                var mine = plane.Sample(i);
                var theirs = pgx.Samples[i];
                var diff = Math.Abs(mine - theirs);
                worst = Math.Max(worst, diff);
                var fraction = (double)diff / maxValue;
                if (fraction > maxAbsFraction + 1e-9 && !reportedHardFailure)
                {
                    var x = i % plane.Width;
                    var y = i / plane.Width;
                    failures.Add($"{file} plane {p}: pixel ({x},{y}) |mine-theirs|={diff} (fraction {fraction:G4}) exceeds maxAbsFraction {maxAbsFraction:G4} for class '{toleranceClass}' (mine={mine}, theirs={theirs}).");
                    reportedHardFailure = true;
                }

                if (fraction > tightAbsFraction)
                {
                    outliers++;
                }
            }

            var outlierRate = total == 0 ? 0.0 : (double)outliers / total;
            if (outlierRate > outlierFraction + 1e-9)
            {
                failures.Add($"{file} plane {p}: outlier rate {outlierRate:P2} (samples beyond tightAbsFraction {tightAbsFraction:G4}) exceeds {outlierFraction:P2} for class '{toleranceClass}' (worst diff {worst}).");
            }

            worstOverall = Math.Max(worstOverall, worst);
            worstOutlierRate = Math.Max(worstOutlierRate, outlierRate);
        }

        return (worstOverall, worstOutlierRate, planesCompared);
    }

    private static (double MaxAbsFraction, double OutlierFraction, double TightAbsFraction) ReadTolerance(JsonElement classes, string toleranceClass)
    {
        if (toleranceClass == "bit-exact")
        {
            return (0.0, 0.0, 0.0);
        }

        var entry = classes.GetProperty(toleranceClass);
        return (entry.GetProperty("maxAbsFraction").GetDouble(), entry.GetProperty("outlierFraction").GetDouble(), entry.GetProperty("tightAbsFraction").GetDouble());
    }
}
