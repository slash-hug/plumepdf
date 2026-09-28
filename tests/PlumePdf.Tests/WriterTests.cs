using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="ObjectSerializer"/> unit tests: serialize a value, reparse it
/// with the reading engine's own tokenizer/parser, and assert the round trip is faithful —
/// per object kind, plus name/string escaping edge cases.
/// </summary>
public class WriterTests
{
    private static PdfObject RoundTrip(PdfObject value)
    {
        using var stream = new MemoryStream();
        ObjectSerializer.WriteValue(stream, value);
        var bytes = stream.ToArray();
        var tokenizer = new PdfTokenizer(bytes);
        return ObjectParser.ParseValue(bytes, ref tokenizer, PdfOptions.Default, diagnostics: null);
    }

    [Fact]
    public void Null_RoundTrips() => Assert.Same(PdfNull.Instance, RoundTrip(PdfNull.Instance));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Boolean_RoundTrips(bool value)
    {
        var result = Assert.IsType<PdfBoolean>(RoundTrip(PdfBoolean.Get(value)));
        Assert.Equal(value, result.Value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-17)]
    [InlineData(1000000)]
    public void IntegerNumber_RoundTrips(long value)
    {
        var result = Assert.IsType<PdfNumber>(RoundTrip(PdfNumber.Get(value)));
        Assert.True(result.IsInteger);
        Assert.Equal(value, result.ToInt64());
    }

    [Fact]
    public void RealNumber_RoundTrips()
    {
        var result = Assert.IsType<PdfNumber>(RoundTrip(PdfNumber.Get(3.25)));
        Assert.False(result.IsInteger);
        Assert.Equal(3.25, result.Value, precision: 6);
    }

    [Theory]
    [InlineData("Type")]
    [InlineData("Name With Spaces")]
    [InlineData("Name#WithHash")]
    [InlineData("Name(With)Parens")]
    public void Name_RoundTrips_IncludingIrregularBytes(string value)
    {
        var result = Assert.IsType<PdfName>(RoundTrip(PdfName.Get(value)));
        Assert.Equal(value, result.Value);
    }

    [Fact]
    public void LiteralString_RoundTrips_IncludingParensAndBackslash()
    {
        var bytes = "A (nested) string with a \\ backslash"u8.ToArray();
        var result = Assert.IsType<PdfString>(RoundTrip(PdfString.FromLiteral(bytes)));
        Assert.Equal(bytes, result.Bytes.ToArray());
    }

    [Fact]
    public void HexString_RoundTrips()
    {
        byte[] bytes = [0x00, 0x01, 0xFF, 0x7E];
        var result = Assert.IsType<PdfString>(RoundTrip(PdfString.FromHex(bytes)));
        Assert.Equal(bytes, result.Bytes.ToArray());
        Assert.True(result.IsHex);
    }

    [Fact]
    public void LiteralString_RoundTrips_EveryByteValue()
    {
        // A literal string containing every possible byte 0x00-0xFF, individually - notably
        // 0x0D (CR): an unescaped CR (or CRLF) normalizes to a single LF on read back
        // (§7.3.4.2), so the serializer must escape it or a source CR silently becomes an LF
        // after a save/reopen cycle.
        for (var b = 0; b <= 0xFF; b++)
        {
            byte[] bytes = [(byte)b];
            var result = Assert.IsType<PdfString>(RoundTrip(PdfString.FromLiteral(bytes)));
            Assert.Equal(bytes, result.Bytes.ToArray());
        }
    }

    [Fact]
    public void LiteralString_CarriageReturn_IsEscapedAndSurvivesSaveReopen()
    {
        // The exact reproduction from the reported issue: a page dictionary entry containing
        // an unescaped CR byte must come back byte-identical after a real save/reopen cycle
        // through PdfDocument, not just through ObjectSerializer/ObjectParser directly.
        byte[] bytes = [0x74, 0x0D, 0x7A]; // "t\rz"
        var sourcePath = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-cr-escape-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(sourcePath))
            {
                var page = document.Pages[0];
                page.Dictionary.Set(PdfName.Get("Marker"), PdfString.FromLiteral(bytes));
                document.Save(outputPath);
            }

            using var reopened = PdfDocument.Open(outputPath);
            var marker = Assert.IsType<PdfString>(reopened.Pages[0].Dictionary[PdfName.Get("Marker")]);
            Assert.Equal(bytes, marker.Bytes.ToArray());
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Array_RoundTrips_HeterogeneousElements()
    {
        var array = new PdfArray([PdfNumber.Get(1), PdfName.Get("Foo"), PdfBoolean.True, new PdfReference(new IndirectReference(7, 0))]);
        var result = Assert.IsType<PdfArray>(RoundTrip(array));
        Assert.Equal(4, result.Count);
        Assert.IsType<PdfNumber>(result[0]);
        Assert.IsType<PdfName>(result[1]);
        Assert.IsType<PdfBoolean>(result[2]);
        var reference = Assert.IsType<PdfReference>(result[3]);
        Assert.Equal(new IndirectReference(7, 0), reference.Target);
    }

    [Fact]
    public void Dictionary_RoundTrips_PreservingKeysAndOrder()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("Catalog"));
        dict.Set(PdfName.Get("Pages"), new PdfReference(new IndirectReference(2, 0)));

        var result = Assert.IsType<PdfDictionary>(RoundTrip(dict));
        Assert.Equal(["Type", "Pages"], result.Keys.Select(static k => k.Value));
        Assert.Equal("Catalog", Assert.IsType<PdfName>(result[PdfName.Type]).Value);
    }

    [Fact]
    public void Stream_RoundTrips_AndRecomputesLength()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Length, PdfNumber.Get(999)); // deliberately wrong; the writer must recompute it
        var payload = "BT /F1 12 Tf ET"u8.ToArray();
        var stream = new PdfStream(dict, payload);

        var result = Assert.IsType<PdfStream>(RoundTrip(stream));
        Assert.Equal(payload, result.RawBytes.ToArray());
        var length = Assert.IsType<PdfNumber>(result.Dictionary[PdfName.Length]);
        Assert.Equal(payload.Length, length.ToInt32());
    }

    [Fact]
    public void Reference_TranslatesThroughCallback()
    {
        using var output = new MemoryStream();
        var reference = new PdfReference(new IndirectReference(3, 0));
        ObjectSerializer.WriteValue(output, reference, static r => new IndirectReference(r.Target.Number + 100, 0));

        var bytes = output.ToArray();
        var tokenizer = new PdfTokenizer(bytes);
        var parsed = Assert.IsType<PdfReference>(ObjectParser.ParseValue(bytes, ref tokenizer, PdfOptions.Default, diagnostics: null));
        Assert.Equal(new IndirectReference(103, 0), parsed.Target);
    }

    [Fact]
    public void IndirectObject_WritesCompleteFraming()
    {
        using var output = new MemoryStream();
        ObjectSerializer.WriteIndirectObject(output, 5, 0, PdfBoolean.True);
        var text = System.Text.Encoding.ASCII.GetString(output.ToArray());
        Assert.Equal("5 0 obj\ntrue\nendobj\n", text);
    }
}
