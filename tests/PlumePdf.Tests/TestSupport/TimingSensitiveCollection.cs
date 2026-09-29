using Xunit;

namespace PlumePdf.Tests.TestSupport;

/// <summary>
/// Tests whose assertions include a tight wall-clock budget. xunit runs this collection on its
/// own, after every parallel collection in the assembly has finished, so the CPU-heavy suites in
/// the same test process cannot stretch a timing measurement. Other processes (the second target
/// framework, the corpus assembly) still share the machine; the tests themselves allow for that.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "Timing-sensitive";
}
