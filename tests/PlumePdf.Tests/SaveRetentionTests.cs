using System.Text;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// A full-rewrite <see cref="PdfDocument.Save"/> holds one stream payload at a time: a stream
/// nothing has read before the save is read once, written, and let go, instead of staying in
/// the open document's object cache for its lifetime. Streams the caller has already read
/// (and may have edited in place) are written from that same instance.
/// </summary>
public sealed class SaveRetentionTests : IDisposable
{
    private const int PageCount = 6;
    private const int ImageBytes = 1024 * 1024;

    private readonly string _output = Path.Combine(Path.GetTempPath(), $"plumepdf-retention-{Guid.NewGuid():N}.pdf");

    public void Dispose() => File.Delete(_output);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Save_DoesNotKeepTheStreamsItWrote(bool optimize, bool indirectLength)
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength));
        document.Save(_output, PdfOptions.Default with { Optimize = optimize });

        // Kept in the cache, the image would come back without a fresh copy of its payload.
        var image = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(3), 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stream = Assert.IsType<PdfStream>(document.Objects[image]);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(ImageBytes, stream.RawBytes.Length);
        Assert.True(allocated >= ImageBytes, $"Resolving the image after Save allocated only {allocated} bytes, so Save left it cached.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_ReadsEachStreamPayloadOnce(bool optimize)
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: true));

        var before = GC.GetAllocatedBytesForCurrentThread();
        document.Save(_output, PdfOptions.Default with { Optimize = optimize });
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // One read of a stream allocates its payload twice (the read buffer and the parsed
        // stream's copy); reading each stream both to find its references and again to write
        // it would be four times the payloads.
        const long payloads = (long)PageCount * ImageBytes;
        Assert.True(allocated < (payloads * 5 / 2) + (2 * 1024 * 1024), $"Saving {payloads} bytes of image payload allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Save_WritesEveryImage(bool optimize, bool indirectLength)
    {
        using (var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength)))
        {
            document.Save(_output, PdfOptions.Default with { Optimize = optimize });
        }

        using var saved = PdfDocument.Open(_output);
        for (var i = 0; i < PageCount; i++)
        {
            ImageDocumentFixture.AssertImageIsPage(saved.Pages[i].ExtractImages(), i, ImageBytes);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_IndirectLengths_WritesTheSameFileAsDirectLengths(bool optimize)
    {
        // The writer always writes /Length direct, so the source's length objects are left
        // out; walking a stream by its dictionary must not carry them over as orphans.
        byte[] SavedBytes(bool indirectLength)
        {
            using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength));
            document.Save(_output, PdfOptions.Default with { Optimize = optimize, Deterministic = true });
            return File.ReadAllBytes(_output);
        }

        Assert.Equal(SavedBytes(indirectLength: false), SavedBytes(indirectLength: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Save_WritesAnInPlaceEditToAStreamTheCallerRead(bool optimize)
    {
        var tag = PdfName.Get("PlumeTag");
        var image = new IndirectReference(ImageDocumentFixture.ImageObjectNumber(2), 0);
        using (var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: true)))
        {
            var stream = Assert.IsType<PdfStream>(document.Objects[image]);
            stream.Dictionary.Set(tag, PdfNumber.Get(7));
            document.Objects.MarkDirty(image);
            document.Save(_output, PdfOptions.Default with { Optimize = optimize });
        }

        using var saved = PdfDocument.Open(_output);
        var resources = Assert.IsType<PdfDictionary>(Entry(saved, saved.Pages[2].Dictionary, "Resources"));
        var xObjects = Assert.IsType<PdfDictionary>(Entry(saved, resources, "XObject"));
        var savedImage = Assert.IsType<PdfStream>(Entry(saved, xObjects, "Im0"));
        Assert.True(savedImage.Dictionary.TryGetValue(tag, out var value));
        Assert.Equal(PdfNumber.Get(7), value);
    }

    [Fact]
    public void Save_TwiceThenExtract_ReportsAStreamsParseDeviationOnce()
    {
        using var document = PdfDocument.Open(ImageDocumentFixture.Bytes(PageCount, ImageBytes, indirectLength: false, wrongLengthPage: 4));

        document.Save(_output);
        document.Save(_output);
        ImageDocumentFixture.AssertImageIsPage(document.Pages[4].ExtractImages(), 4, ImageBytes);

        Assert.Single(document.Diagnostics, d => d.Code == "PLUME2019");
    }

    [Fact]
    public void Save_ManySmallObjects_ReadsEachOnce()
    {
        // Walking a non-stream by its first bytes, then resolving it, must not read it twice.
        const int count = 4000;
        using var document = PdfDocument.Open(ManySmallObjectsDocument(count));

        var before = GC.GetAllocatedBytesForCurrentThread();
        document.Save(_output);
        var perObject = (GC.GetAllocatedBytesForCurrentThread() - before) / count;

        // About 11 KB per object either way; reading each object's first bytes twice, or
        // parsing its dictionary once more to see it isn't a stream, is 12.5-17 KB.
        Assert.True(perObject < 12_000, $"Saving allocated {perObject} bytes per small object.");
    }

    [Fact]
    public void Save_StrictLengthCycle_NamesTheStreamBeingRead()
    {
        // Objects 5 and 6 are streams whose /Length points at each other.
        var body = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[7];
        void Add(int number, string text)
        {
            offsets[number] = body.Length;
            body.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 0 obj\n{text}\nendobj\n");
        }

        Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /Resources << /XObject << /X 6 0 R >> >> >>");
        Add(4, "null");
        Add(5, "<< /Length 6 0 R >>\nstream\nq Q\nendstream");
        Add(6, "<< /Length 5 0 R >>\nstream\nq Q\nendstream");
        using var document = PdfDocument.Open(Encoding.ASCII.GetBytes(body.Append(XrefAndTrailer(offsets, body.Length)).ToString()), PdfOptions.Default with { Strict = true });

        var refusal = Assert.ThrowsAny<PlumePdfException>(() => document.Save(_output, PdfOptions.Default with { Strict = true }));

        Assert.Contains("object 5", refusal.Message, StringComparison.Ordinal);
    }

    private static byte[] ManySmallObjectsDocument(int count)
    {
        var body = new StringBuilder("%PDF-1.7\n");
        var offsets = new long[4 + count];
        void Add(int number, string text)
        {
            offsets[number] = body.Length;
            body.Append(System.Globalization.CultureInfo.InvariantCulture, $"{number} 0 obj\n{text}\nendobj\n");
        }

        var members = string.Join(' ', Enumerable.Range(4, count).Select(n => $"{n} 0 R"));
        Add(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        // Under a private key, so only the save's reference walk reaches them (annotations and
        // form fields are read earlier, by the save's signature scan).
        Add(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /PlumeData [{members}] >>");
        for (var n = 4; n < 4 + count; n++)
        {
            Add(n, $"<< /Rect [0 0 {n} {n}] /C [1 0 0] >>");
        }

        return Encoding.ASCII.GetBytes(body.Append(XrefAndTrailer(offsets, body.Length)).ToString());
    }

    private static string XrefAndTrailer(long[] offsets, long xref)
    {
        var table = new StringBuilder().Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {offsets.Length}\n0000000000 65535 f \n");
        for (var n = 1; n < offsets.Length; n++)
        {
            table.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offsets[n]:D10} 00000 n \n");
        }

        return table.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Length} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n").ToString();
    }

    private static PdfObject? Entry(PdfDocument document, PdfDictionary dictionary, string key) =>
        !dictionary.TryGetValue(PdfName.Get(key), out var value) ? null
        : value is PdfReference reference ? document.Objects[reference.Target]
        : value;
}
