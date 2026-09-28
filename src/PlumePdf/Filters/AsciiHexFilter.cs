namespace PlumePdf.Filters;

/// <summary>
/// The <c>ASCIIHexDecode</c> filter (ISO 32000-1 §7.4.2): pairs of ASCII hex digits
/// (<c>0</c>-<c>9</c>, <c>A</c>-<c>F</c>, <c>a</c>-<c>f</c>) decode to one byte each,
/// whitespace between digits is ignored, and the <c>&gt;</c> character marks end-of-data.
/// A trailing odd digit is completed with an implied low nibble of <c>0</c> per the spec's
/// own definition of that case — that is expected input, not a deviation. Anything else
/// malformed (a stray non-hex character, a missing EOD marker) is lenient-by-default:
/// recorded as a <c>PLUME3xxx</c> diagnostic and tolerated, or thrown under
/// <see cref="PdfOptions.Strict"/>.
/// </summary>
internal static class AsciiHexFilter
{
    /// <summary>Decodes ASCII-hex <paramref name="data"/> to raw bytes.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var output = new List<byte>((data.Length / 2) + 1);
        var pendingHighNibble = -1;
        var invalidByteCount = 0;
        var sawEod = false;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];

            if (b == (byte)'>')
            {
                sawEod = true;
                break;
            }

            if (IsWhitespace(b))
            {
                continue;
            }

            var nibble = HexNibble(b);
            if (nibble < 0)
            {
                // First occurrence only: a hostile payload would otherwise mint one
                // interpolated diagnostic PER INPUT BYTE (~340x memory amplification,
                // proven in review); repeats are counted and summarized after the loop.
                if (invalidByteCount++ == 0)
                {
                    FilterDiagnostics.ReportDeviation("PLUME3020", $"ASCIIHexDecode: byte 0x{b:X2} at offset {i} is not a hex digit, whitespace, or the '>' EOD marker; ignoring it (further occurrences are counted, not reported individually).", options, diagnostics, subject);
                }

                continue;
            }

            if (pendingHighNibble < 0)
            {
                pendingHighNibble = nibble;
            }
            else
            {
                output.Add((byte)((pendingHighNibble << 4) | nibble));
                pendingHighNibble = -1;
            }
        }

        if (pendingHighNibble >= 0)
        {
            // ISO 32000-1 §7.4.2: a final odd digit is completed as if followed by a 0 -
            // defined behavior, not a deviation, so no diagnostic here.
            output.Add((byte)(pendingHighNibble << 4));
        }

        if (invalidByteCount > 1)
        {
            FilterDiagnostics.ReportDeviation("PLUME3020", $"ASCIIHexDecode: {invalidByteCount} byte(s) total were not hex digits, whitespace, or the EOD marker; all were ignored.", options, diagnostics, subject);
        }

        if (!sawEod)
        {
            FilterDiagnostics.ReportDeviation("PLUME3021", "ASCIIHexDecode: input ended without the '>' EOD marker; decoding the bytes seen so far.", options, diagnostics, subject);
        }

        return [.. output];
    }

    private static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static int HexNibble(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        _ => -1,
    };
}

/// <summary>Bridges the internal, object-model-free <see cref="AsciiHexFilter"/> to the public <see cref="IPdfFilter"/> seam.</summary>
internal sealed class AsciiHexFilterAdapter : IPdfFilter
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        AsciiHexFilter.Decode(data.Span, options, diagnostics, subject);
}
