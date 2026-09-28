using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary><c>Pdf.FillForm</c>/<c>Pdf.FlattenForm</c> verbs (the explicit-overloads shape).</summary>
public class PdfFillFormVerbTests
{
    [Fact]
    public void FillForm_DictionaryOverload_SavesBackToPath()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            Pdf.FillForm(path, new Dictionary<string, string> { ["Name"] = "Jane Q. Public" });

            using var reopened = PdfDocument.Open(path);
            Assert.Equal("Jane Q. Public", PdfForm.For(reopened).Fields["Name"].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FillForm_TupleParamsOverload_SavesBackToPath()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            Pdf.FillForm(path, ("Name", "Jane Q. Public"), ("Agree", "Yes"));

            using var reopened = PdfDocument.Open(path);
            var form = PdfForm.For(reopened);
            Assert.Equal("Jane Q. Public", form.Fields["Name"].Value);
            Assert.Equal("Yes", form.Fields["Agree"].Value);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FillForm_UnknownFieldName_ThrowsAndLeavesFileUntouched()
    {
        var bytes = FormsTestDocuments.Build();
        var path = WriterTestDocuments.WriteTempFile(bytes);
        try
        {
            Assert.Throws<PlumePdfException>(() => Pdf.FillForm(path, new Dictionary<string, string> { ["NoSuchField"] = "x" }));

            // Save is never reached (Fill throws first) — the file on disk is unchanged.
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FillForm_WithFlattenTrue_ProducesFormlessDocument()
    {
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            Pdf.FillForm(path, new Dictionary<string, string> { ["Agree"] = "Yes" }, flatten: true);

            using var reopened = PdfDocument.Open(path);
            Assert.False(reopened.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FlattenForm_ToSamePath_ReplacesSourceInPlace()
    {
        // outputPath omitted means Pdf.FlattenForm writes the flattened result back
        // over the source path — the exact shape that requires releasing the source
        // document's memory-mapped handles first (Windows refuses to replace a file with a
        // live mapped section; silent on macOS/Linux, which is why this needs its own test
        // rather than relying on local-run "it worked").
        var path = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        try
        {
            Pdf.FlattenForm(path);

            using var flattened = PdfDocument.Open(path);
            Assert.False(flattened.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FlattenForm_ToSeparateOutputPath_LeavesSourceUntouched()
    {
        var sourcePath = WriterTestDocuments.WriteTempFile(FormsTestDocuments.Build());
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-flatten-out-{Guid.NewGuid():N}.pdf");
        try
        {
            var before = File.ReadAllBytes(sourcePath);
            Pdf.FlattenForm(sourcePath, outputPath);

            Assert.Equal(before, File.ReadAllBytes(sourcePath));

            using var flattened = PdfDocument.Open(outputPath);
            Assert.False(flattened.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }
}
