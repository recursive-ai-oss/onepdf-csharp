using System;
using OnePdf.Functions;

namespace OnePdf.Fuzz;

/// <summary>
/// Type 4 (PostScript calculator) target. The first two bytes pick the input and output counts; the rest is the program text.
/// Compilation and evaluation must never throw, must stop at the operation limit, and must keep every output finite and inside /Range.
/// </summary>
internal static class Type4Target
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return;
        }

        int inputs = 1 + (data[0] & 3);
        int outputs = 1 + (data[1] & 3);
        var domain = new double[inputs * 2];
        for (int i = 0; i < inputs; i++)
        {
            domain[2 * i] = -1;
            domain[(2 * i) + 1] = 1;
        }

        var range = new double[outputs * 2];
        for (int i = 0; i < outputs; i++)
        {
            range[2 * i] = -10;
            range[(2 * i) + 1] = 10;
        }

        var f = new PostScriptFunction(domain, range, data.Slice(2), 5000);
        var inp = new double[inputs];
        var outp = new double[outputs];
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < inputs; i++)
            {
                inp[i] = round switch { 0 => 0, 1 => 0.5 - i, _ => double.NaN };
            }

            f.Evaluate(inp, outp);
            for (int j = 0; j < outputs; j++)
            {
                if (!(outp[j] >= -10 && outp[j] <= 10))
                {
                    throw new InvalidOperationException("output outside /Range");
                }
            }
        }
    }
}
