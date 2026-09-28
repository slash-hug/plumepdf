using PlumePdf.Objects;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// The encode direction for a simple font: Unicode text → single-byte character codes, the
/// inverse of what <see cref="SimpleExtractionFont"/> decodes. Built from the same effective
/// encoding table <see cref="EncodingResolver"/>.<c>Resolve</c> produces for the decode direction, so
/// the two directions can never disagree about what a code means. Codes are chosen
/// lowest-first when several map to the same scalar. Characters the font's encoding cannot
/// represent are reported per <see cref="TryEncode"/>'s contract rather than silently
/// substituted (the fail-loud posture for generated appearances).
/// </summary>
internal sealed class FontEncoder
{
    private readonly Dictionary<int, byte> _unicodeToCode;

    private FontEncoder(Dictionary<int, byte> unicodeToCode) => _unicodeToCode = unicodeToCode;

    /// <summary>Builds an encoder for <paramref name="fontDict"/>, or <see langword="null"/> when its encoding yields no usable mappings.</summary>
    public static FontEncoder? Create(PdfDictionary fontDict, IObjectSource objects, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var table = EncodingResolver.Resolve(fontDict, objects, options, diagnostics);
        var reverse = new Dictionary<int, byte>();
        for (var code = 0; code < table.Length && code <= byte.MaxValue; code++)
        {
            var unicode = table[code].Unicode;
            if (unicode > 0 && !reverse.ContainsKey(unicode))
            {
                reverse[unicode] = (byte)code;
            }
        }

        return reverse.Count == 0 ? null : new FontEncoder(reverse);
    }

    /// <summary>
    /// Encodes <paramref name="text"/> to this font's character codes. Returns
    /// <see langword="false"/> — with <paramref name="firstUnencodable"/> naming the offending
    /// scalar — when any character has no code; <paramref name="encoded"/> is then null.
    /// </summary>
    public bool TryEncode(string text, out byte[]? encoded, out int firstUnencodable)
    {
        var output = new byte[text.Length];
        var count = 0;

        for (var i = 0; i < text.Length; i++)
        {
            int scalar = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                scalar = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }

            if (!_unicodeToCode.TryGetValue(scalar, out var code))
            {
                encoded = null;
                firstUnencodable = scalar;
                return false;
            }

            output[count++] = code;
        }

        encoded = count == output.Length ? output : output[..count];
        firstUnencodable = 0;
        return true;
    }
}
