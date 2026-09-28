using PlumePdf.Elements;
using PlumePdf.Layout;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// UCD <c>BidiTest.txt</c>/<c>BidiCharacterTest.txt</c> conformance lane for
/// <see cref="BidiAlgorithm"/> (a third oracle lane — hermetic, gating). Fetched by
/// <c>scripts/fetch-corpora.sh</c>'s UCD stanza, pinned to the same
/// <c>UNICODE_VERSION</c> (16.0.0) <c>scripts/generate-unicode-data.csx</c> pins for
/// <c>UnicodeShapingData.g.cs</c> — self-skips (with a console note, matching
/// every other corpus lane) when not fetched, so the default hermetic test run stays fast; the
/// dedicated <c>corpus</c> CI job runs the fetch first and this lane gates there.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>BidiCharacterTest.txt</c></b> is character-explicit (real codepoints, not symbolic
/// classes) — run directly through <see cref="BidiAlgorithm.GetBidiClass"/>'s real UCD
/// classification, no representative-character substitution needed. Lines containing a
/// codepoint outside the Basic Multilingual Plane are skipped: <see cref="BidiAlgorithm"/>
/// classifies per UTF-16 code unit (documented, pre-existing scope — see
/// <see cref="BidiAlgorithm.GetBidiClass"/>'s own remarks), so a supplementary-plane codepoint's
/// surrogate pair would classify by the surrogate range itself, not the intended character; that
/// is a distinct, already-scoped limitation this lane does not re-litigate.
/// </para>
/// <para>
/// <b><c>BidiTest.txt</c></b> specifies each test case as a sequence of Bidi_Class *names*
/// (<c>L</c>, <c>AL</c>, <c>LRE</c>, ...), not characters — one representative character per
/// class (verified against the real UCD table by <see cref="RepresentativeCharacterClassifiesCorrectly"/>
/// below) stands in for each token, per the file's own documented usage note ("If the
/// implementation allows for a character string as input, randomly pick characters from those
/// with the same Bidi_Class values").
/// </para>
/// <para>
/// <b>Comparison scope (L1 inclusive, L2 via <see cref="BidiAlgorithm.ReorderRunIndices"/>):</b>
/// both files test resolved embedding levels and the resulting left-to-right visual reorder;
/// L3/L4 (mirroring/shaping) are explicitly out of scope for both files by their own header
/// documentation. A position both files mark <c>x</c> (removed by rule X9 — explicit
/// embedding/override/PDF/BN characters) is skipped for both comparisons, exactly as each file's
/// header specifies — <see cref="BidiAlgorithm"/> does not itself remove these characters (it
/// keeps and assigns them a level, the "retain" of the two UAX#9-sanctioned X9 treatments), so
/// this lane filters them out of the derived visual order itself rather than expecting
/// <see cref="BidiAlgorithm"/> to.
/// </para>
/// <para>
/// <b>Cases excluded from the pass/fail gate — two, both pre-existing, both already documented
/// on <see cref="BidiAlgorithm"/> itself before this lane existed, neither a later regression:</b>
/// (1) any case using an explicit embedding/override/isolate character (LRE/RLE/LRO/RLO/PDF or
/// LRI/RLI/FSI/PDI) — <see cref="BidiAlgorithm"/>'s own remarks document the flat (non-X10)
/// weak/neutral/implicit pass as an approximation once such a character crosses an embedding
/// boundary. (2) any case using a paired-bracket character outside <see cref="BidiAlgorithm"/>'s
/// small hand-picked N0 bracket table (ASCII/common-punctuation brackets and guillemets only —
/// see that method's own <c>MirrorPairs</c> remarks) — U+2329/U+232A and U+3008/U+3009 are the
/// two the UCD files exercise. Every excluded case is still counted (see
/// <see cref="ReportAndAssert"/>'s bound check) so a change that silently widened either
/// exclusion would itself fail loudly; this lane does not expand either scope cut on its own
/// authority — both are one-line, well-understood follow-ons for a future ruling, not something
/// to quietly patch mid-review.
/// </para>
/// </remarks>
public class BidiConformanceTests
{
    private const int MaxReportedFailures = 25;

    /// <summary>Caps how many failure messages get formatted/kept (formatting a huge failure count would itself be slow and useless), while still counting every failure so the reported total is honest.</summary>
    private sealed class FailureLog
    {
        private readonly List<string> _messages = [];

        public int Count { get; private set; }

        public void Add(string message)
        {
            Count++;
            if (_messages.Count < MaxReportedFailures)
            {
                _messages.Add(message);
            }
        }

        public override string ToString() => string.Join('\n', _messages) + (Count > _messages.Count ? $"\n... ({Count - _messages.Count} further failures suppressed)" : string.Empty);
    }

    /// <summary>BidiTest.txt tokens for the explicit embedding/override/isolate characters — see this type's remarks on the excluded X10 scope cut.</summary>
    private static readonly HashSet<string> ExplicitFormattingTokens = ["LRE", "RLE", "LRO", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI"];

    /// <summary>BidiCharacterTest.txt codepoints for the same explicit embedding/override/isolate characters, plus the two paired-bracket characters outside <see cref="BidiAlgorithm"/>'s small N0 bracket table that the UCD files happen to exercise — see this type's remarks.</summary>
    private static bool IsOutOfDocumentedScope(int codepoint) =>
        codepoint is (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069) or 0x2329 or 0x232A or 0x3008 or 0x3009;

    private static string UcdRoot => Path.Combine(CorpusFixture.CorporaRoot, "ucd", "16.0.0");

    private static string BidiTestPath => Path.Combine(UcdRoot, "BidiTest.txt");

    private static string BidiCharacterTestPath => Path.Combine(UcdRoot, "BidiCharacterTest.txt");

    private static bool CorpusAvailable => File.Exists(BidiTestPath) && File.Exists(BidiCharacterTestPath);

    /// <summary>One representative character per <see cref="BidiClass"/> value (see this type's remarks), written as <c>\uXXXX</c> casts — never a raw non-ASCII literal — so this source stays byte-exact regardless of editor/terminal encoding, matching <see cref="BidiAlgorithm"/>'s own convention.</summary>
    private static readonly Dictionary<string, char> RepresentativeChar = new()
    {
        ["L"] = (char)0x0061,     // LATIN SMALL LETTER A
        ["R"] = (char)0x05D0,     // HEBREW LETTER ALEF
        ["AL"] = (char)0x0627,    // ARABIC LETTER ALEF
        ["EN"] = (char)0x0035,    // DIGIT FIVE
        ["ES"] = (char)0x002B,    // PLUS SIGN
        ["ET"] = (char)0x0024,    // DOLLAR SIGN
        ["AN"] = (char)0x0660,    // ARABIC-INDIC DIGIT ZERO
        ["CS"] = (char)0x002C,    // COMMA
        ["NSM"] = (char)0x0301,   // COMBINING ACUTE ACCENT
        ["BN"] = (char)0x200B,    // ZERO WIDTH SPACE
        ["B"] = (char)0x2029,     // PARAGRAPH SEPARATOR
        ["S"] = (char)0x0009,     // TAB (CHARACTER TABULATION)
        ["WS"] = (char)0x0020,    // SPACE
        ["ON"] = (char)0x0021,    // EXCLAMATION MARK
        ["LRE"] = (char)0x202A,   // LEFT-TO-RIGHT EMBEDDING
        ["LRO"] = (char)0x202D,   // LEFT-TO-RIGHT OVERRIDE
        ["RLE"] = (char)0x202B,   // RIGHT-TO-LEFT EMBEDDING
        ["RLO"] = (char)0x202E,   // RIGHT-TO-LEFT OVERRIDE
        ["PDF"] = (char)0x202C,   // POP DIRECTIONAL FORMATTING
        ["LRI"] = (char)0x2066,   // LEFT-TO-RIGHT ISOLATE
        ["RLI"] = (char)0x2067,   // RIGHT-TO-LEFT ISOLATE
        ["FSI"] = (char)0x2068,   // FIRST STRONG ISOLATE
        ["PDI"] = (char)0x2069,   // POP DIRECTIONAL ISOLATE
    };

    [Fact]
    public void RepresentativeCharacterClassifiesCorrectly()
    {
        // Guards the BidiTest.txt lane's own premise: if a future Unicode version bump ever
        // reclassified one of these, the whole class-token lane would silently test the wrong
        // thing rather than failing loudly here first.
        foreach (var (name, ch) in RepresentativeChar)
        {
            Assert.True(Enum.TryParse<BidiClass>(name, out var expected), $"'{name}' is not a BidiClass member name.");
            Assert.Equal(expected, BidiAlgorithm.GetBidiClass(ch));
        }
    }

    [Fact]
    public void BidiTest_FullConformance()
    {
        if (!CorpusAvailable)
        {
            Console.WriteLine($"SKIPPED: requires {BidiTestPath} — run scripts/fetch-corpora.sh");
            return;
        }

        var lines = File.ReadAllLines(BidiTestPath);
        // Prove the fetched fixture set is actually non-empty before iterating it.
        Assert.True(lines.Length > 100_000, $"{BidiTestPath} has only {lines.Length} lines — looks truncated or corrupted, not the real UCD file.");

        string[]? levelTokens = null;
        int[]? reorder = null;
        var failures = new FailureLog();
        var casesRun = 0;
        var linesRun = 0;
        var casesExcluded = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("@Levels:", StringComparison.Ordinal))
            {
                levelTokens = Split(line["@Levels:".Length..]);
                continue;
            }

            if (line.StartsWith("@Reorder:", StringComparison.Ordinal))
            {
                var reorderTokens = Split(line["@Reorder:".Length..]);
                reorder = Array.ConvertAll(reorderTokens, int.Parse);
                continue;
            }

            if (line.StartsWith('@'))
            {
                continue; // Forward-compatibility: any other '@' directive is ignored per the file's own header.
            }

            var semicolon = line.LastIndexOf(';');
            Assert.True(semicolon > 0, $"Malformed BidiTest.txt data line (no ';'): {line}");
            Assert.NotNull(levelTokens);
            Assert.NotNull(reorder);

            var tokens = Split(line[..semicolon]);
            var bitset = Convert.ToInt32(line[(semicolon + 1)..].Trim(), 16);
            Assert.True(tokens.Length == levelTokens!.Length, $"Token count {tokens.Length} != @Levels count {levelTokens.Length} for line: {line}");

            linesRun++;
            if (Array.Exists(tokens, ExplicitFormattingTokens.Contains))
            {
                casesExcluded += CountBits(bitset);
                continue; // Documented X10 scope cut — see this type's remarks.
            }

            var text = new string(Array.ConvertAll(tokens, t => RepresentativeChar[t]));
            var excluded = Array.ConvertAll(levelTokens, t => t == "x");

            foreach (var (bit, direction) in DirectionsFor(bitset, text))
            {
                casesRun++;
                var analysis = BidiAlgorithm.Analyze(text, direction);
                CompareLevels(analysis, levelTokens, tokens.Length, $"BidiTest.txt line \"{line}\" bit {bit}", failures);
                CompareReorder(analysis, excluded, reorder!, $"BidiTest.txt line \"{line}\" bit {bit}", failures);

                if (failures.Count > MaxReportedFailures * 4)
                {
                    goto done; // Bail early once we have more than enough detail to act on.
                }
            }
        }

    done:
        Assert.True(linesRun > 400_000, $"Only processed {linesRun} data lines — expected 400,000+; the parser likely broke partway through.");
        Assert.True(casesExcluded > 0, "Expected some BidiTest.txt cases to hit the documented explicit-formatting scope cut — none did; either the corpus changed or this exclusion no longer matches anything (update this type's remarks either way).");
        Assert.True(casesRun > 100_000, $"Only {casesRun} cases were gated after excluding {casesExcluded} — the exclusion looks too broad.");
        ReportAndAssert(failures, casesRun, "BidiTest.txt");
    }

    private static int CountBits(int bitset) => ((bitset & 1) != 0 ? 1 : 0) + ((bitset & 2) != 0 ? 1 : 0) + ((bitset & 4) != 0 ? 1 : 0);

    [Fact]
    public void BidiCharacterTest_FullConformance()
    {
        if (!CorpusAvailable)
        {
            Console.WriteLine($"SKIPPED: requires {BidiCharacterTestPath} — run scripts/fetch-corpora.sh");
            return;
        }

        var lines = File.ReadAllLines(BidiCharacterTestPath);
        Assert.True(lines.Length > 50_000, $"{BidiCharacterTestPath} has only {lines.Length} lines — looks truncated or corrupted, not the real UCD file.");

        var failures = new FailureLog();
        var casesRun = 0;
        var casesExcluded = 0;
        var skippedSupplementary = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split(';');
            Assert.True(fields.Length == 5, $"Malformed BidiCharacterTest.txt line (expected 5 fields): {line}");

            var codepointTokens = Split(fields[0]);
            var codepoints = Array.ConvertAll(codepointTokens, t => Convert.ToInt32(t, 16));
            if (Array.Exists(codepoints, cp => cp > 0xFFFF))
            {
                skippedSupplementary++;
                continue; // Documented BMP-only scope for this char-granularity lane; see this type's remarks.
            }

            if (Array.Exists(codepoints, IsOutOfDocumentedScope))
            {
                casesExcluded++;
                continue; // Documented X10/N0-bracket-table scope cuts — see this type's remarks.
            }

            var text = new string(Array.ConvertAll(codepoints, cp => (char)cp));
            var directionField = int.Parse(fields[1]);
            var expectedParagraphLevel = int.Parse(fields[2]);
            var expectedLevelTokens = Split(fields[3]);
            var expectedReorder = fields[4].Trim().Length == 0 ? [] : Array.ConvertAll(Split(fields[4]), int.Parse);

            var direction = directionField switch
            {
                0 => TextDirection.LeftToRight,
                1 => TextDirection.RightToLeft,
                2 => BidiAlgorithm.FirstStrongParagraphLevel(text) == 1 ? TextDirection.RightToLeft : TextDirection.LeftToRight,
                _ => throw new InvalidOperationException($"Unknown paragraph direction field '{directionField}' in: {line}"),
            };

            casesRun++;
            var analysis = BidiAlgorithm.Analyze(text, direction);
            var description = $"BidiCharacterTest.txt line \"{line}\"";

            if (analysis.ParagraphLevel != expectedParagraphLevel)
            {
                AddFailure(failures, $"{description}: paragraph level expected {expectedParagraphLevel}, got {analysis.ParagraphLevel}.");
            }

            var excluded = Array.ConvertAll(expectedLevelTokens, t => t == "x");
            CompareLevels(analysis, expectedLevelTokens, codepoints.Length, description, failures);
            CompareReorder(analysis, excluded, expectedReorder, description, failures);

            if (failures.Count > MaxReportedFailures * 4)
            {
                break;
            }
        }

        Assert.True(casesExcluded > 0, "Expected some BidiCharacterTest.txt cases to hit the documented explicit-formatting/bracket-table scope cuts — none did; either the corpus changed or this exclusion no longer matches anything (update this type's remarks either way).");
        Assert.True(casesRun > 50_000, $"Only processed {casesRun} cases (skipped {skippedSupplementary} supplementary-plane, {casesExcluded} scope-cut lines) — expected 50,000+; the parser likely broke partway through or the exclusion is too broad.");
        ReportAndAssert(failures, casesRun, "BidiCharacterTest.txt");
    }

    private static IEnumerable<(int Bit, TextDirection Direction)> DirectionsFor(int bitset, string text)
    {
        if ((bitset & 1) != 0)
        {
            yield return (1, BidiAlgorithm.FirstStrongParagraphLevel(text) == 1 ? TextDirection.RightToLeft : TextDirection.LeftToRight);
        }

        if ((bitset & 2) != 0)
        {
            yield return (2, TextDirection.LeftToRight);
        }

        if ((bitset & 4) != 0)
        {
            yield return (4, TextDirection.RightToLeft);
        }
    }

    private static void CompareLevels(BidiAnalysis analysis, IReadOnlyList<string> expectedTokens, int length, string description, FailureLog failures)
    {
        for (var i = 0; i < length; i++)
        {
            if (expectedTokens[i] == "x")
            {
                continue;
            }

            var expected = int.Parse(expectedTokens[i]);
            if (analysis.Levels[i] != expected)
            {
                AddFailure(failures, $"{description}: level[{i}] expected {expected}, got {analysis.Levels[i]}.");
            }
        }
    }

    private static void CompareReorder(BidiAnalysis analysis, bool[] excluded, IReadOnlyList<int> expectedReorder, string description, FailureLog failures)
    {
        var actual = VisualOrderExcluding(analysis, excluded);
        if (!actual.SequenceEqual(expectedReorder))
        {
            AddFailure(failures, $"{description}: reorder expected [{string.Join(' ', expectedReorder)}], got [{string.Join(' ', actual)}].");
        }
    }

    /// <summary>
    /// Expands <see cref="BidiAlgorithm.ReorderRunIndices"/>'s run-granularity L2 reorder to
    /// character granularity (each run's own characters walk forward for an LTR run, backward
    /// for an RTL run — the same expansion <c>TextLayouter.ShapeVisualLine</c>'s remarks
    /// describe), then maps back to original indices — exactly matching how both UCD test files
    /// define their own <c>@Reorder</c>/field-4 visual-order lists.
    /// </summary>
    /// <remarks>
    /// Positions <paramref name="excluded"/> marks (rule X9's removed characters) are dropped
    /// from the level array BEFORE run detection, not filtered out of the result afterward: X9
    /// removal can make two runs that look separate in the full (unfiltered) level array
    /// logically adjacent once the character between them is gone, and only detecting runs on
    /// the post-removal sequence reverses them as one contiguous run — exactly what both
    /// reference files' own "removed" semantics require. This is a test-harness concern, not a
    /// <see cref="BidiAlgorithm"/> one: production callers (<c>TextLayouter</c>) never carry an
    /// invisible X9-removed character into the glyph sequence being reordered in the first place.
    /// </remarks>
    private static List<int> VisualOrderExcluding(BidiAnalysis analysis, bool[] excluded)
    {
        var filteredLevels = new List<int>(excluded.Length);
        var originalIndex = new List<int>(excluded.Length);
        for (var i = 0; i < excluded.Length; i++)
        {
            if (!excluded[i])
            {
                filteredLevels.Add(analysis.Levels[i]);
                originalIndex.Add(i);
            }
        }

        var runs = BidiAlgorithm.GetLevelRuns(filteredLevels);
        var runOrder = BidiAlgorithm.ReorderRunIndices(runs);
        var result = new List<int>(filteredLevels.Count);

        foreach (var runIndex in runOrder)
        {
            var run = runs[runIndex];
            if (run.IsRightToLeft)
            {
                for (var i = run.Start + run.Length - 1; i >= run.Start; i--)
                {
                    result.Add(originalIndex[i]);
                }
            }
            else
            {
                for (var i = run.Start; i < run.Start + run.Length; i++)
                {
                    result.Add(originalIndex[i]);
                }
            }
        }

        return result;
    }

    private static string[] Split(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static void AddFailure(FailureLog failures, string message) => failures.Add(message);

    private static void ReportAndAssert(FailureLog failures, int casesRun, string fileName)
    {
        Assert.True(failures.Count == 0,
            $"{fileName}: {failures.Count} failing assertions out of {casesRun} cases run.\n{failures}");
    }
}
