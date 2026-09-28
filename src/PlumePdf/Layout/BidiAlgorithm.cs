using PlumePdf.Elements;
using ShapingBidiClass = PlumePdf.Fonts.Shaping.BidiClass;
using UnicodeShapingData = PlumePdf.Fonts.Shaping.UnicodeShapingData;

namespace PlumePdf.Layout;

/// <summary>
/// The Unicode Bidirectional Algorithm (UAX#9), scoped to what Phase 6.5's exit bar needs:
/// paragraph-level direction detection (P2/P3), explicit embeddings and isolates (X1-X8), weak
/// and neutral type resolution (W1-W7, N0-N2), implicit level resolution (I1/I2), the level
/// reset (L1) and the reorder used to paint a line in visual order (L2), plus mirrored-glyph
/// lookup (L4). <see cref="TextLayouter"/> calls this once per wrapped line, using the
/// paragraph-wide base direction <see cref="TextLayouter.ResolveDirection"/> resolves once per
/// <see cref="Elements.Text"/> element (never per line — UAX#9 P2/P3 is a paragraph-scoped
/// decision, and re-detecting it per wrapped line could disagree line to line).
/// </summary>
/// <remarks>
/// <para>
/// <b>Character classification:</b> <see cref="GetBidiClass"/> below looks up
/// Bidi_Class from <c>UnicodeShapingData.g.cs</c> — the pinned-Unicode-version table generated
/// in the Fonts layer — rather than hand-classifying ranges (a drop-in swap for an earlier,
/// hand-authored interim table, with no change to any caller).
/// Every non-ASCII code point in this file is still written as a <c>\uXXXX</c> escape, never a raw
/// character, so the source stays byte-exact regardless of editor/terminal encoding.
/// </para>
/// <para>
/// <b>Isolating run sequences (X10) — documented scope cut:</b> weak-type/neutral/implicit
/// resolution (W1-W7, N0-N2, I1/I2) run as one flat left-to-right pass over the whole
/// paragraph rather than being split per isolating-run-sequence. For the common case this
/// phase targets — no explicit directional-formatting characters (LRE/RLE/LRO/RLO/PDF/LRI/
/// RLI/FSI/PDI) in the input, i.e. plain paragraph-direction auto-detection with implicit
/// resolution only — a paragraph is exactly one isolating run sequence by construction, so
/// this is fully spec-compliant: the flat pass's W2/W7 backward searches seed their initial
/// strong-type context with the paragraph's own sos (start-of-sequence type, UAX#9's own X10
/// definition for a sequence with no preceding character — L if the paragraph level is even, R
/// if odd), exactly as a single-sequence run's boundary requires. Explicit-formatting characters
/// are still classified and given correct embedding levels via X1-X8 (so
/// <see cref="GetLevelRuns"/>/L2 reorder correctly), but W-N-I resolution across an
/// explicit-embedding boundary is an approximation — and an <em>unverified</em> one: the
/// conformance gate below excludes every case containing an explicit-formatting character, so
/// no public claim may describe embedding/override support as more than approximate (README/
/// AGENTS.md carry the narrowed wording; full X10 construction is 1.x, per the 2026-08-20 Phase
/// 6.5 review's honest-claims finding). Verified against
/// hand-picked UAX#9 examples in <c>BidiAlgorithmTests</c> and against the full UCD
/// <c>BidiTest.txt</c>/<c>BidiCharacterTest.txt</c>
/// conformance suites in <c>PlumePdf.CorpusTests.BidiConformanceTests</c> (which gate the
/// no-explicit-formatting cases only, per this scope cut).
/// </para>
/// </remarks>
internal static class BidiAlgorithm
{
    /// <summary>UAX#9 BD2: the maximum explicit embedding/isolate depth. Nesting beyond this overflows gracefully (the standard's own defined recovery), never throws — bidi input is not resource-limited by <see cref="PdfOptions"/> in this phase.</summary>
    public const int MaxDepth = 125;

    /// <summary>Resolves the first-strong paragraph direction (P2/P3): the level of the first character of class L, R, or AL, skipping the contents of any isolate; 0 (LTR) if none is found.</summary>
    public static int FirstStrongParagraphLevel(ReadOnlySpan<char> text)
    {
        var types = Classify(text);
        var level = FindFirstStrongLevel(types, 0, types.Length);
        return level < 0 ? 0 : level;
    }

    /// <summary>Runs the full UAX#9 pipeline (X, W, N, I, L1) over one paragraph/line of text against a fixed base direction (never <see cref="TextDirection.Auto"/> — resolve that once via <see cref="FirstStrongParagraphLevel"/> before calling, per this type's remarks).</summary>
    public static BidiAnalysis Analyze(ReadOnlySpan<char> text, TextDirection baseDirection)
    {
        var paragraphLevel = baseDirection == TextDirection.RightToLeft ? 1 : 0;
        var n = text.Length;
        var chars = text.ToArray();
        var originalTypes = Classify(text);
        var types = (BidiClass[])originalTypes.Clone();
        var levels = new int[n];

        ResolveExplicitLevels(originalTypes, types, levels, paragraphLevel);
        ResolveWeakTypes(types, levels, paragraphLevel);
        ResolveN0Brackets(chars, originalTypes, types, levels);
        ResolveNeutralTypes(types, levels, paragraphLevel);
        ResolveImplicitLevels(types, levels);
        ResetLevelsForLine(originalTypes, levels, paragraphLevel);

        return new BidiAnalysis(paragraphLevel, levels);
    }

    /// <summary>BD7: splits a resolved-level array into maximal runs of one constant level each, in logical (original text) order.</summary>
    public static IReadOnlyList<BidiRun> GetLevelRuns(IReadOnlyList<int> levels)
    {
        var runs = new List<BidiRun>();
        if (levels.Count == 0)
        {
            return runs;
        }

        var start = 0;
        for (var i = 1; i <= levels.Count; i++)
        {
            if (i == levels.Count || levels[i] != levels[start])
            {
                runs.Add(new BidiRun(start, i - start, levels[start]));
                start = i;
            }
        }

        return runs;
    }

    /// <summary>
    /// L2: the run-granularity form of the reorder — reverses contiguous stretches of runs at
    /// or above each level, from the highest level down to the lowest odd level, exactly as L2
    /// does at character granularity. Operating on runs instead of characters preserves each
    /// run's own logical shaping order (so <see cref="TextLayouter.ShapeVisualLine"/> can shape
    /// each run once, in logical order, then simply reverse the resulting glyph list for an
    /// odd-level run — GSUB/GPOS context and measured-vs-painted width both stay correct).
    /// </summary>
    /// <returns>The indices into <paramref name="runs"/>, in the order those runs should be painted left to right.</returns>
    public static IReadOnlyList<int> ReorderRunIndices(IReadOnlyList<BidiRun> runs)
    {
        var order = new int[runs.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        if (runs.Count == 0)
        {
            return order;
        }

        var maxLevel = 0;
        var minOddLevel = int.MaxValue;
        foreach (var run in runs)
        {
            if (run.Level > maxLevel)
            {
                maxLevel = run.Level;
            }

            if ((run.Level & 1) != 0 && run.Level < minOddLevel)
            {
                minOddLevel = run.Level;
            }
        }

        if (minOddLevel == int.MaxValue)
        {
            return order;
        }

        for (var level = maxLevel; level >= minOddLevel; level--)
        {
            var i = 0;
            while (i < order.Length)
            {
                if (runs[order[i]].Level >= level)
                {
                    var j = i;
                    while (j < order.Length && runs[order[j]].Level >= level)
                    {
                        j++;
                    }

                    Array.Reverse(order, i, j - i);
                    i = j;
                }
                else
                {
                    i++;
                }
            }
        }

        return order;
    }

    /// <summary>L4: the mirrored counterpart of a paired/mirrorable character (brackets, angle brackets, guillemets), for substitution before shaping when the character falls in a right-to-left-resolved run.</summary>
    public static bool TryGetMirror(char ch, out char mirrored)
    {
        foreach (var (a, b) in MirrorPairs)
        {
            if (ch == a)
            {
                mirrored = b;
                return true;
            }

            if (ch == b)
            {
                mirrored = a;
                return true;
            }
        }

        mirrored = ch;
        return false;
    }

    /// <summary>Classifies one UTF-16 code unit's Bidi_Class, via the pinned UCD table (see this type's remarks).</summary>
    public static BidiClass GetBidiClass(char ch) => ToLayoutBidiClass(UnicodeShapingData.GetBidiClass(ch));

    /// <summary>
    /// Maps <c>PlumePdf.Fonts.Shaping.BidiClass</c> (the generated UCD table's enum) onto this
    /// namespace's own identically-member-named <see cref="BidiClass"/> -- two intentionally
    /// separate, ratified types (the same pattern as the public/internal <c>TextDirection</c>
    /// split resolved via using-aliases) rather than a shared
    /// cross-layer type, so a switch expression by member name is the seam, not a numeric cast
    /// (the two enums do not share declaration order).
    /// </summary>
    private static BidiClass ToLayoutBidiClass(ShapingBidiClass value) => value switch
    {
        ShapingBidiClass.L => BidiClass.L,
        ShapingBidiClass.R => BidiClass.R,
        ShapingBidiClass.AL => BidiClass.AL,
        ShapingBidiClass.EN => BidiClass.EN,
        ShapingBidiClass.ES => BidiClass.ES,
        ShapingBidiClass.ET => BidiClass.ET,
        ShapingBidiClass.AN => BidiClass.AN,
        ShapingBidiClass.CS => BidiClass.CS,
        ShapingBidiClass.NSM => BidiClass.NSM,
        ShapingBidiClass.BN => BidiClass.BN,
        ShapingBidiClass.B => BidiClass.B,
        ShapingBidiClass.S => BidiClass.S,
        ShapingBidiClass.WS => BidiClass.WS,
        ShapingBidiClass.ON => BidiClass.ON,
        ShapingBidiClass.LRE => BidiClass.LRE,
        ShapingBidiClass.LRO => BidiClass.LRO,
        ShapingBidiClass.RLE => BidiClass.RLE,
        ShapingBidiClass.RLO => BidiClass.RLO,
        ShapingBidiClass.PDF => BidiClass.PDF,
        ShapingBidiClass.LRI => BidiClass.LRI,
        ShapingBidiClass.RLI => BidiClass.RLI,
        ShapingBidiClass.FSI => BidiClass.FSI,
        ShapingBidiClass.PDI => BidiClass.PDI,
        _ => BidiClass.L,
    };

    // Bracket-like mirror pairs (L4/BD16): parentheses, square/curly brackets, angle brackets,
    // guillemets (U+00AB/00BB), single guillemets (U+2039/203A). Kept intentionally small — the
    // exit bar's mirroring test surface is ASCII/common punctuation brackets, not the full UCD
    // BidiMirroring table.
    private static readonly (char Open, char Close)[] MirrorPairs =
    [
        ('(', ')'),
        ('[', ']'),
        ('{', '}'),
        ('<', '>'),
        ('«', '»'),
        ('‹', '›'),
    ];

    private static bool IsOpeningBracket(char ch) => ch is '(' or '[' or '{' or '«' or '‹';

    private static bool IsClosingBracket(char ch) => ch is ')' or ']' or '}' or '»' or '›';

    private static char ClosingFor(char open) => open switch
    {
        '(' => ')',
        '[' => ']',
        '{' => '}',
        '«' => '»',
        '‹' => '›',
        _ => '\0',
    };

    private static BidiClass[] Classify(ReadOnlySpan<char> text)
    {
        var types = new BidiClass[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            types[i] = GetBidiClass(text[i]);
        }

        return types;
    }

    private static int FindFirstStrongLevel(IReadOnlyList<BidiClass> types, int start, int end)
    {
        var depth = 0;
        for (var i = start; i < end; i++)
        {
            var t = types[i];
            if (t is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
            {
                depth++;
                continue;
            }

            if (t == BidiClass.PDI)
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (t == BidiClass.L)
            {
                return 0;
            }

            if (t is BidiClass.R or BidiClass.AL)
            {
                return 1;
            }
        }

        return -1;
    }

    private static int FindMatchingPdi(IReadOnlyList<BidiClass> types, int isolateStart)
    {
        var depth = 1;
        for (var i = isolateStart + 1; i < types.Count; i++)
        {
            var t = types[i];
            if (t is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI)
            {
                depth++;
            }
            else if (t == BidiClass.PDI)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return types.Count;
    }

    private readonly record struct DirectionalStatus(int Level, BidiClass? Override, bool IsolateStatus);

    /// <summary>X1-X8: explicit embedding/override/isolate levels, via the standard directional-status stack.</summary>
    private static void ResolveExplicitLevels(BidiClass[] originalTypes, BidiClass[] types, int[] levels, int paragraphLevel)
    {
        var stack = new List<DirectionalStatus>(MaxDepth + 2) { new(paragraphLevel, null, false) };
        var overflowIsolateCount = 0;
        var overflowEmbeddingCount = 0;
        var validIsolateCount = 0;

        for (var i = 0; i < originalTypes.Length; i++)
        {
            var t = originalTypes[i];
            switch (t)
            {
                case BidiClass.RLE or BidiClass.LRE or BidiClass.RLO or BidiClass.LRO:
                    {
                        levels[i] = stack[^1].Level;
                        var rtl = t is BidiClass.RLE or BidiClass.RLO;
                        var newLevel = NextLevel(stack[^1].Level, rtl);
                        if (newLevel <= MaxDepth && overflowIsolateCount == 0 && overflowEmbeddingCount == 0)
                        {
                            var newOverride = t switch { BidiClass.RLO => (BidiClass?)BidiClass.R, BidiClass.LRO => BidiClass.L, _ => null };
                            stack.Add(new DirectionalStatus(newLevel, newOverride, false));
                        }
                        else if (overflowIsolateCount == 0)
                        {
                            overflowEmbeddingCount++;
                        }

                        break;
                    }

                case BidiClass.RLI or BidiClass.LRI or BidiClass.FSI:
                    {
                        levels[i] = stack[^1].Level;
                        if (stack[^1].Override is { } ov)
                        {
                            types[i] = ov;
                        }

                        var rtl = t == BidiClass.RLI || (t == BidiClass.FSI && ResolveFsiDirection(originalTypes, i));
                        var newLevel = NextLevel(stack[^1].Level, rtl);
                        if (newLevel <= MaxDepth && overflowIsolateCount == 0 && overflowEmbeddingCount == 0)
                        {
                            validIsolateCount++;
                            stack.Add(new DirectionalStatus(newLevel, null, true));
                        }
                        else
                        {
                            overflowIsolateCount++;
                        }

                        break;
                    }

                case BidiClass.PDI:
                    {
                        if (overflowIsolateCount > 0)
                        {
                            overflowIsolateCount--;
                        }
                        else if (validIsolateCount > 0)
                        {
                            overflowEmbeddingCount = 0;
                            while (!stack[^1].IsolateStatus)
                            {
                                stack.RemoveAt(stack.Count - 1);
                            }

                            stack.RemoveAt(stack.Count - 1);
                            validIsolateCount--;
                        }

                        levels[i] = stack[^1].Level;
                        if (stack[^1].Override is { } ov2)
                        {
                            types[i] = ov2;
                        }

                        break;
                    }

                case BidiClass.PDF:
                    {
                        levels[i] = stack[^1].Level;
                        if (overflowIsolateCount > 0)
                        {
                            // Still inside an overflowed isolate: this PDF does not pop anything.
                        }
                        else if (overflowEmbeddingCount > 0)
                        {
                            overflowEmbeddingCount--;
                        }
                        else if (!stack[^1].IsolateStatus && stack.Count >= 2)
                        {
                            stack.RemoveAt(stack.Count - 1);
                        }

                        break;
                    }

                case BidiClass.B:
                    {
                        levels[i] = paragraphLevel;
                        stack.Clear();
                        stack.Add(new DirectionalStatus(paragraphLevel, null, false));
                        overflowIsolateCount = 0;
                        overflowEmbeddingCount = 0;
                        validIsolateCount = 0;
                        break;
                    }

                case BidiClass.BN:
                    {
                        levels[i] = stack[^1].Level;
                        break;
                    }

                default:
                    {
                        levels[i] = stack[^1].Level;
                        if (stack[^1].Override is { } ov3)
                        {
                            types[i] = ov3;
                        }

                        break;
                    }
            }
        }
    }

    private static bool ResolveFsiDirection(IReadOnlyList<BidiClass> types, int fsiIndex)
    {
        var end = FindMatchingPdi(types, fsiIndex);
        var level = FindFirstStrongLevel(types, fsiIndex + 1, end);
        return level == 1;
    }

    private static int NextLevel(int currentLevel, bool rtl) =>
        rtl ? currentLevel + (currentLevel % 2 == 0 ? 1 : 2) : currentLevel + (currentLevel % 2 == 0 ? 2 : 1);

    private static bool IsTransparent(BidiClass t) => t is BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;

    private static int PrevNonTransparent(BidiClass[] types, int i)
    {
        for (var k = i - 1; k >= 0; k--)
        {
            if (!IsTransparent(types[k]))
            {
                return k;
            }
        }

        return -1;
    }

    private static int NextNonTransparent(BidiClass[] types, int i)
    {
        for (var k = i + 1; k < types.Length; k++)
        {
            if (!IsTransparent(types[k]))
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>
    /// W1-W7 (flat pass — see this type's remarks on isolating-run-sequence scope). The flat
    /// pass's single sequence has one boundary each side; <paramref name="paragraphLevel"/> seeds
    /// W1/W2/W7's backward searches with UAX#9's own sos (start-of-sequence) type — L if the
    /// paragraph level is even, R if odd — exactly as X10 defines it for a sequence with no
    /// preceding character, rather than leaving the initial context unset (a fix for an
    /// isolated European number at the very start of an LTR paragraph, e.g.
    /// a bare "5", was resolving to embedding level 2 instead of the UAX#9-correct level 0 —
    /// caught by the UCD <c>BidiTest.txt</c> conformance oracle, <c>BidiConformanceTests</c>).
    /// </summary>
    private static void ResolveWeakTypes(BidiClass[] types, int[] levels, int paragraphLevel)
    {
        var sos = LevelToDirection(paragraphLevel);

        // W1: NSM takes the type of the previous character (isolate initiators/PDI resolve NSM to
        // ON instead); an NSM at the very start of the sequence takes sos, per X10/W1's own text.
        for (var i = 0; i < types.Length; i++)
        {
            if (types[i] != BidiClass.NSM)
            {
                continue;
            }

            var prev = PrevNonTransparent(types, i);
            if (prev < 0)
            {
                types[i] = sos;
                continue;
            }

            var prevType = types[prev];
            types[i] = prevType is BidiClass.LRI or BidiClass.RLI or BidiClass.FSI or BidiClass.PDI ? BidiClass.ON : prevType;
        }

        // W2: EN preceded (skipping transparent) by AL becomes AN. Backward search starts from sos.
        var strongContext = sos;
        for (var i = 0; i < types.Length; i++)
        {
            if (IsTransparent(types[i]))
            {
                continue;
            }

            if (types[i] is BidiClass.L or BidiClass.R or BidiClass.AL)
            {
                strongContext = types[i];
            }
            else if (types[i] == BidiClass.EN && strongContext == BidiClass.AL)
            {
                types[i] = BidiClass.AN;
            }
        }

        // W3: AL -> R.
        for (var i = 0; i < types.Length; i++)
        {
            if (types[i] == BidiClass.AL)
            {
                types[i] = BidiClass.R;
            }
        }

        // W4: a single ES between two EN becomes EN; a single CS between two like numbers becomes that type.
        for (var i = 0; i < types.Length; i++)
        {
            if (types[i] is not (BidiClass.ES or BidiClass.CS))
            {
                continue;
            }

            var prev = PrevNonTransparent(types, i);
            var next = NextNonTransparent(types, i);
            if (prev < 0 || next < 0)
            {
                continue;
            }

            if (types[i] == BidiClass.ES && types[prev] == BidiClass.EN && types[next] == BidiClass.EN)
            {
                types[i] = BidiClass.EN;
            }
            else if (types[i] == BidiClass.CS && types[prev] == types[next] && types[prev] is BidiClass.EN or BidiClass.AN)
            {
                types[i] = types[prev];
            }
        }

        // W5: a sequence of ET adjacent to EN becomes EN. The run itself, like every other
        // W-rule neighbor search in this method, is grown across transparent (BN-class)
        // characters via NextNonTransparent rather than stopping at the very next array slot —
        // a fix for "ET BN ET EN" (an ET, an X9-transparent
        // character, another ET, then an EN) previously treated the two ETs as unrelated runs
        // and left the first unconverted, caught by the UCD BidiTest.txt oracle.
        for (var i = 0; i < types.Length; i++)
        {
            if (types[i] != BidiClass.ET)
            {
                continue;
            }

            var runStart = i;
            var runEnd = i;
            while (true)
            {
                var candidate = NextNonTransparent(types, runEnd);
                if (candidate < 0 || types[candidate] != BidiClass.ET)
                {
                    break;
                }

                runEnd = candidate;
            }

            var before = PrevNonTransparent(types, runStart);
            var after = NextNonTransparent(types, runEnd);
            var adjacentToEn = (before >= 0 && types[before] == BidiClass.EN) || (after >= 0 && types[after] == BidiClass.EN);
            if (adjacentToEn)
            {
                for (var k = runStart; k <= runEnd; k++)
                {
                    if (types[k] == BidiClass.ET)
                    {
                        types[k] = BidiClass.EN;
                    }
                }
            }

            i = runEnd;
        }

        // W6: remaining ES, ET, CS -> ON.
        for (var i = 0; i < types.Length; i++)
        {
            if (types[i] is BidiClass.ES or BidiClass.ET or BidiClass.CS)
            {
                types[i] = BidiClass.ON;
            }
        }

        // W7: EN preceded (skipping transparent) by L becomes L. Backward search starts from sos
        // (the bug this whole method's remarks describe: a bare EN at a paragraph's start needs
        // this to correctly resolve as L when sos is L, i.e. an even/LTR paragraph level).
        var strongContext2 = sos;
        for (var i = 0; i < types.Length; i++)
        {
            if (IsTransparent(types[i]))
            {
                continue;
            }

            if (types[i] is BidiClass.L or BidiClass.R)
            {
                strongContext2 = types[i];
            }
            else if (types[i] == BidiClass.EN && strongContext2 == BidiClass.L)
            {
                types[i] = BidiClass.L;
            }
        }
    }

    private static BidiClass? StrongForN0(BidiClass t) => t switch
    {
        BidiClass.L => BidiClass.L,
        BidiClass.R or BidiClass.AN or BidiClass.EN => BidiClass.R,
        _ => null,
    };

    /// <summary>N0 (BD16 bracket pairs): a bracket pair takes the embedding direction when a matching strong type is enclosed, the opposite (enclosed) direction when the preceding context agrees with it, or stays neutral (resolved later by N1/N2) when nothing strong is enclosed.</summary>
    private static void ResolveN0Brackets(char[] chars, BidiClass[] originalTypes, BidiClass[] types, int[] levels)
    {
        const int MaxStackDepth = 63; // BD16
        var stack = new List<(char OpenChar, int Index)>();
        var pairs = new List<(int Open, int Close)>();

        for (var i = 0; i < chars.Length; i++)
        {
            if (types[i] != BidiClass.ON)
            {
                continue;
            }

            var ch = chars[i];
            if (IsOpeningBracket(ch))
            {
                if (stack.Count >= MaxStackDepth)
                {
                    break;
                }

                stack.Add((ch, i));
            }
            else if (IsClosingBracket(ch))
            {
                for (var s = stack.Count - 1; s >= 0; s--)
                {
                    if (ClosingFor(stack[s].OpenChar) == ch)
                    {
                        pairs.Add((stack[s].Index, i));
                        stack.RemoveRange(s, stack.Count - s);
                        break;
                    }
                }
            }
        }

        pairs.Sort(static (a, b) => a.Open.CompareTo(b.Open));

        foreach (var (open, close) in pairs)
        {
            var e = (levels[open] & 1) == 0 ? BidiClass.L : BidiClass.R;
            var opposite = e == BidiClass.L ? BidiClass.R : BidiClass.L;
            var foundE = false;
            var foundOpposite = false;

            for (var k = open + 1; k < close; k++)
            {
                var strong = StrongForN0(types[k]);
                if (strong == e)
                {
                    foundE = true;
                    break;
                }

                if (strong == opposite)
                {
                    foundOpposite = true;
                }
            }

            BidiClass? resolved = null;
            if (foundE)
            {
                resolved = e;
            }
            else if (foundOpposite)
            {
                var context = e;
                for (var k = open - 1; k >= 0; k--)
                {
                    var strong = StrongForN0(types[k]);
                    if (strong is not null)
                    {
                        context = strong.Value;
                        break;
                    }
                }

                resolved = context == opposite ? opposite : e;
            }

            if (resolved is { } r)
            {
                types[open] = r;
                types[close] = r;
                PropagateNsm(originalTypes, types, open);
                PropagateNsm(originalTypes, types, close);
            }
        }
    }

    /// <summary>
    /// N0's own closing clause: "Any number of characters that had original bidirectional
    /// character type NSM prior to the application of W1 that immediately follow a paired
    /// bracket which changed to L or R under N0 should change to match the type of the paired
    /// bracket." The membership test is deliberately against <paramref name="originalTypes"/> —
    /// the pre-W1 classification — not <paramref name="types"/> itself: W1 (which already ran)
    /// overwrites every NSM's *current* type with its preceding character's type, so checking
    /// the current (mutated) array here would never find an NSM to propagate to (a fix
    /// caught by the UCD BidiCharacterTest.txt oracle, e.g. "a
    /// (b)" + a trailing combining mark, in an RTL paragraph). "Immediately follow" skips over
    /// transparent (BN-class, X9-removed) characters exactly as W1's own <see cref="PrevNonTransparent"/>
    /// does when it looks for "the previous character" — a bracket immediately followed by a ZWJ
    /// then a combining mark still needs to reach that mark (a second fix the same oracle
    /// line surfaced).
    /// </summary>
    private static void PropagateNsm(BidiClass[] originalTypes, BidiClass[] types, int idx)
    {
        var t = types[idx];
        for (var k = idx + 1; k < types.Length; k++)
        {
            if (originalTypes[k] == BidiClass.NSM)
            {
                types[k] = t;
                continue;
            }

            if (!IsTransparent(originalTypes[k]))
            {
                break;
            }
        }
    }

    private static bool IsNeutralOrIsolateFormat(BidiClass t) => t is BidiClass.B or BidiClass.S or BidiClass.WS or BidiClass.ON
        or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI;

    /// <summary>N1/N2: resolves remaining neutral/isolate-formatting runs, flanked-strong-type-agreement first, embedding direction otherwise.</summary>
    private static void ResolveNeutralTypes(BidiClass[] types, int[] levels, int paragraphLevel)
    {
        var i = 0;
        while (i < types.Length)
        {
            if (IsTransparent(types[i]) || !IsNeutralOrIsolateFormat(types[i]))
            {
                i++;
                continue;
            }

            var runStart = i;
            var runEnd = i;
            while (runEnd + 1 < types.Length && (IsTransparent(types[runEnd + 1]) || IsNeutralOrIsolateFormat(types[runEnd + 1])))
            {
                runEnd++;
            }

            var before = PrevStrongForN(types, runStart, paragraphLevel);
            var after = NextStrongForN(types, runEnd, paragraphLevel);
            var resolved = before == after ? before : LevelToDirection(levels[runStart]);

            for (var k = runStart; k <= runEnd; k++)
            {
                if (!IsTransparent(types[k]))
                {
                    types[k] = resolved;
                }
            }

            i = runEnd + 1;
        }
    }

    private static BidiClass PrevStrongForN(BidiClass[] types, int from, int paragraphLevel)
    {
        for (var k = from - 1; k >= 0; k--)
        {
            var s = StrongForN0(types[k]);
            if (s is { } value)
            {
                return value;
            }
        }

        return LevelToDirection(paragraphLevel);
    }

    private static BidiClass NextStrongForN(BidiClass[] types, int from, int paragraphLevel)
    {
        for (var k = from + 1; k < types.Length; k++)
        {
            var s = StrongForN0(types[k]);
            if (s is { } value)
            {
                return value;
            }
        }

        return LevelToDirection(paragraphLevel);
    }

    private static BidiClass LevelToDirection(int level) => (level & 1) == 0 ? BidiClass.L : BidiClass.R;

    /// <summary>I1/I2: bumps each character's resolved level based on its (now L/R/AN/EN-resolved) type and the level's own parity.</summary>
    private static void ResolveImplicitLevels(BidiClass[] types, int[] levels)
    {
        for (var i = 0; i < types.Length; i++)
        {
            if (IsTransparent(types[i]))
            {
                continue;
            }

            var even = (levels[i] & 1) == 0;
            if (even)
            {
                if (types[i] == BidiClass.R)
                {
                    levels[i]++;
                }
                else if (types[i] is BidiClass.AN or BidiClass.EN)
                {
                    levels[i] += 2;
                }
            }
            else
            {
                if (types[i] is BidiClass.L or BidiClass.EN or BidiClass.AN)
                {
                    levels[i]++;
                }
            }
        }
    }

    /// <summary>L1: resets segment/paragraph separators, and any run of whitespace/isolate-formatting/explicit-formatting characters immediately before one of them or at the end of the text, back to the paragraph level — using the ORIGINAL (pre-resolution) types, per the rule's own text.</summary>
    private static void ResetLevelsForLine(BidiClass[] originalTypes, int[] levels, int paragraphLevel)
    {
        var n = originalTypes.Length;
        for (var i = 0; i < n; i++)
        {
            if (originalTypes[i] is BidiClass.B or BidiClass.S)
            {
                levels[i] = paragraphLevel;
            }
        }

        var trailingRunHandled = false;
        for (var i = n - 1; i >= 0; i--)
        {
            var resettable = IsTrailingResettable(originalTypes, i);
            if (!resettable)
            {
                trailingRunHandled = false;
                continue;
            }

            var atLineEnd = i == n - 1;
            var beforeSeparator = i + 1 < n && originalTypes[i + 1] is BidiClass.B or BidiClass.S;
            if (atLineEnd || beforeSeparator || trailingRunHandled)
            {
                levels[i] = paragraphLevel;
                trailingRunHandled = true;
            }
            else
            {
                trailingRunHandled = false;
            }
        }
    }

    private static bool IsTrailingResettable(BidiClass[] originalTypes, int index) =>
        originalTypes[index] is BidiClass.WS or BidiClass.FSI or BidiClass.LRI or BidiClass.RLI or BidiClass.PDI
            or BidiClass.LRE or BidiClass.RLE or BidiClass.LRO or BidiClass.RLO or BidiClass.PDF or BidiClass.BN;
}

/// <summary>One Unicode Bidi_Class value (UAX#44), as classified by <see cref="BidiAlgorithm.GetBidiClass"/>.</summary>
internal enum BidiClass
{
    L, R, AL, EN, ES, ET, AN, CS, NSM, BN, B, S, WS, ON,
    LRE, LRO, RLE, RLO, PDF, LRI, RLI, FSI, PDI,
}

/// <summary>One BD7 level run: a maximal stretch of the input at one constant resolved embedding level.</summary>
/// <param name="Start">The run's start index into the analyzed text.</param>
/// <param name="Length">The run's length, in UTF-16 code units.</param>
/// <param name="Level">The run's resolved embedding level (even = LTR, odd = RTL).</param>
internal readonly record struct BidiRun(int Start, int Length, int Level)
{
    /// <summary>Whether this run paints right-to-left (an odd resolved level).</summary>
    public bool IsRightToLeft => (Level & 1) != 0;
}

/// <summary>The result of <see cref="BidiAlgorithm.Analyze"/>: the paragraph's resolved level plus every character's own resolved level after L1.</summary>
/// <param name="ParagraphLevel">The paragraph embedding level (0 = LTR, 1 = RTL) the analysis ran against.</param>
/// <param name="Levels">Each character's resolved embedding level, one entry per UTF-16 code unit of the analyzed text, after L1's line-edge reset.</param>
internal sealed record BidiAnalysis(int ParagraphLevel, IReadOnlyList<int> Levels)
{
    /// <summary>Whether the paragraph's own base direction is right-to-left.</summary>
    public bool IsRightToLeft => (ParagraphLevel & 1) != 0;

    /// <summary>This analysis's levels, split into <see cref="BidiRun"/>s (BD7).</summary>
    public IReadOnlyList<BidiRun> GetLevelRuns() => BidiAlgorithm.GetLevelRuns(Levels);
}
