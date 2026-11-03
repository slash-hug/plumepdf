using System.Security.Cryptography;
using PlumePdf.Tests.Forms;
using PlumePdf.Tests.PageRemoval;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Pins <see cref="Pdf.Merge(PdfDocument[])"/> and <see cref="Pdf.Split(PdfDocument)"/> output byte
/// for byte (a SHA-256 per case, saved deterministically), so a refactor of the page-import
/// machinery behind them can prove it changed nothing. The cases cover every removed-page fixture
/// shape (links, pop-ups, replies, forms, radio groups, signature fields, structure trees,
/// inherited attributes, ...) merged with itself, split, and merged after a page removal, plus
/// two different forms with overlapping <c>/DR</c> resources.
/// </summary>
/// <remarks>
/// After an intended change to Merge or Split output, regenerate the digests with
/// <c>PLUMEPDF_UPDATE_GOLDENS=1 dotnet test --filter MergeSplitGoldenTests</c> and review the diff.
/// </remarks>
public class MergeSplitGoldenTests
{
    private static readonly string GoldenPath = Path.Combine(FindRepoRoot(), "tests", "PlumePdf.Tests", "Goldens", "merge-split-sha256.txt");

    /// <summary>Every case name and the bytes it produces.</summary>
    private static IEnumerable<(string Name, byte[] Bytes)> Cases()
    {
        foreach (var shape in RemovedPageFixtures.AllShapes)
        {
            var source = RemovedPageFixtures.Build(shape).Bytes;

            using (var document = PdfDocument.Open(source))
            using (var merged = Pdf.Merge(document, document))
            {
                yield return ($"merge-self/{shape}", Save(merged));
            }

            using (var document = PdfDocument.Open(source))
            using (var parts = Pdf.Split(document))
            {
                for (var i = 0; i < parts.Documents.Count; i++)
                {
                    yield return ($"split/{shape}/{i}", Save(parts.Documents[i]));
                }
            }

            using (var document = PdfDocument.Open(source))
            {
                document.Pages.RemoveAt(1);
                using var merged = Pdf.Merge(document, document);
                yield return ($"merge-after-remove/{shape}", Save(merged));
            }
        }

        using (var form = PdfDocument.Open(FormsTestDocuments.Build()))
        using (var other = PdfDocument.Open(RemovedPageFixtures.Build("K").Bytes))
        using (var merged = Pdf.Merge(form, other, form))
        {
            yield return ("merge-two-forms", Save(merged));
        }
    }

    [Fact]
    public void MergeAndSplitOutput_MatchesTheRecordedDigests()
    {
        var actual = Cases().Select(static c => $"{c.Name} {Convert.ToHexString(SHA256.HashData(c.Bytes)).ToLowerInvariant()}").ToList();

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
        var first = Cases().Select(static c => Convert.ToHexString(SHA256.HashData(c.Bytes))).ToList();
        var second = Cases().Select(static c => Convert.ToHexString(SHA256.HashData(c.Bytes))).ToList();

        Assert.Equal(first, second);
    }

    private static byte[] Save(PdfDocument document) => RemovedPageFixtures.SaveToBytes(document, SaveLayout.Save);

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
