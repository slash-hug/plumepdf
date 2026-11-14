using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// Form fields on a copied page: they join the destination's form (renamed on a collision),
/// signature fields arrive empty, only fields placed on the copied page come along,
/// <see cref="PdfDocument.Form"/> sees them at once, and nothing from the source's other pages
/// leaks into the destination through a link, a pop-up or a radio group.
/// </summary>
public class PageAddFormTests
{
    [Fact]
    public void ACollidingField_IsRenamed_AndEachRenameIsReported()
    {
        using var document = PdfDocument.Open(Destination());
        using var source = PdfDocument.Open(Source());

        document.Pages.Add(source.Pages[0]);
        document.Pages.Add(source.Pages[0]);

        var names = document.Form.Fields.Select(static f => f.Name).ToList();
        Assert.Contains("Name", names);
        Assert.Contains("Name~2", names);
        Assert.Contains("Name~3", names);
        Assert.Contains("Choice~2", names);
        Assert.Equal(4, document.Diagnostics.Count(static d => d.Code == "PLUME6099")); // Name~2, then Name~3, Choice~2, Sig~2
    }

    [Fact]
    public void TheFormSeesTheCopiedFields_EvenWhenItWasReadBefore()
    {
        using var document = PdfDocument.Open(Destination());
        using var source = PdfDocument.Open(Source());
        var before = document.Form.Fields.Count;

        document.Pages.Add(source.Pages[0]);

        Assert.True(document.Form.Fields.Count > before);
    }

    [Fact]
    public void ACopiedSignatureField_ArrivesEmpty()
    {
        using var document = PdfDocument.Open(Destination());
        using var source = PdfDocument.Open(Source());

        document.Pages.Add(source.Pages[0]);

        var signature = document.Form.Fields.Single(static f => f.Name == "Sig");
        Assert.Null(signature.Value);
        Assert.Empty(document.Signatures);
    }

    [Fact]
    public void OnlyFieldsPlacedOnTheCopiedPage_ComeAlong()
    {
        using var document = PdfDocument.Open(Destination());
        using var source = PdfDocument.Open(Source());

        document.Pages.Add(source.Pages[0]);

        var names = document.Form.Fields.Select(static f => f.Name).ToList();
        Assert.DoesNotContain("Hidden", names);
        Assert.DoesNotContain("OnPageTwo", names);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACopiedFieldCanBeFilled_AndBothSavePathsKeepIt(bool incremental)
    {
        var input = WriteTemp(Destination());
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Source()))
            {
                document.Pages.Add(source.Pages[0]);
                document.Form.Fill(new Dictionary<string, string> { ["Name~2"] = "Ada" });
                if (incremental)
                {
                    document.SaveIncremental(output);
                }
                else
                {
                    document.Save(output);
                }
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Equal("Ada", reopened.Form.Fields.Single(static f => f.Name == "Name~2").Value);
            Assert.Equal("Dest", reopened.Form.Fields.Single(static f => f.Name == "Name").Value);
            Assert.Equal(2, reopened.Pages.Count);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void AddedThenRemoved_ItsFieldsAreLeftOutOfAFullSave()
    {
        var input = WriteTemp(Destination());
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Source()))
            {
                document.Pages.Add(source.Pages[0]);
                document.Pages.RemoveAt(1);
                document.Save(output);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Equal(["Name"], reopened.Form.Fields.Select(static f => f.Name));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void AddedThenRemoved_BeforeSaveIncremental_KeepsTheCopiedFieldsWithoutAPage()
    {
        var input = WriteTemp(Destination());
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Source()))
            {
                document.Pages.Add(source.Pages[0]);
                document.Pages.RemoveAt(1);
                document.SaveIncremental(output);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Single(reopened.Pages);
            // Documented: SaveIncremental appends what the add wrote, including the form's new
            // fields, whose widgets are on the page that is no longer in the page tree.
            Assert.Contains("Name~2", reopened.Form.Fields.Select(static f => f.Name));
            Assert.Equal("Dest", reopened.Form.Fields.Single(static f => f.Name == "Name").Value);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NothingFromTheSourcesOtherPages_ReachesTheSavedFile(bool incremental)
    {
        var input = WriteTemp(Destination());
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Source()))
            {
                document.Pages.Add(source.Pages[0]);
                if (incremental)
                {
                    document.SaveIncremental(output);
                }
                else
                {
                    document.Save(output);
                }
            }

            // The marker is only in page two's content and annotation; an incremental file is
            // scanned raw, so nothing from page two may have been appended at all.
            Assert.True(File.ReadAllBytes(output).AsSpan().IndexOf("PageTwoSecret"u8) < 0);
            using var reopened = PdfDocument.Open(output);
            var added = reopened.Pages[1];
            var link = ((PdfArray)added.Dictionary[PdfName.Get("Annots")]).Select(a => reopened.Objects[((PdfReference)a).Target]).OfType<PdfDictionary>()
                .SingleOrDefault(static a => a.TryGetValue(PdfName.Get("Subtype"), out var s) && s is PdfName { Value: "Link" });
            Assert.Null(link); // its only target was page two, so it was left out
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    /// <summary>One page with text field "Name" (value "Dest") and Helvetica as /Helv.</summary>
    private static byte[] Destination() => PdfFixtureBuilder.BuildClassicXrefPdf(
        [
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DR << /Font << /Helv 6 0 R >> >> /DA (/Helv 0 Tf 0 g) >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>"),
            (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /V (Dest) /P 3 0 R /Rect [0 0 80 20] /DA (/Helv 10 Tf 0 g) >>"),
            (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        ],
        rootObjectNumber: 1);

    /// <summary>
    /// Two pages. Page 3 places "Name" (collides), signature field "Sig" (signed), one widget of
    /// radio group "Choice" (its other widget is on page 4) and a link to page 4. Page 4 holds a
    /// marker ("PageTwoSecret") in its content and in a text annotation's /Contents, plus field
    /// "OnPageTwo". Field "Hidden" is placed nowhere.
    /// </summary>
    private static byte[] Source()
    {
        const string Secret = "BT /F1 12 Tf 20 50 Td (PageTwoSecret) Tj ET";
        return PdfFixtureBuilder.BuildClassicXrefPdf(
            [
                (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm 20 0 R >>"),
                (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
                (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R 7 0 R 9 0 R 11 0 R] >>"),
                (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 6 0 R >> >> /Contents 12 0 R /Annots [10 0 R 13 0 R 14 0 R] >>"),
                (5, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /P 3 0 R /Rect [0 0 80 20] /DA (/Helv 10 Tf 0 g) >>"),
                (6, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
                (7, "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig) /P 3 0 R /Rect [0 30 80 50] /V 15 0 R >>"),
                (8, "<< /FT /Btn /Ff 49152 /T (Choice) /Kids [9 0 R 10 0 R] /V /a >>"),
                (9, "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /P 3 0 R /Rect [100 0 110 10] /AS /a /AP << /N << /a 16 0 R /Off 16 0 R >> >> >>"),
                (10, "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /P 4 0 R /Rect [100 0 110 10] /AS /Off /AP << /N << /b 16 0 R /Off 16 0 R >> >> >>"),
                (11, "<< /Type /Annot /Subtype /Link /Rect [0 60 50 70] /Dest [4 0 R /Fit] >>"),
                (12, $"<< /Length {Secret.Length} >>\nstream\n{Secret}\nendstream"),
                (13, "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Contents (PageTwoSecret note) >>"),
                (14, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (OnPageTwo) /P 4 0 R /Rect [0 0 80 20] >>"),
                (15, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 10] /Contents <00> >>"),
                (16, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length 0 >>\nstream\n\nendstream"),
                (17, "<< /FT /Tx /T (Hidden) >>"),
                (20, "<< /Fields [5 0 R 7 0 R 8 0 R 14 0 R 17 0 R] /DR << /Font << /Helv 6 0 R >> >> /DA (/Helv 0 Tf 0 g) >>"),
            ],
            rootObjectNumber: 1);
    }
}
