using System.Text;
using System.Text.RegularExpressions;

namespace PlumePdf.Documents.Redaction;

/// <summary>
/// What one <c>PdfDocument.Redact</c> call should remove: an explicit caller-supplied
/// rectangle on one page, a literal text match, or a regular-expression match — resolved
/// against Phase 3's own extraction (<see cref="Letter"/> boxes) into concrete page regions
/// by <see cref="RedactionTargetResolver.Resolve"/> before <c>ContentStreamEditor</c> ever runs
/// (caller-supplied targets only in v1.0 — PlumePDF does not honor a source
/// document's own pre-existing <c>/Subtype /Redact</c> annotations, a documented 1.x gap).
/// </summary>
/// <example>
/// <code>
/// var targets = new[]
/// {
///     RedactionTarget.Text("Social Security Number"),
///     RedactionTarget.Pattern(new Regex(@"\d{3}-\d{2}-\d{4}")),
///     RedactionTarget.Region(pageIndex: 0, new PdfRectangle(72, 700, 300, 720)),
/// };
/// var result = document.Redact(targets);
/// </code>
/// </example>
public sealed class RedactionTarget
{
    private RedactionTarget(RedactionTargetKind kind, int? pageIndex, PdfRectangle rect, string? matchText, Regex? pattern)
    {
        Kind = kind;
        PageIndex = pageIndex;
        Rect = rect;
        MatchText = matchText;
        MatchPattern = pattern;
    }

    /// <summary>An explicit rectangle on one page (page space: points, bottom-left origin, y-up — the same convention <see cref="Letter.BoundingBox"/> uses).</summary>
    /// <param name="pageIndex">The zero-based page index.</param>
    /// <param name="rect">The rectangle to redact.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is negative.</exception>
    public static RedactionTarget Region(int pageIndex, PdfRectangle rect)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        return new RedactionTarget(RedactionTargetKind.Region, pageIndex, rect, null, null);
    }

    /// <summary>
    /// Every occurrence of <paramref name="text"/> found by Phase 3 text extraction, across
    /// <paramref name="pageIndex"/> (or every page when <see langword="null"/>). Case
    /// sensitivity is controlled by <see cref="PdfRedactOptions.CaseSensitiveText"/> on the
    /// <c>Redact</c> call, not here. Matching is against the page's reading-order text
    /// (<see cref="ExtractedText.Text"/>'s own line/word joining rules), so a phrase that wraps
    /// across a PDF line break is still found as long as it appears contiguously in reading
    /// order.
    /// </summary>
    /// <param name="text">The literal text to find and redact. Must be non-empty.</param>
    /// <param name="pageIndex">The zero-based page index to search, or <see langword="null"/> (default) to search every page.</param>
    public static RedactionTarget Text(string text, int? pageIndex = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex ?? 0);
        return new RedactionTarget(RedactionTargetKind.Text, pageIndex, default, text, null);
    }

    /// <summary>Every match of <paramref name="pattern"/> found by Phase 3 text extraction, across <paramref name="pageIndex"/> (or every page when <see langword="null"/>). See <see cref="Text"/>'s remarks on reading-order matching.</summary>
    /// <param name="pattern">The regular expression to match. Its own <see cref="RegexOptions"/> (case sensitivity, multiline, ...) apply as configured.</param>
    /// <param name="pageIndex">The zero-based page index to search, or <see langword="null"/> (default) to search every page.</param>
    public static RedactionTarget Pattern(Regex pattern, int? pageIndex = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex ?? 0);
        return new RedactionTarget(RedactionTargetKind.Pattern, pageIndex, default, null, pattern);
    }

    internal RedactionTargetKind Kind { get; }

    internal int? PageIndex { get; }

    internal PdfRectangle Rect { get; }

    internal string? MatchText { get; }

    internal Regex? MatchPattern { get; }
}

/// <summary>Which of <see cref="RedactionTarget"/>'s three shapes a given instance is.</summary>
internal enum RedactionTargetKind
{
    /// <summary>An explicit caller-supplied rectangle.</summary>
    Region,

    /// <summary>A literal text match.</summary>
    Text,

    /// <summary>A regular-expression match.</summary>
    Pattern,
}

/// <summary>One concrete (page, rectangle) region <c>ContentStreamEditor</c> removes content within — the resolved form every <see cref="RedactionTarget"/> reduces to.</summary>
internal readonly record struct ResolvedRegion(int PageIndex, PdfRectangle Rect);

/// <summary>
/// Resolves <see cref="RedactionTarget"/>s against an open <see cref="PdfDocument"/> into
/// concrete <see cref="ResolvedRegion"/>s, using Phase 3's own <see cref="PdfPage.ExtractText"/>
/// for the text/pattern cases. Lives in the Documents layer (this file's own
/// namespace) specifically so it can depend on <see cref="Letter"/>/<see cref="ExtractedText"/>
/// — <c>PlumePdf.Content.ContentStreamEditor</c>, one layer lower, never sees a
/// <see cref="RedactionTarget"/> at all; it only ever receives the plain rectangles this
/// resolver already computed (docs/architecture.md's layering rule: Content must not depend on
/// Documents).
/// </summary>
internal static class RedactionTargetResolver
{
    /// <summary>
    /// Resolves every target in <paramref name="targets"/> against <paramref name="document"/>.
    /// Region targets pass through unchanged (one match each); text/pattern targets are matched
    /// against each page in scope's reading-order text and each match's matched
    /// <see cref="Letter"/>s' bounding boxes are unioned into one region. Adjacent/overlapping
    /// regions on the same page are merged (so <c>RedactionResult.RegionsRedacted</c> can be
    /// smaller than <c>RedactionResult.MatchCount</c>).
    /// </summary>
    /// <exception cref="PlumePdfException"><c>PLUME6060</c> — the total match count across every target would exceed <see cref="PdfRedactOptions.MaxMatches"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A target names a page index at or beyond <paramref name="document"/>'s page count.</exception>
    public static (IReadOnlyList<ResolvedRegion> Regions, int MatchCount) Resolve(IReadOnlyList<RedactionTarget> targets, PdfDocument document, PdfRedactOptions options)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);

        var matches = new List<ResolvedRegion>();
        var matchCount = 0;
        var textCache = new Dictionary<int, (string Text, IReadOnlyList<Letter?> LetterAtOffset)>();

        foreach (var target in targets)
        {
            switch (target.Kind)
            {
                case RedactionTargetKind.Region:
                    RequirePageInRange(document, target.PageIndex!.Value);
                    matches.Add(new ResolvedRegion(target.PageIndex.Value, target.Rect));
                    matchCount++;
                    GuardMatchCount(matchCount, options);
                    break;

                case RedactionTargetKind.Text:
                case RedactionTargetKind.Pattern:
                    ResolveTextOrPattern(target, document, options, textCache, matches, ref matchCount, options.CaseSensitiveText);
                    break;
            }
        }

        return (MergeOverlapping(matches), matchCount);
    }

    private static void ResolveTextOrPattern(RedactionTarget target, PdfDocument document, PdfRedactOptions options, Dictionary<int, (string Text, IReadOnlyList<Letter?> LetterAtOffset)> textCache, List<ResolvedRegion> matches, ref int matchCount, bool caseSensitive)
    {
        var pageIndices = target.PageIndex is int p ? [p] : Enumerable.Range(0, document.Pages.Count);
        foreach (var pageIndex in pageIndices)
        {
            RequirePageInRange(document, pageIndex);
            if (!textCache.TryGetValue(pageIndex, out var built))
            {
                built = BuildIndexedText(document.Pages[pageIndex].ExtractText());
                textCache[pageIndex] = built;
            }

            var (text, letterAtOffset) = built;
            foreach (var (start, length) in FindMatches(target, text, caseSensitive))
            {
                PdfRectangle? union = null;
                for (var i = start; i < start + length && i < letterAtOffset.Count; i++)
                {
                    var letter = letterAtOffset[i];
                    if (letter is null)
                    {
                        continue;
                    }

                    union = union is { } u ? PdfRectangle.Union(u, letter.BoundingBox) : letter.BoundingBox;
                }

                if (union is { } rect)
                {
                    matches.Add(new ResolvedRegion(pageIndex, rect));
                    matchCount++;
                    GuardMatchCount(matchCount, options);
                }
            }
        }
    }

    private static IEnumerable<(int Start, int Length)> FindMatches(RedactionTarget target, string text, bool caseSensitive)
    {
        if (target.Kind == RedactionTargetKind.Pattern)
        {
            foreach (Match m in target.MatchPattern!.Matches(text))
            {
                if (m.Length > 0)
                {
                    yield return (m.Index, m.Length);
                }
            }

            yield break;
        }

        var needle = target.MatchText!;
        if (needle.Length == 0)
        {
            yield break;
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index <= text.Length - needle.Length && index >= 0)
        {
            var found = text.IndexOf(needle, index, comparison);
            if (found < 0)
            {
                yield break;
            }

            yield return (found, needle.Length);
            index = found + needle.Length;
        }
    }

    // Builds the exact same reading-order text ExtractedText.Text would (Lines' text joined by
    // '\n', each line's Words joined by ' ') alongside a parallel per-character array pointing
    // back at the Letter that produced that character — separators (spaces/newlines between
    // words/lines) map to null. Reconstructed locally rather than reusing ExtractedText.Text
    // directly because that string alone has no such back-mapping, and Letter/ExtractedWord/
    // ExtractedLine's own public shape doesn't expose one either.
    private static (string Text, IReadOnlyList<Letter?> LetterAtOffset) BuildIndexedText(ExtractedText extracted)
    {
        var sb = new StringBuilder();
        var map = new List<Letter?>();

        for (var lineIndex = 0; lineIndex < extracted.Lines.Count; lineIndex++)
        {
            var line = extracted.Lines[lineIndex];
            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                foreach (var letter in word.Letters)
                {
                    sb.Append(letter.Value);
                    for (var c = 0; c < letter.Value.Length; c++)
                    {
                        map.Add(letter);
                    }
                }

                if (wordIndex < line.Words.Count - 1)
                {
                    sb.Append(' ');
                    map.Add(null);
                }
            }

            if (lineIndex < extracted.Lines.Count - 1)
            {
                sb.Append('\n');
                map.Add(null);
            }
        }

        return (sb.ToString(), map);
    }

    private static List<ResolvedRegion> MergeOverlapping(List<ResolvedRegion> regions)
    {
        var byPage = regions.GroupBy(static r => r.PageIndex);
        var merged = new List<ResolvedRegion>();

        foreach (var group in byPage)
        {
            var pending = group.Select(static r => r.Rect).ToList();
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var i = 0; i < pending.Count && !changed; i++)
                {
                    for (var j = i + 1; j < pending.Count; j++)
                    {
                        if (Intersects(pending[i], pending[j]))
                        {
                            pending[i] = PdfRectangle.Union(pending[i], pending[j]);
                            pending.RemoveAt(j);
                            changed = true;
                            break;
                        }
                    }
                }
            }

            merged.AddRange(pending.Select(rect => new ResolvedRegion(group.Key, rect)));
        }

        return merged;
    }

    private static bool Intersects(PdfRectangle a, PdfRectangle b) =>
        a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

    private static void RequirePageInRange(PdfDocument document, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= document.Pages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, $"This document has {document.Pages.Count} page(s); page index {pageIndex} is out of range.");
        }
    }

    private static void GuardMatchCount(int matchCount, PdfRedactOptions options)
    {
        if (matchCount > options.MaxMatches)
        {
            throw new PlumePdfException("PLUME6060", $"Redaction resolved more than {options.MaxMatches} match(es) (PdfRedactOptions.MaxMatches) — refusing to continue (a resource-limit guard against a hostile or pathological pattern target).");
        }
    }
}
