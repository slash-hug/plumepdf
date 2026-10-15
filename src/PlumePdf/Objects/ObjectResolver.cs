using System.Collections.Concurrent;
using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>
/// The seam <see cref="ObjectRegistry"/> (<c>doc.Objects</c>) resolves through — implemented
/// by <see cref="ObjectResolver"/> for a document opened from a byte source, and by an
/// in-memory equivalent for a synthetic document with no backing file (e.g. the result of
/// <c>Pdf.Merge</c>). Keeping <see cref="ObjectRegistry"/> dependent on this interface rather
/// than concretely on <see cref="ObjectResolver"/> is what lets a merged/composed
/// <c>PdfDocument</c> expose the same <c>doc.Objects</c> escape hatch as an opened one.
/// </summary>
internal interface IObjectSource
{
    /// <summary>The document's trailer dictionary.</summary>
    PdfDictionary Trailer { get; }

    /// <summary>Resolves <paramref name="reference"/> to its value.</summary>
    PdfObject Resolve(IndirectReference reference);
}

/// <summary>
/// The lazy, thread-safe resolution cache behind <see cref="ObjectRegistry"/> — resolves an
/// <see cref="IndirectReference"/> to its <see cref="PdfObject"/> on first access and caches
/// the result for later lookups from any thread (docs/architecture.md, "Threading &amp;
/// mutation": an opened document supports thread-safe concurrent reads). Caching is keyed
/// by object number alone, matching <see cref="CrossReferenceTable"/> itself (which keeps
/// only the newest entry per number, generation included in that entry) — a document never
/// has two live, independently-addressable generations of the same number at once.
/// </summary>
internal sealed class ObjectResolver : IObjectSource
{
    private readonly ByteSource _source;
    private readonly CrossReferenceTable _table;
    private readonly PdfOptions _options;
    private readonly DiagnosticCollection? _diagnostics;
    private readonly ObjectStreamReader _objectStreams = new();
    private readonly ConcurrentDictionary<int, PdfObject> _cache = new();
    private ISecurityHandler? _securityHandler;
    private bool _encryptMetadataFalse;

    /// <summary>Creates a resolver over an already-read cross-reference table.</summary>
    public ObjectResolver(ByteSource source, CrossReferenceTable table, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        _source = source;
        _table = table;
        _options = options;
        _diagnostics = diagnostics;
    }

    /// <summary>The document's trailer dictionary.</summary>
    public PdfDictionary Trailer => _table.Trailer;

    /// <summary>
    /// Attaches the security handler that decrypts strings and stream payloads as objects are
    /// resolved from this point on. Call once, after authenticating against the document's
    /// <c>/Encrypt</c> dictionary (which must itself already have been resolved through this
    /// same resolver with no handler attached yet — its own strings are never encrypted per
    /// §7.6.1, and resolving it first, before this call, is what keeps it that way: it's
    /// already cached by the time anything could otherwise try to decrypt it).
    /// </summary>
    public void AttachSecurityHandler(ISecurityHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _securityHandler = handler;
        _encryptMetadataFalse = ReadEncryptMetadataIsFalse();
    }

    /// <summary>
    /// Reads the trailer's <c>/Encrypt</c> dictionary's own <c>/EncryptMetadata</c> flag
    /// directly (independent of <see cref="ISecurityHandler"/>, which has no reason to expose
    /// it — decrypting bytes never needs to know this) so <see cref="Decrypt"/> can apply the
    /// ISO 32000-1 §7.6.2 exemption below. The encryption dictionary was already resolved
    /// through this same resolver, with no handler attached yet, before
    /// <see cref="AttachSecurityHandler"/> is ever called (see that method's remarks) — so this
    /// is a cache hit, never a fresh parse.
    /// </summary>
    private bool ReadEncryptMetadataIsFalse()
    {
        if (!Trailer.TryGetValue(PdfName.Encrypt, out var encryptValue))
        {
            return false;
        }

        var encryptDict = encryptValue switch
        {
            PdfReference reference => Resolve(reference.Target) as PdfDictionary,
            PdfDictionary direct => direct,
            _ => null,
        };

        return encryptDict is not null
            && encryptDict.TryGetValue(PdfName.Get("EncryptMetadata"), out var flagValue)
            && flagValue is PdfBoolean { Value: false };
    }

    /// <summary>
    /// Whether <paramref name="number"/> has a live (non-free) cross-reference entry — answered from
    /// the table alone, with no parse and no diagnostic. For hint-class reads (<c>/Interpolate</c>)
    /// that must never add a <c>PLUME2060</c> or throw under
    /// <c>Strict</c> for a dangling reference the document's rendering never depended on.
    /// </summary>
    internal bool HasLiveEntry(int number) =>
        _table.EntriesByObjectNumber.TryGetValue(number, out var entry) && entry.Kind != CrossReferenceEntryKind.Free;

    /// <summary>Resolves <paramref name="reference"/>, parsing and caching it on first access.</summary>
    public PdfObject Resolve(IndirectReference reference) => _cache.GetOrAdd(reference.Number, _ => ResolveUncached(reference.Number));

    private PdfObject ResolveUncached(int number)
    {
        if (!_table.EntriesByObjectNumber.TryGetValue(number, out var entry) || entry.Kind == CrossReferenceEntryKind.Free)
        {
            ReportDeviation("PLUME2060", $"Object {number} is not present in the cross-reference table (or is marked free); resolving it as null.", null);
            return PdfNull.Instance;
        }

        return entry.Kind switch
        {
            CrossReferenceEntryKind.InFile => ResolveInFile(number, entry),
            CrossReferenceEntryKind.InObjectStream => ResolveInObjectStream(number, entry),
            _ => PdfNull.Instance,
        };
    }

    private PdfObject ResolveInObjectStream(int number, CrossReferenceEntry entry)
    {
        try
        {
            return _objectStreams.GetObject(entry.StreamObjectNumber, entry.IndexInStream, n => Resolve(new IndirectReference(n, 0)), _options, _diagnostics);
        }
        catch (PlumePdfException ex)
        {
            // Same lenient shape as ResolveInFile: a damaged container stream costs the
            // member object, never the whole document (Strict mode rethrows via ReportDeviation).
            ReportDeviation("PLUME2063", $"Failed to read object {number} from object stream {entry.StreamObjectNumber} (index {entry.IndexInStream}): {ex.Message}", null);
            return PdfNull.Instance;
        }
    }

    // Lazy brute-force scan map for entries whose offsets turn out to lie (parse failure or
    // an impostor object at the claimed offset). Built at most once per document, only when
    // first needed; double-checked so concurrent readers share one scan (resource-limit bounds
    // apply inside RecoveryScanner itself).
    private CrossReferenceTable? _recoveryTable;
    private volatile bool _recoveryAttempted;
    private readonly object _recoveryLock = new();

    private CrossReferenceTable? RecoveryTable
    {
        get
        {
            if (!_recoveryAttempted)
            {
                lock (_recoveryLock)
                {
                    if (!_recoveryAttempted)
                    {
                        try
                        {
                            _recoveryTable = RecoveryScanner.Scan(_source, _options, null);
                        }
                        catch (PlumePdfException)
                        {
                            _recoveryTable = null;
                        }

                        _recoveryAttempted = true;
                    }
                }
            }

            return _recoveryTable;
        }
    }

    private PdfObject? TryResolveViaRecoveryScan(int expectedNumber, CrossReferenceEntry badEntry)
    {
        if (RecoveryTable is not { } recoveryTable
            || !recoveryTable.EntriesByObjectNumber.TryGetValue(expectedNumber, out var recovered)
            || recovered.Kind != CrossReferenceEntryKind.InFile
            || recovered.ByteOffset == badEntry.ByteOffset)
        {
            return null;
        }

        var remaining = _source.Length - recovered.ByteOffset;
        if (remaining <= 0)
        {
            return null;
        }

        try
        {
            var buffer = ReadObjectWindow(expectedNumber, recovered.ByteOffset, remaining, out var streamLength);
            var value = ObjectParser.ParseIndirectObject(buffer, _options, null, out var actual, streamLength);
            if (actual.Number != expectedNumber)
            {
                return null;
            }

            ReportDeviation("PLUME2064", $"Cross-reference entry for object {expectedNumber} was wrong; recovered the object at offset {recovered.ByteOffset} via a full-file scan.", recovered.ByteOffset);

            if (_securityHandler is not { } handler)
            {
                return value;
            }

            try
            {
                return Decrypt(value, actual, handler);
            }
            catch (PlumePdfException ex)
            {
                ReportDeviation("PLUME4006", $"Failed to decrypt object {actual}: {ex.Message}", recovered.ByteOffset);
                return value;
            }
        }
        catch (PlumePdfException)
        {
            return null;
        }
    }

    // The starting size of the bounded read window ResolveInFile tries an object in (see its
    // remarks): generous enough that the overwhelming majority of objects (numbers, names,
    // short dictionaries, references) parse cleanly on the first attempt without ever
    // growing, small enough that resolving N objects near the front of a large file doesn't
    // each copy a large fraction of the file the way reading offset-to-EOF did.
    private const int InitialResolveWindowBytes = 4096;

    private PdfObject ResolveInFile(int expectedNumber, CrossReferenceEntry entry)
    {
        var remaining = _source.Length - entry.ByteOffset;
        if (remaining <= 0)
        {
            ReportDeviation("PLUME2061", $"Cross-reference table pointed object {expectedNumber} at offset {entry.ByteOffset}, at or past the end of the source.", entry.ByteOffset);
            return PdfNull.Instance;
        }

        PdfObject value;
        IndirectReference actual;
        try
        {
            var buffer = ReadObjectWindow(expectedNumber, entry.ByteOffset, remaining, out var streamLength);
            value = ObjectParser.ParseIndirectObject(buffer, _options, _diagnostics, out actual, streamLength);
        }
        catch (PlumePdfException ex)
        {
            // The entry's offset lied. Before giving the object up as null, consult the
            // brute-force scan map (built lazily, once) — the "repairable deviation" rung
            // of the recovery ladder for tables that parse but whose offsets are wrong.
            if (TryResolveViaRecoveryScan(expectedNumber, entry) is { } recoveredValue)
            {
                return recoveredValue;
            }

            ReportDeviation("PLUME2061", $"Failed to parse object {expectedNumber} at offset {entry.ByteOffset}: {ex.Message}", entry.ByteOffset);
            return PdfNull.Instance;
        }

        if (actual.Number != expectedNumber)
        {
            // A different object at the claimed offset is the same lie in a subtler form:
            // prefer finding the REAL object via the scan map over using the impostor.
            if (TryResolveViaRecoveryScan(expectedNumber, entry) is { } recoveredValue)
            {
                return recoveredValue;
            }

            ReportDeviation("PLUME2062", $"Cross-reference table pointed at offset {entry.ByteOffset} expecting object {expectedNumber}, but found object {actual.Number}; using it anyway.", entry.ByteOffset);
        }

        if (_securityHandler is not { } handler)
        {
            return value;
        }

        try
        {
            return Decrypt(value, actual, handler);
        }
        catch (PlumePdfException ex)
        {
            // A recoverable deviation, same as a parse failure: surface it and hand back the
            // still-encrypted value rather than throwing the whole resolve away (never silent
            // swallowing — the diagnostic says exactly what happened).
            ReportDeviation("PLUME4006", $"Failed to decrypt object {actual}: {ex.Message}", entry.ByteOffset);
            return value;
        }
    }

    // Bytes read past a stream's declared end so the exact read also holds the EOL,
    // 'endstream' and 'endobj' that close the object; widened once, to the larger value,
    // when 'endobj' sits further out than that.
    private const int StreamTrailerSlackBytes = 1024;
    private const int WideStreamTrailerSlackBytes = 64 * 1024;

    // How many indirect /Length resolutions may nest on one thread (a length object is
    // normally a plain integer; nesting means a length that is itself a stream with an
    // indirect /Length — malformed — and the cap keeps a long chain of those off the stack).
    private const int MaxLengthResolutionDepth = 4;

    // The streams whose indirect /Length this thread is resolving right now, per resolver: a
    // length that leads back to one of them is a cycle, and is unusable rather than re-entered.
    [ThreadStatic]
    private static HashSet<(ObjectResolver Resolver, int Number)>? t_lengthsInFlight;

    /// <summary>
    /// Reads a buffer starting at <paramref name="offset"/> that holds one complete indirect
    /// object. Reads <see cref="InitialResolveWindowBytes"/> first, which holds most objects
    /// whole, and probe-parses it (with a disposable <see cref="DiagnosticCollection"/> and
    /// <see cref="PdfOptions.Strict"/> forced off, so a too-small window can never itself
    /// throw or pollute <c>doc.Diagnostics</c>). When the probe had to scan for
    /// <c>endstream</c> — the object is a stream whose <c>/Length</c> is indirect, or doesn't
    /// fit the window — the stream is read once at its declared size instead: the
    /// <c>/Length</c>, resolved through the cross-reference table when it is an indirect
    /// reference (returned in <paramref name="streamLength"/> so the parse uses it rather than
    /// scanning), plus the bytes that close the object. Only when there is no usable
    /// <c>/Length</c> — missing, unresolvable, or not landing on <c>endstream</c> — does the
    /// probe window keep growing (4× each time) until the object parses without reaching its
    /// edge. The caller's real parse of the returned buffer has the same outcome as parsing
    /// everything from <paramref name="offset"/> to the end of the source.
    /// </summary>
    private byte[] ReadObjectWindow(int expectedNumber, long offset, long remaining, out int? streamLength)
    {
        streamLength = null;
        var probeOptions = _options.Strict ? _options with { Strict = false } : _options;
        var window = (int)Math.Min(InitialResolveWindowBytes, remaining);
        var buffer = ReadWindow(offset, window);
        var declaredLengthTried = false;

        while (true)
        {
            var fits = FitsWindow(buffer, probeOptions, out var scannedForEndstream);
            if (scannedForEndstream && !declaredLengthTried
                && ObjectParser.TryLocateStreamPayload(buffer, _options, out var dictionary, out var payloadStart))
            {
                declaredLengthTried = true;
                if (TryReadStreamAtDeclaredLength(expectedNumber, offset, remaining, buffer, dictionary, payloadStart, out var exact, out streamLength))
                {
                    return exact;
                }
            }

            if (fits || window == remaining)
            {
                return buffer;
            }

            window = GrowProbeWindow(window, remaining);
            buffer = ReadWindow(offset, window);
        }
    }

    private byte[] ReadWindow(long offset, int length)
    {
        var buffer = new byte[length];
        _source.Read(offset, buffer);
        return buffer;
    }

    private static bool FitsWindow(byte[] buffer, PdfOptions probeOptions, out bool scannedForEndstream)
    {
        scannedForEndstream = false;
        var probeDiagnostics = new DiagnosticCollection();
        try
        {
            var probedValue = ObjectParser.ParseIndirectObject(buffer, probeOptions, probeDiagnostics, out _);
            scannedForEndstream = probeDiagnostics.Any(d => d.Code == "PLUME2019");
            return !LooksTruncated(buffer, probedValue, probeDiagnostics);
        }
        catch (PlumePdfException)
        {
            return false; // could just be a window cut mid-token; read more and retry.
        }
    }

    private bool TryReadStreamAtDeclaredLength(int expectedNumber, long offset, long remaining, byte[] probed, PdfDictionary dictionary, int payloadStart, out byte[] buffer, out int? streamLength)
    {
        buffer = probed;
        streamLength = null;
        if (!dictionary.TryGetValue(PdfName.Length, out var lengthValue))
        {
            return false;
        }

        int? indirectLength = null;
        if (lengthValue is PdfReference lengthReference)
        {
            indirectLength = ResolveIndirectLength(expectedNumber, lengthReference.Target);
            lengthValue = indirectLength is int resolved ? PdfNumber.Get(resolved) : PdfNull.Instance;
        }

        if (lengthValue is not PdfNumber { IsInteger: true } number || !number.TryToInt32(out var length) || length < 0)
        {
            return false;
        }

        // A declared length past the end of the source, or too large for one array, is more
        // likely wrong than real: leave it to the probe, which finds the real 'endstream' (or
        // refuses once its window would exceed one array).
        var end = (long)payloadStart + length;
        if (end >= remaining || end + WideStreamTrailerSlackBytes > Array.MaxLength)
        {
            return false;
        }

        foreach (var slack in (ReadOnlySpan<int>)[StreamTrailerSlackBytes, WideStreamTrailerSlackBytes])
        {
            var size = Math.Min(end + slack, remaining);
            var candidate = size <= probed.Length ? probed : ReadWindow(offset, (int)size);
            var closing = ClosingAfterPayload(candidate, (int)end);
            if (closing == StreamClosing.None)
            {
                return false; // the declared length doesn't land on 'endstream'
            }

            if (closing == StreamClosing.EndstreamAndEndobj || size == remaining || slack == WideStreamTrailerSlackBytes)
            {
                buffer = candidate;
                streamLength = indirectLength;
                return true;
            }
        }

        return false;
    }

    private enum StreamClosing
    {
        None,
        Endstream,
        EndstreamAndEndobj,
    }

    // What closes the stream at 'end': 'endstream' (after optional whitespace), and whether
    // 'endobj' (likewise) follows within the buffer.
    private static StreamClosing ClosingAfterPayload(byte[] buffer, int end)
    {
        var p = SkipWhitespace(buffer, end);
        if (!buffer.AsSpan(p).StartsWith("endstream"u8))
        {
            return StreamClosing.None;
        }

        p = SkipWhitespace(buffer, p + "endstream"u8.Length);
        return buffer.AsSpan(p).StartsWith("endobj"u8) ? StreamClosing.EndstreamAndEndobj : StreamClosing.Endstream;
    }

    private static int SkipWhitespace(byte[] buffer, int position)
    {
        while (position < buffer.Length && PdfScanner.IsWhitespace(buffer[position]))
        {
            position++;
        }

        return position;
    }

    /// <summary>
    /// The value of a stream's indirect <c>/Length</c>, or <see langword="null"/> when it can't
    /// be used: no live cross-reference entry (checked first, so a dangling reference never
    /// adds a diagnostic), a reference back to a stream whose length this thread is already
    /// resolving (a cycle), nesting past <see cref="MaxLengthResolutionDepth"/>, or a value
    /// that isn't a non-negative integer.
    /// </summary>
    private int? ResolveIndirectLength(int streamNumber, IndirectReference lengthReference)
    {
        var inFlight = t_lengthsInFlight ??= [];
        if (lengthReference.Number == streamNumber || !HasLiveEntry(lengthReference.Number)
            || inFlight.Contains((this, lengthReference.Number)) || inFlight.Count >= MaxLengthResolutionDepth
            || !inFlight.Add((this, streamNumber)))
        {
            return null;
        }

        try
        {
            return Resolve(lengthReference) is PdfNumber { IsInteger: true } number && number.TryToInt32(out var length) && length >= 0 ? length : null;
        }
        finally
        {
            inFlight.Remove((this, streamNumber));
        }
    }

    /// <summary>
    /// The next probe window: 4× <paramref name="window"/>, capped at
    /// <paramref name="remaining"/>. Refuses with <c>PLUME1005</c> rather than overflow when
    /// that is more than one array can hold.
    /// </summary>
    internal static int GrowProbeWindow(int window, long remaining)
    {
        var next = Math.Min((long)window * 4, remaining);
        if (next > Array.MaxLength)
        {
            throw new PlumePdfException("PLUME1005", $"An object with no usable /Length did not end within the first {window} bytes, and reading further would need more than one array can hold (~2 GiB).");
        }

        return (int)next;
    }

    /// <summary>Best-effort signal that <paramref name="buffer"/> was too small to hold the whole object just probe-parsed out of it — by <see cref="ReadObjectWindow"/>'s probes, or by <c>CrossReferenceReader</c> for a cross-reference stream.</summary>
    internal static bool LooksTruncated(byte[] buffer, PdfObject value, DiagnosticCollection probeDiagnostics)
    {
        // A stream whose raw payload runs right up to (or past) the edge of the window we
        // gave it almost certainly got truncated - either /Length pushed past the edge, or
        // the "scan for endstream" fallback never found one because the real one is further
        // out than we read.
        if (value is PdfStream stream && stream.RawBytes.Length >= buffer.Length - 32)
        {
            return true;
        }

        // These all mean the tokenizer ran off the end of the window while still expecting
        // more of the object (an unclosed array/dict, no 'endstream' where the payload ended,
        // or 'endobj' itself landing past the edge). A stream recovered by scanning for
        // 'endstream' (PLUME2019) is not by itself a sign: when the scan finds one inside the
        // window, it is the same first occurrence a read to the end of the source would find,
        // and when it doesn't, the payload runs to the window's edge (caught above).
        foreach (var diagnostic in probeDiagnostics)
        {
            if (diagnostic.Code is "PLUME2014" or "PLUME2017" or "PLUME2020" or "PLUME2022")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Recursively decrypts every string and stream payload reachable from <paramref name="value"/>
    /// (ISO 32000-1 §7.6.2), keyed by <paramref name="owner"/> — the indirect object these
    /// strings/streams were read as part of, per Algorithm 1. Dictionaries and arrays are
    /// mutated in place (cheaper than rebuilding, and safe: this runs exactly once, right
    /// after an object is first parsed, before it's cached or seen by any caller); strings and
    /// streams are immutable and so are replaced with a decrypted copy. Indirect references
    /// are left untouched - the object they point at is decrypted independently, the first
    /// time <em>it</em> is resolved. Objects that live inside a compressed object stream never
    /// reach this method at all (they resolve through <see cref="ObjectStreamReader"/>
    /// instead): per §7.5.7, they're covered by the object stream's own (already-decrypted-as-
    /// a-whole) stream payload and must not be decrypted a second time.
    /// </summary>
    /// <remarks>
    /// A <c>/Type /Metadata</c> stream is never encrypted in the first place when the
    /// encryption dictionary's <c>/EncryptMetadata</c> is <see langword="false"/> (ISO
    /// 32000-1 §7.6.2) — running it through <see cref="ISecurityHandler.DecryptStream"/>
    /// anyway would corrupt already-plaintext XMP bytes (AES padding failure, or silent
    /// garbage under RC4). <see cref="_encryptMetadataFalse"/> is checked here, on the
    /// stream's own <c>/Type</c>, rather than by walking from the catalog's <c>/Metadata</c>
    /// entry: the exemption is a property of the stream itself, and this resolver has no
    /// reason to special-case only the one metadata stream the catalog happens to point at.
    /// A signature or document-timestamp dictionary's own <c>/Contents</c> entry is exempt
    /// the same way, unconditionally (not gated on any encryption-dictionary flag): ISO
    /// 32000-1 §7.6.2 excludes it from encryption in both directions, since it carries the
    /// raw CMS/PKCS#7 (or RFC 3161 DER) bytes a signature's digest was computed and verified
    /// over — running it through the security handler would silently corrupt every signed,
    /// encrypted document PlumePDF opens. The
    /// write-side counterpart of this same carve-out lives in
    /// <c>IncrementalUpdateWriter.EncryptForWrite</c> (Phase 5).
    /// </remarks>
    private PdfObject Decrypt(PdfObject value, IndirectReference owner, ISecurityHandler handler)
    {
        switch (value)
        {
            case PdfString str:
                var decryptedBytes = handler.DecryptString(str.Bytes.Span, owner);
                return str.IsHex ? PdfString.FromHex(decryptedBytes) : PdfString.FromLiteral(decryptedBytes);

            case PdfArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Decrypt(array[i], owner, handler);
                }

                return array;

            case PdfDictionary dict:
                var skipContents = IsSignatureOrTimestampDictionary(dict);
                foreach (var key in dict.Keys.ToArray())
                {
                    if (skipContents && PdfName.Get("Contents").Equals(key))
                    {
                        continue;
                    }

                    dict.Set(key, Decrypt(dict[key], owner, handler));
                }

                return dict;

            case PdfStream stream:
                var decryptedDict = (PdfDictionary)Decrypt(stream.Dictionary, owner, handler);
                if (_encryptMetadataFalse && IsMetadataStream(decryptedDict))
                {
                    return new PdfStream(decryptedDict, stream.RawBytes);
                }

                var decryptedRaw = handler.DecryptStream(stream.RawBytes.Span, owner);
                return new PdfStream(decryptedDict, decryptedRaw);

            default:
                // Numbers, names, booleans, null, and references carry no encrypted payload.
                return value;
        }
    }

    private static bool IsMetadataStream(PdfDictionary dict) =>
        dict.TryGetValue(PdfName.Type, out var type) && PdfName.Metadata.Equals(type);

    /// <summary>
    /// True for a dictionary whose <c>/Type</c> is <c>/Sig</c> or <c>/DocTimeStamp</c> — the
    /// two shapes ISO 32000-1 §7.6.2 excludes <c>/Contents</c> from encryption for. Gated on
    /// <c>/Type</c> specifically (not merely "has a /Contents key") so an unrelated
    /// dictionary that happens to carry its own <c>/Contents</c> entry with a different
    /// meaning — e.g. a markup annotation's text, ISO 32000-1 §12.5.6.2 — still decrypts
    /// normally.
    /// </summary>
    private static bool IsSignatureOrTimestampDictionary(PdfDictionary dict) =>
        dict.TryGetValue(PdfName.Type, out var type) && (PdfName.Sig.Equals(type) || PdfName.DocTimeStamp.Equals(type));

    private void ReportDeviation(string code, string message, long? offset)
    {
        if (_options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        _diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, offset));
    }
}
