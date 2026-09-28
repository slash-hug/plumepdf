namespace PlumePdf.Documents.Forms.Appearances;

/// <summary>
/// Parses a variable-text <c>/DA</c> (default appearance) string — ISO 32000-1 §12.7.3.3, a
/// fragment of content-stream operators, conventionally <c>/Helv 12 Tf 0 g</c> — into the
/// three things appearance generation needs: the font resource name, the font size (0 means
/// auto-size), and the color operators to replay verbatim. Tolerant of junk: every number
/// goes through <see cref="double.TryParse(string?, out double)"/>-style parsing (a <c>/DA</c>
/// is document-supplied data, never trusted to be well-formed), unknown operators are skipped, and a
/// string with no usable <c>Tf</c> yields <see cref="Parsed.FontResourceName"/> null so the
/// caller can degrade with a diagnostic instead of guessing a font.
/// </summary>
internal static class DefaultAppearanceParser
{
    /// <summary>The parse result; <see cref="ColorOperators"/> defaults to black (<c>0 g</c>).</summary>
    internal sealed record Parsed(string? FontResourceName, double FontSize, string ColorOperators);

    public static Parsed Parse(string defaultAppearance)
    {
        string? fontName = null;
        var fontSize = 0.0;
        var color = "0 g";

        var tokens = defaultAppearance.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var operands = new List<string>();

        foreach (var token in tokens)
        {
            switch (token)
            {
                case "Tf" when operands.Count >= 2:
                    var nameToken = operands[^2];
                    if (nameToken.StartsWith('/') && nameToken.Length > 1
                        && double.TryParse(operands[^1], System.Globalization.CultureInfo.InvariantCulture, out var size)
                        && double.IsFinite(size) && size >= 0)
                    {
                        fontName = nameToken[1..];
                        fontSize = size;
                    }

                    operands.Clear();
                    break;

                case "g" when TryColor(operands, 1, out var gray):
                    color = $"{gray} g";
                    operands.Clear();
                    break;

                case "rg" when TryColor(operands, 3, out var rgb):
                    color = $"{rgb} rg";
                    operands.Clear();
                    break;

                case "k" when TryColor(operands, 4, out var cmyk):
                    color = $"{cmyk} k";
                    operands.Clear();
                    break;

                case "Tf" or "g" or "rg" or "k":
                    operands.Clear(); // operator with malformed operands: skip it whole
                    break;

                default:
                    operands.Add(token);
                    break;
            }
        }

        return new Parsed(fontName, fontSize, color);
    }

    private static bool TryColor(List<string> operands, int count, out string joined)
    {
        joined = string.Empty;
        if (operands.Count < count)
        {
            return false;
        }

        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            if (!double.TryParse(operands[operands.Count - count + i], System.Globalization.CultureInfo.InvariantCulture, out var component)
                || !double.IsFinite(component))
            {
                return false;
            }

            parts[i] = component.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        joined = string.Join(' ', parts);
        return true;
    }
}
