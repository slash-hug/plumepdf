using PlumePdf.Documents.PageRemoval;
using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageRemoval.CleanupFixtures3b;

namespace PlumePdf.Tests.PageRemoval;

/// <summary>
/// The structure-tree clean-up after <c>doc.Pages.RemoveAt</c>: structure elements whose content
/// was only on removed pages are pruned, mixed elements keep their kept content, no <c>/K</c>
/// holds a <c>null</c>, <c>/ParentTree</c> and <c>/IDTree</c> drop what was pruned, and a tree
/// left empty stays (empty, still valid) — in every save layout, and qpdf-clean.
/// </summary>
public class StructureTreeCleanupTests
{
    private static readonly PdfName K = PdfName.Get("K");
    private static readonly PdfName Pg = PdfName.Get("Pg");

    public static TheoryData<string, string, SaveLayout> StructureMatrix()
    {
        var data = new TheoryData<string, string, SaveLayout>();
        foreach (var shape in new[] { "T", "E3", "C" })
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
    [MemberData(nameof(StructureMatrix))]
    public void StructureShapes_TreeIsNullFreeConsistentAndQpdfClean(string shape, string removal, SaveLayout layout)
    {
        var removed = RemovedPageFixtures.PageIndexes(removal);
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build(shape).Bytes, removed, layout);

        using var reopened = PdfDocument.Open(saved);
        var elements = StructureElements(reopened);
        var expectedTexts = removed.Contains(0) ? [] : new[] { "PAGE-ONE-ACTUALTEXT-MARK" };
        if (shape == "E3")
        {
            expectedTexts = [];
        }

        Assert.Equal(expectedTexts, elements.Select(e => Assert.IsType<PdfString>(e[PdfName.Get("ActualText")]).GetText()));
        AssertPagesAreKept(reopened, elements);

        // /ParentTree lists only kept pages' /StructParents keys, and /ParentTreeNextKey stays above them.
        var keys = ParentTreeEntries(reopened).Select(static e => e.Key).ToList();
        int[] expectedKeys = expectedTexts.Length == 0 ? [] : [0];
        Assert.Equal(expectedKeys, keys);
        var nextKey = Assert.IsType<PdfNumber>(StructTreeRoot(reopened)[PdfName.Get("ParentTreeNextKey")]).ToInt32();
        Assert.All(keys, key => Assert.True(key < nextKey));

        // PdfStructureInfo reads back the same tree, with no deviation to report.
        var info = PdfStructureInfo.For(reopened);
        Assert.True(info.IsTagged);
        Assert.Equal(expectedTexts, ActualTexts(info.Root));
        Assert.Empty(info.Diagnostics);

        AssertQpdfClean(saved, $"Shape {shape}, removing page(s) {removal}, {layout}");
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void EmptiedTree_StaysWithEmptyKidsAndValidParentTree(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(RemovedPageFixtures.Build("E3").Bytes, [1], layout);

        using var reopened = PdfDocument.Open(saved);
        var root = StructTreeRoot(reopened);
        Assert.Empty(Assert.IsType<PdfArray>(Resolve(reopened, root[K])));
        Assert.Empty(ParentTreeEntries(reopened));
        Assert.True(root.ContainsKey(PdfName.Get("ParentTreeNextKey")));
        var markInfo = Assert.IsType<PdfDictionary>(Resolve(reopened, Catalog(reopened)[PdfName.Get("MarkInfo")]));
        Assert.True(Assert.IsType<PdfBoolean>(markInfo[PdfName.Get("Marked")]).Value);
        Assert.Null(PdfStructureInfo.For(reopened).Root);
    }

    [Theory]
    [MemberData(nameof(AllLayouts))]
    public void MixedTree_KeepsKeptContentAndPrunesTheRest(SaveLayout layout)
    {
        var saved = SaveAfterRemoval(MixedFixture(), [1], layout);

        using var reopened = PdfDocument.Open(saved);
        var elements = StructureElements(reopened);
        Assert.Equal(["P", "Sect", "P"], elements.Select(static e => ((PdfName)e[PdfName.Get("S")]).Value));
        AssertPagesAreKept(reopened, elements);

        // The mixed paragraph keeps only its kept-page MCR; its removed-page /Pg is gone.
        var mixed = elements[0];
        Assert.False(mixed.ContainsKey(Pg));
        var mcr = Assert.IsType<PdfDictionary>(Resolve(reopened, Assert.Single(NullFreeArray(reopened, mixed[K], "/K"))));
        Assert.Equal(reopened.Pages[0].Reference, Assert.IsType<PdfReference>(mcr[Pg]).Target);

        Assert.Equal([0, 2], ParentTreeEntries(reopened).Select(static e => e.Key));
        Assert.False(RemovedPageRecoverable(saved, "SECT-REMOVED-MARK"));
        Assert.False(RemovedPageRecoverable(saved, "DIV-REMOVED-MARK"));

        // /IDTree lists only the kept element.
        var idTree = Assert.IsType<PdfDictionary>(Resolve(reopened, StructTreeRoot(reopened)[PdfName.Get("IDTree")]));
        var names = IdTreeNames(reopened, idTree);
        Assert.Equal(["mixed"], names);

        var info = PdfStructureInfo.For(reopened);
        Assert.Empty(info.Diagnostics);
        var references = MarkedContent(info.Root).ToList();
        Assert.Equal([(0, 0), (1, 0)], references);

        AssertQpdfClean(saved, $"mixed, {layout}");
    }

    [Fact]
    public void MixedTree_CountsEveryPrunedElement()
    {
        using var document = AcroFormCleanupTests.Open(MixedFixture(), [1]);
        var context = AcroFormCleanupTests.Cleanup(document);
        StructureTreePass.Apply(context);

        // /P "gone" (54), /Div (55) and its /P (57).
        Assert.Equal(3, context.Counts.StructureElements);
    }

    [Fact]
    public void ElementEmptiedByAnEarlierPass_IsPruned()
    {
        // Nothing of the structure tree is on removed page 2, but a clean-up pass dropped the
        // kept-page link (object 40) the /Link element tags: that element is left with no
        // content and goes too, with its /ParentTree entry.
        using var document = AcroFormCleanupTests.Open(LinkFixture(), [1]);
        var before = CanonicalPdf.GraphSnapshot(document);
        var context = AcroFormCleanupTests.Cleanup(document);
        context.Excluded.Add(40);
        StructureTreePass.Apply(context);

        Assert.Equal(before, CanonicalPdf.GraphSnapshot(document));
        Assert.Equal(1, context.Counts.StructureElements);
        Assert.Contains(51, context.Excluded);

        using var output = new MemoryStream();
        FullRewriteWriter.Write(output, context.Objects, context.CatalogReference, context.Catalog, context.Pages, PdfOptions.Default with { Deterministic = true }, context.Excluded, context.Replacements);
        using var reopened = PdfDocument.Open(output.ToArray());
        var elements = StructureElements(reopened);
        Assert.Equal(["P"], elements.Select(static e => ((PdfName)e[PdfName.Get("S")]).Value));
        Assert.Equal([0], ParentTreeEntries(reopened).Select(static e => e.Key));
    }

    [Fact]
    public void Pass_NeverMutatesTheDocument()
    {
        using var document = AcroFormCleanupTests.Open(MixedFixture(), [1]);
        var before = CanonicalPdf.GraphSnapshot(document);
        var context = AcroFormCleanupTests.Cleanup(document);
        StructureTreePass.Apply(context);
        Assert.Equal(before, CanonicalPdf.GraphSnapshot(document));
    }

    // Root: /P "mixed" (page 1 MCR + removed page 2's MCID 0, /Pg page 2), /Sect with a /P on the
    // removed page and a /P on page 3, and a /Div whose only /P is on the removed page. Page keys:
    // page 1 → 0, page 2 → 1, page 3 → 2, in a /ParentTree with an intermediate /Kids node, and an
    // /IDTree naming the mixed and the removed-page paragraph.
    private static byte[] MixedFixture() => ComposeDocument(
        "/StructTreeRoot 50 0 R /MarkInfo << /Marked true >>",
        new Dictionary<int, string>
        {
            [50] = "<< /Type /StructTreeRoot /K [51 0 R 53 0 R 55 0 R] /ParentTree 60 0 R /ParentTreeNextKey 3 /IDTree 61 0 R >>",
            [51] = "<< /Type /StructElem /S /P /P 50 0 R /Pg 4 0 R /ID (mixed) /K [0 << /Type /MCR /Pg 3 0 R /MCID 0 >>] >>",
            [53] = "<< /Type /StructElem /S /Sect /P 50 0 R /K [54 0 R 56 0 R] >>",
            [54] = "<< /Type /StructElem /S /P /P 53 0 R /Pg 4 0 R /ID (gone) /ActualText (SECT-REMOVED-MARK) /K 0 >>",
            [56] = "<< /Type /StructElem /S /P /P 53 0 R /Pg 5 0 R /K 0 >>",
            [55] = "<< /Type /StructElem /S /Div /P 50 0 R /K [57 0 R] >>",
            [57] = "<< /Type /StructElem /S /P /P 55 0 R /Pg 4 0 R /ActualText (DIV-REMOVED-MARK) /K [0] >>",
            [60] = "<< /Kids [63 0 R] >>",
            [63] = "<< /Nums [0 [51 0 R] 1 [54 0 R] 2 [56 0 R]] /Limits [0 2] >>",
            [61] = "<< /Kids [62 0 R] >>",
            [62] = "<< /Names [(gone) 54 0 R (mixed) 51 0 R] /Limits [(gone) (mixed)] >>",
        },
        ["/StructParents 0", "/StructParents 1", "/StructParents 2"],
        [0, 1, 2]);

    // Page 1 carries a tagged link (object 40, /StructParent 1) and a paragraph.
    private static byte[] LinkFixture() => ComposeDocument(
        "/StructTreeRoot 50 0 R /MarkInfo << /Marked true >>",
        new Dictionary<int, string>
        {
            [40] = "<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest [4 0 R /Fit] /StructParent 1 >>",
            [50] = "<< /Type /StructTreeRoot /K [51 0 R 52 0 R] /ParentTree << /Nums [0 [52 0 R] 1 51 0 R] >> /ParentTreeNextKey 2 >>",
            [51] = "<< /Type /StructElem /S /Link /P 50 0 R /K [<< /Type /OBJR /Obj 40 0 R /Pg 3 0 R >>] >>",
            [52] = "<< /Type /StructElem /S /P /P 50 0 R /Pg 3 0 R /K 0 >>",
        },
        ["/Annots [40 0 R] /StructParents 0"],
        [0]);

    private static void AssertPagesAreKept(PdfDocument document, List<PdfDictionary> elements)
    {
        foreach (var element in elements)
        {
            if (element.TryGetValue(Pg, out var page))
            {
                var target = Assert.IsType<PdfReference>(page).Target;
                Assert.Contains(document.Pages, p => p.Reference == target);
            }
        }
    }

    private static List<string> IdTreeNames(PdfDocument document, PdfDictionary node)
    {
        var names = new List<string>();
        if (node.TryGetValue(PdfName.Get("Names"), out var leaf))
        {
            var array = Assert.IsType<PdfArray>(Resolve(document, leaf));
            for (var i = 0; i + 1 < array.Count; i += 2)
            {
                names.Add(Assert.IsType<PdfString>(array[i]).GetText());
                Assert.IsType<PdfDictionary>(Resolve(document, array[i + 1]));
            }
        }

        if (node.TryGetValue(PdfName.Kids, out var kids))
        {
            foreach (var kid in NullFreeArray(document, kids, "/IDTree /Kids"))
            {
                names.AddRange(IdTreeNames(document, Assert.IsType<PdfDictionary>(Resolve(document, kid))));
            }
        }

        return names;
    }

    private static List<string> ActualTexts(PdfStructureNode? node)
    {
        var texts = new List<string>();
        if (node is PdfStructureElement element)
        {
            if (element.ActualText is { } text)
            {
                texts.Add(text);
            }

            foreach (var child in element.Children)
            {
                texts.AddRange(ActualTexts(child));
            }
        }

        return texts;
    }

    private static IEnumerable<(int Page, int Mcid)> MarkedContent(PdfStructureNode? node) => node switch
    {
        PdfMarkedContentReference mcr => [(mcr.PageIndex, mcr.Mcid)],
        PdfStructureElement element => element.Children.SelectMany(MarkedContent),
        _ => [],
    };

    private static bool RemovedPageRecoverable(byte[] saved, string marker) =>
        PlumePdf.Tests.TestSupport.RecoveredBytes.AnyRecoveredObjectContains(saved, marker);
}
