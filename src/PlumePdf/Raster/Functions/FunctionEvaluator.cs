using System.Globalization;

namespace PlumePdf.Raster.Functions;

/// <summary>
/// A parsed, evaluatable PDF function (ISO 32000-1 §7.10) — the shared numeric engine behind
/// axial/radial shadings (<c>Shading/AxialShading.cs</c>/<c>RadialShading.cs</c>) and
/// Separation/DeviceN tint transforms (<c>Color/SeparationDeviceN.cs</c>). Every concrete
/// subtype below corresponds to one <c>/FunctionType</c> value; construct one via
/// <see cref="FunctionEvaluator.Parse"/>, never directly.
/// </summary>
internal abstract class PdfFunction
{
    private protected PdfFunction(double[] domain, double[]? range)
    {
        Domain = domain;
        Range = range;
    }

    /// <summary>The function's <c>/Domain</c> — <c>2 * InputCount</c> values, (min0, max0, min1, max1, ...).</summary>
    public double[] Domain { get; }

    /// <summary>The function's <c>/Range</c> — <c>2 * OutputCount</c> values, or <see langword="null"/> when the type does not require one (Type 2/3 may omit it).</summary>
    public double[]? Range { get; }

    /// <summary>The number of input components this function accepts (<c>Domain.Length / 2</c>).</summary>
    public int InputCount => Domain.Length / 2;

    /// <summary>The number of output components this function produces.</summary>
    public abstract int OutputCount { get; }

    /// <summary>
    /// Evaluates the function. <paramref name="input"/> is clipped to <see cref="Domain"/>
    /// before <see cref="EvaluateCore"/> runs (ISO 32000-1 §7.10.1: "input values ... shall
    /// be clipped to the domain"); <paramref name="output"/> is clipped to <see cref="Range"/>
    /// afterward when one is present.
    /// </summary>
    /// <param name="input">Exactly <see cref="InputCount"/> input values.</param>
    /// <param name="output">Receives exactly <see cref="OutputCount"/> output values.</param>
    public void Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> clippedInput = input.Length <= 8 ? stackalloc double[input.Length] : new double[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var lo = Domain[2 * i];
            var hi = Domain[(2 * i) + 1];
            clippedInput[i] = Math.Clamp(input[i], Math.Min(lo, hi), Math.Max(lo, hi));
        }

        EvaluateCore(clippedInput, output);

        if (Range is not { } range)
        {
            return;
        }

        for (var j = 0; j < output.Length; j++)
        {
            var lo = range[2 * j];
            var hi = range[(2 * j) + 1];
            output[j] = Math.Clamp(output[j], Math.Min(lo, hi), Math.Max(lo, hi));
        }
    }

    /// <summary>Evaluates the function against already-domain-clipped input. Implemented by each concrete function type.</summary>
    private protected abstract void EvaluateCore(ReadOnlySpan<double> input, Span<double> output);

    private protected static double Interpolate(double x, double xMin, double xMax, double yMin, double yMax) =>
        xMax == xMin ? yMin : yMin + ((x - xMin) * (yMax - yMin) / (xMax - xMin));
}

/// <summary>
/// A function that dispatches to <c>N</c> independent 1-in/1-out functions, one per output
/// component — the array form ISO 32000-1 §7.10.1 permits wherever a single <c>/Function</c>
/// entry could appear (most commonly a shading's <c>/Function</c>). Not itself a
/// <c>/FunctionType</c>; produced by <see cref="FunctionEvaluator.ParseArray"/>.
/// </summary>
internal sealed class FunctionArray : PdfFunction
{
    private readonly PdfFunction[] _functions;

    internal FunctionArray(PdfFunction[] functions)
        : base(functions[0].Domain, BuildRange(functions))
    {
        _functions = functions;
    }

    public override int OutputCount => _functions.Length;

    private protected override void EvaluateCore(ReadOnlySpan<double> input, Span<double> output)
    {
        Span<double> single = stackalloc double[1];
        for (var i = 0; i < _functions.Length; i++)
        {
            _functions[i].Evaluate(input, single);
            output[i] = single[0];
        }
    }

    private static double[]? BuildRange(PdfFunction[] functions)
    {
        var range = new double[functions.Length * 2];
        for (var i = 0; i < functions.Length; i++)
        {
            if (functions[i].Range is not { Length: 2 } single)
            {
                return null;
            }

            range[2 * i] = single[0];
            range[(2 * i) + 1] = single[1];
        }

        return range;
    }
}

/// <summary>
/// Type 0 — a sampled function: an <c>m</c>-dimensional table of <c>n</c>-component output
/// samples, looked up by multilinear ("cube-vertex") interpolation over the input's position
/// within the sample grid (ISO 32000-1 §7.10.2). Field-by-field port of the algorithm's
/// published shape in pdf.js's <c>PDFFunction.constructSampled</c>/<c>getSampleArray</c>
/// (Mozilla, Apache-2.0; permitted under the clean-room policy in AGENTS.md — ports from permissive
/// sources with attribution — no iText7/Aspose/Syncfusion/QuestPDF source was read). Cubic
/// spline interpolation (<c>/Order 3</c>) has no normative algorithm in ISO 32000-1 and is
/// treated as linear, the same "no worse than every other open-source reader" choice pdf.js
/// and poppler both make.
/// </summary>
internal sealed class SampledFunction : PdfFunction
{
    // Defensive upper bound on total decoded samples (outputSize * product(size)) — no
    // PdfOptions cap exists for function sample tables yet (only shading/surface/display-list
    // caps are named elsewhere), so this local constant stands in per
    // the "validate a document-controlled dimension before allocating" discipline until a
    // dedicated PdfOptions knob is added. 64M doubles is already a wildly implausible sample
    // table for any real tint-transform or shading function.
    private const int MaxTotalSamples = 64 * 1024 * 1024;

    private readonly int[] _size;
    private readonly int _outputSize;
    private readonly double[] _encode;
    private readonly double[] _decode;
    private readonly double[] _samples;

    private SampledFunction(double[] domain, double[] range, int[] size, int outputSize, double[] encode, double[] decode, double[] samples)
        : base(domain, range)
    {
        _size = size;
        _outputSize = outputSize;
        _encode = encode;
        _decode = decode;
        _samples = samples;
    }

    public override int OutputCount => _outputSize;

    internal static SampledFunction Parse(PdfDictionary dict, ReadOnlyMemory<byte> decodedStreamBytes, Func<IndirectReference, PdfObject> resolve)
    {
        var domain = FunctionEvaluator.RequireNumberArray(dict, "Domain", resolve);
        var range = FunctionEvaluator.RequireNumberArray(dict, "Range", resolve);
        var inputSize = domain.Length / 2;
        var outputSize = range.Length / 2;

        var sizeArray = FunctionEvaluator.RequireNumberArray(dict, "Size", resolve);
        if (sizeArray.Length != inputSize)
        {
            throw new PlumePdfException("PLUME7710", $"Type 0 function's /Size array has {sizeArray.Length} entries but /Domain implies {inputSize} input dimensions.");
        }

        var size = new int[inputSize];
        for (var i = 0; i < inputSize; i++)
        {
            size[i] = Math.Max(1, (int)sizeArray[i]);
        }

        var bitsPerSample = (int)FunctionEvaluator.RequireNumber(dict, "BitsPerSample", resolve);
        if (bitsPerSample is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
        {
            throw new PlumePdfException("PLUME7710", $"Type 0 function's /BitsPerSample ({bitsPerSample}) is not one of the values ISO 32000-1 §7.10.2 permits (1, 2, 4, 8, 12, 16, 24, 32).");
        }

        var encode = FunctionEvaluator.TryNumberArray(dict, "Encode", resolve) ?? BuildDefaultEncode(size);
        var decode = FunctionEvaluator.TryNumberArray(dict, "Decode", resolve) ?? range;

        long totalSamples = outputSize;
        foreach (var s in size)
        {
            totalSamples = checked(totalSamples * s);
        }

        if (totalSamples > MaxTotalSamples)
        {
            throw new PlumePdfException("PLUME7711", $"Type 0 function's sample table would hold {totalSamples} samples, exceeding the {MaxTotalSamples} defensive cap; refusing to allocate it.");
        }

        var samples = ReadSamples(decodedStreamBytes.Span, (int)totalSamples, bitsPerSample);

        return new SampledFunction(domain, range, size, outputSize, encode, decode, samples);
    }

    /// <summary>Unpacks <paramref name="count"/> fixed-width samples from <paramref name="bytes"/>, each normalized to [0, 1]. Port of pdf.js <c>PDFFunction.getSampleArray</c>'s bit-packing loop.</summary>
    private static double[] ReadSamples(ReadOnlySpan<byte> bytes, int count, int bitsPerSample)
    {
        var result = new double[count];
        var sampleMax = bitsPerSample >= 32 ? uint.MaxValue : ((1u << bitsPerSample) - 1u);
        var scale = 1.0 / sampleMax;

        var byteIndex = 0;
        var codeSize = 0;
        ulong codeBuffer = 0;
        for (var i = 0; i < count; i++)
        {
            while (codeSize < bitsPerSample)
            {
                codeBuffer <<= 8;
                codeBuffer |= byteIndex < bytes.Length ? bytes[byteIndex] : 0u;
                byteIndex++;
                codeSize += 8;
            }

            codeSize -= bitsPerSample;
            var sample = (uint)((codeBuffer >> codeSize) & (bitsPerSample >= 32 ? 0xFFFFFFFFu : ((1u << bitsPerSample) - 1u)));
            result[i] = sample * scale;
        }

        return result;
    }

    private static double[] BuildDefaultEncode(int[] size)
    {
        var encode = new double[size.Length * 2];
        for (var i = 0; i < size.Length; i++)
        {
            encode[2 * i] = 0;
            encode[(2 * i) + 1] = size[i] - 1;
        }

        return encode;
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> input, Span<double> output)
    {
        var inputSize = InputCount;
        var cubeVertexCount = 1 << inputSize;

        Span<double> cubeWeight = cubeVertexCount <= 32 ? stackalloc double[cubeVertexCount] : new double[cubeVertexCount];
        Span<int> cubeVertex = cubeVertexCount <= 32 ? stackalloc int[cubeVertexCount] : new int[cubeVertexCount];
        for (var j = 0; j < cubeVertexCount; j++)
        {
            cubeWeight[j] = 1;
            cubeVertex[j] = 0;
        }

        var stride = _outputSize;
        var bitPos = 1;
        for (var i = 0; i < inputSize; i++)
        {
            var domainMin = Domain[2 * i];
            var domainMax = Domain[(2 * i) + 1];
            var e = Interpolate(input[i], domainMin, domainMax, _encode[2 * i], _encode[(2 * i) + 1]);
            e = Math.Clamp(e, 0, _size[i] - 1);

            var e0 = e < _size[i] - 1 ? Math.Floor(e) : e - 1;
            var n0 = e0 + 1 - e;
            var n1 = e - e0;
            var offset0 = (int)e0 * stride;
            var offset1 = offset0 + stride;

            for (var j = 0; j < cubeVertexCount; j++)
            {
                if ((j & bitPos) != 0)
                {
                    cubeWeight[j] *= n1;
                    cubeVertex[j] += offset1;
                }
                else
                {
                    cubeWeight[j] *= n0;
                    cubeVertex[j] += offset0;
                }
            }

            stride *= _size[i];
            bitPos <<= 1;
        }

        for (var j = 0; j < _outputSize; j++)
        {
            double sum = 0;
            for (var v = 0; v < cubeVertexCount; v++)
            {
                sum += _samples[cubeVertex[v] + j] * cubeWeight[v];
            }

            output[j] = Interpolate(sum, 0, 1, _decode[2 * j], _decode[(2 * j) + 1]);
        }
    }
}

/// <summary>
/// Type 2 — an exponential interpolation function: <c>y_j = C0_j + x^N * (C1_j - C0_j)</c>
/// (ISO 32000-1 §7.10.3). Almost every real-world axial/radial shading uses <c>N = 1</c>
/// (a plain linear color ramp); the general case uses <see cref="RasterMath.Pow"/> to stay
/// libm-free.
/// </summary>
internal sealed class ExponentialFunction : PdfFunction
{
    private readonly double[] _c0;
    private readonly double[] _c1;
    private readonly double _n;

    private ExponentialFunction(double[] domain, double[] c0, double[] c1, double n)
        : base(domain, null)
    {
        _c0 = c0;
        _c1 = c1;
        _n = n;
    }

    public override int OutputCount => _c0.Length;

    internal static ExponentialFunction Parse(PdfDictionary dict, Func<IndirectReference, PdfObject> resolve)
    {
        var domain = FunctionEvaluator.RequireNumberArray(dict, "Domain", resolve);
        var c0 = FunctionEvaluator.TryNumberArray(dict, "C0", resolve) ?? [0.0];
        var c1 = FunctionEvaluator.TryNumberArray(dict, "C1", resolve) ?? [1.0];
        var n = FunctionEvaluator.RequireNumber(dict, "N", resolve);

        if (c0.Length != c1.Length)
        {
            throw new PlumePdfException("PLUME7712", $"Type 2 function's /C0 ({c0.Length} components) and /C1 ({c1.Length} components) disagree in length.");
        }

        return new ExponentialFunction(domain, c0, c1, n);
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> input, Span<double> output)
    {
        var x = _n == 1 ? input[0] : RasterMath.Pow(input[0], _n);
        for (var j = 0; j < _c0.Length; j++)
        {
            output[j] = _c0[j] + (x * (_c1[j] - _c0[j]));
        }
    }
}

/// <summary>
/// Type 3 — a stitching function: partitions a single-input <c>/Domain</c> into <c>k</c>
/// subdomains via <c>/Bounds</c>, remapping into each subfunction's own domain via
/// <c>/Encode</c> before delegating (ISO 32000-1 §7.10.4).
/// </summary>
internal sealed class StitchingFunction : PdfFunction
{
    private readonly PdfFunction[] _functions;
    private readonly double[] _bounds;
    private readonly double[] _encode;

    private StitchingFunction(double[] domain, double[]? range, PdfFunction[] functions, double[] bounds, double[] encode)
        : base(domain, range)
    {
        _functions = functions;
        _bounds = bounds;
        _encode = encode;
    }

    public override int OutputCount => _functions[0].OutputCount;

    internal static StitchingFunction Parse(PdfDictionary dict, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var domain = FunctionEvaluator.RequireNumberArray(dict, "Domain", resolve);
        if (domain.Length != 2)
        {
            throw new PlumePdfException("PLUME7713", $"Type 3 function's /Domain has {domain.Length / 2} input dimension(s); a stitching function must have exactly one.");
        }

        if (!dict.TryGetValue(PdfName.Get("Functions"), out var functionsObj) || FunctionEvaluator.Resolve(functionsObj, resolve) is not PdfArray functionArray)
        {
            throw new PlumePdfException("PLUME7713", "Type 3 function is missing a required /Functions array.");
        }

        var functions = new PdfFunction[functionArray.Count];
        for (var i = 0; i < functionArray.Count; i++)
        {
            functions[i] = FunctionEvaluator.Parse(FunctionEvaluator.Resolve(functionArray[i], resolve), resolve, filters, options, diagnostics);
        }

        if (functions.Length == 0)
        {
            throw new PlumePdfException("PLUME7713", "Type 3 function's /Functions array is empty.");
        }

        var bounds = FunctionEvaluator.TryNumberArray(dict, "Bounds", resolve) ?? [];
        if (bounds.Length != functions.Length - 1)
        {
            throw new PlumePdfException("PLUME7713", $"Type 3 function's /Bounds has {bounds.Length} entries; expected {functions.Length - 1} (one fewer than /Functions).");
        }

        var encode = FunctionEvaluator.RequireNumberArray(dict, "Encode", resolve);
        if (encode.Length != functions.Length * 2)
        {
            throw new PlumePdfException("PLUME7713", $"Type 3 function's /Encode has {encode.Length} entries; expected {functions.Length * 2} (two per subfunction).");
        }

        var range = FunctionEvaluator.TryNumberArray(dict, "Range", resolve);
        return new StitchingFunction(domain, range, functions, bounds, encode);
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> input, Span<double> output)
    {
        var x = input[0];
        var k = _bounds.Length;
        var i = 0;
        while (i < k && x >= _bounds[i])
        {
            i++;
        }

        var dMin = i > 0 ? _bounds[i - 1] : Domain[0];
        var dMax = i < k ? _bounds[i] : Domain[1];
        var mapped = Interpolate(x, dMin, dMax, _encode[2 * i], _encode[(2 * i) + 1]);

        Span<double> subInput = [mapped];
        _functions[i].Evaluate(subInput, output);
    }
}

/// <summary>
/// Type 4 — a PostScript calculator function: a small, non-Turing-complete subset of the
/// PostScript language (arithmetic, comparison, boolean/bitwise, stack manipulation, and
/// <c>if</c>/<c>ifelse</c> — no loops, no procedure definitions beyond the two <c>if</c>/
/// <c>ifelse</c> bodies, so evaluation always terminates) restricted to the operator set
/// ISO 32000-1 §7.10.5 Table 42 defines. Implemented in-house against that operator set's
/// well-known public semantics (not derived from any single implementation's source) rather
/// than ported, since the "compile a tiny stack language to a jump table" architecture pdf.js
/// now uses (<c>postscript/js_evaluator.js</c>) is itself JS-codegen-specific and has no
/// C#-portable shape. All math routes through <see cref="RasterMath"/> — libm-free.
/// </summary>
internal sealed class PostScriptFunction : PdfFunction
{
    private readonly object[] _program;

    private PostScriptFunction(double[] domain, double[] range, object[] program)
        : base(domain, range)
    {
        _program = program;
    }

    public override int OutputCount => (Range?.Length ?? 0) / 2;

    internal static PostScriptFunction Parse(PdfDictionary dict, ReadOnlyMemory<byte> decodedStreamBytes, Func<IndirectReference, PdfObject> resolve)
    {
        var domain = FunctionEvaluator.RequireNumberArray(dict, "Domain", resolve);
        var range = FunctionEvaluator.RequireNumberArray(dict, "Range", resolve);

        var source = System.Text.Encoding.Latin1.GetString(decodedStreamBytes.Span);
        var tokens = Tokenize(source);
        var pos = 0;
        var program = tokens.Count > 0 && tokens[0] is char c && c == '{'
            ? ParseBlock(tokens, ref pos)
            : ParseFlat(tokens, ref pos);

        return new PostScriptFunction(domain, range, program);
    }

    private protected override void EvaluateCore(ReadOnlySpan<double> input, Span<double> output)
    {
        var stack = new List<object>(input.Length + 16);
        foreach (var v in input)
        {
            stack.Add(v);
        }

        Execute(_program, stack);

        var outputCount = OutputCount;
        var start = Math.Max(0, stack.Count - outputCount);
        for (var j = 0; j < outputCount; j++)
        {
            var idx = start + j;
            output[j] = idx < stack.Count && stack[idx] is double d ? d : 0.0;
        }
    }

    private static void Execute(object[] program, List<object> stack)
    {
        foreach (var token in program)
        {
            switch (token)
            {
                case double d:
                    stack.Add(d);
                    break;
                case object[] block:
                    stack.Add(block);
                    break;
                case string op:
                    ExecuteOperator(op, stack);
                    break;
            }
        }
    }

    private static void ExecuteOperator(string op, List<object> stack)
    {
        switch (op)
        {
            case "add": BinaryNum(stack, static (a, b) => a + b); break;
            case "sub": BinaryNum(stack, static (a, b) => a - b); break;
            case "mul": BinaryNum(stack, static (a, b) => a * b); break;
            case "div": BinaryNum(stack, static (a, b) => b == 0 ? 0 : a / b); break;
            case "idiv": BinaryNum(stack, static (a, b) => (int)b == 0 ? 0 : (double)((long)a / (long)b)); break;
            case "mod": BinaryNum(stack, static (a, b) => (long)b == 0 ? 0 : (double)((long)a % (long)b)); break;
            case "neg": UnaryNum(stack, static a => -a); break;
            case "abs": UnaryNum(stack, Math.Abs); break;
            case "sqrt": UnaryNum(stack, static a => RasterMath.Sqrt(Math.Max(0, a))); break;
            case "sin": UnaryNum(stack, RasterMath.SinDegrees); break;
            case "cos": UnaryNum(stack, RasterMath.CosDegrees); break;
            case "atan": BinaryNum(stack, static (num, den) => RasterMath.AtanDegrees(num, den)); break;
            case "exp": BinaryNum(stack, static (b, e) => RasterMath.Pow(b, e)); break;
            case "ln": UnaryNum(stack, static a => RasterMath.Ln(Math.Max(1e-300, a))); break;
            case "log": UnaryNum(stack, static a => RasterMath.Log10(Math.Max(1e-300, a))); break;
            case "ceiling": UnaryNum(stack, Math.Ceiling); break;
            case "floor": UnaryNum(stack, Math.Floor); break;
            case "round": UnaryNum(stack, static a => Math.Floor(a + 0.5)); break;
            case "truncate": UnaryNum(stack, Math.Truncate); break;
            case "cvi": UnaryNum(stack, Math.Truncate); break;
            case "cvr": break;

            case "eq": Compare(stack, static (a, b) => a == b); break;
            case "ne": Compare(stack, static (a, b) => a != b); break;
            case "gt": Compare(stack, static (a, b) => a > b); break;
            case "ge": Compare(stack, static (a, b) => a >= b); break;
            case "lt": Compare(stack, static (a, b) => a < b); break;
            case "le": Compare(stack, static (a, b) => a <= b); break;

            case "and": BooleanOrBitwise(stack, static (a, b) => a && b, static (a, b) => a & b); break;
            case "or": BooleanOrBitwise(stack, static (a, b) => a || b, static (a, b) => a | b); break;
            case "xor": BooleanOrBitwise(stack, static (a, b) => a ^ b, static (a, b) => a ^ b); break;
            case "not":
                {
                    var v = Pop(stack);
                    stack.Add(v is bool b ? !b : (double)(~(long)(double)v));
                    break;
                }

            case "bitshift":
                {
                    var shift = (int)(double)Pop(stack);
                    var value = (long)(double)Pop(stack);
                    stack.Add((double)(shift >= 0 ? value << shift : value >> -shift));
                    break;
                }

            case "true": stack.Add(true); break;
            case "false": stack.Add(false); break;

            case "if":
                {
                    var proc = (object[])Pop(stack);
                    var cond = (bool)Pop(stack);
                    if (cond)
                    {
                        Execute(proc, stack);
                    }

                    break;
                }

            case "ifelse":
                {
                    var proc2 = (object[])Pop(stack);
                    var proc1 = (object[])Pop(stack);
                    var cond = (bool)Pop(stack);
                    Execute(cond ? proc1 : proc2, stack);
                    break;
                }

            case "pop": Pop(stack); break;
            case "exch":
                {
                    var b = Pop(stack);
                    var a = Pop(stack);
                    stack.Add(b);
                    stack.Add(a);
                    break;
                }

            case "dup":
                {
                    var a = Pop(stack);
                    stack.Add(a);
                    stack.Add(a);
                    break;
                }

            case "copy":
                {
                    var n = (int)(double)Pop(stack);
                    if (n > 0)
                    {
                        var start = Math.Max(0, stack.Count - n);
                        var snapshot = stack.GetRange(start, stack.Count - start);
                        stack.AddRange(snapshot);
                    }

                    break;
                }

            case "index":
                {
                    var n = (int)(double)Pop(stack);
                    var idx = stack.Count - 1 - n;
                    stack.Add(idx >= 0 && idx < stack.Count ? stack[idx] : 0.0);
                    break;
                }

            case "roll":
                {
                    var j = (int)(double)Pop(stack);
                    var n = (int)(double)Pop(stack);
                    if (n > 0 && n <= stack.Count)
                    {
                        var start = stack.Count - n;
                        var segment = stack.GetRange(start, n);
                        var shift = ((j % n) + n) % n;
                        var rotated = new object[n];
                        for (var i = 0; i < n; i++)
                        {
                            rotated[(i + shift) % n] = segment[i];
                        }

                        stack.RemoveRange(start, n);
                        stack.AddRange(rotated);
                    }

                    break;
                }
        }
    }

    private static object Pop(List<object> stack)
    {
        if (stack.Count == 0)
        {
            return 0.0;
        }

        var v = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        return v;
    }

    private static double PopNum(List<object> stack) => Pop(stack) is double d ? d : 0.0;

    private static void UnaryNum(List<object> stack, Func<double, double> f) => stack.Add(f(PopNum(stack)));

    private static void BinaryNum(List<object> stack, Func<double, double, double> f)
    {
        var b = PopNum(stack);
        var a = PopNum(stack);
        stack.Add(f(a, b));
    }

    private static void Compare(List<object> stack, Func<double, double, bool> f)
    {
        var b = PopNum(stack);
        var a = PopNum(stack);
        stack.Add(f(a, b));
    }

    private static void BooleanOrBitwise(List<object> stack, Func<bool, bool, bool> boolOp, Func<long, long, long> intOp)
    {
        var b = Pop(stack);
        var a = Pop(stack);
        if (a is bool ba && b is bool bb)
        {
            stack.Add(boolOp(ba, bb));
        }
        else
        {
            var ia = a is double da ? (long)da : 0;
            var ib = b is double db ? (long)db : 0;
            stack.Add((double)intOp(ia, ib));
        }
    }

    // --- Tokenizer/parser -------------------------------------------------------------------

    private static List<object> Tokenize(string source)
    {
        var tokens = new List<object>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '{' or '}')
            {
                tokens.Add(c);
                i++;
                continue;
            }

            if (c == '%')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            var start = i;
            while (i < source.Length && !char.IsWhiteSpace(source[i]) && source[i] is not ('{' or '}' or '%'))
            {
                i++;
            }

            var word = source[start..i];
            if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                tokens.Add(number);
            }
            else if (word.Length > 0)
            {
                tokens.Add(word);
            }
        }

        return tokens;
    }

    /// <summary>Parses a <c>{ ... }</c> block starting at <paramref name="pos"/> (which must point at the opening brace), consuming through its matching closing brace.</summary>
    private static object[] ParseBlock(List<object> tokens, ref int pos)
    {
        pos++; // consume '{'
        var items = new List<object>();
        while (pos < tokens.Count)
        {
            if (tokens[pos] is char c)
            {
                if (c == '}')
                {
                    pos++; // consume '}'
                    break;
                }

                if (c == '{')
                {
                    items.Add(ParseBlock(tokens, ref pos)); // recursion consumes through its own '}' and advances pos past it
                    continue;
                }
            }

            items.Add(tokens[pos]);
            pos++;
        }

        return [.. items];
    }

    /// <summary>Fallback for malformed input with no enclosing top-level braces: treats the whole flat token stream as the program body.</summary>
    private static object[] ParseFlat(List<object> tokens, ref int pos)
    {
        var items = new List<object>();
        while (pos < tokens.Count)
        {
            if (tokens[pos] is char c && c == '{')
            {
                items.Add(ParseBlock(tokens, ref pos));
                continue;
            }

            items.Add(tokens[pos]);
            pos++;
        }

        return [.. items];
    }
}

/// <summary>
/// Parses a PDF <c>/Function</c> entry (ISO 32000-1 §7.10) into an evaluatable
/// <see cref="PdfFunction"/> — the sole entry point every consumer (shadings,
/// Separation/DeviceN) goes through. Shared, in-house, libm-free math lives in
/// <see cref="RasterMath"/> alongside the Type 4 interpreter that most needs it.
/// </summary>
internal static class FunctionEvaluator
{
    /// <summary>
    /// Parses a single function object — a <see cref="PdfDictionary"/> (Type 2/3, which carry
    /// no stream payload) or a <see cref="PdfStream"/> (Type 0/4, whose stream holds the
    /// sample table or PostScript program).
    /// </summary>
    internal static PdfFunction Parse(PdfObject functionObject, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var resolved = Resolve(functionObject, resolve);
        var (dict, stream) = resolved switch
        {
            PdfStream s => (s.Dictionary, s),
            PdfDictionary d => (d, null),
            _ => throw new PlumePdfException("PLUME7709", $"A /Function entry must be a dictionary or stream, found {resolved.GetType().Name}."),
        };

        var typeNumber = (int)RequireNumber(dict, "FunctionType", resolve);
        return typeNumber switch
        {
            0 => SampledFunction.Parse(dict, RequireStreamBytes(stream, filters, options, diagnostics, resolve), resolve),
            2 => ExponentialFunction.Parse(dict, resolve),
            3 => StitchingFunction.Parse(dict, resolve, filters, options, diagnostics),
            4 => PostScriptFunction.Parse(dict, RequireStreamBytes(stream, filters, options, diagnostics, resolve), resolve),
            _ => throw new PlumePdfException("PLUME7709", $"Unsupported /FunctionType {typeNumber} (ISO 32000-1 §7.10 defines 0, 2, 3, and 4; Type 1 sampled-vs-mesh function-based shadings are Phase 9 scope)."),
        };
    }

    /// <summary>
    /// Parses a <c>/Function</c> entry that may be either a single multi-output function or an
    /// array of single-output functions (ISO 32000-1 §7.10.1) — the shape shadings' own
    /// <c>/Function</c> key permits.
    /// </summary>
    internal static PdfFunction ParseArray(PdfObject functionOrArray, Func<IndirectReference, PdfObject> resolve, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var resolved = Resolve(functionOrArray, resolve);
        if (resolved is not PdfArray array)
        {
            return Parse(resolved, resolve, filters, options, diagnostics);
        }

        var functions = new PdfFunction[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            functions[i] = Parse(array[i], resolve, filters, options, diagnostics);
        }

        return functions.Length == 1 ? functions[0] : new FunctionArray(functions);
    }

    internal static PdfObject Resolve(PdfObject value, Func<IndirectReference, PdfObject> resolve) =>
        value is PdfReference reference ? resolve(reference.Target) : value;

    private static byte[] RequireStreamBytes(PdfStream? stream, PdfFilterRegistry filters, PdfOptions options, DiagnosticCollection? diagnostics, Func<IndirectReference, PdfObject> resolve)
    {
        if (stream is null)
        {
            throw new PlumePdfException("PLUME7709", "This /FunctionType requires a stream (sample table or PostScript program), but a plain dictionary was given.");
        }

        return stream.GetDecodedBytes(filters, options, r => resolve(r));
    }

    internal static double RequireNumber(PdfDictionary dict, string key, Func<IndirectReference, PdfObject> resolve)
    {
        if (!dict.TryGetValue(PdfName.Get(key), out var value) || Resolve(value, resolve) is not PdfNumber number)
        {
            throw new PlumePdfException("PLUME7709", $"Function dictionary is missing required numeric key /{key}.");
        }

        return number.Value;
    }

    internal static double[] RequireNumberArray(PdfDictionary dict, string key, Func<IndirectReference, PdfObject> resolve) =>
        TryNumberArray(dict, key, resolve) ?? throw new PlumePdfException("PLUME7709", $"Function dictionary is missing required array key /{key}.");

    internal static double[]? TryNumberArray(PdfDictionary dict, string key, Func<IndirectReference, PdfObject> resolve)
    {
        if (!dict.TryGetValue(PdfName.Get(key), out var value) || Resolve(value, resolve) is not PdfArray array)
        {
            return null;
        }

        var result = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            result[i] = Resolve(array[i], resolve) is PdfNumber n ? n.Value : 0.0;
        }

        return result;
    }
}

/// <summary>
/// In-house, libm-free math primitives for the Raster layer's deterministic evaluation paths
/// (function evaluation, radial-shading circle solving) — <see cref="System.Math"/>'s
/// <c>Sin</c>/<c>Cos</c>/<c>Pow</c>/<c>Exp</c>/<c>Log</c>/<c>Sqrt</c> route through the
/// platform's C runtime libm, whose exact bit pattern is not guaranteed identical across
/// platforms/library versions even for the same input. Every routine here uses only <c>+</c>/
/// <c>-</c>/<c>*</c>/<c>/</c> and IEEE-754 bit manipulation (<see cref="BitConverter"/>,
/// <see cref="Math.ScaleB"/> — exact power-of-two scaling, not a transcendental), so results
/// are reproducible from the same in-house arithmetic on any platform reachable from the same
/// input, byte-for-byte on a given platform/architecture/.NET runtime (the same scope
/// <see cref="PdfOptions.Deterministic"/> already promises everywhere else). Standard
/// numerical-analysis techniques (Newton–Raphson square roots, range-reduced Taylor series,
/// the argument-halving arctangent identity) — textbook methods, not derived from any single
/// implementation's source.
/// </summary>
internal static class RasterMath
{
    private const double Ln2 = 0.69314718055994530942;
    private const double Ln10 = 2.30258509299404568402;
    private const double Pi = 3.14159265358979323846;
    private const double HalfPi = Pi / 2.0;
    private const double DegToRad = Pi / 180.0;
    private const double RadToDeg = 180.0 / Pi;

    /// <summary>Square root via Newton–Raphson from an exact IEEE-754 exponent-halved seed. Never calls <see cref="Math.Sqrt"/>.</summary>
    internal static double Sqrt(double x)
    {
        if (double.IsNaN(x) || x < 0)
        {
            return double.NaN;
        }

        if (x == 0 || double.IsPositiveInfinity(x))
        {
            return x;
        }

        // Decompose x = m * 2^e with m in [0.5, 1); make e even so sqrt(x) = sqrt(m') * 2^(e/2)
        // for some m' in [0.5, 2).
        var m = Frexp(x, out var e);
        if ((e & 1) != 0)
        {
            m *= 2;
            e -= 1;
        }

        // m is now in [0.5, 2); a cheap linear seed (sqrt is concave there) gets Newton into
        // its quadratic-convergence basin within a handful of iterations.
        var guess = 0.5 + (m * 0.5);
        for (var i = 0; i < 8; i++)
        {
            guess = 0.5 * (guess + (m / guess));
        }

        return Math.ScaleB(guess, e / 2);
    }

    /// <summary>Natural exponential via range reduction (<c>x = k*ln2 + r</c>) plus a Taylor series for the small remainder <c>r</c>. Never calls <see cref="Math.Exp"/>.</summary>
    internal static double Exp(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x > 709.78)
        {
            return double.PositiveInfinity;
        }

        if (x < -745.13)
        {
            return 0.0;
        }

        var k = (long)(x / Ln2 >= 0 ? (x / Ln2) + 0.5 : (x / Ln2) - 0.5);
        var r = x - (k * Ln2);

        double term = 1;
        double sum = 1;
        for (var n = 1; n <= 24; n++)
        {
            term *= r / n;
            sum += term;
        }

        return Math.ScaleB(sum, (int)k);
    }

    /// <summary>Natural logarithm via IEEE-754 exponent extraction plus an <c>atanh</c> series for the mantissa. Never calls <see cref="Math.Log(double)"/>.</summary>
    internal static double Ln(double x)
    {
        if (double.IsNaN(x) || x < 0)
        {
            return double.NaN;
        }

        if (x == 0)
        {
            return double.NegativeInfinity;
        }

        var m = Frexp(x, out var e);

        // ln(m) = 2*atanh((m-1)/(m+1)), |t| <= 1/3 for m in [0.5, 1) — converges fast.
        var t = (m - 1) / (m + 1);
        var t2 = t * t;
        double term = t;
        double sum = t;
        for (var n = 1; n <= 12; n++)
        {
            term *= t2;
            sum += term / ((2 * n) + 1);
        }

        return (2 * sum) + (e * Ln2);
    }

    internal static double Log10(double x) => double.IsNaN(x) || x <= 0 ? (x == 0 ? double.NegativeInfinity : double.NaN) : Ln(x) / Ln10;

    /// <summary>Raises <paramref name="x"/> to the power <paramref name="n"/>. Integer exponents use exact repeated squaring; the general case is <c>exp(n * ln(x))</c>.</summary>
    internal static double Pow(double x, double n)
    {
        if (n == 0)
        {
            return 1;
        }

        if (x == 0)
        {
            return n > 0 ? 0 : double.PositiveInfinity;
        }

        if (n == Math.Truncate(n) && Math.Abs(n) <= 1024)
        {
            var exponent = (long)n;
            var negative = exponent < 0;
            var magnitude = negative ? -exponent : exponent;
            double result = 1;
            var baseValue = x;
            while (magnitude > 0)
            {
                if ((magnitude & 1) != 0)
                {
                    result *= baseValue;
                }

                baseValue *= baseValue;
                magnitude >>= 1;
            }

            return negative ? 1 / result : result;
        }

        if (x < 0)
        {
            // Non-integer power of a negative base is undefined over the reals; PDF content
            // streams that hit this are already malformed (Type 2 functions with fractional N
            // over a negative-going domain) — return 0 rather than NaN so it composites as
            // "no contribution" instead of poisoning the rest of the pixel.
            return 0;
        }

        return Exp(n * Ln(x));
    }

    /// <summary>Sine of an angle given in degrees. Reduces to a base octant then uses a Taylor series over <c>[-π/4, π/4]</c>. Never calls <see cref="Math.Sin"/>.</summary>
    internal static double SinDegrees(double degrees) => SinCosDegrees(degrees).Sin;

    /// <summary>Cosine of an angle given in degrees. See <see cref="SinDegrees"/>. Never calls <see cref="Math.Cos"/>.</summary>
    internal static double CosDegrees(double degrees) => SinCosDegrees(degrees).Cos;

    private static (double Sin, double Cos) SinCosDegrees(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees))
        {
            return (double.NaN, double.NaN);
        }

        var radians = degrees * DegToRad;
        var quadrant = (long)(radians / HalfPi >= 0 ? (radians / HalfPi) + 0.5 : (radians / HalfPi) - 0.5);
        var reduced = radians - (quadrant * HalfPi);

        var (s, c) = TaylorSinCos(reduced);
        return ((quadrant & 3) switch
        {
            0 => s,
            1 => c,
            2 => -s,
            _ => -c,
        },
        (quadrant & 3) switch
        {
            0 => c,
            1 => -s,
            2 => -c,
            _ => s,
        });
    }

    private static (double Sin, double Cos) TaylorSinCos(double r)
    {
        double sinSum = r, sinTerm = r;
        double cosSum = 1, cosTerm = 1;
        var r2 = r * r;
        for (var n = 1; n <= 10; n++)
        {
            sinTerm *= -r2 / ((2 * n) * ((2 * n) + 1));
            sinSum += sinTerm;
            cosTerm *= -r2 / (((2 * n) - 1) * (2 * n));
            cosSum += cosTerm;
        }

        return (sinSum, cosSum);
    }

    /// <summary>
    /// PostScript's <c>num den atan angle</c> operator: the angle in degrees, <c>[0, 360)</c>,
    /// whose tangent is <c>num/den</c>, resolved to the correct quadrant from the signs of
    /// both arguments (standard <c>atan2</c> semantics). Never calls <see cref="Math.Atan2"/>.
    /// </summary>
    internal static double AtanDegrees(double num, double den)
    {
        double radians;
        if (den > 0)
        {
            radians = Atan(num / den);
        }
        else if (den < 0)
        {
            radians = num >= 0 ? Atan(num / den) + Pi : Atan(num / den) - Pi;
        }
        else
        {
            radians = num > 0 ? HalfPi : num < 0 ? -HalfPi : 0;
        }

        var degrees = radians * RadToDeg;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    /// <summary>Arctangent (radians) via the argument-halving identity <c>atan(t) = 2*atan(t/(1+sqrt(1+t²)))</c>, applied until the residual argument is small enough for a plain series.</summary>
    private static double Atan(double t)
    {
        if (double.IsNaN(t))
        {
            return double.NaN;
        }

        var sign = t < 0 ? -1.0 : 1.0;
        var v = Math.Abs(t);

        const int halvings = 6;
        var scale = 1.0;
        for (var i = 0; i < halvings; i++)
        {
            v /= 1 + Sqrt(1 + (v * v));
            scale *= 2;
        }

        // v is now tiny; the plain series converges in a handful of terms.
        var v2 = v * v;
        double term = v, sum = v;
        for (var n = 1; n <= 6; n++)
        {
            term *= -v2;
            sum += term / ((2 * n) + 1);
        }

        return sign * scale * sum;
    }

    /// <summary>Decomposes <paramref name="x"/> (positive, finite) into <c>m * 2^e</c> with <c>m</c> in <c>[0.5, 1)</c> — the C runtime's <c>frexp</c>, implemented via direct IEEE-754 bit manipulation rather than a libm call.</summary>
    private static double Frexp(double x, out int exponent)
    {
        var bits = BitConverter.DoubleToInt64Bits(x);
        var rawExponent = (int)((bits >> 52) & 0x7FF);
        if (rawExponent == 0)
        {
            // Subnormal: normalize by scaling up first (multiplying by 2^64 is exact).
            var scaled = x * 18446744073709551616.0; // 2^64
            bits = BitConverter.DoubleToInt64Bits(scaled);
            rawExponent = (int)((bits >> 52) & 0x7FF);
            var mantissaBits = (bits & 0x000FFFFFFFFFFFFFL) | 0x3FE0000000000000L;
            exponent = rawExponent - 1022 - 64;
            return BitConverter.Int64BitsToDouble(mantissaBits | (bits & unchecked((long)0x8000000000000000)));
        }

        exponent = rawExponent - 1022;
        var normalizedMantissaBits = (bits & 0x000FFFFFFFFFFFFFL) | 0x3FE0000000000000L;
        return BitConverter.Int64BitsToDouble(normalizedMantissaBits | (bits & unchecked((long)0x8000000000000000)));
    }
}
