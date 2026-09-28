using PlumePdf.Filters;
using PlumePdf.Filters.Jbig2;
using PlumePdf.Filters.Jpx;

namespace PlumePdf;

/// <summary>
/// A single named PDF stream filter codec (ISO 32000-1 §7.4) — the unit the public
/// <see cref="PdfFilterRegistry"/> extension seam accepts. PNG/TIFF predictor
/// un-filtering is applied by the registry itself after a filter runs, and is not part of
/// an <see cref="IPdfFilter"/> implementation's job.
/// </summary>
public interface IPdfFilter
{
    /// <summary>
    /// Decodes <paramref name="data"/>. Recoverable deviations should be appended to
    /// <paramref name="diagnostics"/> (when non-null) rather than thrown, per the
    /// recovery-ladder philosophy; genuinely unrecoverable input throws a coded
    /// <see cref="PlumePdfException"/>.
    /// </summary>
    /// <param name="data">The still-encoded bytes to decode.</param>
    /// <param name="options">The active options, including resource limits.</param>
    /// <param name="diagnostics">The collection to append recoverable-deviation entries to, if any.</param>
    /// <param name="subject">The indirect object this data belongs to, for diagnostic context.</param>
    byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject);
}

/// <summary>
/// Optional <see cref="IPdfFilter"/> extension for a filter that needs its own
/// <c>/DecodeParms</c> entry <em>at decode time</em>, rather than only as post-processing
/// (predictor un-filtering, which <see cref="PdfFilterRegistry"/> applies itself,
/// separately, after a filter's <c>Decode</c> returns). <c>LZWDecode</c>'s
/// <c>/EarlyChange</c> (ISO 32000-1 §7.4.4.2) is the motivating case: it changes the
/// decompression algorithm's own bit-width schedule, so it has to be known before decoding
/// starts. Kept internal rather than added to the public <see cref="IPdfFilter"/> seam - a
/// community codec that needs the same capability can be added to the public interface
/// later without breaking anything already implementing it.
/// </summary>
internal interface IPdfFilterWithDecodeParms : IPdfFilter
{
    /// <summary>Decodes <paramref name="data"/> using this filter's own <paramref name="decodeParms"/> entry, if any.</summary>
    /// <param name="data">The still-encoded bytes to decode.</param>
    /// <param name="decodeParms">This filter's matching <c>/DecodeParms</c> entry, if any — already resolved from an indirect reference when <see cref="PdfFilterRegistry.Decode"/> was given a resolver.</param>
    /// <param name="options">The active options, including resource limits.</param>
    /// <param name="diagnostics">The collection to append recoverable-deviation entries to, if any.</param>
    /// <param name="subject">The indirect object this data belongs to, for diagnostic context.</param>
    /// <param name="resolver">
    /// The same resolver <see cref="PdfFilterRegistry.Decode"/> was given, if any —
    /// needed by a filter whose <paramref name="decodeParms"/> may itself carry a nested
    /// indirect reference one level deeper than <c>/DecodeParms</c> itself (e.g. <c>/JBIG2Globals</c>,
    /// conventionally an indirect reference to a separate stream object). Most implementations
    /// ignore this.
    /// </param>
    byte[] Decode(ReadOnlyMemory<byte> data, PdfDictionary? decodeParms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, Func<IndirectReference, object?>? resolver = null);
}

/// <summary>
/// Shared "record a diagnostic, or throw under <see cref="PdfOptions.Strict"/>" helper for
/// the codec filters (ISO 32000-1 §7.4.2-§7.4.5) — mirrors the same-shaped helper each
/// Objects-layer parser already has (e.g. <c>ObjectParser.ReportDeviation</c>), kept
/// filter-local rather than shared upward since Filters sits below Objects and can't
/// reference it.
/// </summary>
internal static class FilterDiagnostics
{
    /// <summary>Throws a coded <see cref="PlumePdfException"/> under <see cref="PdfOptions.Strict"/>; otherwise appends a <see cref="DiagnosticSeverity.Warning"/> diagnostic.</summary>
    public static void ReportDeviation(string code, string message, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message, subject: subject));
    }
}

/// <summary>
/// Resolves PDF stream filters by name and decodes payload bytes through a stream's
/// <c>/Filter</c> chain, applying each entry's matching <c>/DecodeParms</c> — including PNG/TIFF
/// predictor un-filtering — in order (ISO 32000-1 §7.4). This is the public extension seam
/// (<c>docs/architecture.md</c>): community codecs register an <see cref="IPdfFilter"/> for a
/// vendor filter name PlumePDF doesn't ship (e.g. a proprietary <c>/AcmeCompress</c>). Every
/// ISO 32000-1 §7.4 filter — <c>ASCIIHexDecode</c>, <c>ASCII85Decode</c>, <c>LZWDecode</c>,
/// <c>FlateDecode</c>, <c>RunLengthDecode</c>, <c>CCITTFaxDecode</c>, <c>JBIG2Decode</c>,
/// <c>DCTDecode</c>, and <c>JPXDecode</c> — is registered by default (the same
/// registry-behaviour-change shape already used for CCITT/JBIG2), so a caller
/// only needs this seam to reach a codec PlumePDF doesn't ship, or to override one it does.
/// </summary>
/// <example>
/// <code>
/// var registry = new PdfFilterRegistry();
/// registry.Register("AcmeCompress", new MyAcmeCompressFilter());
/// byte[] bytes = registry.Decode(streamDictionary, encodedPayload, PdfOptions.Default);
/// </code>
/// </example>
public sealed class PdfFilterRegistry
{
    // Concurrent because Default is a mutable process-wide singleton: a consumer may
    // Register a codec on one thread while another thread's object resolution decodes
    // streams — the documented concurrent-reads contract must survive that.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPdfFilter> _filters = new(StringComparer.Ordinal);

    // Separate map, same concurrency reasoning: a filter name can have a decoder, an
    // encoder, both, or (for decode-only community codecs) just the former — the two
    // registrations are independent.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPdfEncodingFilter> _encoders = new(StringComparer.Ordinal);

    /// <summary>The shared, process-wide registry pre-populated with every built-in filter — what <see cref="PdfOptions.Default"/> uses. To override or remove one filter for a single options object without mutating this singleton, start from <see cref="CreateDefault"/> instead.</summary>
    public static PdfFilterRegistry Default { get; } = CreateDefault();

    /// <summary>Creates an empty registry with no filters registered — nothing decodes until <see cref="Register"/> is called, <c>FlateDecode</c> included. To keep the built-in filters and change one, use <see cref="CreateDefault"/>.</summary>
    public PdfFilterRegistry()
    {
    }

    /// <summary>
    /// Creates a fresh registry pre-populated with every built-in filter, for callers who want
    /// to override or remove one filter without mutating the shared <see cref="Default"/>. Every
    /// call returns a new, independent instance; <see cref="Default"/> is itself one of them.
    /// </summary>
    /// <example>
    /// <code>
    /// var registry = PdfFilterRegistry.CreateDefault();
    /// registry.Register("JPXDecode", new MyRefusingJpxFilter()); // FlateDecode etc. stay registered
    /// var options = PdfOptions.Default with { Filters = registry };
    /// </code>
    /// </example>
    public static PdfFilterRegistry CreateDefault()
    {
        var registry = new PdfFilterRegistry();
        var flate = new FlateFilterAdapter();
        registry.Register("FlateDecode", flate);
        registry.Register("Fl", flate); // ISO 32000-1 Table 8 abbreviated name (inline images)

        var flateEncoder = new FlateEncodingFilter();
        registry.RegisterEncoder("FlateDecode", flateEncoder);
        registry.RegisterEncoder("Fl", flateEncoder);

        // Text-critical decode-only codecs (Phase 3): no encoders — Phase 3 is
        // read-only, so nothing writes these filters yet.
        var asciiHex = new AsciiHexFilterAdapter();
        registry.Register("ASCIIHexDecode", asciiHex);
        registry.Register("AHx", asciiHex); // ISO 32000-1 Table 8 abbreviated name

        var ascii85 = new Ascii85FilterAdapter();
        registry.Register("ASCII85Decode", ascii85);
        registry.Register("A85", ascii85); // ISO 32000-1 Table 8 abbreviated name

        var runLength = new RunLengthFilterAdapter();
        registry.Register("RunLengthDecode", runLength);
        registry.Register("RL", runLength); // ISO 32000-1 Table 8 abbreviated name

        var lzw = new LzwFilterAdapter();
        registry.Register("LZWDecode", lzw);
        registry.Register("LZW", lzw); // ISO 32000-1 Table 8 abbreviated name

        // Phase 7: CCITT/JBIG2 are now built-in decoders rather than
        // community-seam gaps — this is a deliberate behavior change to ImageExtractor's
        // output shape for documents using these filters.
        registry.Register("CCITTFaxDecode", new CcittFaxFilterAdapter());
        registry.Register("CCF", new CcittFaxFilterAdapter()); // ISO 32000-1 Table 8 abbreviated name
        registry.Register("JBIG2Decode", new Jbig2FilterAdapter());

        // Phase 7: decode-only — ImageExtractor's bespoke DCT
        // pass-through (DecodePrefixFilters) still dispatches before the registry and is
        // byte-identical to its pre-Phase-7 behavior; this entry exists for
        // RasterImage.Decode and future (Phase 8+) registry-driven consumers.
        var dct = new DctFilterAdapter();
        registry.Register("DCTDecode", dct);
        registry.Register("DCT", dct); // ISO 32000-1 Table 8 abbreviated name

        // In-house JPEG 2000 Part 1 decode — the same registry-behaviour-
        // change shape already used for CCITT/JBIG2 (a community-seam gap becomes a
        // built-in decoder, loudly: CHANGELOG, docs/errors/PLUME7745.md's retirement note — not
        // silently). No encoder: JPX encode stays out of scope.
        registry.Register("JPXDecode", new JpxFilterAdapter());

        return registry;
    }

    /// <summary>Registers (or replaces) the filter implementation used for <paramref name="filterName"/>.</summary>
    public void Register(string filterName, IPdfFilter filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        ArgumentNullException.ThrowIfNull(filter);
        _filters[filterName] = filter;
    }

    /// <summary>
    /// Registers (or replaces) the encoder used for <paramref name="filterName"/> — the
    /// write-side counterpart of <see cref="Register"/>. Community codecs that only
    /// need to decode never have to call this.
    /// </summary>
    public void RegisterEncoder(string filterName, IPdfEncodingFilter encoder)
    {
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        ArgumentNullException.ThrowIfNull(encoder);
        _encoders[filterName] = encoder;
    }

    /// <summary>
    /// Looks up the encoder registered for <paramref name="filterName"/>, if any. A writer
    /// path probes this rather than assuming <c>FlateDecode</c> is always available, so it
    /// degrades to writing uncompressed payloads when no encoder is registered instead of
    /// throwing.
    /// </summary>
    /// <param name="filterName">The filter name to look up an encoder for, e.g. <c>"FlateDecode"</c>.</param>
    /// <param name="encoder">The registered encoder, or <see langword="null"/> when none is registered.</param>
    /// <returns><see langword="true"/> when an encoder is registered for <paramref name="filterName"/>.</returns>
    public bool TryGetEncoder(string filterName, out IPdfEncodingFilter? encoder)
    {
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        return _encoders.TryGetValue(filterName, out encoder);
    }

    /// <summary>
    /// Looks up the decoder registered for <paramref name="filterName"/>, if any. Used
    /// internally to detect whether a caller has overridden a built-in decoder — e.g.
    /// <c>ImageXObjectResolver</c>'s direct-unwrap JPX path checks this before bypassing the
    /// generic registry route, so a caller who registers their own <c>JPXDecode</c> filter (to
    /// refuse it, or to decode it differently) is honored rather than shadowed. Internal, unlike
    /// <see cref="TryGetEncoder"/>: decode-side lookup exists only for that override check, not as
    /// a write-path capability a caller drives directly — making it public later would be
    /// additive, so nothing is lost by starting narrow.
    /// </summary>
    /// <param name="filterName">The filter name to look up a decoder for, e.g. <c>"JPXDecode"</c>.</param>
    /// <param name="filter">The registered decoder, or <see langword="null"/> when none is registered.</param>
    /// <returns><see langword="true"/> when a decoder is registered for <paramref name="filterName"/>.</returns>
    internal bool TryGetDecoder(string filterName, out IPdfFilter? filter)
    {
        ArgumentException.ThrowIfNullOrEmpty(filterName);
        return _filters.TryGetValue(filterName, out filter);
    }

    /// <summary>
    /// Decodes a stream's payload by walking its <c>/Filter</c> chain (a single name or an
    /// array of names) and applying each filter's matching <c>/DecodeParms</c> entry,
    /// including predictor un-filtering. An unregistered filter name throws
    /// <c>PLUME3010</c>; call <see cref="Register"/> first to support it.
    /// </summary>
    /// <param name="streamDictionary">The stream's dictionary — read for <c>/Filter</c> and <c>/DecodeParms</c>.</param>
    /// <param name="rawBytes">The still-encoded payload bytes.</param>
    /// <param name="options">The active options, including resource limits.</param>
    /// <param name="diagnostics">The collection to append recoverable-deviation entries to, if any.</param>
    /// <param name="subject">The indirect object this data belongs to, for diagnostic context.</param>
    /// <param name="resolver">
    /// Resolves an indirect object reference to its value, or <see langword="null"/> to leave
    /// an indirect <c>/Filter</c> or <c>/DecodeParms</c> entry unresolved (ISO 32000-1 §7.4
    /// permits either as a direct or indirect object; both are conventionally written direct,
    /// but a resolver lets PlumePDF honor the uncommon indirect form instead of degrading it to
    /// "absent"). Callers holding an <see cref="ObjectRegistry"/> pass
    /// <c>reference => objects[reference]</c>; <see cref="PdfStream.GetDecodedBytes"/> forwards
    /// its own <c>resolver</c> parameter here.
    /// </param>
    public byte[] Decode(PdfDictionary streamDictionary, ReadOnlySpan<byte> rawBytes, PdfOptions options, DiagnosticCollection? diagnostics = null, IndirectReference? subject = null, Func<IndirectReference, object?>? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(streamDictionary);
        ArgumentNullException.ThrowIfNull(options);

        var filterNames = ReadNameChain(streamDictionary, PdfName.Filter, resolver, options, diagnostics, subject);
        var decodeParms = ReadDictChain(streamDictionary, PdfName.DecodeParms, filterNames.Count, resolver, options, diagnostics, subject);

        var current = rawBytes.ToArray();
        for (var i = 0; i < filterNames.Count; i++)
        {
            var name = filterNames[i];
            if (!_filters.TryGetValue(name, out var filter))
            {
                throw new PlumePdfException("PLUME3010", $"Unsupported filter '{name}'. Register an IPdfFilter via PdfFilterRegistry to decode it.");
            }

            current = filter is IPdfFilterWithDecodeParms parmsAwareFilter
                ? parmsAwareFilter.Decode(current, decodeParms[i], options, diagnostics, subject, resolver)
                : filter.Decode(current, options, diagnostics, subject);
            current = ApplyPredictor(current, decodeParms[i]);
        }

        return current;
    }

    private static byte[] ApplyPredictor(byte[] data, PdfDictionary? parms)
    {
        if (parms is null)
        {
            return data;
        }

        var predictor = GetInt(parms, PdfName.Predictor, 1);
        if (predictor <= 1)
        {
            return data;
        }

        var colors = GetInt(parms, PdfName.Colors, 1);
        var bitsPerComponent = GetInt(parms, PdfName.BitsPerComponent, 8);
        var columns = GetInt(parms, PdfName.Columns, 1);
        return Predictor.Undo(data, predictor, colors, bitsPerComponent, columns);
    }

    private static int GetInt(PdfDictionary dict, PdfName key, int fallback) =>
        dict.TryGetValue(key, out var value) && value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var converted) ? converted : fallback;

    // Phase 7: an indirect /Filter or /DecodeParms is uncommon in practice (both
    // are conventionally written direct) but not nonconformant. When resolver is non-null,
    // resolve it and use the resolved value exactly as if it had been written direct.
    //
    // Two distinct failure shapes fall out of that, and only one of them stays silent
    // (the PLUME3011 retirement contract, docs/errors/PLUME3011.md):
    //   (b) resolver is null - no object graph available at all (e.g. cross-reference-stream
    //       bootstrap, before any ObjectRegistry exists). Nothing could have been resolved
    //       with, so the entry degrades to absent silently, exactly as PLUME3011's retirement
    //       note documents. Every GetDecodedBytes call site that holds an ObjectRegistry (or
    //       IObjectSource) passes a resolver, so this is reached only by genuine bootstrap
    //       callers.
    //   (a) resolver is non-null but the reference is dangling (resolves to nothing usable) or
    //       resolves to the wrong object shape (e.g. /Filter pointing at a dictionary, or
    //       /DecodeParms pointing at a name). A resolver *was* available and still couldn't
    //       make sense of the entry - that is a real deviation from a well-formed document, not
    //       the bootstrap case, so it is recorded via ReportDeviation (PLUME3012, PLUME3011's
    //       successor for this narrower remaining case) instead of being swallowed.
    private static PdfObject? ResolveIndirectEntry(PdfReference reference, Func<IndirectReference, object?>? resolver, string entryName, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (resolver is null)
        {
            // Case (b): no object graph to resolve against - silent, documented degrade.
            return null;
        }

        if (resolver.Invoke(reference.Target) is PdfObject resolved)
        {
            return resolved;
        }

        // Case (a): a resolver was supplied but the reference didn't resolve to anything usable
        // (dangling, or the resolver returned something that isn't even a PdfObject).
        FilterDiagnostics.ReportDeviation(
            "PLUME3012",
            $"Indirect {entryName} reference ({reference.Target.Number} {reference.Target.Generation} R) did not resolve to a usable object; treating the entry as absent.",
            options, diagnostics, subject);
        return null;
    }

    private static List<string> ReadNameChain(PdfDictionary dict, PdfName key, Func<IndirectReference, object?>? resolver, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (!dict.TryGetValue(key, out var value))
        {
            return [];
        }

        var wasIndirect = value is PdfReference;
        if (value is PdfReference reference)
        {
            var resolved = ResolveIndirectEntry(reference, resolver, "/Filter", options, diagnostics, subject);
            if (resolved is null)
            {
                return [];
            }

            value = resolved;
        }

        switch (value)
        {
            case PdfName single:
                return [single.Value];
            case PdfArray array:
                return [.. array.OfType<PdfName>().Select(static n => n.Value)];
            default:
                // Case (a), shape half: the reference resolved to something real, just not a
                // name or array of names. Only worth flagging when a resolver actually produced
                // this value - a direct, non-indirect /Filter of the wrong shape is ordinary
                // malformed-document territory the caller already tolerates elsewhere.
                if (wasIndirect && resolver is not null)
                {
                    FilterDiagnostics.ReportDeviation(
                        "PLUME3012",
                        $"Indirect /Filter reference resolved to a {value.GetType().Name}, not a name or array of names; treating the entry as absent.",
                        options, diagnostics, subject);
                }

                return [];
        }
    }

    private static List<PdfDictionary?> ReadDictChain(PdfDictionary dict, PdfName key, int count, Func<IndirectReference, object?>? resolver, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var result = new List<PdfDictionary?>(new PdfDictionary?[count]);
        if (!dict.TryGetValue(key, out var value))
        {
            return result;
        }

        var wasIndirect = value is PdfReference;
        if (value is PdfReference singleReference)
        {
            var resolved = ResolveIndirectEntry(singleReference, resolver, "/DecodeParms", options, diagnostics, subject);
            if (resolved is null)
            {
                return result;
            }

            value = resolved;
        }

        switch (value)
        {
            case PdfDictionary single when count > 0:
                result[0] = single;
                break;
            case PdfArray array:
                for (var i = 0; i < count && i < array.Count; i++)
                {
                    var entry = array[i];
                    var entryWasIndirect = entry is PdfReference;
                    if (entry is PdfReference entryReference)
                    {
                        var resolvedEntry = ResolveIndirectEntry(entryReference, resolver, "/DecodeParms", options, diagnostics, subject);
                        if (resolvedEntry is null)
                        {
                            continue;
                        }

                        entry = resolvedEntry;
                    }

                    if (entry is PdfDictionary d)
                    {
                        result[i] = d;
                    }
                    else if (entryWasIndirect && resolver is not null)
                    {
                        FilterDiagnostics.ReportDeviation(
                            "PLUME3012",
                            $"Indirect /DecodeParms array entry resolved to a {entry.GetType().Name}, not a dictionary; treating that entry's predictor parameters as absent.",
                            options, diagnostics, subject);
                    }
                }

                break;
            default:
                if (wasIndirect && resolver is not null && count > 0)
                {
                    FilterDiagnostics.ReportDeviation(
                        "PLUME3012",
                        $"Indirect /DecodeParms reference resolved to a {value.GetType().Name}, not a dictionary or array; treating the entry as absent.",
                        options, diagnostics, subject);
                }

                break;
        }

        return result;
    }
}
