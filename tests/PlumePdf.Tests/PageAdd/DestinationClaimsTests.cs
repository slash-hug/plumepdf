using PlumePdf.Documents;
using Xunit;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// <see cref="DestinationClaims"/>: what an opened document claims about itself that page-add
/// cannot keep true for an added page (tagged, PDF/A, PDF/UA), its XFA form and whether that form
/// is dynamic, and whether its catalog's <c>/Pages</c> is an indirect reference a new page can be
/// hung under.
/// </summary>
public class DestinationClaimsTests
{
    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>";
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";

    [Fact]
    public void APlainDocument_ClaimsNothing()
    {
        var claims = Read("<< /Type /Catalog /Pages 2 0 R >>");

        Assert.False(claims.IsTagged);
        Assert.Null(claims.PdfAPart);
        Assert.False(claims.ClaimsPdfUa);
        Assert.False(claims.HasConformanceClaims);
        Assert.False(claims.HasXfa);
        Assert.False(claims.NeedsRendering);
        Assert.True(claims.HasIndirectPages);
    }

    [Fact]
    public void AStructureTree_MakesItTagged()
    {
        var claims = Read("<< /Type /Catalog /Pages 2 0 R /StructTreeRoot << /Type /StructTreeRoot >> >>");

        Assert.True(claims.IsTagged);
        Assert.True(claims.HasConformanceClaims);
    }

    [Fact]
    public void MarkedTrue_MakesItTagged()
    {
        Assert.True(Read("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> >>").IsTagged);
    }

    [Fact]
    public void MarkedFalse_WithoutAStructureTree_IsNotTagged()
    {
        Assert.False(Read("<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked false >> >>").IsTagged);
    }

    [Fact]
    public void APdfaIdentification_ClaimsPdfA()
    {
        var claims = Read(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            (4, Xmp("""<rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/" pdfaid:part="2" pdfaid:conformance="B"/>""")));

        Assert.Equal("2", claims.PdfAPart);
        Assert.False(claims.ClaimsPdfUa);
        Assert.True(claims.HasConformanceClaims);
    }

    [Fact]
    public void APdfuaIdentification_ClaimsPdfUa_InEitherSerialization()
    {
        var attribute = Read(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            (4, Xmp("""<rdf:Description rdf:about="" xmlns:pdfuaid="http://www.aiim.org/pdfua/ns/id/" pdfuaid:part="1"/>""")));
        var element = Read(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            (4, Xmp("""<rdf:Description rdf:about="" xmlns:pdfuaid="http://www.aiim.org/pdfua/ns/id/"><pdfuaid:part>1</pdfuaid:part></rdf:Description>""")));

        Assert.True(attribute.ClaimsPdfUa);
        Assert.True(element.ClaimsPdfUa);
        Assert.Null(attribute.PdfAPart);
    }

    [Fact]
    public void AnXfaForm_IsReported_AndIsStaticUnlessNeedsRenderingIsTrue()
    {
        var staticXfa = Read("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA 4 0 R >> >>", (4, "<< /Length 0 >>\nstream\n\nendstream"));
        var dynamicXfa = Read("<< /Type /Catalog /Pages 2 0 R /NeedsRendering true /AcroForm << /Fields [] /XFA 4 0 R >> >>", (4, "<< /Length 0 >>\nstream\n\nendstream"));

        Assert.True(staticXfa.HasXfa);
        Assert.False(staticXfa.NeedsRendering);
        Assert.True(dynamicXfa.HasXfa);
        Assert.True(dynamicXfa.NeedsRendering);
        Assert.False(dynamicXfa.HasConformanceClaims);
    }

    [Fact]
    public void AnXfaForm_BehindAnIndirectAcroForm_IsReported()
    {
        var claims = Read("<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>", (4, "<< /Fields [] /XFA [(template) 5 0 R] >>"), (5, "<< /Length 0 >>\nstream\n\nendstream"));

        Assert.True(claims.HasXfa);
    }

    [Fact]
    public void ADirectPagesDictionary_HasNoIndirectPages()
    {
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(
            [(1, "<< /Type /Catalog /Pages << /Type /Pages /Kids [3 0 R] /Count 1 >> >>"), (3, "<< /Type /Page /MediaBox [0 0 100 100] >>")],
            rootObjectNumber: 1);
        using var document = PdfDocument.Open(bytes);

        Assert.False(DestinationClaims.Read(document).HasIndirectPages);
    }

    private static DestinationClaims Read(string catalog, params (int Number, string Body)[] extra)
    {
        var objects = new List<(int, string)> { (1, catalog), (2, Pages), (3, Page) };
        objects.AddRange(extra);
        using var document = PdfDocument.Open(PdfFixtureBuilder.BuildClassicXrefPdf(objects, rootObjectNumber: 1));
        return DestinationClaims.Read(document);
    }

    private static string Xmp(string description)
    {
        var packet = $"""<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">{description}</rdf:RDF></x:xmpmeta><?xpacket end="w"?>""";
        return $"<< /Type /Metadata /Subtype /XML /Length {packet.Length} >>\nstream\n{packet}\nendstream";
    }
}
