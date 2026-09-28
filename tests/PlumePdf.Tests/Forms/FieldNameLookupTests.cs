using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary>Exact fully-qualified match, else a unique trailing-segment match; ambiguity throws naming every candidate.</summary>
public class FieldNameLookupTests
{
    [Fact]
    public void Indexer_ExactFullyQualifiedName_Resolves()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var field = form.Fields["topmostSubform[0].Page1[0].c1_01[0]"];
        Assert.Equal("topmostSubform[0].Page1[0].c1_01[0]", field.FullName);
    }

    [Fact]
    public void Indexer_UniqueTrailingSegment_ResolvesIgnoringArrayIndexSuffix()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var field = form.Fields["c1_01"];
        Assert.Equal("topmostSubform[0].Page1[0].c1_01[0]", field.FullName);
    }

    [Fact]
    public void Indexer_AmbiguousTrailingSegment_ThrowsNamingBothCandidates()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithAmbiguousNames());
        var form = PdfForm.For(document);

        var ex = Assert.Throws<PlumePdfException>(() => form.Fields["dup"]);
        Assert.Equal("PLUME6030", ex.Code);
        Assert.Contains("Group1[0].dup[0]", ex.Message);
        Assert.Contains("Group2[0].dup[0]", ex.Message);
    }

    [Fact]
    public void Indexer_UnknownName_ThrowsNotFound()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var ex = Assert.Throws<PlumePdfException>(() => form.Fields["DoesNotExist"]);
        Assert.Equal("PLUME6031", ex.Code);
    }

    [Fact]
    public void TryGetValue_UnknownName_ReturnsFalse()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        Assert.False(form.Fields.TryGetValue("DoesNotExist", out var field));
        Assert.Null(field);
    }

    [Fact]
    public void TryGetValue_AmbiguousName_StillThrows()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithAmbiguousNames());
        var form = PdfForm.For(document);

        Assert.Throws<PlumePdfException>(() => form.Fields.TryGetValue("dup", out _));
    }
}
