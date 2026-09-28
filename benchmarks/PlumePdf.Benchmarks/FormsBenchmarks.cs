using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks;

// =============================================================================
// STATUS (PlumePdf-only forms benchmarks):
//
// Forms benchmarking calls for "fill N fields, flatten" benchmarks with stored
// baselines. Pdf.FillForm/doc.Form.Flatten() exist and compile, but
// appearance generation for a fill-then-flatten pass never landed, so a Flatten()
// benchmark needs a fixture whose fields already carry /AP, or it hits PLUME6036
// instead of measuring steady-state cost. That benchmark is still open (same
// reasoning as this file's sibling FormsCorpusTests.cs/AotSmoke Program.cs; see
// those files' own STATUS notes), no longer because the surface doesn't compile,
// but because no such richer fixture is wired up here yet.
//
// AcroFormRawWalkThroughput below benchmarks what IS available today: walking a
// document's raw /AcroForm -> /Fields -> /Kids graph through the pre-existing public
// object-graph escape hatch (doc.Objects), the same technique
// tests/PlumePdf.CorpusTests/FormsCorpusTests.cs's AcroFormRawSweepTests uses for
// correctness. This gives a real, running baseline for "how fast can PlumePdf reach
// every field in a form" ahead of the field-model facade landing — benchmarks in this
// repo are added incrementally as the API they measure lands (see
// PlumePdf.Benchmarks.Comparisons/ExtractionComparisons.cs's own doc comment for the
// Phase 3 precedent of the same pattern).
//
// Once a richer multi-field fixture with pre-existing /AP is wired up, extend this file
// with (do not replace the benchmark below):
//   - FillFields(int n): Pdf.FillForm(path, values) via SaveIncremental for N field
//     values, timed end-to-end including reopen.
//   - Flatten(): doc.Form.Flatten() timed on a filled document.
//   - Stored baselines alongside the existing extraction-benchmark pattern (a
//     matching forms-benchmark baseline file, out of this file's ownership).
// =============================================================================

/// <summary>
/// Raw <c>/AcroForm</c> field-tree walk throughput — mirrors
/// <see cref="ExtractionBenchmarks"/>'s corpus-file resolution pattern. Uses the
/// one pinned pdf.js AcroForm fixture that exists in <c>scripts/fetch-corpora.sh</c>
/// today (<c>acroform_calculation_order.pdf</c>) — a single-field
/// fixture, so this is more a floor-cost baseline than an N-fields throughput curve;
/// retarget at a richer multi-field fixture once a corpus expansion lands.
/// </summary>
[MemoryDiagnoser]
public class FormsBenchmarks
{
    private PdfDocument _document = null!;

    [GlobalSetup]
    public void Setup()
    {
        _document = PdfDocument.Open(ComparisonCorpora.ResolvePdfJsSubsetFile("acroform_calculation_order.pdf"));
    }

    [GlobalCleanup]
    public void Cleanup() => _document.Dispose();

    [Benchmark(Baseline = true)]
    public int AcroFormRawWalkThroughput()
    {
        if (_document.Objects.Trailer[PdfName.Root] is not PdfReference rootRef ||
            _document.Objects[rootRef.Target] is not PdfDictionary catalog ||
            !catalog.TryGetValue(PdfName.Get("AcroForm"), out var acroFormValue) ||
            Resolve(acroFormValue) is not PdfDictionary acroForm ||
            !acroForm.TryGetValue(PdfName.Get("Fields"), out var fieldsValue) ||
            Resolve(fieldsValue) is not PdfArray fields)
        {
            return 0;
        }

        var fieldCount = 0;
        var visited = new HashSet<int>();
        foreach (var field in fields)
        {
            WalkFieldNode(Resolve(field), visited, depth: 0, ref fieldCount);
        }

        return fieldCount;
    }

    private void WalkFieldNode(PdfObject? node, HashSet<int> visited, int depth, ref int fieldCount)
    {
        if (node is not PdfDictionary dict || depth > 64)
        {
            return;
        }

        if (dict.ContainsKey(PdfName.Get("T")) || dict.ContainsKey(PdfName.Get("FT")))
        {
            fieldCount++;
        }

        if (!dict.TryGetValue(PdfName.Get("Kids"), out var kidsValue) || Resolve(kidsValue) is not PdfArray kids)
        {
            return;
        }

        foreach (var kid in kids)
        {
            if (kid is PdfReference kidRef && !visited.Add(kidRef.Target.Number))
            {
                continue;
            }

            WalkFieldNode(Resolve(kid), visited, depth + 1, ref fieldCount);
        }
    }

    private PdfObject? Resolve(PdfObject? value) =>
        value is PdfReference reference ? _document.Objects[reference.Target] : value;
}
