namespace PlumePdf.Filters;

/// <summary>
/// The <c>RunLengthDecode</c> filter (ISO 32000-1 §7.4.5): a run-length codec over a
/// sequence of (length-byte, payload) pairs. A length byte <c>0-127</c> introduces
/// <c>length + 1</c> literal bytes copied as-is; a length byte <c>129-255</c> introduces a
/// single byte to be repeated <c>257 - length</c> times; the length byte <c>128</c> marks
/// end-of-data. A truncated run or a missing EOD marker is lenient-by-default:
/// recorded as a <c>PLUME3xxx</c> diagnostic and decoded as far as the input allows, or
/// thrown under <see cref="PdfOptions.Strict"/>.
/// </summary>
internal static class RunLengthFilter
{
    private const byte EodLength = 128;

    /// <summary>Decodes run-length-encoded <paramref name="data"/> to raw bytes.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var maxBytes = options.MaxDecompressedStreamBytes;
        var output = new List<byte>((int)Math.Min(data.Length, maxBytes));
        var i = 0;
        var sawEod = false;

        while (i < data.Length)
        {
            // Same decompression-bomb cap Flate and LZW enforce: run-length amplifies up to
            // 64x per stage and chained /RunLengthDecode entries multiply, so the per-stage
            // output must be bounded (proven at 33 MB from 2 input bytes).
            if (output.Count > maxBytes)
            {
                throw new PlumePdfException("PLUME3042", $"RunLengthDecode: decompressed output exceeds the {maxBytes}-byte cap (PdfOptions.MaxDecompressedStreamBytes) - refusing to continue (possible decompression bomb).");
            }

            var lengthOffset = i;
            var length = data[i++];

            if (length == EodLength)
            {
                sawEod = true;
                break;
            }

            if (length < EodLength)
            {
                var wanted = length + 1;
                var available = Math.Min(wanted, data.Length - i);
                if (available < wanted)
                {
                    FilterDiagnostics.ReportDeviation("PLUME3041", $"RunLengthDecode: the literal run at offset {lengthOffset} declared {wanted} byte(s) but only {available} remained; using what was available.", options, diagnostics, subject);
                }

                output.AddRange(data.Slice(i, available));
                i += available;
            }
            else
            {
                var repeatCount = 257 - length;
                if (i >= data.Length)
                {
                    FilterDiagnostics.ReportDeviation("PLUME3041", $"RunLengthDecode: the repeated run at offset {lengthOffset} declared {repeatCount} repetition(s) but the stream ended before the byte to repeat.", options, diagnostics, subject);
                    break;
                }

                var value = data[i++];
                for (var j = 0; j < repeatCount; j++)
                {
                    output.Add(value);
                }
            }
        }

        // The loop-top check bounds growth between iterations; this closes the final
        // iteration's overshoot so the cap is exact, not cap-plus-one-run.
        if (output.Count > maxBytes)
        {
            throw new PlumePdfException("PLUME3042", $"RunLengthDecode: decompressed output exceeds the {maxBytes}-byte cap (PdfOptions.MaxDecompressedStreamBytes) - refusing to continue (possible decompression bomb).");
        }

        if (!sawEod)
        {
            FilterDiagnostics.ReportDeviation("PLUME3040", "RunLengthDecode: input ended without the 128 EOD length byte; decoding the bytes seen so far.", options, diagnostics, subject);
        }

        return [.. output];
    }
}

/// <summary>Bridges the internal, object-model-free <see cref="RunLengthFilter"/> to the public <see cref="IPdfFilter"/> seam.</summary>
internal sealed class RunLengthFilterAdapter : IPdfFilter
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        RunLengthFilter.Decode(data.Span, options, diagnostics, subject);
}
