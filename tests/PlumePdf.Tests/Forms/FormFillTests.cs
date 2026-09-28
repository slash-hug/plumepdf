using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary><c>FormField.Value</c> typed setters — text, checkbox/radio on-state (with <c>/AS</c>), choice, and fail-fast validation.</summary>
public class FormFillTests
{
    [Fact]
    public void Value_TextField_RoundTripsThroughLiveDocument()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        form.Fields["Name"].Value = "Jane Q. Public";

        Assert.Equal("Jane Q. Public", form.Fields["Name"].Value);
    }

    [Fact]
    public void Value_TextField_RoundTripsAcrossSaveAndReopen()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            using (var document = PdfDocument.Open(path))
            {
                PdfForm.For(document).Fields["Name"].Value = "Jane Q. Public";
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            Assert.Equal("Jane Q. Public", PdfForm.For(reopened).Fields["Name"].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Value_TextField_RoundTripsAcrossSaveIncrementalAndReopen()
    {
        // FieldValues.SetValue must mark the field's own dictionary dirty on
        // ObjectRegistry, or SaveIncremental — the default, signature-preserving save path —
        // silently drops the fill entirely (the dirty-object-set contract). Save's full
        // rewrite always happened to pick this up regardless (it walks the whole live object
        // graph from scratch), which is why the sibling Save-based test above never caught
        // this — SaveIncremental needs its own coverage.
        var sourceBytes = FormsTestDocuments.Build();
        var path = WriterTestDocuments.WriteTempFile(sourceBytes);
        try
        {
            using (var document = PdfDocument.Open(path))
            {
                PdfForm.For(document).Fields["Name"].Value = "Jane Q. Public";
                document.SaveIncremental(path);
            }

            var updatedBytes = File.ReadAllBytes(path);
            Assert.True(updatedBytes.Length > sourceBytes.Length, "SaveIncremental produced no appendix for the field-value fill.");
            Assert.Equal(sourceBytes, updatedBytes[..sourceBytes.Length]); // Prior bytes untouched

            using var reopened = PdfDocument.Open(path);
            Assert.Equal("Jane Q. Public", PdfForm.For(reopened).Fields["Name"].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Value_Checkbox_SetsVAndAsToDiscoveredOnState()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        form.Fields["Agree"].Value = "Yes";

        Assert.Equal("Yes", form.Fields["Agree"].Value);
        Assert.True(form.Fields["Agree"].Checked);
    }

    [Fact]
    public void Checked_SetToFalse_WritesOffState()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        form.Fields["Agree"].Checked = true;
        Assert.Equal("Yes", form.Fields["Agree"].Value);

        form.Fields["Agree"].Checked = false;
        Assert.Equal("Off", form.Fields["Agree"].Value);
    }

    [Fact]
    public void Value_Checkbox_UnrecognizedState_ThrowsCoded()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var ex = Assert.Throws<PlumePdfException>(() => form.Fields["Agree"].Value = "Nope");
        Assert.Equal("PLUME6033", ex.Code);
    }

    [Fact]
    public void Checked_GroupedCheckboxesSharingValue_TrueOnlyForMemberOwningThatOnState()
    {
        // Every kid of the W-9's "tax classification" group carried the group's
        // selected value /V /3, and Checked compared Value against the literal "Off" — so all
        // seven members reported Checked == true. ISO 32000-1 §12.7.4.2.3: a checkbox is on
        // only when /V names one of its OWN /AP /N appearance states.
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithGroupedCheckboxesSharingValue());
        var form = PdfForm.For(document);

        Assert.False(form.Fields["Boxes3a-b_ReadOrder[0].c1_1[0]"].Checked);
        Assert.False(form.Fields["Boxes3a-b_ReadOrder[0].c1_1[1]"].Checked);
        Assert.True(form.Fields["Boxes3a-b_ReadOrder[0].c1_1[2]"].Checked);
    }

    [Fact]
    public void Checked_StandaloneCheckbox_OffAmongDiscoveredStates_DoesNotFlipIt()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithGroupedCheckboxesSharingValue());
        var form = PdfForm.For(document);

        Assert.Contains("Off", form.Fields["Agree"].AllowedValues);
        Assert.True(form.Fields["Agree"].Checked);

        form.Fields["Agree"].Value = "Off";
        Assert.False(form.Fields["Agree"].Checked);
    }

    [Fact]
    public void Checked_CheckboxWithoutAppearance_FallsBackToNonOffValue()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithGroupedCheckboxesSharingValue());
        var form = PdfForm.For(document);

        Assert.Empty(form.Fields["Bare"].AllowedValues);
        Assert.True(form.Fields["Bare"].Checked);
    }

    [Fact]
    public void Checked_OnNonCheckboxField_Throws()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var ex = Assert.Throws<PlumePdfException>(() => _ = form.Fields["Name"].Checked);
        Assert.Equal("PLUME6032", ex.Code);
    }

    [Fact]
    public void Value_Radio_SetsAsOnTheWidgetToo()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        form.Fields["Pick"].Value = "1";

        Assert.Equal("1", form.Fields["Pick"].Value);
    }

    [Fact]
    public void Value_ChoiceField_ValidOption_Sets()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        form.Fields["Color"].Value = "Blue";

        Assert.Equal("Blue", form.Fields["Color"].Value);
    }

    [Fact]
    public void Value_ChoiceField_InvalidOption_ThrowsCoded()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        var ex = Assert.Throws<PlumePdfException>(() => form.Fields["Color"].Value = "Purple");
        Assert.Equal("PLUME6034", ex.Code);
    }

    [Fact]
    public void PdfForm_Fill_SetsMultipleFieldsByName()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        form.Fill(new Dictionary<string, string>
        {
            ["Name"] = "Jane Q. Public",
            ["Agree"] = "Yes",
            ["Color"] = "Green",
        });

        Assert.Equal("Jane Q. Public", form.Fields["Name"].Value);
        Assert.Equal("Yes", form.Fields["Agree"].Value);
        Assert.Equal("Green", form.Fields["Color"].Value);
    }
}
