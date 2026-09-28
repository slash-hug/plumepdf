using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks.Comparisons;

// =============================================================================
// STATUS (forms comparison scenario):
//
// PdfPig_ReadFields and ITextSeven_FillFields below are real, running benchmarks
// against each competitor's own published binary API — neither needs
// PlumePdf's own form surface to exist, so both compile and run independent of it.
//
// A `PlumePdf_FillFields` row belongs in this same [BenchmarkCategory] group.
// `Pdf.FillForm` has since merged (per the "Post-merge
// status" note — appearance generation is the one piece that never landed, and
// this row doesn't need it: filling a field with no pre-existing /AP just records a
// PLUME6041 diagnostic rather than failing), so this row is now addable — mirroring
// how ExtractionComparisons.cs's
// `PlumePdf_MultiPageRealWorld`/`PlumePdf_SinglePageTrivial` rows were added only
// once extraction landed `PdfPage.ExtractText()` (see that file's own doc
// comment). Add it here, do not replace the two rows below.
//
// Fixture note: `acroform_calculation_order.pdf` (the one pinned pdf.js AcroForm
// fixture that exists in scripts/fetch-corpora.sh today)
// carries exactly one field, so this is a correctness-shaped smoke benchmark more
// than a meaningful "N fields" throughput measurement. Once a corpus
// expansion (the ~38-file pinned pdf.js widget/annotation lane) lands, retarget
// these benchmarks at a richer multi-field fixture from that set — same reasoning
// as ExtractionComparisons.cs's MultiPageRealWorld/SinglePageTrivial split.
// =============================================================================

/// <summary>
/// Forms read/fill, informational competitor comparison. Per the
/// project's AGPL isolation wall (the clean-room policy in AGENTS.md; docs/architecture.md's
/// Enforcement section: "manual and informational, run per-phase, not CI-gated"), this
/// project is the one place PdfPig's and iText7's own binary APIs are called — source of
/// either is never read, and neither package nor this project may be referenced from
/// PlumePdf.sln.
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

    /// <summary>Enumerates every AcroForm field via PdfPig's public binary API (<c>PdfDocument.TryGetForm</c>/<c>AcroForm.Fields</c>) — read-only, no fill/flatten support in PdfPig.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("ReadFields")]
    public int PdfPig_ReadFields()
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(_formFixturePath);
        if (!document.TryGetForm(out var form))
        {
            return 0;
        }

        var total = 0;
        foreach (var field in form.Fields)
        {
            total += field.Information.PartialName?.Length ?? 0;
        }

        return total;
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
