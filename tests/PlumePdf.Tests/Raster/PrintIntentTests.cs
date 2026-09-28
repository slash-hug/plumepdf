using System.Text;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Raster.Annotations;
using PlumePdf.Raster.OptionalContent;
using Xunit;
using static PlumePdf.Raster.AnnotationFlagMatrix;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// The Hidden/NoView/Print flag-matrix truth table (<see cref="AnnotationFlagMatrix"/>)
/// and an OCG's <c>/Usage</c> <c>/Print</c> override, exercised both as unit-level truth
/// tables and end-to-end through <see cref="Rasterizer.Rasterize"/>'s annotation pass.
/// </summary>
public class PrintIntentTests
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

    // AnnotationFlags is internal, so InlineData/the test method signature use the bitfield's
    // plain int form (xUnit's [Theory] discovery requires a public method, which cannot expose an
    // internal parameter type per CS0051) — cast at the call site instead.
    [Theory]
    // flags, printIntent, expected
    [InlineData((int)AnnotationFlags.None, false, true)] // View, no flags: visible.
    [InlineData((int)AnnotationFlags.None, true, false)] // Print, no /Print flag: not printed.
    [InlineData((int)AnnotationFlags.Print, true, true)] // Print, /Print set: printed.
    [InlineData((int)AnnotationFlags.Print, false, true)] // View, /Print set (irrelevant to view): still visible.
    [InlineData((int)AnnotationFlags.NoView, false, false)] // View, /NoView set: hidden on screen.
    [InlineData((int)AnnotationFlags.NoView, true, false)] // Print, /NoView set but no /Print: not printed either (Print requires /Print).
    [InlineData((int)(AnnotationFlags.NoView | AnnotationFlags.Print), true, true)] // Print, /NoView + /Print: a printer-only annotation prints.
    [InlineData((int)(AnnotationFlags.NoView | AnnotationFlags.Print), false, false)] // View, /NoView + /Print: never shown on screen.
    [InlineData((int)AnnotationFlags.Hidden, false, false)] // Hidden always wins, view.
    [InlineData((int)AnnotationFlags.Hidden, true, false)] // Hidden always wins, print — even with /Print set.
    [InlineData((int)(AnnotationFlags.Hidden | AnnotationFlags.Print), true, false)]
    public void ShouldRender_MatchesTheFlagMatrixTruthTable(int flags, bool printIntent, bool expected)
    {
        Assert.Equal(expected, ShouldRender((AnnotationFlags)flags, printIntent));
    }

    [Fact]
    public void ReadFlags_NoFEntry_IsNone()
    {
        Assert.Equal(AnnotationFlags.None, ReadFlags(new PdfDictionary()));
    }

    [Fact]
    public void ReadFlags_ReadsTheBitfield()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("F"), PdfNumber.Get(2 + 32)); // Hidden (2) + NoView (32).
        var flags = ReadFlags(dict);
        Assert.Equal(AnnotationFlags.Hidden | AnnotationFlags.NoView, flags);
    }

    [Fact]
    public void OptionalContentConfig_PrintUsageOverridesOffToOnUnderPrintIntent()
    {
        var ocgRef = new IndirectReference(10, 0);
        var ocg = new PdfDictionary();
        var usage = new PdfDictionary();
        var printUsage = new PdfDictionary();
        printUsage.Set(PdfName.Get("PrintState"), PdfName.Get("ON"));
        usage.Set(PdfName.Get("Print"), printUsage);
        ocg.Set(PdfName.Get("Usage"), usage);

        var objects = new ObjectRegistry(new SingleSource(ocgRef.Number, ocg));
        var ocProperties = new PdfDictionary();
        var d = new PdfDictionary();
        d.Set(PdfName.Get("OFF"), new PdfArray([new PdfReference(ocgRef)])); // Off by default (view) — ISO 32000-1 Table 96's literal uppercase /OFF key, not PdfName.Off's widget-/AS-state "Off".
        ocProperties.Set(PdfName.Get("D"), d);

        var config = OptionalContentConfig.Parse(ocProperties, objects, null);
        var ocgValue = new PdfReference(ocgRef);

        Assert.False(config.IsVisible(ocgValue, objects, printIntent: false)); // View: off by default.
        Assert.True(config.IsVisible(ocgValue, objects, printIntent: true)); // Print: /Usage /Print /PrintState ON overrides it.
    }

    [Fact]
    public void OptionalContentConfig_PrintUsageOverridesOnToOffUnderPrintIntent()
    {
        var ocgRef = new IndirectReference(10, 0);
        var ocg = new PdfDictionary();
        var usage = new PdfDictionary();
        var printUsage = new PdfDictionary();
        printUsage.Set(PdfName.Get("PrintState"), PdfName.Get("OFF")); // ISO 32000-1 Table 101: /PrintState's OFF value is the literal uppercase /OFF, not PdfName.Off's widget-/AS-state "Off".
        usage.Set(PdfName.Get("Print"), printUsage);
        ocg.Set(PdfName.Get("Usage"), usage);

        var objects = new ObjectRegistry(new SingleSource(ocgRef.Number, ocg));
        var config = OptionalContentConfig.Parse(EmptyOcProperties(), objects, null); // On by default (no /OFF entries).
        var ocgValue = new PdfReference(ocgRef);

        Assert.True(config.IsVisible(ocgValue, objects, printIntent: false)); // View: on by default.
        Assert.False(config.IsVisible(ocgValue, objects, printIntent: true)); // Print: /Usage /Print /PrintState Off overrides it.
    }

    [Fact]
    public void RenderAnnotations_PrintOnlyAnnotation_VisibleUnderPrintNotView()
    {
        var path = WriteTempFile(BuildPrintOnlyFixture());
        using var document = PdfDocument.Open(path);
        var page = document.Objects[new IndirectReference(4, 0)] as PdfDictionary ?? throw new InvalidOperationException();
        var annotations = page[PdfName.Annots] as PdfArray;

        var viewOptions = new AnnotationRenderOptions(annotations, PrintIntent: false, NeedAppearances: false, WidgetAppearanceResolver: null);
        var viewFrame = Rasterizer.Rasterize(ReadOnlyMemory<byte>.Empty, null, 100, 100, 100, 100, PdfOptions.Default, RasterPaintContext.Default, null, document.Objects, annotationOptions: viewOptions);
        AssertBlue(viewFrame, 50, 50, expected: false); // /NoView set: never shown on screen, regardless of /Print.

        var printOptions = new AnnotationRenderOptions(annotations, PrintIntent: true, NeedAppearances: false, WidgetAppearanceResolver: null);
        var printFrame = Rasterizer.Rasterize(ReadOnlyMemory<byte>.Empty, null, 100, 100, 100, 100, PdfOptions.Default, RasterPaintContext.Default, null, document.Objects, annotationOptions: printOptions);
        AssertBlue(printFrame, 50, 50, expected: true);
    }

    private static void AssertBlue(RasterImageFrame frame, int x, int y, bool expected)
    {
        var offset = ((y * frame.Width) + x) * 4;
        var span = frame.Pixels.Span;
        if (expected)
        {
            Assert.True(span[offset] < 40 && span[offset + 1] < 40 && span[offset + 2] > 200);
        }
        else
        {
            Assert.True(span[offset] > 200 && span[offset + 1] > 200 && span[offset + 2] > 200);
        }
    }

    // A printer-only annotation: /F 36 = NoView (32) + Print (4) together (ISO 32000-1 Table 165's
    // own worked example) — invisible on screen, visible only when printed.
    private static byte[] BuildPrintOnlyFixture()
    {
        var buffer = new List<byte>();
        buffer.AddRange(Header);
        var offsets = new Dictionary<int, int>();

        void WriteObject(int num, string body)
        {
            offsets[num] = buffer.Count;
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        void WriteStream(int num, string dictBody, string content)
        {
            offsets[num] = buffer.Count;
            var bytes = Encoding.ASCII.GetBytes(content);
            buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< {dictBody} /Length {bytes.Length} >>\nstream\n"));
            buffer.AddRange(bytes);
            buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        const int totalObjects = 7;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 5 0 R /Annots [6 0 R] >>");
        WriteStream(5, string.Empty, string.Empty);
        WriteObject(6, "<< /Type /Annot /Subtype /Square /Rect [10 10 90 90] /F 36 /AP << /N 7 0 R >> >>");
        WriteStream(7, "/Type /XObject /Subtype /Form /BBox [0 0 80 80]", "0 0 1 rg 0 0 80 80 re f");

        var xrefOffset = buffer.Count;
        buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {totalObjects + 1}\n0000000000 65535 f \n"));
        for (var n = 1; n <= totalObjects; n++)
        {
            buffer.AddRange(Encoding.ASCII.GetBytes($"{offsets[n]:D10} 00000 n \n"));
        }

        buffer.AddRange(Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R /ID [<00112233445566778899aabbccddeeff> <00112233445566778899aabbccddeeff>] >>\nstartxref\n{xrefOffset}\n%%EOF"));

        return [.. buffer];
    }

    private static PdfDictionary EmptyOcProperties()
    {
        var ocProperties = new PdfDictionary();
        ocProperties.Set(PdfName.Get("D"), new PdfDictionary());
        return ocProperties;
    }

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-printintent-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private sealed class SingleSource(int number, PdfObject value) : IObjectSource
    {
        public PdfDictionary Trailer => new();

        public PdfObject Resolve(IndirectReference reference) =>
            reference.Number == number ? value : PdfNull.Instance;
    }
}
