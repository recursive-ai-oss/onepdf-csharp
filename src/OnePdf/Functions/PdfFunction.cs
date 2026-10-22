using System;
using System.Collections.Generic;

namespace OnePdf.Functions;

/// <summary>What went wrong during one evaluation. The outputs are always defined; a non-zero status is for a diagnostic.</summary>
[Flags]
internal enum FunctionStatus
{
    /// <summary>Evaluated cleanly.</summary>
    None = 0,

    /// <summary>A Type 4 program ran past the operation limit and was stopped.</summary>
    OperationLimit = 1,

    /// <summary>A Type 4 operator found too few operands; the program was stopped.</summary>
    StackUnderflow = 2,

    /// <summary>A Type 4 program grew the operand stack past its bound; the program was stopped.</summary>
    StackOverflow = 4,

    /// <summary>An arithmetic operation had no defined result (division by zero, square root of a negative, ...); 0 was used.</summary>
    MathError = 8,

    /// <summary>A Type 4 operator got operands of the wrong type; they were converted.</summary>
    TypeError = 16,

    /// <summary>A Type 4 program left a number of results different from the function's output count.</summary>
    BadResultCount = 32,

    /// <summary>The function could not be compiled (Type 4); every evaluation returns the clipped zero vector.</summary>
    BadProgram = 64,

    /// <summary>The input had fewer values than the function's input count; missing ones were taken as 0.</summary>
    ShortInput = 128,
}

/// <summary>
/// A PDF function (ISO 32000-2 7.10): numbers in, numbers out. Build once with <see cref="Create"/>, then call
/// <see cref="Evaluate"/> any number of times from any number of threads; evaluation allocates nothing.
/// </summary>
/// <remarks>
/// Arithmetic is <c>double</c> with a fixed evaluation order and no fused multiply-add, and the transcendental
/// functions are our own (<see cref="DetMath"/>), so results are identical on every platform.
/// Inputs are clipped to /Domain and outputs to /Range (when present) before and after evaluation. NaN clips to the lower bound.
/// </remarks>
internal abstract class PdfFunction
{
    /// <summary>Most functions built from one root object, counting every nested one. Blocks shared-reference blow-up.</summary>
    private const int MaxNodes = 4096;

    /// <summary>Most input dimensions of a sampled function (the interpolation visits 2^m corners).</summary>
    internal const int MaxSampledInputs = 16;

    private readonly double[] _domain;
    private readonly double[]? _range;

    private protected PdfFunction(int inputs, int outputs, double[] domain, double[]? range)
    {
        InputCount = inputs;
        OutputCount = outputs;
        _domain = domain;
        _range = range;
    }

    /// <summary>Number of input values.</summary>
    public int InputCount { get; }

    /// <summary>Number of output values.</summary>
    public int OutputCount { get; }

    /// <summary>A description of a repair made while building (for example a truncated sample table), or null.</summary>
    public string? Problem { get; private protected set; }

    /// <summary>
    /// Evaluates the function. Missing inputs count as 0; extra ones are ignored. Never throws for any numbers.
    /// </summary>
    /// <param name="input">At least <see cref="InputCount"/> values.</param>
    /// <param name="output">At least <see cref="OutputCount"/> values; only that many are written.</param>
    /// <exception cref="ArgumentException"><paramref name="output"/> is shorter than <see cref="OutputCount"/>.</exception>
    public abstract FunctionStatus Evaluate(ReadOnlySpan<double> input, Span<double> output);

    private protected double ClipInput(int i, double v)
    {
        double lo = _domain[2 * i];
        double hi = _domain[(2 * i) + 1];
        return Clip(v, lo, hi);
    }

    private protected void ClipOutputs(Span<double> output)
    {
        if (_range is null)
        {
            return;
        }

        for (int j = 0; j < OutputCount; j++)
        {
            output[j] = Clip(output[j], _range[2 * j], _range[(2 * j) + 1]);
        }
    }

    private protected static double Clip(double v, double lo, double hi)
    {
        if (!(v > lo))
        {
            return lo;
        }

        return v > hi ? hi : v;
    }

    private protected FunctionStatus CheckArguments(ReadOnlySpan<double> input, Span<double> output)
    {
        if (output.Length < OutputCount)
        {
            throw new ArgumentException("The output span is shorter than OutputCount.", nameof(output));
        }

        return input.Length < InputCount ? FunctionStatus.ShortInput : FunctionStatus.None;
    }

    /// <summary>
    /// Builds a function from a function dictionary or stream, or the name /Identity. Returns null with
    /// <paramref name="problem"/> set when the object is not a usable function (missing or inconsistent required entries,
    /// nesting deeper than <see cref="PdfLimits.MaxRecursionDepth"/>, more than a few thousand nested functions).
    /// A damaged Type 4 program does not return null: it yields a function whose evaluations report
    /// <see cref="FunctionStatus.BadProgram"/>, so a page still draws.
    /// </summary>
    /// <exception cref="PdfLimitException">A Type 0 or Type 4 stream exceeds the decoded-size limits.</exception>
    public static PdfFunction? Create(PdfObject? obj, PdfLimits limits, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var ctx = new BuildContext(limits);
        PdfFunction? f = Build(obj, ctx, 0);
        problem = f is null ? ctx.Error ?? "Not a function." : f.Problem ?? ctx.Warning;
        return f;
    }

    /// <summary>As <c>Create</c> for a dictionary whose stream bytes the caller already holds (Types 0 and 4).</summary>
    internal static PdfFunction? CreateFromParts(PdfDictionary dictionary, ReadOnlyMemory<byte> streamData, PdfLimits limits, out string? problem)
    {
        var ctx = new BuildContext(limits);
        PdfFunction? f = BuildFrom(dictionary, streamData, ctx, 0);
        problem = f is null ? ctx.Error ?? "Not a function." : f.Problem ?? ctx.Warning;
        return f;
    }

    private sealed class BuildContext(PdfLimits limits)
    {
        public PdfLimits Limits { get; } = limits;

        public int Nodes { get; set; }

        public string? Error { get; set; }

        public string? Warning { get; set; }
    }

    private static PdfFunction? Build(PdfObject? obj, BuildContext ctx, int depth)
    {
        if (obj is PdfName { Value: "Identity" })
        {
            return new IdentityFunction();
        }

        PdfDictionary? dict = obj switch
        {
            PdfStream s => s.Dictionary,
            PdfDictionary d => d,
            _ => null,
        };
        if (dict is null)
        {
            ctx.Error = "A function must be a dictionary, a stream or /Identity.";
            return null;
        }

        long type = dict.GetInt("FunctionType", -1);
        ReadOnlyMemory<byte> data = default;
        if (type is 0 or 4)
        {
            if (obj is not PdfStream stream)
            {
                ctx.Error = $"A Type {type} function must be a stream.";
                return null;
            }

            data = stream.GetDecodedBytes();
        }

        return BuildFrom(dict, data, ctx, depth);
    }

    private static PdfFunction? BuildFrom(PdfDictionary dict, ReadOnlyMemory<byte> data, BuildContext ctx, int depth)
    {
        if (depth > ctx.Limits.MaxRecursionDepth)
        {
            ctx.Error = "Functions are nested too deeply (or refer to themselves).";
            return null;
        }

        if (++ctx.Nodes > MaxNodes)
        {
            ctx.Error = "Too many nested functions.";
            return null;
        }

        long type = dict.GetInt("FunctionType", -1);
        double[]? domain = ReadNumbers(dict, "Domain");
        if (domain is null || domain.Length < 2)
        {
            ctx.Error = "A function needs a /Domain array of at least one pair.";
            return null;
        }

        domain = Normalize(domain);
        double[]? range = ReadNumbers(dict, "Range");
        if (range is not null)
        {
            range = range.Length >= 2 ? Normalize(range) : null;
        }

        switch (type)
        {
            case 0:
                return SampledFunction.TryCreate(dict, data, domain, range, m => ctx.Error = m);
            case 2:
                return ExponentialFunction.TryCreate(dict, domain, range, m => ctx.Error = m);
            case 3:
                return BuildStitching(dict, domain, range, ctx, depth);
            case 4:
                if (range is null)
                {
                    ctx.Error = "A Type 4 function needs a /Range.";
                    return null;
                }

                return new PostScriptFunction(domain, range, data.Span, ctx.Limits.MaxType4Operations);
            default:
                ctx.Error = $"Unknown /FunctionType {type}.";
                return null;
        }
    }

    private static StitchingFunction? BuildStitching(PdfDictionary dict, double[] domain, double[]? range, BuildContext ctx, int depth)
    {
        PdfArray? fns = dict.GetArray("Functions");
        double[]? bounds = ReadNumbers(dict, "Bounds");
        double[]? encode = ReadNumbers(dict, "Encode");
        if (fns is null || fns.Count == 0 || bounds is null || encode is null)
        {
            ctx.Error = "A Type 3 function needs /Functions, /Bounds and /Encode.";
            return null;
        }

        int k = fns.Count;
        if (bounds.Length != k - 1 || encode.Length < 2 * k)
        {
            ctx.Error = "A Type 3 function's /Bounds and /Encode do not match its /Functions.";
            return null;
        }

        var subs = new PdfFunction[k];
        int outputs = -1;
        for (int i = 0; i < k; i++)
        {
            PdfObject? item = fns[i];
            PdfFunction? sub = item is null ? null : BuildNested(item, ctx, depth + 1);
            if (sub is null)
            {
                ctx.Error ??= "A Type 3 sub-function is not valid.";
                return null;
            }

            if (sub.InputCount != 1 || (outputs >= 0 && sub.OutputCount != outputs))
            {
                ctx.Error = "The sub-functions of a Type 3 function must take one input and agree on the output count.";
                return null;
            }

            outputs = sub.OutputCount;
            subs[i] = sub;
        }

        if (range is not null && range.Length < 2 * outputs)
        {
            range = null;
        }

        return new StitchingFunction(domain, range, subs, bounds, encode, outputs);
    }

    private static PdfFunction? BuildNested(PdfObject item, BuildContext ctx, int depth)
    {
        if (item is PdfStream or PdfDictionary or PdfName)
        {
            return Build(item, ctx, depth);
        }

        return null;
    }

    /// <summary>Reads a numeric array entry; null when absent or not an array. Non-numbers read as 0.</summary>
    internal static double[]? ReadNumbers(PdfDictionary dict, string key)
    {
        if (dict.GetArray(key) is not { } a)
        {
            return null;
        }

        var r = new double[a.Count];
        for (int i = 0; i < r.Length; i++)
        {
            double v = a.GetDouble(i);
            r[i] = double.IsNaN(v) ? 0 : v;
        }

        return r;
    }

    // Makes every (lo, hi) pair ordered and drops an odd trailing value.
    private static double[] Normalize(double[] a)
    {
        int n = a.Length & ~1;
        var r = new double[n];
        for (int i = 0; i < n; i += 2)
        {
            double lo = a[i];
            double hi = a[i + 1];
            if (lo > hi)
            {
                (lo, hi) = (hi, lo);
            }

            r[i] = lo;
            r[i + 1] = hi;
        }

        return r;
    }

    /// <summary>Maps <paramref name="x"/> from [x0, x1] to [y0, y1] linearly (ISO 32000-2 7.10.2 Interpolate); a degenerate input interval gives y0.</summary>
    private protected static double Interpolate(double x, double x0, double x1, double y0, double y1)
    {
        double d = x1 - x0;
        if (d == 0)
        {
            return y0;
        }

        return y0 + ((x - x0) * (y1 - y0) / d);
    }

    /// <summary>The /Identity function: one input, returned unchanged.</summary>
    private sealed class IdentityFunction : PdfFunction
    {
        public IdentityFunction()
            : base(1, 1, [double.NegativeInfinity, double.PositiveInfinity], null)
        {
        }

        public override FunctionStatus Evaluate(ReadOnlySpan<double> input, Span<double> output)
        {
            FunctionStatus s = CheckArguments(input, output);
            output[0] = input.Length > 0 ? input[0] : 0;
            return s;
        }
    }

    /// <summary>Type 2 (ISO 32000-2 7.10.3): y_j = C0_j + x^N (C1_j - C0_j).</summary>
    private sealed class ExponentialFunction : PdfFunction
    {
        private readonly double[] _c0;
        private readonly double[] _c1;
        private readonly double _n;

        private ExponentialFunction(double[] domain, double[]? range, double[] c0, double[] c1, double n)
            : base(1, c0.Length, domain, range)
        {
            _c0 = c0;
            _c1 = c1;
            _n = n;
        }

        public static ExponentialFunction? TryCreate(PdfDictionary dict, double[] domain, double[]? range, Action<string> fail)
        {
            double[] c0 = ReadNumbers(dict, "C0") ?? [0.0];
            double[] c1 = ReadNumbers(dict, "C1") ?? [1.0];
            if (c0.Length == 0 || c0.Length != c1.Length)
            {
                fail("A Type 2 function's /C0 and /C1 must have the same, non-zero length.");
                return null;
            }

            if (range is not null && range.Length < 2 * c0.Length)
            {
                range = null;
            }

            return new ExponentialFunction(domain, range, c0, c1, dict.GetDouble("N", 1));
        }

        public override FunctionStatus Evaluate(ReadOnlySpan<double> input, Span<double> output)
        {
            FunctionStatus status = CheckArguments(input, output);
            double x = ClipInput(0, input.Length > 0 ? input[0] : 0);
            double p = DetMath.Pow(x, _n);
            if (!double.IsFinite(p))
            {
                status |= FunctionStatus.MathError;
                p = 0;
            }

            for (int j = 0; j < _c0.Length; j++)
            {
                output[j] = _c0[j] + (p * (_c1[j] - _c0[j]));
            }

            ClipOutputs(output);
            return status;
        }
    }

    /// <summary>Type 3 (ISO 32000-2 7.10.4): a 1-input function made of sub-functions over consecutive sub-domains.</summary>
    private sealed class StitchingFunction : PdfFunction
    {
        private readonly PdfFunction[] _subs;
        private readonly double[] _bounds;
        private readonly double[] _encode;

        public StitchingFunction(double[] domain, double[]? range, PdfFunction[] subs, double[] bounds, double[] encode, int outputs)
            : base(1, outputs, domain, range)
        {
            _subs = subs;
            _bounds = bounds;
            _encode = encode;
        }

        public override FunctionStatus Evaluate(ReadOnlySpan<double> input, Span<double> output)
        {
            FunctionStatus status = CheckArguments(input, output);
            double x = ClipInput(0, input.Length > 0 ? input[0] : 0);
            int k = _subs.Length;
            int i = 0;

            // Sub-domain i is [Bounds[i-1], Bounds[i]); a value equal to a bound belongs to the sub-function above it.
            while (i < k - 1 && x >= _bounds[i])
            {
                i++;
            }

            double lo = i == 0 ? DomainLow : _bounds[i - 1];
            double hi = i == k - 1 ? DomainHigh : _bounds[i];
            Span<double> t = stackalloc double[1];
            t[0] = Interpolate(x, lo, hi, _encode[2 * i], _encode[(2 * i) + 1]);
            status |= _subs[i].Evaluate(t, output);
            ClipOutputs(output);
            return status;
        }

        private double DomainLow => ClipInput(0, double.NegativeInfinity);

        private double DomainHigh => ClipInput(0, double.PositiveInfinity);
    }
}
