using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// Mechanical enforcement of <c>docs/cookbook/README.md</c>'s own stated contract ("one task
/// per file") — a discoverability finding: two Phase 7 recipes
/// (<c>from-images.md</c>, <c>decode-image.md</c>) existed, compiled, and passed their own
/// Verify snapshot, yet were unreachable from the cookbook's index page. IL-scan-style source
/// checks (the <c>ReflectionBanTests</c>/<c>PdfOptionsCapWiringTests</c> precedent) catch a
/// declared-but-dead cap; this is that same "declared but unreachable" failure mode applied to
/// documentation instead of code.
/// </summary>
public class CookbookIndexTests
{
    private const string SolutionFileName = "PlumePdf.sln";

    [Fact]
    public void EveryCookbookRecipeIsLinkedFromTheIndex()
    {
        var repoRoot = FindRepoRoot();
        var cookbookDir = Path.Combine(repoRoot, "docs", "cookbook");
        var readmePath = Path.Combine(cookbookDir, "README.md");

        Assert.True(File.Exists(readmePath), $"Missing {readmePath}.");
        var readmeText = File.ReadAllText(readmePath);

        var recipeFiles = Directory.EnumerateFiles(cookbookDir, "*.md")
            .Select(Path.GetFileName)
            .Where(static name => name != "README.md")
            .Cast<string>()
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(recipeFiles); // sanity: the directory scan itself still works.

        var unlinked = recipeFiles.Where(name => !readmeText.Contains($"({name})", StringComparison.Ordinal)).ToList();

        Assert.True(unlinked.Count == 0,
            "Every docs/cookbook/*.md recipe must be linked from docs/cookbook/README.md's " +
            "index — a page that compiles, has a passing Verify snapshot, and is still " +
            "unreachable from the index is a live discoverability bug, not a documentation " +
            "nicety. Link (or remove) these:\n  " + string.Join("\n  ", unlinked));
    }

    [Fact]
    public void EveryIndexLinkPointsAtAnExistingFile()
    {
        var repoRoot = FindRepoRoot();
        var cookbookDir = Path.Combine(repoRoot, "docs", "cookbook");
        var readmeText = File.ReadAllText(Path.Combine(cookbookDir, "README.md"));

        var linkedNames = System.Text.RegularExpressions.Regex.Matches(readmeText, @"\(([a-z0-9-]+\.md)\)")
            .Select(static m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(linkedNames); // sanity: the regex itself still matches something.

        var missing = linkedNames.Where(name => !File.Exists(Path.Combine(cookbookDir, name))).ToList();

        Assert.True(missing.Count == 0,
            "docs/cookbook/README.md links to a recipe file that does not exist — a stale or " +
            "typo'd link:\n  " + string.Join("\n  ", missing));
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root ({SolutionFileName}) above {AppContext.BaseDirectory}.");
    }
}
