using PlumePdf.Fonts.Reading;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Fonts.Reading;

/// <summary>
/// Unit tests for <see cref="EncodingResolver"/>: <c>/Differences</c> overrides on top of a
/// named <c>/BaseEncoding</c>, and the symbolic-vs-nonsymbolic built-in-encoding fallback used
/// when a simple font declares no <c>/Encoding</c> at all.
/// </summary>
public class EncodingResolverTests
{
    private static readonly IObjectSource EmptySource = new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject>());

    [Fact]
    public void NoEncoding_Nonsymbolic_DefaultsToStandardEncoding()
    {
        var fontDict = new PdfDictionary();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null);

        // StandardEncoding code 65 is 'A' - same as ASCII.
        Assert.Equal(("A", 65), table[65]);

        // StandardEncoding code 39 is "quoteright" (U+2019), unlike WinAnsi's "quotesingle".
        Assert.Equal(("quoteright", 0x2019), table[39]);
    }

    [Fact]
    public void NoEncoding_Symbolic_ProducesAnOpaqueTable()
    {
        var fontDict = BuildFontDictWithFlags(symbolic: true);

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null);

        Assert.Equal(("", -1), table[65]);
        Assert.Equal(256, table.Length);
    }

    [Fact]
    public void NamedEncoding_WinAnsi_ResolvesToTheWinAnsiTable()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null);

        Assert.Equal(("quotesingle", 0x27), table[39]);
    }

    [Fact]
    public void DifferencesOverridesSpecificCodesOnTopOfTheBaseEncoding()
    {
        var fontDict = new PdfDictionary();
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("BaseEncoding"), PdfName.Get("WinAnsiEncoding"));
        encodingDict.Set(PdfName.Get("Differences"), new PdfArray(
        [
            PdfNumber.Get(65),
            PdfName.Get("g1234custom"), // not a real/algorithmic glyph name — exercises the "unresolvable name" branch
            PdfName.Get("B"),
            PdfNumber.Get(100),
            PdfName.Get("uni20AC"), // Adobe Glyph List algorithmic uniXXXX convention
        ]));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null);

        // Code 65 was reassigned to a name PlumePDF can't resolve to Unicode - the glyph name
        // is still recorded (useful for width lookup) even though Unicode is unknown.
        Assert.Equal("g1234custom", table[65].GlyphName);
        Assert.Equal(-1, table[65].Unicode);

        // Code 66 was reassigned to the well-known glyph name "B".
        Assert.Equal(("B", 66), table[66]);

        // Code 100 resolves via the uniXXXX algorithmic convention.
        Assert.Equal(("uni20AC", 0x20AC), table[100]);

        // Everything else still comes from the declared /BaseEncoding (WinAnsi).
        Assert.Equal(("space", 32), table[32]);
    }

    [Fact]
    public void DifferencesWithoutExplicitBaseEncoding_AppliesOnTopOfTheDefaultForSymbolicity()
    {
        var fontDict = new PdfDictionary();
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("Differences"), new PdfArray([PdfNumber.Get(200), PdfName.Get("bullet")]));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null);

        // No /BaseEncoding, nonsymbolic default is StandardEncoding: code 65 unaffected.
        Assert.Equal(("A", 65), table[65]);

        // The /Differences override still applies on top of that base.
        Assert.Equal(("bullet", 0x2022), table[200]);
    }

    [Fact]
    public void ResolveNeverMutatesTheSharedBaseTables()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null);
        table[65] = ("Mutated", 0);

        Assert.Equal(("A", 65), SimpleFontEncodings.WinAnsiEncoding[65]);
    }

    private static PdfDictionary BuildFontDictWithFlags(bool symbolic)
    {
        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(symbolic ? 4 : 32));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("FontDescriptor"), descriptor);
        return fontDict;
    }

    // ---- §3.2 built-in-base rule tables (builtInBase / builtInIsAuthoritative) ----

    /// <summary>
    /// A built-in base table distinct from every named base table (Standard/WinAnsi/MacRoman),
    /// so a test can tell which table actually won without depending on those tables' contents.
    /// Codes 65 ('A') and 66 ('B') are populated; everything else is unassigned.
    /// </summary>
    private static (string GlyphName, int Unicode)[] BuildCustomBuiltInBase()
    {
        var table = new (string, int)[256];
        for (var i = 0; i < table.Length; i++)
        {
            table[i] = (string.Empty, -1);
        }

        table[65] = ("customA", 0xE001);
        table[66] = ("customB", 0xE002);
        return table;
    }

    // Row 1 (non-authoritative, null /Encoding): builtInBase ?? DefaultBaseTable(isSymbolic).
    [Fact]
    public void NonAuthoritative_NullEncoding_UsesBuiltInBaseAsTheDefault()
    {
        var fontDict = new PdfDictionary();
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: false);

        Assert.Equal(("customA", 0xE001), table[65]);
        Assert.Equal(("", -1), table[32]); // Not StandardEncoding's "space" — builtInBase, not the historical default, wins.
    }

    // Row 2 (non-authoritative, named /Encoding): the named table wins unchanged; builtInBase is ignored.
    [Fact]
    public void NonAuthoritative_NamedEncoding_IgnoresBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: false);

        Assert.Equal(("quotesingle", 0x27), table[39]); // WinAnsi, not builtInBase.
        Assert.Equal(("A", 65), table[65]); // WinAnsi's own 'A', not builtInBase's "customA".
    }

    // Row 3 (non-authoritative, dict with /BaseEncoding): the explicit base wins unchanged; builtInBase is ignored.
    [Fact]
    public void NonAuthoritative_DictWithBaseEncoding_IgnoresBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("BaseEncoding"), PdfName.Get("WinAnsiEncoding"));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: false);

        Assert.Equal(("A", 65), table[65]); // WinAnsi's own 'A', not builtInBase's "customA".
    }

    // Row 4 (non-authoritative, dict without /BaseEncoding): builtInBase ?? DefaultBaseTable(isSymbolic), then /Differences.
    [Fact]
    public void NonAuthoritative_DictWithoutBaseEncoding_AppliesDifferencesOverBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("Differences"), new PdfArray([PdfNumber.Get(200), PdfName.Get("bullet")]));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: false);

        Assert.Equal(("customA", 0xE001), table[65]); // builtInBase, unaffected by /Differences.
        Assert.Equal(("bullet", 0x2022), table[200]); // /Differences override still applies.
    }

    // Row 5 (authoritative, null /Encoding): builtInBase, unconditionally.
    [Fact]
    public void Authoritative_NullEncoding_ReturnsBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: true);

        Assert.Equal(("customA", 0xE001), table[65]);
        Assert.Equal(("customB", 0xE002), table[66]);
    }

    // Row 6 (authoritative, named /Encoding): the name is ignored outright; builtInBase wins.
    [Fact]
    public void Authoritative_NamedEncodingIgnored_ReturnsBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: true);

        // If WinAnsi had won, code 39 would be "quotesingle" — it stays unassigned instead.
        Assert.Equal(("", -1), table[39]);
        Assert.Equal(("customA", 0xE001), table[65]);
    }

    // Row 7 (authoritative, dict with /BaseEncoding present): /BaseEncoding is ignored; /Differences layers on builtInBase.
    [Fact]
    public void Authoritative_DictWithBaseEncoding_IgnoresBaseEncodingAppliesDifferencesOverBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("BaseEncoding"), PdfName.Get("WinAnsiEncoding"));
        encodingDict.Set(PdfName.Get("Differences"), new PdfArray([PdfNumber.Get(200), PdfName.Get("bullet")]));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: true);

        Assert.Equal(("customA", 0xE001), table[65]); // builtInBase, not WinAnsi's 'A' — /BaseEncoding was ignored.
        Assert.Equal(("bullet", 0x2022), table[200]); // /Differences still applies.
    }

    // Row 8 (authoritative, dict without /BaseEncoding): /Differences layers on builtInBase.
    [Fact]
    public void Authoritative_DictWithoutBaseEncoding_AppliesDifferencesOverBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("Differences"), new PdfArray([PdfNumber.Get(200), PdfName.Get("bullet")]));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: true);

        Assert.Equal(("customA", 0xE001), table[65]);
        Assert.Equal(("bullet", 0x2022), table[200]);
    }

    [Fact]
    public void Authoritative_WithNullBuiltInBase_ThrowsArgumentException()
    {
        var fontDict = new PdfDictionary();

        var ex = Assert.Throws<ArgumentException>(() => EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase: null, builtInIsAuthoritative: true));
        Assert.Equal("builtInBase", ex.ParamName);
    }

    // ResolveBaseEncodingName's fallback branches (MacExpertEncoding, unrecognized base name)
    // now honor builtInBase instead of always reverting to DefaultBaseTable — otherwise their
    // PLUME8019/PLUME8017 messages ("falls back to the font's default built-in encoding") would
    // be false whenever the caller actually supplied a built-in table.
    [Fact]
    public void NonAuthoritative_NamedMacExpertEncoding_FallsBackToBuiltInBase_NotDefaultBaseTable()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("MacExpertEncoding"));
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: false);

        Assert.Equal(("customA", 0xE001), table[65]); // builtInBase, not StandardEncoding's "A".
    }

    [Fact]
    public void NonAuthoritative_UnrecognizedBaseEncodingName_FallsBackToBuiltInBase_NotDefaultBaseTable()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("NotARealEncoding"));
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: false);

        Assert.Equal(("customA", 0xE001), table[65]); // builtInBase, not StandardEncoding's "A".
    }

    // A malformed /Encoding (neither a name nor a dictionary) must report the same PLUME8017
    // deviation whether or not the caller passed builtInIsAuthoritative — the authoritative
    // branch previously swallowed it silently.
    [Fact]
    public void Authoritative_MalformedEncodingValue_ReportsPlume8017_FallsBackToBuiltInBase()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Encoding"), PdfNumber.Get(42)); // Neither a name nor a dictionary.
        var builtInBase = BuildCustomBuiltInBase();
        var diagnostics = new DiagnosticCollection();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, diagnostics, builtInBase, builtInIsAuthoritative: true);

        Assert.Equal(("customA", 0xE001), table[65]);
        Assert.Contains(diagnostics, d => d.Code == "PLUME8017");
    }

    [Fact]
    public void Resolve_NeverReturnsTheCallersBuiltInBaseArrayItself()
    {
        var fontDict = new PdfDictionary();
        var builtInBase = BuildCustomBuiltInBase();

        var table = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, builtInBase, builtInIsAuthoritative: true);
        table[65] = ("Mutated", 0);

        Assert.Equal(("customA", 0xE001), builtInBase[65]);
    }

    public static TheoryData<PdfDictionary> ExistingCallerDictionaries()
    {
        var noEncoding = new PdfDictionary();

        var symbolic = BuildFontDictWithFlags(symbolic: true);

        var namedWinAnsi = new PdfDictionary();
        namedWinAnsi.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));

        var dictWithBase = new PdfDictionary();
        var encodingDictWithBase = new PdfDictionary();
        encodingDictWithBase.Set(PdfName.Get("BaseEncoding"), PdfName.Get("WinAnsiEncoding"));
        encodingDictWithBase.Set(PdfName.Get("Differences"), new PdfArray([PdfNumber.Get(65), PdfName.Get("B")]));
        dictWithBase.Set(PdfName.Get("Encoding"), encodingDictWithBase);

        var dictWithoutBase = new PdfDictionary();
        var encodingDictWithoutBase = new PdfDictionary();
        encodingDictWithoutBase.Set(PdfName.Get("Differences"), new PdfArray([PdfNumber.Get(200), PdfName.Get("bullet")]));
        dictWithoutBase.Set(PdfName.Get("Encoding"), encodingDictWithoutBase);

        return new TheoryData<PdfDictionary>
        {
            noEncoding,
            symbolic,
            namedWinAnsi,
            dictWithBase,
            dictWithoutBase,
        };
    }

    /// <summary>
    /// Bit-identity: for every dictionary shape the pre-existing (four-parameter-era) call sites
    /// exercise, calling <c>Resolve</c> with the two new parameters explicitly defaulted produces
    /// the exact same table as omitting them — the widened signature changes nothing for a caller
    /// that never passes <c>builtInBase</c>/<c>builtInIsAuthoritative</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExistingCallerDictionaries))]
    public void WidenedSignature_DefaultsAreBitIdenticalToTheOldFourParameterOverload(PdfDictionary fontDict)
    {
        // Comparing Resolve(d) with Resolve(d, null, false) is the same
        // method twice and proves nothing. The expected tables below are built INDEPENDENTLY from
        // the base tables the earlier resolver returned for each shape (StandardEncoding clone,
        // all-unassigned opaque table for a symbolic font, the named table, base + /Differences).
        var actual = EncodingResolver.Resolve(fontDict, EmptySource, PdfOptions.Default, null, null, false);

        Assert.Equal(ExpectedLegacyTable(fontDict), actual);
    }

    private static (string GlyphName, int Unicode)[] ExpectedLegacyTable(PdfDictionary fontDict)
    {
        (string GlyphName, int Unicode)[] Clone((string GlyphName, int Unicode)[] t) => [.. t];
        var opaque = new (string GlyphName, int Unicode)[256];
        Array.Fill(opaque, (string.Empty, -1));

        if (!fontDict.TryGetValue(PdfName.Get("Encoding"), out var enc))
        {
            var symbolic = fontDict.TryGetValue(PdfName.Get("FontDescriptor"), out var fd) && fd is PdfDictionary d
                && d.TryGetValue(PdfName.Get("Flags"), out var f) && f is PdfNumber n && n.TryToInt32(out var flags) && (flags & 4) != 0 && (flags & 32) == 0;
            return symbolic ? opaque : Clone(SimpleFontEncodings.StandardEncoding);
        }

        if (enc is PdfName name)
        {
            return name.Value == "WinAnsiEncoding" ? Clone(SimpleFontEncodings.WinAnsiEncoding) : throw new InvalidOperationException("unexpected fixture");
        }

        var encDict = (PdfDictionary)enc;
        var table = encDict.TryGetValue(PdfName.Get("BaseEncoding"), out var be) && be is PdfName bn && bn.Value == "WinAnsiEncoding"
            ? Clone(SimpleFontEncodings.WinAnsiEncoding)
            : Clone(SimpleFontEncodings.StandardEncoding);
        if (encDict.TryGetValue(PdfName.Get("Differences"), out var diffs) && diffs is PdfArray arr)
        {
            var code = 0;
            foreach (var item in arr)
            {
                if (item is PdfNumber num && num.TryToInt32(out var c))
                {
                    code = c;
                }
                else if (item is PdfName gn)
                {
                    table[code++] = (gn.Value, SimpleFontEncodings.TryGlyphNameToUnicode(gn.Value, out var u) ? u : -1);
                }
            }
        }

        return table;
    }
}
