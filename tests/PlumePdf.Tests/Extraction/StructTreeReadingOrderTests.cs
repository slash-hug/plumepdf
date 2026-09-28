using System.Text;
using PlumePdf.Documents.Structure;
using PlumePdf.Elements;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Extraction;

/// <summary>
/// Extraction's reading order is structure-tree-driven for a document
/// with a parseable <c>/StructTreeRoot</c>, geometric otherwise (with the <c>PLUME6070</c>
/// informational fallback diagnostic). These tests pin the whole production path — MCID
/// provenance threaded from the content stream's <c>BDC</c>/<c>EMC</c> nesting onto
/// <see cref="PlumePdf.Documents.Letter.Mcid"/>/<see cref="PlumePdf.Documents.ExtractedWord.Mcid"/>,
/// and <c>TextExtractor</c> consuming the tree through <c>ReadingOrderer</c>'s
/// structure-order overload — hermetically, via synthetic documents whose tag order
/// deliberately disagrees with their geometric order.
/// </summary>
public class StructTreeReadingOrderTests
{
    [Fact]
    public void ExtractText_TaggedDocument_StructureTreeOrderWinsOverGeometry()
    {
        // Geometric order (top-to-bottom) says "First" (y=700) then "Second" (y=650); the
        // structure tree deliberately lists MCID 1 ("Second") before MCID 0 ("First") — the
        // tag-authored order must win, with no fallback diagnostic.
        const string content = """
            /P <</MCID 0>> BDC
            BT /F1 12 Tf 1 0 0 1 50 700 Tm (First) Tj ET
            EMC
            /P <</MCID 1>> BDC
            BT /F1 12 Tf 1 0 0 1 50 650 Tm (Second) Tj ET
            EMC
            """;

        var second = new StructureElement { Role = "P" };
        second.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = 1 });
        var first = new StructureElement { Role = "P" };
        first.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = 0 });
        var root = new StructureElement { Role = "Document" };
        root.Children.Add(second);
        root.Children.Add(first);

        using var document = BuildOnePageDocument(content, root);
        var extracted = document.Pages[0].ExtractText();

        Assert.Equal("Second\nFirst", extracted.Text);
        Assert.DoesNotContain(extracted.Diagnostics, static d => d.Code == "PLUME6070");
        Assert.All(extracted.Letters, static letter => Assert.NotNull(letter.Mcid));
        Assert.Equal([1, 0], extracted.Words.Select(static w => w.Mcid!.Value));
    }

    [Fact]
    public void ExtractText_UntaggedDocument_GeometricOrderWithPlume6070Fallback()
    {
        const string content = """
            BT /F1 12 Tf 1 0 0 1 50 700 Tm (First) Tj ET
            BT /F1 12 Tf 1 0 0 1 50 650 Tm (Second) Tj ET
            """;

        using var document = BuildOnePageDocument(content, structureRoot: null);
        var extracted = document.Pages[0].ExtractText();

        Assert.Equal("First\nSecond", extracted.Text);
        Assert.Contains(extracted.Diagnostics, static d => d.Code == "PLUME6070" && d.Severity == DiagnosticSeverity.Info);
        Assert.All(extracted.Letters, static letter => Assert.Null(letter.Mcid));
    }

    [Fact]
    public void ExtractText_ArtifactContent_OrdersAfterTaggedContentWithoutDisablingStructureOrder()
    {
        // The /Artifact footer paints geometrically ABOVE the body (y=750 vs y=650), so the
        // geometric heuristic would read "Footer" first. Structure order must still activate
        // (an artifact word never disqualifies the page), putting tagged content first
        // and the un-ranked artifact last.
        const string content = """
            /Artifact BMC
            BT /F1 8 Tf 1 0 0 1 50 750 Tm (Footer) Tj ET
            EMC
            /P <</MCID 0>> BDC
            BT /F1 12 Tf 1 0 0 1 50 650 Tm (Body) Tj ET
            EMC
            """;

        var paragraph = new StructureElement { Role = "P" };
        paragraph.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = 0 });
        var root = new StructureElement { Role = "Document" };
        root.Children.Add(paragraph);

        using var document = BuildOnePageDocument(content, root);
        var extracted = document.Pages[0].ExtractText();

        Assert.Equal("Body\nFooter", extracted.Text);
        Assert.DoesNotContain(extracted.Diagnostics, static d => d.Code == "PLUME6070");
        Assert.Null(extracted.Words.Single(static w => w.Text == "Footer").Mcid);
    }

    [Fact]
    public void ExtractText_HostileOutOfRangeMcid_RecordsPlume6082AndFallsBackGeometrically()
    {
        // /MCID 1e20 once raw-cast to int.MinValue silently; now it reads leniently as "no
        // MCID" (PLUME6082 diagnostic), so no word ranks and ordering falls back (PLUME6070).
        const string content = """
            /P <</MCID 99999999999999999999>> BDC
            BT /F1 12 Tf 1 0 0 1 50 700 Tm (Alpha) Tj ET
            EMC
            """;

        var paragraph = new StructureElement { Role = "P" };
        paragraph.Children.Add(new MarkedContentReference { PageIndex = 0, Mcid = 0 });
        var root = new StructureElement { Role = "Document" };
        root.Children.Add(paragraph);

        using var document = BuildOnePageDocument(content, root);
        var extracted = document.Pages[0].ExtractText();

        Assert.Equal("Alpha", extracted.Text);
        Assert.Contains(extracted.Diagnostics, static d => d.Code == "PLUME6082");
        Assert.Contains(extracted.Diagnostics, static d => d.Code == "PLUME6070");
        Assert.All(extracted.Letters, static letter => Assert.Null(letter.Mcid));
    }

    [Fact]
    public void ExtractText_ComposedTaggedManuscript_UsesStructureOrderEndToEnd()
    {
        // The full production round trip: Manuscript.Language opts into tagging, the renderer
        // assigns MCIDs, and extraction reads the tree back in one pass.
        var manuscript = new Manuscript
        {
            Language = "en-US",
            Sections =
            [
                new Section
                {
                    Body = new Column(
                        new Text("Heading text") { HeadingLevel = 1 },
                        new Text("Paragraph text")),
                },
            ],
        };

        using var document = manuscript.Render();
        var extracted = document.Pages[0].ExtractText();

        Assert.Equal("Heading text\nParagraph text", extracted.Text);
        Assert.DoesNotContain(extracted.Diagnostics, static d => d.Code == "PLUME6070");
        Assert.Contains(extracted.Letters, static letter => letter.Mcid == 0);
        Assert.Contains(extracted.Letters, static letter => letter.Mcid == 1);
    }

    // A one-page synthetic document: an uncompressed content stream (no /Font resource — the
    // extractor's ASCII fallback font decodes the Tj strings), plus an optional structure tree
    // built through the same StructureTreeBuilder the renderer uses.
    private static PdfDocument BuildOnePageDocument(string content, StructureElement? structureRoot)
    {
        var objects = new Dictionary<int, PdfObject>();
        var next = 1;
        IndirectReference Reserve() => new(next++, 0);
        void SetObj(IndirectReference r, PdfObject v) => objects[r.Number] = v;

        var contentBytes = Encoding.ASCII.GetBytes(content);
        var contentDict = new PdfDictionary();
        contentDict.Set(PdfName.Length, PdfNumber.Get(contentBytes.Length));
        var contentRef = Reserve();
        SetObj(contentRef, new PdfStream(contentDict, contentBytes));

        var pageRef = Reserve();
        var pagesRef = Reserve();
        var pageDict = new PdfDictionary();
        pageDict.Set(PdfName.Type, PdfName.Get("Page"));
        pageDict.Set(PdfName.Get("Parent"), new PdfReference(pagesRef));
        pageDict.Set(PdfName.Get("MediaBox"), new PdfArray([PdfNumber.Get(0), PdfNumber.Get(0), PdfNumber.Get(612), PdfNumber.Get(792)]));
        pageDict.Set(PdfName.Get("Contents"), new PdfReference(contentRef));
        SetObj(pageRef, pageDict);

        var pagesDict = new PdfDictionary();
        pagesDict.Set(PdfName.Type, PdfName.Get("Pages"));
        pagesDict.Set(PdfName.Get("Kids"), new PdfArray([new PdfReference(pageRef)]));
        pagesDict.Set(PdfName.Get("Count"), PdfNumber.Get(1));
        SetObj(pagesRef, pagesDict);

        var catalogRef = Reserve();
        var catalogDict = new PdfDictionary();
        catalogDict.Set(PdfName.Type, PdfName.Get("Catalog"));
        catalogDict.Set(PdfName.Get("Pages"), new PdfReference(pagesRef));

        if (structureRoot is not null)
        {
            var built = StructureTreeBuilder.Build(structureRoot, [pageRef], PdfOptions.Default, Reserve, SetObj);
            catalogDict.Set(PdfName.Get("StructTreeRoot"), new PdfReference(built.StructTreeRootReference));
            catalogDict.Set(PdfName.Get("MarkInfo"), built.MarkInfoDictionary);
        }

        SetObj(catalogRef, catalogDict);

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, PdfNumber.Get(next));
        trailer.Set(PdfName.Root, new PdfReference(catalogRef));

        return PdfDocument.CreateSynthetic(new InMemoryObjectSource(trailer, objects));
    }
}
