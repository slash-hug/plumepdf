using System.Text;
using BenchmarkDotNet.Attributes;
using PlumePdf.Objects;

namespace PlumePdf.Benchmarks;

/// <summary>
/// Raw tokenizer throughput (ISO 32000-1 §7.2/§7.3 lexical rules) for the
/// zero-allocation <c>ref struct</c> hot path, isolated from the
/// higher-level object/xref/document pipeline that <see cref="OpenBenchmarks"/>
/// measures end to end. <c>[MemoryDiagnoser]</c> is the whole point here: the
/// tokenizer is a one-way-door low-allocation commitment, and this suite
/// is what would catch a regression back into per-token allocation.
/// </summary>
/// <remarks>
/// <see cref="PdfTokenizer"/> is internal by design (layer namespaces are
/// internals-only; the tokenizer is not part of the public façade). This file
/// compiles once <c>src/PlumePdf</c> declares
/// <c>[assembly: InternalsVisibleTo("PlumePdf.Benchmarks")]</c> alongside
/// <c>PdfTokenizer.cs</c> — the standard, expected way to micro-benchmark
/// an internal hot-path type without inflating the public surface just for it.
/// </remarks>
[MemoryDiagnoser]
public class TokenizerBenchmarks
{
    private byte[] _numericArray = null!;
    private byte[] _nameHeavyDictionary = null!;
    private byte[] _literalStringRun = null!;

    [GlobalSetup]
    public void Setup()
    {
        _numericArray = BuildNumericArray(count: 10_000);
        _nameHeavyDictionary = BuildNameHeavyDictionary(count: 2_000);
        _literalStringRun = BuildLiteralStringRun(count: 2_000);
    }

    [Benchmark(Baseline = true)]
    public int NumericArray() => CountTokens(_numericArray);

    [Benchmark]
    public int NameHeavyDictionary() => CountTokens(_nameHeavyDictionary);

    [Benchmark]
    public int LiteralStringRun() => CountTokens(_literalStringRun);

    private static int CountTokens(byte[] bytes)
    {
        var tokenizer = new PdfTokenizer(bytes);
        var count = 0;
        while (tokenizer.Read())
        {
            count++;
        }

        return count;
    }

    private static byte[] BuildNumericArray(int count)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            sb.Append(i * 0.5).Append(' ');
        }

        sb.Append(']');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildNameHeavyDictionary(int count)
    {
        var sb = new StringBuilder("<<");
        for (var i = 0; i < count; i++)
        {
            sb.Append("/Key").Append(i).Append(" /Value").Append(i).Append(' ');
        }

        sb.Append(">>");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildLiteralStringRun(int count)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < count; i++)
        {
            sb.Append("(A reasonably long literal string value number ").Append(i).Append(") ");
        }

        sb.Append(']');
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
