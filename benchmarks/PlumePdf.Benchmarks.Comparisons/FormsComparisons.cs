using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks.Comparisons;

// Fixture note: `acroform_calculation_order.pdf` (the one pinned pdf.js AcroForm
// fixture scripts/fetch-corpora.sh fetches) carries exactly one field, so this is a
// correctness-shaped smoke benchmark more than an "N fields" throughput measurement.
// A `PlumePdf_FillAndFlattenFields` row (via `Pdf.FillForm`/`doc.Form.Flatten()`)
// belongs in the same [BenchmarkCategory] group as the iText7 row below.

/// <summary>
/// Forms fill/flatten, informational competitor comparison. Per the
/// project's AGPL isolation wall (the clean-room policy in AGENTS.md; docs/architecture.md's
/// Enforcement section: "manual and informational, run per-phase, not CI-gated"), this
/// project is the one place iText7's binary API is called — its source is never read, and
/// neither the package nor this project may be referenced from PlumePdf.sln.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class FormsComparisons
{
    private const string FormFixtureFileName = "acroform_calculation_order.pdf";

    private string _formFixturePath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _formFixturePath = ComparisonCorpora.ResolvePdfJsSubsetFile(FormFixtureFileName);
    }

    /// <summary>Sets every AcroForm field's value and flattens via iText7's public binary API (<c>PdfAcroForm.GetAcroForm</c>/<c>PdfFormField.SetValue</c>/<c>FlattenFields</c>) — never its source. Writes to an in-memory stream; the pinned corpus file itself is never modified.</summary>
    [Benchmark]
    [BenchmarkCategory("FillAndFlattenFields")]
    public int ITextSeven_FillAndFlattenFields()
    {
        using var reader = new iText.Kernel.Pdf.PdfReader(_formFixturePath);
        using var output = new MemoryStream();
        using var writer = new iText.Kernel.Pdf.PdfWriter(output);
        using var document = new iText.Kernel.Pdf.PdfDocument(reader, writer);

        var form = iText.Forms.PdfAcroForm.GetAcroForm(document, true);
        var filled = 0;
        foreach (var field in form.GetAllFormFields().Values)
        {
            field.SetValue("PlumePDF benchmark");
            filled++;
        }

        form.FlattenFields();
        return filled;
    }
}
