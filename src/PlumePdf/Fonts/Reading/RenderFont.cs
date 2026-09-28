using System.Collections.Generic;
using PlumePdf.Fonts.Outlines;
using PlumePdf.Fonts.Substitute;
using PlumePdf.Fonts.Tables;
using PlumePdf.Objects;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// The render-path counterpart to <see cref="ExtractionFont"/> (Phase 8 text rasterization): for
/// one page <c>/Font</c> resource it resolves each content-stream character code to a decoded
/// <see cref="GlyphOutline"/> (font design units), reusing the whole read-side stack rather than
/// reinventing it. Advance widths, code lengths, and the <c>/ToUnicode</c>-or-encoding text for a
/// code all come from a wrapped <see cref="ExtractionFont"/> (so <c>/Widths</c>/<c>/W</c>/<c>/DW</c>,
/// Identity-H code lengths, and CMap decoding are shared with extraction, never duplicated); glyph
/// <em>selection</em> and outline decoding are this type's own new work, dispatching to
/// <see cref="SfntFont.GetGlyphOutline"/> (embedded TrueType <c>/FontFile2</c> and CFF-flavored
/// OpenType <c>/FontFile3</c>), <see cref="CffParser"/> (bare CFF <c>/FontFile3</c> — name-keyed
/// simple/Type1C via its charset, or CID-keyed),
/// <see cref="Type1Parser"/> (<c>/FontFile</c>, glyph-name keyed), or the bundled Liberation and
/// Foxit substitute faces (<see cref="SubstituteFontStore"/>) when a font carries no embedded
/// program — a Foxit substitute (Standard-14 Symbol/ZapfDingbats) is itself a bare CFF and reuses
/// the name-keyed <c>_cff</c> branch below rather than a dedicated code path.
/// Glyph outlines are handed to <c>Raster/Glyphs/GlyphRasterizer.cs</c> at paint time; this layer
/// never rasterizes (the parse/rasterize split).
/// </summary>
internal sealed class RenderFont
{
    private readonly ExtractionFont _metrics;

    // Exactly one of these backends is the glyph source (all may be null — a "no glyph" font that
    // still tracks advances so following text stays positioned: the CJK substitute-gap case, or a
    // bundled substitute program that failed to parse under the configured limits).
    private readonly SfntFont? _sfnt;          // embedded TrueType (/FontFile2) or CFF-OpenType (/FontFile3 OpenType).
    private readonly CmapTable? _sfntCmap;      // the embedded sfnt's own cmap, for simple (non-CID) code->GID.
    private readonly CffParser? _cff;           // bare CFF — name-keyed simple (Type1C/bundled Foxit substitute) or CID-keyed (/FontFile3).
    private readonly Type1Parser? _type1;       // embedded Type1 (/FontFile), glyph-name keyed.
    private readonly TrueTypeFontProgram? _substituteFont; // a bundled Liberation face (non-embedded fallback).

    private readonly (string GlyphName, int Unicode)[]? _encoding; // simple-font code->glyph-name/Unicode (Type1 name lookup, symbolic fallback).
    private readonly bool _isCid;               // Type0/composite: code decodes to a CID, then CIDToGIDMap gives the GID.
    private readonly int[]? _cidToGid;          // CID->GID stream (null = Identity, GID == CID).
    private readonly bool _isSymbolic;          // FontDescriptor /Flags: symbolic simple TrueType uses the (3,0) cmap with an 0xF0xx offset.
    private readonly bool _substitute;          // glyph selection goes Unicode -> substitute cmap -> GID.
    private readonly FontReadLimits _limits;

    // Per-GID decoded-outline cache: a page of body text renders the same few
    // dozen glyphs hundreds of times, and no layer below this one caches — every occurrence
    // re-parsed the glyph program from glyf/CFF. GlyphOutline is immutable, so sharing one
    // instance across occurrences is safe; GlyphOutline.Empty is cached for failed/degenerate
    // GIDs so a corrupt glyph is parsed (and rejected) once, not per occurrence. RenderFont
    // instances are built per render via RenderFontFactory, so this cache is single-render and
    // needs no synchronization.
    private Dictionary<int, GlyphOutline>? _outlinesByGid;

    /// <summary>The font's units-per-em, for the design-unit -> text-space scale the caller folds into each glyph's device transform (TrueType/OpenType from <c>head</c>; CFF/Type1 the 1000-unit convention; Liberation substitutes 2048).</summary>
    public double UnitsPerEm { get; }

    internal RenderFont(
        ExtractionFont metrics,
        double unitsPerEm,
        FontReadLimits limits,
        SfntFont? sfnt = null,
        CmapTable? sfntCmap = null,
        CffParser? cff = null,
        Type1Parser? type1 = null,
        TrueTypeFontProgram? substituteFont = null,
        (string GlyphName, int Unicode)[]? encoding = null,
        bool isCid = false,
        int[]? cidToGid = null,
        bool isSymbolic = false,
        bool substitute = false)
    {
        _metrics = metrics;
        UnitsPerEm = unitsPerEm > 0 ? unitsPerEm : 1000.0;
        _limits = limits;
        _sfnt = sfnt;
        _sfntCmap = sfntCmap;
        _cff = cff;
        _type1 = type1;
        _substituteFont = substituteFont;
        _encoding = encoding;
        _isCid = isCid;
        _cidToGid = cidToGid;
        _isSymbolic = isSymbolic;
        _substitute = substitute;
    }

    /// <summary>
    /// Decodes exactly one character code at the start of <paramref name="bytes"/> — delegating to
    /// the wrapped <see cref="ExtractionFont"/> for the code length, advance width, and text — and
    /// additionally surfaces the raw integer code (which extraction consumes internally) so the
    /// caller can select the glyph and apply word spacing (§9.3.3, single-byte code 32 only).
    /// </summary>
    public void DecodeNext(ReadOnlySpan<byte> bytes, out int codeLength, out int code, out string unicode, out double width)
    {
        _metrics.DecodeNext(bytes, out codeLength, out unicode, out width);

        code = 0;
        var consumed = Math.Min(Math.Max(codeLength, 0), bytes.Length);
        for (var i = 0; i < consumed; i++)
        {
            code = (code << 8) | bytes[i];
        }
    }

    /// <summary>
    /// Resolves the decoded outline for character <paramref name="code"/> (with its decoded
    /// <paramref name="unicode"/> text, used for substitute-face glyph selection). Returns
    /// <see langword="false"/> — and leaves <paramref name="outline"/> at <see cref="GlyphOutline.Empty"/> —
    /// for a code with no renderable glyph (an unmapped code, a <c>.notdef</c>, or a font whose
    /// glyph source is unavailable): the caller skips painting it but still advances the text
    /// matrix, so surrounding text keeps its position.
    /// </summary>
    public bool TryGetGlyphOutline(int code, string unicode, out GlyphOutline outline)
    {
        outline = GlyphOutline.Empty;

        if (_type1 is not null)
        {
            if (_encoding is not null && code >= 0 && code < _encoding.Length)
            {
                var glyphName = _encoding[code].GlyphName;
                if (!string.IsNullOrEmpty(glyphName) && _type1.TryGetGlyphOutline(glyphName, out var t1) && t1.Commands.Count > 0)
                {
                    outline = t1;
                    return true;
                }
            }

            return false;
        }
        else if (_cff is not null && !_isCid)
        {
            // A bare, non-CID CFF (/FontFile3 Type1C): code -> glyph name (via the resolved
            // /Encoding, which may itself derive from the CFF's own built-in encoding — see
            // EncodingResolver's builtInBase) -> the CFF's charset name -> GID. Mirrors the
            // _type1 branch immediately above (same _encoding == null handling).
            if (_encoding is not null && code >= 0 && code < _encoding.Length)
            {
                var glyphName = _encoding[code].GlyphName;
                if (!string.IsNullOrEmpty(glyphName) && _cff.TryGetGlyphId(glyphName, out var cffGid))
                {
                    return TryOutlineByGid(cffGid, out outline);
                }
            }

            return false;
        }

        if (_isCid)
        {
            // Identity-H (and the Identity fallback every unsupported predefined CMap degrades to)
            // makes CID == code; CIDToGIDMap then gives the GID (Identity when the map is absent).
            return TryOutlineByGid(MapCidToGid(code), out outline);
        }

        if (_substitute && _substituteFont is not null)
        {
            var rune = FirstRune(unicode);
            if (rune >= 0 && _substituteFont.Cmap.TryGetGlyphId(rune, out var subGid))
            {
                return TryOutlineByGid(subGid, out outline);
            }

            return false;
        }

        // Embedded simple font (TrueType/OpenType): §9.6.6.4 code->glyph. Try the Unicode the
        // encoding resolved (the (3,1) cmap path), then the symbolic (3,0) conventions (0xF0xx
        // then the bare code) that symbol fonts use with no /Encoding.
        if (_sfntCmap is not null)
        {
            var rune = FirstRune(unicode);
            if (rune >= 0 && _sfntCmap.TryGetGlyphId(rune, out var uniGid) && uniGid != 0)
            {
                return TryOutlineByGid(uniGid, out outline);
            }

            if (_isSymbolic || rune < 0)
            {
                if (code is >= 0 and <= 0xFF && _sfntCmap.TryGetGlyphId(0xF000 | code, out var symGid) && symGid != 0)
                {
                    return TryOutlineByGid(symGid, out outline);
                }

                if (_sfntCmap.TryGetGlyphId(code, out var rawGid) && rawGid != 0)
                {
                    return TryOutlineByGid(rawGid, out outline);
                }
            }

            return false;
        }

        return false;
    }

    private int MapCidToGid(int cid)
    {
        if (_cidToGid is not null)
        {
            // /CIDToGIDMap stream: highest precedence — an explicit map always wins. ISO 32000-1
            // §9.7.4.2 defines the entry for CIDFontType2 only; a stream on a CID-keyed CFF is
            // spec-violating, and PDFium then runs the stream's output through the charset as a
            // CID again. We take the stream's output as the GID (the only reading under which the
            // producer's explicit map means anything); this is the single known divergence.
            return cid >= 0 && cid < _cidToGid.Length ? _cidToGid[cid] : 0;
        }

        if (_cff?.CidToGid is { } charsetMap)
        {
            // No /CIDToGIDMap (or /Identity): for a CID-keyed bare CFF, GID is looked up through
            // the font's own charset (ISO 32000-1 §9.7.4.2) rather than assumed identity — a
            // subsetted/reordered CIDFontType0C's charset is very often not CID == GID.
            return cid >= 0 && cid < charsetMap.Length ? charsetMap[cid] : 0;
        }

        return cid; // Identity — no map and no CFF charset to consult (e.g. embedded TrueType CID).
    }

    private bool TryOutlineByGid(int gid, out GlyphOutline outline)
    {
        outline = GlyphOutline.Empty;
        if (gid <= 0)
        {
            return false; // GID 0 is always .notdef — never paint a substitute box for it.
        }

        _outlinesByGid ??= [];
        if (_outlinesByGid.TryGetValue(gid, out var cached))
        {
            outline = cached;
            return cached.Commands.Count > 0;
        }

        try
        {
            if (_sfnt is not null)
            {
                outline = _sfnt.GetGlyphOutline(gid, _limits);
            }
            else if (_cff is not null)
            {
                outline = _cff.GetGlyphOutline(gid);
            }
            else if (_substituteFont is not null)
            {
                outline = _substituteFont.Glyf.BuildOutline(gid, _substituteFont.Limits);
            }
            else
            {
                return false;
            }
        }
        catch (PlumePdfException)
        {
            // A corrupt/over-limit glyph (PLUME8xxx) is skipped, not fatal — the rest of the run
            // still renders (lenient-by-default, matching the paint pass's own philosophy).
            // Cached as Empty so the corrupt program is parsed-and-rejected once, not per
            // occurrence.
            _outlinesByGid[gid] = GlyphOutline.Empty;
            return false;
        }

        _outlinesByGid[gid] = outline;
        return outline.Commands.Count > 0;
    }

    private static int FirstRune(string unicode)
    {
        if (string.IsNullOrEmpty(unicode))
        {
            return -1;
        }

        var c0 = unicode[0];
        if (c0 == '�')
        {
            return -1; // The extraction layer's "no mapping" sentinel — not a real glyph selector.
        }

        if (char.IsHighSurrogate(c0) && unicode.Length > 1 && char.IsLowSurrogate(unicode[1]))
        {
            return char.ConvertToUtf32(c0, unicode[1]);
        }

        return c0;
    }
}
