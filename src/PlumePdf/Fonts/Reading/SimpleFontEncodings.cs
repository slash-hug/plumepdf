using System.Globalization;
using PlumePdf.Fonts.Standard14;

namespace PlumePdf.Fonts.Reading;

/// <summary>
/// The named built-in encodings a simple font's <c>/Encoding</c> can select
/// (ISO 32000-1 §9.6.6, Annex D): <see cref="StandardEncoding"/> (Adobe's original PostScript
/// encoding), <see cref="MacRomanEncoding"/>, <see cref="WinAnsiEncoding"/>, and the two
/// symbol fonts' own built-in encodings. Each is a 256-entry (glyph name, Unicode codepoint)
/// table indexed by byte code; an unassigned code is <c>("", -1)</c>. <see cref="WinAnsiEncoding"/>,
/// <see cref="SymbolEncoding"/>, and <see cref="ZapfDingbatsEncoding"/> are the exact tables
/// already source-generated for the write path (<see cref="Standard14Encodings"/>, from Adobe's
/// redistributable Core 14 AFM files — see <c>NOTICE</c> and <c>docs/spec-sources.md</c>) rather
/// than duplicated; <see cref="StandardEncoding"/> and <see cref="MacRomanEncoding"/> are
/// transcribed here from the same source this project already relies on for WinAnsi: ISO
/// 32000-1 Annex D.2's code/glyph-name/Unicode data table — data, not spec prose, matching the
/// precedent <c>Standard14Encodings.g.cs</c>'s own header comment already establishes (the
/// clean-room policy in AGENTS.md is not implicated by transcribing a data table of facts).
/// </summary>
internal static class SimpleFontEncodings
{
    // Codes 32-126 are shared PDF/ASCII Latin glyph names across Standard/MacRoman/WinAnsi,
    // except position 39 ("'"): StandardEncoding uses the typographic "quoteright"/"quoteleft"
    // pair (Unicode U+2019/U+2018) at 39/96, while MacRoman (like WinAnsi) uses the straight
    // "quotesingle"/"grave" pair (Unicode U+0027/U+0060) there. Declared first (ahead of every
    // field below that transitively calls BuildAsciiBase through its own initializer) because
    // C# runs static field initializers in textual declaration order — a field referencing
    // this one from a later line would otherwise read it as still-null.
    private static readonly string[] AsciiGlyphNames =
    [
        "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "APOS",
        "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash",
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "colon", "semicolon", "less", "equal", "greater", "question", "at",
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
        "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "GRAVE",
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
        "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
        "braceleft", "bar", "braceright", "asciitilde",
    ];

    /// <summary>ISO 32000-1 Annex D.2 WinAnsiEncoding — the same table the write path uses.</summary>
    public static readonly (string GlyphName, int Unicode)[] WinAnsiEncoding = Standard14Encodings.WinAnsiEncoding;

    /// <summary>Symbol's built-in encoding — the same table the write path uses.</summary>
    public static readonly (string GlyphName, int Unicode)[] SymbolEncoding = Standard14Encodings.SymbolEncoding;

    /// <summary>ZapfDingbats's built-in encoding — the same table the write path uses.</summary>
    public static readonly (string GlyphName, int Unicode)[] ZapfDingbatsEncoding = Standard14Encodings.ZapfDingbatsEncoding;

    /// <summary>ISO 32000-1 Annex D.2 StandardEncoding (Adobe's original PostScript text-font encoding).</summary>
    public static readonly (string GlyphName, int Unicode)[] StandardEncoding = BuildStandardEncoding();

    /// <summary>ISO 32000-1 Annex D.2 MacRomanEncoding (Mac OS Roman).</summary>
    public static readonly (string GlyphName, int Unicode)[] MacRomanEncoding = BuildMacRomanEncoding();

    // Built once, after the four tables above, from every glyph name any of them declares —
    // the same glyph name maps to the same Unicode codepoint across all of PlumePDF's built-in
    // tables, so a /Differences entry naming a glyph that happens to live in a different base
    // table than the font's own still resolves correctly.
    private static readonly Dictionary<string, int> GlyphNameToUnicode = BuildGlyphNameIndex();

    /// <summary>
    /// Resolves a PDF glyph name (as it appears in <c>/Differences</c>, or as a base encoding's
    /// own table entry) to a Unicode codepoint: first via the combined table above, then via
    /// the Adobe Glyph List's algorithmic <c>uniXXXX</c>/<c>uXXXXXXX</c> naming convention
    /// (4 hex digits after <c>uni</c>, or 4-6 after <c>u</c>). <see langword="false"/> when
    /// neither resolves the name.
    /// </summary>
    public static bool TryGlyphNameToUnicode(string glyphName, out int unicode)
    {
        if (string.IsNullOrEmpty(glyphName))
        {
            unicode = -1;
            return false;
        }

        if (GlyphNameToUnicode.TryGetValue(glyphName, out unicode))
        {
            return true;
        }

        if (glyphName.Length == 7 && glyphName.StartsWith("uni", StringComparison.Ordinal)
            && TryParseHex(glyphName.AsSpan(3), out unicode))
        {
            return true;
        }

        if (glyphName.Length is >= 5 and <= 7 && glyphName[0] == 'u'
            && TryParseHex(glyphName.AsSpan(1), out unicode))
        {
            return true;
        }

        unicode = -1;
        return false;
    }

    private static bool TryParseHex(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    private static Dictionary<string, int> BuildGlyphNameIndex()
    {
        var map = new Dictionary<string, int>(512, StringComparer.Ordinal);
        AddTable(map, StandardEncoding);
        AddTable(map, MacRomanEncoding);
        AddTable(map, WinAnsiEncoding);
        AddTable(map, SymbolEncoding);
        AddTable(map, ZapfDingbatsEncoding);
        return map;

        static void AddTable(Dictionary<string, int> map, (string GlyphName, int Unicode)[] table)
        {
            foreach (var (glyphName, unicode) in table)
            {
                if (glyphName.Length > 0 && unicode >= 0)
                {
                    map.TryAdd(glyphName, unicode);
                }
            }
        }
    }

    private static (string GlyphName, int Unicode)[] BuildAsciiBase(bool typewriterQuotes)
    {
        var table = new (string, int)[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = ("", -1);
        }

        for (var i = 0; i < AsciiGlyphNames.Length; i++)
        {
            var code = 32 + i;
            var name = AsciiGlyphNames[i];
            table[code] = name switch
            {
                "APOS" => typewriterQuotes ? ("quotesingle", 0x0027) : ("quoteright", 0x2019),
                "GRAVE" => typewriterQuotes ? ("grave", 0x0060) : ("quoteleft", 0x2018),
                _ => (name, code),
            };
        }

        return table;
    }

    private static (string GlyphName, int Unicode)[] BuildStandardEncoding()
    {
        var table = BuildAsciiBase(typewriterQuotes: false);

        // ISO 32000-1 Annex D.2, StandardEncoding column, codes 161-251 (128-160 and every
        // other code in 128-255 not listed here are unassigned in StandardEncoding).
        Set(table, 161, "exclamdown", 0x00A1);
        Set(table, 162, "cent", 0x00A2);
        Set(table, 163, "sterling", 0x00A3);
        Set(table, 164, "fraction", 0x2044);
        Set(table, 165, "yen", 0x00A5);
        Set(table, 166, "florin", 0x0192);
        Set(table, 167, "section", 0x00A7);
        Set(table, 168, "currency", 0x00A4);
        Set(table, 169, "quotesingle", 0x0027);
        Set(table, 170, "quotedblleft", 0x201C);
        Set(table, 171, "guillemotleft", 0x00AB);
        Set(table, 172, "guilsinglleft", 0x2039);
        Set(table, 173, "guilsinglright", 0x203A);
        Set(table, 174, "fi", 0xFB01);
        Set(table, 175, "fl", 0xFB02);
        Set(table, 177, "endash", 0x2013);
        Set(table, 178, "dagger", 0x2020);
        Set(table, 179, "daggerdbl", 0x2021);
        Set(table, 180, "periodcentered", 0x00B7);
        Set(table, 182, "paragraph", 0x00B6);
        Set(table, 183, "bullet", 0x2022);
        Set(table, 184, "quotesinglbase", 0x201A);
        Set(table, 185, "quotedblbase", 0x201E);
        Set(table, 186, "quotedblright", 0x201D);
        Set(table, 187, "guillemotright", 0x00BB);
        Set(table, 188, "ellipsis", 0x2026);
        Set(table, 189, "perthousand", 0x2030);
        Set(table, 191, "questiondown", 0x00BF);
        Set(table, 193, "grave", 0x0060);
        Set(table, 194, "acute", 0x00B4);
        Set(table, 195, "circumflex", 0x02C6);
        Set(table, 196, "tilde", 0x02DC);
        Set(table, 197, "macron", 0x00AF);
        Set(table, 198, "breve", 0x02D8);
        Set(table, 199, "dotaccent", 0x02D9);
        Set(table, 200, "dieresis", 0x00A8);
        Set(table, 202, "ring", 0x02DA);
        Set(table, 203, "cedilla", 0x00B8);
        Set(table, 205, "hungarumlaut", 0x02DD);
        Set(table, 206, "ogonek", 0x02DB);
        Set(table, 207, "caron", 0x02C7);
        Set(table, 208, "emdash", 0x2014);
        Set(table, 225, "AE", 0x00C6);
        Set(table, 227, "ordfeminine", 0x00AA);
        Set(table, 232, "Lslash", 0x0141);
        Set(table, 233, "Oslash", 0x00D8);
        Set(table, 234, "OE", 0x0152);
        Set(table, 235, "ordmasculine", 0x00BA);
        Set(table, 241, "ae", 0x00E6);
        Set(table, 245, "dotlessi", 0x0131);
        Set(table, 248, "lslash", 0x0142);
        Set(table, 249, "oslash", 0x00F8);
        Set(table, 250, "oe", 0x0153);
        Set(table, 251, "germandbls", 0x00DF);

        return table;
    }

    private static (string GlyphName, int Unicode)[] BuildMacRomanEncoding()
    {
        var table = BuildAsciiBase(typewriterQuotes: true);

        // ISO 32000-1 Annex D.2, MacRomanEncoding column, codes 128-255 (Mac OS Roman).
        Set(table, 128, "Adieresis", 0x00C4);
        Set(table, 129, "Aring", 0x00C5);
        Set(table, 130, "Ccedilla", 0x00C7);
        Set(table, 131, "Eacute", 0x00C9);
        Set(table, 132, "Ntilde", 0x00D1);
        Set(table, 133, "Odieresis", 0x00D6);
        Set(table, 134, "Udieresis", 0x00DC);
        Set(table, 135, "aacute", 0x00E1);
        Set(table, 136, "agrave", 0x00E0);
        Set(table, 137, "acircumflex", 0x00E2);
        Set(table, 138, "adieresis", 0x00E4);
        Set(table, 139, "atilde", 0x00E3);
        Set(table, 140, "aring", 0x00E5);
        Set(table, 141, "ccedilla", 0x00E7);
        Set(table, 142, "eacute", 0x00E9);
        Set(table, 143, "egrave", 0x00E8);
        Set(table, 144, "ecircumflex", 0x00EA);
        Set(table, 145, "edieresis", 0x00EB);
        Set(table, 146, "iacute", 0x00ED);
        Set(table, 147, "igrave", 0x00EC);
        Set(table, 148, "icircumflex", 0x00EE);
        Set(table, 149, "idieresis", 0x00EF);
        Set(table, 150, "ntilde", 0x00F1);
        Set(table, 151, "oacute", 0x00F3);
        Set(table, 152, "ograve", 0x00F2);
        Set(table, 153, "ocircumflex", 0x00F4);
        Set(table, 154, "odieresis", 0x00F6);
        Set(table, 155, "otilde", 0x00F5);
        Set(table, 156, "uacute", 0x00FA);
        Set(table, 157, "ugrave", 0x00F9);
        Set(table, 158, "ucircumflex", 0x00FB);
        Set(table, 159, "udieresis", 0x00FC);
        Set(table, 160, "dagger", 0x2020);
        Set(table, 161, "degree", 0x00B0);
        Set(table, 162, "cent", 0x00A2);
        Set(table, 163, "sterling", 0x00A3);
        Set(table, 164, "section", 0x00A7);
        Set(table, 165, "bullet", 0x2022);
        Set(table, 166, "paragraph", 0x00B6);
        Set(table, 167, "germandbls", 0x00DF);
        Set(table, 168, "registered", 0x00AE);
        Set(table, 169, "copyright", 0x00A9);
        Set(table, 170, "trademark", 0x2122);
        Set(table, 171, "acute", 0x00B4);
        Set(table, 172, "dieresis", 0x00A8);
        Set(table, 173, "notequal", 0x2260);
        Set(table, 174, "AE", 0x00C6);
        Set(table, 175, "Oslash", 0x00D8);
        Set(table, 176, "infinity", 0x221E);
        Set(table, 177, "plusminus", 0x00B1);
        Set(table, 178, "lessequal", 0x2264);
        Set(table, 179, "greaterequal", 0x2265);
        Set(table, 180, "yen", 0x00A5);
        Set(table, 181, "mu", 0x00B5);
        Set(table, 182, "partialdiff", 0x2202);
        Set(table, 183, "summation", 0x2211);
        Set(table, 184, "product", 0x220F);
        Set(table, 185, "pi", 0x03C0);
        Set(table, 186, "integral", 0x222B);
        Set(table, 187, "ordfeminine", 0x00AA);
        Set(table, 188, "ordmasculine", 0x00BA);
        Set(table, 189, "Omega", 0x03A9);
        Set(table, 190, "ae", 0x00E6);
        Set(table, 191, "oslash", 0x00F8);
        Set(table, 192, "questiondown", 0x00BF);
        Set(table, 193, "exclamdown", 0x00A1);
        Set(table, 194, "logicalnot", 0x00AC);
        Set(table, 195, "radical", 0x221A);
        Set(table, 196, "florin", 0x0192);
        Set(table, 197, "approxequal", 0x2248);
        Set(table, 198, "Delta", 0x2206);
        Set(table, 199, "guillemotleft", 0x00AB);
        Set(table, 200, "guillemotright", 0x00BB);
        Set(table, 201, "ellipsis", 0x2026);
        Set(table, 202, "space", 0x00A0);
        Set(table, 203, "Agrave", 0x00C0);
        Set(table, 204, "Atilde", 0x00C3);
        Set(table, 205, "Otilde", 0x00D5);
        Set(table, 206, "OE", 0x0152);
        Set(table, 207, "oe", 0x0153);
        Set(table, 208, "endash", 0x2013);
        Set(table, 209, "emdash", 0x2014);
        Set(table, 210, "quotedblleft", 0x201C);
        Set(table, 211, "quotedblright", 0x201D);
        Set(table, 212, "quoteleft", 0x2018);
        Set(table, 213, "quoteright", 0x2019);
        Set(table, 214, "divide", 0x00F7);
        Set(table, 215, "lozenge", 0x25CA);
        Set(table, 216, "ydieresis", 0x00FF);
        Set(table, 217, "Ydieresis", 0x0178);
        Set(table, 218, "fraction", 0x2044);
        Set(table, 219, "currency", 0x00A4);
        Set(table, 220, "guilsinglleft", 0x2039);
        Set(table, 221, "guilsinglright", 0x203A);
        Set(table, 222, "fi", 0xFB01);
        Set(table, 223, "fl", 0xFB02);
        Set(table, 224, "daggerdbl", 0x2021);
        Set(table, 225, "periodcentered", 0x00B7);
        Set(table, 226, "quotesinglbase", 0x201A);
        Set(table, 227, "quotedblbase", 0x201E);
        Set(table, 228, "perthousand", 0x2030);
        Set(table, 229, "Acircumflex", 0x00C2);
        Set(table, 230, "Ecircumflex", 0x00CA);
        Set(table, 231, "Aacute", 0x00C1);
        Set(table, 232, "Edieresis", 0x00CB);
        Set(table, 233, "Egrave", 0x00C8);
        Set(table, 234, "Iacute", 0x00CD);
        Set(table, 235, "Icircumflex", 0x00CE);
        Set(table, 236, "Idieresis", 0x00CF);
        Set(table, 237, "Igrave", 0x00CC);
        Set(table, 238, "Oacute", 0x00D3);
        Set(table, 239, "Ocircumflex", 0x00D4);
        Set(table, 240, "apple", 0xF8FF);
        Set(table, 241, "Ograve", 0x00D2);
        Set(table, 242, "Uacute", 0x00DA);
        Set(table, 243, "Ucircumflex", 0x00DB);
        Set(table, 244, "Ugrave", 0x00D9);
        Set(table, 245, "dotlessi", 0x0131);
        Set(table, 246, "circumflex", 0x02C6);
        Set(table, 247, "tilde", 0x02DC);
        Set(table, 248, "macron", 0x00AF);
        Set(table, 249, "breve", 0x02D8);
        Set(table, 250, "dotaccent", 0x02D9);
        Set(table, 251, "ring", 0x02DA);
        Set(table, 252, "cedilla", 0x00B8);
        Set(table, 253, "hungarumlaut", 0x02DD);
        Set(table, 254, "ogonek", 0x02DB);
        Set(table, 255, "caron", 0x02C7);

        return table;
    }

    private static void Set((string GlyphName, int Unicode)[] table, int code, string glyphName, int unicode) =>
        table[code] = (glyphName, unicode);
}
