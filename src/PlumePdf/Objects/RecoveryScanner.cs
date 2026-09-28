using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>
/// The last rung of the recovery ladder: reconstructs a synthetic cross-reference table by
/// brute-force scanning the entire source for <c>N G obj</c> signatures, used when
/// <see cref="CrossReferenceReader.Read"/> fails outright (missing <c>startxref</c>,
/// corrupt trailer, unreadable cross-reference data). Later occurrences of the same object
/// number win, matching how a well-formed incrementally-updated file's <c>/Prev</c> chain
/// would have resolved it. Bounded by <see cref="PdfOptions.MaxBruteForceScanBytes"/>.
/// </summary>
/// <remarks>
/// Clean-room, per the policy in AGENTS.md: this approach is designed from ISO 32000-1 §7.5.8's
/// description of what a cross-reference table normally maps (object number → offset) plus the
/// general shape of brute-force PDF repair described in PdfPig's (Apache-2.0) public
/// documentation and behavior — not derived from any restricted source, and no restricted
/// material was consulted for this file.
/// </remarks>
internal static class RecoveryScanner
{
    private static readonly byte[] ObjKeyword = "obj"u8.ToArray();
    private static readonly byte[] EndKeyword = "end"u8.ToArray();
    private static readonly byte[] TrailerKeyword = "trailer"u8.ToArray();

    /// <summary>Scans <paramref name="source"/> end to end and reconstructs a best-effort cross-reference table.</summary>
    public static CrossReferenceTable Scan(ByteSource source, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        // Bound the read itself by MaxBruteForceScanBytes - not just the scan over an
        // already-fully-read buffer. A cap that only limited how far FindAll looked, after
        // ReadToEnd(0) had already materialized the entire (possibly huge, possibly hostile)
        // source, wouldn't actually bound anything.
        var capBytes = options.MaxBruteForceScanBytes <= 0 ? 0L : options.MaxBruteForceScanBytes;
        var readLength = Math.Min(source.Length, Math.Min(capBytes, int.MaxValue));
        var buffer = new byte[(int)readLength];
        source.Read(0, buffer);
        var scanCap = buffer.Length;

        var entries = new Dictionary<int, CrossReferenceEntry>();
        foreach (var objIndex in PdfScanner.FindAll(buffer, ObjKeyword, scanCap))
        {
            if (TryParseObjectHeader(buffer, objIndex, out var number, out var generation, out var headerStart))
            {
                entries[number] = CrossReferenceEntry.InFile(headerStart, generation);
            }
        }

        var trailer = FindTrailerDictionary(buffer, options) ?? SynthesizeTrailerFromCatalog(buffer, entries, options, diagnostics);
        if (trailer is null)
        {
            throw new PlumePdfException("PLUME2041", "Brute-force recovery scanned the whole file but found neither a 'trailer' dictionary nor a /Type /Catalog object to synthesize one from.");
        }

        return new CrossReferenceTable(trailer, entries);
    }

    private static bool TryParseObjectHeader(ReadOnlySpan<byte> buffer, int objKeywordIndex, out int number, out int generation, out int headerStart)
    {
        number = 0;
        generation = 0;
        headerStart = 0;

        if (objKeywordIndex >= EndKeyword.Length && buffer.Slice(objKeywordIndex - EndKeyword.Length, EndKeyword.Length).SequenceEqual(EndKeyword))
        {
            return false; // this "obj" is the tail of "endobj", not a fresh header
        }

        var i = SkipWhitespaceBackward(buffer, objKeywordIndex - 1);
        var generationEnd = i + 1;
        i = SkipDigitsBackward(buffer, i);
        var generationStart = i + 1;
        if (generationStart == generationEnd)
        {
            return false;
        }

        i = SkipWhitespaceBackward(buffer, i);
        var numberEnd = i + 1;
        i = SkipDigitsBackward(buffer, i);
        var numberStart = i + 1;
        if (numberStart == numberEnd)
        {
            return false;
        }

        if (!TryParseAsciiInt(buffer[generationStart..generationEnd], out generation))
        {
            return false;
        }

        if (!TryParseAsciiInt(buffer[numberStart..numberEnd], out number))
        {
            return false;
        }

        headerStart = numberStart;
        return true;
    }

    private static int SkipWhitespaceBackward(ReadOnlySpan<byte> buffer, int i)
    {
        while (i >= 0 && PdfScanner.IsWhitespace(buffer[i]))
        {
            i--;
        }

        return i;
    }

    private static int SkipDigitsBackward(ReadOnlySpan<byte> buffer, int i)
    {
        while (i >= 0 && buffer[i] is >= (byte)'0' and <= (byte)'9')
        {
            i--;
        }

        return i;
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<byte> digits, out int value)
    {
        value = 0;
        if (digits.IsEmpty)
        {
            return false;
        }

        foreach (var b in digits)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            value = (value * 10) + (b - (byte)'0');
        }

        return true;
    }

    private static PdfDictionary? FindTrailerDictionary(byte[] buffer, PdfOptions options)
    {
        var matches = PdfScanner.FindAll(buffer, TrailerKeyword, buffer.Length);
        if (matches.Count == 0)
        {
            return null;
        }

        // The last "trailer" in the file corresponds to the most recent revision.
        var afterKeyword = matches[^1] + TrailerKeyword.Length;
        var slice = buffer.AsSpan(afterKeyword).ToArray();
        var tokenizer = new PdfTokenizer(slice);

        try
        {
            return ObjectParser.ParseValue(slice, ref tokenizer, options, diagnostics: null) as PdfDictionary;
        }
        catch (PlumePdfException)
        {
            return null;
        }
    }

    private static PdfDictionary? SynthesizeTrailerFromCatalog(byte[] buffer, Dictionary<int, CrossReferenceEntry> entries, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        foreach (var (number, entry) in entries)
        {
            if (entry.Kind != CrossReferenceEntryKind.InFile || entry.ByteOffset >= buffer.Length)
            {
                continue;
            }

            PdfObject value;
            IndirectReference reference;
            try
            {
                var slice = buffer.AsSpan((int)entry.ByteOffset).ToArray();
                value = ObjectParser.ParseIndirectObject(slice, options, diagnostics: null, out reference);
            }
            catch (PlumePdfException)
            {
                continue;
            }

            if (value is PdfDictionary dict && dict.TryGetValue(PdfName.Type, out var type) && type is PdfName { Value: "Catalog" })
            {
                var trailer = new PdfDictionary();
                trailer.Set(PdfName.Root, new PdfReference(reference));
                CrossReferenceReader.ReportDeviation("PLUME2042", $"No 'trailer' keyword found; synthesized a minimal trailer from the recovered /Type /Catalog object {number}.", entry.ByteOffset, options, diagnostics);
                return trailer;
            }
        }

        return null;
    }
}
