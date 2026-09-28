using PlumePdf.Fonts;
using PlumePdf.Fonts.Standard14;

namespace PlumePdf;

/// <summary>
/// Names a font for use in a composed document: one of the 14 standard PDF fonts (no
/// embedding — every PDF reader supplies these), or a TrueType/OpenType font file to embed
/// and subset. This is the entire public font surface: the parser, subsetter, and
/// shaping seam behind it (<c>PlumePdf.Fonts</c>) stay internal until the font extension seam
/// itself goes public (a documented 1.x backlog item) — a third party cannot yet plug in
/// its own <see cref="ILineShaper"/>, but can already name any font it has bytes for.
/// </summary>
/// <example>
/// <code>
/// PdfFont body = PdfFont.Helvetica;
/// PdfFont heading = PdfFont.FromFile("NotoSans-Bold.ttf");
/// </code>
/// </example>
public sealed class PdfFont
{
    private PdfFont(string name, IFontMetrics metrics)
    {
        Name = name;
        Metrics = metrics;
    }

    /// <summary>The font's name — a Standard-14 name (e.g. <c>"Helvetica"</c>) or the embedded font's own PostScript/family name.</summary>
    public string Name { get; }

    /// <summary>The internal metrics/shaping seam this facade wraps — consumed by the layout engine, never exposed publicly.</summary>
    internal IFontMetrics Metrics { get; }

    /// <summary>Helvetica, the standard sans-serif PDF font. No embedding.</summary>
    public static PdfFont Helvetica { get; } = FromStandard14("Helvetica");

    /// <summary>Helvetica-Bold, the standard sans-serif PDF font, bold weight. No embedding.</summary>
    public static PdfFont HelveticaBold { get; } = FromStandard14("Helvetica-Bold");

    /// <summary>Helvetica-Oblique, the standard sans-serif PDF font, italic. No embedding.</summary>
    public static PdfFont HelveticaOblique { get; } = FromStandard14("Helvetica-Oblique");

    /// <summary>Helvetica-BoldOblique, the standard sans-serif PDF font, bold italic. No embedding.</summary>
    public static PdfFont HelveticaBoldOblique { get; } = FromStandard14("Helvetica-BoldOblique");

    /// <summary>Times-Roman, the standard serif PDF font. No embedding.</summary>
    public static PdfFont TimesRoman { get; } = FromStandard14("Times-Roman");

    /// <summary>Times-Bold, the standard serif PDF font, bold weight. No embedding.</summary>
    public static PdfFont TimesBold { get; } = FromStandard14("Times-Bold");

    /// <summary>Times-Italic, the standard serif PDF font, italic. No embedding.</summary>
    public static PdfFont TimesItalic { get; } = FromStandard14("Times-Italic");

    /// <summary>Times-BoldItalic, the standard serif PDF font, bold italic. No embedding.</summary>
    public static PdfFont TimesBoldItalic { get; } = FromStandard14("Times-BoldItalic");

    /// <summary>Courier, the standard monospace PDF font. No embedding.</summary>
    public static PdfFont Courier { get; } = FromStandard14("Courier");

    /// <summary>Courier-Bold, the standard monospace PDF font, bold weight. No embedding.</summary>
    public static PdfFont CourierBold { get; } = FromStandard14("Courier-Bold");

    /// <summary>Courier-Oblique, the standard monospace PDF font, italic. No embedding.</summary>
    public static PdfFont CourierOblique { get; } = FromStandard14("Courier-Oblique");

    /// <summary>Courier-BoldOblique, the standard monospace PDF font, bold italic. No embedding.</summary>
    public static PdfFont CourierBoldOblique { get; } = FromStandard14("Courier-BoldOblique");

    /// <summary>Symbol, the standard PDF symbol font (Greek letters and mathematical symbols). No embedding.</summary>
    public static PdfFont Symbol { get; } = FromStandard14("Symbol");

    /// <summary>ZapfDingbats, the standard PDF dingbat/ornament font. No embedding.</summary>
    public static PdfFont ZapfDingbats { get; } = FromStandard14("ZapfDingbats");

    /// <summary>
    /// Loads a TrueType/OpenType font file to embed and subset. Only the glyphs actually used
    /// in the composed document are embedded (<see cref="Fonts.FontSubsetter"/>), keeping
    /// output size proportional to the text drawn, not the font's full glyph count.
    /// </summary>
    /// <param name="path">Path to a <c>.ttf</c>/<c>.otf</c> file (TrueType-flavored outlines only — CFF is out of Phase 2 scope).</param>
    /// <exception cref="PlumePdfException">A coded <c>PLUME8xxx</c> failure parsing the font file — see <c>docs/errors/</c> for the specific codes.</exception>
    /// <example>
    /// <code>
    /// PdfFont heading = PdfFont.FromFile("fonts/NotoSans-Bold.ttf");
    /// </code>
    /// </example>
    public static PdfFont FromFile(string path) => FromFile(path, PdfOptions.Default);

    /// <summary>
    /// Loads a TrueType/OpenType font file to embed and subset, parsing it under
    /// <paramref name="options"/>'s font resource limits (<see cref="PdfOptions.MaxFontFileBytes"/>,
    /// <see cref="PdfOptions.MaxFontGlyphCount"/>, <see cref="PdfOptions.MaxCompositeGlyphDepth"/>,
    /// <see cref="PdfOptions.MaxSubsetGlyphs"/>) instead of <see cref="PdfOptions.Default"/>'s.
    /// </summary>
    /// <param name="path">Path to a <c>.ttf</c>/<c>.otf</c> file (TrueType-flavored outlines only — CFF is out of Phase 2 scope).</param>
    /// <param name="options">The options whose font resource limits guard parsing this (potentially untrusted) font file.</param>
    /// <exception cref="PlumePdfException">A coded <c>PLUME8xxx</c> failure parsing the font file — see <c>docs/errors/</c> for the specific codes.</exception>
    /// <example>
    /// <code>
    /// var strictFonts = PdfOptions.Default with { MaxFontFileBytes = 8L * 1024 * 1024 };
    /// PdfFont font = PdfFont.FromFile("untrusted.ttf", strictFonts);
    /// </code>
    /// </example>
    public static PdfFont FromFile(string path, PdfOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(options);
        return FromBytes(File.ReadAllBytes(path), options);
    }

    /// <summary>Parses a TrueType/OpenType font already loaded into memory under <see cref="PdfOptions.Default"/>'s font resource limits. See <see cref="FromFile(string)"/> for the file-based overload.</summary>
    /// <param name="fontBytes">The font file's raw bytes.</param>
    /// <exception cref="PlumePdfException">A coded <c>PLUME8xxx</c> failure parsing the font file — see <c>docs/errors/</c> for the specific codes.</exception>
    /// <example>
    /// <code>
    /// byte[] bytes = await File.ReadAllBytesAsync("fonts/NotoSans-Bold.ttf");
    /// PdfFont heading = PdfFont.FromBytes(bytes);
    /// </code>
    /// </example>
    public static PdfFont FromBytes(byte[] fontBytes) => FromBytes(fontBytes, PdfOptions.Default);

    /// <summary>Parses a TrueType/OpenType font already loaded into memory, under <paramref name="options"/>'s font resource limits. See <see cref="FromFile(string,PdfOptions)"/> for the file-based overload.</summary>
    /// <param name="fontBytes">The font file's raw bytes.</param>
    /// <param name="options">The options whose font resource limits guard parsing this (potentially untrusted) font file.</param>
    /// <exception cref="PlumePdfException">A coded <c>PLUME8xxx</c> failure parsing the font file — see <c>docs/errors/</c> for the specific codes.</exception>
    /// <example>
    /// <code>
    /// var strictFonts = PdfOptions.Default with { MaxFontFileBytes = 8L * 1024 * 1024 };
    /// byte[] bytes = await File.ReadAllBytesAsync("untrusted.ttf");
    /// PdfFont font = PdfFont.FromBytes(bytes, strictFonts);
    /// </code>
    /// </example>
    public static PdfFont FromBytes(byte[] fontBytes, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(fontBytes);
        ArgumentNullException.ThrowIfNull(options);
        var program = TrueTypeFontProgram.Parse(fontBytes, FontReadLimits.From(options));
        return new PdfFont(program.BaseFontName, program);
    }

    private static PdfFont FromStandard14(string standard14Name)
    {
        if (!Standard14Font.TryGet(standard14Name, out var font))
        {
            throw new PlumePdfException("PLUME8014", $"Internal font-table invariant violated: '{standard14Name}' is not a recognized Standard-14 font name.");
        }

        return new PdfFont(standard14Name, font);
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
