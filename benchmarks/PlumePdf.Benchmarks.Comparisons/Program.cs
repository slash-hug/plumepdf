using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Running;

// Mirrors benchmarks/PlumePdf.Benchmarks/Program.cs: full JSON
// export on every run so a future baseline-comparison tool has something to diff.
// Manual/per-phase, not CI-gated.
var config = DefaultConfig.Instance.AddExporter(JsonExporter.Full);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);

public partial class Program;
