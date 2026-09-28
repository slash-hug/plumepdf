namespace PlumePdf.Documents.Forms.Appearances;

/// <summary>
/// Lays variable text into a widget's rectangle (ISO 32000-1 §12.7.3.3): quadding
/// (<c>/Q</c> 0 left / 1 center / 2 right), <c>0 Tf</c> auto-sizing for single-line fields,
/// and greedy word-wrap for multiline ones. Geometry is deliberately simple and locked by
/// snapshot tests: a 2pt inset on every side, a 1.15 line pitch, and a baseline placed with
/// a 0.28em descent allowance — the same order of approximation every mainstream filler
/// uses, since §12.7.3.3 specifies no layout algorithm.
/// </summary>
internal static class VariableTextLayout
{
    private const double Inset = 2.0;
    private const double LinePitch = 1.15;
    private const double DescentAllowance = 0.28;
    private const double MinAutoSize = 4.0;
    private const double MaxAutoSize = 24.0;

    /// <summary>One positioned line: the text and its baseline origin in the appearance's BBox space.</summary>
    internal readonly record struct Line(string Text, double X, double Y);

    /// <summary>
    /// Lays out <paramref name="text"/>. <paramref name="measure"/> returns a string's width
    /// at font size 1 (i.e. glyph-space widths / 1000 summed). Returns the effective font
    /// size and the positioned lines; <paramref name="truncated"/> is set when a multiline
    /// field had more lines than fit.
    /// </summary>
    public static (double FontSize, IReadOnlyList<Line> Lines) Layout(
        string text,
        Func<string, double> measure,
        double width,
        double height,
        double requestedSize,
        int quadding,
        bool multiline,
        out bool truncated)
    {
        truncated = false;
        var availableWidth = Math.Max(width - (2 * Inset), 1);
        var availableHeight = Math.Max(height - (2 * Inset), 1);

        if (!multiline)
        {
            var size = requestedSize > 0 ? requestedSize : AutoSize(text, measure, availableWidth, availableHeight);
            var textWidth = measure(text) * size;
            var x = Inset + (quadding switch
            {
                1 => Math.Max(0, (availableWidth - textWidth) / 2),
                2 => Math.Max(0, availableWidth - textWidth),
                _ => 0,
            });
            var y = ((height - size) / 2) + (DescentAllowance * size);
            return (size, [new Line(text, x, y)]);
        }

        var lineSize = requestedSize > 0 ? requestedSize : 12;
        var wrapped = Wrap(text, measure, availableWidth, lineSize);
        var pitch = lineSize * LinePitch;
        var lines = new List<Line>(wrapped.Count);
        for (var i = 0; i < wrapped.Count; i++)
        {
            var y = height - Inset - ((i + 1) * pitch) + (DescentAllowance * lineSize);
            if (y < Inset - (DescentAllowance * lineSize))
            {
                truncated = true;
                break;
            }

            var lineWidth = measure(wrapped[i]) * lineSize;
            var x = Inset + (quadding switch
            {
                1 => Math.Max(0, (availableWidth - lineWidth) / 2),
                2 => Math.Max(0, availableWidth - lineWidth),
                _ => 0,
            });
            lines.Add(new Line(wrapped[i], x, y));
        }

        return (lineSize, lines);
    }

    private static double AutoSize(string text, Func<string, double> measure, double availableWidth, double availableHeight)
    {
        var size = Math.Min(availableHeight / LinePitch, MaxAutoSize);
        var unit = measure(text);
        if (unit > 0 && unit * size > availableWidth)
        {
            size = availableWidth / unit;
        }

        return Math.Max(size, MinAutoSize);
    }

    private static List<string> Wrap(string text, Func<string, double> measure, double availableWidth, double size)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var current = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && measure(candidate) * size > availableWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
        }

        return lines;
    }
}
