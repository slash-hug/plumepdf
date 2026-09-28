using NetArchTest.Rules;
using Xunit;

namespace PlumePdf.ArchitectureTests;

/// <summary>
/// The JPEG 2000 codec is an internal subsystem — every type in
/// <c>PlumePdf.Filters.Jpx</c> is <see langword="internal"/>, like every <c>Filters/Jbig2</c> type before it.
/// The public surface is the <c>JPXDecode</c> filter registration and <c>RasterImage.Decode</c>; a
/// worktree cannot widen it by accident.
/// </summary>
public class CodecInternalsTests
{
    [Fact]
    public void JpxCodecTypesAreAllInternal()
    {
        var result = Types.InAssembly(typeof(PlumePdfException).Assembly)
            .That().ResideInNamespace("PlumePdf.Filters.Jpx")
            .Should().NotBePublic()
            .GetResult();

        var offenders = result.FailingTypeNames is null ? string.Empty : string.Join(", ", result.FailingTypeNames);
        Assert.True(result.IsSuccessful, $"Public types found in PlumePdf.Filters.Jpx (must be internal): {offenders}");
    }

    [Fact]
    public void JpxNamespaceExists()
    {
        // Guards the rule above against passing vacuously if the namespace were ever renamed.
        var count = Types.InAssembly(typeof(PlumePdfException).Assembly)
            .That().ResideInNamespace("PlumePdf.Filters.Jpx")
            .GetTypes().Count();

        Assert.True(count >= 12, $"Expected the JPX type surface (12+ types); found {count}.");
    }
}
