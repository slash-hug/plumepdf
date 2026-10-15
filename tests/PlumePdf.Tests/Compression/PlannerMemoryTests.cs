using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using PlumePdf.Documents.Compression;
using PlumePdf.Tests.TestSupport;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// The planning pass reads a document without keeping it: after planning a 200-image document
/// the object cache holds no image or content payload (only the few non-stream objects it had to
/// resolve), and the plan itself costs a bounded number of bytes per image whatever the size of
/// the images' payloads. Runs in the non-parallel collection because it reads the whole
/// process's managed heap.
/// </summary>
[Collection(TimingSensitiveCollection.Name)]
public class PlannerMemoryTests
{
    private const int Pages = 20;
    private const int ImagesPerPage = 10;
    private const int ImageCount = Pages * ImagesPerPage;
    private const int DuplicateCount = 50;
    private const int PayloadBytes = 16 * 1024;
    private const int FirstImage = 100;

    // Every stream (images and page content alike) is padded past the resolver's 4 KiB head
    // read, below which a stream is cached whole by design even on a dictionary-only resolve.
    private const int MinimumStreamBytes = 8 * 1024;

    private static readonly CompressionSettings AllLevers = new(Lossless: true, MaxDpi: 150, JpegQuality: 80);

    [Fact]
    public void PlanningTwoHundredImages_LeavesNoPayloadInTheObjectCache()
    {
        using var document = PdfDocument.Open(Build(indirectLengths: 0));
        var before = document.Objects.CachedObjectCount;

        var plan = CompressionPlanner.Plan(document, AllLevers, PdfOptions.Default, CancellationToken.None);

        Assert.Equal(before, document.Objects.CachedObjectCount);
        Assert.Equal(DuplicateCount, plan.Aliases.Keys.Count(static number => number is >= FirstImage and < FirstImage + ImageCount));
        Assert.Equal(ImageCount - DuplicateCount, plan.Images.Count);
        Assert.All(plan.Images.Values, static image => Assert.Equal(PlannedImageAction.DownsampleAndReencode, image.Action));
    }

    // An indirect /Length is a non-stream object the planner may resolve (and so cache); the
    // cache may grow by those, never by a payload.
    [Fact]
    public void PlanningWithIndirectLengths_CachesAtMostTheLengthObjects()
    {
        const int IndirectLengths = 20;
        using var document = PdfDocument.Open(Build(indirectLengths: IndirectLengths));
        var before = document.Objects.CachedObjectCount;

        var plan = CompressionPlanner.Plan(document, AllLevers, PdfOptions.Default, CancellationToken.None);

        var growth = document.Objects.CachedObjectCount - before;
        Assert.InRange(growth, 0, IndirectLengths);
        Assert.Equal(ImageCount - DuplicateCount, plan.Images.Count);
    }

    [Fact]
    public void PlanRetainedSize_IsBoundedPerImage_NotByThePayloads()
    {
        const long BytesPerImage = 2048;
        const int Rounds = 5;
        using var document = PdfDocument.Open(Build(indirectLengths: 0));

        // The plan's own size: the live heap holding it less the live heap once it is unreachable.
        // A single reading can come out megabytes high (the collector's accounting moves between
        // the two readings), never low, so the smallest of several is the plan's size.
        var retained = long.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            retained = Math.Min(retained, RetainedByOnePlan(document));
        }

        Assert.True(
            retained < ImageCount * BytesPerImage,
            $"The plan for {ImageCount} images ({(long)ImageCount * PayloadBytes} payload bytes) retained {retained} bytes, over {BytesPerImage} per image.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RetainedByOnePlan(PdfDocument document)
    {
        var plan = CompressionPlanner.Plan(document, AllLevers, PdfOptions.Default, CancellationToken.None);
        var holding = GC.GetTotalMemory(forceFullCollection: true);
        Assert.Equal(ImageCount - DuplicateCount, plan.Images.Count);
        GC.KeepAlive(plan);
        return holding - GC.GetTotalMemory(forceFullCollection: true);
    }

    // Twenty pages of ten 600x600 RGB images each, every one drawn on a one-inch square (600
    // DPI). The last fifty images are byte-identical copies of the first fifty; the rest have
    // distinct payloads. The first 'indirectLengths' images give their /Length indirectly.
    private static byte[] Build(int indirectLengths)
    {
        var pdf = new ScanTestPdf { MinimumStreamBytes = MinimumStreamBytes };
        for (var i = 0; i < ImageCount; i++)
        {
            var number = FirstImage + i;
            var payload = Payload(i < ImageCount - DuplicateCount ? i : i - (ImageCount - DuplicateCount));
            const string Dictionary = "/Type /XObject /Subtype /Image /Width 600 /Height 600 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode";
            if (i < indirectLengths)
            {
                var lengthNumber = FirstImage + ImageCount + i;
                pdf.Object(lengthNumber, payload.Length.ToString(CultureInfo.InvariantCulture));
                pdf.Object(number, $"<< {Dictionary} /Length {lengthNumber} 0 R >>\nstream\n{Encoding.Latin1.GetString(payload)}\nendstream");
            }
            else
            {
                pdf.Stream(number, Dictionary, payload);
            }
        }

        for (var page = 0; page < Pages; page++)
        {
            var content = new StringBuilder();
            var xobjects = new StringBuilder();
            for (var j = 0; j < ImagesPerPage; j++)
            {
                content.Append(CultureInfo.InvariantCulture, $"q 72 0 0 72 {j * 50} 0 cm /Im{j} Do Q\n");
                xobjects.Append(CultureInfo.InvariantCulture, $"/Im{j} {FirstImage + (page * ImagesPerPage) + j} 0 R ");
            }

            pdf.Page(1000 + (page * 2), content.ToString(), xobjects: xobjects.ToString());
        }

        return pdf.Build();
    }

    // Distinct per seed in its first bytes, so payloads of the same length still differ.
    private static byte[] Payload(int seed)
    {
        var payload = new byte[PayloadBytes];
        BitConverter.TryWriteBytes(payload, seed);
        payload[^1] = (byte)seed;
        return payload;
    }
}
