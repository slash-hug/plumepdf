using System.Text;

namespace PlumePdf;

/// <summary>
/// A PDF string object — literal <c>(...)</c> or hexadecimal <c>&lt;...&gt;</c>
/// (ISO 32000-1 §7.3.4). A PDF string is a byte sequence, not text; <see cref="Bytes"/>
/// exposes the decoded bytes directly and <see cref="GetText"/> offers a best-effort text
/// interpretation (a leading UTF-16BE byte-order mark per §7.9.2.2, else the bytes are
/// treated as Latin-1 — a full PDFDocEncoding table is out of Phase 1 scope). Records
/// whether it was originally lexed as literal or hex (<see cref="IsHex"/>) so a writer that
/// round-trips an unmodified string can preserve its source lexical form.
/// </summary>
/// <example>
/// <code>
/// PdfString title = PdfString.FromLiteral("Hello"u8.ToArray());
/// Console.WriteLine(title.GetText()); // "Hello"
/// </code>
/// </example>
public sealed class PdfString : PdfObject
{
    private static readonly byte[] Utf16BigEndianBom = [0xFE, 0xFF];

    private PdfString(byte[] bytes, bool isHex)
    {
        Bytes = bytes;
        IsHex = isHex;
    }

    /// <summary>The string's raw decoded bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>Whether this string was lexed from hexadecimal <c>&lt;...&gt;</c> notation rather than a literal <c>(...)</c>.</summary>
    public bool IsHex { get; }

    /// <summary>Creates a string from bytes decoded out of a literal <c>(...)</c> form.</summary>
    public static PdfString FromLiteral(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new PdfString(bytes, isHex: false);
    }

    /// <summary>Creates a string from bytes decoded out of a hexadecimal <c>&lt;...&gt;</c> form.</summary>
    public static PdfString FromHex(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new PdfString(bytes, isHex: true);
    }

    /// <summary>
    /// Best-effort text decoding: UTF-16BE when the bytes start with the §7.9.2.2 byte-order
    /// mark, otherwise a Latin-1 (byte-for-byte) interpretation.
    /// </summary>
    public string GetText()
    {
        var span = Bytes.Span;
        if (span.Length >= 2 && span[0] == Utf16BigEndianBom[0] && span[1] == Utf16BigEndianBom[1])
        {
            return Encoding.BigEndianUnicode.GetString(span[2..]);
        }

        return Encoding.Latin1.GetString(span);
    }

    /// <inheritdoc/>
    public override string ToString() => GetText();
}
