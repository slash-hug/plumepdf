using System.Security.Cryptography;
using PlumePdf.Objects;
using PlumePdf.Tests.Forms;
using PlumePdf.Tests.PageRemoval;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Pins <see cref="Pdf.Merge(PdfDocument[])"/> and <see cref="Pdf.Split(PdfDocument)"/> output, so a
/// refactor of the page-import machinery behind them can prove it changed nothing. Each case
/// records two SHA-256 digests: the bytes of a plain deterministic <c>Save</c> (which renumbers
/// objects, so it pins content but not numbering), and the composed document as it is in memory
/// before any save (its trailer, every object under its own number, and its diagnostics), which
/// pins the numbering and <c>/Size</c> too. The cases cover every removed-page fixture
/// shape (links, pop-ups, replies, forms, radio groups, signature fields, structure trees,
/// inherited attributes, ...) merged with itself, split, and merged after a page removal, plus
/// two different forms with overlapping <c>/DR</c> resources. Optimize and Linearize layouts are not
/// pinned: their Flate output differs between .NET 8's zlib and .NET 9+'s zlib-ng.
/// </summary>
/// <remarks>
/// After an intended change to Merge or Split output, regenerate the digests with
/// <c>PLUMEPDF_UPDATE_GOLDENS=1 dotnet test --filter MergeSplitGoldenTests</c> and review the diff.
/// </remarks>
public class MergeSplitGoldenTests
{
    private static readonly string GoldenPath = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.Tests", "Goldens", "merge-split-sha256.txt");

    /// <summary>Every case name and the bytes it produces.</summary>
    private static IEnumerable<(string Name, string Digests)> Cases()
    {
        foreach (var shape in RemovedPageFixtures.AllShapes)
        {
            var source = RemovedPageFixtures.Build(shape).Bytes;

            using (var document = PdfDocument.Open(source))
            using (var merged = Pdf.Merge(document, document))
            {
                yield return ($"merge-self/{shape}", Digest(merged));
            }

            using (var document = PdfDocument.Open(source))
            using (var parts = Pdf.Split(document))
            {
                for (var i = 0; i < parts.Documents.Count; i++)
                {
                    yield return ($"split/{shape}/{i}", Digest(parts.Documents[i]));
                }
            }

            using (var document = PdfDocument.Open(source))
            {
                document.Pages.RemoveAt(1);
                using var merged = Pdf.Merge(document, document);
                yield return ($"merge-after-remove/{shape}", Digest(merged));
            }
        }

        using (var form = PdfDocument.Open(FormsTestDocuments.Build()))
        using (var other = PdfDocument.Open(RemovedPageFixtures.Build("K").Bytes))
        using (var merged = Pdf.Merge(form, other, form))
        {
            yield return ("merge-two-forms", Digest(merged));
        }
    }

    [Fact]
    public void MergeAndSplitOutput_MatchesTheRecordedDigests()
    {
        var actual = Cases().Select(static c => $"{c.Name} {c.Digests}").ToList();

        if (Environment.GetEnvironmentVariable("PLUMEPDF_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllLines(GoldenPath, actual);
            return;
        }

        var expected = File.ReadAllLines(GoldenPath).Where(static l => l.Length > 0).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MergeAndSplitOutput_IsDeterministic()
    {
        var first = Cases().Select(static c => c.Digests).ToList();
        var second = Cases().Select(static c => c.Digests).ToList();

        Assert.Equal(first, second);
    }

    // "<saved> <in-memory>", lowercase hex.
    private static string Digest(PdfDocument document)
    {
        var memory = Hex(InMemory(document));
        return $"{Hex(RemovedPageFixtures.SaveToBytes(document, SaveLayout.Save))} {memory}";
    }

    private static byte[] InMemory(PdfDocument document)
    {
        using var output = new MemoryStream();
        ObjectSerializer.WriteValue(output, document.Objects.Trailer);
        var size = document.Objects.NextFreshObjectNumber;
        output.Write(System.Text.Encoding.ASCII.GetBytes($"\nnext {size}\n"));
        for (var number = 1; number < size; number++)
        {
            ObjectSerializer.WriteIndirectObject(output, number, 0, document.Objects[new IndirectReference(number, 0)]);
        }

        foreach (var diagnostic in document.Diagnostics)
        {
            output.Write(System.Text.Encoding.UTF8.GetBytes($"{diagnostic.Code} {diagnostic.Severity} {diagnostic.Message}\n"));
        }

        return output.ToArray();
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PlumePdf.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("PlumePdf.sln not found above the test output directory.");
    }
}
