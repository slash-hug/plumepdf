using BenchmarkDotNet.Attributes;

namespace PlumePdf.Benchmarks;

/// <summary>
/// <see cref="PdfDocument.SaveIncremental(string)"/> vs. full-rewrite
/// <see cref="PdfDocument.Save(string, PdfOptions)"/> on the same opened document
/// (docs/architecture.md "Writing pipeline": incremental is the default save path;
/// full rewrite garbage-collects and renumbers). The two are expected to have very
/// different cost profiles — incremental should scale with the size of the change,
/// full rewrite with the size of the whole document — and this suite is what
/// makes that difference visible and trackable release over release.
/// </summary>
[MemoryDiagnoser]
public class SaveBenchmarks
{
    private string _sourcePath = null!;
    private PdfDocument _document = null!;
    private string _incrementalOutputPath = null!;
    private string _fullRewriteOutputPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sourcePath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-source.pdf");
        File.WriteAllBytes(_sourcePath, SampleDocuments.BuildClassicXrefDocument(pageCount: 200));
        _document = PdfDocument.Open(_sourcePath);
        _incrementalOutputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-incremental.pdf");
        _fullRewriteOutputPath = Path.Combine(Path.GetTempPath(), $"plumepdf-bench-{Guid.NewGuid():N}-full.pdf");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _document.Dispose();
        foreach (var path in new[] { _sourcePath, _incrementalOutputPath, _fullRewriteOutputPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Benchmark(Baseline = true)]
    public void SaveIncremental() => _document.SaveIncremental(_incrementalOutputPath);

    [Benchmark]
    public void SaveFullRewriteDeterministic() =>
        _document.Save(_fullRewriteOutputPath, new PdfOptions { Deterministic = true });
}
