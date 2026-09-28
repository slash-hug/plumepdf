using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary><c>AcroFormReader</c>/<c>WidgetAnnotationReader</c>/<c>FieldTree</c> — field count/types/names against a hand-built fixture.</summary>
public class AcroFormReadingTests
{
    [Fact]
    public void Fields_HandBuiltFixture_DiscoversEveryFieldWithCorrectTypeAndName()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        Assert.True(form.HasFields);
        Assert.Equal(5, form.Fields.Count);

        Assert.Equal(FormFieldType.Text, form.Fields["Name"].FieldType);
        Assert.Equal(FormFieldType.CheckBox, form.Fields["Agree"].FieldType);
        Assert.Equal(FormFieldType.Radio, form.Fields["Pick"].FieldType);
        Assert.Equal(FormFieldType.ListBox, form.Fields["Color"].FieldType);
        Assert.Equal(FormFieldType.Text, form.Fields["c1_01[0]"].FieldType);
    }

    [Fact]
    public void Fields_ChoiceFieldWithoutComboFlag_ClassifiesAsListBox()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        // The fixture's /Color field sets no /Ff Combo bit, so it's a (non-editable) list box.
        Assert.Equal(FormFieldType.ListBox, form.Fields["Color"].FieldType);
    }

    [Fact]
    public void Fields_NestedFieldTree_BuildsFullyQualifiedName()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var field = form.Fields["topmostSubform[0].Page1[0].c1_01[0]"];
        Assert.Equal("c1_01[0]", field.Name);
        Assert.Equal(FormFieldType.Text, field.FieldType);
    }

    [Fact]
    public void NoAcroForm_ReadsAsEmptyFormRatherThanThrowing()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithoutAcroForm());
        var form = PdfForm.For(document);

        Assert.False(form.HasFields);
        Assert.Empty(form.Fields);
    }

    [Fact]
    public void CheckBox_AllowedValues_DiscoveredFromApNKeysNeverAssumedYes()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var allowed = form.Fields["Agree"].AllowedValues;
        Assert.Contains("Yes", allowed);
        Assert.Contains("Off", allowed);

        var radioAllowed = form.Fields["Pick"].AllowedValues;
        Assert.Contains("1", radioAllowed);
        Assert.DoesNotContain("Yes", radioAllowed);
    }

    [Fact]
    public void ChoiceField_AllowedValues_ComeFromOptEntries()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        Assert.Equal(["Red", "Green", "Blue"], form.Fields["Color"].AllowedValues);
        Assert.Equal("Red", form.Fields["Color"].Value);
    }
}
