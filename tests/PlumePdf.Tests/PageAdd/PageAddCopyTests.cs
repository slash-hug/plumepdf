using PlumePdf.Objects;
using Xunit;
using static PlumePdf.Tests.PageAdd.PageAddFixtures;

namespace PlumePdf.Tests.PageAdd;

/// <summary>
/// Copying a page from another open document into an opened one (<see cref="PageCollection.Add"/>,
/// <see cref="PageCollection.Insert"/>): where it lands, that it is live at once, that the source
/// can go away, and that both save paths persist it.
/// </summary>
public class PageAddCopyTests
{
    [Theory]
    [InlineData(0, new[] { "New", "One", "Two" })]
    [InlineData(1, new[] { "One", "New", "Two" })]
    [InlineData(2, new[] { "One", "Two", "New" })]
    public void Insert_PutsTheCopyAtTheIndex(int index, string[] expected)
    {
        using var document = PdfDocument.Open(Text(["One", "Two"]));
        using var source = PdfDocument.Open(Text(["New"]));

        var added = document.Pages.Insert(index, source.Pages[0]);

        Assert.Same(added, document.Pages[index]);
        Assert.Equal(expected, PageTexts(document));
    }

    [Fact]
    public void TheReturnedPage_IsLiveInTheDestination()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        using var source = PdfDocument.Open(Text(["New"]));

        var added = document.Pages.Add(source.Pages[0]);

        Assert.Same(added.Dictionary, document.Objects[added.Reference]);
        Assert.Equal("New", added.ExtractText().Text.Trim());
        Assert.NotEqual(source.Pages[0].Reference, added.Reference);
    }

    [Fact]
    public void InheritedResources_AreWrittenOntoTheCopy()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        using var source = PdfDocument.Open(Text(["New"], inheritResources: true));

        var added = document.Pages.Add(source.Pages[0]);

        Assert.True(added.Dictionary.ContainsKey(PdfName.Get("Resources")));
        Assert.Equal("New", added.ExtractText().Text.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSourceCanBeDisposed_AndBothSavePathsPersistTheCopy(bool incremental)
    {
        var input = WriteTemp(Text(["One"]));
        var sourcePath = WriteTemp(Text(["New"]));
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            {
                using (var source = PdfDocument.Open(sourcePath))
                {
                    document.Pages.Add(source.Pages[0]);
                }

                if (incremental)
                {
                    document.SaveIncremental(output);
                }
                else
                {
                    document.Save(output);
                }
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Empty(reopened.Diagnostics);
            Assert.Equal(["One", "New"], PageTexts(reopened));
            Assert.NotNull(reopened.Pages[1].Rasterize());
            if (incremental)
            {
                var original = File.ReadAllBytes(input);
                Assert.Equal(original, File.ReadAllBytes(output).AsSpan(0, original.Length).ToArray());
            }
        }
        finally
        {
            File.Delete(input);
            File.Delete(sourcePath);
            File.Delete(output);
        }
    }

    [Fact]
    public void AddingTheSamePageTwice_CopiesItTwice()
    {
        using var document = PdfDocument.Open(Text(["One"]));
        using var source = PdfDocument.Open(Text(["New"]));

        var first = document.Pages.Add(source.Pages[0]);
        var second = document.Pages.Add(source.Pages[0]);

        Assert.NotEqual(first.Reference, second.Reference);
        var firstContents = ((PdfReference)first.Dictionary[PdfName.Get("Contents")]).Target;
        var secondContents = ((PdfReference)second.Dictionary[PdfName.Get("Contents")]).Target;
        Assert.NotEqual(firstContents, secondContents);
        Assert.Equal(["One", "New", "New"], PageTexts(document));
    }

    [Fact]
    public void RemoveAtThenAdd_KeepsTheAddedPage()
    {
        var input = WriteTemp(Text(["One", "Two"]));
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Text(["New"])))
            {
                document.Pages.RemoveAt(0);
                document.Pages.Add(source.Pages[0]);
                document.Save(output);
            }

            using var reopened = PdfDocument.Open(output);
            Assert.Equal(["Two", "New"], PageTexts(reopened));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void AddedThenRemoved_IsLeftOutOfAFullSave()
    {
        var input = WriteTemp(Text(["One"]));
        var output = TempPath();
        try
        {
            using (var document = PdfDocument.Open(input))
            using (var source = PdfDocument.Open(Text(["Zebra"])))
            {
                document.Pages.Add(source.Pages[0]);
                document.Pages.RemoveAt(1);
                document.Save(output);
            }

            Assert.False(Recovered(output).AsSpan().IndexOf("Zebra"u8) >= 0);
            using var reopened = PdfDocument.Open(output);
            Assert.Equal(["One"], PageTexts(reopened));
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Fact]
    public void Deterministic_IncrementalOutputIsByteIdenticalAcrossRuns()
    {
        var input = WriteTemp(Text(["One"]));
        var outputs = new[] { TempPath(), TempPath() };
        try
        {
            foreach (var output in outputs)
            {
                using var document = PdfDocument.Open(input, new PdfOptions { Deterministic = true });
                using var source = PdfDocument.Open(Text(["New", "Other"]));
                document.Pages.Add(source.Pages[1]);
                document.Pages.Insert(0, source.Pages[0]);
                document.SaveIncremental(output);
            }

            Assert.Equal(File.ReadAllBytes(outputs[0]), File.ReadAllBytes(outputs[1]));
        }
        finally
        {
            File.Delete(input);
            Array.ForEach(outputs, File.Delete);
        }
    }

    // Every decoded content stream in the file, concatenated: what a recovery tool could read back.
    private static byte[] Recovered(string path)
    {
        using var document = PdfDocument.Open(path);
        var all = new List<byte>();
        for (var number = 1; number < document.Objects.NextFreshObjectNumber; number++)
        {
            if (document.Objects[new IndirectReference(number, 0)] is PdfStream stream)
            {
                all.AddRange(stream.GetDecodedBytes(PdfFilterRegistry.Default).ToArray());
            }
        }

        return [.. all];
    }
}
