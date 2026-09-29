using PlumePdf.Documents.PageRemoval;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageRemoval.CleanupFixtures3b;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// The form clean-up after <c>doc.Pages.RemoveAt</c>: the saved (and reopened) form lists only
/// what is still placed on a kept page — no <c>null</c> in <c>/Fields</c> or <c>/Kids</c>, radio
/// <c>/Opt</c> still aligned with <c>/Kids</c>, a value naming a removed widget's state reset to
/// <c>/Off</c>, <c>/CO</c> filtered, <c>/XFA</c> dropped when a field went, <c>/Perms /DocMDP</c>
/// dropped with its signature — in every save layout, and qpdf-clean.
/// </summary>
public class AcroFormCleanupTests
{
    public static TheoryData<string, string, SaveLayout> FormMatrix()
    {
        var data = new TheoryData<string, string, SaveLayout>();
        foreach (var shape in RemovedPageFixtures.FormShapes)
        {
            foreach (var removal in RemovedPageFixtures.Removals)
            {
                foreach (var layout in Enum.GetValues<SaveLayout>())
                {
                    data.Add(shape, removal, layout);
                }
            }
        }

        return data;
    }

    public static TheoryData<SaveLayout> AllLayouts() => Layouts();

    [Theory]
    [MemberData(nameof(FormMatrix))]
    public void FormShapes_SaveNullFreeAndQpdfClean(string shape, string removal, SaveLayout layout)
    {
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build(shape).Bytes, RemovedPageFixtures.PageIndexes(removal), layout);

        using var reopened = PdfDocument.Open(saved);
        FieldTreeNodes(reopened);
        _ = reopened.Form.Fields.Count;
        AssertQpdfClean(saved, $"Shape {shape}, removing page(s) {removal}, {layout}");
    }

    [Theory]
    [MemberData(nameof(FormMatrix))]
    public void FormShapes_FormListsOnlyWhatIsLeftOnKeptPages(string shape, string removal, SaveLayout layout)
    {
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build(shape).Bytes, RemovedPageFixtures.PageIndexes(removal), layout);

        using var reopened = PdfDocument.Open(saved);
        var names = reopened.Form.Fields.Select(static f => f.FullName).Order(StringComparer.Ordinal).ToList();
        string[] expected = shape switch
        {
            "K" => ["shared"],
            "R" => ["radio"],
            "C" => ["radio", "shared"],
            _ => [],
        };
        Assert.Equal(expected, names);

        // Every widget still listed sits on a kept page.
        foreach (var node in FieldTreeNodes(reopened))
        {
            if (node.TryGetValue(PdfName.P, out var page))
            {
                var pageReference = Assert.IsType<PdfReference>(page);
                Assert.Contains(reopened.Pages, p => p.Reference == pageReference.Target);
            }
        }

        // An emptied form keeps its dictionary, with an empty /Fields.
        Assert.Equal(expected.Length, NullFreeArray(reopened, AcroForm(reopened)[PdfName.Fields], "/Fields").Count);
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void FieldAcrossPages_KeepsItsValueAndOnlyTheKeptWidget(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build("K").Bytes, [1], layout);

        using var reopened = PdfDocument.Open(saved);
        var field = Assert.Single(reopened.Form.Fields);
        Assert.Equal("KEPT-FIELD-VALUE-MARK", field.Value);
        var node = Assert.Single(FieldTreeNodes(reopened), static n => n.ContainsKey(PdfName.T));
        var widget = Assert.IsType<PdfDictionary>(Resolve(reopened, Assert.Single(NullFreeArray(reopened, node[PdfName.Kids], "/Kids"))));
        Assert.Equal(reopened.Pages[0].Reference, Assert.IsType<PdfReference>(widget[PdfName.P]).Target);
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void RadioGroup_OptStaysAlignedAndRemovedOnStateBecomesOff(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build("R").Bytes, [1], layout);

        using var reopened = PdfDocument.Open(saved);
        var field = Assert.Single(reopened.Form.Fields);
        Assert.Equal("Off", field.Value);
        var node = Assert.Single(FieldTreeNodes(reopened), static n => n.ContainsKey(PdfName.T));
        Assert.Single(NullFreeArray(reopened, node[PdfName.Kids], "/Kids"));
        var opt = NullFreeArray(reopened, node[PdfName.Opt], "/Opt");
        Assert.Equal("alpha", Assert.IsType<PdfString>(Assert.Single(opt)).GetText());
        Assert.Same(PdfName.Off, node[PdfName.V]);
        AssertQpdfClean(saved, $"R, {layout}");
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void RadioGroup_FirstWidgetRemoved_DropsOptAtItsIndexAndResetsValueAndDefault(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(RadioFixture(value: "/0"), [1], layout);

        using var reopened = PdfDocument.Open(saved);
        var node = Assert.Single(FieldTreeNodes(reopened), static n => n.ContainsKey(PdfName.T));
        var kid = Assert.IsType<PdfDictionary>(Resolve(reopened, Assert.Single(NullFreeArray(reopened, node[PdfName.Kids], "/Kids"))));
        Assert.Equal(reopened.Pages[0].Reference, Assert.IsType<PdfReference>(kid[PdfName.P]).Target);
        Assert.Equal("beta", Assert.IsType<PdfString>(Assert.Single(NullFreeArray(reopened, node[PdfName.Opt], "/Opt"))).GetText());
        Assert.Same(PdfName.Off, node[PdfName.V]);
        Assert.Same(PdfName.Off, node[PdfName.DV]);
        Assert.Equal("Off", Assert.Single(reopened.Form.Fields).Value);
        AssertQpdfClean(saved, $"radio, {layout}");
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void RadioGroup_ValueOnKeptWidget_IsKept(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(RadioFixture(value: "/1"), [1], layout);

        using var reopened = PdfDocument.Open(saved);
        var node = Assert.Single(FieldTreeNodes(reopened), static n => n.ContainsKey(PdfName.T));
        Assert.Equal(PdfName.Get("1"), node[PdfName.V]);
        Assert.Equal(PdfName.Get("1"), node[PdfName.DV]);
        Assert.Equal("1", Assert.Single(reopened.Form.Fields).Value);
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void CalculationOrder_DropsRemovedFields(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(CalculationFixture(), [1], layout);

        using var reopened = PdfDocument.Open(saved);
        Assert.Equal(["kept"], reopened.Form.Fields.Select(static f => f.FullName));
        var co = NullFreeArray(reopened, AcroForm(reopened)[PdfName.CO], "/CO");
        var only = Assert.IsType<PdfDictionary>(Resolve(reopened, Assert.Single(co)));
        Assert.Equal("kept", Assert.IsType<PdfString>(only[PdfName.T]).GetText());
        AssertQpdfClean(saved, $"CO, {layout}");
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void Xfa_IsDroppedWhenAFieldIsRemoved(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(XfaFixture(), [1], layout);

        using var reopened = PdfDocument.Open(saved);
        Assert.False(AcroForm(reopened).ContainsKey(PdfName.XFA));
        Assert.False(RecoveredContains(saved, "XFA-DATASET-MARK"));
        AssertQpdfClean(saved, $"XFA, {layout}");
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void Xfa_IsKeptWhenNoFieldIsRemoved(SaveLayout layout)
    {
        // Page 3 carries no field; removing it removes nothing from the form.
        var saved = SaveAfterRemoval(XfaFixture(), [2], layout);

        using var reopened = PdfDocument.Open(saved);
        Assert.True(AcroForm(reopened).ContainsKey(PdfName.XFA));
        Assert.Equal(["kept", "secret"], reopened.Form.Fields.Select(static f => f.FullName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void SignatureOnRemovedPage_DropsDocMdpAndIsNotReportedInvalidated(SaveLayout layout)
    {
        using var document = PdfDocument.Open(RemovedPageFixtures.Build("Q").Bytes);
        RemovedPageFixtures.RemovePages(document, [1]);
        var saved = RemovedPageFixtures.SaveToBytes(document, layout);
        Assert.DoesNotContain(document.Diagnostics, static d => d.Code == "PLUME5014");

        using var reopened = PdfDocument.Open(saved);
        Assert.Empty(reopened.Form.Fields);
        var catalog = Catalog(reopened);
        if (catalog.TryGetValue(PdfName.Perms, out var perms))
        {
            Assert.False(Assert.IsType<PdfDictionary>(Resolve(reopened, perms)).ContainsKey(PdfName.DocMDP));
        }

        AssertQpdfClean(saved, $"Q, {layout}");
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void IndirectFormAndArrays_AreCompacted(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(IndirectFixture(), [1], layout);

        using var reopened = PdfDocument.Open(saved);
        Assert.Equal(["group.b", "shared"], reopened.Form.Fields.Select(static f => f.FullName).Order(StringComparer.Ordinal));
        var nodes = FieldTreeNodes(reopened);
        var group = Assert.Single(nodes, n => n.TryGetValue(PdfName.T, out var t) && ((PdfString)t).GetText() == "group");
        Assert.Single(NullFreeArray(reopened, group[PdfName.Kids], "/Kids"));
        Assert.DoesNotContain(nodes, n => n.TryGetValue(PdfName.T, out var t) && ((PdfString)t).GetText() is "gone" or "a");
        Assert.False(RecoveredContains(saved, "GONE-VALUE-MARK"));
        Assert.False(RecoveredContains(saved, "A-VALUE-MARK"));
        Assert.True(RecoveredContains(saved, "B-VALUE-MARK"));
        AssertQpdfClean(saved, $"indirect, {layout}");
    }

    [Fact]
    public void Counts_FieldsWidgetsAndXfa()
    {
        using var document = Open(IndirectFixture(), [1]);
        var context = Cleanup(document);
        AcroFormPass.Apply(context);

        // Fields "gone" (with its child "gone.x"), "group.a"; the shared field's removed widget.
        Assert.Equal(3, context.Counts.Fields);
        Assert.Equal(1, context.Counts.Widgets);
        Assert.Equal(0, context.Counts.XfaForms);

        using var xfaDocument = Open(XfaFixture(), [1]);
        var xfa = Cleanup(xfaDocument);
        AcroFormPass.Apply(xfa);
        Assert.Equal(1, xfa.Counts.Fields);
        Assert.Equal(1, xfa.Counts.XfaForms);
    }

    [Fact]
    public void Pass_NeverMutatesTheDocument()
    {
        using var document = PdfDocument.Open(IndirectFixture());
        RemovedPageFixtures.RemovePages(document, [1]);
        var before = CanonicalPdf.GraphSnapshot(document);
        var context = Cleanup(document);
        AcroFormPass.Apply(context);
        Assert.Equal(before, CanonicalPdf.GraphSnapshot(document));
    }

    // A radio group whose first widget (state /0, Opt "alpha") is on removed page 2 and whose
    // second (state /1, Opt "beta") is on kept page 1.
    internal static byte[] RadioFixture(string value) => ComposeDocument(
        "/AcroForm << /Fields [30 0 R] >>",
        new Dictionary<int, string>
        {
            [30] = $"<< /FT /Btn /Ff 32768 /T (radio) /V {value} /DV {value} /Kids [32 0 R 31 0 R] /Opt [(alpha) (beta)] >>",
            [31] = "<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /P 3 0 R /Parent 30 0 R /AS /Off /AP << /N << /1 13 0 R /Off 13 0 R >> >> >>",
            [32] = "<< /Type /Annot /Subtype /Widget /Rect [10 10 30 30] /P 4 0 R /Parent 30 0 R /AS /Off /AP << /N << /0 13 0 R /Off 13 0 R >> >> >>",
            [13] = "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream",
        },
        ["/Annots [31 0 R]", "/Annots [32 0 R]"]);

    // Two text fields in /CO: "calc" on removed page 2, "kept" on kept page 1.
    private static byte[] CalculationFixture() => ComposeDocument(
        "/AcroForm << /Fields [30 0 R 31 0 R] /CO [30 0 R 31 0 R] >>",
        new Dictionary<int, string>
        {
            [30] = "<< /FT /Tx /T (calc) /V (CALC-VALUE-MARK) /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 4 0 R >>",
            [31] = "<< /FT /Tx /T (kept) /V (1) /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 3 0 R >>",
        },
        ["/Annots [31 0 R]", "/Annots [30 0 R]"]);

    // A form with XFA data, one field on removed page 2 and one on kept page 1.
    private static byte[] XfaFixture()
    {
        const string Dataset = "<xfa:datasets>XFA-DATASET-MARK</xfa:datasets>";
        return ComposeDocument(
            "/AcroForm << /Fields [30 0 R 31 0 R] /XFA [(datasets) 40 0 R] >>",
            new Dictionary<int, string>
            {
                [30] = "<< /FT /Tx /T (secret) /V (FIELD-VALUE-MARK) /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 4 0 R >>",
                [31] = "<< /FT /Tx /T (kept) /V (1) /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 3 0 R >>",
                [40] = $"<< /Length {Dataset.Length} >>\nstream\n{Dataset}\nendstream",
            },
            ["/Annots [31 0 R]", "/Annots [30 0 R]"]);
    }

    // An indirect /AcroForm with an indirect /Fields and indirect /Kids arrays: "shared" (a kept
    // and a removed widget), "group" (child "a" on the removed page, child "b" kept) and "gone"
    // (a non-terminal field whose only child "x" is on the removed page).
    internal static byte[] IndirectFixture() => ComposeDocument(
        "/AcroForm 20 0 R",
        new Dictionary<int, string>
        {
            [20] = "<< /Fields 21 0 R /DA (/Helv 0 Tf 0 g) >>",
            [21] = "[30 0 R 33 0 R 36 0 R]",
            [30] = "<< /FT /Tx /T (shared) /V (SHARED-VALUE-MARK) /Kids 22 0 R >>",
            [22] = "[31 0 R 32 0 R]",
            [31] = "<< /Type /Annot /Subtype /Widget /Rect [10 10 100 30] /P 3 0 R /Parent 30 0 R >>",
            [32] = "<< /Type /Annot /Subtype /Widget /Rect [10 40 100 60] /P 4 0 R /Parent 30 0 R >>",
            [33] = "<< /T (group) /Kids 23 0 R >>",
            [23] = "[34 0 R 35 0 R]",
            [34] = "<< /FT /Tx /T (a) /V 24 0 R /Parent 33 0 R /Type /Annot /Subtype /Widget /Rect [10 70 100 90] /P 4 0 R >>",
            [24] = "(A-VALUE-MARK)",
            [35] = "<< /FT /Tx /T (b) /V (B-VALUE-MARK) /Parent 33 0 R /Type /Annot /Subtype /Widget /Rect [10 70 100 90] /P 3 0 R >>",
            [36] = "<< /T (gone) /Kids [37 0 R] >>",
            [37] = "<< /FT /Tx /T (x) /V (GONE-VALUE-MARK) /Parent 36 0 R /Type /Annot /Subtype /Widget /Rect [10 100 100 120] /P 4 0 R >>",
        },
        ["/Annots [31 0 R 35 0 R]", "/Annots [32 0 R 34 0 R 37 0 R]"]);

    internal static PdfDocument Open(byte[] bytes, int[] removed)
    {
        var document = PdfDocument.Open(bytes);
        RemovedPageFixtures.RemovePages(document, removed);
        return document;
    }

    // The clean-up context Save would build (before any pass ran).
    internal static SaveCleanupContext Cleanup(PdfDocument document)
    {
        var openTimeTree = new HashSet<int> { 2, 3, 4, 5 };
        IReadOnlyList<IndirectReference> openTimePages = [new(3, 0), new(4, 0), new(5, 0)];
        var pages = document.Pages.Select(static p => (p.Reference, p.Dictionary)).ToList();
        var (excluded, removedPages, removedFields) = RemovedSetBuilder.Build(document.Objects, document.Catalog!.Reference, document.Catalog.Dictionary, openTimeTree, openTimePages, pages, PdfOptions.Default);
        var context = new SaveCleanupContext(document.Objects, document.Catalog.Reference, document.Catalog.Dictionary, pages, excluded, removedPages, PdfOptions.Default);
        context.RemovedFields.UnionWith(removedFields);
        return context;
    }

    private static bool RecoveredContains(byte[] saved, string marker) =>
        PlumePdf.Tests.TestSupport.RecoveredBytes.AnyRecoveredObjectContains(saved, marker);
}
