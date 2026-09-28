using System.Text;
using PlumePdf.Documents.Forms;
using PlumePdf.Documents.Forms.Appearances;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Raster.Annotations;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// Asserts that rasterizing a
/// <c>/V</c>-no-<c>/AP</c> widget — which requires render-time appearance synthesis
/// (<see cref="WidgetAppearanceSynthesizer"/>) — never mutates the real document's
/// <see cref="ObjectRegistry"/>. The design risk this guards against: a synthesis implementation
/// that accidentally calls the real <c>document.Objects.AllocateNumber()</c>/<c>RegisterNew()</c>
/// instead of routing every allocation through a scratch registry
/// (<see cref="ScratchObjectRegistry"/>) that discards its writes.
/// </summary>
/// <remarks>
/// <see cref="ObjectRegistry"/> exposes no public "next fresh number"/"free list" accessor to
/// snapshot directly (by design — see <c>docs/architecture.md</c>'s "Threading &amp; mutation"),
/// so this test observes the same
/// invariant indirectly but just as decisively: <see cref="ObjectRegistry.AllocateNumber"/>
/// mutates the registry's private counter on every call and nothing else, so calling it once
/// immediately before and once immediately after <c>Rasterizer.Rasterize</c> must return
/// consecutive numbers — any allocation Rasterize itself performed against the <em>real</em>
/// registry would widen that gap. Paired with the already-<see langword="internal"/>
/// <see cref="ObjectRegistry.DirtyObjects"/> (the write-marking half) and a direct re-read of the
/// widget dictionary (still lacking <c>/AP</c> afterward), this exercises the exact failure mode
/// using only the registry's existing, real surface — no new accessor needed to be added.
/// </remarks>
public class RasterReadOnlyInvariantTests
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

    [Fact]
    public void Rasterize_WithVNoApWidgetSynthesis_NeverMutatesTheRealObjectRegistry()
    {
        var path = WriteTempFile(BuildVNoApWidgetDocument());
        using var document = PdfDocument.Open(path);

        var page = document.Objects[new IndirectReference(4, 0)] as PdfDictionary
            ?? throw new InvalidOperationException("fixture page missing");
        var widgetRef = new IndirectReference(7, 0);
        var widget = document.Objects[widgetRef] as PdfDictionary
            ?? throw new InvalidOperationException("fixture widget missing");
        var acroForm = document.Objects[new IndirectReference(3, 0)] as PdfDictionary
            ?? throw new InvalidOperationException("fixture AcroForm missing");
        var dr = acroForm[PdfName.DR] as PdfDictionary
            ?? throw new InvalidOperationException("fixture /DR missing");

        Assert.False(widget.ContainsKey(PdfName.AP), "fixture precondition: the widget must start with no /AP");

        var beforeDirty = document.Objects.DirtyObjects.Count;
        var beforeProbe = document.Objects.AllocateNumber();

        WidgetAppearanceSynthesizer.Resolver resolver = (w, diagnostics) =>
        {
            // A genuinely real synthesis path: a fresh scratch registry per call, seeded
            // far past anything this tiny fixture could ever use, so it can never collide with a
            // real object number regardless of how many objects the fixture grows to.
            var scratch = ScratchObjectRegistry.CreateFor(document.Objects, nextObjectNumber: 1000);
            var generator = new AppearanceGenerator(scratch);
            var value = new FieldValue(FormFieldKind.Text, "Hello", null);
            var reference = generator.GenerateAppearance(value, w, "/Helv 12 Tf 0 g", dr, scratch, PdfOptions.Default, diagnostics);
            return scratch[reference] as PdfStream;
        };

        var annotations = page[PdfName.Annots] as PdfArray;
        var annotationOptions = new AnnotationRenderOptions(annotations, PrintIntent: false, NeedAppearances: false, resolver);

        var frame = Rasterizer.Rasterize(
            ReadOnlyMemory<byte>.Empty,
            resources: null,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics: null,
            document.Objects,
            annotationOptions: annotationOptions);

        Assert.Equal(100, frame.Width); // sanity: the call actually ran the annotation pass, not a no-op.

        var afterProbe = document.Objects.AllocateNumber();
        Assert.Equal(beforeProbe.Number + 1, afterProbe.Number); // No allocation happened against the real registry during Rasterize.

        Assert.Equal(beforeDirty, document.Objects.DirtyObjects.Count); // Nothing was marked dirty either.

        var widgetAfter = document.Objects[widgetRef] as PdfDictionary;
        Assert.NotNull(widgetAfter);
        Assert.False(widgetAfter!.ContainsKey(PdfName.AP), "the real widget dictionary must still have no /AP after Rasterize — synthesis is render-time-only");
    }

    private static byte[] BuildVNoApWidgetDocument()
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

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [7 0 R] /DR << /Font << /Helv 6 0 R >> >> >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /Helv 6 0 R >> >> /Contents 5 0 R /Annots [7 0 R] >>");
        WriteStream(5, string.Empty, string.Empty);
        WriteObject(6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        WriteObject(7, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [10 10 90 90] /V (Hello) >>");

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

    private static string WriteTempFile(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-invariant-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
