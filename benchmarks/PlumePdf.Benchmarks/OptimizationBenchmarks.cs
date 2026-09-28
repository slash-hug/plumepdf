using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks;

/// <summary>
/// Phase 6 optimization/linearization save-path suite. PlumePDF-only against
/// the stored JSON baseline (see <c>Program.cs</c>) — there is no third-party
/// "optimize/linearize an existing PDF" comparison shape that fits the harness's
/// scenarios, so the classic full rewrite is the in-suite baseline the two
/// new layouts are measured against. Reported output sizes matter as much as time here: the
/// optimized layout's whole point is a smaller file, so the suite also exposes the written
/// byte count as each benchmark's return value.
/// </summary>
[MemoryDiagnoser]
public class OptimizationBenchmarks
{
    private string _sourcePath = null!;
    private PdfDocument _document = null!;
    private string _classicOutputPath = null!;
    private string _optimizedOutputPath = null!;
    private string _linearizedOutputPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sourcePath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-optimization-source.pdf");
        File.WriteAllBytes(_sourcePath, SampleDocuments.BuildClassicXrefDocument(pageCount: 200));
        _document = PdfDocument.Open(_sourcePath);
        _classicOutputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-classic.pdf");
        _optimizedOutputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-optimized.pdf");
        _linearizedOutputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-linearized.pdf");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _document.Dispose();
        foreach (var path in new[] { _sourcePath, _classicOutputPath, _optimizedOutputPath, _linearizedOutputPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Benchmark(Baseline = true)]
    public long SaveClassic()
    {
        _document.Save(_classicOutputPath, PdfOptions.Default with { Deterministic = true });
        return new FileInfo(_classicOutputPath).Length;
    }

    [Benchmark]
    public long SaveOptimized()
    {
        _document.Save(_optimizedOutputPath, PdfOptions.Default with { Deterministic = true, Optimize = true });
        return new FileInfo(_optimizedOutputPath).Length;
    }

    [Benchmark]
    public long SaveLinearized()
    {
        _document.Save(_linearizedOutputPath, PdfOptions.Default with { Deterministic = true, Linearize = true });
        return new FileInfo(_linearizedOutputPath).Length;
    }
}
