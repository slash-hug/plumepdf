using System.Collections.Generic;
using PlumePdf.Fonts.Outlines;
using PlumePdf.Fonts.Substitute;
using PlumePdf.Fonts.Tables;
using PlumePdf.Objects;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// Builds <see cref="RenderFont"/>s from a page's resolved <c>/Font</c> dictionaries for the
/// Phase 8 raster text pipeline, caching one per font-dictionary instance for the lifetime of one
/// display-list build (the render-path sibling of <see cref="ExtractionFontFactory"/>, which it
/// reuses for the metrics/decoding half). Never throws out of <see cref="GetOrBuild"/>: a font
/// that cannot be parsed at all degrades to a no-glyph <see cref="RenderFont"/> (advances still
/// tracked) rather than aborting the page — the same lenient-by-default contract the rest of the
/// render path keeps.
/// </summary>
internal sealed class RenderFontFactory
{
    private readonly IObjectSource _objects;
    private readonly PdfFilterRegistry _filters;
    private readonly PdfOptions _options;
    private readonly DiagnosticCollection? _diagnostics;
    private readonly ExtractionFontFactory _extraction;
    private readonly FontReadLimits _limits;
    private readonly Dictionary<PdfDictionary, RenderFont?> _cache = new(ReferenceEqualityComparer.Instance);
    private bool _renderModeNoticed;

    public RenderFontFactory(IObjectSource objects, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(filters);
        ArgumentNullException.ThrowIfNull(options);
        _objects = objects;
        _filters = filters;
        _options = options;
        _diagnostics = diagnostics;
        _extraction = new ExtractionFontFactory(objects, filters, options, diagnostics);
        _limits = FontReadLimits.From(options);
    }

    /// <summary>
    /// Records — exactly once per build — that a text run used a rendering mode this phase paints
    /// only best-effort (stroke or clip modes approximated as a fill, or clip-by-text not applied,
    /// PLUME7730). Returns <see langword="true"/> for the first such run so the caller emits the
    /// diagnostic without repeating it per run.
    /// </summary>
    public bool TryClaimRenderModeNotice()
    {
        if (_renderModeNoticed)
        {
            return false;
        }

        _renderModeNoticed = true;
        return true;
    }

    /// <summary>Returns the cached render font for <paramref name="fontDict"/>, building it (and its diagnostics) on first access.</summary>
    public RenderFont? GetOrBuild(PdfDictionary fontDict)
    {
        ArgumentNullException.ThrowIfNull(fontDict);
        if (_cache.TryGetValue(fontDict, out var cached))
        {
            return cached;
        }

        RenderFont? font;
        try
        {
            font = Build(fontDict);
        }
        catch (PlumePdfException)
        {
            font = null; // A font we cannot resolve at all never breaks the page — it just contributes no glyphs.
        }

        _cache[fontDict] = font;
        return font;
    }

    private RenderFont Build(PdfDictionary fontDict)
    {
        var subtype = Resolve(fontDict, "Subtype") is PdfName subtypeName ? subtypeName.Value : string.Empty;
        var baseFontName = FontNames.StripSubsetPrefix(Resolve(fontDict, "BaseFont") is PdfName baseValue ? baseValue.Value : "Unknown");
        var isCid = subtype == "Type0";

        var metrics = _extraction.Build(fontDict);

        // The descriptor (and, for a Type0 font, the CIDToGIDMap) live on the descendant CID font.
        PdfDictionary? descendant = null;
        var descriptor = Resolve(fontDict, "FontDescriptor") as PdfDictionary;
        if (isCid)
        {
            descendant = ResolveDescendant(fontDict);
            descriptor = descendant is not null ? Resolve(descendant, "FontDescriptor") as PdfDictionary : null;
        }

        var (bold, italic, serif, fixedPitch, symbolic) = ReadDescriptorStyle(descriptor);

        // Presence-based, exactly like ExtractionFontFactory.BuildSimple's own isEmbedded check
        // — whether the descriptor names an embedded program at all, regardless of
        // whether that program goes on to decode/parse successfully. Computed once so every
        // BuildNonEmbedded call site below (not embedded at all; embedded but unsupported for
        // name-keyed rendering; embedded but undecodable/unparseable) agrees with extraction on
        // the same font.
        var hadEmbeddedProgram = descriptor is not null
            && (Resolve(descriptor, "FontFile") is not null
                || Resolve(descriptor, "FontFile2") is not null
                || Resolve(descriptor, "FontFile3") is not null);

        var embedded = TryReadEmbeddedProgram(descriptor);
        if (embedded is null)
        {
            return BuildNonEmbedded(fontDict, subtype, metrics, baseFontName, isCid, bold, italic, serif, fixedPitch, hadEmbeddedProgram);
        }

        var (kind, programBytes, fontFile3Subtype) = embedded.Value;
        try
        {
            switch (kind)
            {
                case EmbeddedKind.TrueType:
                    {
                        var sfnt = SfntFont.ParseForRender(programBytes, _limits);
                        var upem = ReadSfntUnitsPerEm(sfnt);
                        if (isCid)
                        {
                            return new RenderFont(metrics, upem, _limits, sfnt: sfnt, isCid: true, cidToGid: ReadCidToGidMap(descendant));
                        }

                        return new RenderFont(metrics, upem, _limits, sfnt: sfnt, sfntCmap: TryReadSfntCmap(sfnt), encoding: EncodingResolver.Resolve(fontDict, _objects, _options, _diagnostics), isSymbolic: symbolic);
                    }

                case EmbeddedKind.OpenType:
                    {
                        var sfnt = SfntFont.ParseForRender(programBytes, _limits);
                        var upem = ReadSfntUnitsPerEm(sfnt);
                        if (isCid)
                        {
                            return new RenderFont(metrics, upem, _limits, sfnt: sfnt, isCid: true, cidToGid: ReadCidToGidMap(descendant));
                        }

                        return new RenderFont(metrics, upem, _limits, sfnt: sfnt, sfntCmap: TryReadSfntCmap(sfnt), encoding: EncodingResolver.Resolve(fontDict, _objects, _options, _diagnostics), isSymbolic: symbolic);
                    }

                case EmbeddedKind.CffCid:
                    {
                        var cff = CffParser.Parse(programBytes, _limits);
                        return new RenderFont(metrics, 1000.0, _limits, cff: cff, isCid: true, cidToGid: ReadCidToGidMap(descendant));
                    }

                case EmbeddedKind.Type1:
                    {
                        var type1 = Type1Parser.Parse(programBytes, _limits);
                        return new RenderFont(metrics, 1000.0, _limits, type1: type1, encoding: EncodingResolver.Resolve(fontDict, _objects, _options, _diagnostics, builtInBase: type1.BuiltInEncoding));
                    }

                case EmbeddedKind.CffSimple:
                default:
                    {
                        _ = fontFile3Subtype;
                        var cff = CffParser.Parse(programBytes, _limits);
                        if (isCid)
                        {
                            // Some producers label a CID-keyed bare CFF's FontFile3 stream
                            // /Type1C instead of the correct /CIDFontType0C; the outer Type0
                            // wrapper is what actually decides CID-ness here, so this is treated
                            // exactly like EmbeddedKind.CffCid — code -> CID -> GID, never a name
                            // lookup (the substitute/name-keyed paths never see isCid: true).
                            return new RenderFont(metrics, 1000.0, _limits, cff: cff, isCid: true, cidToGid: ReadCidToGidMap(descendant));
                        }

                        if (cff.IsSupportedForNameKeyedRendering)
                        {
                            return new RenderFont(metrics, cff.UnitsPerEm, _limits, cff: cff, encoding: EncodingResolver.Resolve(fontDict, _objects, _options, _diagnostics, builtInBase: cff.BuiltInEncoding));
                        }

                        // A simple (non-CID) bare CFF this parser cannot use for name-keyed
                        // rendering (CID-keyed-shaped data reached via a simple font, Expert
                        // charset with no names, CharstringType 1, or a non-uniform FontMatrix)
                        // falls back to a metric-compatible substitute rather than dropping the
                        // run (best-effort). A program *was* present, so the Standard-14
                        // Symbol/ZapfDingbats sticky-encoding rule below must not apply — see
                        // hadEmbeddedProgram's remarks on BuildNonEmbedded.
                        return BuildNonEmbedded(fontDict, subtype, metrics, baseFontName, isCid, bold, italic, serif, fixedPitch, hadEmbeddedProgram);
                    }
            }
        }
        catch (PlumePdfException)
        {
            // An embedded program that will not parse (corrupt/unsupported) falls back to a
            // substitute face rather than leaving the page's text blank. hadEmbeddedProgram is
            // true here for the same reason as the CffSimple case above — a program was present
            // (we only reach this catch once embedded is non-null), just unusable.
            return BuildNonEmbedded(fontDict, subtype, metrics, baseFontName, isCid, bold, italic, serif, fixedPitch, hadEmbeddedProgram);
        }
    }

    /// <summary>
    /// Builds a <see cref="RenderFont"/> for a font with no usable embedded program: a Standard-14
    /// name substitutes a metric-compatible bundled Liberation face (<c>PLUME7510</c>), a font
    /// whose name suggests CJK coverage the bundle doesn't carry degrades to advances-only
    /// (<c>PLUME7511</c>) — and a Type 1/MMType1 font named exactly
    /// <c>Symbol</c>/<c>SymbolMT</c>/<c>ZapfDingbats</c> substitutes PDFium's own bundled Foxit CFF
    /// face instead, rendered through the same name-keyed <see cref="RenderFont"/> path an
    /// embedded Type1C program uses (<see cref="Standard14SymbolFonts"/> supplies the built-in
    /// encoding that makes the name lookup mean anything).
    /// </summary>
    /// <param name="fontDict">The font's resolved <c>/Font</c> dictionary.</param>
    /// <param name="subtype">The font dictionary's <c>/Subtype</c> value.</param>
    /// <param name="metrics">The extraction-side metrics/widths already built for this font.</param>
    /// <param name="baseFontName">The subset-prefix-stripped <c>/BaseFont</c> name.</param>
    /// <param name="isCid">Whether the font is a Type0/CID font.</param>
    /// <param name="bold">Whether the descriptor's flags/weight indicate a bold face.</param>
    /// <param name="italic">Whether the descriptor's flags/angle indicate an italic face.</param>
    /// <param name="serif">Whether the descriptor's flags indicate a serif face.</param>
    /// <param name="fixedPitch">Whether the descriptor's flags indicate a fixed-pitch face.</param>
    /// <param name="hadEmbeddedProgram">
    /// <see langword="true"/> when the font's <c>/FontDescriptor</c> names an embedded program
    /// (<c>/FontFile</c>, <c>/FontFile2</c>, or <c>/FontFile3</c>) at all — regardless of whether
    /// that program went on to decode or parse successfully — as opposed to the genuinely
    /// non-embedded case. Computed once in <see cref="Build"/> by presence, exactly like
    /// <see cref="ExtractionFontFactory.Build(PdfDictionary)"/>'s own <c>isEmbedded</c> gate.
    /// The Standard-14 Symbol/ZapfDingbats sticky built-in-encoding rule (ignore a
    /// declared <c>/Encoding</c>) applies only when this is <see langword="false"/> — an embedded
    /// program merely named Symbol/ZapfDingbats (including one that is unsupported for
    /// name-keyed rendering, or fails to parse at all) keeps its declared <c>/Encoding</c>
    /// non-authoritatively on both the extraction and render sides, instead of the two paths
    /// disagreeing on the same font.
    /// </param>
    private RenderFont BuildNonEmbedded(PdfDictionary fontDict, string subtype, ExtractionFont metrics, string baseFontName, bool isCid, bool bold, bool italic, bool serif, bool fixedPitch, bool hadEmbeddedProgram)
    {
        var isType1 = !isCid && subtype is ("Type1" or "MMType1");
        var resolution = SubstituteFontMap.Resolve(baseFontName, bold, italic, serif, fixedPitch, isType1Subtype: isType1);

        if (resolution.FaceKey is not { } faceKey)
        {
            _diagnostics?.Add(resolution.Diagnostic); // PLUME7511 (CJK gap, FaceKey null).
            return new RenderFont(metrics, 1000.0, _limits); // No usable substitute (CJK gap) — advances only.
        }

        if (SubstituteFontStore.GetFaceKind(faceKey) != SubstituteFaceKind.Cff)
        {
            _diagnostics?.Add(resolution.Diagnostic); // PLUME7510 (Liberation substitution).

            if (!SubstituteFontStore.TryGetFont(faceKey, _limits, out var substitute))
            {
                return new RenderFont(metrics, 1000.0, _limits); // No usable substitute — advances only.
            }

            // Substitute glyph selection is Unicode -> substitute cmap -> GID for both simple and
            // CID fonts (a CID font's own glyph indices are meaningless against a different
            // face), so the substitute path is uniform regardless of the original font's CID-ness.
            _ = isCid;
            return new RenderFont(metrics, substitute.UnitsPerEm, _limits, substituteFont: substitute, substitute: true);
        }

        // A Foxit CFF substitute (Standard-14 Symbol/ZapfDingbats): name-keyed rendering, never a
        // CID font (the substitute path never sees isCid: true). SubstituteFontMap only
        // ever returns a Cff-kind key when its own (identical name/subtype) TryGet call just
        // succeeded, so this should always succeed too — but fail safe to advances-only rather
        // than trusting an invariant across two files for a font the document never asked
        // PlumePDF to embed.
        if (!Standard14SymbolFonts.TryGet(baseFontName, subtype, out var symbolBase, out _))
        {
            _diagnostics?.Add(resolution.Diagnostic);
            return new RenderFont(metrics, 1000.0, _limits);
        }

        // The sticky rule (declared /Encoding ignored, built-in table wins) applies
        // only when there was no embedded program at all — see hadEmbeddedProgram's remarks.
        var authoritative = !hadEmbeddedProgram;

        var message = resolution.Diagnostic.Message;
        var severity = resolution.Diagnostic.Severity;
        if (hadEmbeddedProgram)
        {
            // SubstituteFontMap's generic "is not embedded" phrasing is wrong for this call
            // site: the descriptor DOES name an embedded program, it just failed to parse. Worse,
            // because the declared /Encoding stays authoritative here (not the sticky
            // built-in table), this substitution is not guaranteed to paint anything — the Foxit
            // charset only has Greek/math/dingbat glyph names, so a Latin declared encoding (the
            // common case for a font merely *named* Symbol) resolves to codes the face has no
            // glyph for at all, and nothing paints. An Info message claiming "rendered using..."
            // would actively misreport that outcome, so this case gets its own accurate wording
            // and a Warning severity instead — a caller filtering on Warning must still see that
            // this font's text may have gone unrendered, the signal PLUME7729 used to carry.
            message = DescribeDeclaredEncoding(fontDict) is { } declared
                ? $"Font '{baseFontName}' has an embedded font program that failed to parse; " +
                    $"falling back to the bundled substitute face '{faceKey}', rendered using the " +
                    $"font's own declared /Encoding {declared} rather than the face's built-in table — glyphs " +
                    "whose declared encoding names are outside the face's charset will not paint."
                : $"Font '{baseFontName}' has an embedded font program that failed to parse; " +
                    $"falling back to the bundled substitute face '{faceKey}' with its Standard-14 built-in encoding " +
                    "(the font declares no /Encoding of its own).";
            severity = DiagnosticSeverity.Warning;
        }
        else if (DescribeDeclaredEncoding(fontDict) is { } declaredEncoding)
        {
            // A named /Encoding (or a dictionary's /BaseEncoding) on a Standard-14 symbol
            // font is meaningless against the Foxit face's own built-in encoding —
            // EncodingResolver's builtInIsAuthoritative: true below ignores both exactly as this
            // sentence records (ISO 32000-1 §9.6.6.2).
            message = $"{message} Its declared /Encoding {declaredEncoding} was ignored in favour of the face's built-in encoding (ISO 32000-1 §9.6.6.2 / Standard-14 symbol rule).";
        }

        _diagnostics?.Add(new PdfDiagnostic(resolution.Diagnostic.Code, severity, message));

        if (!SubstituteFontStore.TryGetCffFont(faceKey, _limits, out var cff))
        {
            return new RenderFont(metrics, 1000.0, _limits); // Bundled CFF failed to parse under the configured limits — advances only.
        }

        return new RenderFont(
            metrics,
            cff.UnitsPerEm,
            _limits,
            cff: cff,
            // The Standard-14 table is ALWAYS the built-in base; only its authority toggles.
            // Non-embedded: authoritative — a declared /Encoding is
            // ignored. Embedded-but-unparseable: a declared /Encoding wins,
            // but when the font declares none, ISO 32000-1 §9.6.6.1's "font's built-in encoding"
            // is Symbol's own table — not the opaque all-unassigned default, which painted nothing.
            // ExtractionFontFactory.BuildSimple uses the identical pair, so both sides agree.
            encoding: EncodingResolver.Resolve(fontDict, _objects, _options, _diagnostics, builtInBase: symbolBase, builtInIsAuthoritative: authoritative));
    }

    /// <summary>
    /// Describes a Standard-14 symbol font's declared <c>/Encoding</c> for the "was ignored"
    /// diagnostic sentence, covering both shapes a producer uses to name a base encoding: a bare
    /// name (<c>/Encoding /WinAnsiEncoding</c>) and a dictionary's <c>/BaseEncoding</c> entry
    /// (<c>/Encoding &lt;&lt; /BaseEncoding /WinAnsiEncoding /Differences [...] &gt;&gt;</c> — at
    /// least as common a producer shape as the bare name). Returns <see langword="null"/> when
    /// <c>/Encoding</c> is absent, is a dictionary with no <c>/BaseEncoding</c>, or is otherwise
    /// not a name/dictionary the sticky rule actually overrides.
    /// </summary>
    private string? DescribeDeclaredEncoding(PdfDictionary fontDict)
    {
        switch (Resolve(fontDict, "Encoding"))
        {
            case PdfName encodingName:
                return $"/{encodingName.Value}";

            case PdfDictionary encodingDict when Resolve(encodingDict, "BaseEncoding") is PdfName baseEncodingName:
                return $"dictionary's /BaseEncoding /{baseEncodingName.Value}";

            default:
                return null;
        }
    }

    private enum EmbeddedKind
    {
        TrueType,
        OpenType,
        CffCid,
        CffSimple,
        Type1,
    }

    private (EmbeddedKind Kind, byte[] Bytes, string FontFile3Subtype)? TryReadEmbeddedProgram(PdfDictionary? descriptor)
    {
        if (descriptor is null)
        {
            return null;
        }

        if (Resolve(descriptor, "FontFile2") is PdfStream ttf)
        {
            return TryDecode(ttf) is { } bytes ? (EmbeddedKind.TrueType, bytes, string.Empty) : null;
        }

        if (Resolve(descriptor, "FontFile3") is PdfStream cff)
        {
            var streamSubtype = Resolve(cff.Dictionary, "Subtype") is PdfName s ? s.Value : string.Empty;
            var kind = streamSubtype switch
            {
                "OpenType" => EmbeddedKind.OpenType,
                "CIDFontType0C" => EmbeddedKind.CffCid,
                _ => EmbeddedKind.CffSimple, // "Type1C" and anything unrecognized.
            };

            return TryDecode(cff) is { } bytes ? (kind, bytes, streamSubtype) : null;
        }

        if (Resolve(descriptor, "FontFile") is PdfStream type1)
        {
            return TryDecode(type1) is { } bytes ? (EmbeddedKind.Type1, bytes, string.Empty) : null;
        }

        return null;
    }

    private byte[]? TryDecode(PdfStream stream)
    {
        try
        {
            return stream.GetDecodedBytes(_filters, _options, r => _objects.Resolve(r));
        }
        catch (PlumePdfException)
        {
            return null;
        }
    }

    private int[]? ReadCidToGidMap(PdfDictionary? descendant)
    {
        if (descendant is null || Resolve(descendant, "CIDToGIDMap") is not PdfStream stream)
        {
            return null; // Absent or the name /Identity — GID == CID.
        }

        var bytes = TryDecode(stream);
        if (bytes is null)
        {
            return null;
        }

        var map = new int[bytes.Length / 2];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = (bytes[(i * 2) + 0] << 8) | bytes[(i * 2) + 1];
        }

        return map;
    }

    private static double ReadSfntUnitsPerEm(SfntFont sfnt) =>
        sfnt.TryGetTable("head", out var head) ? HeadTable.Parse(head).UnitsPerEm : 1000.0;

    private static CmapTable? TryReadSfntCmap(SfntFont sfnt)
    {
        try
        {
            return sfnt.TryGetTable("cmap", out var cmap) ? CmapTable.Parse(cmap) : null;
        }
        catch (PlumePdfException)
        {
            return null;
        }
    }

    private (bool Bold, bool Italic, bool Serif, bool FixedPitch, bool Symbolic) ReadDescriptorStyle(PdfDictionary? descriptor)
    {
        if (descriptor is null)
        {
            return (false, false, false, false, false);
        }

        var flags = Resolve(descriptor, "Flags") is PdfNumber flagsNumber && flagsNumber.TryToInt32(out var f) ? f : 0;
        const int FixedPitchFlag = 1 << 0;
        const int SerifFlag = 1 << 1;
        const int SymbolicFlag = 1 << 2;
        const int NonsymbolicFlag = 1 << 5;
        const int ItalicFlag = 1 << 6;
        const int ForceBoldFlag = 1 << 18;

        var italic = (flags & ItalicFlag) != 0 || (Resolve(descriptor, "ItalicAngle") is PdfNumber angle && angle.Value != 0);
        var bold = (flags & ForceBoldFlag) != 0 || (Resolve(descriptor, "FontWeight") is PdfNumber weight && weight.Value >= 600);
        var serif = (flags & SerifFlag) != 0;
        var fixedPitch = (flags & FixedPitchFlag) != 0;
        var symbolic = (flags & SymbolicFlag) != 0 && (flags & NonsymbolicFlag) == 0;

        return (bold, italic, serif, fixedPitch, symbolic);
    }

    private PdfDictionary? ResolveDescendant(PdfDictionary fontDict)
    {
        if (Resolve(fontDict, "DescendantFonts") is not PdfArray descendants || descendants.Count == 0)
        {
            return null;
        }

        return Resolve(descendants[0]) as PdfDictionary;
    }

    private PdfObject? Resolve(PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(PdfName.Get(key), out var value) ? Resolve(value) : null;

    private PdfObject Resolve(PdfObject value) =>
        value is PdfReference reference ? _objects.Resolve(reference.Target) : value;
}
