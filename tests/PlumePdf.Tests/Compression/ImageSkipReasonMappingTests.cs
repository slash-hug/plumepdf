using PlumePdf.Documents.Compression;
using Xunit;

namespace PlumePdf.Tests.Compression;

/// <summary>
/// The planner's internal skip reasons and the public ones the result reports: every planned
/// skip maps, to the public reason of the same name, so a reason added to the planner can't
/// reach a caller unnamed.
/// </summary>
public class ImageSkipReasonMappingTests
{
    // By name: the enum is internal, and theory parameters must be public types.
    public static TheoryData<string> EveryPlannedSkip()
    {
        var data = new TheoryData<string>();
        foreach (var skip in Enum.GetNames<PlannedSkip>().Where(static s => s != nameof(PlannedSkip.None)))
        {
            data.Add(skip);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPlannedSkip))]
    public void EveryPlannedSkip_MapsToThePublicReasonOfTheSameName(string skip)
    {
        var reason = CompressionEngine.ToSkipReason(Enum.Parse<PlannedSkip>(skip));

        Assert.Equal(skip, reason.ToString());
    }

    [Fact]
    public void TheMapping_IsOneToOne()
    {
        var planned = Enum.GetValues<PlannedSkip>().Where(static s => s != PlannedSkip.None).ToList();

        var mapped = planned.Select(CompressionEngine.ToSkipReason).Distinct().ToList();

        Assert.Equal(planned.Count, mapped.Count);
        Assert.Equal(Enum.GetValues<ImageSkipReason>().Length, mapped.Count);
    }

    [Fact]
    public void None_IsNotASkipReason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompressionEngine.ToSkipReason(PlannedSkip.None));
    }
}
