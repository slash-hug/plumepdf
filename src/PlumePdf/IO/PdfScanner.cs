using System.Buffers;

namespace PlumePdf.IO;

/// <summary>
/// Low-level byte-classification and bounded-search primitives shared by the tokenizer,
/// the cross-reference reader's tail scan, and the recovery ladder's brute-force scan.
/// Classification uses <see cref="SearchValues{T}"/> (ISO 32000-1 §7.2.2's whitespace and
/// delimiter character sets) for fast, allocation-free membership tests.
/// </summary>
internal static class PdfScanner
{
    /// <summary>
    /// ISO 32000-1 §7.2.2 whitespace bytes: NUL, HT, LF, FF, CR, SP.
    /// </summary>
    public static readonly SearchValues<byte> Whitespace = SearchValues.Create([0x00, 0x09, 0x0A, 0x0C, 0x0D, 0x20]);

    /// <summary>
    /// ISO 32000-1 §7.2.2 delimiter bytes: <c>( ) &lt; &gt; [ ] { } / %</c>.
    /// </summary>
    public static readonly SearchValues<byte> Delimiters = SearchValues.Create("()<>[]{}/%"u8);

    /// <summary>The union of <see cref="Whitespace"/> and <see cref="Delimiters"/> — bytes that end a regular-character run.</summary>
    public static readonly SearchValues<byte> WhitespaceOrDelimiters = SearchValues.Create([0x00, 0x09, 0x0A, 0x0C, 0x0D, 0x20, (byte)'(', (byte)')', (byte)'<', (byte)'>', (byte)'[', (byte)']', (byte)'{', (byte)'}', (byte)'/', (byte)'%']);

    /// <summary>Whether <paramref name="b"/> is whitespace per §7.2.2.</summary>
    public static bool IsWhitespace(byte b) => Whitespace.Contains(b);

    /// <summary>Whether <paramref name="b"/> is a delimiter per §7.2.2.</summary>
    public static bool IsDelimiter(byte b) => Delimiters.Contains(b);

    /// <summary>Whether <paramref name="b"/> is a "regular" character — neither whitespace nor a delimiter.</summary>
    public static bool IsRegular(byte b) => !WhitespaceOrDelimiters.Contains(b);

    /// <summary>Finds the last occurrence of ASCII <paramref name="pattern"/> in <paramref name="buffer"/>, or -1.</summary>
    public static int LastIndexOf(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> pattern) => buffer.LastIndexOf(pattern);

    /// <summary>
    /// Returns the start offsets of every occurrence of <paramref name="pattern"/> in
    /// <paramref name="buffer"/>, examining at most <paramref name="maxBytesToScan"/> bytes
    /// from the start of the buffer — the resource-limit guard against unbounded
    /// scans over hostile or simply enormous input. A cap smaller than the buffer silently
    /// ignores any occurrence that starts at or past the cap; pass <c>buffer.Length</c> to
    /// scan the whole thing.
    /// </summary>
    public static List<int> FindAll(ReadOnlySpan<byte> buffer, ReadOnlySpan<byte> pattern, long maxBytesToScan)
    {
        var matches = new List<int>();
        if (pattern.IsEmpty || buffer.IsEmpty || maxBytesToScan <= 0)
        {
            return matches;
        }

        var limit = (int)Math.Min(buffer.Length, maxBytesToScan);
        var window = buffer[..limit];
        var offset = 0;
        while (offset < window.Length)
        {
            var found = window[offset..].IndexOf(pattern);
            if (found < 0)
            {
                break;
            }

            matches.Add(offset + found);
            offset += found + 1;
        }

        return matches;
    }
}
