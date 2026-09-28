using PlumePdf.IO;
using PlumePdf.Objects;

namespace PlumePdf.Content;

/// <summary>
/// Scans a page's decoded content-stream bytes into an ordered list of
/// <see cref="ContentOperation"/>s (ISO 32000-1 §8.2, §9.2) — the read-side mirror of
/// <see cref="ContentStreamBuilder"/>. Reuses <see cref="PdfTokenizer"/> as the lexer
/// (<c>Keyword</c> tokens are operators; every other token kind starts an operand, parsed in
/// full via <see cref="ObjectParser.ParseValue"/> so arrays and inline dictionaries nest
/// correctly) rather than a second, duplicate lexer. Inline images
/// (<c>BI…ID…EI</c>, §8.9.7) are a dedicated escape: PlumePDF does not extract inline image
/// data in Phase 3, so the reader parses only the parameter dictionary between
/// <c>BI</c> and <c>ID</c>, then skips the binary payload up to a whitespace-delimited
/// <c>EI</c> without materializing it — enough for text extraction to survive an inline image
/// without corrupting the surrounding operator stream. Lenient by default: a
/// malformed trailing operand run with no operator records a diagnostic and stops rather than
/// throwing; only a hostile operator-count bomb throws (a resource-limit guard, not a
/// recoverable document deviation).
/// </summary>
internal static class ContentStreamReader
{
    /// <summary>
    /// The default ceiling on the number of operators a single content stream may contain
    /// before <see cref="Read"/> refuses to continue (<c>PLUME7010</c>) — a resource-limit
    /// guard against a hostile or pathological content stream (an operator-count "bomb")
    /// rather than a document deviation. Used when a caller doesn't supply an explicit
    /// <see cref="Read"/> <c>maxOperators</c> override; callers with a <c>PdfOptions</c> in
    /// scope (<c>TextExtractor</c>, <c>ImageExtractor</c>) pass <c>options.MaxContentStreamOperators</c>
    /// instead of relying on this default.
    /// </summary>
    internal const int DefaultMaxOperators = 2_000_000;

    /// <summary>
    /// Reads every operator in <paramref name="content"/> in order.
    /// </summary>
    /// <param name="content">The already filter-decoded content-stream bytes (concatenated across a page's <c>/Contents</c> array, if more than one).</param>
    /// <param name="options">Controls whether a deviation is recorded as a diagnostic or thrown (<see cref="PdfOptions.Strict"/>).</param>
    /// <param name="diagnostics">The collection recoverable deviations are appended to, or <see langword="null"/> to discard them.</param>
    /// <param name="maxOperators">Overrides <see cref="DefaultMaxOperators"/> — primarily for tests exercising the operator-count guard cheaply.</param>
    /// <exception cref="PlumePdfException"><c>PLUME7010</c> — the stream contains more than <paramref name="maxOperators"/> operators.</exception>
    public static IReadOnlyList<ContentOperation> Read(ReadOnlySpan<byte> content, PdfOptions options, DiagnosticCollection? diagnostics, int maxOperators = DefaultMaxOperators)
    {
        ArgumentNullException.ThrowIfNull(options);

        var operations = new List<ContentOperation>();
        var operands = new List<PdfObject>();
        var tokenizer = new PdfTokenizer(content);
        var operatorCount = 0;

        while (true)
        {
            var before = tokenizer.Consumed;
            if (!tokenizer.Read())
            {
                if (operands.Count > 0)
                {
                    ReportDeviation("PLUME7011", $"Content stream ended with {operands.Count} operand(s) pending and no closing operator near offset {before}; discarding the trailing operands.", options, diagnostics);
                }

                break;
            }

            if (tokenizer.TokenType == PdfTokenType.Keyword && !IsLiteralKeyword(tokenizer.ValueSpan))
            {
                var operatorText = System.Text.Encoding.ASCII.GetString(tokenizer.ValueSpan);

                if (operatorText == "BI")
                {
                    ReadInlineImage(content, ref tokenizer, options, diagnostics, before, operations);
                    operands.Clear();
                    operatorCount++;
                    GuardOperatorCount(operatorCount, maxOperators);
                    continue;
                }

                operations.Add(new ContentOperation(operatorText, operands.Count == 0 ? [] : [.. operands], before));
                operands.Clear();
                operatorCount++;
                GuardOperatorCount(operatorCount, maxOperators);
                continue;
            }

            // Not an operator keyword: this token starts an operand. Rewind to its start and
            // let ObjectParser parse it fully (recursing into arrays/dictionaries as needed,
            // applying PdfOptions.MaxObjectNestingDepth) rather than re-implementing that here.
            tokenizer.Reset(before);
            operands.Add(ObjectParser.ParseValue(content, ref tokenizer, options, diagnostics));
        }

        return operations;
    }

    // "true"/"false"/"null" lex as Keyword tokens but are operand values (e.g. inline-image
    // dictionary entries, or a BDC/marked-content boolean property), not operator names —
    // ObjectParser.ParseValue already knows how to turn them into PdfBoolean/PdfNull.
    private static bool IsLiteralKeyword(ReadOnlySpan<byte> value) =>
        value.SequenceEqual("true"u8) || value.SequenceEqual("false"u8) || value.SequenceEqual("null"u8);

    private static void GuardOperatorCount(int count, int maxOperators)
    {
        if (count > maxOperators)
        {
            throw new PlumePdfException("PLUME7010", $"Content stream contains more than {maxOperators} operators; refusing to continue (a resource-limit guard against a hostile or pathological content stream).");
        }
    }

    // Parses the inline image's parameter dictionary (BI ... key/value pairs ... ID), then
    // skips the binary payload up to a whitespace-delimited "EI" (§8.9.7). The payload itself
    // is never materialized — callers that care an inline image was present see
    // it as a single ContentOperation with Operator "BI" and the parsed dictionary as its only
    // operand. Because this skip is the only place both ends of the BI…EI run are ever known,
    // the exact raw byte span is recorded on the operation (ContentOperation.InlineImageSpan)
    // so a consumer that must re-emit the image verbatim (ContentStreamEditor's rebuild) never
    // has to re-derive it by guesswork; a malformed run with no locatable terminator records
    // no span at all, which such a consumer must treat as "drop the image" (fail safe).
    private static void ReadInlineImage(ReadOnlySpan<byte> content, ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics, long biOffset, List<ContentOperation> operations)
    {
        var dict = new PdfDictionary();

        while (true)
        {
            var before = tokenizer.Consumed;
            if (!tokenizer.Read())
            {
                ReportDeviation("PLUME7012", $"Inline image beginning at offset {biOffset} ran to end of input before 'ID'; discarding it.", options, diagnostics);
                operations.Add(new ContentOperation("BI", [dict], biOffset));
                return;
            }

            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("ID"u8))
            {
                break;
            }

            if (tokenizer.TokenType != PdfTokenType.Name)
            {
                // Not a well-formed "key value" pair — bail out to the ID/EI skip rather than
                // looping forever on unexpected content.
                ReportDeviation("PLUME7012", $"Inline image beginning at offset {biOffset} has a malformed parameter dictionary near offset {before}; scanning ahead for 'ID'.", options, diagnostics);
                tokenizer.Reset(before);
                SkipToKeyword(content, ref tokenizer, "ID"u8);
                break;
            }

            var key = PdfName.Get(System.Text.Encoding.Latin1.GetString(tokenizer.ValueSpan));
            var value = ObjectParser.ParseValue(content, ref tokenizer, options, diagnostics);
            dict.Set(key, value);
        }

        // Exactly one whitespace byte separates "ID" from the binary payload (§8.9.7).
        var dataStart = tokenizer.Consumed;
        if (dataStart < content.Length && PdfScanner.IsWhitespace(content[dataStart]))
        {
            dataStart++;
        }

        var eiOffset = FindInlineImageEnd(content, dataStart);
        if (eiOffset < 0)
        {
            ReportDeviation("PLUME7012", $"Inline image beginning at offset {biOffset} has no whitespace-delimited 'EI' terminator; treating the rest of the content stream as its payload.", options, diagnostics);
            tokenizer.Reset(content.Length);
            operations.Add(new ContentOperation("BI", [dict], biOffset));
            return;
        }

        tokenizer.Reset(eiOffset + 2);

        // The payload alone: after ID's single whitespace, up to (not including) the
        // whitespace delimiter FindInlineImageEnd required before "EI" — what the render path
        // hands the image resolver (T-0.3). The delimiter is absent only when EI sits at the
        // payload start (a zero-length payload).
        var dataEnd = eiOffset > dataStart && PdfScanner.IsWhitespace(content[eiOffset - 1]) ? eiOffset - 1 : eiOffset;
        operations.Add(new ContentOperation("BI", [dict], biOffset)
        {
            InlineImageSpan = ((int)biOffset, eiOffset + 2),
            InlineImageDataSpan = (dataStart, dataEnd),
        });
    }

    // Finds a whitespace-delimited "EI" at or after "from" — required on both sides (or end of
    // buffer) so two stray bytes inside genuinely binary image data aren't mistaken for the
    // terminator (§8.9.7 leaves this ambiguity to the reader; whitespace-boundary scanning is
    // the conventional recovery, matching PdfPig/pdf.js).
    private static int FindInlineImageEnd(ReadOnlySpan<byte> content, int from)
    {
        var search = content[from..];
        var offset = 0;
        while (true)
        {
            var found = search[offset..].IndexOf("EI"u8);
            if (found < 0)
            {
                return -1;
            }

            var candidate = offset + found;
            var precededByWhitespaceOrStart = candidate == 0 || PdfScanner.IsWhitespace(search[candidate - 1]);
            var followedByWhitespaceOrEnd = candidate + 2 >= search.Length || PdfScanner.IsWhitespace(search[candidate + 2]);

            if (precededByWhitespaceOrStart && followedByWhitespaceOrEnd)
            {
                return from + candidate;
            }

            offset = candidate + 1;
        }
    }

    private static void SkipToKeyword(ReadOnlySpan<byte> content, ref PdfTokenizer tokenizer, ReadOnlySpan<byte> keyword)
    {
        while (tokenizer.Read())
        {
            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual(keyword))
            {
                return;
            }
        }
    }

    private static void ReportDeviation(string code, string message, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}
