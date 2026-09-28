using System.Buffers.Binary;

namespace PlumePdf.Filters;

/// <summary>
/// The <c>ASCII85Decode</c> filter (ISO 32000-1 §7.4.3): groups of 5 base-85 digits
/// (<c>!</c> through <c>u</c>, values 0-84) decode to 4 bytes each; the single character
/// <c>z</c> is a shortcut for a whole group of 4 zero bytes when it appears at the start of
/// a group; whitespace is ignored; <c>~&gt;</c> marks end-of-data. A final partial group of
/// <c>n</c> (2-5) digits decodes <c>n - 1</c> bytes, using the same math as a full group
/// with the missing trailing digits treated as the maximum digit value (<c>84</c>, the
/// decode-side mirror of how an encoder pads a partial group with literal <c>u</c>
/// characters). Malformed input (an out-of-range byte, <c>z</c> mid-group, a 1-digit final
/// group, a missing EOD marker) is lenient-by-default: recorded as a
/// <c>PLUME3xxx</c> diagnostic and tolerated, or thrown under <see cref="PdfOptions.Strict"/>.
/// </summary>
internal static class Ascii85Filter
{
    private const int GroupSize = 5;
    private const byte MaxDigitValue = 84; // 'u' (117) - '!' (33)

    /// <summary>Decodes ASCII-85 <paramref name="data"/> to raw bytes.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var output = new List<byte>((data.Length * 4 / 5) + 4);
        Span<byte> digits = stackalloc byte[GroupSize];
        var count = 0;
        var invalidByteCount = 0;
        var misplacedZCount = 0;
        var sawEod = false;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];

            if (b == (byte)'~')
            {
                sawEod = true;
                if (i + 1 < data.Length && data[i + 1] == (byte)'>')
                {
                    i++;
                }
                else
                {
                    FilterDiagnostics.ReportDeviation("PLUME3033", $"ASCII85Decode: '~' at offset {i} was not followed by '>'; treating it as the EOD marker anyway.", options, diagnostics, subject);
                }

                break;
            }

            if (IsWhitespace(b))
            {
                continue;
            }

            if (b == (byte)'z')
            {
                if (count != 0)
                {
                    if (misplacedZCount++ == 0)
                    {
                        FilterDiagnostics.ReportDeviation("PLUME3031", $"ASCII85Decode: 'z' shortcut at offset {i} appeared after {count} digit(s) of a group instead of at its start; ignoring it (further occurrences are counted, not reported individually).", options, diagnostics, subject);
                    }

                    continue;
                }

                output.Add(0);
                output.Add(0);
                output.Add(0);
                output.Add(0);
                continue;
            }

            if (b is < (byte)'!' or > (byte)'u')
            {
                // First occurrence only — see AsciiHexFilter: per-byte diagnostics on
                // hostile input are a memory-amplification vector (proven in review).
                if (invalidByteCount++ == 0)
                {
                    FilterDiagnostics.ReportDeviation("PLUME3030", $"ASCII85Decode: byte 0x{b:X2} at offset {i} is outside the base-85 digit range '!'-'u'; ignoring it (further occurrences are counted, not reported individually).", options, diagnostics, subject);
                }

                continue;
            }

            digits[count++] = (byte)(b - '!');
            if (count == GroupSize)
            {
                AppendGroup(digits, 4, output);
                count = 0;
            }
        }

        if (!sawEod)
        {
            FilterDiagnostics.ReportDeviation("PLUME3033", "ASCII85Decode: input ended without the '~>' EOD marker; decoding the bytes seen so far.", options, diagnostics, subject);
        }

        if (invalidByteCount > 1)
        {
            FilterDiagnostics.ReportDeviation("PLUME3030", $"ASCII85Decode: {invalidByteCount} byte(s) total were outside the base-85 digit range; all were ignored.", options, diagnostics, subject);
        }

        if (misplacedZCount > 1)
        {
            FilterDiagnostics.ReportDeviation("PLUME3031", $"ASCII85Decode: {misplacedZCount} misplaced 'z' shortcut(s) total; all were ignored.", options, diagnostics, subject);
        }

        if (count == 1)
        {
            // A single leftover digit can never decode to a whole byte (each byte of output
            // needs at least 2 digits of input) - ISO 32000-1 §7.4.3 rules this out as
            // invalid, so the lone digit is discarded rather than guessed at.
            FilterDiagnostics.ReportDeviation("PLUME3032", "ASCII85Decode: a final partial group of exactly 1 digit cannot decode a byte; discarding it.", options, diagnostics, subject);
        }
        else if (count > 1)
        {
            for (var i = count; i < GroupSize; i++)
            {
                digits[i] = MaxDigitValue;
            }

            AppendGroup(digits, count - 1, output);
        }

        return [.. output];
    }

    private static void AppendGroup(ReadOnlySpan<byte> digits, int byteCount, List<byte> output)
    {
        // A well-formed group's value always fits in 32 bits (it represents 4 encoded
        // bytes); a hostile/malformed group could combine to more than that, so accumulate
        // in ulong and let the low 32 bits win rather than let unchecked uint math define
        // "correct" behavior for input that was never valid to begin with.
        var value = 0UL;
        for (var i = 0; i < GroupSize; i++)
        {
            value = (value * 85) + digits[i];
        }

        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, unchecked((uint)value));
        output.AddRange(bytes[..byteCount].ToArray());
    }

    private static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;
}

/// <summary>Bridges the internal, object-model-free <see cref="Ascii85Filter"/> to the public <see cref="IPdfFilter"/> seam.</summary>
internal sealed class Ascii85FilterAdapter : IPdfFilter
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        Ascii85Filter.Decode(data.Span, options, diagnostics, subject);
}
