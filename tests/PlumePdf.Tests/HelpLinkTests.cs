using System.Text.RegularExpressions;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// Every <see cref="PlumePdfException.HelpLink"/>
/// points at the public GitHub blob URL for that code's <c>docs/errors/</c> page —
/// <c>https://github.com/slash-hug/plumepdf/blob/main/docs/errors/PLUME####.md</c> — a shape
/// that resolves directly against the published repository, with zero code change required.
/// </summary>
public partial class HelpLinkTests
{
    [GeneratedRegex(@"^https://github\.com/slash-hug/plumepdf/blob/main/docs/errors/PLUME[0-9]{4}\.md$")]
    private static partial Regex HelpLinkShape();

    [Theory]
    [InlineData("PLUME1001")]
    [InlineData("PLUME5017")]
    [InlineData("PLUME9010")]
    public void HelpLink_MatchesPublicGitHubBlobShape(string code)
    {
        var ex = new PlumePdfException(code, "message");

        Assert.Matches(HelpLinkShape(), ex.HelpLink);
        Assert.Equal($"https://github.com/slash-hug/plumepdf/blob/main/docs/errors/{code}.md", ex.HelpLink);
    }

    [Fact]
    public void HelpLink_OnARealThrownRefusal_CarriesItsOwnCodesPage()
    {
        // End to end through a real coded refusal, not just the constructor: the
        // Optimize+Linearize combination refusal (PLUME5018) must link its own page.
        using var document = PdfDocument.Compose(page => page.Content().Text("HelpLink check"));
        var outputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-helplink-{Guid.NewGuid():N}.pdf");
        try
        {
            var ex = Assert.Throws<PlumePdfException>(() =>
                document.Save(outputPath, PdfOptions.Default with { Optimize = true, Linearize = true }));

            Assert.Equal("PLUME5018", ex.Code);
            Assert.Equal("https://github.com/slash-hug/plumepdf/blob/main/docs/errors/PLUME5018.md", ex.HelpLink);
            Assert.Matches(HelpLinkShape(), ex.HelpLink);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
