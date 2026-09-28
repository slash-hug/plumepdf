using System.Text;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Outlines;
using PlumePdf.Fonts.Reading;
using Xunit;

namespace PlumePdf.Tests.Fonts.Outlines;

/// <summary>
/// <see cref="Type1Parser"/>'s eexec/charstring decryption and Type 1 charstring
/// interpreter, tested against hand-built but byte-exact Type 1 font programs — same
/// "byte-exact, spec-verified, hand-crafted" convention <see cref="CffParserTests"/> and
/// <c>Shaping/TestFontBuilder.cs</c> already establish, since no Type 1/PFB fixture is
/// pinned in this repo's corpus.
/// </summary>
public class Type1ParserTests
{
    [Fact]
    public void RawBinaryEexec_HsbwRmovetoRlinetoClosepath_ProducesTriangle()
    {
        byte[] charstring =
        [
            .. Type1TestBuilder.Number(10), .. Type1TestBuilder.Number(10), 13, // 10 10 hsbw -> current = (10, 0)
            .. Type1TestBuilder.Number(100), .. Type1TestBuilder.Number(100), 21, // 100 100 rmoveto -> (110, 100)
            .. Type1TestBuilder.Number(200), .. Type1TestBuilder.Number(0), 5,     // 200 0 rlineto -> (310, 100)
            .. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(200), 5,     // 0 200 rlineto -> (310, 300)
            9,  // closepath
            14, // endchar
        ];

        var font = Type1TestBuilder.BuildFont(("A", charstring));
        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.True(parser.TryGetGlyphOutline("A", out var outline));
        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 110, 100),
            c => AssertLineTo(c, 310, 100),
            c => AssertLineTo(c, 310, 300),
            c => AssertLineTo(c, 110, 100), // implicit close back to the moveTo point
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void HexEncodedEexec_DecryptsIdenticallyToRawBinary()
    {
        byte[] charstring = [.. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(0), 13, .. Type1TestBuilder.Number(5), .. Type1TestBuilder.Number(5), 21, 14];

        var rawFont = Type1TestBuilder.BuildFont(hexEncode: false, ("A", charstring));
        var hexFont = Type1TestBuilder.BuildFont(hexEncode: true, ("A", charstring));

        var rawOutline = Type1Parser.Parse(rawFont, FontReadLimits.Default).TryGetGlyphOutline("A", out var o1) ? o1 : throw new InvalidOperationException();
        var hexOutline = Type1Parser.Parse(hexFont, FontReadLimits.Default).TryGetGlyphOutline("A", out var o2) ? o2 : throw new InvalidOperationException();

        Assert.Equal(rawOutline.Commands, hexOutline.Commands);
    }

    [Fact]
    public void PfbSegmented_UnwrapsAndDecrypts()
    {
        byte[] charstring = [.. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(0), 13, .. Type1TestBuilder.Number(7), .. Type1TestBuilder.Number(7), 21, 14];

        var plain = Type1TestBuilder.BuildFont(("A", charstring));
        var pfb = Type1TestBuilder.WrapAsPfb(plain);

        Assert.True(Type1Parser.Parse(pfb, FontReadLimits.Default).TryGetGlyphOutline("A", out var outline));
        Assert.Collection(outline.Commands, c => AssertMoveTo(c, 7, 7), c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void Callsubr_ExecutesSubroutineCharstring()
    {
        // Subr 0: "20 20 rmoveto return" (no bias adjustment in Type 1, unlike Type 2).
        byte[] subr0 = [.. Type1TestBuilder.Number(20), .. Type1TestBuilder.Number(20), 21, 11];
        byte[] charstring = [.. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(0), 13, .. Type1TestBuilder.Number(0), 10, 14]; // 0 callsubr; endchar

        var font = Type1TestBuilder.BuildFont([subr0], ("A", charstring));

        Assert.True(Type1Parser.Parse(font, FontReadLimits.Default).TryGetGlyphOutline("A", out var outline));
        Assert.Collection(outline.Commands, c => AssertMoveTo(c, 20, 20), c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void Div_ComputesFractionalDelta()
    {
        // "10 4 div 10 4 div rmoveto" -> rmoveto(2.5, 2.5).
        byte[] charstring =
        [
            .. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(0), 13,
            .. Type1TestBuilder.Number(10), .. Type1TestBuilder.Number(4), 12, 12, // div
            .. Type1TestBuilder.Number(10), .. Type1TestBuilder.Number(4), 12, 12, // div
            21, 14,
        ];

        var font = Type1TestBuilder.BuildFont(("A", charstring));
        Assert.True(Type1Parser.Parse(font, FontReadLimits.Default).TryGetGlyphOutline("A", out var outline));

        Assert.Collection(outline.Commands, c => AssertMoveTo(c, 2.5f, 2.5f), c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void FlexSequence_EmitsTwoCurvesFromSevenCollectedPoints()
    {
        // hsbw; an ordinary rmoveto opens the contour; then the standard Adobe Flex protocol
        // (OtherSubrs 1/2/0, App. A): flex-start, 7 rmoveto's each followed by the per-point
        // marker (othersubr 2), flex-end (othersubr 0) with its 3 summary args, then the
        // conventional 'pop pop setcurrentpoint' realignment.
        List<byte> cs = [];
        cs.AddRange(Type1TestBuilder.Number(0));
        cs.AddRange(Type1TestBuilder.Number(0));
        cs.Add(13); // hsbw

        cs.AddRange(Type1TestBuilder.Number(0));
        cs.AddRange(Type1TestBuilder.Number(0));
        cs.Add(21); // rmoveto -> opens the contour at (0,0)

        cs.AddRange(Type1TestBuilder.OtherSubrCall(othersubr: 1, args: [])); // flex start

        float[] deltas = [5, 5, 5, 5, 5, 5, 5]; // 7 points, each +(5,5) from the last.
        foreach (var _ in deltas)
        {
            cs.AddRange(Type1TestBuilder.Number(5));
            cs.AddRange(Type1TestBuilder.Number(5));
            cs.Add(21); // rmoveto (intercepted while in flex mode)
            cs.AddRange(Type1TestBuilder.OtherSubrCall(othersubr: 2, args: [])); // per-point marker
        }

        cs.AddRange(Type1TestBuilder.OtherSubrCall(othersubr: 0, args: [50, 35, 35])); // flex end: flexHeight, x, y
        cs.Add(12); cs.Add(17); // pop
        cs.Add(12); cs.Add(17); // pop
        cs.Add(12); cs.Add(33); // setcurrentpoint
        cs.Add(14); // endchar

        var font = Type1TestBuilder.BuildFont(("A", [.. cs]));
        Assert.True(Type1Parser.Parse(font, FontReadLimits.Default).TryGetGlyphOutline("A", out var outline));

        // p0 (5,5) is the ignored reference point; p1..p6 = (10,10)(15,15)(20,20)(25,25)(30,30)(35,35).
        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 0, 0),
            c => AssertCurveTo(c, 10, 10, 15, 15, 20, 20),
            c => AssertCurveTo(c, 25, 25, 30, 30, 35, 35),
            c => AssertLineTo(c, 0, 0), // implicit close
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void Seac_ComposesBaseAndAccentByStandardEncodingCode()
    {
        var baseCode = SimpleFontEncodings.StandardEncoding.ToList().FindIndex(e => e.GlyphName == "B");
        var accentCode = SimpleFontEncodings.StandardEncoding.ToList().FindIndex(e => e.GlyphName == "grave");
        Assert.True(baseCode >= 0, "StandardEncoding has no 'B' — test assumption invalid.");
        Assert.True(accentCode >= 0, "StandardEncoding has no 'grave' — test assumption invalid.");

        byte[] baseGlyph = [.. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(0), 13, .. Type1TestBuilder.Number(1), .. Type1TestBuilder.Number(1), 21, 14];
        byte[] accentGlyph = [.. Type1TestBuilder.Number(2), .. Type1TestBuilder.Number(0), 13, .. Type1TestBuilder.Number(3), .. Type1TestBuilder.Number(3), 21, 14];

        // asb=2 (the accent's own declared sidebearing) adx=10 ady=20 bchar achar seac.
        // dx = current-glyph-sbx(0) - asb(2) + adx(10) - accent-own-sbx(2) = 6.
        byte[] composite =
        [
            .. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(0), 13, // sbx=0
            .. Type1TestBuilder.Number(2), .. Type1TestBuilder.Number(10), .. Type1TestBuilder.Number(20),
            .. Type1TestBuilder.Number(baseCode), .. Type1TestBuilder.Number(accentCode),
            12, 6, // seac
        ];

        var font = Type1TestBuilder.BuildFont(("B", baseGlyph), ("grave", accentGlyph), ("Bgrave", composite));
        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.True(parser.TryGetGlyphOutline("Bgrave", out var outline));

        // Base glyph's own absolute point: hsbw(sbx=0) + rmoveto(1,1) -> (1,1), untranslated.
        // Accent's own absolute point: hsbw(sbx=2) + rmoveto(3,3) -> (5,3); translated by
        // (dx = compositeSbx(0) - asb(2) + adx(10) - accentSbx(2) = 6, dy = ady(20)) -> (11,23).
        Assert.Collection(
            outline.Commands,
            c => AssertMoveTo(c, 1, 1),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind),
            c => AssertMoveTo(c, 11, 23),
            c => Assert.Equal(GlyphPathCommandKind.ClosePath, c.Kind));
    }

    [Fact]
    public void UnknownGlyphName_ReturnsFalse()
    {
        var font = Type1TestBuilder.BuildFont(("A", [14]));
        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.False(parser.TryGetGlyphOutline("NotAGlyph", out var outline));
        Assert.Same(GlyphOutline.Empty, outline);
    }

    [Fact]
    public void NoEexecSection_ThrowsPlume8028()
    {
        var bytes = Encoding.ASCII.GetBytes("%!PS-AdobeFont-1.0\nnot a real Type 1 program\n");

        var ex = Assert.Throws<PlumePdfException>(() => Type1Parser.Parse(bytes, FontReadLimits.Default));
        Assert.Equal("PLUME8028", ex.Code);
    }

    // ---- BuiltInEncoding (T-A2.1: new cleartext /Encoding scan) ----

    [Fact]
    public void BuiltInEncoding_StopsAtTheDefinitionsTerminator_IgnoresLaterCodeNamePairs()
    {
        // The entry loop used to tokenize the whole remaining cleartext
        // header, so any later "<int> /<name> put"-shaped pair — an unrelated dictionary entry —
        // was absorbed as an encoding entry. The array's own "readonly def" must end the scan.
        var font = Type1TestBuilder.BuildFont(
            encoding: "/Encoding 256 array\ndup 65 /A put\nreadonly def\n/Weight 1 /junk put\n",
            ("A", [.. Type1TestBuilder.Number(0), .. Type1TestBuilder.Number(0), 13, 14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("A", parser.BuiltInEncoding![0x41].GlyphName);
        Assert.Equal(string.Empty, parser.BuiltInEncoding[1].GlyphName); // "1 /junk put" after the def is NOT an entry.
    }

    [Fact]
    public void BuiltInEncoding_StandardEncodingDef_ClonesStandardEncoding()
    {
        var font = Type1TestBuilder.BuildFont("/Encoding StandardEncoding def\n", ("A", [14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("A", parser.BuiltInEncoding![0x41].GlyphName);
        Assert.Equal(SimpleFontEncodings.StandardEncoding, parser.BuiltInEncoding);
        Assert.NotSame(SimpleFontEncodings.StandardEncoding, parser.BuiltInEncoding); // Cloned, not aliased.
    }

    [Fact]
    public void BuiltInEncoding_CustomDupPutTable_ResolvesGlyphNameAndUnicode()
    {
        var font = Type1TestBuilder.BuildFont("/Encoding 256 array\ndup 65 /alpha put\nreadonly def\n", ("A", [14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("alpha", parser.BuiltInEncoding![65].GlyphName);

        // The Unicode column follows SimpleFontEncodings' own convention for this glyph name
        // (Symbol/ZapfDingbats names resolve through the shared PUA-mapped table — see
        // SimpleFontEncodings.TryGlyphNameToUnicode), not a Greek-script assumption.
        Assert.True(SimpleFontEncodings.TryGlyphNameToUnicode("alpha", out var expectedUnicode));
        Assert.Equal(expectedUnicode, parser.BuiltInEncoding[65].Unicode);
    }

    [Fact]
    public void BuiltInEncoding_MalformedDupEntries_AreSkippedButRestIsIntact()
    {
        var font = Type1TestBuilder.BuildFont(
            "/Encoding 256 array\ndup 300 /x put\ndup foo /y put\ndup 66 /B put\nreadonly def\n",
            ("A", [14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        var table = parser.BuiltInEncoding!;
        Assert.Equal(256, table.Length);

        // The well-formed entry survives despite the two malformed ones around it.
        Assert.Equal(("B", 66), table[66]);

        // Untouched codes default to (string.Empty, -1) — including 300's would-be target and
        // any code the malformed entries could have landed on had they not been skipped.
        Assert.Equal((string.Empty, -1), table[65]);
        Assert.Equal((string.Empty, -1), table[0]);
    }

    [Fact]
    public void BuiltInEncoding_NoEncodingInCleartext_IsNull()
    {
        var font = Type1TestBuilder.BuildFont(("A", [14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.Null(parser.BuiltInEncoding);
    }

    [Fact]
    public void BuiltInEncoding_ArrayDefThenEncodingPut_ResolvesGlyphName()
    {
        // The standard-idiom PostScript alternative to "dup <code> /<name> put": the array is
        // def'd empty and filled with .notdef by a for-loop, then real entries are assigned by
        // referencing the /Encoding variable directly rather than via "dup" (finding: Type1Parser.cs
        // review — this idiom must not degrade to a blank, all-(Empty,-1) 256-entry table).
        var font = Type1TestBuilder.BuildFont(
            "/Encoding 256 array def\n0 1 255 {Encoding exch /.notdef put} for\nEncoding 65 /A put\nreadonly def\n",
            ("A", [14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("A", parser.BuiltInEncoding![65].GlyphName);
        Assert.Equal((string.Empty, -1), parser.BuiltInEncoding[66]); // Untouched codes stay blank.
    }

    [Fact]
    public void BuiltInEncoding_CodeGluedToName_ResolvesGlyphName()
    {
        // No whitespace between the code and the name ("65/A" as a single token) — a producer
        // idiom the whitespace-only tokenizer previously dropped the entire entry over (finding:
        // Type1Parser.cs review).
        var font = Type1TestBuilder.BuildFont("/Encoding 256 array\ndup 65/A put\nreadonly def\n", ("A", [14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.NotNull(parser.BuiltInEncoding);
        Assert.Equal("A", parser.BuiltInEncoding![65].GlyphName);
    }

    [Fact]
    public void BuiltInEncoding_ArrayDeclaredButNoEntriesParsed_IsNull()
    {
        // An explicit array with zero recognizable "<code> /<name> put" entries must not
        // masquerade as a real (but entirely blank) built-in encoding — EncodingResolver treats a
        // non-null builtInBase as authoritative/default, so a hollow table would silently blank
        // the font's text instead of falling back to StandardEncoding (finding: Type1Parser.cs
        // review).
        var font = Type1TestBuilder.BuildFont("/Encoding 256 array def\nreadonly def\n", ("A", [14]));

        var parser = Type1Parser.Parse(font, FontReadLimits.Default);

        Assert.Null(parser.BuiltInEncoding);
    }

    private static void AssertMoveTo(GlyphPathCommand c, float x, float y)
    {
        Assert.Equal(GlyphPathCommandKind.MoveTo, c.Kind);
        Assert.Equal(x, c.X);
        Assert.Equal(y, c.Y);
    }

    private static void AssertLineTo(GlyphPathCommand c, float x, float y)
    {
        Assert.Equal(GlyphPathCommandKind.LineTo, c.Kind);
        Assert.Equal(x, c.X);
        Assert.Equal(y, c.Y);
    }

    private static void AssertCurveTo(GlyphPathCommand c, float x1, float y1, float x2, float y2, float x, float y)
    {
        Assert.Equal(GlyphPathCommandKind.CurveTo, c.Kind);
        Assert.Equal(x1, c.X1);
        Assert.Equal(y1, c.Y1);
        Assert.Equal(x2, c.X2);
        Assert.Equal(y2, c.Y2);
        Assert.Equal(x, c.X);
        Assert.Equal(y, c.Y);
    }
}

/// <summary>
/// Builds byte-exact Type 1 font programs (Adobe Type 1 Font Format) for
/// <see cref="Type1ParserTests"/>: cleartext header + <c>eexec</c> + the eexec-encrypted
/// private portion (<c>/Subrs</c>, <c>/CharStrings</c> with per-glyph charstring-encrypted
/// entries) — every encryption step uses the exact Adobe TN cipher (Ch. 7), the inverse of
/// <see cref="Type1Parser"/>'s own decrypt, so a round trip through the real parser is a
/// genuine field-by-field check, not a self-consistent tautology.
/// </summary>
internal static class Type1TestBuilder
{
    public static byte[] BuildFont(params (string Name, byte[] Charstring)[] glyphs) => BuildFont(subrs: [], hexEncode: false, encoding: null, glyphs);

    public static byte[] BuildFont(bool hexEncode, params (string Name, byte[] Charstring)[] glyphs) => BuildFont(subrs: [], hexEncode, encoding: null, glyphs);

    public static byte[] BuildFont(IReadOnlyList<byte[]> subrs, params (string Name, byte[] Charstring)[] glyphs) => BuildFont(subrs, hexEncode: false, encoding: null, glyphs);

    public static byte[] BuildFont(IReadOnlyList<byte[]> subrs, bool hexEncode, params (string Name, byte[] Charstring)[] glyphs) => BuildFont(subrs, hexEncode, encoding: null, glyphs);

    /// <summary>Builds a font whose cleartext header carries the given <c>/Encoding …</c> text (verbatim, including its own trailing newline) immediately before <c>eexec</c> — for <see cref="Type1ParserTests"/>'s <c>BuiltInEncoding</c> cases.</summary>
    public static byte[] BuildFont(string? encoding, params (string Name, byte[] Charstring)[] glyphs) => BuildFont(subrs: [], hexEncode: false, encoding, glyphs);

    public static byte[] BuildFont(IReadOnlyList<byte[]> subrs, bool hexEncode, string? encoding, params (string Name, byte[] Charstring)[] glyphs)
    {
        List<byte> priv = [];
        AppendAscii(priv, "/lenIV 4 def\n");

        if (subrs.Count > 0)
        {
            AppendAscii(priv, $"/Subrs {subrs.Count} array\n");
            for (var i = 0; i < subrs.Count; i++)
            {
                var cipher = EncryptCharstring(subrs[i]);
                AppendAscii(priv, $"dup {i} {cipher.Length} RD ");
                priv.AddRange(cipher);
                AppendAscii(priv, " NP\n");
            }
        }

        AppendAscii(priv, $"/CharStrings {glyphs.Length} dict dup begin\n");
        foreach (var (name, charstring) in glyphs)
        {
            var cipher = EncryptCharstring(charstring);
            AppendAscii(priv, $"/{name} {cipher.Length} RD ");
            priv.AddRange(cipher);
            AppendAscii(priv, " ND\n");
        }

        AppendAscii(priv, "end\n");

        var eexecCipher = Encrypt([.. priv], initialR: 55665, discard: 4);

        List<byte> font = [];
        AppendAscii(font, "%!PS-AdobeFont-1.0: Test 001.000\n");
        if (encoding is not null)
        {
            AppendAscii(font, encoding);
        }

        AppendAscii(font, "eexec\n");
        if (hexEncode)
        {
            AppendAscii(font, ToHex(eexecCipher));
        }
        else
        {
            font.AddRange(eexecCipher);
        }

        AppendAscii(font, "\n0000000000000000000000000000000000000000000000000000000000000000\ncleartomark\n");
        return [.. font];
    }

    /// <summary>Wraps an already-built PFA-style byte stream into PFB segments (a single ASCII cleartext segment up to 'eexec\n', one binary segment for the rest, and an EOF marker) — <see cref="Type1Parser"/>'s <c>UnwrapPfb</c> must reconstruct the original stream from this.</summary>
    public static byte[] WrapAsPfb(byte[] plain)
    {
        var eexecEnd = IndexOf(plain, Encoding.ASCII.GetBytes("eexec\n")) + "eexec\n".Length;
        var cleartext = plain[..eexecEnd];
        var binary = plain[eexecEnd..];

        List<byte> pfb = [];
        AppendSegment(pfb, 1, cleartext);
        AppendSegment(pfb, 2, binary);
        pfb.Add(0x80);
        pfb.Add(3);
        return [.. pfb];
    }

    private static void AppendSegment(List<byte> buf, byte type, byte[] data)
    {
        buf.Add(0x80);
        buf.Add(type);
        buf.Add((byte)data.Length);
        buf.Add((byte)(data.Length >> 8));
        buf.Add((byte)(data.Length >> 16));
        buf.Add((byte)(data.Length >> 24));
        buf.AddRange(data);
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i <= data.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Emits a charstring-level <c>callothersubr</c> invocation: pushes each arg, then the arg count, then the OtherSubr number, per the stack convention <see cref="Type1Parser"/>'s interpreter expects.</summary>
    public static byte[] OtherSubrCall(int othersubr, int[] args)
    {
        List<byte> bytes = [];
        foreach (var a in args)
        {
            bytes.AddRange(Number(a));
        }

        bytes.AddRange(Number(args.Length));
        bytes.AddRange(Number(othersubr));
        bytes.Add(12);
        bytes.Add(16);
        return [.. bytes];
    }

    /// <summary>Type 1 charstring number encoding (Adobe Type 1 Font Format Ch. 8) — the same 32-254 scheme as CFF/Type 2, minus the <c>28</c> int16 form (Type 1 has no such opcode).</summary>
    public static byte[] Number(int v) => v switch
    {
        >= -107 and <= 107 => [(byte)(v + 139)],
        >= 108 and <= 1131 => [(byte)(((v - 108) / 256) + 247), (byte)((v - 108) % 256)],
        >= -1131 and <= -108 => [(byte)(((-v - 108) / 256) + 251), (byte)((-v - 108) % 256)],
        _ => [255, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v],
    };

    private static byte[] EncryptCharstring(byte[] plain) => Encrypt(plain, initialR: 4330, discard: 4);

    /// <summary>The inverse of <see cref="Type1Parser"/>'s own decrypt (Adobe TN Ch. 7 cipher): prepends <paramref name="discard"/> zero padding bytes, then runs the same rolling stream cipher forward.</summary>
    private static byte[] Encrypt(byte[] plain, ushort initialR, int discard)
    {
        const ushort c1 = 52845;
        const ushort c2 = 22719;
        var padded = new byte[discard + plain.Length];
        Array.Copy(plain, 0, padded, discard, plain.Length);

        var r = initialR;
        var cipher = new byte[padded.Length];
        for (var i = 0; i < padded.Length; i++)
        {
            var c = (byte)(padded[i] ^ (r >> 8));
            cipher[i] = c;
            r = (ushort)(((c + r) * c1) + c2);
        }

        return cipher;
    }

    private static void AppendAscii(List<byte> buf, string s) => buf.AddRange(Encoding.ASCII.GetBytes(s));

    private static string ToHex(byte[] data) => Convert.ToHexString(data);
}
