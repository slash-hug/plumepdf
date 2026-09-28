using System.Globalization;
using System.Text;
using PlumePdf.Fonts.Standard14;
using PlumePdf.Objects;

namespace PlumePdf.Fonts;

/// <summary>The result of embedding a subsetted TrueType font: the composite <c>/Font</c> dictionary's reference, its subset tag, and the retained old-glyph-ID→new-glyph-ID map (the content-stream builder needs this to translate the glyph IDs <see cref="SimpleShaper"/> produced against the original font).</summary>
/// <param name="FontDictionaryReference">The indirect reference to the top-level <c>/Type0</c> font dictionary — what a page's <c>/Resources /Font</c> entry points at.</param>
/// <param name="Tag">The six-letter subset tag (see <see cref="FontSubsetter"/>).</param>
/// <param name="GlyphIdMap">Original glyph ID → subset (CID) glyph ID.</param>
internal readonly record struct EmbeddedFontResult(IndirectReference FontDictionaryReference, string Tag, IReadOnlyDictionary<int, int> GlyphIdMap);

/// <summary>
/// One glyph-cluster's <c>/ToUnicode</c> mapping: an original (pre-subset) glyph ID a shaper
/// emitted, paired with every Unicode codepoint of input text that glyph's cluster consumed.
/// A ligature or conjunct (e.g. Latin "fi", Arabic لا, Devanagari क्ष) is an N-codepoint,
/// one-glyph cluster — <paramref name="Codepoints"/> carries all N so the single output glyph
/// gets a correct multi-character <c>bfchar</c> destination instead of the silent gap the
/// plain per-codepoint <c>cmap</c> lookup below leaves for every substituted glyph. For a
/// reordered one-codepoint-to-many-glyphs cluster (a capability the current
/// <see cref="ShapedGlyph"/> shape cannot express — no cluster id, only a single
/// <see cref="ShapedGlyph.TextIndex"/>/<see cref="ShapedGlyph.CodepointCount"/> pair per
/// glyph), the rule is: the cluster's first glyph in shaping order carries every
/// codepoint, trailing glyphs of that cluster are simply omitted from the list — a missing
/// <c>bfchar</c> entry is spec-legal, never a wrong one.
/// </summary>
/// <remarks>
/// This is <see cref="FontObjectBuilder"/>'s own local shape of the renderer→font-object-builder
/// cluster contract Phase 6.5 formalized as the shared <see cref="GlyphClusterMap"/>
/// type. <c>ManuscriptRenderer</c> is the real, wired-up production caller: its
/// <c>DocumentBuilder</c> accumulates one <see cref="GlyphClusterMap"/> per embedded font as
/// <c>TextLayouter.ShapeVisualLine</c> shapes each line, then converts it to a list of these
/// before calling <see cref="FontObjectBuilder.BuildEmbeddedTrueType"/>. A caller that still
/// passes no clusters (or an empty list) falls back to the pre-existing per-codepoint
/// <c>cmap</c> walk below, byte-identical to pre-Phase-6.5 output — but that fallback is a
/// caller choice now, not the only path that exists.
/// </remarks>
internal readonly record struct GlyphCluster(int GlyphId, IReadOnlyList<int> Codepoints);

/// <summary>
/// Builds the PDF object graph for a font — the <c>/Font</c> dictionary a page resource
/// points at, plus (for an embedded font) its <c>/FontDescriptor</c>, <c>/FontFile2</c>, and
/// <c>/ToUnicode</c> CMap. Standard-14 fonts get a plain <c>/Type1</c> simple font dictionary
/// (ISO 32000-1 §9.6.2); embedded TrueType fonts get a composite <c>/Type0</c>/
/// <c>CIDFontType2</c> pair under <c>/Identity-H</c> encoding (§9.7) — CIDs equal subset glyph
/// IDs directly, which is exactly what a pre-shaped glyph run (<see cref="SimpleShaper"/>'s
/// output) already is, so the content-stream builder never needs a separate code↔glyph
/// translation table.
/// </summary>
/// <remarks>
/// <c>/FontFile2</c> is Flate-encoded via the registry's <c>FlateDecode</c> encoder when one
/// is registered; <c>/Length1</c> always carries the uncompressed length per ISO
/// 32000-1 §9.9. A registry with no encoder degrades to an uncompressed (larger, still valid)
/// stream — mirroring <c>PageContentAssembler</c>'s contract.
/// </remarks>
internal static class FontObjectBuilder
{
    private static readonly PdfName FontDescriptorName = PdfName.Get("FontDescriptor");
    private static readonly PdfName FontFile2Name = PdfName.Get("FontFile2");
    private static readonly PdfName BaseFontName = PdfName.Get("BaseFont");

    /// <summary>Builds a simple <c>/Type1</c> font dictionary for a Standard-14 font. No <c>/FontDescriptor</c> or embedded program — PDF readers supply these fonts themselves.</summary>
    public static IndirectReference BuildStandard14(Standard14Font font, Func<PdfObject, IndirectReference> allocate)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(allocate);

        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("Font"));
        dict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        dict.Set(BaseFontName, PdfName.Get(font.BaseFontName));

        // Symbol/ZapfDingbats are symbolic — they carry their own built-in encoding and must
        // NOT declare /Encoding /WinAnsiEncoding (§9.6.6.2: a symbolic font's built-in
        // encoding should be used unmodified).
        if (font.BaseFontName is not ("Symbol" or "ZapfDingbats"))
        {
            dict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));
        }

        dict.Set(PdfName.Get("FirstChar"), PdfNumber.Get(32));
        dict.Set(PdfName.Get("LastChar"), PdfNumber.Get(255));

        var widths = new PdfArray();
        for (var code = 32; code <= 255; code++)
        {
            widths.Add(PdfNumber.Get((long)font.GetAdvanceWidth(code)));
        }

        dict.Set(PdfName.Get("Widths"), widths);

        return allocate(dict);
    }

    /// <summary>
    /// Subsets <paramref name="font"/> to <paramref name="usedGlyphIds"/>, builds its
    /// <c>/FontFile2</c>, <c>/FontDescriptor</c>, <c>CIDFontType2</c>, <c>/ToUnicode</c>, and
    /// top-level <c>/Type0</c> dictionaries, and allocates all of them via
    /// <paramref name="allocate"/> (leaf-first, so each parent can reference its children by
    /// the reference <paramref name="allocate"/> just returned — the same shape
    /// <c>DocumentComposer</c> uses for its own object graph construction).
    /// </summary>
    public static EmbeddedFontResult BuildEmbeddedTrueType(
        TrueTypeFontProgram font,
        IReadOnlySet<int> usedGlyphIds,
        IReadOnlyDictionary<int, int>? codepointToGlyphId,
        Func<PdfObject, IndirectReference> allocate,
        PdfFilterRegistry filterRegistry,
        PdfOptions options,
        IReadOnlyList<GlyphCluster>? clusters = null)
    {
        ArgumentNullException.ThrowIfNull(filterRegistry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(usedGlyphIds);
        ArgumentNullException.ThrowIfNull(allocate);

        var subset = FontSubsetter.Subset(font, usedGlyphIds, codepointToGlyphId);
        var fullName = $"{subset.Tag}+{font.BaseFontName}";

        var fontFileDict = new PdfDictionary();
        fontFileDict.Set(PdfName.Get("Length1"), PdfNumber.Get(subset.FontBytes.LongLength));
        var fontFilePayload = subset.FontBytes;
        if (filterRegistry.TryGetEncoder("FlateDecode", out var fontFileEncoder) && fontFileEncoder is not null)
        {
            fontFilePayload = fontFileEncoder.Encode(subset.FontBytes, options);
            fontFileDict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        }

        var fontFileStream = new PdfStream(fontFileDict, fontFilePayload);
        var fontFileRef = allocate(fontFileStream);

        var cidSetRef = allocate(BuildCidSet(subset.GlyphIdMap));
        var representedCodepoints = CollectRepresentedCodepoints(codepointToGlyphId, clusters);
        var descriptor = BuildFontDescriptor(font, fullName, fontFileRef, cidSetRef, representedCodepoints);
        var descriptorRef = allocate(descriptor);

        var cidFont = BuildCidFont(font, fullName, subset, descriptorRef);
        var cidFontRef = allocate(cidFont);

        var toUnicodeRef = allocate(BuildToUnicodeCMap(codepointToGlyphId, subset.GlyphIdMap, clusters));

        var type0 = new PdfDictionary();
        type0.Set(PdfName.Type, PdfName.Get("Font"));
        type0.Set(PdfName.Subtype, PdfName.Get("Type0"));
        type0.Set(BaseFontName, PdfName.Get(fullName));
        type0.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        type0.Set(PdfName.Get("DescendantFonts"), new PdfArray([new PdfReference(cidFontRef)]));
        type0.Set(PdfName.Get("ToUnicode"), new PdfReference(toUnicodeRef));

        var type0Ref = allocate(type0);
        return new EmbeddedFontResult(type0Ref, subset.Tag, subset.GlyphIdMap);
    }

    /// <summary>
    /// Derives a <see cref="GlyphCluster"/> list from one shaped run's glyphs and the exact
    /// input text span that produced them — the shape a caller (<c>ManuscriptRenderer</c>)
    /// wires into <see cref="BuildEmbeddedTrueType"/>'s <c>clusters</c> parameter once it
    /// collects cluster data per line instead of the independent per-Rune <c>cmap</c> walk it
    /// uses today. Every emitted glyph becomes exactly one cluster entry — today's
    /// <see cref="ShapedGlyph"/> shape (a single <see cref="ShapedGlyph.TextIndex"/>/
    /// <see cref="ShapedGlyph.CodepointCount"/> pair per glyph) can only express an N-codepoints
    /// to-one-glyph cluster (a ligature or conjunct), never a one-codepoint-to-many-glyphs
    /// reordered cluster — that needs a cluster id field on the shaping seam and is out
    /// of reach until it lands.
    /// </summary>
    /// <param name="shapedRun">The shaped glyphs, in the same order <see cref="ILineShaper.Shape"/> produced them.</param>
    /// <param name="text">The exact text span that was shaped — <see cref="ShapedGlyph.TextIndex"/>/<see cref="ShapedGlyph.CodepointCount"/> index into this span.</param>
    public static IReadOnlyList<GlyphCluster> BuildClustersFromShapedRun(ShapedRun shapedRun, ReadOnlySpan<char> text)
    {
        ArgumentNullException.ThrowIfNull(shapedRun);

        var clusters = new List<GlyphCluster>(shapedRun.Glyphs.Count);
        foreach (var glyph in shapedRun.Glyphs)
        {
            if (glyph.TextIndex < 0 || glyph.CodepointCount <= 0 || glyph.TextIndex + glyph.CodepointCount > text.Length)
            {
                // Defensive only — the production call site always shapes and then passes
                // back the same span; a mismatched caller loses this glyph's ToUnicode entry
                // rather than throwing or reading out of range.
                continue;
            }

            var slice = text.Slice(glyph.TextIndex, glyph.CodepointCount);
            var codepoints = new List<int>(glyph.CodepointCount);
            var i = 0;
            while (i < slice.Length)
            {
                if (char.IsHighSurrogate(slice[i]) && i + 1 < slice.Length && char.IsLowSurrogate(slice[i + 1]))
                {
                    codepoints.Add(char.ConvertToUtf32(slice[i], slice[i + 1]));
                    i += 2;
                }
                else
                {
                    codepoints.Add(slice[i]);
                    i += 1;
                }
            }

            clusters.Add(new GlyphCluster(glyph.GlyphId, codepoints));
        }

        return clusters;
    }

    /// <summary>Every distinct Unicode codepoint this embedding's ToUnicode provenance covers — from <paramref name="clusters"/> when supplied (the richer source), else from the legacy per-codepoint map. Used to re-derive the descriptor's Symbolic/Nonsymbolic flags (see <see cref="BuildFontDescriptor"/>).</summary>
    private static IReadOnlyCollection<int> CollectRepresentedCodepoints(IReadOnlyDictionary<int, int>? codepointToGlyphId, IReadOnlyList<GlyphCluster>? clusters)
    {
        if (clusters is { Count: > 0 })
        {
            var set = new HashSet<int>();
            foreach (var cluster in clusters)
            {
                foreach (var codepoint in cluster.Codepoints)
                {
                    set.Add(codepoint);
                }
            }

            return set;
        }

        return codepointToGlyphId?.Keys.ToArray() ?? [];
    }

    /// <summary>The set of Unicode codepoints ISO 32000-1 Annex D.2's WinAnsiEncoding assigns — the same table the write path already uses for Standard-14 fonts (<see cref="Standard14Encodings.WinAnsiEncoding"/>), reused here as the "does this look like ordinary Latin text" repertoire test for the embedded descriptor's Symbolic flag.</summary>
    private static readonly HashSet<int> WinAnsiCodepoints =
        Standard14Encodings.WinAnsiEncoding
            .Where(static entry => entry.Unicode >= 0)
            .Select(static entry => entry.Unicode)
            .ToHashSet();

    /// <summary>
    /// True when every codepoint this embedding represents falls inside WinAnsiEncoding's
    /// repertoire — the practical test for "Nonsymbolic" (ISO 32000-1 Table 123 bit 6): a
    /// font whose used text stays inside the standard Latin-text encoding. An empty
    /// <paramref name="representedCodepoints"/> (a caller that supplied neither
    /// <c>codepointToGlyphId</c> nor <c>clusters</c>) keeps the historical Nonsymbolic default
    /// rather than guessing from nothing.
    /// </summary>
    private static bool IsNonsymbolic(IReadOnlyCollection<int> representedCodepoints)
    {
        if (representedCodepoints.Count == 0)
        {
            return true;
        }

        foreach (var codepoint in representedCodepoints)
        {
            if (!WinAnsiCodepoints.Contains(codepoint))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the <c>/CIDSet</c> stream (ISO 32000-1 §9.8.3, Table 124): one bit per CID, set
    /// when that CID's glyph is present in the embedded subset — CID 0 (<c>.notdef</c>) is
    /// always present, then every retained glyph's subset ID. PDF/A-1 requires this stream for
    /// a subset CIDFont (veraPDF clause 6.3.5, observed empirically from its CLI report per
    /// the clean-room policy in AGENTS.md); it is valid, tiny, and useful metadata for every
    /// other consumer too, so it is written unconditionally rather than only under a PDF/A
    /// conformance.
    /// </summary>
    private static PdfStream BuildCidSet(IReadOnlyDictionary<int, int> glyphIdMap)
    {
        var maxCid = 0;
        foreach (var newId in glyphIdMap.Values)
        {
            maxCid = Math.Max(maxCid, newId);
        }

        var bits = new byte[(maxCid / 8) + 1];
        bits[0] |= 0x80; // CID 0, .notdef — FontSubsetter always retains it.
        foreach (var newId in glyphIdMap.Values)
        {
            bits[newId / 8] |= (byte)(0x80 >> (newId % 8));
        }

        return new PdfStream(new PdfDictionary(), bits);
    }

    private static PdfDictionary BuildFontDescriptor(TrueTypeFontProgram font, string fullName, IndirectReference fontFileRef, IndirectReference cidSetRef, IReadOnlyCollection<int> representedCodepoints)
    {
        var flags = 0;
        if (font.IsFixedPitch)
        {
            flags |= 1; // FixedPitch
        }

        // Symbolic vs. Nonsymbolic (ISO 32000-1 Table 123, bits 3/6 — exactly one must be
        // set): re-derived from what this embedding actually encodes rather than the old
        // hardcoded "Phase 2 is Latin, so always Nonsymbolic" assumption, which was already
        // wrong for the Cyrillic/Greek text Phase 2 ships (neither is in WinAnsiEncoding) and
        // would be flatly wrong for Arabic/Devanagari. `TrueTypeFontProgram`'s parsed OS/2
        // table (<see cref="Tables.Os2Table"/>) carries no symbolic-repertoire
        // signal at all (no usFirstCharIndex/usLastCharIndex, no Unicode-range bits) — the
        // actual per-embedding codepoint provenance already collected for /ToUnicode is the
        // real answer to "does this font's used text fit the standard Latin-text encoding",
        // which is precisely what the Symbolic flag distinguishes.
        flags |= IsNonsymbolic(representedCodepoints) ? 0x20 : 0x04;

        if (font.ItalicAngle != 0)
        {
            flags |= 0x40; // Italic
        }

        var scale = 1000.0 / font.UnitsPerEm;
        var (xMin, yMin, xMax, yMax) = font.FontBoundingBox;

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Type, FontDescriptorName);
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get(fullName));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(flags));
        descriptor.Set(PdfName.Get("FontBBox"), new PdfArray([
            PdfNumber.Get(xMin * scale), PdfNumber.Get(yMin * scale), PdfNumber.Get(xMax * scale), PdfNumber.Get(yMax * scale),
        ]));
        descriptor.Set(PdfName.Get("ItalicAngle"), PdfNumber.Get(font.ItalicAngle));
        descriptor.Set(PdfName.Get("Ascent"), PdfNumber.Get(font.Ascender * scale));
        descriptor.Set(PdfName.Get("Descent"), PdfNumber.Get(font.Descender * scale));
        descriptor.Set(PdfName.Get("CapHeight"), PdfNumber.Get(font.CapHeight * scale));

        // Real stem width isn't present in a TrueType font's tables at all — this is the
        // same coarse weight-based estimate every minimal PDF font embedder uses (nothing in
        // §9.8.1 requires exactness; it's a rasterizer hint).
        var stemV = font.Os2.WeightClass >= 600 ? 120 : 80;
        descriptor.Set(PdfName.Get("StemV"), PdfNumber.Get(stemV));
        descriptor.Set(FontFile2Name, new PdfReference(fontFileRef));
        descriptor.Set(PdfName.Get("CIDSet"), new PdfReference(cidSetRef));

        return descriptor;
    }

    private static PdfDictionary BuildCidFont(TrueTypeFontProgram font, string fullName, SubsetResult subset, IndirectReference descriptorRef)
    {
        var scale = 1000.0 / font.UnitsPerEm;

        var cidFont = new PdfDictionary();
        cidFont.Set(PdfName.Type, PdfName.Get("Font"));
        cidFont.Set(PdfName.Subtype, PdfName.Get("CIDFontType2"));
        cidFont.Set(BaseFontName, PdfName.Get(fullName));

        var cidSystemInfo = new PdfDictionary();
        cidSystemInfo.Set(PdfName.Get("Registry"), PdfString.FromLiteral("Adobe"u8.ToArray()));
        cidSystemInfo.Set(PdfName.Get("Ordering"), PdfString.FromLiteral("Identity"u8.ToArray()));
        cidSystemInfo.Set(PdfName.Get("Supplement"), PdfNumber.Get(0));
        cidFont.Set(PdfName.Get("CIDSystemInfo"), cidSystemInfo);

        cidFont.Set(FontDescriptorName, new PdfReference(descriptorRef));
        cidFont.Set(PdfName.Get("CIDToGIDMap"), PdfName.Get("Identity"));

        var widthsArray = new PdfArray();
        foreach (var (oldId, newId) in subset.GlyphIdMap.OrderBy(static kvp => kvp.Value))
        {
            var width = font.GetAdvanceWidth(oldId) * scale;
            widthsArray.Add(PdfNumber.Get((long)newId));
            widthsArray.Add(new PdfArray([PdfNumber.Get(width)]));
        }

        cidFont.Set(PdfName.Get("W"), widthsArray);

        return cidFont;
    }

    /// <summary>
    /// Builds the <c>/ToUnicode</c> CMap (ISO 32000-1 §9.10.3). Cluster-driven when
    /// <paramref name="clusters"/> is non-empty: every glyph a shaper actually emitted
    /// gets the exact codepoint sequence its cluster consumed, so an N-codepoint ligature or
    /// conjunct — which today's plain per-Rune <paramref name="codepointToGlyphId"/> walk
    /// silently drops, since none of its N independent single-codepoint lookups ever resolves
    /// to the substituted glyph ID that was actually drawn — gets a correct multi-character
    /// <c>bfchar</c> destination instead of no entry at all. Falls back to the legacy
    /// per-codepoint map (byte-identical to the pre-6.5 behavior) when no caller has supplied
    /// clusters yet.
    /// </summary>
    private static PdfStream BuildToUnicodeCMap(IReadOnlyDictionary<int, int>? codepointToGlyphId, IReadOnlyDictionary<int, int> glyphIdMap, IReadOnlyList<GlyphCluster>? clusters = null)
    {
        var glyphIdToCodepoints = new SortedDictionary<int, IReadOnlyList<int>>();

        if (clusters is { Count: > 0 })
        {
            foreach (var cluster in clusters)
            {
                if (cluster.Codepoints.Count == 0)
                {
                    // The "trailing glyph of a reordered cluster" case: deliberately no
                    // bfchar entry for this glyph, not an error.
                    continue;
                }

                if (glyphIdMap.TryGetValue(cluster.GlyphId, out var newGlyphId))
                {
                    glyphIdToCodepoints.TryAdd(newGlyphId, cluster.Codepoints);
                }
            }
        }
        else if (codepointToGlyphId is not null)
        {
            foreach (var (codepoint, oldGlyphId) in codepointToGlyphId)
            {
                if (glyphIdMap.TryGetValue(oldGlyphId, out var newGlyphId))
                {
                    glyphIdToCodepoints.TryAdd(newGlyphId, [codepoint]);
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n");
        sb.Append("12 dict begin\n");
        sb.Append("begincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n");
        sb.Append("/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        var entries = glyphIdToCodepoints.ToArray();
        for (var start = 0; start < entries.Length; start += 100)
        {
            var chunk = entries.Skip(start).Take(100).ToArray();
            sb.Append(chunk.Length.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            foreach (var (glyphId, codepoints) in chunk)
            {
                sb.Append('<').Append(glyphId.ToString("X4", CultureInfo.InvariantCulture)).Append("> <");
                AppendUtf16BeHex(sb, codepoints);
                sb.Append(">\n");
            }

            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\n");
        sb.Append("CMapName currentdict /CMap defineresource pop\n");
        sb.Append("end\nend\n");

        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        return new PdfStream(new PdfDictionary(), bytes);
    }

    /// <summary>Appends a <c>bfchar</c> destination string's hex digits for one or more codepoints, UTF-16BE — a ligature/conjunct cluster's multi-codepoint destination is simply every codepoint's encoding concatenated in order (ISO 32000-1 §9.10.3's informative "ffi" ligature example uses exactly this shape). A supplementary-plane codepoint is written as a surrogate pair (a bare 4-hex-digit encoding would emit an odd-length string every extractor reads as garbage).</summary>
    private static void AppendUtf16BeHex(StringBuilder sb, IReadOnlyList<int> codepoints)
    {
        foreach (var codepoint in codepoints)
        {
            if (codepoint > 0xFFFF)
            {
                var v = codepoint - 0x10000;
                var high = 0xD800 + (v >> 10);
                var low = 0xDC00 + (v & 0x3FF);
                sb.Append(high.ToString("X4", CultureInfo.InvariantCulture))
                    .Append(low.ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(codepoint.ToString("X4", CultureInfo.InvariantCulture));
            }
        }
    }
}
