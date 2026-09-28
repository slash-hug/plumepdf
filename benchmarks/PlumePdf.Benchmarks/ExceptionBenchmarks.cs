using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks;

/// <summary>
/// Cost of constructing and throwing <see cref="PlumePdfException"/> — every
/// recoverable-vs-strict decision and every unrecoverable failure in Phase 1
/// goes through this type (AGENTS.md "never a bare Exception"), so its
/// allocation profile matters on the hot recovery-ladder path, not just at the
/// API boundary. Superseded the original placeholder suite (which only proved
/// the harness end-to-end) once <see cref="TokenizerBenchmarks"/>,
/// <see cref="OpenBenchmarks"/>, and <see cref="SaveBenchmarks"/> landed.
/// </summary>
[MemoryDiagnoser]
public class ExceptionBenchmarks
{
    [Benchmark(Baseline = true)]
    public PlumePdfException Construct() => new("PLUME0001", "benchmark: exception construction cost");

    [Benchmark]
    public string ThrowAndCatch()
    {
        try
        {
            throw new PlumePdfException("PLUME0001", "benchmark: throw/catch cost");
        }
        catch (PlumePdfException ex)
        {
            return ex.Code;
        }
    }
}
