using Xunit;

namespace PlumePdf.Tests.Forms;

/// <summary><c>FormFlattener</c>/<c>doc.Form.Flatten()</c> — stamps existing <c>/AP /N</c> appearances into page content, drops widgets/<c>/AcroForm</c>.</summary>
public class FormFlattenTests
{
    [Fact]
    public void Flatten_ReopenedDocument_HasNoAcroForm()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);
        form.Fields["Agree"].Checked = true;

        using var flattened = form.Flatten();

        Assert.NotNull(flattened.Catalog);
        Assert.False(flattened.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
    }

    [Fact]
    public void Flatten_PageAnnotsNoLongerListsWidgets()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);

        using var flattened = form.Flatten();

        var page = flattened.Pages[0];
        if (page.Dictionary.TryGetValue(PdfName.Get("Annots"), out var annotsValue))
        {
            var annots = Assert.IsType<PdfArray>(annotsValue);
            Assert.Empty(annots);
        }
    }

    [Fact]
    public void Flatten_CheckedCheckbox_StampsItsOnStateAppearanceIntoPageContent()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);
        form.Fields["Agree"].Checked = true;

        using var flattened = form.Flatten();

        var page = flattened.Pages[0];
        var contentRef = (PdfReference)page.Dictionary[PdfName.Get("Contents")];
        var content = (PdfStream)flattened.Objects[contentRef.Target];
        var text = System.Text.Encoding.ASCII.GetString(content.RawBytes.Span);

        Assert.Contains("Do", text);
        Assert.Contains("cm", text);
    }

    [Fact]
    public void Flatten_RoundTripsThroughSaveAndReopen()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.Build());
        var form = PdfForm.For(document);
        form.Fields["Agree"].Checked = true;

        using var flattened = form.Flatten();
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-flatten-{Guid.NewGuid():N}.pdf");
        try
        {
            flattened.Save(path);
            using var reopened = PdfDocument.Open(path);
            Assert.Single(reopened.Pages);
            Assert.False(reopened.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A widget with no <c>/AP</c> no longer aborts the whole flatten with a
    /// thrown <c>PLUME6036</c> — flatten synthesizes an appearance where it can
    /// (generated or pre-existing) and degrades per-widget for the rest. The historical fixture
    /// (an empty-<c>/V</c> text field, no <c>/DA</c>, no <c>/DR</c>) must now flatten without
    /// throwing, whichever branch it lands in.
    /// </summary>
    [Fact]
    public void Flatten_FieldWithNoAppearance_NoLongerAbortsTheDocument()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithFieldMissingAppearance());
        var form = PdfForm.For(document);

        using var flattened = form.Flatten();

        Assert.NotNull(flattened.Catalog);
        Assert.False(flattened.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
    }

    /// <summary>
    /// A widget with a
    /// <c>/V</c> value and a <c>/DA</c> but no <c>/AP</c> gets its appearance synthesized by
    /// the same <c>AppearanceGenerator</c> Fill and Rasterize already use, stamped into the
    /// page, and dropped from <c>/Annots</c> — and the result survives a save/reopen round
    /// trip (proving the scratch-registry import produced no dangling references).
    /// </summary>
    [Fact]
    public void Flatten_VOnlyWidget_SynthesizesStampsAndRoundTrips()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithVOnlyAndValuelessWidgets());
        var form = PdfForm.For(document);

        using var flattened = form.Flatten();

        Assert.Contains("Hello", flattened.Pages[0].ExtractText().Text, StringComparison.Ordinal);

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-flatten-vonly-{Guid.NewGuid():N}.pdf");
        try
        {
            flattened.Save(path);
            using var reopened = PdfDocument.Open(path);
            Assert.Contains("Hello", reopened.Pages[0].ExtractText().Text, StringComparison.Ordinal);
            Assert.False(reopened.Catalog!.Dictionary.ContainsKey(PdfName.Get("AcroForm")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A widget with no <c>/AP</c> AND nothing synthesizable (no <c>/V</c>)
    /// degrades per-widget — a <c>PLUME6036</c> Warning diagnostic, the widget kept as a live
    /// annotation — while the rest of the document (here, the synthesizable sibling) still
    /// flattens.
    /// </summary>
    [Fact]
    public void Flatten_ValuelessNoApWidget_DegradesPerWidgetAndStaysLive()
    {
        using var document = PdfDocument.Open(FormsTestDocuments.BuildWithVOnlyAndValuelessWidgets());
        var form = PdfForm.For(document);

        using var flattened = form.Flatten();

        Assert.Contains(document.Diagnostics, d => d.Code == "PLUME6036" && d.Severity == DiagnosticSeverity.Warning);

        var page = flattened.Pages[0];
        var annots = Assert.IsType<PdfArray>(page.Dictionary[PdfName.Get("Annots")]);
        var live = Assert.Single(annots);
        var liveDict = Assert.IsType<PdfDictionary>(flattened.Objects[((PdfReference)live).Target]);
        Assert.Equal("Empty", ((PdfString)liveDict[PdfName.Get("T")]).GetText());

        // The synthesizable sibling was flattened, not kept.
        Assert.Contains("Hello", page.ExtractText().Text, StringComparison.Ordinal);
    }

    // Flatten's encrypted-source refusal reuses PLUME6012 — the exact code path
    // MergeSplitTests already exercises for Pdf.Merge/Pdf.Split against an encrypted source
    // (same guard, same message shape); not duplicated here to avoid a second hand-rolled
    // encryption fixture builder for identical coverage.
}
