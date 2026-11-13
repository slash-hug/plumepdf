using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// Duplicating a page within its own document (<see cref="PageCollection.Add"/> with one of the
/// document's own pages): the page dictionary, its content and its annotations are copied, and
/// everything else is shared; widgets are left off with <c>PLUME6094</c>.
/// </summary>
public class PageAddDuplicateTests
{
    private static readonly PdfName Annots = PdfName.Get("Annots");
    private static readonly PdfName Contents = PdfName.Get("Contents");
    private static readonly PdfName Resources = PdfName.Get("Resources");
    private static readonly PdfName Subtype = PdfName.Get("Subtype");
    private static readonly PdfName Popup = PdfName.Get("Popup");
    private static readonly PdfName Parent = PdfName.Get("Parent");
    private static readonly PdfName P = PdfName.Get("P");
    private static readonly PdfName Irt = PdfName.Get("IRT");
    private static readonly PdfName Dest = PdfName.Get("Dest");

    [Fact]
    public void ResourcesAreShared_AndTheContentIsCopied()
    {
        using var document = PdfDocument.Open(Annotated());
        var original = document.Pages[0];

        var copy = document.Pages.Add(original);

        Assert.Same(original.Dictionary[Resources], copy.Dictionary[Resources]);
        var originalContents = ((PdfReference)original.Dictionary[Contents]).Target;
        var copyContents = ((PdfReference)copy.Dictionary[Contents]).Target;
        Assert.NotEqual(originalContents, copyContents);
        Assert.Equal(["One", "One"], PageTexts(document));
    }

    [Fact]
    public void Widgets_AreLeftOff_AndReportedOnce()
    {
        using var document = PdfDocument.Open(Annotated());

        var copy = document.Pages.Add(document.Pages[0]);
        document.Pages.Add(document.Pages[0]);

        Assert.DoesNotContain(Annotations(document, copy), static a => a[Subtype] is PdfName { Value: "Widget" });
        Assert.Single(document.Diagnostics, static d => d.Code == "PLUME6094");
        Assert.Single(document.Form.Fields);
    }

    [Fact]
    public void Annotations_AreCopied_PointAtTheNewPage_AndPopupPairsPointAtEachOther()
    {
        using var document = PdfDocument.Open(Annotated());
        var original = document.Pages[0];

        var copy = document.Pages.Add(original);

        var annotations = Annotations(document, copy);
        var text = annotations.Single(static a => a.ContainsKey(Popup));
        var popup = annotations.Single(static a => a[Subtype] is PdfName { Value: "Popup" });
        Assert.Equal(copy.Reference, ((PdfReference)text[P]).Target);
        Assert.Same(popup, document.Objects[((PdfReference)text[Popup]).Target]);
        Assert.Same(text, document.Objects[((PdfReference)popup[Parent]).Target]);
        Assert.DoesNotContain(Annotations(document, original), a => ReferenceEquals(a, text));
    }

    [Fact]
    public void RepliesAndLinks_KeepTheirTargets()
    {
        using var document = PdfDocument.Open(Annotated());

        var copy = document.Pages.Add(document.Pages[0]);

        var annotations = Annotations(document, copy);
        var reply = annotations.Single(static a => a.ContainsKey(Irt));
        Assert.Equal(new IndirectReference(30, 0), ((PdfReference)reply[Irt]).Target);
        var link = annotations.Single(static a => a[Subtype] is PdfName { Value: "Link" });
        Assert.Equal(new IndirectReference(10, 0), ((PdfReference)((PdfArray)link[Dest])[0]).Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatingTwice_SavesAValidPageTree(bool incremental)
    {
        var input = WriteTemp(Annotated());
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            {
                document.Pages.Add(document.Pages[0]);
                document.Pages.Insert(0, document.Pages[0]);
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
            Assert.Empty(reopened.Diagnostics);
            Assert.Equal(["One", "One", "One"], PageTexts(reopened));
            Assert.Equal(3, reopened.Pages.Select(static p => p.Reference).Distinct().Count());
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static List<PdfDictionary> Annotations(PdfDocument document, PdfPage page) =>
        [.. ((PdfArray)page.Dictionary[Annots]).Select(a => (PdfDictionary)document.Objects[((PdfReference)a).Target])];

    /// <summary>
    /// One text page (10) with a text annotation (30) and its pop-up (31), a reply to it (32), a
    /// link to the page itself (33), and a text-field widget (34).
    /// </summary>
    private static byte[] Annotated() => Text(
        ["One"],
        catalogExtra: "/AcroForm << /Fields [34 0 R] >>",
        extra:
        [
            (30, "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /P 10 0 R /Contents (note) /Popup 31 0 R >>"),
            (31, "<< /Type /Annot /Subtype /Popup /Rect [10 0 60 40] /P 10 0 R /Parent 30 0 R >>"),
            (32, "<< /Type /Annot /Subtype /Text /Rect [0 20 10 30] /P 10 0 R /Contents (reply) /IRT 30 0 R >>"),
            (33, "<< /Type /Annot /Subtype /Link /Rect [0 40 50 50] /Dest [10 0 R /Fit] >>"),
            (34, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /P 10 0 R /Rect [0 60 80 80] >>"),
        ],
        pageExtra: "/Annots [30 0 R 31 0 R 32 0 R 33 0 R 34 0 R]");
}
