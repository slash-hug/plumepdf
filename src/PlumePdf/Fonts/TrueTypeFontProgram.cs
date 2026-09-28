using PlumePdf.Fonts.Tables;

namespace PlumePdf.Fonts;

/// <summary>
/// A parsed TrueType/OpenType font program: every table this Phase 2 subset understands,
/// held together and exposed both as <see cref="IFontMetrics"/> (for the layout engine) and
/// as its typed tables (for <see cref="SimpleShaper"/>, <see cref="FontSubsetter"/>, and
/// <see cref="FontObjectBuilder"/>, which all need more than the metrics-only view). Built
/// entirely from the freely published OpenType specification, not from any other library's
/// source (the clean-room policy in AGENTS.md).
/// </summary>
internal sealed class TrueTypeFontProgram : IFontMetrics
{
    private TrueTypeFontProgram(
        SfntFont sfnt,
        byte[] rawData,
        HeadTable head,
        HheaTable hhea,
        MaxpTable maxp,
        HmtxTable hmtx,
        CmapTable cmap,
        NameTable name,
        Os2Table os2,
        PostTable post,
        GlyfTable glyf,
        LocaTable loca,
        GsubTable gsub,
        GposTable gpos,
        GdefTable gdef,
        FontReadLimits limits)
    {
        Sfnt = sfnt;
        RawData = rawData;
        Head = head;
        Hhea = hhea;
        Maxp = maxp;
        Hmtx = hmtx;
        Cmap = cmap;
        Name = name;
        Os2 = os2;
        Post = post;
        Glyf = glyf;
        Loca = loca;
        Gsub = gsub;
        Gpos = gpos;
        Gdef = gdef;
        Limits = limits;
    }

    /// <summary>The parsed table directory this font was read from — <see cref="FontSubsetter"/> uses it to copy glyph-ID-free tables (<c>name</c>, <c>OS/2</c>, <c>post</c>) through to a subset verbatim.</summary>
    public SfntFont Sfnt { get; }

    /// <summary>The font file's full, original bytes — the source <see cref="FontSubsetter"/> copies glyph/table data out of.</summary>
    public byte[] RawData { get; }

    public HeadTable Head { get; }

    public HheaTable Hhea { get; }

    public MaxpTable Maxp { get; }

    public HmtxTable Hmtx { get; }

    public CmapTable Cmap { get; }

    public NameTable Name { get; }

    public Os2Table Os2 { get; }

    public PostTable Post { get; }

    public GlyfTable Glyf { get; }

    public LocaTable Loca { get; }

    /// <summary>Ligature-substitution lookups (empty if the font has none, or GSUB is absent).</summary>
    public GsubTable Gsub { get; }

    /// <summary>Pair-kerning lookups (empty if the font has none, or GPOS is absent).</summary>
    public GposTable Gpos { get; }

    /// <summary>
    /// The font's <c>GDEF</c> table (glyph classes, mark-attachment classes, mark-filtering
    /// sets): <see cref="GdefTable.Empty"/> if the font has no <c>GDEF</c> table or it
    /// doesn't parse. Consumed by <see cref="Shaping.OpenTypeLayoutEngine"/> for
    /// <c>LookupFlag</c> skip decisions; <see cref="GsubTable"/>/<see cref="GposTable"/>'s own
    /// narrow Phase 2 fast path does not need it.
    /// </summary>
    public GdefTable Gdef { get; }

    /// <summary>The resource limits this font was parsed under — reused by <see cref="FontSubsetter"/>'s composite-closure walk.</summary>
    public FontReadLimits Limits { get; }

    /// <summary>
    /// Parses <paramref name="fontBytes"/> as a TrueType font, reading every table this Phase
    /// 2 subset understands. Every offset, length, and count is bounds-checked against
    /// <paramref name="limits"/> before being trusted — see the individual
    /// <c>PlumePdf.Fonts.Tables</c> parsers for the specific <c>PLUME8xxx</c> codes each
    /// failure mode raises.
    /// </summary>
    public static TrueTypeFontProgram Parse(byte[] fontBytes, FontReadLimits? limits = null)
    {
        var effectiveLimits = limits ?? FontReadLimits.Default;
        var sfnt = SfntFont.Parse(fontBytes, effectiveLimits);

        var head = HeadTable.Parse(sfnt.GetRequiredTable("head"));
        var hhea = HheaTable.Parse(sfnt.GetRequiredTable("hhea"));
        var maxp = MaxpTable.Parse(sfnt.GetRequiredTable("maxp"), effectiveLimits);
        var hmtx = HmtxTable.Parse(sfnt.GetRequiredTable("hmtx"), hhea.NumberOfHMetrics, maxp.NumGlyphs);
        var cmap = CmapTable.Parse(sfnt.GetRequiredTable("cmap"));

        var name = sfnt.TryGetTable("name", out var nameTable) ? NameTable.Parse(nameTable) : NameTable.Parse(ReadOnlyMemory<byte>.Empty);
        var os2 = sfnt.TryGetTable("OS/2", out var os2Table) ? Os2Table.Parse(os2Table) : default;
        var post = sfnt.TryGetTable("post", out var postTable) ? PostTable.Parse(postTable) : default;

        var locaTableBytes = sfnt.GetRequiredTable("loca");
        var loca = LocaTable.Parse(locaTableBytes, maxp.NumGlyphs, head.IndexToLocFormat != 0);

        if (!sfnt.Tables.TryGetValue("glyf", out var glyfRecord))
        {
            throw new PlumePdfException("PLUME8003", "Font is missing the required 'glyf' table.");
        }

        var glyf = GlyfTable.Wrap(fontBytes, glyfRecord.Offset, glyfRecord.Length, loca);

        var gsub = sfnt.TryGetTable("GSUB", out var gsubTable) ? GsubTable.Parse(gsubTable) : GsubTable.Parse(ReadOnlyMemory<byte>.Empty);
        var gpos = sfnt.TryGetTable("GPOS", out var gposTable) ? GposTable.Parse(gposTable) : GposTable.Parse(ReadOnlyMemory<byte>.Empty);
        var gdef = sfnt.TryGetTable("GDEF", out var gdefTable) ? GdefTable.Parse(gdefTable) : GdefTable.Empty;

        return new TrueTypeFontProgram(sfnt, fontBytes, head, hhea, maxp, hmtx, cmap, name, os2, post, glyf, loca, gsub, gpos, gdef, effectiveLimits);
    }

    /// <inheritdoc/>
    public string BaseFontName => Name.PostScriptName ?? Name.FamilyName ?? "EmbeddedFont";

    /// <inheritdoc/>
    public int UnitsPerEm => Head.UnitsPerEm;

    /// <inheritdoc/>
    public double Ascender => Os2.TypoAscender != 0 ? Os2.TypoAscender : Hhea.Ascender;

    /// <inheritdoc/>
    public double Descender => Os2.TypoDescender != 0 ? Os2.TypoDescender : Hhea.Descender;

    /// <inheritdoc/>
    public double CapHeight => Os2.CapHeight != 0 ? Os2.CapHeight : Head.YMax;

    /// <inheritdoc/>
    public double ItalicAngle => Post.ItalicAngle;

    /// <inheritdoc/>
    public bool IsFixedPitch => Post.IsFixedPitch;

    /// <inheritdoc/>
    public (double XMin, double YMin, double XMax, double YMax) FontBoundingBox => (Head.XMin, Head.YMin, Head.XMax, Head.YMax);

    /// <inheritdoc/>
    public bool TryGetGlyphId(int codepoint, out int glyphId)
    {
        if (Cmap.TryGetGlyphId(codepoint, out var gid))
        {
            glyphId = gid;
            return true;
        }

        glyphId = 0;
        return false;
    }

    /// <inheritdoc/>
    public double GetAdvanceWidth(int glyphId) => Hmtx.GetAdvanceWidth(glyphId);
}
