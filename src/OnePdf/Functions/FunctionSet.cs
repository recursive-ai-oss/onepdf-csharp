using System;

namespace OnePdf.Functions;

/// <summary>
/// Builds the "function or array of functions" values that shading dictionaries use (ISO 32000-2 8.7.4.3 Table 79): one
/// n-output function, or an array of n functions that each produce one output.
/// </summary>
internal static class FunctionSet
{
    /// <summary>
    /// Creates one function that produces <paramref name="outputs"/> values, from either form. An array must hold exactly
    /// <paramref name="outputs"/> one-output functions that agree on their input count. A single function that returns more
    /// than <paramref name="outputs"/> values is accepted (the extra values are ignored); fewer is rejected.
    /// </summary>
    /// <returns>The function, or null with <paramref name="problem"/> set.</returns>
    public static PdfFunction? Create(PdfObject? obj, int outputs, PdfLimits limits, out string? problem)
    {
        if (obj is not PdfArray array)
        {
            PdfFunction? single = PdfFunction.Create(obj, limits, out problem);
            if (single is not null && single.OutputCount < outputs)
            {
                problem = "A shading function returns fewer values than the colour space has components.";
                return null;
            }

            return single;
        }

        if (array.Count != outputs || outputs < 1 || outputs > 32)
        {
            problem = "A shading's array of functions must have one function per colour component.";
            return null;
        }

        var parts = new PdfFunction[outputs];
        for (int i = 0; i < outputs; i++)
        {
            PdfFunction? f = PdfFunction.Create(array[i], limits, out problem);
            if (f is null)
            {
                return null;
            }

            if (f.OutputCount != 1 || (i > 0 && f.InputCount != parts[0].InputCount))
            {
                problem = "The functions in a shading's function array must each return one value and share their input count.";
                return null;
            }

            parts[i] = f;
        }

        problem = null;
        return new ParallelFunction(parts);
    }

    /// <summary>n one-output functions evaluated on the same input, outputs side by side.</summary>
    private sealed class ParallelFunction : PdfFunction
    {
        private readonly PdfFunction[] _parts;

        public ParallelFunction(PdfFunction[] parts)
            : base(parts[0].InputCount, parts.Length, Unbounded(parts[0].InputCount), null)
        {
            _parts = parts;
        }

        private static double[] Unbounded(int inputs)
        {
            var d = new double[inputs * 2];
            for (int i = 0; i < inputs; i++)
            {
                d[2 * i] = double.NegativeInfinity;
                d[(2 * i) + 1] = double.PositiveInfinity;
            }

            return d;
        }

        public override FunctionStatus Evaluate(ReadOnlySpan<double> input, Span<double> output)
        {
            FunctionStatus status = CheckArguments(input, output);
            Span<double> one = stackalloc double[1];
            for (int i = 0; i < _parts.Length; i++)
            {
                status |= _parts[i].Evaluate(input, one);
                output[i] = one[0];
            }

            return status;
        }
    }
}
