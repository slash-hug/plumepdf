using System.Text;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>Regressions from the adversarial review of page-add, one per finding.</summary>
public class PageAddReviewRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStructureDestination_DoesNotBringTheSourcesStructureTree(bool incremental)
    {
        var input = WriteTemp(Text(["One"]));
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(TaggedWithStructureDestination()))
            {
                document.Pages.Add(source.Pages[0]);
                Save(document, output, incremental);
            }

            var bytes = File.ReadAllBytes(output);
            Assert.True(bytes.AsSpan().IndexOf("SECRET"u8) < 0);
            Assert.True(bytes.AsSpan().IndexOf("StructTreeRoot"u8) < 0);
            using var reopened = PdfDocument.Open(output);
            Assert.Equal(2, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void FieldsThatInheritAFormDaNamingARenamedFont_GetTheRenamedFont()
    {
        using var document = PdfDocument.Open(Form("/F1 6 0 R", "/DA (/F1 12 Tf 0 g)", "/Helvetica"));
        using var source = PdfDocument.Open(Form("/F1 6 0 R", "/DA (/F1 12 Tf 0 g)", "/Courier", fieldName: "B"));

        document.Pages.Add(source.Pages[0]);

        var copied = (PdfDictionary)document.Objects[((PdfReference)Fields(document)[1]).Target];
        Assert.Equal("/F1~2 12 Tf 0 g", Encoding.Latin1.GetString(((PdfString)copied[PdfName.Get("DA")]).Bytes.Span));
    }

    [Fact]
    public void AFieldWithAnImplicitQuadding_KeepsItInADestinationWithQ()
    {
        using var document = PdfDocument.Open(Form("/F1 6 0 R", "/DA (/F1 12 Tf 0 g) /Q 2", "/Helvetica"));
        using var source = PdfDocument.Open(Form("/F1 6 0 R", "/DA (/F1 12 Tf 0 g)", "/Helvetica", fieldName: "B"));

        document.Pages.Add(source.Pages[0]);

        var copied = (PdfDictionary)document.Objects[((PdfReference)Fields(document)[1]).Target];
        Assert.Equal(0, ((PdfNumber)copied[PdfName.Get("Q")]).ToInt32());
    }

    [Fact]
    public void RealObjectNumbersAboveTheProvisionalBase_DoNotBreakTheCommit()
    {
        const int Big = (1 << 30) + 6;
        var bytes = PdfFixtureBuilder.BuildClassicXrefPdf(
            [
                (1, "<< /Type /Catalog /Pages 2 0 R >>"),
                (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                (3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 {Big} 0 R >> >> >>"),
            ],
            rootObjectNumber: 1);
        using var document = PdfDocument.Open(bytes);
        using var source = PdfDocument.Open(Text(["New"]));

        var duplicate = document.Pages.Add(document.Pages[0]);
        document.Pages.Add(source.Pages[0]);
        document.Pages.Add(duplicate);

        Assert.Equal(4, document.Pages.Count);
        var font = ((PdfDictionary)((PdfDictionary)duplicate.Dictionary[PdfName.Get("Resources")])[PdfName.Get("Font")])[PdfName.Get("F1")];
        Assert.Equal(Big, ((PdfReference)font).Target.Number);
    }

    [Fact]
    public void OnlyTheFormFontsTheCopiedFieldsName_AreAdded()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        using var source = PdfDocument.Open(Form("/F1 6 0 R /Unused 7 0 R >> /ExtGState << /GS1 8 0 R", "/DA (/F1 12 Tf 0 g)", "/Courier", fieldName: "B"));

        document.Pages.Add(source.Pages[0]);

        var form = (PdfDictionary)Resolve(document, ((PdfDictionary)document.Objects[new IndirectReference(1, 0)])[PdfName.Get("AcroForm")]);
        var dr = (PdfDictionary)Resolve(document, form[PdfName.Get("DR")]);
        Assert.Equal(["F1"], ((PdfDictionary)Resolve(document, dr[PdfName.Get("Font")])).Select(static e => e.Key.Value));
        Assert.False(dr.ContainsKey(PdfName.Get("ExtGState")));
    }

    private static void Save(PdfDocument document, string output, bool incremental)
    {
        if (incremental)
        {
            document.SaveIncremental(output);
        }
        else
        {
            document.Save(output);
        }
    }

    private static PdfArray Fields(PdfDocument document)
    {
        var form = (PdfDictionary)Resolve(document, ((PdfDictionary)document.Objects[new IndirectReference(1, 0)])[PdfName.Get("AcroForm")]);
        return (PdfArray)Resolve(document, form[PdfName.Get("Fields")]);
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) => value is PdfReference r ? document.Objects[r.Target] : value;

    /// <summary>
    /// One page with text field <paramref name="fieldName"/> (no /DA or /Q of its own), a form
    /// whose /DR fonts are <paramref name="fonts"/> (6 is a Type1 font with base font
    /// <paramref name="baseFont"/>, 7 Times, 8 an ExtGState) plus <paramref name="formExtra"/>.
    /// </summary>
    private static byte[] Form(string fonts, string formExtra, string baseFont, string fieldName = "A") => PdfFixtureBuilder.BuildClassicXrefPdf(
        [
            (1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DR << /Font << {fonts} >> >> {formExtra} >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>"),
            (5, $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /P 3 0 R /Rect [0 0 80 20] >>"),
            (6, $"<< /Type /Font /Subtype /Type1 /BaseFont {baseFont} /Encoding /WinAnsiEncoding >>"),
            (7, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>"),
            (8, "<< /Type /ExtGState /CA 1 >>"),
        ],
        rootObjectNumber: 1);

    /// <summary>
    /// Two tagged pages; page 3's link carries a PDF 2.0 structure destination (/SD) to an element
    /// of page 3, and the structure tree also holds page 4's element with secret alt text, plus an
    /// outline item with a secret title.
    /// </summary>
    private static byte[] TaggedWithStructureDestination() => PdfFixtureBuilder.BuildClassicXrefPdf(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 50 0 R /MarkInfo << /Marked true >> /Outlines 60 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [9 0 R] >>"),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoTo /D [3 0 R /Fit] /SD [51 0 R /Fit] /Next 61 0 R >> >>"),
            (50, "<< /Type /StructTreeRoot /K [51 0 R 52 0 R] >>"),
            (51, "<< /Type /StructElem /S /P /P 50 0 R /Pg 3 0 R >>"),
            (52, "<< /Type /StructElem /S /P /P 50 0 R /Pg 4 0 R /ActualText (SECRET-STRUCT) /Alt (SECRET-ALT) >>"),
            (60, "<< /Type /Outlines /First 61 0 R /Last 61 0 R /Count 1 >>"),
            (61, "<< /Title (SECRET-OUTLINE) /Parent 60 0 R /Dest [4 0 R /Fit] >>"),
        ],
        rootObjectNumber: 1);
}
