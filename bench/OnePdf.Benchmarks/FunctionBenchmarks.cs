using System;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using OnePdf.Functions;

namespace OnePdf.Benchmarks;

/// <summary>One million evaluations of each function type, the way a shading evaluates a colour function per pixel.</summary>
[MemoryDiagnoser]
public class FunctionBenchmarks
{
    private const int N = 1_000_000;
    private PdfFunction _sampled1 = null!;
    private PdfFunction _sampled3 = null!;
    private PdfFunction _exponential = null!;
    private PdfFunction _stitching = null!;
    private PdfFunction _postScript = null!;

    [GlobalSetup]
    public void Setup()
    {
        var clut = new byte[17 * 17 * 17 * 3];
        new Random(5).NextBytes(clut);
        var ramp = new byte[256];
        for (int i = 0; i < ramp.Length; i++)
        {
            ramp[i] = (byte)i;
        }

        // Each function is object 1 of its own small file.
        _sampled3 = Make("<< /FunctionType 0 /Domain [0 1 0 1 0 1] /Range [0 1 0 1 0 1] /Size [17 17 17] /BitsPerSample 8", clut);
        _sampled1 = Make("<< /FunctionType 0 /Domain [0 1] /Range [0 1 0 1 0 1] /Size [256] /BitsPerSample 8", Triple(ramp));
        _exponential = Make("<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 0.5 0.25] /N 1.5 >>", null);
        _stitching = Make("<< /FunctionType 3 /Domain [0 1] /Bounds [0.5] /Encode [0 1 0 1] /Functions [<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 2 >>] >>", null);
        _postScript = Make("<< /FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1]", Encoding.ASCII.GetBytes("{ dup 0.5 gt { 1 exch sub } { 2 mul } ifelse dup 360 mul sin 2 div 0.5 add exch dup mul 1 index }"));
    }

    private static byte[] Triple(byte[] ramp)
    {
        var r = new byte[ramp.Length * 3];
        for (int i = 0; i < ramp.Length; i++)
        {
            r[3 * i] = ramp[i];
            r[(3 * i) + 1] = (byte)(255 - ramp[i]);
            r[(3 * i) + 2] = (byte)(ramp[i] / 2);
        }

        return r;
    }

    private static PdfFunction Make(string dictionary, byte[]? stream)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        long o1 = ms.Position;
        W("1 0 obj\n" + dictionary);
        if (stream is null)
        {
            W("\nendobj\n");
        }
        else
        {
            W(" /Length " + stream.Length + " >>\nstream\n");
            ms.Write(stream);
            W("\nendstream\nendobj\n");
        }

        long o2 = ms.Position;
        W("2 0 obj\n<< /Type /Catalog /Pages 3 0 R >>\nendobj\n");
        long o3 = ms.Position;
        W("3 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        long xref = ms.Position;
        W($"xref\n0 4\n0000000000 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n{o3:D10} 00000 n \n");
        W($"trailer\n<< /Size 4 /Root 2 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        using PdfDocument doc = PdfDocument.Open(ms.ToArray());
        return PdfFunction.Create(doc.GetObject(1), doc.Limits, out string? problem) ?? throw new InvalidOperationException(problem);
    }

    private static double Run(PdfFunction f, int inputs)
    {
        Span<double> input = stackalloc double[3];
        Span<double> output = stackalloc double[3];
        double sum = 0;
        for (int i = 0; i < N; i++)
        {
            double t = (i & 1023) / 1023.0;
            input[0] = t;
            input[1] = 1 - t;
            input[2] = t * t;
            f.Evaluate(input.Slice(0, inputs), output);
            sum += output[0];
        }

        return sum;
    }

    [Benchmark]
    public double Type0Ramp() => Run(_sampled1, 1);

    [Benchmark]
    public double Type0Clut() => Run(_sampled3, 3);

    [Benchmark]
    public double Type2() => Run(_exponential, 1);

    [Benchmark]
    public double Type3() => Run(_stitching, 1);

    [Benchmark]
    public double Type4() => Run(_postScript, 1);
}
