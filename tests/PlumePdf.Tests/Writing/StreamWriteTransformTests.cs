using System.Globalization;
using System.Text;
using PlumePdf.Objects;
using PlumePdf.Tests.PageRemoval;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// The full-rewrite writer's write-time stream transform: offered every stream it writes on
/// both the classic and the <see cref="PdfOptions.Optimize"/> body, never one a caller
/// override already replaced or one excluded from the output, and refused (<c>PLUME5010</c>)
/// when a replacement reaches for an object its original did not reference.
/// </summary>
public class StreamWriteTransformTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transform_ReplacesEveryOfferedStream_OnBothBodyPaths(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3));
        var transform = new RecordingTransform(n => ImagePagesDocument.IsImage(n, 3), Replace);

        var output = Write(document, PdfOptions.Default with { Optimize = optimize }, transform);

        using var saved = PdfDocument.Open(output);
        for (var page = 0; page < 3; page++)
        {
            var image = ImagePagesDocument.PageImage(saved, page, "Im0");
            Assert.Equal($"REPLACED-{ImagePagesDocument.Image(page)}", Encoding.ASCII.GetString(image.RawBytes.Span));
            Assert.False(image.Dictionary.ContainsKey(PdfName.DecodeParms));
        }

        // Every stream (the three contents and the three images) is offered exactly once, and
        // nothing that isn't a stream is.
        var streams = Enumerable.Range(0, 3).SelectMany(p => new[] { ImagePagesDocument.Content(p), ImagePagesDocument.Image(p) });
        Assert.Equal(streams.Order(), transform.Offered.Order());
        Assert.Equal(Enumerable.Range(0, 3).Select(ImagePagesDocument.Image), transform.Transformed.Order());
        CleanupFixtures3b.AssertQpdfClean(output, $"transformed save (Optimize={optimize})");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transform_ReturningTheOriginal_WritesItUnchanged(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2));
        var transform = new RecordingTransform(_ => true, static (_, original) => original);

        var output = Write(document, PdfOptions.Default with { Optimize = optimize, Deterministic = true }, transform);
        var baseline = Write(document, PdfOptions.Default with { Optimize = optimize, Deterministic = true }, transform: null);

        Assert.Equal(baseline, output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Override_TakesPrecedence_AndIsNeverOffered(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2));
        var overridden = ImagePagesDocument.Image(1);
        var replacement = new PdfStream(ImageDictionary(), Encoding.ASCII.GetBytes("OVERRIDDEN"));
        var replacements = new Dictionary<int, PdfObject> { [overridden] = replacement };
        var transform = new RecordingTransform(_ => true, Replace);

        var output = Write(document, PdfOptions.Default with { Optimize = optimize }, transform, replacements: replacements);

        Assert.DoesNotContain(overridden, transform.Offered);
        using var saved = PdfDocument.Open(output);
        Assert.Equal("OVERRIDDEN", Encoding.ASCII.GetString(ImagePagesDocument.PageImage(saved, 1, "Im0").RawBytes.Span));
        Assert.Equal($"REPLACED-{ImagePagesDocument.Image(0)}", Encoding.ASCII.GetString(ImagePagesDocument.PageImage(saved, 0, "Im0").RawBytes.Span));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExcludedStream_IsNeverOffered(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 1, extraImageOnFirstPage: true));
        var extra = ImagePagesDocument.ExtraImage(pageCount: 1);
        var transform = new RecordingTransform(_ => true, Replace);

        var output = Write(document, PdfOptions.Default with { Optimize = optimize }, transform, excluded: new HashSet<int> { extra });

        Assert.DoesNotContain(extra, transform.Offered);
        Assert.Contains(ImagePagesDocument.Image(0), transform.Offered);
        Assert.DoesNotContain("EXTRA-PIXELS", Encoding.ASCII.GetString(output), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacement_ReferencingANewObject_IsRefused(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 1));
        var transform = new RecordingTransform(
            n => n == ImagePagesDocument.Image(0),
            static (_, original) =>
            {
                var dictionary = Copy(original.Dictionary);
                dictionary.Set(PdfName.Get("Extra"), new PdfReference(new IndirectReference(1, 0))); // the catalog: discovered, but not this stream's
                return new PdfStream(dictionary, original.RawBytes);
            });

        var ex = Assert.Throws<PlumePdfException>(() => Write(document, PdfOptions.Default with { Optimize = optimize }, transform));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Fact]
    public void Save_WithATransform_NeverOffersARemovedPagesStreams()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3));
        document.Pages.RemoveAt(1);
        var transform = new RecordingTransform(_ => true, Replace);
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-transform-{Guid.NewGuid():N}.pdf");
        try
        {
            document.SaveWithWriteHooks(path, PdfOptions.Default, transform, aliases: null);

            Assert.DoesNotContain(ImagePagesDocument.Image(1), transform.Offered);
            Assert.DoesNotContain(ImagePagesDocument.Content(1), transform.Offered);
            Assert.Contains(ImagePagesDocument.Image(0), transform.Offered);
            Assert.Contains(ImagePagesDocument.Image(2), transform.Offered);

            using var saved = PdfDocument.Open(path);
            Assert.Equal(2, saved.Pages.Count);
            Assert.Equal($"REPLACED-{ImagePagesDocument.Image(2)}", Encoding.ASCII.GetString(ImagePagesDocument.PageImage(saved, 1, "Im0").RawBytes.Span));
            CleanupFixtures3b.AssertQpdfClean(File.ReadAllBytes(path), "transformed save over a removed page");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_WithATransform_RefusesLinearize()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 1));
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-transform-{Guid.NewGuid():N}.pdf");

        var ex = Assert.Throws<PlumePdfException>(() =>
            document.SaveWithWriteHooks(path, PdfOptions.Default with { Linearize = true }, new RecordingTransform(_ => true, Replace), aliases: null));

        Assert.Equal("PLUME5010", ex.Code);
        Assert.False(File.Exists(path));
    }

    // Drops the indirect /DecodeParms (a subset of the original's references, which leaves
    // that dictionary written but unreferenced) and swaps the payload for a marker.
    private static PdfStream Replace(int number, PdfStream original)
    {
        var dictionary = Copy(original.Dictionary);
        dictionary.Remove(PdfName.DecodeParms);
        return new PdfStream(dictionary, Encoding.ASCII.GetBytes($"REPLACED-{number}"));
    }

    private static PdfDictionary Copy(PdfDictionary original)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in original)
        {
            copy.Set(key, value);
        }

        return copy;
    }

    private static PdfDictionary ImageDictionary()
    {
        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, PdfName.Get("XObject"));
        dictionary.Set(PdfName.Get("Subtype"), PdfName.Get("Image"));
        dictionary.Set(PdfName.Get("Width"), PdfNumber.Get(1));
        dictionary.Set(PdfName.Get("Height"), PdfNumber.Get(1));
        dictionary.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceGray"));
        dictionary.Set(PdfName.Get("BitsPerComponent"), PdfNumber.Get(8));
        return dictionary;
    }

    internal static byte[] Write(
        PdfDocument document,
        PdfOptions options,
        IStreamWriteTransform? transform,
        IReadOnlySet<int>? excluded = null,
        IReadOnlyDictionary<int, PdfObject>? replacements = null,
        IReadOnlyDictionary<int, int>? aliases = null)
    {
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        using var output = new MemoryStream();
        FullRewriteWriter.Write(output, document.Objects, document.Catalog!.Reference, document.Catalog.Dictionary, pages, options, excluded, replacements, transform, aliases);
        return output.ToArray();
    }

    internal sealed class RecordingTransform(Func<int, bool> handles, Func<int, PdfStream, PdfStream> transform) : IStreamWriteTransform
    {
        public List<int> Offered { get; } = [];

        public List<int> Transformed { get; } = [];

        public bool Handles(int originalNumber)
        {
            Offered.Add(originalNumber);
            return handles(originalNumber);
        }

        public PdfStream Transform(int originalNumber, PdfStream original)
        {
            Transformed.Add(originalNumber);
            return transform(originalNumber, original);
        }
    }
}

/// <summary>
/// A classic-xref document of image pages, written directly from the object/xref grammar:
/// each page draws one 2×2 gray image whose <c>/DecodeParms</c> is an indirect dictionary.
/// With <c>identicalImages</c> every page's image carries the same dictionary and payload
/// (separate objects, the same bytes); with <c>extraImageOnFirstPage</c> the first page also
/// names a second image, <c>/Im1</c>, numbered after every page's objects.
/// </summary>
internal static class ImagePagesDocument
{
    public static int Page(int page) => 3 + (4 * page);

    public static int Content(int page) => Page(page) + 1;

    public static int Image(int page) => Page(page) + 2;

    public static int DecodeParms(int page) => Page(page) + 3;

    public static int ExtraImage(int pageCount) => Page(pageCount);

    public static bool IsImage(int number, int pageCount) => Enumerable.Range(0, pageCount).Any(p => Image(p) == number);

    public static byte[] Build(int pageCount, bool identicalImages = false, bool extraImageOnFirstPage = false)
    {
        var objectCount = Page(pageCount) + (extraImageOnFirstPage ? 1 : 0);
        var offsets = new long[objectCount];
        using var output = new MemoryStream();

        void Write(string text) => output.Write(Encoding.ASCII.GetBytes(text));
        void Object(int number, string body)
        {
            offsets[number] = output.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        void StreamObject(int number, string dictionary, string payload) =>
            Object(number, $"<< {dictionary} /Length {payload.Length} >>\nstream\n{payload}\nendstream");

        Write("%PDF-1.7\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(p => $"{Page(p)} 0 R"));
        Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

        for (var p = 0; p < pageCount; p++)
        {
            var extra = p == 0 && extraImageOnFirstPage ? $" /Im1 {ExtraImage(pageCount)} 0 R" : string.Empty;
            Object(Page(p), $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents {Content(p)} 0 R /Resources << /XObject << /Im0 {Image(p)} 0 R{extra} >> >> >>");
            StreamObject(Content(p), string.Empty, $"q 50 0 0 50 10 10 cm /Im0 Do Q % page {p}");
            var parms = identicalImages ? DecodeParms(0) : DecodeParms(p);
            StreamObject(Image(p), $"/Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /DecodeParms {parms} 0 R", identicalImages ? "PIXELS-SAME" : $"PIXELS-{p}");
            Object(DecodeParms(p), "<< /Columns 2 >>");
        }

        if (extraImageOnFirstPage)
        {
            StreamObject(ExtraImage(pageCount), "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "EXTRA-PIXELS");
        }

        var xref = output.Position;
        var table = new StringBuilder($"xref\n0 {objectCount}\n0000000000 65535 f \n");
        for (var n = 1; n < objectCount; n++)
        {
            table.Append(CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n");
        }

        table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(table.ToString());
        return output.ToArray();
    }

    /// <summary>The image page <paramref name="page"/> of <paramref name="document"/> names <paramref name="name"/>.</summary>
    public static PdfStream PageImage(PdfDocument document, int page, string name) =>
        Assert.IsType<PdfStream>(document.Objects[PageImageReference(document, page, name)]);

    /// <summary>The reference page <paramref name="page"/>'s <c>/XObject</c> resources hold under <paramref name="name"/>.</summary>
    public static IndirectReference PageImageReference(PdfDocument document, int page, string name)
    {
        var resources = Assert.IsType<PdfDictionary>(document.Pages[page].Dictionary[PdfName.Get("Resources")]);
        var xobjects = Assert.IsType<PdfDictionary>(resources[PdfName.Get("XObject")]);
        return Assert.IsType<PdfReference>(xobjects[PdfName.Get(name)]).Target;
    }
}
