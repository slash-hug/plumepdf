using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

public class TokenizerTests
{
    private static PdfTokenizer Make(string text) => new(Encoding.ASCII.GetBytes(text));

    [Fact]
    public void SkipsWhitespaceAndComments()
    {
        var tokenizer = Make("   % a comment\r\n   42");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Integer, tokenizer.TokenType);
        Assert.Equal(42, tokenizer.IntegerValue);
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("+17", 17L)]
    [InlineData("-17", -17L)]
    public void ReadsIntegers(string text, long expected)
    {
        var tokenizer = Make(text);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Integer, tokenizer.TokenType);
        Assert.False(tokenizer.IsMalformed);
        Assert.Equal(expected, tokenizer.IntegerValue);
    }

    [Theory]
    [InlineData("34.5", 34.5)]
    [InlineData("-3.62", -3.62)]
    [InlineData(".5", 0.5)]
    [InlineData("4.", 4.0)]
    [InlineData("-.002", -0.002)]
    public void ReadsRealNumbers(string text, double expected)
    {
        var tokenizer = Make(text);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Real, tokenizer.TokenType);
        Assert.False(tokenizer.IsMalformed);
        Assert.Equal(expected, tokenizer.RealValue, precision: 9);
    }

    [Fact]
    public void ReadsRealNumbers_ExactlyAsStandardDoubleParsingWould()
    {
        // Accumulating the fraction digit-by-digit via repeated multiply-and-add (the old
        // approach) introduces representation error standard parsing doesn't: "0.7" used to
        // materialize as 0.7000000000000001. Parsed directly from the matched bytes instead,
        // this must equal exactly what double.Parse produces for the same literal - not just
        // "close" to it.
        var tokenizer = Make("0.7");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Real, tokenizer.TokenType);
        Assert.Equal(double.Parse("0.7", System.Globalization.CultureInfo.InvariantCulture), tokenizer.RealValue);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(".")]
    public void BareSignOrDot_IsMalformedButMakesProgress(string text)
    {
        var tokenizer = Make(text);
        Assert.True(tokenizer.Read());
        Assert.True(tokenizer.IsMalformed);
        Assert.False(tokenizer.Read()); // consumed the whole (single-byte) buffer
    }

    [Fact]
    public void ReadsSimpleName()
    {
        var tokenizer = Make("/Type");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Name, tokenizer.TokenType);
        Assert.Equal("Type", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void ReadsEmptyName()
    {
        var tokenizer = Make("/ ");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Name, tokenizer.TokenType);
        Assert.Equal(0, tokenizer.ValueSpan.Length);
    }

    [Fact]
    public void ReadsNameWithHashEscapes()
    {
        var tokenizer = Make("/A#42#20B");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Name, tokenizer.TokenType);
        Assert.Equal("AB B", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void ReadsNameStoppingAtDelimiter()
    {
        var tokenizer = Make("/Type/Pages");
        Assert.True(tokenizer.Read());
        Assert.Equal("Type", Encoding.ASCII.GetString(tokenizer.ValueSpan));
        Assert.True(tokenizer.Read());
        Assert.Equal("Pages", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void ReadsLiteralStringWithEscapes()
    {
        var tokenizer = Make(@"(Hi \(there\)\n\101)");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.LiteralString, tokenizer.TokenType);
        Assert.Equal("Hi (there)\nA", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void LiteralString_SupportsNestedBalancedParens()
    {
        var tokenizer = Make("(outer (inner) text)");
        Assert.True(tokenizer.Read());
        Assert.Equal("outer (inner) text", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void LiteralString_BackslashLineContinuation_ProducesNoCharacter()
    {
        var tokenizer = Make("(line1\\\nline2)");
        Assert.True(tokenizer.Read());
        Assert.Equal("line1line2", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void LiteralString_UnescapedCarriageReturn_NormalizesToLineFeed()
    {
        var tokenizer = Make("(a\rb)");
        Assert.True(tokenizer.Read());
        Assert.Equal("a\nb", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void LiteralString_Unterminated_IsMalformed()
    {
        var tokenizer = Make("(no closing paren");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.LiteralString, tokenizer.TokenType);
        Assert.True(tokenizer.IsMalformed);
    }

    [Fact]
    public void ReadsHexString()
    {
        var tokenizer = Make("<48656C6C6F>");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.HexString, tokenizer.TokenType);
        Assert.Equal("Hello", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void HexString_OddDigitCount_PadsWithImplicitZero()
    {
        var tokenizer = Make("<901FA3>"); // even, sanity baseline
        Assert.True(tokenizer.Read());
        Assert.Equal(3, tokenizer.ValueSpan.Length);

        var odd = Make("<901FA>");
        Assert.True(odd.Read());
        Assert.Equal(3, odd.ValueSpan.Length);
        Assert.Equal(0xA0, odd.ValueSpan[2]);
    }

    [Fact]
    public void HexString_IgnoresWhitespaceBetweenDigits()
    {
        var tokenizer = Make("<48 65 6C 6C 6F>");
        Assert.True(tokenizer.Read());
        Assert.Equal("Hello", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void HexString_Unterminated_IsMalformed()
    {
        var tokenizer = Make("<4865");
        Assert.True(tokenizer.Read());
        Assert.True(tokenizer.IsMalformed);
    }

    [Fact]
    public void ReadsArrayDelimiters()
    {
        var tokenizer = Make("[1 2]");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.ArrayStart, tokenizer.TokenType);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Integer, tokenizer.TokenType);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Integer, tokenizer.TokenType);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.ArrayEnd, tokenizer.TokenType);
    }

    [Fact]
    public void ReadsDictionaryDelimiters()
    {
        var tokenizer = Make("<< /Type /Catalog >>");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.DictStart, tokenizer.TokenType);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Name, tokenizer.TokenType);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Name, tokenizer.TokenType);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.DictEnd, tokenizer.TokenType);
    }

    [Fact]
    public void SingleAngleBracket_WithoutMatch_IsMalformedKeyword()
    {
        var tokenizer = Make(">x");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Keyword, tokenizer.TokenType);
        Assert.True(tokenizer.IsMalformed);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("obj")]
    [InlineData("endobj")]
    [InlineData("stream")]
    [InlineData("endstream")]
    [InlineData("R")]
    [InlineData("xref")]
    [InlineData("trailer")]
    [InlineData("startxref")]
    public void ReadsKeywords(string keyword)
    {
        var tokenizer = Make(keyword);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Keyword, tokenizer.TokenType);
        Assert.False(tokenizer.IsMalformed);
        Assert.Equal(keyword, Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void ReadsIndirectReferencePatternAsThreeTokens()
    {
        var tokenizer = Make("12 0 R");
        Assert.True(tokenizer.Read());
        Assert.Equal(12, tokenizer.IntegerValue);
        Assert.True(tokenizer.Read());
        Assert.Equal(0, tokenizer.IntegerValue);
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Keyword, tokenizer.TokenType);
        Assert.Equal("R", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }

    [Fact]
    public void EndOfInput_ReturnsFalseRepeatedly()
    {
        var tokenizer = Make("42");
        Assert.True(tokenizer.Read());
        Assert.False(tokenizer.Read());
        Assert.Equal(PdfTokenType.EndOfInput, tokenizer.TokenType);
        Assert.False(tokenizer.Read());
    }

    [Fact]
    public void Reset_RewindsToAGivenPosition()
    {
        var tokenizer = Make("1 2 R");
        Assert.True(tokenizer.Read());
        var checkpoint = tokenizer.Consumed;
        Assert.True(tokenizer.Read());
        Assert.True(tokenizer.Read());

        tokenizer.Reset(checkpoint);
        Assert.True(tokenizer.Read());
        Assert.Equal(2, tokenizer.IntegerValue);
    }

    [Fact]
    public void ConsumedTracksByteOffset()
    {
        var tokenizer = Make("42 true");
        Assert.True(tokenizer.Read());
        Assert.Equal(2, tokenizer.Consumed);
        Assert.True(tokenizer.Read());
        Assert.Equal(7, tokenizer.Consumed);
    }

    [Fact]
    public void EmptyBuffer_ReturnsFalseImmediately()
    {
        var tokenizer = new PdfTokenizer(ReadOnlySpan<byte>.Empty);
        Assert.False(tokenizer.Read());
        Assert.Equal(PdfTokenType.EndOfInput, tokenizer.TokenType);
    }

    [Fact]
    public void PostScriptFunctionBraces_AreSingleCharacterKeywords()
    {
        var tokenizer = Make("{ 1 2 add }");
        Assert.True(tokenizer.Read());
        Assert.Equal(PdfTokenType.Keyword, tokenizer.TokenType);
        Assert.Equal("{", Encoding.ASCII.GetString(tokenizer.ValueSpan));
    }
}

public class ObjectParserTests
{
    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    [Fact]
    public void ParsesIndirectObject_SimpleDictionary()
    {
        var value = ObjectParser.ParseIndirectObject(Bytes("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj"), PdfOptions.Default, null, out var reference);

        Assert.Equal(new IndirectReference(1, 0), reference);
        var dict = Assert.IsType<PdfDictionary>(value);
        Assert.Equal(PdfName.Get("Catalog"), dict[PdfName.Type]);
        var pagesRef = Assert.IsType<PdfReference>(dict[PdfName.Get("Pages")]);
        Assert.Equal(new IndirectReference(2, 0), pagesRef.Target);
    }

    [Fact]
    public void ParsesArray()
    {
        var tokenizer = new PdfTokenizer(Bytes("[1 2.5 /Name (str) true null]"));
        var value = ObjectParser.ParseValue(Bytes("[1 2.5 /Name (str) true null]"), ref tokenizer, PdfOptions.Default, null);

        var array = Assert.IsType<PdfArray>(value);
        Assert.Equal(6, array.Count);
        Assert.Equal(1, ((PdfNumber)array[0]).ToInt32());
        Assert.Equal(2.5, ((PdfNumber)array[1]).Value);
        Assert.Equal("Name", ((PdfName)array[2]).Value);
        Assert.Equal("str", ((PdfString)array[3]).GetText());
        Assert.Same(PdfBoolean.True, array[4]);
        Assert.Same(PdfNull.Instance, array[5]);
    }

    [Fact]
    public void PlainInteger_IsNotMisreadAsReference()
    {
        var buffer = Bytes("7");
        var tokenizer = new PdfTokenizer(buffer);
        var value = ObjectParser.ParseValue(buffer, ref tokenizer, PdfOptions.Default, null);

        var number = Assert.IsType<PdfNumber>(value);
        Assert.Equal(7, number.ToInt32());
    }

    [Fact]
    public void TwoIntegersNotFollowedByR_AreNotMisreadAsReference()
    {
        var buffer = Bytes("[7 8]");
        var tokenizer = new PdfTokenizer(buffer);
        var value = ObjectParser.ParseValue(buffer, ref tokenizer, PdfOptions.Default, null);

        var array = Assert.IsType<PdfArray>(value);
        Assert.Equal(2, array.Count);
        Assert.IsType<PdfNumber>(array[0]);
        Assert.IsType<PdfNumber>(array[1]);
    }

    [Fact]
    public void ParsesStream_WithCorrectLength()
    {
        var buffer = Bytes("1 0 obj << /Length 5 >> stream\r\nHello\r\nendstream endobj");
        var value = ObjectParser.ParseIndirectObject(buffer, PdfOptions.Default, null, out _);

        var stream = Assert.IsType<PdfStream>(value);
        Assert.Equal("Hello", Encoding.ASCII.GetString(stream.RawBytes.Span));
    }

    [Fact]
    public void ParsesStream_MissingLength_FallsBackToScanningForEndstream()
    {
        var diagnostics = new DiagnosticCollection();
        var buffer = Bytes("1 0 obj << /Type /X >> stream\r\nHello\r\nendstream endobj");

        var value = ObjectParser.ParseIndirectObject(buffer, PdfOptions.Default, diagnostics, out _);

        var stream = Assert.IsType<PdfStream>(value);
        Assert.Equal("Hello", Encoding.ASCII.GetString(stream.RawBytes.Span));
        Assert.Contains(diagnostics, d => d.Code == "PLUME2019");
    }

    [Fact]
    public void ParsesStream_WrongLength_FallsBackToScanningForEndstream()
    {
        var diagnostics = new DiagnosticCollection();
        var buffer = Bytes("1 0 obj << /Length 999 >> stream\r\nHello\r\nendstream endobj");

        var value = ObjectParser.ParseIndirectObject(buffer, PdfOptions.Default, diagnostics, out _);

        var stream = Assert.IsType<PdfStream>(value);
        Assert.Equal("Hello", Encoding.ASCII.GetString(stream.RawBytes.Span));
        Assert.Contains(diagnostics, d => d.Code == "PLUME2019");
    }

    [Fact]
    public void MalformedToken_UnderLenientOptions_ProducesDiagnosticNotException()
    {
        var diagnostics = new DiagnosticCollection();
        var buffer = Bytes("[1 . 3]"); // bare "." is a malformed numeric token
        var tokenizer = new PdfTokenizer(buffer);

        var value = ObjectParser.ParseValue(buffer, ref tokenizer, PdfOptions.Default, diagnostics);

        Assert.IsType<PdfArray>(value);
        Assert.Contains(diagnostics, d => d.Code == "PLUME2013");
    }

    [Fact]
    public void MalformedToken_UnderStrict_Throws()
    {
        var buffer = Bytes("[1 . 3]");
        var tokenizer = new PdfTokenizer(buffer);
        var strict = PdfOptions.Default with { Strict = true };

        PlumePdfException? caught = null;
        try
        {
            ObjectParser.ParseValue(buffer, ref tokenizer, strict, null);
        }
        catch (PlumePdfException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
    }

    [Fact]
    public void NestingDepth_BeyondLimit_ThrowsCodedException()
    {
        var options = PdfOptions.Default with { MaxObjectNestingDepth = 3 };
        var buffer = Bytes("[[[[1]]]]"); // four levels deep, limit is 3
        var tokenizer = new PdfTokenizer(buffer);

        PlumePdfException? caught = null;
        try
        {
            ObjectParser.ParseValue(buffer, ref tokenizer, options, null);
        }
        catch (PlumePdfException ex)
        {
            caught = ex;
        }

        Assert.NotNull(caught);
        Assert.Equal("PLUME2010", caught!.Code);
    }

    [Fact]
    public void MissingEndobj_IsToleratedWithDiagnostic()
    {
        var diagnostics = new DiagnosticCollection();
        var buffer = Bytes("1 0 obj << /Type /X >>"); // no endobj at all

        var value = ObjectParser.ParseIndirectObject(buffer, PdfOptions.Default, diagnostics, out var reference);

        Assert.IsType<PdfDictionary>(value);
        Assert.Equal(new IndirectReference(1, 0), reference);
        Assert.Contains(diagnostics, d => d.Code == "PLUME2022");
    }
}
