using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// Edge cases of the save-time clean-up found by review: every <c>/Perms</c> entry whose signature
/// went is dropped, <c>/SigFlags</c> goes with the last signature field, a surviving bookmark
/// drops a pruned <c>/SE</c> and the removed fields its submit action listed, a malformed
/// <c>/Fields</c> is not counted as form fields, and a structure key at the top of the integer
/// range does not overflow <c>/ParentTreeNextKey</c>.
/// </summary>
public class CleanupEdgeCaseTests
{
    public static TheoryData<SaveLayout> Layouts() => CleanupFixtures.Layouts();

    private static readonly string SignatureValue =
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <00> >>";

    private static PdfDocument SaveAndReopen(byte[] source, SaveLayout layout) =>
        PdfDocument.Open(CleanupFixtures.SaveAfterRemoval(source, "2", layout));

    [Theory]
    [MemberData(nameof(Layouts))]
    public void PermsEntryAndSigFlags_GoWithTheLastSignature(SaveLayout layout)
    {
        var source = CleanupFixtures.Compose(
            "/Perms << /UR3 60 0 R >> /AcroForm << /Fields [20 0 R] /SigFlags 3 >>",
            new Dictionary<int, string>
            {
                [20] = "<< /FT /Sig /T (Sig1) /V 60 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R >>",
                [60] = SignatureValue,
            },
            "", "/Annots [20 0 R]");

        using var saved = SaveAndReopen(source, layout);
        var catalog = CleanupFixtures.Catalog(saved);

        Assert.False(catalog.ContainsKey(PdfName.Perms));
        var form = (PdfDictionary)CleanupFixtures.Resolve(saved, catalog[PdfName.AcroForm])!;
        Assert.False(form.ContainsKey(PdfName.Get("SigFlags")));
    }

    [Fact]
    public void SigFlags_Stays_WhileASignatureFieldSurvives()
    {
        var source = CleanupFixtures.Compose(
            "/AcroForm << /Fields [20 0 R 21 0 R] /SigFlags 3 >>",
            new Dictionary<int, string>
            {
                [20] = "<< /FT /Sig /T (Sig1) /V 60 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R >>",
                [21] = "<< /FT /Sig /T (Sig2) /V 61 0 R /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>",
                [60] = SignatureValue,
                [61] = SignatureValue,
            },
            "/Annots [21 0 R]", "/Annots [20 0 R]");

        using var saved = SaveAndReopen(source, SaveLayout.Save);
        var form = (PdfDictionary)CleanupFixtures.Resolve(saved, CleanupFixtures.Catalog(saved)[PdfName.AcroForm])!;

        Assert.True(form.ContainsKey(PdfName.Get("SigFlags")));
    }

    // Linearize is left out here: a linearized file whose bookmark action lists form fields gets
    // an outline hint-table object count qpdf rejects, with or without a page removed — a separate
    // linearizer defect (#19) this test would otherwise report.
    [Theory]
    [InlineData(SaveLayout.Save)]
    [InlineData(SaveLayout.Optimize)]
    public void SurvivingBookmark_DropsPrunedStructureElementAndRemovedSubmitFields(SaveLayout layout)
    {
        var source = CleanupFixtures.Compose(
            "/Outlines 20 0 R /AcroForm << /Fields [30 0 R 31 0 R] >> /StructTreeRoot 40 0 R /MarkInfo << /Marked true >>",
            new Dictionary<int, string>
            {
                [20] = "<< /Type /Outlines /First 21 0 R /Last 21 0 R /Count 1 >>",
                [21] = "<< /Title (Kept bookmark) /Parent 20 0 R /Dest [3 0 R /Fit] /SE 41 0 R /A << /S /SubmitForm /F (https://example.invalid/) /Fields [30 0 R 31 0 R] >> >>",
                [30] = "<< /FT /Tx /T (OnRemoved) /V (REMOVED-FIELD-VALUE) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 4 0 R >>",
                [31] = "<< /FT /Tx /T (OnKept) /V (KEPT-FIELD-VALUE) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>",
                [40] = "<< /Type /StructTreeRoot /K [41 0 R 42 0 R] >>",
                [41] = "<< /Type /StructElem /S /P /P 40 0 R /Pg 4 0 R /K 0 >>",
                [42] = "<< /Type /StructElem /S /P /P 40 0 R /Pg 3 0 R /K 0 >>",
            },
            "/Annots [31 0 R]", "/Annots [30 0 R]");

        using var saved = SaveAndReopen(source, layout);
        var outlines = (PdfDictionary)CleanupFixtures.Resolve(saved, CleanupFixtures.Catalog(saved)[PdfName.Get("Outlines")])!;
        var item = (PdfDictionary)CleanupFixtures.Resolve(saved, outlines[PdfName.First])!;

        Assert.False(item.ContainsKey(PdfName.Get("SE")));
        var action = (PdfDictionary)CleanupFixtures.Resolve(saved, item[PdfName.Get("A")])!;
        var fields = (PdfArray)CleanupFixtures.Resolve(saved, action[PdfName.Get("Fields")])!;
        var only = Assert.Single(fields);
        var field = (PdfDictionary)CleanupFixtures.Resolve(saved, only)!;
        Assert.Equal("OnKept", ((PdfString)field[PdfName.T]).ToString());
    }

    [Fact]
    public void MalformedFieldsArray_IsNotCountedAsFormFields()
    {
        // /Fields names the catalog and the page being removed, neither of which is a field.
        var source = CleanupFixtures.Compose("/AcroForm << /Fields [1 0 R 4 0 R] >>", new Dictionary<int, string>());

        using var document = PdfDocument.Open(source);
        document.Pages.RemoveAt(1);
        RemovedPageFixtures.SaveToBytes(document, SaveLayout.Save);

        Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5021" && d.Message.Contains("form field", StringComparison.Ordinal));
    }

    [Fact]
    public void StructParentsKeyAtIntMaxValue_DoesNotOverflowTheNextKey()
    {
        var source = CleanupFixtures.Compose(
            "/StructTreeRoot 40 0 R /MarkInfo << /Marked true >>",
            new Dictionary<int, string>
            {
                [40] = "<< /Type /StructTreeRoot /K [41 0 R 42 0 R] /ParentTree 50 0 R /ParentTreeNextKey 5 >>",
                [41] = "<< /Type /StructElem /S /P /P 40 0 R /Pg 3 0 R /K 0 >>",
                [42] = "<< /Type /StructElem /S /P /P 40 0 R /Pg 4 0 R /K 0 >>",
                [50] = "<< /Nums [0 [42 0 R] 2147483647 [41 0 R]] >>",
            },
            "/StructParents 2147483647", "/StructParents 0");

        using var saved = SaveAndReopen(source, SaveLayout.Save);
        var root = (PdfDictionary)CleanupFixtures.Resolve(saved, CleanupFixtures.Catalog(saved)[PdfName.Get("StructTreeRoot")])!;
        var nextKey = (PdfNumber)CleanupFixtures.Resolve(saved, root[PdfName.Get("ParentTreeNextKey")])!;

        Assert.True(nextKey.Value >= 0, $"/ParentTreeNextKey overflowed to {nextKey.Value}.");
    }
}
