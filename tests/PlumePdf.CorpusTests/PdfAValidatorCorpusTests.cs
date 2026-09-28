using PlumePdf.Documents.PdfA;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// TDD's <see cref="PdfAValidator"/> against the labelled veraPDF-corpus fixtures (Phase 6):
/// every fixture under a clause directory this self-check claims to cover (see
/// <see cref="CoveredClauses"/>) must classify correctly — a <c>-pass-</c> fixture's
/// clause-tagged findings must all be <see cref="PdfARuleStatus.Pass"/>, a <c>-fail-</c>
/// fixture must carry at least one <see cref="PdfARuleStatus.Fail"/> among its clause-tagged
/// findings. Every clause directory NOT in <see cref="CoveredClauses"/> is swept separately
/// (<see cref="UncoveredClauseFixtures_AreReportedNotChecked"/>) to prove PlumePDF never
/// silently reports green on a clause it does not implement.
///
/// Skips (produces zero theory cases) when <c>corpora/veraPDF-corpus-master</c> hasn't been
/// fetched, so the default hermetic lane stays green; the dedicated <c>corpus</c> CI job runs
/// <c>scripts/fetch-corpora.sh</c> first.
/// </summary>
public class PdfAValidatorCorpusTests
{
    // Clause directory -> (relative path under corpora/veraPDF-corpus-master, the RuleId
    // *prefix* this self-check evaluates for that clause). Matched by RuleId rather than the
    // finding's own (informational, best-effort) ClauseId: PdfAIdentification's declared-part
    // detection is exactly what several of 6.6.4's fail fixtures deliberately break (wrong
    // namespace/prefix/missing part) — the finding itself can't always attribute a clause id
    // in that case, but it always carries the stable RuleId "PdfAIdentification" regardless.
    // Deliberately excludes PDF_A-1b's "6.7.11" sibling clause: one of its four fail fixtures
    // (t04) tests whether a pdfaid:corr *value* is a real published ISO 19005-1 corrigendum —
    // a check this self-check does not attempt (see PdfAValidator's KnownPdfaidProperties
    // remark), so that whole clause directory cannot be honestly claimed as covered.
    private static readonly (string RuleIdPrefix, string RelativePath)[] CoveredClauses =
    [
        ("ForbiddenFilters", "PDF_A-1b/6.1 File structure/6.1.10 Filters"),
        ("DocInfoXmpAgreement", "PDF_A-1b/6.7 Metadata/6.7.3 Document information dictionary"),
        ("PdfAIdentification", "PDF_A-2b/6.6 Metadata/6.6.4 Version and conformance level identification"),
    ];

    // Every other clause directory that exists in the corpus but this self-check does not
    // implement — swept by UncoveredClauseFixtures_AreNeverReportedPass to prove the "never
    // silently green" contract. Not exhaustive of every subclause in the corpus (that would
    // duplicate the corpus's own directory listing); a representative sample per top-level
    // clause is enough to prove the contract holds. Includes "6.7.11" (see remark above).
    private static readonly (string ClauseId, string RelativePath)[] UncoveredClauseSamples =
    [
        ("6.2.2", "PDF_A-1b/6.2 Graphics/6.2.2 Output intent"),
        ("6.3.5", "PDF_A-1b/6.3 Fonts/6.3.5 Font subsets"),
        ("6.9", "PDF_A-1b/6.9 Interactive Forms"),
        ("6.7.11", "PDF_A-1b/6.7 Metadata/6.7.11 Version and conformance level identification"),
        ("6.2.3", "PDF_A-2b/6.2 Graphics/6.2.3 Output intent"),
    ];

    public static TheoryData<string, string> PassFixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var (ruleIdPrefix, relativePath) in CoveredClauses)
        {
            foreach (var file in EnumerateFixtures(relativePath, "-pass-"))
            {
                data.Add(ruleIdPrefix, file);
            }
        }

        AddHermeticSentinelOrThrowIfVacuous(data, nameof(PassFixtures));
        return data;
    }

    public static TheoryData<string, string> FailFixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var (ruleIdPrefix, relativePath) in CoveredClauses)
        {
            foreach (var file in EnumerateFixtures(relativePath, "-fail-"))
            {
                data.Add(ruleIdPrefix, file);
            }
        }

        AddHermeticSentinelOrThrowIfVacuous(data, nameof(FailFixtures));
        return data;
    }

    /// <summary>
    /// The anti-vacuity gate every fixture-set provider in this class routes through: when the
    /// corpus is fetched but a provider matched ZERO files (a renamed clause directory, a
    /// changed corpus layout), the suite must fail loudly — an empty theory set silently passes
    /// having validated nothing, which is indistinguishable from green. Only the truly hermetic
    /// case (corpus not fetched at all) gets the empty-string sentinel row the theories
    /// early-return on.
    /// </summary>
    private static void AddHermeticSentinelOrThrowIfVacuous(TheoryData<string, string> data, string providerName)
    {
        if (data.Count > 0)
        {
            return;
        }

        if (CorpusFixture.CorporaAvailable)
        {
            throw new InvalidOperationException(
                $"{providerName}: corpora/veraPDF-corpus-master is fetched but no fixtures matched — the corpus layout or a clause directory name changed, and this suite would otherwise pass having validated zero files.");
        }

        data.Add(string.Empty, string.Empty);
    }

    [Theory]
    [MemberData(nameof(PassFixtures))]
    public void PassFixture_HasNoFailingFindingForItsRule(string ruleIdPrefix, string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        using var document = OpenLeniently(path);
        var result = PdfAValidator.Validate(document);

        var ruleFindings = result.Findings.Where(f => f.RuleId == ruleIdPrefix || f.RuleId.StartsWith(ruleIdPrefix + ".", StringComparison.Ordinal)).ToList();
        var failing = ruleFindings.Where(f => f.Status == PdfARuleStatus.Fail).ToList();
        Assert.True(failing.Count == 0, $"{path}: expected no Fail findings for {ruleIdPrefix}, got: {string.Join("; ", failing.Select(f => $"{f.RuleId}: {f.Message}"))}");
    }

    [Theory]
    [MemberData(nameof(FailFixtures))]
    public void FailFixture_HasAtLeastOneFailingFindingForItsRule(string ruleIdPrefix, string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        using var document = OpenLeniently(path);
        var result = PdfAValidator.Validate(document);

        var ruleFindings = result.Findings.Where(f => f.RuleId == ruleIdPrefix || f.RuleId.StartsWith(ruleIdPrefix + ".", StringComparison.Ordinal)).ToList();
        Assert.True(ruleFindings.Any(f => f.Status == PdfARuleStatus.Fail), $"{path}: expected at least one Fail finding for {ruleIdPrefix}, got: {string.Join("; ", ruleFindings.Select(f => $"{f.RuleId}={f.Status}"))}");
    }

    public static TheoryData<string, string> UncoveredClauseFixturePaths()
    {
        var data = new TheoryData<string, string>();
        foreach (var (clauseId, relativePath) in UncoveredClauseSamples)
        {
            foreach (var file in EnumerateFixtures(relativePath, marker: null))
            {
                data.Add(clauseId, file);
            }
        }

        AddHermeticSentinelOrThrowIfVacuous(data, nameof(UncoveredClauseFixturePaths));
        return data;
    }

    [Theory]
    [MemberData(nameof(UncoveredClauseFixturePaths))]
    public void UncoveredClauseFixtures_AreNeverReportedPass(string clauseId, string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        using var document = OpenLeniently(path);
        var result = PdfAValidator.Validate(document);

        // Never silently green: this self-check must emit no finding at all tagged with THIS
        // uncovered clause id, since PdfAValidator only ever assigns a ClauseId for a clause it
        // actually claims coverage of.
        var claimedThisClause = result.Findings.Where(f => f.ClauseId == clauseId).ToList();
        Assert.True(claimedThisClause.Count == 0, $"{path}: PdfAValidator emitted a finding tagged with uncovered clause {clauseId}: {string.Join("; ", claimedThisClause.Select(f => $"{f.RuleId}={f.Status}"))}");
    }

    private static IEnumerable<string> EnumerateFixtures(string relativeClausePath, string? marker)
    {
        var directory = Path.Combine(CorpusFixture.CorporaRoot, "veraPDF-corpus-master", relativeClausePath);
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.pdf", SearchOption.AllDirectories))
        {
            if (marker is null || file.Contains(marker, StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    private static PdfDocument OpenLeniently(string path) => PdfDocument.Open(path);

    // Robustness sweep, independent of the clause-by-clause TDD above: PdfAValidator.Validate
    // must never throw over any fixture in the corpus's PDF_A-1b/PDF_A-2b trees — including
    // the ~500 fixtures outside CoveredClauses this self-check does not implement — since a
    // caller (or the corpus CI lane's own verify sweep) can hand it any
    // candidate PDF/A file, conformant or not, well-formed or not.
    public static TheoryData<string> AllPdfA1bAnd2bFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in EnumerateFixtures("PDF_A-1b", marker: null).Concat(EnumerateFixtures("PDF_A-2b", marker: null)))
        {
            data.Add(file);
        }

        if (data.Count == 0)
        {
            if (CorpusFixture.CorporaAvailable)
            {
                throw new InvalidOperationException(
                    $"{nameof(AllPdfA1bAnd2bFixtures)}: corpora/veraPDF-corpus-master is fetched but the PDF_A-1b/PDF_A-2b trees matched no fixtures — the corpus layout changed, and this sweep would otherwise pass having validated zero files.");
            }

            data.Add(string.Empty);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPdfA1bAnd2bFixtures))]
    public void Validate_NeverThrows(string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        using var document = OpenLeniently(path);
        var result = PdfAValidator.Validate(document);
        Assert.NotEmpty(result.Findings);
    }
}
