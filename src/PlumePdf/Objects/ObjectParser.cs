using System.Text;
using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>
/// Turns a <see cref="PdfTokenizer"/>'s token stream into a <see cref="PdfObject"/> graph
/// (ISO 32000-1 §7.3), including indirect-object framing (<c>N G obj ... endobj</c>) and
/// stream payload extraction (§7.3.8). Nesting depth is capped by
/// <see cref="PdfOptions.MaxObjectNestingDepth"/> — a pathological
/// <c>[[[[[...</c> input throws a coded exception instead of recursing until the process
/// stack overflows. Malformed lexical or structural content is tolerated under lenient
/// reading (recorded to <see cref="DiagnosticCollection"/>, a best-effort value
/// substituted) and rejected outright only under <see cref="PdfOptions.Strict"/> — the
/// same recovery-ladder philosophy as the rest of the reading engine.
/// </summary>
internal static class ObjectParser
{
    /// <summary>
    /// Parses one indirect object's complete <c>N G obj ... endobj</c> framing starting at
    /// the beginning of <paramref name="buffer"/>, returning its value and, via
    /// <paramref name="reference"/>, the object number and generation read from the framing
    /// itself (which the caller should cross-check against where it expected to find this
    /// object, per the recovery ladder).
    /// </summary>
    public static PdfObject ParseIndirectObject(ReadOnlySpan<byte> buffer, PdfOptions options, DiagnosticCollection? diagnostics, out IndirectReference reference)
    {
        var tokenizer = new PdfTokenizer(buffer);

        if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
        {
            throw new PlumePdfException("PLUME2021", "Expected an object number at the start of an indirect object.");
        }

        var number = (int)tokenizer.IntegerValue;

        if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
        {
            throw new PlumePdfException("PLUME2021", "Expected a generation number after the object number.");
        }

        var generation = (int)tokenizer.IntegerValue;

        if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Keyword || !tokenizer.ValueSpan.SequenceEqual("obj"u8))
        {
            throw new PlumePdfException("PLUME2021", "Expected the 'obj' keyword after the object and generation numbers.");
        }

        reference = new IndirectReference(number, generation);
        var value = ParseValue(buffer, ref tokenizer, options, diagnostics);

        var beforeEndobj = tokenizer.Consumed;
        if (!(tokenizer.Read() && tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("endobj"u8)))
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME2022", DiagnosticSeverity.Warning, $"Expected 'endobj' for object {reference}; continuing without it.", beforeEndobj, reference));
        }

        return value;
    }

    /// <summary>Parses a single value (any of the eight object kinds) starting at the tokenizer's current position.</summary>
    public static PdfObject ParseValue(ReadOnlySpan<byte> buffer, ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics, int depth = 0)
    {
        if (!tokenizer.Read())
        {
            throw new PlumePdfException("PLUME2011", "Unexpected end of input while parsing an object value.");
        }

        return ParseCurrentToken(buffer, ref tokenizer, options, diagnostics, depth);
    }

    private static PdfObject ParseCurrentToken(ReadOnlySpan<byte> buffer, ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics, int depth)
    {
        ReportIfMalformed(ref tokenizer, options, diagnostics);

        switch (tokenizer.TokenType)
        {
            case PdfTokenType.Integer:
                return ParseIntegerOrReference(ref tokenizer, tokenizer.IntegerValue);

            case PdfTokenType.Real:
                return PdfNumber.Get(tokenizer.RealValue);

            case PdfTokenType.Name:
                return PdfName.Get(Encoding.Latin1.GetString(tokenizer.ValueSpan));

            case PdfTokenType.LiteralString:
                return PdfString.FromLiteral(tokenizer.ValueSpan.ToArray());

            case PdfTokenType.HexString:
                return PdfString.FromHex(tokenizer.ValueSpan.ToArray());

            case PdfTokenType.ArrayStart:
                GuardDepth(options, depth + 1);
                return ParseArray(buffer, ref tokenizer, options, diagnostics, depth + 1);

            case PdfTokenType.DictStart:
                GuardDepth(options, depth + 1);
                return ParseDictionaryOrStream(buffer, ref tokenizer, options, diagnostics, depth + 1);

            case PdfTokenType.Keyword:
                return ParseKeyword(ref tokenizer, options, diagnostics);

            default:
                {
                    var message = $"Unexpected token type {tokenizer.TokenType} while parsing a value near offset {tokenizer.Consumed}; substituting null.";
                    ReportDeviation("PLUME2012", message, tokenizer.Consumed, options, diagnostics);
                    return PdfNull.Instance;
                }
        }
    }

    private static void GuardDepth(PdfOptions options, int depth)
    {
        if (depth > options.MaxObjectNestingDepth)
        {
            throw new PlumePdfException("PLUME2010", $"Object nesting exceeded PdfOptions.MaxObjectNestingDepth ({options.MaxObjectNestingDepth}).");
        }
    }

    private static void ReportIfMalformed(ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (!tokenizer.IsMalformed)
        {
            return;
        }

        var message = $"Malformed {tokenizer.TokenType} token near offset {tokenizer.Consumed}; a best-effort value was substituted.";
        ReportDeviation("PLUME2013", message, tokenizer.Consumed, options, diagnostics);
    }

    private static void ReportDeviation(string code, string message, long? offset, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, offset));
    }

    private static PdfObject ParseIntegerOrReference(ref PdfTokenizer tokenizer, long firstValue)
    {
        var checkpoint = tokenizer.Consumed;

        if (tokenizer.Read() && tokenizer.TokenType == PdfTokenType.Integer && !tokenizer.IsMalformed)
        {
            var generation = tokenizer.IntegerValue;

            if (tokenizer.Read() && tokenizer.TokenType == PdfTokenType.Keyword && !tokenizer.IsMalformed && tokenizer.ValueSpan.SequenceEqual("R"u8)
                && firstValue >= 0 && generation >= 0)
            {
                return new PdfReference(new IndirectReference((int)firstValue, (int)generation));
            }
        }

        tokenizer.Reset(checkpoint);
        return PdfNumber.Get(firstValue);
    }

    private static PdfObject ParseKeyword(ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (tokenizer.ValueSpan.SequenceEqual("true"u8))
        {
            return PdfBoolean.True;
        }

        if (tokenizer.ValueSpan.SequenceEqual("false"u8))
        {
            return PdfBoolean.False;
        }

        if (tokenizer.ValueSpan.SequenceEqual("null"u8))
        {
            return PdfNull.Instance;
        }

        var text = Encoding.Latin1.GetString(tokenizer.ValueSpan);
        var message = $"Unexpected keyword '{text}' while parsing a value near offset {tokenizer.Consumed}; substituting null.";
        ReportDeviation("PLUME2016", message, tokenizer.Consumed, options, diagnostics);
        return PdfNull.Instance;
    }

    private static PdfArray ParseArray(ReadOnlySpan<byte> buffer, ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics, int depth)
    {
        var array = new PdfArray();

        while (true)
        {
            if (!tokenizer.Read())
            {
                ReportDeviation("PLUME2014", "Array ran to end of input without a closing ']'.", tokenizer.Consumed, options, diagnostics);
                break;
            }

            if (tokenizer.TokenType == PdfTokenType.ArrayEnd)
            {
                break;
            }

            array.Add(ParseCurrentToken(buffer, ref tokenizer, options, diagnostics, depth));
        }

        return array;
    }

    private static PdfObject ParseDictionaryOrStream(ReadOnlySpan<byte> buffer, ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics, int depth)
    {
        var dict = new PdfDictionary();

        while (true)
        {
            if (!tokenizer.Read())
            {
                ReportDeviation("PLUME2017", "Dictionary ran to end of input without a closing '>>'.", tokenizer.Consumed, options, diagnostics);
                break;
            }

            if (tokenizer.TokenType == PdfTokenType.DictEnd)
            {
                break;
            }

            if (tokenizer.TokenType != PdfTokenType.Name)
            {
                var message = $"Expected a name as a dictionary key near offset {tokenizer.Consumed}; skipping this entry.";
                ReportDeviation("PLUME2018", message, tokenizer.Consumed, options, diagnostics);
                continue;
            }

            var key = PdfName.Get(Encoding.Latin1.GetString(tokenizer.ValueSpan));
            var value = ParseValue(buffer, ref tokenizer, options, diagnostics, depth);
            dict.Set(key, value);
        }

        var beforeStreamCheck = tokenizer.Consumed;
        if (tokenizer.Read())
        {
            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("stream"u8))
            {
                return ParseStreamPayload(buffer, ref tokenizer, dict, options, diagnostics);
            }

            tokenizer.Reset(beforeStreamCheck);
        }

        return dict;
    }

    private static PdfStream ParseStreamPayload(ReadOnlySpan<byte> buffer, ref PdfTokenizer tokenizer, PdfDictionary dict, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var position = tokenizer.Consumed;

        // The "stream" keyword is followed by CRLF or a bare LF (a bare CR is nonconforming
        // but tolerated) before the raw payload begins, per §7.3.8.1.
        if (position < buffer.Length && buffer[position] == (byte)'\r')
        {
            position++;
        }

        if (position < buffer.Length && buffer[position] == (byte)'\n')
        {
            position++;
        }

        var payloadStart = position;
        var directLength = TryGetDirectLength(dict);
        int payloadLength;

        if (directLength is int knownLength && knownLength >= 0 && payloadStart + knownLength <= buffer.Length
            && LooksLikeEndstream(buffer, payloadStart + knownLength))
        {
            payloadLength = knownLength;
        }
        else
        {
            const string message = "Stream /Length was missing, indirect, or did not land on 'endstream'; recovered by scanning for 'endstream'.";
            ReportDeviation("PLUME2019", message, payloadStart, options, diagnostics);

            var remaining = buffer[payloadStart..];
            var found = PdfScanner.FindAll(remaining, "endstream"u8, remaining.Length);
            payloadLength = found.Count > 0 ? found[0] : remaining.Length;

            // The EOL immediately before "endstream" is a delimiter, not payload data
            // (§7.3.8.1: "This marker shall not be included in the stream length").
            if (payloadLength > 0 && remaining[payloadLength - 1] == (byte)'\n')
            {
                payloadLength--;
                if (payloadLength > 0 && remaining[payloadLength - 1] == (byte)'\r')
                {
                    payloadLength--;
                }
            }
            else if (payloadLength > 0 && remaining[payloadLength - 1] == (byte)'\r')
            {
                payloadLength--;
            }
        }

        var rawBytes = buffer.Slice(payloadStart, payloadLength).ToArray();
        tokenizer.Reset(payloadStart + payloadLength);

        if (!(tokenizer.Read() && tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("endstream"u8)))
        {
            ReportDeviation("PLUME2020", "Expected 'endstream' after the stream payload; continuing without it.", tokenizer.Consumed, options, diagnostics);
            tokenizer.Reset(payloadStart + payloadLength);
        }

        return new PdfStream(dict, rawBytes);
    }

    private static int? TryGetDirectLength(PdfDictionary dict) =>
        dict.TryGetValue(PdfName.Length, out var value) && value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var converted) ? converted : null;

    private static bool LooksLikeEndstream(ReadOnlySpan<byte> buffer, int position)
    {
        var p = position;
        while (p < buffer.Length && PdfScanner.IsWhitespace(buffer[p]))
        {
            p++;
        }

        return buffer[p..].StartsWith("endstream"u8);
    }
}
