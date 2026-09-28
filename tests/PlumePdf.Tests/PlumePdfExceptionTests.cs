using Xunit;

namespace PlumePdf.Tests;

public class PlumePdfExceptionTests
{
    [Fact]
    public void CarriesStableCodeAndHelpLink()
    {
        var ex = new PlumePdfException("PLUME2001", "Something specific failed at offset 42.");

        Assert.Equal("PLUME2001", ex.Code);
        Assert.Equal("Something specific failed at offset 42.", ex.Message);
        Assert.Equal("https://github.com/slash-hug/plumepdf/blob/main/docs/errors/PLUME2001.md", ex.HelpLink);
    }

    [Fact]
    public void PreservesInnerException()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new PlumePdfException("PLUME2002", "Wrapper.", inner);

        Assert.Same(inner, ex.InnerException);
    }
}
