using PlumePdf.Documents.Compression;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// The one mapping from a <c>/Filter</c> value to the <see cref="ImageEncoding"/> family the
/// compression result reports: the last filter that encodes the samples decides it, ASCII
/// wrappers don't, abbreviated inline-image names map like their full names, and indirect
/// values are followed.
/// </summary>
public class ImageEncodingsTests
{
    public static TheoryData<string, ImageEncoding> SingleNames() => new()
    {
        { "FlateDecode", ImageEncoding.Flate },
        { "Fl", ImageEncoding.Flate },
        { "DCTDecode", ImageEncoding.Jpeg },
        { "DCT", ImageEncoding.Jpeg },
        { "JPXDecode", ImageEncoding.Jpeg2000 },
        { "JBIG2Decode", ImageEncoding.Jbig2 },
        { "CCITTFaxDecode", ImageEncoding.Ccitt },
        { "CCF", ImageEncoding.Ccitt },
        { "RunLengthDecode", ImageEncoding.RunLength },
        { "RL", ImageEncoding.RunLength },
        { "LZWDecode", ImageEncoding.Lzw },
        { "LZW", ImageEncoding.Lzw },
        { "ASCIIHexDecode", ImageEncoding.Uncompressed },
        { "AHx", ImageEncoding.Uncompressed },
        { "ASCII85Decode", ImageEncoding.Uncompressed },
        { "A85", ImageEncoding.Uncompressed },
        { "Crypt", ImageEncoding.Other },
        { "BrotliDecode", ImageEncoding.Other },
        { "flatedecode", ImageEncoding.Other },
    };

    [Theory]
    [MemberData(nameof(SingleNames))]
    public void SingleName_MapsToItsFamily(string name, ImageEncoding expected)
    {
        Assert.Equal(expected, ImageEncodings.FromFilter(PdfName.Get(name), source: null));
    }

    [Theory]
    [MemberData(nameof(SingleNames))]
    public void OneElementArray_MapsLikeTheName(string name, ImageEncoding expected)
    {
        Assert.Equal(expected, ImageEncodings.FromFilter(Array(name), source: null));
    }

    [Fact]
    public void NoFilter_IsUncompressed()
    {
        Assert.Equal(ImageEncoding.Uncompressed, ImageEncodings.FromFilter(null, source: null));
        Assert.Equal(ImageEncoding.Uncompressed, ImageEncodings.FromFilter(PdfNull.Instance, source: null));
        Assert.Equal(ImageEncoding.Uncompressed, ImageEncodings.FromFilter(new PdfArray(), source: null));
    }

    [Fact]
    public void Uncompressed_IsTheDefaultValue()
    {
        Assert.Equal(ImageEncoding.Uncompressed, default(ImageEncoding));
    }

    [Theory]
    [InlineData(ImageEncoding.Jpeg, "ASCII85Decode", "DCTDecode")]
    [InlineData(ImageEncoding.Flate, "AHx", "Fl")]
    [InlineData(ImageEncoding.Flate, "FlateDecode", "ASCIIHexDecode")]
    [InlineData(ImageEncoding.Jpeg, "DCTDecode", "ASCII85Decode", "ASCIIHexDecode")]
    [InlineData(ImageEncoding.Jpeg, "FlateDecode", "DCTDecode")]
    [InlineData(ImageEncoding.Flate, "DCTDecode", "FlateDecode")]
    [InlineData(ImageEncoding.Flate, "Crypt", "FlateDecode")]
    [InlineData(ImageEncoding.Other, "FlateDecode", "Crypt")]
    [InlineData(ImageEncoding.Other, "ASCII85Decode", "SomethingNew", "AHx")]
    [InlineData(ImageEncoding.Uncompressed, "ASCII85Decode", "ASCIIHexDecode")]
    [InlineData(ImageEncoding.Uncompressed, "A85", "AHx", "A85")]
    [InlineData(ImageEncoding.Ccitt, "CCF", "A85")]
    [InlineData(ImageEncoding.Jbig2, "ASCIIHexDecode", "JBIG2Decode")]
    [InlineData(ImageEncoding.Jpeg2000, "JPXDecode", "AHx")]
    [InlineData(ImageEncoding.RunLength, "LZWDecode", "RunLengthDecode")]
    [InlineData(ImageEncoding.Lzw, "RL", "LZW", "A85")]
    public void Chain_IsDecidedByTheLastFilterThatIsNotAnAsciiWrapper(ImageEncoding expected, params string[] chain)
    {
        Assert.Equal(expected, ImageEncodings.FromFilter(Array(chain), source: null));
    }

    [Fact]
    public void ANonNameInTheChain_IsOther_WhenItIsTheEncodingFilter()
    {
        var chain = new PdfArray([PdfName.Get("FlateDecode"), PdfNumber.Get(3), PdfName.Get("ASCIIHexDecode")]);
        Assert.Equal(ImageEncoding.Other, ImageEncodings.FromFilter(chain, source: null));

        var before = new PdfArray([PdfNumber.Get(3), PdfName.Get("DCTDecode")]);
        Assert.Equal(ImageEncoding.Jpeg, ImageEncodings.FromFilter(before, source: null));
    }

    [Fact]
    public void AValueThatIsNeitherNameNorArray_IsOther()
    {
        Assert.Equal(ImageEncoding.Other, ImageEncodings.FromFilter(PdfNumber.Get(7), source: null));
        Assert.Equal(ImageEncoding.Other, ImageEncodings.FromFilter(new PdfDictionary(), source: null));
    }

    [Fact]
    public void IndirectFilterName_IsFollowed()
    {
        var source = Source((5, PdfName.Get("DCTDecode")));
        Assert.Equal(ImageEncoding.Jpeg, ImageEncodings.FromFilter(Ref(5), source));
    }

    [Fact]
    public void IndirectFilterArray_AndIndirectElements_AreFollowed()
    {
        var source = Source(
            (5, new PdfArray([PdfName.Get("ASCII85Decode"), Ref(6)])),
            (6, PdfName.Get("JBIG2Decode")),
            (7, PdfName.Get("ASCIIHexDecode")));

        Assert.Equal(ImageEncoding.Jbig2, ImageEncodings.FromFilter(Ref(5), source));
        Assert.Equal(ImageEncoding.Flate, ImageEncodings.FromFilter(new PdfArray([PdfName.Get("FlateDecode"), Ref(7)]), source));
    }

    [Fact]
    public void DanglingReference_IsNoFilter()
    {
        Assert.Equal(ImageEncoding.Uncompressed, ImageEncodings.FromFilter(Ref(99), Source()));
    }

    [Fact]
    public void Reference_WithNoSourceToResolveIt_IsOther()
    {
        Assert.Equal(ImageEncoding.Other, ImageEncodings.FromFilter(Ref(5), source: null));
    }

    [Fact]
    public void StreamDictionary_ReadsFilter()
    {
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Filter, PdfName.Get("DCTDecode"));
        Assert.Equal(ImageEncoding.Jpeg, ImageEncodings.OfStream(dictionary, source: null));

        // /F on a stream names an external file, not a filter.
        var external = new PdfDictionary();
        external.Set(PdfName.Get("F"), PdfName.Get("DCTDecode"));
        Assert.Equal(ImageEncoding.Uncompressed, ImageEncodings.OfStream(external, source: null));
    }

    [Fact]
    public void InlineImageDictionary_ReadsTheAbbreviatedKey_OrTheFullOne()
    {
        var abbreviated = new PdfDictionary();
        abbreviated.Set(PdfName.Get("F"), new PdfArray([PdfName.Get("A85"), PdfName.Get("DCT")]));
        Assert.Equal(ImageEncoding.Jpeg, ImageEncodings.OfInlineImage(abbreviated));

        var full = new PdfDictionary();
        full.Set(PdfName.Filter, PdfName.Get("CCF"));
        Assert.Equal(ImageEncoding.Ccitt, ImageEncodings.OfInlineImage(full));

        Assert.Equal(ImageEncoding.Uncompressed, ImageEncodings.OfInlineImage(new PdfDictionary()));
    }

    private static PdfArray Array(params string[] names) => new(names.Select(static n => (PdfObject)PdfName.Get(n)));

    private static PdfReference Ref(int number) => new(new IndirectReference(number, 0));

    private static InMemoryObjectSource Source(params (int Number, PdfObject Value)[] objects) =>
        new(new PdfDictionary(), objects.ToDictionary(static o => o.Number, static o => o.Value));
}
