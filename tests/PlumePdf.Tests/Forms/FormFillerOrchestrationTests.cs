using PlumePdf.Documents;
using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary><c>FormFiller</c> orchestration diagnostics — <c>/XFA</c> dropped, <c>/Perms /UR3</c> invalidated, and the appearance-not-regenerated notice.</summary>
public class FormFillerOrchestrationTests
{
    [Fact]
    public void Fill_XfaHybrid_DropsXfaAndRecordsDiagnostic()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildXfaHybridWithUsageRights());
        var form = PdfForm.For(document);

        form.Fill(new Dictionary<string, string> { ["Name"] = "Jane" });

        Assert.Contains(document.Diagnostics, d => d.Code == "PLUME6037");
        Assert.False(document.Catalog is null); // sanity: catalog still resolves after mutation
    }

    [Fact]
    public void Fill_XfaHybrid_XfaKeyActuallyRemovedFromAcroForm()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.BuildXfaHybridWithUsageRights());
        try
        {
            using (var document = PdfDocument.Open(path))
            {
                PdfForm.For(document).Fill(new Dictionary<string, string> { ["Name"] = "Jane" });
                document.Save(path);
            }

            using var reopened = PdfDocument.Open(path);
            var acroForm = reopened.Catalog!.Dictionary[PdfName.Get("AcroForm")];
            var resolved = reopened.Objects[((PdfReference)acroForm).Target];
            Assert.False(((PdfDictionary)resolved).ContainsKey(PdfName.Get("XFA")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Fill_DocumentWithUsageRights_RecordsInvalidationDiagnostic()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildXfaHybridWithUsageRights());
        var form = PdfForm.For(document);

        form.Fill(new Dictionary<string, string> { ["Name"] = "Jane" });

        Assert.Contains(document.Diagnostics, d => d.Code == "PLUME6038");
    }

    [Fact]
    public void Fill_WhenAppearanceGenerationFails_RecordsStaleAppearanceDiagnostic()
    {
        // The stub era is over — the contract this guards is the FAILURE path:
        // when generation throws for a widget, fill still succeeds, the per-field coded
        // diagnostic lands, and the PLUME6041 summary points at the NeedAppearances hatch.
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var read = AcroFormReader.Read(document, null);

        FormFiller.Fill(document, read, new Dictionary<string, string> { ["Name"] = "Jane" }, needAppearances: false, appearanceGenerator: new ThrowingGenerator());

        Assert.Contains(document.Diagnostics, d => d.Code == "PLUME6041");
        Assert.Equal("Jane", FieldValues.GetValue(read.Fields.Single(f => f.ShortName == "Name")));
    }

    private sealed class ThrowingGenerator : PlumePdf.Documents.Forms.IAppearanceGenerator
    {
        public IndirectReference GenerateAppearance(PlumePdf.Documents.Forms.FieldValue value, PdfDictionary widget, string defaultAppearance, PdfDictionary resources, ObjectRegistry registry, PdfOptions options, DiagnosticCollection? diagnostics) =>
            throw new PlumePdfException("PLUME6043", "test: generation unavailable");
    }
}
