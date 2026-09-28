using PlumePdf.Fonts.Reading;
using Xunit;

namespace PlumePdf.Tests.Fonts.Reading;

/// <summary>
/// Pins <see cref="FontNames.StripSubsetPrefix"/> (moved verbatim out of <c>RenderFontFactory</c>)
/// before it becomes load-bearing on the extraction
/// metrics path, where a wrong answer means an advance of 0 rather than a wrong glyph name.
/// </summary>
public class FontNamesTests
{
    [Theory]
    [InlineData("Symbol", "Symbol")]                       // plain name — untouched
    [InlineData("ABCDEF+Symbol", "Symbol")]                // valid six-uppercase-letter tag
    [InlineData("ABCDEF+ZapfDingbats", "ZapfDingbats")]    // valid tag, longer name
    [InlineData("abcdef+Symbol", "abcdef+Symbol")]         // lowercase tag is not a subset tag
    [InlineData("ABCDE+Symbol", "ABCDE+Symbol")]           // five letters — '+' is not at index 6
    [InlineData("ABCDEFG+Symbol", "ABCDEFG+Symbol")]       // seven letters — '+' is not at index 6
    [InlineData("ABC1EF+Symbol", "ABC1EF+Symbol")]         // digit inside the tag rejects it
    [InlineData("ABCDEF+", "ABCDEF+")]                     // nothing after the '+' (length 7) — untouched
    [InlineData("ABCDEF+X", "X")]                          // shortest strippable form
    [InlineData("", "")]
    public void StripSubsetPrefix_StripsOnlyWellFormedTags(string input, string expected)
    {
        Assert.Equal(expected, FontNames.StripSubsetPrefix(input));
    }
}
