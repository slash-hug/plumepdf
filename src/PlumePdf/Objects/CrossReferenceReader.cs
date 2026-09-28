using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>What kind of location a <see cref="CrossReferenceEntry"/> describes (ISO 32000-1 §7.5.8.2, type field).</summary>
internal enum CrossReferenceEntryKind
{
    /// <summary>The object number is on the free list — not currently in use.</summary>
    Free,

    /// <summary>The object is stored directly in the file at a byte offset.</summary>
    InFile,

    /// <summary>The object is compressed inside an object stream (§7.5.7).</summary>
    InObjectStream,
}

/// <summary>One cross-reference table entry — where to find (or that there is no) an object.</summary>
internal readonly struct CrossReferenceEntry
{
    private CrossReferenceEntry(CrossReferenceEntryKind kind, long field1, int field2)
    {
        Kind = kind;
        Field1 = field1;
        Field2 = field2;
    }

    /// <summary>What kind of entry this is.</summary>
    public CrossReferenceEntryKind Kind { get; }

    private long Field1 { get; }

    private int Field2 { get; }

    /// <summary>Valid when <see cref="Kind"/> is <see cref="CrossReferenceEntryKind.InFile"/>: the byte offset of the object's <c>N G obj</c> framing.</summary>
    public long ByteOffset => Field1;

    /// <summary>Valid when <see cref="Kind"/> is <see cref="CrossReferenceEntryKind.InFile"/>: the object's generation.</summary>
    public int Generation => Field2;

    /// <summary>Valid when <see cref="Kind"/> is <see cref="CrossReferenceEntryKind.InObjectStream"/>: the containing object stream's object number.</summary>
    public int StreamObjectNumber => (int)Field1;

    /// <summary>Valid when <see cref="Kind"/> is <see cref="CrossReferenceEntryKind.InObjectStream"/>: the object's index within that stream.</summary>
    public int IndexInStream => Field2;

    /// <summary>Creates a free-list entry.</summary>
    public static CrossReferenceEntry Free() => new(CrossReferenceEntryKind.Free, 0, 0);

    /// <summary>Creates an in-file entry at <paramref name="byteOffset"/>, generation <paramref name="generation"/>.</summary>
    public static CrossReferenceEntry InFile(long byteOffset, int generation) => new(CrossReferenceEntryKind.InFile, byteOffset, generation);

    /// <summary>Creates an object-stream entry at index <paramref name="indexInStream"/> of stream object <paramref name="streamObjectNumber"/>.</summary>
    public static CrossReferenceEntry InObjectStream(int streamObjectNumber, int indexInStream) => new(CrossReferenceEntryKind.InObjectStream, streamObjectNumber, indexInStream);
}

/// <summary>A fully merged cross-reference table: the newest-wins-per-object-number entry map, plus the merged trailer.</summary>
internal sealed class CrossReferenceTable(PdfDictionary trailer, IReadOnlyDictionary<int, CrossReferenceEntry> entriesByObjectNumber, long? startXrefOffset = null)
{
    /// <summary>The merged trailer dictionary — the newest revision's keys, backfilled from older revisions for keys it didn't repeat.</summary>
    public PdfDictionary Trailer { get; } = trailer;

    /// <summary>Every known object number's location, newest revision wins.</summary>
    public IReadOnlyDictionary<int, CrossReferenceEntry> EntriesByObjectNumber { get; } = entriesByObjectNumber;

    /// <summary>
    /// The byte offset the source's own <c>startxref</c> pointed at — the offset an
    /// incremental update's new trailer must chain onto via <c>/Prev</c>
    /// (<see cref="IncrementalUpdateWriter"/>). <see langword="null"/> when the table came
    /// from <see cref="RecoveryScanner"/>'s brute-force scan rather than a clean read: a
    /// recovered document has no reliable prior <c>startxref</c> to chain onto, so
    /// <see cref="IncrementalUpdateWriter"/> refuses to append to it (use a full rewrite
    /// instead).
    /// </summary>
    public long? StartXrefOffset { get; } = startXrefOffset;
}

/// <summary>One cross-reference section's own entries and trailer, before merging into a <see cref="CrossReferenceTable"/>.</summary>
internal sealed class CrossReferenceSection(PdfDictionary trailer, IReadOnlyDictionary<int, CrossReferenceEntry> entries)
{
    public PdfDictionary Trailer { get; } = trailer;

    public IReadOnlyDictionary<int, CrossReferenceEntry> Entries { get; } = entries;
}

/// <summary>
/// Reads a document's cross-reference data — classic tables, cross-reference streams, and
/// hybrid files (<c>/XRefStm</c>) — walking the <c>/Prev</c> chain newest-first with a
/// cycle guard and a chain-length cap, per ISO 32000-1 §7.5.4 (classic tables),
/// §7.5.8 (streams), and §7.5.8.4 (hybrid files). <see cref="ReadWithRecovery"/> is the
/// top of the recovery ladder: a clean read that fails outright (no <c>startxref</c>,
/// corrupt trailer, …) falls back to <see cref="RecoveryScanner"/>'s brute-force scan
/// under lenient reading, and propagates under <see cref="PdfOptions.Strict"/>.
/// </summary>
/// <remarks>
/// Each section is read by loading everything from its offset to end-of-file
/// (<see cref="ByteSource.ReadToEnd"/>) rather than pre-computing its exact length — a
/// deliberate Phase 1 simplification. For the common case (the xref section lives at the
/// tail of the file) this reads a small window; for a long <c>/Prev</c> chain of small
/// incremental updates on a very large file, each link re-reads its own remainder-to-EOF
/// window, which is not optimal. Revisit with benchmark evidence before
/// treating this as settled.
/// </remarks>
internal static class CrossReferenceReader
{
    /// <summary>Reads cross-reference data, falling back to brute-force recovery on failure unless <see cref="PdfOptions.Strict"/> is set.</summary>
    public static CrossReferenceTable ReadWithRecovery(ByteSource source, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        try
        {
            return Read(source, options, diagnostics);
        }
        catch (PlumePdfException ex) when (!options.Strict)
        {
            var message = $"Cross-reference data could not be read cleanly ({ex.Code}: {ex.Message}); falling back to a brute-force object scan.";
            diagnostics?.Add(new PdfDiagnostic("PLUME2043", DiagnosticSeverity.Warning, message));
            return RecoveryScanner.Scan(source, options, diagnostics);
        }
    }

    /// <summary>Reads cross-reference data with no recovery fallback — throws on any unrecoverable deviation.</summary>
    public static CrossReferenceTable Read(ByteSource source, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var startOffset = FindStartXrefOffset(source);
        var entries = new Dictionary<int, CrossReferenceEntry>();
        PdfDictionary? trailer = null;
        var visitedOffsets = new HashSet<long>();
        var chainLength = 0;
        long? nextOffset = startOffset;

        while (nextOffset is long offset)
        {
            if (!visitedOffsets.Add(offset))
            {
                ReportDeviation("PLUME2030", $"Cross-reference /Prev chain revisits offset {offset} — cycle detected; stopping the walk.", offset, options, diagnostics);
                break;
            }

            chainLength++;
            if (chainLength > options.MaxCrossReferencePrevChainLength)
            {
                ReportDeviation("PLUME2031", $"Cross-reference /Prev chain exceeded PdfOptions.MaxCrossReferencePrevChainLength ({options.MaxCrossReferencePrevChainLength}).", offset, options, diagnostics);
                break;
            }

            var section = ReadSection(source, offset, options, diagnostics);
            if (section is null)
            {
                break;
            }

            if (trailer is null)
            {
                trailer = new PdfDictionary();
            }

            MergeMissingTrailerKeys(trailer, section.Trailer);

            // Start from this revision's classic-table entries, then let its hybrid /XRefStm
            // (§7.5.8.4) override them within this same revision. A hybrid file's classic
            // table marks objects that actually live in an object stream as Free (for pre-1.5
            // reader compatibility) - the real location is only in /XRefStm, so /XRefStm must
            // win the tie, not the classic table. Merging into a per-revision map first (rather
            // than TryAdd-ing each independently into the global map) keeps that precedence
            // local to this revision while still preserving "newest revision wins" across the
            // outer /Prev chain below.
            var sectionEntries = section.Entries.Count == 0
                ? new Dictionary<int, CrossReferenceEntry>()
                : new Dictionary<int, CrossReferenceEntry>(section.Entries);

            if (section.Trailer.TryGetValue(PdfName.XRefStm, out var xrefStmValue) && xrefStmValue is PdfNumber { IsInteger: true } xrefStmOffset && xrefStmOffset.TryToInt64(out var xrefStmTarget))
            {
                var hybridSection = ReadSection(source, xrefStmTarget, options, diagnostics);
                if (hybridSection is not null)
                {
                    foreach (var (number, entry) in hybridSection.Entries)
                    {
                        sectionEntries[number] = entry;
                    }
                }
            }

            foreach (var (number, entry) in sectionEntries)
            {
                entries.TryAdd(number, entry);
            }

            nextOffset = section.Trailer.TryGetValue(PdfName.Prev, out var prevValue) && prevValue is PdfNumber { IsInteger: true } prev && prev.TryToInt64(out var prevTarget)
                ? prevTarget
                : null;
        }

        if (trailer is null || !trailer.ContainsKey(PdfName.Root))
        {
            throw new PlumePdfException("PLUME2032", "No usable cross-reference trailer with a /Root entry was found.");
        }

        return new CrossReferenceTable(trailer, entries, startXrefOffset: startOffset);
    }

    private static void MergeMissingTrailerKeys(PdfDictionary accumulated, PdfDictionary section)
    {
        foreach (var (key, value) in section)
        {
            if (!accumulated.ContainsKey(key))
            {
                accumulated.Set(key, value);
            }
        }
    }

    private static CrossReferenceSection? ReadSection(ByteSource source, long offset, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (offset < 0 || offset >= source.Length)
        {
            ReportDeviation("PLUME2033", $"Cross-reference offset {offset} is out of range; stopping the walk.", offset, options, diagnostics);
            return null;
        }

        var buffer = source.ReadToEnd(offset);
        var tokenizer = new PdfTokenizer(buffer);

        if (!tokenizer.Read())
        {
            return null;
        }

        if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("xref"u8))
        {
            return ReadClassicTable(buffer, ref tokenizer, options, diagnostics);
        }

        if (tokenizer.TokenType == PdfTokenType.Integer)
        {
            var value = ObjectParser.ParseIndirectObject(buffer, options, diagnostics, out _);
            if (value is PdfStream xrefStream)
            {
                return ReadCrossReferenceStream(xrefStream, options, diagnostics);
            }
        }

        ReportDeviation("PLUME2034", $"No recognizable cross-reference section (classic table or stream) at offset {offset}.", offset, options, diagnostics);
        return null;
    }

    private static CrossReferenceSection ReadClassicTable(byte[] buffer, ref PdfTokenizer tokenizer, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var entries = new Dictionary<int, CrossReferenceEntry>();

        while (true)
        {
            var checkpoint = tokenizer.Consumed;
            if (!tokenizer.Read())
            {
                ReportDeviation("PLUME2035", "Classic cross-reference table ran to end of input before 'trailer'.", tokenizer.Consumed, options, diagnostics);
                return new CrossReferenceSection(new PdfDictionary(), entries);
            }

            if (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("trailer"u8))
            {
                var trailerValue = ObjectParser.ParseValue(buffer, ref tokenizer, options, diagnostics);
                return new CrossReferenceSection(trailerValue as PdfDictionary ?? new PdfDictionary(), entries);
            }

            if (tokenizer.TokenType != PdfTokenType.Integer)
            {
                ReportDeviation("PLUME2036", $"Expected a subsection header or 'trailer' near offset {tokenizer.Consumed}.", tokenizer.Consumed, options, diagnostics);
                tokenizer.Reset(checkpoint);
                return new CrossReferenceSection(new PdfDictionary(), entries);
            }

            var firstObjectNumber = tokenizer.IntegerValue;
            if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
            {
                ReportDeviation("PLUME2036", "Expected a subsection entry count after its first object number.", tokenizer.Consumed, options, diagnostics);
                return new CrossReferenceSection(new PdfDictionary(), entries);
            }

            // The declared count is attacker-controlled and untrustworthy on its own:
            // a hostile "0 2000000000" over a tiny file must not cost anywhere near 2 billion
            // iterations, and - since it's a long while the classic loop counter used to be an
            // int - a count above int.MaxValue used to wrap the counter negative and never
            // terminate at all. The loop variable below is long (no wraparound); the entry
            // read itself is what bounds total work to the buffer's real size, not the
            // declared count (see the two cases inside the loop).
            var count = tokenizer.IntegerValue;

            for (var i = 0L; i < count; i++)
            {
                var entryCheckpoint = tokenizer.Consumed;
                if (!TryReadClassicEntry(ref tokenizer, out var byteOffset, out var generation, out var inUse))
                {
                    // Two different reasons an entry read can fail: (a) the tokens present just
                    // don't shape up like an entry (garbage, or a deliberately-corrupted entry
                    // like the "q" case below) - tolerate it and keep going, same as any other
                    // recoverable deviation; or (b) we've run off the end of what's actually
                    // present (true end of input, or we've landed on the subsection's own
                    // "trailer" keyword) - the declared count overstated how many entries exist,
                    // which is exactly the hostile-count shape. For (b), rewind past
                    // whatever this attempt consumed (e.g. the "trailer" keyword itself) and
                    // stop this subsection outright, rather than "trying" for up to
                    // count-many more iterations that can only ever fail identically.
                    var exhausted = tokenizer.TokenType == PdfTokenType.EndOfInput
                        || (tokenizer.TokenType == PdfTokenType.Keyword && tokenizer.ValueSpan.SequenceEqual("trailer"u8));

                    if (exhausted)
                    {
                        tokenizer.Reset(entryCheckpoint);
                        ReportDeviation("PLUME2037", $"Subsection declared {count} entries starting at object {firstObjectNumber}, but only {i} were actually present; stopping.", entryCheckpoint, options, diagnostics);
                        break;
                    }

                    ReportDeviation("PLUME2037", $"Malformed cross-reference entry for object {firstObjectNumber + i}; skipping it.", tokenizer.Consumed, options, diagnostics);
                    continue;
                }

                var objectNumber = (int)(firstObjectNumber + i);
                entries[objectNumber] = inUse ? CrossReferenceEntry.InFile(byteOffset, generation) : CrossReferenceEntry.Free();
            }
        }
    }

    private static bool TryReadClassicEntry(ref PdfTokenizer tokenizer, out long byteOffset, out int generation, out bool inUse)
    {
        byteOffset = 0;
        generation = 0;
        inUse = false;

        if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
        {
            return false;
        }

        byteOffset = tokenizer.IntegerValue;

        if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
        {
            return false;
        }

        generation = (int)tokenizer.IntegerValue;

        if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Keyword)
        {
            return false;
        }

        if (tokenizer.ValueSpan.SequenceEqual("n"u8))
        {
            inUse = true;
            return true;
        }

        if (tokenizer.ValueSpan.SequenceEqual("f"u8))
        {
            inUse = false;
            return true;
        }

        return false;
    }

    private static CrossReferenceSection ReadCrossReferenceStream(PdfStream stream, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var dict = stream.Dictionary;
        if (!dict.TryGetValue(PdfName.W, out var wValue) || wValue is not PdfArray wArray || wArray.Count < 3)
        {
            throw new PlumePdfException("PLUME2038", "Cross-reference stream is missing a valid /W field-width array.");
        }

        // Every numeric field here comes from a potentially hostile or corrupt file, so no
        // checked ToInt32 may run on it directly — a value past int range is a bare
        // OverflowException otherwise (seen in the wild: pdf.js corpus REDHAT-1531897-0.pdf).
        static bool TryToInt(PdfObject? value, out int result)
        {
            result = 0;
            if (value is not PdfNumber { IsInteger: true } number)
            {
                return false;
            }

            try
            {
                var wide = number.ToInt64();
                if (wide < int.MinValue || wide > int.MaxValue)
                {
                    return false;
                }

                result = (int)wide;
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        // Each width feeds ReadBigEndian into a long, so 8 bytes is the ceiling of the
        // representable; -1 marks unusable (non-integer, negative, oversized, overflowing).
        int FieldWidth(int i) => TryToInt(wArray[i], out var w) && w is >= 0 and <= 8 ? w : -1;
        var w0 = FieldWidth(0);
        var w1 = FieldWidth(1);
        var w2 = FieldWidth(2);
        var entryWidth = w0 + w1 + w2;

        // A hostile or corrupt /W can be negative (indexes before the buffer) or all-zero
        // (the position-vs-length bound below never advances, so a large /Index count would
        // allocate without limit). Either way the stream is unreadable as declared.
        if (w0 < 0 || w1 < 0 || w2 < 0 || entryWidth <= 0)
        {
            throw new PlumePdfException("PLUME2038", $"Cross-reference stream declares invalid /W field widths; each must be an integer from 0 to 8 and they must sum to at least one byte.");
        }

        var subsections = new List<(int Start, int Count)>();
        if (dict.TryGetValue(PdfName.Index, out var indexValue) && indexValue is PdfArray indexArray)
        {
            for (var i = 0; i + 1 < indexArray.Count; i += 2)
            {
                if (TryToInt(indexArray[i], out var start) && TryToInt(indexArray[i + 1], out var count) && start >= 0 && count >= 0)
                {
                    subsections.Add((start, count));
                }
                else
                {
                    ReportDeviation("PLUME2039", "Cross-reference stream declares an /Index subsection with a non-integer, negative, or out-of-range start/count; skipping it.", null, options, diagnostics);
                }
            }
        }
        else if (dict.TryGetValue(PdfName.Size, out var sizeValue))
        {
            if (TryToInt(sizeValue, out var size) && size >= 0)
            {
                subsections.Add((0, size));
            }
            else
            {
                ReportDeviation("PLUME2039", "Cross-reference stream has no /Index and its /Size is not a usable non-negative integer; reading no declared entries.", null, options, diagnostics);
            }
        }

        var decoded = stream.GetDecodedBytes(options.Filters, options);
        var entries = new Dictionary<int, CrossReferenceEntry>();
        var position = 0;

        foreach (var (start, count) in subsections)
        {
            for (var i = 0; i < count; i++)
            {
                if (position + entryWidth > decoded.Length)
                {
                    ReportDeviation("PLUME2039", "Cross-reference stream data ended before all declared entries were read.", position, options, diagnostics);
                    return new CrossReferenceSection(dict, entries);
                }

                var type = w0 == 0 ? 1 : (int)ReadBigEndian(decoded, position, w0);
                var field2 = ReadBigEndian(decoded, position + w0, w1);
                var field3 = w2 == 0 ? 0 : ReadBigEndian(decoded, position + w0 + w1, w2);
                position += entryWidth;

                var objectNumber = start + i;
                entries[objectNumber] = type switch
                {
                    0 => CrossReferenceEntry.Free(),
                    1 => CrossReferenceEntry.InFile(field2, (int)field3),
                    2 => CrossReferenceEntry.InObjectStream((int)field2, (int)field3),
                    _ => CrossReferenceEntry.Free(),
                };
            }
        }

        return new CrossReferenceSection(dict, entries);
    }

    private static long ReadBigEndian(byte[] data, int offset, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        return value;
    }

    private static long FindStartXrefOffset(ByteSource source)
    {
        const int tailWindow = 2048;
        var start = Math.Max(0, source.Length - tailWindow);
        var buffer = source.ReadToEnd(start);

        var index = PdfScanner.LastIndexOf(buffer, "startxref"u8);
        if (index < 0)
        {
            throw new PlumePdfException("PLUME2040", "Could not find 'startxref' near the end of the file.");
        }

        var tail = buffer.AsSpan(index + "startxref"u8.Length).ToArray();
        var tokenizer = new PdfTokenizer(tail);
        if (!tokenizer.Read() || tokenizer.TokenType != PdfTokenType.Integer)
        {
            throw new PlumePdfException("PLUME2040", "'startxref' was not followed by a valid byte offset.");
        }

        return tokenizer.IntegerValue;
    }

    internal static void ReportDeviation(string code, string message, long? offset, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, offset));
    }
}
