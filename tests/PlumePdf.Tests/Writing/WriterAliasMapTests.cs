using System.Text;
using PlumePdf.Objects;
using PlumePdf.Tests.PageRemoval;
using Xunit;

namespace PlumePdf.Tests.Writing;

/// <summary>
/// The full-rewrite writer's alias map: a duplicate object is written as its canonical
/// original, so every reference to it lands on the one written copy and the duplicate itself
/// is never numbered or written. Aliases that would write something other than what was found
/// equal, or reach past the removed-page exclusion set, are refused (<c>PLUME5010</c>).
/// </summary>
public class WriterAliasMapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalImagesOnTwoPages_AreWrittenOnce(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2, identicalImages: true));
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0) };
        var options = PdfOptions.Default with { Optimize = optimize };

        var deduplicated = StreamWriteTransformTests.Write(document, options, transform: null, aliases: aliases);
        var baseline = StreamWriteTransformTests.Write(document, options, transform: null);

        using var saved = PdfDocument.Open(deduplicated);
        var first = ImagePagesDocument.PageImageReference(saved, 0, "Im0");
        var second = ImagePagesDocument.PageImageReference(saved, 1, "Im0");
        Assert.Equal(first, second);
        Assert.Equal("PIXELS-SAME", Encoding.ASCII.GetString(ImagePagesDocument.PageImage(saved, 1, "Im0").RawBytes.Span));

        Assert.Equal(2, CountOccurrences(baseline, "PIXELS-SAME"));
        Assert.Equal(1, CountOccurrences(deduplicated, "PIXELS-SAME"));
        Assert.Equal(ObjectCount(baseline) - 1, ObjectCount(deduplicated));
        CleanupFixtures3b.AssertQpdfClean(deduplicated, $"aliased save (Optimize={optimize})");
    }

    [Fact]
    public void Transform_IsOfferedTheCanonicalOnly()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3, identicalImages: true));
        var aliases = new Dictionary<int, int>
        {
            [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0),
            [ImagePagesDocument.Image(2)] = ImagePagesDocument.Image(0),
        };
        var transform = new StreamWriteTransformTests.RecordingTransform(_ => true, static (_, original) => original);

        StreamWriteTransformTests.Write(document, PdfOptions.Default, transform, aliases: aliases);

        Assert.Contains(ImagePagesDocument.Image(0), transform.Offered);
        Assert.DoesNotContain(ImagePagesDocument.Image(1), transform.Offered);
        Assert.DoesNotContain(ImagePagesDocument.Image(2), transform.Offered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeterministicAliasedSave_IsByteIdenticalAcrossRuns(bool optimize)
    {
        var options = PdfOptions.Default with { Optimize = optimize, Deterministic = true };
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0) };
        byte[] Run()
        {
            using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2, identicalImages: true));
            return StreamWriteTransformTests.Write(document, options, transform: null, aliases: aliases);
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void AliasToAnExcludedObject_IsRefused()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2, identicalImages: true));
        var excluded = new HashSet<int> { ImagePagesDocument.Image(0) };
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0) };

        var ex = Assert.Throws<PlumePdfException>(() => StreamWriteTransformTests.Write(document, PdfOptions.Default, transform: null, excluded: excluded, aliases: aliases));
        Assert.Equal("PLUME5010", ex.Code);
    }

    // A deduplicator must choose canonicals the save will actually write: the exclusion set it
    // reads is the one the save uses, and an alias onto any member of it is refused. A stream
    // only a removed page reached is not excluded (it is merely unreferenced), so it may still
    // serve as a canonical and is written once for the kept page.
    [Fact]
    public void ExcludedForSave_IsTheSetTheSaveRefusesToAliasOnto()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2, identicalImages: true));
        var pageTreeRoot = document.Catalog!.Dictionary.TryGetValue(PdfName.Get("Pages"), out var root) && root is PdfReference rootReference
            ? rootReference.Target.Number
            : throw new InvalidOperationException("The fixture's catalog has no /Pages reference.");
        document.Pages.RemoveAt(0);

        var excluded = document.ExcludedForSave(PdfOptions.Default);

        Assert.Contains(ImagePagesDocument.Page(0), excluded);
        Assert.Contains(pageTreeRoot, excluded);
        Assert.DoesNotContain(ImagePagesDocument.Page(1), excluded);
        Assert.DoesNotContain(ImagePagesDocument.Image(0), excluded);
        Assert.DoesNotContain(ImagePagesDocument.Image(1), excluded);

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-alias-{Guid.NewGuid():N}.pdf");
        try
        {
            var refused = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = pageTreeRoot };
            var ex = Assert.Throws<PlumePdfException>(() => document.SaveWithWriteHooks(path, PdfOptions.Default, transform: null, refused));
            Assert.Equal("PLUME5010", ex.Code);
            Assert.Contains("excluded", ex.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(path));

            var accepted = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0) };
            document.SaveWithWriteHooks(path, PdfOptions.Default, transform: null, accepted);

            var bytes = File.ReadAllBytes(path);
            using var saved = PdfDocument.Open(bytes);
            Assert.Equal("PIXELS-SAME", Encoding.ASCII.GetString(ImagePagesDocument.PageImage(saved, 0, "Im0").RawBytes.Span));
            Assert.Equal(1, CountOccurrences(bytes, "PIXELS-SAME"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AliasChain_IsRefused()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3, identicalImages: true));
        var aliases = new Dictionary<int, int>
        {
            [ImagePagesDocument.Image(2)] = ImagePagesDocument.Image(1),
            [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0),
        };

        var ex = Assert.Throws<PlumePdfException>(() => StreamWriteTransformTests.Write(document, PdfOptions.Default, transform: null, aliases: aliases));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Fact]
    public void AliasingARetainedPage_IsRefused()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2));
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Page(1)] = ImagePagesDocument.Page(0) };

        var ex = Assert.Throws<PlumePdfException>(() => StreamWriteTransformTests.Write(document, PdfOptions.Default, transform: null, aliases: aliases));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Fact]
    public void AliasToARetainedPage_IsRefused()
    {
        // The writer rebuilds a retained page itself, so the page object is never the content
        // an image was found equal to.
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2));
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Page(0) };

        var ex = Assert.Throws<PlumePdfException>(() => StreamWriteTransformTests.Write(document, PdfOptions.Default, transform: null, aliases: aliases));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Fact]
    public void AliasToTheCatalog_IsRefused()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2));
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = 1 };

        var ex = Assert.Throws<PlumePdfException>(() => StreamWriteTransformTests.Write(document, PdfOptions.Default, transform: null, aliases: aliases));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Fact]
    public void AliasOfAnOverriddenObject_IsRefused()
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2, identicalImages: true));
        var replacements = new Dictionary<int, PdfObject> { [ImagePagesDocument.Image(0)] = new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("X")) };
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0) };

        var ex = Assert.Throws<PlumePdfException>(() => StreamWriteTransformTests.Write(document, PdfOptions.Default, transform: null, replacements: replacements, aliases: aliases));
        Assert.Equal("PLUME5010", ex.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedPageWithAnAliasedImage_StaysOutOfTheOutput(bool optimize)
    {
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 3, identicalImages: true));
        document.Pages.RemoveAt(2);
        var aliases = new Dictionary<int, int>
        {
            [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0),
            [ImagePagesDocument.Image(2)] = ImagePagesDocument.Image(0), // the removed page's own image
        };
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-alias-{Guid.NewGuid():N}.pdf");
        try
        {
            document.SaveWithWriteHooks(path, PdfOptions.Default with { Optimize = optimize }, transform: null, aliases);

            var bytes = File.ReadAllBytes(path);
            using var saved = PdfDocument.Open(bytes);
            Assert.Equal(2, saved.Pages.Count);
            Assert.Equal(ImagePagesDocument.PageImageReference(saved, 0, "Im0"), ImagePagesDocument.PageImageReference(saved, 1, "Im0"));
            Assert.Equal(1, CountOccurrences(bytes, "PIXELS-SAME"));
            if (!optimize)
            {
                // The removed page's content stream (uncompressed only on the classic body) is gone.
                Assert.Equal(0, CountOccurrences(bytes, "% page 2"));
                Assert.Equal(1, CountOccurrences(bytes, "% page 1"));
            }

            Assert.Equal(2, saved.Pages.Count(p => p.Dictionary.ContainsKey(PdfName.Get("Contents"))));
            CleanupFixtures3b.AssertQpdfClean(bytes, $"aliased save over a removed page (Optimize={optimize})");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CanonicalOnlyOnARemovedPage_IsWrittenForTheKeptDuplicate()
    {
        // A removed page's resources are not excluded, only left unreachable; an alias to one
        // from a kept page makes it reachable again as the kept page's (identical) image, while
        // the removed page and its content stay out.
        using var document = PdfDocument.Open(ImagePagesDocument.Build(pageCount: 2, identicalImages: true));
        document.Pages.RemoveAt(0);
        var aliases = new Dictionary<int, int> { [ImagePagesDocument.Image(1)] = ImagePagesDocument.Image(0) };
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-alias-{Guid.NewGuid():N}.pdf");
        try
        {
            document.SaveWithWriteHooks(path, PdfOptions.Default, transform: null, aliases);

            var bytes = File.ReadAllBytes(path);
            using var saved = PdfDocument.Open(bytes);
            Assert.Single(saved.Pages);
            Assert.Equal("PIXELS-SAME", Encoding.ASCII.GetString(ImagePagesDocument.PageImage(saved, 0, "Im0").RawBytes.Span));
            Assert.Equal(1, CountOccurrences(bytes, "PIXELS-SAME"));
            Assert.Equal(0, CountOccurrences(bytes, "% page 0"));
            CleanupFixtures3b.AssertQpdfClean(bytes, "aliased save whose canonical was on a removed page");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static int CountOccurrences(byte[] haystack, string needle)
    {
        var pattern = Encoding.ASCII.GetBytes(needle);
        var count = 0;
        var span = haystack.AsSpan();
        for (var index = span.IndexOf(pattern); index >= 0; index = span.IndexOf(pattern))
        {
            count++;
            span = span[(index + pattern.Length)..];
        }

        return count;
    }

    // The trailer's /Size: one past the highest object number written (every number from 1 up is used).
    private static int ObjectCount(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        return ((PdfNumber)document.Objects.Trailer[PdfName.Size]).ToInt32();
    }
}
