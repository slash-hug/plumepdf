using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace PlumePdf.CookbookTests;

/// <summary>
/// Mechanical enforcement of <c>docs/agent-forward.md</c>'s agent surfaces for the one-line
/// verbs: every public <see cref="Pdf"/> verb is named in the in-repo skill, <c>llms.txt</c>
/// and the NuGet README, and in at least one cookbook recipe. Build, snippet and error-page
/// gates already cover XML docs, recipe drift and error codes; nothing covered these, and the
/// skill went three phases without naming <c>Pdf.Rasterize</c>, <c>Pdf.FromImages</c> or
/// <c>Pdf.FlattenForm</c> (the <see cref="CookbookIndexTests"/> "declared but unreachable"
/// failure mode, applied to the agent guides).
/// </summary>
public class AgentDocsCoverageTests
{
    private const string SolutionFileName = "PlumePdf.sln";

    // The charter's budget for the skill body (~5K tokens), as words.
    private const int MaxSkillWords = 3_700;

    public static TheoryData<string> AgentSurfaces => new()
    {
        ".claude/skills/plumepdf/SKILL.md",
        "llms.txt",
        "src/PlumePdf/README.md",
    };

    [Theory]
    [MemberData(nameof(AgentSurfaces))]
    public void EveryPdfVerbIsNamedInTheAgentSurface(string surface)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), surface));

        var missing = Verbs().Where(verb => !NamesVerb(text, verb)).ToList();

        Assert.True(missing.Count == 0,
            $"{surface} must name every public Pdf verb (as `Pdf.<Verb>`) so an agent reading it " +
            "finds the one-line door. Add these:\n  " + string.Join("\n  ", missing.Select(v => $"Pdf.{v}")));
    }

    [Fact]
    public void EveryPdfVerbHasACookbookRecipe()
    {
        var recipes = Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "docs", "cookbook"), "*.md")
            .Where(static path => Path.GetFileName(path) != "README.md")
            .Select(File.ReadAllText)
            .ToList();

        var missing = Verbs().Where(verb => !recipes.Any(recipe => NamesVerb(recipe, verb))).ToList();

        Assert.True(missing.Count == 0,
            "Every public Pdf verb needs a cookbook recipe that names it (as `Pdf.<Verb>`), backed " +
            "by a CookbookTests snippet. Missing:\n  " + string.Join("\n  ", missing.Select(v => $"Pdf.{v}")));
    }

    [Fact]
    public void TheSkillStaysWithinItsBudget()
    {
        var skill = File.ReadAllText(Path.Combine(FindRepoRoot(), ".claude", "skills", "plumepdf", "SKILL.md"));
        var words = Regex.Matches(skill, @"\S+").Count;

        Assert.True(words <= MaxSkillWords,
            $"SKILL.md is {words} words; the charter keeps the skill body under ~5K tokens " +
            $"(~{MaxSkillWords} words). Move detail into a cookbook recipe and link it.");
    }

    // Async variants share their verb's documentation.
    private static List<string> Verbs()
    {
        var verbs = typeof(Pdf).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name.EndsWith("Async", StringComparison.Ordinal) ? method.Name[..^"Async".Length] : method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(verbs); // sanity: the reflection scan itself still works.
        return verbs;
    }

    private static bool NamesVerb(string text, string verb) =>
        Regex.IsMatch(text, $@"\bPdf\.{verb}(?![A-Za-z])");

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
