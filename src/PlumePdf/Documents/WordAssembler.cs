namespace PlumePdf.Documents;

// Provenance (the clean-room policy in AGENTS.md; NOTICE): the clustering heuristic below is
// adapted from PdfPig's NearestNeighbourWordExtractor (UglyToad.PdfPig.DocumentLayoutAnalysis
// .WordExtractor, Apache-2.0, https://github.com/UglyToad/PdfPig) — same-line baseline
// tolerance plus a font-size-relative horizontal gap threshold to decide whether consecutive
// letters belong to the same word. Reimplemented from the algorithm's published shape (not
// copied source) against PlumePDF's own Letter/GraphicsStateStack types; see NOTICE for the
// attribution text.

/// <summary>
/// Groups a page's letters (in content-stream order) into words, using a simplified
/// nearest-neighbour heuristic: consecutive letters join the same word when they sit on the
/// same baseline (within a font-size-relative tolerance) and the horizontal gap between them
/// is small relative to the font size; a decoded space character, a font/size change, or a
/// larger gap starts a new word. Ported from PdfPig's approach (see the provenance comment
/// above) rather than PlumePDF's own invention.
/// </summary>
internal static class WordAssembler
{
    // How much of the font size a horizontal gap may span before it counts as a word break,
    // and how much of the font size a baseline may drift before two letters are no longer
    // considered "the same line" - both relative thresholds (not absolute point values) so
    // the same heuristic works across wildly different font sizes on the same page.
    private const double MaxGapFraction = 0.26;
    private const double MaxBaselineDriftFraction = 0.35;

    /// <summary>Assembles <paramref name="letters"/> (content order) into words (content order — <c>ReadingOrderer</c> reorders them).</summary>
    public static List<ExtractedWord> Assemble(IReadOnlyList<Letter> letters)
    {
        var words = new List<ExtractedWord>();
        var current = new List<Letter>();

        foreach (var letter in letters)
        {
            if (string.IsNullOrEmpty(letter.Value) || letter.Value.Trim().Length == 0)
            {
                // A decoded space (or other whitespace) never joins a word itself, but it
                // ends whatever word was in progress.
                FlushWord(current, words);
                continue;
            }

            if (current.Count == 0)
            {
                current.Add(letter);
                continue;
            }

            var previous = current[^1];
            var referenceSize = Math.Max(Math.Max(previous.FontSize, letter.FontSize), 0.01);

            // Decompose the current letter's origin, relative to the previous letter's origin,
            // into components along and perpendicular to the previous letter's own writing
            // direction (Letter.DirectionX/Y) — a unit vector, (1,0) for ordinary horizontal
            // text and something else for rotated/vertical text (§9.4.4 CTM/Tm composition
            // already captures the rotation; this is just re-expressing the gap in that
            // letter's local frame instead of assuming a horizontal page). "Same line" becomes
            // "small perpendicular drift"; "gap" becomes "distance along the direction, past
            // the previous letter's own advance width" — both generalize the axis-aligned case
            // exactly (perpendicular = Y drift, along = X gap) while also handling rotated text.
            var relX = letter.X - previous.X;
            var relY = letter.Y - previous.Y;
            var along = (relX * previous.DirectionX) + (relY * previous.DirectionY);
            var perpendicular = (relX * -previous.DirectionY) + (relY * previous.DirectionX);
            var directionsAligned = ((previous.DirectionX * letter.DirectionX) + (previous.DirectionY * letter.DirectionY)) >= 0.9;

            var sameBaseline = directionsAligned && Math.Abs(perpendicular) <= referenceSize * MaxBaselineDriftFraction;
            var gap = along - previous.AdvanceWidth;
            var withinGap = gap <= referenceSize * MaxGapFraction;

            if (sameBaseline && withinGap)
            {
                current.Add(letter);
            }
            else
            {
                FlushWord(current, words);
                current.Add(letter);
            }
        }

        FlushWord(current, words);
        return words;
    }

    private static void FlushWord(List<Letter> current, List<ExtractedWord> words)
    {
        if (current.Count == 0)
        {
            return;
        }

        var text = string.Concat(current.Select(static l => l.Value));
        var box = current[0].BoundingBox;
        for (var i = 1; i < current.Count; i++)
        {
            box = PdfRectangle.Union(box, current[i].BoundingBox);
        }

        words.Add(new ExtractedWord(text, box, [.. current], current[0].Mcid));
        current.Clear();
    }
}
