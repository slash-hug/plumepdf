using System.Text;
using PlumePdf.Documents.Forms;
using PlumePdf.Documents.Forms.Appearances;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Raster.Annotations;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// <see cref="AnnotationReader"/>'s universal <c>/Annots</c> walk, run end-to-end
/// through <see cref="Rasterizer.Rasterize"/>. Every assertion here reads back painted pixels
/// (the KEYSTONE anti-vacuity pattern established during Phase 8 — a feature that
/// merges tested-green but never actually paints ink is not done) rather than only inspecting the
/// display list, so a regression that captures an annotation structurally but forgets to hand it
/// to the interpreter would fail loudly here.
/// </summary>
public class AnnotationRenderingTests
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

    [Fact]
    public void RenderAnnotations_VisibleAnnotationWithAp_PaintsItsAppearanceInk()
    {
        var frame = RasterizeAnnotationFixture(printIntent: false);

        // Annotation 6 (/Rect [5 5 45 45], has /AP painting solid blue) maps to device pixels
        // x in [5,45], y in [55,95] (PDF y-up -> device y-down). Center ~ (25, 75).
        AssertPixel(frame, 25, 75, expectedBlue: true);
    }

    [Fact]
    public void RenderAnnotations_NonWidgetAnnotationWithNoAp_SkipsPaintAndRecordsPlume7732()
    {
        var diagnostics = new DiagnosticCollection();
        var frame = RasterizeAnnotationFixture(printIntent: false, diagnostics: diagnostics);

        // Annotation 7 (/Rect [55 5 95 45], no /AP) -> device x[55,95] y[55,95], center ~ (75, 75).
        AssertPixel(frame, 75, 75, expectedBlue: false);

        var diagnostic = Assert.Single(diagnostics, d => d.Code == "PLUME7732");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public void RenderAnnotations_HiddenAnnotation_NeverPaintsAndNeverDiagnoses()
    {
        var diagnostics = new DiagnosticCollection();
        var frame = RasterizeAnnotationFixture(printIntent: false, diagnostics: diagnostics);

        // Annotation 8 (/Rect [5 55 45 95], /F 2 = Hidden, has /AP) -> device x[5,45] y[5,45], center ~ (25, 25).
        AssertPixel(frame, 25, 25, expectedBlue: false);

        // The fixture's annotation 7 (no /AP) legitimately still fires its own PLUME7732 — the
        // point here is that the Hidden annotation 8 contributes no *second* diagnostic of its
        // own despite also never painting (Hidden honored is never itself diagnosed).
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7732", diagnostic.Code);
    }

    [Fact]
    public void RenderAnnotations_VNoApWidgetWithInjectedResolver_SynthesizesAndPaintsInk()
    {
        var path = WriteTempFile(BuildWidgetFixture());
        using var document = PdfDocument.Open(path);

        var page = document.Objects[new IndirectReference(4, 0)] as PdfDictionary ?? throw new InvalidOperationException();
        var acroForm = document.Objects[new IndirectReference(3, 0)] as PdfDictionary ?? throw new InvalidOperationException();
        var dr = acroForm[PdfName.DR] as PdfDictionary ?? throw new InvalidOperationException();
        var annotations = page[PdfName.Annots] as PdfArray;

        WidgetAppearanceSynthesizer.Resolver resolver = (widget, diagnostics) =>
        {
            var scratch = ScratchObjectRegistry.CreateFor(document.Objects, nextObjectNumber: 1000);
            var generator = new AppearanceGenerator(scratch);
            var value = new FieldValue(FormFieldKind.Text, "Hi", null);
            var reference = generator.GenerateAppearance(value, widget, "/Helv 12 Tf 0 g", dr, scratch, PdfOptions.Default, diagnostics);
            return scratch[reference] as PdfStream;
        };

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

        // /MK /BG [0 0 1] paints the synthesized appearance's whole background blue -> the
        // widget's /Rect [10 10 90 90] fills device x[10,90] y[10,90].
        AssertPixel(frame, 50, 50, expectedBlue: true);
    }

    /// <summary>
    /// An off-state widget whose <c>/AS</c> names a state absent from <c>/AP /N</c>
    /// (the overwhelmingly common <c>/N &lt;&lt; /Yes … &gt;&gt; /AS /Off</c> checkbox shape) must paint
    /// nothing — it previously fell into the first-entry lenience meant for a missing <c>/AS</c>
    /// and painted its on-appearance, so every checkbox in a form rendered checked. The rule is
    /// PDFium's (<c>CPDF_Annot</c> <c>GetAnnotAPInternal</c>): <c>/N[/AS]</c> when <c>/AS</c> is
    /// present; otherwise <c>/N[/V]</c> (own or inherited) when that state exists, else
    /// <c>/N[/Off]</c>; never the first entry. A widget's authored blank is never handed to the
    /// widget-appearance synthesizer (D is the control that synthesis still runs for a
    /// <c>/V</c>-no-<c>/AP</c> widget); a non-widget has no off-state, so its unresolvable state
    /// still records <c>PLUME7732</c>.
    /// </summary>
    [Fact]
    public void RenderAnnotations_AppearanceStateSelection_FollowsAsThenValueThenOff_NeverFirstEntry()
    {
        var path = WriteTempFile(BuildAppearanceStateFixture());
        using var document = PdfDocument.Open(path);
        var page = document.Objects[new IndirectReference(4, 0)] as PdfDictionary ?? throw new InvalidOperationException();
        var annotations = page[PdfName.Annots] as PdfArray;
        var blueForm = document.Objects[new IndirectReference(14, 0)] as PdfStream ?? throw new InvalidOperationException();

        // A resolver that would paint blue for ANY widget it is offered — so a widget that stays
        // white below was never offered to synthesis at all.
        WidgetAppearanceSynthesizer.Resolver resolver = (_, _) => blueForm;
        var diagnostics = new DiagnosticCollection();
        var annotationOptions = new AnnotationRenderOptions(annotations, PrintIntent: false, NeedAppearances: false, WidgetAppearanceResolver: resolver);

        var frame = Rasterizer.Rasterize(
            ReadOnlyMemory<byte>.Empty,
            resources: null,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            document.Objects,
            annotationOptions: annotationOptions);

        AssertPixel(frame, 15, 15, expectedBlue: false); // A: /N << /Yes >> /AS /Off /V /Yes — authored blank, not synthesized (this off-state shape).
        AssertPixel(frame, 45, 15, expectedBlue: true);  // B: /N << /Yes >> /AS /Yes — the named state paints.
        AssertPixel(frame, 75, 15, expectedBlue: true);  // C: no /AS, /V /Yes — PDFium's /V rule.
        AssertPixel(frame, 15, 45, expectedBlue: true);  // D: no /AP at all, /V /Yes — synthesis control.
        AssertPixel(frame, 45, 45, expectedBlue: false); // E: /N << /Yes >> with neither /AS nor /V — Off, not the first entry.
        AssertPixel(frame, 75, 45, expectedBlue: true);  // F: no /AS, /V inherited from /Parent.
        AssertPixel(frame, 15, 75, expectedBlue: false); // G: /Square with /N << /A >> /AS /Zzz — nothing to paint; a non-widget has no off-state, so PLUME7732.
        AssertRedPixel(frame, 45, 75);                     // H: neither /AS nor /V, /N has a visible (red) /Off — /Off paints, not the first entry (/Yes, blue).
        AssertRedPixel(frame, 75, 75);                     // I: radio kid, no /AS, parent /V names a SIBLING's state — falls to this kid's red /Off.
        AssertPixel(frame, 45, 15, expectedBlue: true);  // B again: unaffected by the extra widgets.
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("PLUME7732", diagnostic.Code);
    }

    [Fact]
    public void RenderAnnotations_NoAnnotationOptions_IsANoOp()
    {
        var frame = Rasterizer.Rasterize(
            ReadOnlyMemory<byte>.Empty,
            resources: null,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 10,
            pixelHeight: 10,
            PdfOptions.Default, RasterPaintContext.Default);

        Assert.Equal(10, frame.Width);
        AssertPixel(frame, 5, 5, expectedBlue: false); // Plain white background, nothing painted.
    }

    [Fact]
    public void RenderAnnotations_AnnotsArrayExceedingCap_ThrowsPlume7743()
    {
        // The per-page /Annots-array-size cap (reusing PdfOptions.MaxWidgetsPerPage, the
        // same limit Documents.Forms.WidgetAnnotationReader applies to widgets specifically) is a
        // resource-limit guard against a hostile or pathologically large page — thrown
        // unconditionally, like PLUME7500-7503, not a per-annotation content degradation. Its own
        // code (PLUME7743), never the recoverable per-annotation PLUME7731.
        var entries = new List<PdfObject>();
        for (var i = 0; i < 3; i++)
        {
            entries.Add(new PdfDictionary()); // Malformed (non-reference) entries are enough to reach the count cap before any resolution work.
        }

        var annotations = new PdfArray(entries);
        var limited = PdfOptions.Default with { MaxWidgetsPerPage = 2 };
        var annotationOptions = new AnnotationRenderOptions(annotations, PrintIntent: false, NeedAppearances: false, WidgetAppearanceResolver: null);

        var ex = Assert.Throws<PlumePdfException>(() => Rasterizer.Rasterize(
            ReadOnlyMemory<byte>.Empty,
            resources: null,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 10,
            pixelHeight: 10,
            limited, RasterPaintContext.Default,
            annotationOptions: annotationOptions));

        Assert.Equal("PLUME7743", ex.Code);
    }

    private static RasterImageFrame RasterizeAnnotationFixture(bool printIntent, DiagnosticCollection? diagnostics = null)
    {
        var path = WriteTempFile(BuildAnnotationFixture());
        using var document = PdfDocument.Open(path);
        var page = document.Objects[new IndirectReference(4, 0)] as PdfDictionary ?? throw new InvalidOperationException();
        var annotations = page[PdfName.Annots] as PdfArray;

        var annotationOptions = new AnnotationRenderOptions(annotations, printIntent, NeedAppearances: false, WidgetAppearanceResolver: null);

        return Rasterizer.Rasterize(
            ReadOnlyMemory<byte>.Empty,
            resources: null,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            document.Objects,
            annotationOptions: annotationOptions);
    }

    private static void AssertPixel(RasterImageFrame frame, int x, int y, bool expectedBlue)
    {
        var offset = ((y * frame.Width) + x) * 4;
        var span = frame.Pixels.Span;
        var r = span[offset];
        var g = span[offset + 1];
        var b = span[offset + 2];

        if (expectedBlue)
        {
            Assert.True(r < 40 && g < 40 && b > 200, $"expected solid blue ink at ({x},{y}), got rgb({r},{g},{b})");
        }
        else
        {
            Assert.True(r > 200 && g > 200 && b > 200, $"expected unpainted white background at ({x},{y}), got rgb({r},{g},{b})");
        }
    }

    private static void AssertRedPixel(RasterImageFrame frame, int x, int y)
    {
        var offset = ((y * frame.Width) + x) * 4;
        var span = frame.Pixels.Span;
        var (r, g, b) = (span[offset], span[offset + 1], span[offset + 2]);
        Assert.True(r > 200 && g < 40 && b < 40, $"expected solid red ink at ({x},{y}), got rgb({r},{g},{b})");
    }

    // Objects: 1 Catalog, 2 Pages, 4 Page (Annots [6 7 8]), 5 empty content, 6 visible /AP
    // annotation, 7 no-/AP annotation, 8 Hidden /AP annotation, 9 the shared blue-fill form.
    private static byte[] BuildAnnotationFixture()
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

        const int totalObjects = 9;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< >>"); // Unused object number, kept for numbering symmetry with other fixtures in this file.
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 5 0 R /Annots [6 0 R 7 0 R 8 0 R] >>");
        WriteStream(5, string.Empty, string.Empty);
        WriteObject(6, "<< /Type /Annot /Subtype /Square /Rect [5 5 45 45] /AP << /N 9 0 R >> >>");
        WriteObject(7, "<< /Type /Annot /Subtype /Square /Rect [55 5 95 45] >>");
        WriteObject(8, "<< /Type /Annot /Subtype /Square /Rect [5 55 45 95] /F 2 /AP << /N 9 0 R >> >>");
        WriteStream(9, "/Type /XObject /Subtype /Form /BBox [0 0 40 40]", "0 0 1 rg 0 0 40 40 re f");

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

    // Objects: 1 Catalog/AcroForm, 2 Pages, 3 AcroForm, 4 Page (Annots [7]), 5 empty content,
    // 6 /DR font, 7 the /V-no-/AP widget with a blue /MK background.
    private static byte[] BuildWidgetFixture()
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
        WriteObject(7, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [10 10 90 90] /MK << /BG [0 0 1] >> /V (Hi) >>");

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

    // Objects: 1 Catalog/AcroForm, 2 Pages, 3 AcroForm, 4 Page (Annots [6..10 12 13 16 18]), 5
    // empty content, 6-10 checkbox widgets A-E, 11 a parent field carrying /V for kid 12 (F), 13 a
    // /Square with a states dictionary (G), 14 the shared 20x20 blue-fill form, 15 an empty form,
    // 16 widget H (visible red /Off, neither /AS nor /V), 17 a radio parent whose /V names a
    // sibling state, 18 its kid I, 19 the 20x20 red-fill form.
    private static byte[] BuildAppearanceStateFixture()
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

        const int totalObjects = 19;

        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [4 0 R] /Count 1 >>");
        WriteObject(3, "<< /Fields [6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 11 0 R 16 0 R 17 0 R] >>");
        WriteObject(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 5 0 R /Annots [6 0 R 7 0 R 8 0 R 9 0 R 10 0 R 12 0 R 13 0 R 16 0 R 18 0 R] >>");
        WriteStream(5, string.Empty, string.Empty);
        // A (device center 15,15): this off-state shape — /AS /Off names a state /N does not carry. Blank.
        WriteObject(6, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (A) /Rect [5 75 25 95] /AP << /N << /Yes 14 0 R >> >> /AS /Off /V /Yes >>");
        // B (45,15): /AS names the on-state. Paints.
        WriteObject(7, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (B) /Rect [35 75 55 95] /AP << /N << /Yes 14 0 R /Off 15 0 R >> >> /AS /Yes /V /Yes >>");
        // C (75,15): no /AS (malformed per §12.5.5); /V names a present state. Paints (PDFium's rule).
        WriteObject(8, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (C) /Rect [65 75 85 95] /AP << /N << /Off 15 0 R /Yes 14 0 R >> >> /V /Yes >>");
        // D (15,45): no /AP at all, /V /Yes — the synthesizer control (resolver paints blue).
        WriteObject(9, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (D) /Rect [5 45 25 65] /V /Yes >>");
        // E (45,45): no /AS and no /V; /N holds only the on-state. Off → nothing (was: first entry).
        WriteObject(10, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (E) /Rect [35 45 55 65] /AP << /N << /Yes 14 0 R >> >> >>");
        // F (75,45): kid with no /AS whose /V is inherited from its parent field. Paints.
        WriteObject(11, "<< /FT /Btn /T (F) /V /On /Kids [12 0 R] >>");
        WriteObject(12, "<< /Type /Annot /Subtype /Widget /Parent 11 0 R /Rect [65 45 85 65] /AP << /N << /On 14 0 R /Off 15 0 R >> >> >>");
        // G (15,75): a non-widget whose /AS names an absent state — authored blank, no PLUME7732.
        WriteObject(13, "<< /Type /Annot /Subtype /Square /Rect [5 15 25 35] /AP << /N << /A 14 0 R >> >> /AS /Zzz >>");
        WriteStream(14, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "0 0 1 rg 0 0 20 20 re f");
        WriteStream(15, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", string.Empty);
        // H (45,75): neither /AS nor /V; /Yes is the FIRST entry (blue) but /Off (red) must paint.
        WriteObject(16, "<< /Type /Annot /Subtype /Widget /FT /Btn /T (H) /Rect [35 15 55 35] /AP << /N << /Yes 14 0 R /Off 19 0 R >> >> >>");
        // I (75,75): radio kid with no /AS; the parent's /V names a sibling's state, not this kid's → /Off (red).
        WriteObject(17, "<< /FT /Btn /Ff 32768 /T (I) /V /Choice1 /Kids [18 0 R] >>");
        WriteObject(18, "<< /Type /Annot /Subtype /Widget /Parent 17 0 R /Rect [65 15 85 35] /AP << /N << /Choice2 14 0 R /Off 19 0 R >> >> >>");
        WriteStream(19, "/Type /XObject /Subtype /Form /BBox [0 0 20 20]", "1 0 0 rg 0 0 20 20 re f");

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
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-annots-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
