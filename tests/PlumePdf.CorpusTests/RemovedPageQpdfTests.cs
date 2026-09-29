using PlumePdf.Tests.PageRemoval;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// The independent structural check for <c>doc.Pages.RemoveAt</c> followed by a full-rewrite
/// save: <c>qpdf --check</c> must accept the output of every removed-page fixture,
/// both removal sets, in all three layouts. The non-flat page tree with inherited
/// <c>/Resources</c> and <c>/MediaBox</c> (shape N) is a hard gate in every layout, with and
/// without a removal: the original <c>/Pages</c> nodes are dropped on save, so the inherited
/// attributes must already live on the kept pages.
/// </summary>
/// <remarks>
/// Every shape, form shapes included: the save-time clean-up leaves no <c>/Fields</c>,
/// <c>/Kids</c>, <c>/Annots</c> or structure <c>/K</c> entry pointing at what was left out.
/// </remarks>
public class RemovedPageQpdfTests
{
    public static TheoryData<string, string, SaveLayout> ShapeMatrix()
    {
        var data = new TheoryData<string, string, SaveLayout>();
        foreach (var shape in RemovedPageFixtures.AllShapes)
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

    public static TheoryData<string, SaveLayout> NonFlatTreeCases()
    {
        var data = new TheoryData<string, SaveLayout>();
        foreach (var removal in RemovedPageFixtures.Removals.Prepend("none"))
        {
            foreach (var layout in Enum.GetValues<SaveLayout>())
            {
                data.Add(removal, layout);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ShapeMatrix))]
    public void SaveAfterRemoval_PassesQpdfCheck(string shape, string removal, SaveLayout layout)
    {
        if (!QpdfOracle.AvailableOrFailIfRequired())
        {
            return;
        }

        AssertQpdfAccepts(shape, RemovedPageFixtures.PageIndexes(removal), layout);
    }

    [Theory]
    [MemberData(nameof(NonFlatTreeCases))]
    public void NonFlatPageTree_PassesQpdfCheckInEveryLayout(string removal, SaveLayout layout)
    {
        if (!QpdfOracle.AvailableOrFailIfRequired())
        {
            return;
        }

        AssertQpdfAccepts("N", removal == "none" ? [] : RemovedPageFixtures.PageIndexes(removal), layout);
    }

    private static void AssertQpdfAccepts(string shape, int[] removed, SaveLayout layout)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-qpdf-removed-page-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.Open(RemovedPageFixtures.Build(shape).Bytes))
            {
                RemovedPageFixtures.RemovePages(document, removed);
                document.Save(outputPath, RemovedPageFixtures.OptionsFor(layout));
            }

            if (layout == SaveLayout.Linearize)
            {
                QpdfOracle.AssertValidLinearization(outputPath);
            }
            else
            {
                QpdfOracle.AssertClean(outputPath);
            }
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
