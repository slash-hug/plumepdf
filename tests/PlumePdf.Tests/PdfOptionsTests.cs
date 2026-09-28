using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="PdfOptions.Filters"/> and the extraction resource
/// limits. Behavioral proof that <see cref="PdfOptions.Filters"/> is actually consulted by the
/// reading pipeline lives in <see cref="ObjectResolverTests"/> — these tests only cover the
/// record's own defaults and <c>with</c>-expression shape.
/// </summary>
public class PdfOptionsTests
{
    [Fact]
    public void Default_FiltersIsTheSharedDefaultRegistry()
    {
        Assert.Same(PdfFilterRegistry.Default, PdfOptions.Default.Filters);
    }

    [Fact]
    public void With_CanOverrideFiltersToACustomRegistry()
    {
        var custom = new PdfFilterRegistry();
        var options = PdfOptions.Default with { Filters = custom };

        Assert.Same(custom, options.Filters);
        // Overriding one property never mutates the shared Default instance or its registry.
        Assert.Same(PdfFilterRegistry.Default, PdfOptions.Default.Filters);
    }

    [Theory]
    [InlineData(5_000_000)]
    public void Default_MaxContentStreamOperators_MatchesDocumentedDefault(int expected) =>
        Assert.Equal(expected, PdfOptions.Default.MaxContentStreamOperators);

    [Theory]
    [InlineData(1_000_000)]
    public void Default_MaxLettersPerPage_MatchesDocumentedDefault(int expected) =>
        Assert.Equal(expected, PdfOptions.Default.MaxLettersPerPage);

    [Theory]
    [InlineData(1_000_000)]
    public void Default_MaxCMapEntries_MatchesDocumentedDefault(int expected) =>
        Assert.Equal(expected, PdfOptions.Default.MaxCMapEntries);

    [Theory]
    [InlineData(32)]
    public void Default_MaxXObjectNestingDepth_MatchesDocumentedDefault(int expected) =>
        Assert.Equal(expected, PdfOptions.Default.MaxXObjectNestingDepth);

    [Fact]
    public void With_CanTightenEveryExtractionLimitIndependently()
    {
        var tightened = PdfOptions.Default with
        {
            MaxContentStreamOperators = 10,
            MaxLettersPerPage = 20,
            MaxCMapEntries = 30,
            MaxXObjectNestingDepth = 2,
        };

        Assert.Equal(10, tightened.MaxContentStreamOperators);
        Assert.Equal(20, tightened.MaxLettersPerPage);
        Assert.Equal(30, tightened.MaxCMapEntries);
        Assert.Equal(2, tightened.MaxXObjectNestingDepth);

        // Untouched properties keep their own defaults - a with-expression is additive, not
        // a fresh record.
        Assert.Equal(PdfOptions.Default.MaxObjectNestingDepth, tightened.MaxObjectNestingDepth);
    }
}
