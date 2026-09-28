using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

// Full JSON export on every run (PlumePDF-only macro suites
// run manually/per-phase against a stored baseline — no per-commit hard gate on
// shared runners — but the JSON output is wired in from Phase 1 so a future
// baseline-comparison tool has something to diff against without a benchmark
// project change). Phase 9 is the first suite to
// actually consume that JSON export: RasterizeBenchmarks' results feed
// scripts/check-raster-perf-baseline.sh against benchmarks/perf-baselines/raster-baselines.json
// (see that suite's own doc comment, and the ci.yml raster-perf-gate job, for the
// fast-`--job short` per-commit exception to the "manual/per-phase" rule above).
//
// RasterizeBenchmarks (like every [Benchmark]-attributed class in this assembly) needs no
// explicit registration below — BenchmarkSwitcher.FromAssembly discovers every such class via
// reflection automatically.
var config = DefaultConfig.Instance.AddExporter(JsonExporter.Full);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);

public partial class Program;
