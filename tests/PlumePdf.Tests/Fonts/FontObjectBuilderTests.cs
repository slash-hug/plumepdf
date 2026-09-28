using PlumePdf.Fonts;
using PlumePdf.Fonts.Standard14;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Fonts;

public class FontObjectBuilderTests
{
    private static Func<PdfObject, IndirectReference> MakeAllocator(Dictionary<int, PdfObject> objects)
    {
        var next = 1;
        return obj =>
        {
            var number = next++;
            objects[number] = obj;
            return new IndirectReference(number, 0);
        };
    }

    [Fact]
    public void Standard14_BuildsAType1FontDictionary()
    {
        Assert.True(Standard14Font.TryGet("Helvetica", out var font));
        var objects = new Dictionary<int, PdfObject>();

        var reference = FontObjectBuilder.BuildStandard14(font, MakeAllocator(objects));
        var dict = Assert.IsType<PdfDictionary>(objects[reference.Number]);

        Assert.Equal("Font", ((PdfName)dict[PdfName.Type]).Value);
        Assert.Equal("Type1", ((PdfName)dict[PdfName.Subtype]).Value);
        Assert.Equal("Helvetica", ((PdfName)dict[PdfName.Get("BaseFont")]).Value);
        Assert.Equal("WinAnsiEncoding", ((PdfName)dict[PdfName.Get("Encoding")]).Value);

        var widths = Assert.IsType<PdfArray>(dict[PdfName.Get("Widths")]);
        Assert.Equal(224, widths.Count); // codes 32..255 inclusive.
        Assert.Equal(278, (int)((PdfNumber)widths[0]).Value); // code 32 = space.
    }

    [Fact]
    public void Standard14_Symbol_OmitsEncoding()
    {
        Assert.True(Standard14Font.TryGet("Symbol", out var font));
        var objects = new Dictionary<int, PdfObject>();

        var reference = FontObjectBuilder.BuildStandard14(font, MakeAllocator(objects));
        var dict = (PdfDictionary)objects[reference.Number];

        Assert.False(dict.ContainsKey(PdfName.Get("Encoding")), "A symbolic font's built-in encoding must not be overridden.");
    }

    [Fact]
    public void EmbeddedTrueType_BuildsType0CompositeFontGraph()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));
        Assert.True(font.TryGetGlyphId('B', out var glyphB));

        var objects = new Dictionary<int, PdfObject>();
        var codepointMap = new Dictionary<int, int> { ['A'] = glyphA, ['B'] = glyphB };

        var result = FontObjectBuilder.BuildEmbeddedTrueType(font, new HashSet<int> { glyphA, glyphB }, codepointMap, MakeAllocator(objects), PdfFilterRegistry.Default, PdfOptions.Default);

        var type0 = (PdfDictionary)objects[result.FontDictionaryReference.Number];
        Assert.Equal("Type0", ((PdfName)type0[PdfName.Subtype]).Value);
        Assert.Equal("Identity-H", ((PdfName)type0[PdfName.Get("Encoding")]).Value);
        Assert.StartsWith(result.Tag + "+", ((PdfName)type0[PdfName.Get("BaseFont")]).Value, StringComparison.Ordinal);

        var descendants = (PdfArray)type0[PdfName.Get("DescendantFonts")];
        var cidFontRef = (PdfReference)descendants[0];
        var cidFont = (PdfDictionary)objects[cidFontRef.Target.Number];
        Assert.Equal("CIDFontType2", ((PdfName)cidFont[PdfName.Subtype]).Value);
        Assert.Equal("Identity", ((PdfName)cidFont[PdfName.Get("CIDToGIDMap")]).Value);

        var descriptorRef = (PdfReference)cidFont[PdfName.Get("FontDescriptor")];
        var descriptor = (PdfDictionary)objects[descriptorRef.Target.Number];
        var fontFileRef = (PdfReference)descriptor[PdfName.Get("FontFile2")];
        var fontFileStream = (PdfStream)objects[fontFileRef.Target.Number];

        // /FontFile2 is Flate-encoded; /Length1 must carry the UNCOMPRESSED length.
        Assert.Equal("FlateDecode", ((PdfName)fontFileStream.Dictionary[PdfName.Filter]).Value);
        var decodedFontBytes = fontFileStream.GetDecodedBytes(PdfFilterRegistry.Default);
        Assert.Equal(((PdfNumber)fontFileStream.Dictionary[PdfName.Get("Length1")]).ToInt64(), decodedFontBytes.LongLength);

        // The embedded subset must itself be a valid, re-parseable SFNT file.
        var reparsed = TrueTypeFontProgram.Parse(decodedFontBytes);
        Assert.True(reparsed.Maxp.NumGlyphs >= 3);
    }

    [Fact]
    public void EmbeddedTrueType_WritesCidSetCoveringNotdefAndEveryRetainedGlyph()
    {
        // ISO 32000-1 §9.8.3 Table 124: /CIDSet is one bit per CID, set when that CID's glyph
        // is embedded — PDF/A-1 requires it for a subset CIDFont (veraPDF clause 6.3.5), and
        // FontObjectBuilder writes it unconditionally. This is the repo's only assertion that
        // the stream actually exists and its bits actually match the subset.
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));
        Assert.True(font.TryGetGlyphId('B', out var glyphB));

        var objects = new Dictionary<int, PdfObject>();
        var codepointMap = new Dictionary<int, int> { ['A'] = glyphA, ['B'] = glyphB };

        var result = FontObjectBuilder.BuildEmbeddedTrueType(font, new HashSet<int> { glyphA, glyphB }, codepointMap, MakeAllocator(objects), PdfFilterRegistry.Default, PdfOptions.Default);

        var type0 = (PdfDictionary)objects[result.FontDictionaryReference.Number];
        var cidFontRef = (PdfReference)((PdfArray)type0[PdfName.Get("DescendantFonts")])[0];
        var cidFont = (PdfDictionary)objects[cidFontRef.Target.Number];
        var descriptor = (PdfDictionary)objects[((PdfReference)cidFont[PdfName.Get("FontDescriptor")]).Target.Number];

        var cidSetRef = Assert.IsType<PdfReference>(descriptor[PdfName.Get("CIDSet")]);
        var cidSet = Assert.IsType<PdfStream>(objects[cidSetRef.Target.Number]);
        var bits = cidSet.GetDecodedBytes(PdfFilterRegistry.Default);

        static bool IsSet(byte[] bytes, int cid) => (bytes[cid / 8] & (0x80 >> (cid % 8))) != 0;

        // CID 0 (.notdef, always retained by the subsetter) plus every retained glyph's
        // subset CID must be set...
        Assert.True(IsSet(bits, 0), "CID 0 (.notdef) must be marked present.");
        var retainedCids = result.GlyphIdMap.Values.ToHashSet();
        foreach (var cid in retainedCids)
        {
            Assert.True(IsSet(bits, cid), $"Retained subset CID {cid} must be marked present.");
        }

        // ...the stream must span exactly through the highest retained CID's byte, and no
        // OTHER bit may claim a glyph the subset does not actually embed.
        var maxCid = retainedCids.Max();
        Assert.Equal((maxCid / 8) + 1, bits.Length);
        for (var cid = 0; cid < bits.Length * 8; cid++)
        {
            if (cid != 0 && !retainedCids.Contains(cid))
            {
                Assert.False(IsSet(bits, cid), $"CID {cid} is marked present but is not in the embedded subset.");
            }
        }
    }
}
