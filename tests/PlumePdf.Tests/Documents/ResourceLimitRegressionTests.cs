using System.Text;
using PlumePdf.Compose;
using PlumePdf.Documents;
using PlumePdf.Elements;
using Xunit;

namespace PlumePdf.Tests.Documents;

/// <summary>
/// Regression coverage for two bugs:
/// (1) <c>TextExtractor.HandleDo</c>'s Form XObject fan-out was unbounded — nesting depth was
/// guarded, but total work was not, so <c>fanout^depth</c> total <c>Do</c> invocations could
/// come from a tiny, shallow-looking crafted document; (2) <see cref="PdfOptions"/>'s
/// <c>MaxContentStreamOperators</c>/<c>MaxLettersPerPage</c>/<c>MaxXObjectNestingDepth</c> were
/// documented and public but unreachable from either extraction path, which were reading their
/// own hardcoded/shadowing defaults instead.
/// </summary>
public class ResourceLimitRegressionTests
{
    [Fact]
    public void ExtractText_FanOutBomb_TripsCumulativeOperatorGuardRatherThanBurningCpu()
    {
        // 14 levels deep, fan-out 3 per level (mirrors a crafted worst-case example) -
        // well under the default MaxXObjectNestingDepth (32), so only a cumulative work bound
        // (not the depth guard) can catch this. Every non-leaf level's content is just
        // "fanOut" copies of a single "Do" operator, so ContentStreamReader's own per-call
        // operator ceiling never trips on any single stream either - only summing across the
        // whole page's nested invocations does. A tightened MaxContentStreamOperators keeps
        // this test fast and deterministic regardless of the default's exact magnitude, while
        // the full fanOut^depth graph (roughly 2.4M total Do invocations at these parameters)
        // stays comfortably unreachable within that tightened budget - proving the guard trips
        // long before the graph is fully walked, not merely eventually.
        var bytes = BuildFanOutDocument(depth: 14, fanOut: 3);

        var options = PdfOptions.Default with { MaxContentStreamOperators = 1_000 };
        using var document = PdfDocument.Open(bytes, options);
        var ex = Assert.Throws<PlumePdfException>(() => document.Pages[0].ExtractText());
        Assert.Equal("PLUME6028", ex.Code);
    }

    [Fact]
    public void ExtractText_SameFormInvokedTwiceFromOnePage_ExtractsBothOccurrencesText()
    {
        // Legitimate reuse (e.g. a shared header/footer XObject painted at two different
        // positions) must keep working - the fan-out guard must not become a "visited once"
        // dedup that would silently drop real repeated content.
        var bytes = BuildTwiceInvokedFormDocument();

        using var document = PdfDocument.Open(bytes);
        var extracted = document.Pages[0].ExtractText();

        Assert.Equal(2, extracted.Words.Count(w => w.Text == "Reused"));
    }

    [Fact]
    public void ExtractText_TightenedPdfOptionsMaxLettersPerPage_ThrowsPlume6021WithNoExtractionOptionsOverride()
    {
        // Previously TextExtractor ignored PdfOptions.MaxLettersPerPage entirely (only its own
        // internal default, or a PdfTextExtractionOptions override, had any effect) - tightening
        // it on the document-level PdfOptions, with no per-call override, must now be honored.
        var options = PdfOptions.Default with { MaxLettersPerPage = 3 };
        using var document = PdfDocument.Compose(page =>
        {
            page.Size(PageSize.A4).Margin(40);
            page.Content().Text("This sentence has more than three letters in it.");
        }, options);

        var ex = Assert.Throws<PlumePdfException>(() => document.Pages[0].ExtractText());
        Assert.Equal("PLUME6021", ex.Code);
    }

    [Fact]
    public void ExtractText_TightenedPdfOptionsMaxXObjectNestingDepth_ThrowsPlume6020WithNoExtractionOptionsOverride()
    {
        var bytes = BuildFanOutDocument(depth: 4, fanOut: 1);

        var options = PdfOptions.Default with { MaxXObjectNestingDepth = 1 };
        using var document = PdfDocument.Open(bytes, options);

        var ex = Assert.Throws<PlumePdfException>(() => document.Pages[0].ExtractText());
        Assert.Equal("PLUME6020", ex.Code);
    }

    [Fact]
    public void ExtractImages_TightenedPdfOptionsMaxXObjectNestingDepth_RecordsPlume6025AndStopsDescending()
    {
        // ImageExtractor previously hardcoded its own MaxXObjectDepth = 16 local const,
        // ignoring PdfOptions.MaxXObjectNestingDepth entirely.
        var bytes = BuildImageNestedInFormsDocument(formDepth: 3);

        var options = PdfOptions.Default with { MaxXObjectNestingDepth = 1 };
        using var document = PdfDocument.Open(bytes, options);

        var (images, diagnostics) = document.Pages[0].ExtractImagesWithDiagnostics();

        Assert.Empty(images);
        Assert.Contains(diagnostics, d => d.Code == "PLUME6025");
    }

    // Builds a page whose content invokes Form_0, which (for every non-leaf level) invokes the
    // next level's form "fanOut" times via repeated "/C Do" operators; the leaf level paints no
    // text and references no further forms. Total Do invocations across the whole page grow as
    // fanOut^depth.
    private static byte[] BuildFanOutDocument(int depth, int fanOut)
    {
        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int firstFormNum = 4;
        var pageContentNum = firstFormNum + depth;
        var totalObjects = pageContentNum + 1;

        var builder = new RawPdfBuilder(totalObjects);
        builder.WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        builder.WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        builder.WriteObject(
            pageNum,
            $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 200] " +
            $"/Resources << /XObject << /F0 {firstFormNum} 0 R >> >> /Contents {pageContentNum} 0 R >>");

        for (var level = 0; level < depth; level++)
        {
            var num = firstFormNum + level;
            if (level == depth - 1)
            {
                builder.WriteStreamObject(num, "/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "0 0 m");
            }
            else
            {
                var nextNum = firstFormNum + level + 1;
                var content = string.Join(' ', Enumerable.Repeat("/C Do", fanOut));
                builder.WriteStreamObject(
                    num,
                    $"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /C {nextNum} 0 R >> >>",
                    content);
            }
        }

        builder.WriteStreamObject(pageContentNum, string.Empty, "/F0 Do");
        return builder.Build(catalogNum);
    }

    private static byte[] BuildTwiceInvokedFormDocument()
    {
        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int fontNum = 4;
        const int formNum = 5;
        const int pageContentNum = 6;
        const int totalObjects = 7;

        var builder = new RawPdfBuilder(totalObjects);
        builder.WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        builder.WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        builder.WriteObject(fontNum, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        builder.WriteObject(
            pageNum,
            $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 300 300] " +
            $"/Resources << /Font << /F1 {fontNum} 0 R >> /XObject << /Form {formNum} 0 R >> >> /Contents {pageContentNum} 0 R >>");
        builder.WriteStreamObject(
            formNum,
            $"/Type /XObject /Subtype /Form /BBox [0 0 100 20] /Resources << /Font << /F1 {fontNum} 0 R >> >>",
            "BT /F1 12 Tf 0 0 Td (Reused) Tj ET");
        builder.WriteStreamObject(pageContentNum, string.Empty, "q 1 0 0 1 20 250 cm /Form Do Q q 1 0 0 1 20 20 cm /Form Do Q");
        return builder.Build(catalogNum);
    }

    private static byte[] BuildImageNestedInFormsDocument(int formDepth)
    {
        const int catalogNum = 1;
        const int pagesNum = 2;
        const int pageNum = 3;
        const int imageNum = 4;
        var firstFormNum = imageNum + 1;
        var pageContentNum = firstFormNum + formDepth;
        var totalObjects = pageContentNum + 1;

        var builder = new RawPdfBuilder(totalObjects);
        builder.WriteObject(catalogNum, $"<< /Type /Catalog /Pages {pagesNum} 0 R >>");
        builder.WriteObject(pagesNum, $"<< /Type /Pages /Kids [{pageNum} 0 R] /Count 1 >>");
        builder.WriteObject(
            pageNum,
            $"<< /Type /Page /Parent {pagesNum} 0 R /MediaBox [0 0 200 200] " +
            $"/Resources << /XObject << /F0 {firstFormNum} 0 R >> >> /Contents {pageContentNum} 0 R >>");

        // A minimal 1x1 uncompressed RGB image.
        builder.WriteImageObject(imageNum, "/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceRGB", [255, 0, 0]);

        for (var level = 0; level < formDepth; level++)
        {
            var num = firstFormNum + level;
            var isLeaf = level == formDepth - 1;
            var xobjectResource = isLeaf ? $"/Img {imageNum} 0 R" : $"/C {firstFormNum + level + 1} 0 R";
            var content = isLeaf ? "q 1 0 0 1 0 0 cm /Img Do Q" : "/C Do";
            builder.WriteStreamObject(
                num,
                $"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << {xobjectResource} >> >>",
                content);
        }

        builder.WriteStreamObject(pageContentNum, string.Empty, "/F0 Do");
        return builder.Build(catalogNum);
    }

    // Minimal, self-contained raw-PDF-bytes builder (ISO 32000-1 grammar directly, a source
    // allowed by the clean-room policy in AGENTS.md; never PlumePdf's own writer) for the
    // hand-crafted object graphs above, in the same spirit as
    // ExtractionCorpusTests.GeneratedFixtures - kept local to this file
    // since these documents need arbitrary indirect XObject graphs the corpus fixtures don't.
    private sealed class RawPdfBuilder(int totalObjects)
    {
        private readonly List<byte> _buffer = [.. Encoding.ASCII.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n")];
        private readonly Dictionary<int, int> _offsets = [];
        private readonly int _totalObjects = totalObjects;

        public void WriteObject(int num, string body)
        {
            _offsets[num] = _buffer.Count;
            _buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n{body}\nendobj\n"));
        }

        public void WriteStreamObject(int num, string dictBody, string content)
        {
            _offsets[num] = _buffer.Count;
            var dict = string.IsNullOrEmpty(dictBody) ? string.Empty : $"{dictBody} ";
            _buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< {dict}/Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));
        }

        public void WriteImageObject(int num, string dictBody, byte[] rawSamples)
        {
            _offsets[num] = _buffer.Count;
            _buffer.AddRange(Encoding.ASCII.GetBytes($"{num} 0 obj\n<< /Type /XObject /Subtype /Image {dictBody} /Length {rawSamples.Length} >>\nstream\n"));
            _buffer.AddRange(rawSamples);
            _buffer.AddRange(Encoding.ASCII.GetBytes("\nendstream\nendobj\n"));
        }

        public byte[] Build(int catalogNum)
        {
            var xrefOffset = _buffer.Count;
            _buffer.AddRange(Encoding.ASCII.GetBytes($"xref\n0 {_totalObjects}\n0000000000 65535 f \n"));
            for (var n = 1; n < _totalObjects; n++)
            {
                _buffer.AddRange(Encoding.ASCII.GetBytes($"{_offsets[n]:D10} 00000 n \n"));
            }

            _buffer.AddRange(Encoding.ASCII.GetBytes(
                $"trailer\n<< /Size {_totalObjects} /Root {catalogNum} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF"));
            return [.. _buffer];
        }
    }
}
