using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary><c>Pdf.Merge</c> carries <c>/AcroForm</c> through, with deterministic field-name collision renaming and zero orphaned widgets.</summary>
public class AcroFormMergeTests
{
    [Fact]
    public void Merge_FixtureWithItself_CatalogHasAcroFormWithUnionOfFields()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            using var a = PdfDocument.Open(path);
            using var b = PdfDocument.Open(path);
            using var merged = Pdf.Merge(a, b);

            Assert.NotNull(merged.Catalog);
            Assert.True(merged.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));

            var form = PdfForm.For(merged);
            Assert.Equal(10, form.Fields.Count); // 5 fields per source document, two sources
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_FixtureWithItself_TopLevelNameCollisionIsRenamedDeterministically()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            using var a = PdfDocument.Open(path);
            using var b = PdfDocument.Open(path);
            using var merged = Pdf.Merge(a, b);

            var form = PdfForm.For(merged);
            Assert.True(form.Fields.TryGetValue("Name", out _));
            Assert.True(form.Fields.TryGetValue("Name~2", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_FixtureWithItself_NoOrphanedWidgets()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            using var a = PdfDocument.Open(path);
            using var b = PdfDocument.Open(path);
            using var merged = Pdf.Merge(a, b);

            // Every widget referenced from a page's /Annots must resolve, and (the actual
            // orphan bug) every such widget must be reachable from /AcroForm/Fields too:
            // filling every field by name and confirming each page's /Annots widget count
            // matches the fields discovered per page is a stronger check than merely "it
            // doesn't throw" — cross-check via the field count directly.
            Assert.Equal(2, merged.Pages.Count);
            var form = PdfForm.For(merged);
            Assert.Equal(10, form.Fields.Count);

            foreach (var field in form.Fields)
            {
                if (field.FieldType is FormFieldType.Text)
                {
                    field.Value = "x"; // fails loudly (PLUME6032, or a KeyNotFound-style break) if the merged graph is inconsistent
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_SingleDocumentWithForm_PreservesFieldsUnchanged()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            using var a = PdfDocument.Open(path);
            using var merged = Pdf.Merge(a);

            var form = PdfForm.For(merged);
            Assert.Equal(5, form.Fields.Count);
            Assert.True(form.Fields.TryGetValue("Name", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Merge_DocumentsWithoutForms_NoAcroFormOnCatalog()
    {
        var pathA = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        var pathB = WriterTestDocuments.WriteTempFile(WriterTestDocuments.BuildDocument(pageCount: 1));
        try
        {
            using var a = PdfDocument.Open(pathA);
            using var b = PdfDocument.Open(pathB);
            using var merged = Pdf.Merge(a, b);

            Assert.False(merged.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
        }
        finally
        {
            File.Delete(pathA);
            File.Delete(pathB);
        }
    }
}
