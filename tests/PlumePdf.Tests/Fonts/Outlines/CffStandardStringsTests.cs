using PlumePdf.Fonts.Outlines;
using Xunit;

namespace PlumePdf.Tests.Fonts.Outlines;

/// <summary>
/// Pins <see cref="CffStandardStrings"/>'s transcription of Adobe TN #5176 Appendix A: the count,
/// a handful of well-known SIDs spread across the table (Latin letters, the tail's font-menu
/// names), and that every entry is present and unique (a duplicate or blank entry would silently
/// break <c>CffParser</c>'s SID -&gt; name -&gt; GID lookups for whichever name collided).
/// </summary>
public class CffStandardStringsTests
{
    [Fact]
    public void Count_Is391()
    {
        Assert.Equal(391, CffStandardStrings.Count);
        Assert.Equal(391, CffStandardStrings.Names.Length);
    }

    [Fact]
    public void Sid0_IsNotdef()
    {
        Assert.Equal(".notdef", CffStandardStrings.Names[0]);
    }

    [Fact]
    public void Sid1_IsSpace()
    {
        Assert.Equal("space", CffStandardStrings.Names[1]);
    }

    [Fact]
    public void Sid34_IsUppercaseA()
    {
        Assert.Equal("A", CffStandardStrings.Names[34]);
    }

    [Fact]
    public void Sid66_IsLowercaseA()
    {
        Assert.Equal("a", CffStandardStrings.Names[66]);
    }

    [Fact]
    public void Sid390_IsSemibold()
    {
        Assert.Equal("Semibold", CffStandardStrings.Names[390]);
    }

    [Fact]
    public void EveryEntry_IsNonEmpty()
    {
        Assert.All(CffStandardStrings.Names, name => Assert.False(string.IsNullOrEmpty(name)));
    }

    [Fact]
    public void EveryEntry_IsDistinct()
    {
        Assert.Equal(CffStandardStrings.Names.Length, new HashSet<string>(CffStandardStrings.Names, StringComparer.Ordinal).Count);
    }
}
