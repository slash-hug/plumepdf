using System.Text;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// What page-add leaves alone, and what it reports about the destination: the appended revision
/// rewrites none of the outline, the XMP metadata or the structure tree; tagged, PDF/A, PDF/UA and
/// XFA destinations and tagged sources are reported, each kind once per document.
/// </summary>
public class PageAddPreservationTests
{
    private const string Xmp = """<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" xmlns:pdfuaid="http://www.aiim.org/pdfua/ns/id/" pdfaid:part="2" pdfaid:conformance="B" pdfuaid:part="1"/></rdf:RDF></x:xmpmeta><?xpacket end="w"?>""";

    [Fact]
    public void TheAppendedRevision_RewritesNeitherOutlineNorMetadataNorStructureTree()
    {
        var input = WriteTemp(Rich());
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Text(["New"])))
            {
                document.Pages.Add(source.Pages[0]);
                document.SaveIncremental(output);
            }

            var original = File.ReadAllBytes(input);
            var appendix = Encoding.Latin1.GetString(File.ReadAllBytes(output).AsSpan(original.Length));
            foreach (var number in new[] { 1, 40, 41, 42, 43 })
            {
                Assert.DoesNotContain($"\n{number} 0 obj", appendix, StringComparison.Ordinal);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Equal(["One", "New"], PageTexts(reopened));
            Assert.NotNull(reopened.GetXmpMetadataText());
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void ATaggedPdfAAndPdfUaDestination_IsReportedOnce()
    {
        using var document = PdfDocument.Open(Rich());
        using var source = PdfDocument.Open(Text(["New"]));

        document.Pages.Add(source.Pages[0]);
        document.Pages.Add(source.Pages[0]);

        var diagnostic = Assert.Single(document.Diagnostics, static d => d.Code == "PLUME6095");
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.Contains("tagged", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("PDF/A-2", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("PDF/UA", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATaggedSource_IsReportedOnce()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        using var source = PdfDocument.Open(Rich());

        document.Pages.Add(source.Pages[0]);
        document.Pages.Add(source.Pages[0]);

        Assert.Single(document.Diagnostics, static d => d.Code == "PLUME6093");
        Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME6095");
    }

    [Theory]
    [InlineData(false, DiagnosticSeverity.Info)]
    [InlineData(true, DiagnosticSeverity.Warning)]
    public void AnXfaDestination_IsReported_AsAWarningWhenDynamic(bool dynamic, DiagnosticSeverity expected)
    {
        using var document = PdfDocument.Open(Text(
            ["One"],
            catalogExtra: $"/AcroForm << /Fields [] /XFA 40 0 R >> {(dynamic ? "/NeedsRendering true" : "")}",
            extra: [(40, "<< /Length 0 >>\nstream\n\nendstream")]));
        using var source = PdfDocument.Open(Text(["New"]));

        document.Pages.Add(source.Pages[0]);

        Assert.Equal(expected, Assert.Single(document.Diagnostics, static d => d.Code == "PLUME6096").Severity);
    }

    [Fact]
    public void APlainCopy_ReportsNothing()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        using var source = PdfDocument.Open(Text(["New"]));

        document.Pages.Add(source.Pages[0]);

        Assert.Empty(document.Diagnostics);
    }

    /// <summary>One text page in a tagged document with an outline (40, 41), XMP claiming PDF/A-2b and PDF/UA (42), and a structure tree (43).</summary>
    private static byte[] Rich() => Text(
        ["One"],
        catalogExtra: "/Outlines 40 0 R /Metadata 42 0 R /StructTreeRoot 43 0 R /MarkInfo << /Marked true >> /Lang (en)",
        extra:
        [
            (40, "<< /Type /Outlines /First 41 0 R /Last 41 0 R /Count 1 >>"),
            (41, "<< /Title (Start) /Parent 40 0 R /Dest [10 0 R /Fit] >>"),
            (42, $"<< /Type /Metadata /Subtype /XML /Length {Xmp.Length} >>\nstream\n{Xmp}\nendstream"),
            (43, "<< /Type /StructTreeRoot /K [] >>"),
        ]);
}
