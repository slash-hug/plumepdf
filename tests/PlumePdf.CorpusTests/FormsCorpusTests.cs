using Xunit;

namespace PlumePdf.CorpusTests;

// =============================================================================
// STATUS (forms corpus gate):
//
// This file was originally authored against main before the field/widget facade
// had merged: at that time there was no PdfForm/FormField/Pdf.FillForm/
// doc.Form.Flatten() surface to call, and PdfOptions had none of the forms caps
// (MaxFormFields/MaxFieldTreeDepth/MaxWidgetsPerPage/MaxGeneratedAppearanceBytes).
// That situation, and the "skeleton first, grows as the surface lands"
// resolution below, mirrored Phase 3's ExtractionCorpusTests.cs when it
// was first authored (see that file's own STATUS block).
//
// **The field/widget facade has since merged** (Pdf.FillForm,
// doc.Form.Flatten(), and the PdfOptions caps all exist in this tree now) —
// **appearance generation for a fill-then-flatten pass never landed.**
// AcroFormFacadeSweepTests below extends this file onto the real facade
// (doc.Form.Fields enumerates every corpus AcroForm file without throwing, and
// every field's Name/FullName/FieldType/Value/AllowedValues access is exercised).
// The pre-existing AcroFormRawSweepTests stays as its own independent proof that
// this codebase's low-level reader survives real-world /AcroForm structures (merged
// field+widget dictionaries, deep /Kids trees, shared nodes) even without the
// field-model facade — a genuine second line of coverage, not a subset of the
// facade sweep, so both stay.
//
// AcroFormRawSweepTests below walks every corpus PDF's /AcroForm -> /Fields ->
// /Kids graph (when present) via raw object resolution, with its own cycle guard
// and depth cap (independent of PdfOptions's forms caps, so this sweep works the
// same regardless of which caps are configured) and asserts the walk completes
// without an unhandled exception and counts terminal field-like nodes (a dictionary
// with /T or /FT is a field; ISO 32000-1 §12.7.3.1's merged field+widget
// dictionaries are the common case in real forms, e.g. f1040: 136/136 fields are
// merged, so this walk never assumes fields and widgets are separate objects).
//
// Still open (independent of appearance generation):
//   - The pinned demo-forms corpus (corpora/demo-forms/f1040-2022.pdf, i-9.pdf)
//     has no dedicated exit-demo test here yet.
//   - Fill+reopen round-trip on the pinned pdf.js widget/annotation lane:
//     Pdf.FillForm(path, values) via SaveIncremental, reopen,
//     assert the filled values are present.
//   - qpdf external validation (QpdfInteropTests.cs's ProbeQpdf/RunQpdfCheck
//     pattern) of the *filled* output specifically — today's corpus files are
//     validated as-is by QpdfInteropTests.cs already; the new assertion is that
//     PlumePdf's own incremental-fill append still produces a qpdf-clean file.
//   - docs/cookbook/fill-form.md and flatten-form.md are still illustrative fenced
//     blocks, not MarkdownSnippets-embedded CookbookTests recipes (the doc-side
//     twin) — tests/PlumePdf.AotSmoke/Program.cs still carries only a PENDING
//     comment for that FillForm/Flatten exercise.
// =============================================================================

/// <summary>
/// Raw-object-graph invariants over every corpus PDF that carries an <c>/AcroForm</c>
/// entry in its catalog (ISO 32000-1 §12.7.2) — proves the reader survives real-world
/// field trees (merged field+widget dictionaries, deep/shared <c>/Kids</c> graphs)
/// using only the pre-existing public escape hatch (<see cref="ObjectRegistry"/>), independent
/// of the dedicated field-model facade (this file's header STATUS block tracks extending
/// the sweep onto that facade as an open follow-up). Self-skips (via
/// <see cref="CorpusFixture"/>'s hermetic-lane pattern) when no corpora are fetched, so
/// the default test lane stays hermetic; the dedicated <c>corpus</c> CI job runs
/// <c>scripts/fetch-corpora.sh</c> first and this then runs for real.
/// </summary>
public class AcroFormRawSweepTests
{
    // Independent of PdfOptions.MaxFieldTreeDepth: a defensive cap
    // so a pathological or cyclic /Kids graph in a hostile corpus fixture can't spin this
    // sweep forever. 64 comfortably exceeds any real-world field tree depth observed in
    // the f1040/I-9 probes.
    private const int MaxWalkDepth = 64;

    // Same sampling stride as ExtractionCorpusTests.FullCorpusSweepTests, for the same
    // reason: ~2,700 veraPDF-corpus-master files is too many to open on every CI run.
    private const int VeraCorpusSampleStride = 20;

    public static TheoryData<string> SweepFiles()
    {
        var data = new TheoryData<string>();

        foreach (var file in CorpusFixture.PdfJsSubsetFiles.OrderBy(static f => f, StringComparer.Ordinal))
        {
            data.Add(file);
        }

        if (CorpusFixture.CorporaAvailable)
        {
            var veraRoot = Path.Combine(CorpusFixture.CorporaRoot, "veraPDF-corpus-master");
            if (Directory.Exists(veraRoot))
            {
                var veraFiles = Directory.EnumerateFiles(veraRoot, "*.pdf", SearchOption.AllDirectories)
                    .OrderBy(static f => f, StringComparer.Ordinal)
                    .ToList();

                for (var i = 0; i < veraFiles.Count; i += VeraCorpusSampleStride)
                {
                    data.Add(veraFiles[i]);
                }
            }
        }

        if (data.Count == 0)
        {
            // Hermetic lane (no fetched corpora): xUnit fails a [Theory] whose MemberData
            // yields zero rows, so add the same empty-string sentinel the extraction sweep
            // uses; the test body treats it as a no-op. The corpus CI lane fetches first.
            data.Add(string.Empty);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SweepFiles))]
    public void WalksAcroFormFieldTree_WithoutUnhandledException(string path)
    {
        if (path.Length == 0)
        {
            return; // hermetic-lane sentinel — nothing fetched to sweep
        }

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path);
        }
        catch (PlumePdfException)
        {
            // Unopenable (encrypted without a usable empty password, malformed beyond
            // recovery, ...) — the open path itself is covered by Phase1CorpusTests /
            // ExtractionCorpusTests; not this sweep's concern.
            return;
        }

        using (document)
        {
            var acroForm = TryGetAcroForm(document);
            if (acroForm is null)
            {
                return; // not an /AcroForm-bearing file — nothing for this sweep to walk
            }

            var fieldCount = 0;
            var visited = new HashSet<int>();

            if (acroForm.TryGetValue(PdfName.Get("Fields"), out var fieldsValue) &&
                Resolve(document, fieldsValue) is PdfArray fields)
            {
                foreach (var field in fields)
                {
                    WalkFieldNode(document, Resolve(document, field), visited, depth: 0, ref fieldCount);
                }
            }

            // The walk completing without throwing (including on a cyclic/self-referential
            // /Kids graph, guarded by `visited` + MaxWalkDepth) IS the assertion — a hostile
            // or malformed field tree must degrade to "counted fewer fields than expected",
            // never an unhandled exception. fieldCount itself is non-negative by construction.
            Assert.True(fieldCount >= 0, $"{path}: negative field count should be impossible.");
        }
    }

    /// <summary>
    /// Resolves <c>catalog[/AcroForm]</c> to a dictionary, or <see langword="null"/> if the
    /// document has no catalog, no <c>/AcroForm</c> entry, or the entry doesn't resolve to a
    /// dictionary (lenient — malformed catalogs are Phase 1/3's concern, not this sweep's).
    /// </summary>
    private static PdfDictionary? TryGetAcroForm(PdfDocument document)
    {
        if (document.Objects.Trailer[PdfName.Root] is not PdfReference rootRef)
        {
            return null;
        }

        if (document.Objects[rootRef.Target] is not PdfDictionary catalog)
        {
            return null;
        }

        if (!catalog.TryGetValue(PdfName.Get("AcroForm"), out var acroFormValue))
        {
            return null;
        }

        return Resolve(document, acroFormValue) as PdfDictionary;
    }

    private static void WalkFieldNode(PdfDocument document, PdfObject? node, HashSet<int> visited, int depth, ref int fieldCount)
    {
        if (node is not PdfDictionary dict || depth > MaxWalkDepth)
        {
            return;
        }

        // /T (partial field name) or /FT (field type) marks a field node — including the
        // merged field+widget dictionary that's the common case on real forms (§12.7.3.1).
        // A pure intermediate /Kids container has neither.
        if (dict.ContainsKey(PdfName.Get("T")) || dict.ContainsKey(PdfName.Get("FT")))
        {
            fieldCount++;
        }

        if (!dict.TryGetValue(PdfName.Get("Kids"), out var kidsValue) ||
            Resolve(document, kidsValue) is not PdfArray kids)
        {
            return;
        }

        foreach (var kid in kids)
        {
            // Cycle guard: a hostile /Kids graph can share or self-reference a node. Only
            // guard actual indirect references — direct (inline) dictionaries can't cycle.
            if (kid is PdfReference kidRef)
            {
                if (!visited.Add(kidRef.Target.Number))
                {
                    continue; // already visited this object number — skip, don't recurse
                }
            }

            WalkFieldNode(document, Resolve(document, kid), visited, depth + 1, ref fieldCount);
        }
    }

    /// <summary>Dereferences <paramref name="value"/> if it's a <see cref="PdfReference"/>, otherwise returns it as-is.</summary>
    private static PdfObject? Resolve(PdfDocument document, PdfObject? value) =>
        value is PdfReference reference ? document.Objects[reference.Target] : value;
}

/// <summary>
/// A follow-up: sweeps every corpus PDF's <c>/AcroForm</c> through the real
/// <c>PdfForm</c>/<c>FormField</c> facade, rather than the raw object graph
/// <see cref="AcroFormRawSweepTests"/> proves survives — <c>doc.Form.Fields</c> must enumerate
/// without throwing, and every discovered field's <c>Name</c>/<c>FullName</c>/
/// <c>FieldType</c>/<c>Value</c>/<c>AllowedValues</c> accessors must not throw either, for
/// every real-world <c>/AcroForm</c> shape the pdf.js/veraPDF corpora contain. Shares
/// <see cref="AcroFormRawSweepTests.SweepFiles"/>'s file selection (same hermetic-lane
/// self-skip) rather than re-deriving it.
/// </summary>
public class AcroFormFacadeSweepTests
{
    [Theory]
    [MemberData(nameof(AcroFormRawSweepTests.SweepFiles), MemberType = typeof(AcroFormRawSweepTests))]
    public void FormFieldsEnumerate_WithoutUnhandledException(string path)
    {
        if (path.Length == 0)
        {
            return; // hermetic-lane sentinel — nothing fetched to sweep
        }

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(path);
        }
        catch (PlumePdfException)
        {
            // Unopenable — Phase1CorpusTests/ExtractionCorpusTests already cover the open
            // path itself; not this sweep's concern.
            return;
        }

        using (document)
        {
            FormFieldCollection fields;
            try
            {
                fields = document.Form.Fields;
            }
            catch (PlumePdfException ex) when (ex.Code is "PLUME6035")
            {
                // The untrusted-input resource-limit guards (MaxFormFields/MaxFieldTreeDepth/
                // MaxWidgetsPerPage) throw even under default options by design —
                // a corpus file that legitimately exceeds a conservative default is a correct
                // refusal, not a bug this sweep exists to catch.
                return;
            }

            foreach (var field in fields)
            {
                // The assertion IS that none of these throw for any real-world field shape —
                // mirroring AcroFormRawSweepTests' "walk completes" contract at the facade
                // layer instead of the raw object-graph layer.
                _ = field.Name;
                _ = field.FullName;
                _ = field.FieldType;
                _ = field.Value;
                _ = field.AllowedValues;
            }
        }
    }
}
