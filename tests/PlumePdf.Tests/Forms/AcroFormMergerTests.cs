using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary><c>AcroFormMerger</c> carries <c>/AcroForm</c> through <c>Pdf.Merge</c>.</summary>
public class AcroFormMergerTests
{
    [Fact]
    public void Merge_FormLevelDaQAndCo_AreCarriedThrough()
    {
        // A field that inherits its appearance defaults from the AcroForm root (rather than
        // setting its own /DA/Q) loses them silently if the merged /AcroForm drops the
        // form-level entries — exactly the fidelity regression this test guards against. /CO
        // (calculation order) must be read and preserved, never executed (plan out-of-scope
        // note), which still means it must survive a merge intact.
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithFormLevelDefaults());
        using var merged = Pdf.Merge(document);

        var acroForm = Assert.IsType<PdfDictionary>(
            merged.Objects[Assert.IsType<PdfReference>(merged.Catalog!.Dictionary[PdfName.AcroForm]).Target]);

        Assert.True(acroForm.TryGetValue(PdfName.DA, out var daValue));
        Assert.Equal("/Helv 10 Tf 0 g", Assert.IsType<PdfString>(daValue).GetText());

        Assert.True(acroForm.TryGetValue(PdfName.Q, out var qValue));
        Assert.True(Assert.IsType<PdfNumber>(qValue).TryToInt32(out var q));
        Assert.Equal(1, q);

        Assert.True(acroForm.TryGetValue(PdfName.CO, out var coValue));
        var co = Assert.IsType<PdfArray>(coValue);
        Assert.Single(co);
        Assert.IsType<PdfReference>(co[0]);
    }
}
