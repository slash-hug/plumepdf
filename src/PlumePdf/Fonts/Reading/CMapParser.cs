using System.Text;
using PlumePdf.Objects;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// Parses a PDF CMap stream's PostScript-subset syntax (ISO 32000-1 §9.7.5.3) into a
/// <see cref="CMap"/>: <c>begincodespacerange</c>/<c>endcodespacerange</c>,
/// <c>begincidrange</c>/<c>begincidchar</c> (for a Type0 font's embedded <c>/Encoding</c>
/// CMap), and <c>beginbfrange</c>/<c>beginbfchar</c> (for a <c>/ToUnicode</c> CMap) — the same
/// syntax covers both directions, so one parser serves both consumers. <c>usecmap</c> is
/// recorded on the result (<see cref="CMap.UseCMapName"/>) but never resolved or merged
/// (deferred for this phase). Reuses <see cref="PdfTokenizer"/> for lexing rather than a second
/// lexer. Lenient by default: a malformed entry is skipped and reported to the
/// diagnostics collection (or thrown as <see cref="PlumePdfException"/> under
/// <see cref="PdfOptions.Strict"/>) rather than aborting the whole parse; a stream declaring
/// more entries than the configured limit stops there with a partial map instead of
/// growing unbounded on hostile input.
/// </summary>
internal static class CMapParser
{
    /// <summary>
    /// The entry-count ceiling used when a caller doesn't supply one — <see cref="ExtractionFontFactory"/>
    /// always passes <c>PdfOptions.MaxCMapEntries</c> explicitly instead of relying on this
    /// default; it exists for callers exercising this parser standalone (e.g. tests).
    /// </summary>
    internal const int DefaultMaxEntries = 100_000;

    /// <summary>Parses a complete CMap stream's decoded bytes.</summary>
    /// <param name="content">The CMap stream's already filter-decoded bytes.</param>
    /// <param name="options">Active options; <see cref="PdfOptions.Strict"/> turns a malformed entry or an exceeded entry limit into a thrown <see cref="PlumePdfException"/> instead of a diagnostic.</param>
    /// <param name="diagnostics">Where recoverable deviations are recorded, if any.</param>
    /// <param name="maxEntries">The maximum number of declared entries (codespace ranges, cid/bf ranges and chars, including each element of an array-form <c>beginbfrange</c> target) this parse will retain before stopping with a partial map. Defaults to <see cref="DefaultMaxEntries"/>.</param>
    public static CMap Parse(ReadOnlySpan<byte> content, PdfOptions options, DiagnosticCollection? diagnostics, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(options);

        var codespaceRanges = new List<CMapCodespaceRange>();
        var cidSingles = new Dictionary<uint, int>();
        var cidRanges = new List<(uint Lo, uint Hi, int StartCid)>();
        var bfSingles = new Dictionary<uint, string>();
        var bfRanges = new List<(uint Lo, uint Hi, byte[] DstLo)>();
        string? useCMapName = null;
        var entryCount = 0;
        string? pendingName = null;

        var tokenizer = new PdfTokenizer(content);
        while (entryCount <= maxEntries && tokenizer.Read())
        {
            switch (tokenizer.TokenType)
            {
                case PdfTokenType.Name:
                    pendingName = Encoding.Latin1.GetString(tokenizer.ValueSpan);
                    continue;

                case PdfTokenType.Keyword:
                    if (tokenizer.ValueSpan.SequenceEqual("usecmap"u8))
                    {
                        useCMapName = pendingName;
                    }
                    else if (tokenizer.ValueSpan.SequenceEqual("begincodespacerange"u8))
                    {
                        ParseCodespaceRanges(ref tokenizer, codespaceRanges, options, diagnostics, ref entryCount, maxEntries);
                    }
                    else if (tokenizer.ValueSpan.SequenceEqual("begincidrange"u8))
                    {
                        ParseCidRanges(ref tokenizer, cidRanges, options, diagnostics, ref entryCount, maxEntries);
                    }
                    else if (tokenizer.ValueSpan.SequenceEqual("begincidchar"u8))
                    {
                        ParseCidChars(ref tokenizer, cidSingles, options, diagnostics, ref entryCount, maxEntries);
                    }
                    else if (tokenizer.ValueSpan.SequenceEqual("beginbfrange"u8))
                    {
                        ParseBfRanges(ref tokenizer, bfRanges, bfSingles, options, diagnostics, ref entryCount, maxEntries);
                    }
                    else if (tokenizer.ValueSpan.SequenceEqual("beginbfchar"u8))
                    {
                        ParseBfChars(ref tokenizer, bfSingles, options, diagnostics, ref entryCount, maxEntries);
                    }

                    break;
            }

            pendingName = null;
        }

        return new CMap(codespaceRanges, cidSingles, cidRanges, bfSingles, bfRanges, useCMapName);
    }

    private static void ParseCodespaceRanges(ref PdfTokenizer tokenizer, List<CMapCodespaceRange> ranges, PdfOptions options, DiagnosticCollection? diagnostics, ref int entryCount, int maxEntries)
    {
        while (tokenizer.Read())
        {
            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("endcodespacerange"u8))
            {
                return;
            }

            if (tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "begincodespacerange");
                continue;
            }

            var low = tokenizer.ValueSpan.ToArray();

            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "begincodespacerange");
                continue;
            }

            var high = tokenizer.ValueSpan.ToArray();

            if (!TryCountEntry(ref entryCount, maxEntries, options, diagnostics))
            {
                return;
            }

            ranges.Add(new CMapCodespaceRange(low, high));
        }
    }

    private static void ParseCidRanges(ref PdfTokenizer tokenizer, List<(uint Lo, uint Hi, int StartCid)> ranges, PdfOptions options, DiagnosticCollection? diagnostics, ref int entryCount, int maxEntries)
    {
        while (tokenizer.Read())
        {
            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("endcidrange"u8))
            {
                return;
            }

            if (tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "begincidrange");
                continue;
            }

            var lo = BytesToCode(tokenizer.ValueSpan);

            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "begincidrange");
                continue;
            }

            var hi = BytesToCode(tokenizer.ValueSpan);

            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
            {
                ReportMalformedEntry(options, diagnostics, "begincidrange");
                continue;
            }

            if (!TryCountEntry(ref entryCount, maxEntries, options, diagnostics))
            {
                return;
            }

            ranges.Add((lo, hi, (int)tokenizer.IntegerValue));
        }
    }

    private static void ParseCidChars(ref PdfTokenizer tokenizer, Dictionary<uint, int> singles, PdfOptions options, DiagnosticCollection? diagnostics, ref int entryCount, int maxEntries)
    {
        while (tokenizer.Read())
        {
            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("endcidchar"u8))
            {
                return;
            }

            if (tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "begincidchar");
                continue;
            }

            var code = BytesToCode(tokenizer.ValueSpan);

            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
            {
                ReportMalformedEntry(options, diagnostics, "begincidchar");
                continue;
            }

            if (!TryCountEntry(ref entryCount, maxEntries, options, diagnostics))
            {
                return;
            }

            singles[code] = (int)tokenizer.IntegerValue;
        }
    }

    private static void ParseBfChars(ref PdfTokenizer tokenizer, Dictionary<uint, string> singles, PdfOptions options, DiagnosticCollection? diagnostics, ref int entryCount, int maxEntries)
    {
        while (tokenizer.Read())
        {
            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("endbfchar"u8))
            {
                return;
            }

            if (tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "beginbfchar");
                continue;
            }

            var code = BytesToCode(tokenizer.ValueSpan);

            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "beginbfchar");
                continue;
            }

            if (!TryCountEntry(ref entryCount, maxEntries, options, diagnostics))
            {
                return;
            }

            singles[code] = DecodeUtf16BigEndianEntry(tokenizer.ValueSpan);
        }
    }

    private static void ParseBfRanges(ref PdfTokenizer tokenizer, List<(uint Lo, uint Hi, byte[] DstLo)> ranges, Dictionary<uint, string> singles, PdfOptions options, DiagnosticCollection? diagnostics, ref int entryCount, int maxEntries)
    {
        while (tokenizer.Read())
        {
            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("endbfrange"u8))
            {
                return;
            }

            if (tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "beginbfrange");
                continue;
            }

            var lo = BytesToCode(tokenizer.ValueSpan);

            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.HexString)
            {
                ReportMalformedEntry(options, diagnostics, "beginbfrange");
                continue;
            }

            var hi = BytesToCode(tokenizer.ValueSpan);

            if (!tokenizer.Read())
            {
                return;
            }

            if (tokenizer.TokenType == PdfTokenType.HexString)
            {
                var dst = tokenizer.ValueSpan.ToArray();
                if (!TryCountEntry(ref entryCount, maxEntries, options, diagnostics))
                {
                    return;
                }

                ranges.Add((lo, hi, dst));
            }
            else if (tokenizer.TokenType == PdfTokenType.ArrayStart)
            {
                var index = 0u;
                while (tokenizer.Read() && tokenizer.TokenType != PdfTokenType.ArrayEnd)
                {
                    var code = lo + index;
                    index++;

                    if (tokenizer.TokenType != PdfTokenType.HexString)
                    {
                        ReportMalformedEntry(options, diagnostics, "beginbfrange");
                        continue;
                    }

                    if (code > hi)
                    {
                        // More array targets than the declared range spans - the excess is
                        // ignored (lenient: the range bound is authoritative).
                        continue;
                    }

                    if (!TryCountEntry(ref entryCount, maxEntries, options, diagnostics))
                    {
                        return;
                    }

                    singles[code] = DecodeUtf16BigEndianEntry(tokenizer.ValueSpan);
                }
            }
            else
            {
                ReportMalformedEntry(options, diagnostics, "beginbfrange");
            }
        }
    }

    /// <summary>Converts up to the last 4 bytes of a hex-string code to a <see cref="uint"/> (best-effort for a longer-than-4-byte code, which is not a real-world shape).</summary>
    private static uint BytesToCode(ReadOnlySpan<byte> bytes)
    {
        var value = 0u;
        var start = Math.Max(0, bytes.Length - 4);
        for (var i = start; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }

    private static string DecodeUtf16BigEndianEntry(ReadOnlySpan<byte> bytes)
    {
        var count = bytes.Length / 2;
        if (count == 0)
        {
            return string.Empty;
        }

        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = (char)((bytes[2 * i] << 8) | bytes[(2 * i) + 1]);
        }

        return new string(chars);
    }

    private static bool TryCountEntry(ref int count, int maxEntries, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        count++;
        if (count <= maxEntries)
        {
            return true;
        }

        if (count == maxEntries + 1)
        {
            ReportDeviation(options, diagnostics, "PLUME8015", $"CMap declares more entries than the configured limit of {maxEntries}; parsing stopped with a partial map.");
        }

        return false;
    }

    private static void ReportMalformedEntry(PdfOptions options, DiagnosticCollection? diagnostics, string blockKeyword) =>
        ReportDeviation(options, diagnostics, "PLUME8016", $"Malformed entry inside a CMap '{blockKeyword}' block; the entry was skipped.");

    private static void ReportDeviation(PdfOptions options, DiagnosticCollection? diagnostics, string code, string message)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}
