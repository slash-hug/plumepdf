using System.Collections.Concurrent;

namespace PlumePdf.Objects;

/// <summary>
/// Decodes object streams (<c>/Type /ObjStm</c>, ISO 32000-1 §7.5.7): a stream whose payload
/// is a header of <c>N</c> <c>(object number, offset)</c> pairs followed by that many bare
/// object values (no <c>N G obj ... endobj</c> framing — just the value). Decoding a given
/// stream is memoized per stream object number so resolving several objects out of the same
/// stream only decodes and re-parses it once. <c>/Extends</c> chains (§7.5.7, a stream
/// numbering that continues a previous one) are walked purely to guard against a cycle —
/// each entry's own cross-reference record already names the exact stream and index that
/// holds it, so nothing about value resolution actually needs to follow <c>/Extends</c>.
/// </summary>
internal sealed class ObjectStreamReader
{
    private readonly ConcurrentDictionary<int, PdfObject[]> _cache = new();

    /// <summary>
    /// Returns the object at <paramref name="indexInStream"/> within object stream
    /// <paramref name="streamObjectNumber"/>, decoding (and memoizing) the stream on first access.
    /// </summary>
    /// <param name="streamObjectNumber">The object number of the containing object stream.</param>
    /// <param name="indexInStream">The zero-based index of the desired object within the stream's header.</param>
    /// <param name="resolve">Resolves any object number to its value — used both for the stream itself and to walk <c>/Extends</c>.</param>
    /// <param name="options">The active options, including resource limits.</param>
    /// <param name="diagnostics">The collection to append recoverable-deviation entries to, if any.</param>
    public PdfObject GetObject(int streamObjectNumber, int indexInStream, Func<int, PdfObject?> resolve, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var values = _cache.GetOrAdd(streamObjectNumber, num => DecodeObjectStream(num, resolve, options, diagnostics));

        if (indexInStream < 0 || indexInStream >= values.Length)
        {
            throw new PlumePdfException("PLUME2050", $"Object stream {streamObjectNumber} has no entry at index {indexInStream} (it declares {values.Length}).");
        }

        return values[indexInStream];
    }

    private static PdfObject[] DecodeObjectStream(int streamObjectNumber, Func<int, PdfObject?> resolve, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (resolve(streamObjectNumber) is not PdfStream stream)
        {
            throw new PlumePdfException("PLUME2053", $"Object {streamObjectNumber} is not an object stream.");
        }

        ValidateExtendsChain(streamObjectNumber, stream, resolve, options);

        var dict = stream.Dictionary;
        var n = GetInt(dict, PdfName.N) ?? throw new PlumePdfException("PLUME2054", $"Object stream {streamObjectNumber} is missing /N.");
        var first = GetInt(dict, PdfName.First) ?? throw new PlumePdfException("PLUME2054", $"Object stream {streamObjectNumber} is missing /First.");

        var decoded = stream.GetDecodedBytes(options.Filters, options);

        // /N is attacker-controlled and untrustworthy on its own: a hostile /N of
        // e.g. 400,000,000 over an 8-byte decoded payload must not allocate multi-gigabyte
        // arrays. Bound it by what the decoded payload could plausibly hold (each header
        // pair needs at least 4 bytes: two digits and two separators) as well as by an
        // explicit cap, whichever is tighter.
        const int minHeaderPairBytes = 4;
        var maxPossibleEntries = decoded.Length / minHeaderPairBytes;
        var effectiveN = n;
        if (effectiveN < 0 || effectiveN > maxPossibleEntries || effectiveN > options.MaxObjectStreamEntries)
        {
            var clamped = Math.Clamp(effectiveN, 0, Math.Min(maxPossibleEntries, options.MaxObjectStreamEntries));
            ReportDeviation("PLUME2057", $"Object stream {streamObjectNumber} declares /N {effectiveN}, more than its {decoded.Length}-byte decoded payload or PdfOptions.MaxObjectStreamEntries ({options.MaxObjectStreamEntries}) could plausibly hold; clamping to {clamped}.", null, options, diagnostics);
            effectiveN = clamped;
        }

        var header = ParseHeader(decoded, effectiveN, options, diagnostics);

        var result = new PdfObject[header.Length];
        for (var i = 0; i < header.Length; i++)
        {
            var valueStart = first + header[i].OffsetInStream;
            if (valueStart < 0 || valueStart > decoded.Length)
            {
                ReportDeviation("PLUME2055", $"Object stream {streamObjectNumber} entry {i} has an out-of-range offset; substituting null.", valueStart, options, diagnostics);
                result[i] = PdfNull.Instance;
                continue;
            }

            var slice = decoded.AsSpan(valueStart).ToArray();
            var tokenizer = new PdfTokenizer(slice);
            result[i] = ObjectParser.ParseValue(slice, ref tokenizer, options, diagnostics);
        }

        return result;
    }

    private static void ValidateExtendsChain(int startingStreamObjectNumber, PdfStream startingStream, Func<int, PdfObject?> resolve, PdfOptions options)
    {
        var visited = new HashSet<int> { startingStreamObjectNumber };
        var current = startingStream;

        while (current.Dictionary.TryGetValue(PdfName.Extends, out var extendsValue) && extendsValue is PdfReference extendsRef)
        {
            var nextNumber = extendsRef.Target.Number;
            if (!visited.Add(nextNumber))
            {
                throw new PlumePdfException("PLUME2051", $"Object stream {startingStreamObjectNumber}'s /Extends chain cycles back to object {nextNumber}.");
            }

            if (visited.Count > options.MaxObjectStreamExtendsDepth)
            {
                throw new PlumePdfException("PLUME2052", $"Object stream /Extends chain exceeded PdfOptions.MaxObjectStreamExtendsDepth ({options.MaxObjectStreamExtendsDepth}).");
            }

            if (resolve(nextNumber) is not PdfStream nextStream)
            {
                break; // dangling /Extends - nothing further to validate
            }

            current = nextStream;
        }
    }

    private static (int Number, int OffsetInStream)[] ParseHeader(byte[] decoded, int n, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var header = new List<(int, int)>(Math.Min(n, 1024));
        var tokenizer = new PdfTokenizer(decoded);

        for (var i = 0; i < n; i++)
        {
            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
            {
                // The payload ran out before supplying every declared entry - stop here
                // rather than padding the rest with placeholder (0,0) entries that don't
                // correspond to anything real.
                ReportDeviation("PLUME2056", $"Object stream header entry {i} is missing its object number; stopping the header at {header.Count} of {n} declared entries.", tokenizer.Consumed, options, diagnostics);
                break;
            }

            var number = (int)tokenizer.IntegerValue;

            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
            {
                ReportDeviation("PLUME2056", $"Object stream header entry {i} is missing its offset; stopping the header at {header.Count} of {n} declared entries.", tokenizer.Consumed, options, diagnostics);
                break;
            }

            header.Add((number, (int)tokenizer.IntegerValue));
        }

        return [.. header];
    }

    private static int? GetInt(PdfDictionary dict, PdfName key) =>
        dict.TryGetValue(key, out var value) && value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var converted) ? converted : null;

    private static void ReportDeviation(string code, string message, long? offset, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, offset));
    }
}
