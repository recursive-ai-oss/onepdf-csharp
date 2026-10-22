using System.Text;
using OnePdf.Functions;

namespace OnePdf.Tests.Functions;

public class FunctionTests
{
    // --- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>Opens a file whose object 4 (and following) are the given functions, and builds the function at object 4.</summary>
    private static PdfFunction Build(string dict, byte[]? data = null, params string[] more)
    {
        var b = new PdfBuilder();
        b.BasicPages();
        if (data is null)
        {
            b.Obj(4, dict.StartsWith("<<", StringComparison.Ordinal) ? dict : "<< " + dict + " >>");
        }
        else
        {
            b.Stream(4, dict, data);
        }

        for (int i = 0; i < more.Length; i++)
        {
            b.Obj(5 + i, more[i]);
        }

        int size = 5 + more.Length;
        long x = b.XrefTable(size);
        b.Trailer($"/Size {size} /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(b.ToArray());
        PdfFunction? f = PdfFunction.Create(doc.GetObject(4), doc.Limits, out string? problem);
        Assert.True(f is not null, problem);
        return f;
    }

    private static PostScriptFunction Ps(string program, string domain = "[0 1]", string range = "[-1e9 1e9]", PdfLimits? limits = null)
    {
        double[] d = Nums(domain);
        double[] r = Nums(range);
        return new PostScriptFunction(d, r, Encoding.Latin1.GetBytes(program), (limits ?? PdfLimits.Default).MaxType4Operations);
    }

    private static double[] Nums(string s) => s.Trim('[', ']').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static double[] Eval(PdfFunction f, params double[] input)
    {
        double[] o = new double[f.OutputCount];
        FunctionStatus s = f.Evaluate(input, o);
        Assert.True(s == FunctionStatus.None, "status " + s);
        return o;
    }

    private static double One(string program) => Eval(Ps("{ pop " + program + " }", range: "[-1e12 1e12]"), 0)[0];

    private static void Close(double expected, double actual, double tol = 1e-12) => Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected}, got {actual}");

    // --- Type 4 --------------------------------------------------------------------------------------------------------

    [Fact]
    public void SpecExampleSumOfSines()
    {
        // ISO 32000-2 7.10.5: { 360 mul sin 2 div exch 360 mul sin 2 div add } on two inputs.
        PdfFunction f = Ps("{ 360 mul sin 2 div exch 360 mul sin 2 div add }", "[0 1 0 1]", "[-1 1]");
        Assert.Equal(2, f.InputCount);
        Close(1.0, Eval(f, 0.25, 0.25)[0]);
        Close(0.0, Eval(f, 0.25, 0.75)[0]);
        Close(0.5, Eval(f, 0.25, 0.0)[0]);
    }

    [Fact]
    public void IfAndIfElse()
    {
        PdfFunction f = Ps("{ dup 0.5 gt { 1 exch sub } { 2 mul } ifelse }");
        Close(0.4, Eval(f, 0.2)[0]);
        Close(0.2, Eval(f, 0.8)[0]);
        PdfFunction g = Ps("{ dup 0.5 lt { pop 0 } if }");
        Close(0.0, Eval(g, 0.2)[0]);
        Close(0.75, Eval(g, 0.75)[0]);

        // Nested conditionals and a block before and after another.
        PdfFunction h = Ps("{ dup 0.25 lt { pop 10 } { dup 0.5 lt { pop 20 } { pop 30 } ifelse } ifelse 1 add }");
        Close(11, Eval(h, 0.1)[0]);
        Close(21, Eval(h, 0.4)[0]);
        Close(31, Eval(h, 0.9)[0]);
    }

    [Theory]
    [InlineData("-7 2 idiv", -3)]
    [InlineData("7 -2 idiv", -3)]
    [InlineData("-7 -2 idiv", 3)]
    [InlineData("-7 2 mod", -1)]
    [InlineData("7 -2 mod", 1)]
    [InlineData("7 2 mod", 1)]
    [InlineData("3.7 cvi", 3)]
    [InlineData("-3.7 cvi", -3)]
    [InlineData("-3.7 truncate", -3)]
    [InlineData("3.7 truncate", 3)]
    [InlineData("2.5 round", 3)]
    [InlineData("-2.5 round", -2)]
    [InlineData("-3.5 round", -3)]
    [InlineData("3.49 round", 3)]
    [InlineData("-0.2 ceiling", 0)]
    [InlineData("0.2 ceiling", 1)]
    [InlineData("-0.2 floor", -1)]
    [InlineData("1 3 bitshift", 8)]
    [InlineData("8 -2 bitshift", 2)]
    [InlineData("-8 -1 bitshift", 2147483644)]
    [InlineData("1 31 bitshift", -2147483648)]
    [InlineData("1 32 bitshift", 0)]
    [InlineData("-1 -32 bitshift", 0)]
    [InlineData("1 -1 atan", 135)]
    [InlineData("-1 1 atan", 315)]
    [InlineData("0 1 atan", 0)]
    [InlineData("1 0 atan", 90)]
    [InlineData("0 -1 atan", 180)]
    [InlineData("-1 0 atan", 270)]
    [InlineData("-1 -1 atan", 225)]
    [InlineData("3 3.0 eq", 1)]
    [InlineData("3 3.5 eq", 0)]
    [InlineData("3 3.5 ne", 1)]
    [InlineData("2 3.5 lt", 1)]
    [InlineData("3.5 3 le", 0)]
    [InlineData("3.5 3 ge", 1)]
    [InlineData("3 3 gt", 0)]
    [InlineData("true 1 eq", 0)]
    [InlineData("true true eq", 1)]
    [InlineData("true false or", 1)]
    [InlineData("true false and", 0)]
    [InlineData("true true xor", 0)]
    [InlineData("5 3 and", 1)]
    [InlineData("5 3 or", 7)]
    [InlineData("5 3 xor", 6)]
    [InlineData("5 not", -6)]
    [InlineData("true not", 0)]
    [InlineData("false not", 1)]
    [InlineData("7 2 div", 3.5)]
    [InlineData("-5 abs", 5)]
    [InlineData("5.5 neg", -5.5)]
    [InlineData("2147483647 1 add", 2147483648.0)]
    [InlineData("-2147483648 abs", 2147483648.0)]
    [InlineData("65536 65536 mul", 4294967296.0)]
    [InlineData("3 4 mul 5 sub", 7)]
    [InlineData("16#FF", 255)]
    [InlineData("2#101", 5)]
    [InlineData("1.5e2", 150)]
    [InlineData("-.5", -0.5)]
    [InlineData("5. 2 mul", 10)]
    [InlineData("9 sqrt", 3)]
    [InlineData("100 log", 2)]
    [InlineData("1 ln", 0)]
    [InlineData("2 8 exp", 256)]
    [InlineData("8 3 cvr 3 div cvi add", 9)]
    [InlineData("90 sin", 1)]
    [InlineData("180 cos", -1)]
    [InlineData("0 cos", 1)]
    [InlineData("-90 sin", -1)]
    [InlineData("450 sin", 1)]
    public void OperatorValues(string program, double expected) => Close(expected, One(program), 1e-12);

    [Fact]
    public void TranscendentalAccuracy()
    {
        for (double x = -720; x <= 720; x += 7.3)
        {
            Close(Math.Sin(x * Math.PI / 180), One($"{x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} sin"), 1e-14);
            Close(Math.Cos(x * Math.PI / 180), One($"{x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} cos"), 1e-14);
        }

        foreach (double x in new[] { 1e-300, 0.001, 0.5, 1, 2, 10, 123456.789, 1e100 })
        {
            double s = x;
            Close(Math.Log(s), DetMath.Ln(s), 1e-14 * Math.Max(1, Math.Abs(Math.Log(s))));
        }

        foreach (double x in new[] { -700, -20, -1, -0.1, 0, 0.3, 1, 5, 40, 700 })
        {
            double e = Math.Exp(x);
            Close(e, DetMath.Exp(x), 1e-14 * e);
        }

        for (double a = -3; a <= 3; a += 0.37)
        {
            for (double b = -3; b <= 3; b += 0.41)
            {
                double expect = Math.Atan2(a, b) * 180 / Math.PI;
                if (expect < 0)
                {
                    expect += 360;
                }

                Close(expect, DetMath.Atan2Deg(a, b), 1e-11);
            }
        }

        Close(0.5, DetMath.Pow(0.25, 0.5), 1e-15);
        Close(8, DetMath.Pow(2, 3), 0);
        Close(0.125, DetMath.Pow(2, -3), 0);
        Assert.True(double.IsNaN(DetMath.Pow(-2, 0.5)));
    }

    [Fact]
    public void StackOperators()
    {
        PdfFunction f = Ps("{ pop pop 1 2 3 3 1 roll }", "[0 1 0 1]", "[0 9 0 9 0 9]");
        Assert.Equal([3.0, 1.0, 2.0], Eval(f, 0, 0));
        f = Ps("{ pop pop 1 2 3 3 -1 roll }", "[0 1 0 1]", "[0 9 0 9 0 9]");
        Assert.Equal([2.0, 3.0, 1.0], Eval(f, 0, 0));
        f = Ps("{ pop pop 1 2 3 4 3 5 roll }", "[0 1 0 1]", "[0 9 0 9 0 9 0 9]");
        Assert.Equal([1.0, 3.0, 4.0, 2.0], Eval(f, 0, 0));
        f = Ps("{ pop 1 2 3 2 index }", "[0 1]", "[0 9 0 9 0 9 0 9]");
        Assert.Equal([1.0, 2.0, 3.0, 1.0], Eval(f, 0));
        f = Ps("{ pop 1 2 2 copy }", "[0 1]", "[0 9 0 9 0 9 0 9]");
        Assert.Equal([1.0, 2.0, 1.0, 2.0], Eval(f, 0));
        f = Ps("{ pop 1 2 exch dup }", "[0 1]", "[0 9 0 9 0 9]");
        Assert.Equal([2.0, 1.0, 1.0], Eval(f, 0));
    }

    [Fact]
    public void InputsClipToDomainAndOutputsToRange()
    {
        PdfFunction f = Ps("{ 10 mul }", "[0 1]", "[0 5]");
        Close(5, Eval(f, 0.9)[0]);
        Close(5, Eval(f, 99)[0]); // clipped to 1, 10, then to 5
        Close(0, Eval(f, -3)[0]);
        Close(0, Eval(f, double.NaN)[0]);
    }

    [Theory]
    [InlineData("1 0 div", 8)]
    [InlineData("1 0 idiv", 8)]
    [InlineData("1 0 mod", 8)]
    [InlineData("-1 sqrt", 8)]
    [InlineData("0 ln", 8)]
    [InlineData("-1 log", 8)]
    [InlineData("0 0 atan", 8)]
    [InlineData("-8 0.5 exp", 8)]
    [InlineData("add", 2)]
    [InlineData("pop pop pop pop", 2)]
    [InlineData("5 index", 2)]
    [InlineData("-1 index", 2)]
    [InlineData("9 copy", 2)]
    [InlineData("3 1 roll", 2)]
    [InlineData("1 2 3", 32)]
    [InlineData("pop", 32)]
    [InlineData("true 1 add", 16)]
    [InlineData("1.5 2 idiv", 16)]
    [InlineData("1 dup 2 copy 4 copy 8 copy 16 copy 32 copy 64 copy 128 copy 256 copy", 4)]
    public void BadOperationsGiveDefinedOutputAndAStatus(string program, int expectedBit)
    {
        PdfFunction f = Ps("{ " + program + " }", range: "[-1e12 1e12]");
        var o = new double[1];
        FunctionStatus s = f.Evaluate([0.5], o);
        Assert.True(((int)s & expectedBit) != 0, $"status {s}");
        Assert.True(double.IsFinite(o[0]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 2 add")]
    [InlineData("{ 1 2 add")]
    [InlineData("{ foo }")]
    [InlineData("{ { 1 } }")]
    [InlineData("{ 1 { 2 } 3 if }")]
    [InlineData("{ if }")]
    [InlineData("{ { 1 } ifelse }")]
    [InlineData("}")]
    [InlineData("{ 1 ] }")]
    [InlineData("{ 99999999999999999999999 }")]
    [InlineData("{ 1#0 }")]
    [InlineData("{ (str) }")]
    [InlineData("{ /name }")]
    public void UncompilablePrograms(string program)
    {
        var f = Ps(program);
        // "99999999999999999999999" is a valid real; everything else must be rejected.
        if (program.Contains("9999", StringComparison.Ordinal))
        {
            Assert.True(f.IsValid);
            return;
        }

        Assert.False(f.IsValid);
        Assert.NotNull(f.Problem);
        var o = new double[1] { 7 };
        FunctionStatus s = f.Evaluate([0.5], o);
        Assert.True((s & FunctionStatus.BadProgram) != 0);
        Assert.Equal(0, o[0]);
    }

    [Fact]
    public void CommentsAndWhitespaceAreSkipped()
    {
        PdfFunction f = Ps("% a comment\n{\r\n  2 % times two\n mul\t}% done");
        Close(1.0, Eval(f, 0.5)[0]);
    }

    [Fact]
    public void RunawayProgramStopsAtTheOperationLimit()
    {
        var sb = new StringBuilder("{ ");
        for (int i = 0; i < 200_000; i++)
        {
            sb.Append("1 pop ");
        }

        sb.Append('}');
        var limits = new PdfLimits { MaxType4Operations = 100_000 };
        PdfFunction f = Ps(sb.ToString(), limits: limits);
        var o = new double[1];
        FunctionStatus s = f.Evaluate([0.5], o);
        Assert.True((s & FunctionStatus.OperationLimit) != 0);

        // Under the limit it runs to the end.
        Assert.Equal(FunctionStatus.None, Ps("{ 1 pop 2 mul }").Evaluate([0.5], o));
        Assert.Equal(FunctionStatus.None, Ps(sb.ToString(), limits: new PdfLimits { MaxType4Operations = 1_000_000 }).Evaluate([0.5], o) & ~FunctionStatus.BadResultCount);
    }

    [Fact]
    public void DeeplyNestedBlocksCompileWithoutRecursing()
    {
        var sb = new StringBuilder("{ 1 ");
        const int Depth = 20_000;
        for (int i = 0; i < Depth; i++)
        {
            sb.Append("true { ");
        }

        sb.Append("2 add ");
        for (int i = 0; i < Depth; i++)
        {
            sb.Append("} if ");
        }

        sb.Append('}');
        PdfFunction f = Ps(sb.ToString(), range: "[0 100]");
        var o = new double[1];
        FunctionStatus s = f.Evaluate([0.5], o);
        Assert.Equal(FunctionStatus.None, s & ~FunctionStatus.BadResultCount);
    }

    [Fact]
    public void Type4FromAStreamObject()
    {
        PdfFunction f = Build("/FunctionType 4 /Domain [0 1] /Range [0 1]", "{ 1 exch sub }"u8.ToArray());
        Close(0.75, Eval(f, 0.25)[0]);
        Assert.Equal(1, f.InputCount);
    }

    // --- Type 0 --------------------------------------------------------------------------------------------------------

    [Fact]
    public void SampledOneDimensional8Bit()
    {
        PdfFunction f = Build("/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [3] /BitsPerSample 8", [0, 128, 255]);
        Close(0, Eval(f, 0)[0]);
        Close(64.0 / 255, Eval(f, 0.25)[0], 1e-15);
        Close(128.0 / 255, Eval(f, 0.5)[0], 1e-15);
        Close((128 + 127 * 0.5) / 255, Eval(f, 0.75)[0], 1e-15);
        Close(1, Eval(f, 1)[0]);
        Close(1, Eval(f, 7)[0]);
        Close(0, Eval(f, -7)[0]);
    }

    [Fact]
    public void SampledTwoDimensional8Bit()
    {
        // First input varies fastest: s(0,0)=0, s(1,0)=255, s(0,1)=51, s(1,1)=102.
        PdfFunction f = Build("/FunctionType 0 /Domain [0 1 0 1] /Range [0 1] /Size [2 2] /BitsPerSample 8", [0, 255, 51, 102]);
        Close(0, Eval(f, 0, 0)[0]);
        Close(1, Eval(f, 1, 0)[0]);
        Close(51.0 / 255, Eval(f, 0, 1)[0]);
        Close(102.0 / 255, Eval(f, 1, 1)[0]);
        Close(102.0 / 255, Eval(f, 0.5, 0.5)[0], 1e-15);
        Close(178.5 / 255, Eval(f, 1, 0.5)[0], 1e-15);
        Close((0.75 * ((0.25 * 0) + (0.75 * 255)) / 255) + (0.25 * (((0.25 * 51) + (0.75 * 102)) / 255)), Eval(f, 0.75, 0.25)[0], 1e-15);
    }

    [Fact]
    public void SampledThreeDimensionalAgainstTrilinearReference()
    {
        var data = new byte[3 * 4 * 2];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)((i * 37) % 256);
        }

        PdfFunction f = Build("/FunctionType 0 /Domain [0 1 0 1 0 1] /Range [0 1] /Size [3 4 2] /BitsPerSample 8", data);
        double S(int x, int y, int z) => data[x + (3 * (y + (4 * z)))] / 255.0;
        foreach ((double u, double v, double w) in new[] { (0.3, 0.6, 0.9), (0.0, 0.0, 0.0), (1.0, 1.0, 1.0), (0.5, 0.5, 0.5), (0.99, 0.01, 0.4) })
        {
            double ex = u * 2, ey = v * 3, ez = w * 1;
            int x0 = (int)Math.Min(Math.Floor(ex), 1), y0 = (int)Math.Min(Math.Floor(ey), 2), z0 = (int)Math.Min(Math.Floor(ez), 0);
            double fx = ex - x0, fy = ey - y0, fz = ez - z0;
            double expect = 0;
            for (int dz = 0; dz < 2; dz++)
            {
                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        double wgt = (dx == 1 ? fx : 1 - fx) * (dy == 1 ? fy : 1 - fy) * (dz == 1 ? fz : 1 - fz);
                        expect += wgt * S(Math.Min(x0 + dx, 2), Math.Min(y0 + dy, 3), Math.Min(z0 + dz, 1));
                    }
                }
            }

            Close(expect, Eval(f, u, v, w)[0], 1e-13);
        }
    }

    [Fact]
    public void SampledBitDepths()
    {
        // 12 bit: samples 0x000 and 0xFFF packed as 00 0F FF.
        PdfFunction f = Build("/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 12", [0x00, 0x0F, 0xFF]);
        Close(0.5, Eval(f, 0.5)[0], 1e-15);
        Close(0.25, Eval(f, 0.25)[0], 1e-15);

        // 12 bit, three samples 0x123 0x456 0x789 -> 12 34 56 78 90.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 4095] /Size [3] /BitsPerSample 12", [0x12, 0x34, 0x56, 0x78, 0x90]);
        Close(0x123, Eval(f, 0)[0]);
        Close(0x456, Eval(f, 0.5)[0]);
        Close(0x789, Eval(f, 1)[0]);

        // 16 bit.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 16", [0x00, 0x00, 0xFF, 0xFF]);
        Close(0.25, Eval(f, 0.25)[0], 1e-15);

        // 24 bit.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 16777215] /Size [2] /BitsPerSample 24", [0x00, 0x00, 0x00, 0x12, 0x34, 0x56]);
        Close(0x123456, Eval(f, 1)[0]);

        // 32 bit.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 32", [0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF]);
        Close(0.5, Eval(f, 0.5)[0], 1e-15);
        Close(1, Eval(f, 1)[0]);

        // 32 bit, full width value in the middle of the data.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 4294967295] /Size [2] /BitsPerSample 32", [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04]);
        Close(0xDEADBEEFL, Eval(f, 0)[0]);
        Close(0x01020304L, Eval(f, 1)[0]);

        // 1 bit: 0 1 0 1 -> bits 0101 0000.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [4] /BitsPerSample 1", [0x50]);
        Close(0, Eval(f, 0)[0]);
        Close(1, Eval(f, 1.0 / 3)[0], 1e-12);
        Close(0.5, Eval(f, 0.5)[0], 1e-12);

        // 4 bit: 1 2 3 4 -> 12 34.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 15] /Size [4] /BitsPerSample 4", [0x12, 0x34]);
        Close(3, Eval(f, 2.0 / 3)[0], 1e-12);

        // 2 bit and 5 bit run unaligned.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 31] /Size [2] /BitsPerSample 5", [0b10101_010, 0b11_000000]);
        Close(21, Eval(f, 0)[0]);
        Close(11, Eval(f, 1)[0]);
    }

    [Fact]
    public void SampledMultipleOutputsEncodeAndDecode()
    {
        // Two outputs; samples (a0, b0, a1, b1).
        PdfFunction f = Build("/FunctionType 0 /Domain [0 1] /Range [0 1 0 1] /Size [2] /BitsPerSample 8", [0, 255, 255, 0]);
        Assert.Equal(2, f.OutputCount);
        double[] o = Eval(f, 0.25);
        Close(0.25, o[0], 1e-15);
        Close(0.75, o[1], 1e-15);

        // Decode reverses the second output; Encode reverses the input.
        f = Build("/FunctionType 0 /Domain [0 1] /Range [0 1 0 1] /Size [2] /BitsPerSample 8 /Decode [0 1 1 0] /Encode [1 0]", [0, 255, 255, 0]);
        o = Eval(f, 0.25);
        Close(0.75, o[0], 1e-15);
        Close(0.75, o[1], 1e-15);

        // Domain not 0..1.
        f = Build("/FunctionType 0 /Domain [-1 1] /Range [0 100] /Size [2] /BitsPerSample 8", [0, 255]);
        Close(25, Eval(f, -0.5)[0], 1e-12);
    }

    [Fact]
    public void SampledOrderThreeIsReadAsLinear()
    {
        PdfFunction a = Build("/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [3] /BitsPerSample 8 /Order 3", [0, 200, 50]);
        PdfFunction b = Build("/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [3] /BitsPerSample 8", [0, 200, 50]);
        Assert.Equal(Eval(b, 0.3)[0], Eval(a, 0.3)[0]);
    }

    [Fact]
    public void ShortSampleTableReadsZerosAndSaysSo()
    {
        var bld = new PdfBuilder();
        bld.BasicPages();
        bld.Stream(4, "/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [4] /BitsPerSample 8", [255, 255]);
        long x = bld.XrefTable(5);
        bld.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(bld.ToArray());
        PdfFunction? f = PdfFunction.Create(doc.GetObject(4), doc.Limits, out string? problem);
        Assert.NotNull(f);
        Assert.NotNull(problem);
        Close(1, Eval(f, 0)[0]);
        Close(0, Eval(f, 1)[0]);
    }

    [Fact]
    public void SampledInvalidDictionariesAreRejected()
    {
        foreach (string d in new[]
        {
            "/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2]",
            "/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [2] /BitsPerSample 33",
            "/FunctionType 0 /Domain [0 1] /Range [0 1] /Size [0] /BitsPerSample 8",
            "/FunctionType 0 /Domain [0 1] /Size [2] /BitsPerSample 8",
            "/FunctionType 0 /Range [0 1] /Size [2] /BitsPerSample 8",
            "/FunctionType 0 /Domain [0 1 0 1] /Range [0 1] /Size [2000000000 2000000000] /BitsPerSample 32",
            "/FunctionType 0 /Domain [" + string.Join(' ', Enumerable.Repeat("0 1", 17)) + "] /Range [0 1] /Size [" + string.Join(' ', Enumerable.Repeat("2", 17)) + "] /BitsPerSample 8",
        })
        {
            var bld = new PdfBuilder();
            bld.BasicPages();
            bld.Stream(4, d, [1, 2, 3, 4]);
            long x = bld.XrefTable(5);
            bld.Trailer("/Size 5 /Root 1 0 R", x);
            using PdfDocument doc = PdfDocument.Open(bld.ToArray());
            PdfFunction? f = PdfFunction.Create(doc.GetObject(4), doc.Limits, out string? problem);
            Assert.Null(f);
            Assert.NotNull(problem);
        }
    }

    // --- Type 2 --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Exponential()
    {
        PdfFunction f = Build("/FunctionType 2 /Domain [0 1] /C0 [0 0] /C1 [1 2] /N 2");
        Assert.Equal(2, f.OutputCount);
        double[] o = Eval(f, 0.5);
        Close(0.25, o[0], 1e-15);
        Close(0.5, o[1], 1e-15);
        Close(1, Eval(f, 7)[0]); // clipped to the domain

        f = Build("/FunctionType 2 /Domain [0 1] /N 1");
        Close(0.3, Eval(f, 0.3)[0], 1e-15); // C0 = 0, C1 = 1 by default

        f = Build("/FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 0.5");
        Close(0.5, Eval(f, 0.25)[0], 1e-14);

        f = Build("/FunctionType 2 /Domain [0 1] /C0 [0.2] /C1 [0.8] /N 0");
        Close(0.8, Eval(f, 0)[0], 1e-15); // 0^0 = 1

        f = Build("/FunctionType 2 /Domain [0 1] /C0 [0] /C1 [10] /N 1 /Range [0 5]");
        Close(5, Eval(f, 1)[0]);
    }

    [Fact]
    public void ExponentialMismatchedEndpointsAreRejected()
    {
        var bld = new PdfBuilder();
        bld.BasicPages();
        bld.Obj(4, "<< /FunctionType 2 /Domain [0 1] /C0 [0 0] /C1 [1] /N 1 >>");
        long x = bld.XrefTable(5);
        bld.Trailer("/Size 5 /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(bld.ToArray());
        Assert.Null(PdfFunction.Create(doc.GetObject(4), doc.Limits, out string? problem));
        Assert.NotNull(problem);
    }

    // --- Type 3 --------------------------------------------------------------------------------------------------------

    private const string F0 = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [10] /N 1 >>";
    private const string F1 = "<< /FunctionType 2 /Domain [0 1] /C0 [100] /C1 [200] /N 1 >>";

    [Fact]
    public void StitchingPicksTheSubFunctionAndMapsWithEncode()
    {
        PdfFunction f = Build("<< /FunctionType 3 /Domain [0 1] /Functions [5 0 R 6 0 R] /Bounds [0.5] /Encode [0 1 1 0] >>", null, F0, F1);
        Close(0, Eval(f, 0)[0]);
        Close(5, Eval(f, 0.25)[0], 1e-12); // 0.25 -> 0.5 in [0,1] -> 5
        Close(10, Eval(f, 0.4999999)[0], 1e-5); // end of the first interval
        Close(200, Eval(f, 0.5)[0]); // the bound belongs to the second function, whose reversed encode maps it to 1
        Close(150, Eval(f, 0.75)[0], 1e-12); // 0.75 -> 0.5 -> 150
        Close(100, Eval(f, 1)[0]); // last: maps to 0 -> 100
        Close(100, Eval(f, 9)[0]);
    }

    [Fact]
    public void StitchingThreeIntervalsAndNesting()
    {
        const string Inner = "<< /FunctionType 3 /Domain [0 1] /Functions [5 0 R 6 0 R] /Bounds [0.5] /Encode [0 1 0 1] >>";
        PdfFunction f = Build("<< /FunctionType 3 /Domain [0 1] /Functions [5 0 R 7 0 R 5 0 R] /Bounds [0.25 0.75] /Encode [0 1 0 1 0 1] >>", null, F0, F1, Inner);
        Close(4, Eval(f, 0.1)[0], 1e-12); // first interval, x' = 0.4
        // The middle interval [0.25, 0.75) feeds the inner stitching function with x' = (x - 0.25) / 0.5.
        Close(5, Eval(f, 0.375)[0], 1e-12); // x' = 0.25, inner first half at 0.5
        Close(150, Eval(f, 0.625)[0], 1e-12); // x' = 0.75, inner second half at 0.5
        Close(9, Eval(f, 0.975)[0], 1e-12); // last interval, x' = (0.975 - 0.75)/0.25 = 0.9
    }

    [Fact]
    public void StitchingRejectsCyclesDeepChainsAndBlowUp()
    {
        // A function that lists itself.
        var cyc = new PdfBuilder();
        cyc.BasicPages();
        cyc.Obj(4, "<< /FunctionType 3 /Domain [0 1] /Functions [4 0 R] /Bounds [] /Encode [0 1] >>");
        long x = cyc.XrefTable(5);
        cyc.Trailer("/Size 5 /Root 1 0 R", x);
        using (PdfDocument doc = PdfDocument.Open(cyc.ToArray()))
        {
            Assert.Null(PdfFunction.Create(doc.GetObject(4), doc.Limits, out string? problem));
            Assert.NotNull(problem);
        }

        // A chain 100 deep.
        var chain = new PdfBuilder();
        chain.BasicPages();
        for (int i = 4; i < 104; i++)
        {
            chain.Obj(i, $"<< /FunctionType 3 /Domain [0 1] /Functions [{i + 1} 0 R] /Bounds [] /Encode [0 1] >>");
        }

        chain.Obj(104, F0);
        x = chain.XrefTable(105);
        chain.Trailer("/Size 105 /Root 1 0 R", x);
        using (PdfDocument doc = PdfDocument.Open(chain.ToArray()))
        {
            Assert.Null(PdfFunction.Create(doc.GetObject(4), doc.Limits, out string? problem));
            Assert.Contains("deeply", problem, StringComparison.Ordinal);
        }

        // 25 levels, each listing the next one twice: 2^25 leaves unless the node budget stops it.
        var blow = new PdfBuilder();
        blow.BasicPages();
        for (int i = 4; i < 29; i++)
        {
            blow.Obj(i, $"<< /FunctionType 3 /Domain [0 1] /Functions [{i + 1} 0 R {i + 1} 0 R] /Bounds [0.5] /Encode [0 1 0 1] >>");
        }

        blow.Obj(29, F0);
        x = blow.XrefTable(30);
        blow.Trailer("/Size 30 /Root 1 0 R", x);
        using (PdfDocument doc = PdfDocument.Open(blow.ToArray()))
        {
            Assert.Null(PdfFunction.Create(doc.GetObject(4), doc.Limits, out string? problem));
            Assert.NotNull(problem);
        }

        // A moderate nesting works.
        var ok = new PdfBuilder();
        ok.BasicPages();
        for (int i = 4; i < 14; i++)
        {
            ok.Obj(i, $"<< /FunctionType 3 /Domain [0 1] /Functions [{i + 1} 0 R] /Bounds [] /Encode [0 1] >>");
        }

        ok.Obj(14, F0);
        x = ok.XrefTable(15);
        ok.Trailer("/Size 15 /Root 1 0 R", x);
        using (PdfDocument doc = PdfDocument.Open(ok.ToArray()))
        {
            PdfFunction? f = PdfFunction.Create(doc.GetObject(4), doc.Limits, out _);
            Assert.NotNull(f);
            Close(5, Eval(f, 0.5)[0], 1e-12);
        }
    }

    [Fact]
    public void IdentityAndNonFunctions()
    {
        var bld = new PdfBuilder();
        bld.BasicPages();
        bld.Obj(4, "/Identity");
        bld.Obj(5, "42");
        bld.Obj(6, "<< /FunctionType 9 /Domain [0 1] >>");
        bld.Obj(7, "<< /FunctionType 2 >>");
        long x = bld.XrefTable(8);
        bld.Trailer("/Size 8 /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(bld.ToArray());
        PdfFunction? id = PdfFunction.Create(doc.GetObject(4), doc.Limits, out _);
        Assert.NotNull(id);
        Close(0.3, Eval(id, 0.3)[0], 0);
        Close(-7, Eval(id, -7)[0], 0);
        foreach (int n in new[] { 5, 6, 7 })
        {
            Assert.Null(PdfFunction.Create(doc.GetObject(n), doc.Limits, out string? problem));
            Assert.NotNull(problem);
        }

        Assert.Null(PdfFunction.Create(null, doc.Limits, out _));
    }

    [Fact]
    public void ShortOutputSpanThrowsArgumentException()
    {
        PdfFunction f = Build("/FunctionType 2 /Domain [0 1] /C0 [0 0] /C1 [1 1] /N 1");
        Assert.Throws<ArgumentException>(() => f.Evaluate([0.5], new double[1]));
        Assert.Equal(FunctionStatus.ShortInput, f.Evaluate([], new double[2]));
    }

    // --- allocation -----------------------------------------------------------------------------------------------------

    [Fact]
    public void EvaluationAllocatesNothing()
    {
        PdfFunction t0 = Build("/FunctionType 0 /Domain [0 1 0 1] /Range [0 1 0 1 0 1] /Size [4 4] /BitsPerSample 8", Enumerable.Range(0, 96).Select(i => (byte)(i * 5)).ToArray());
        PdfFunction t2 = Build("/FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 0.5 0.2] /N 1.5");
        PdfFunction t3 = Build("<< /FunctionType 3 /Domain [0 1] /Functions [5 0 R 6 0 R] /Bounds [0.5] /Encode [0 1 0 1] >>", null, F0, F1);
        PdfFunction t4 = Ps("{ dup 0.5 gt { 1 exch sub } { 2 mul } ifelse dup 360 mul sin 2 div exch 3 1 roll pop 2 copy add }", range: "[0 5 0 5 0 5]");
        var o = new double[3];
        double[] x = [0.37, 0.61];
        foreach (PdfFunction f in new[] { t0, t2, t3, t4 })
        {
            for (int i = 0; i < 1000; i++)
            {
                f.Evaluate(x, o);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++)
            {
                x[0] = (i & 1023) / 1023.0;
                f.Evaluate(x, o);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [Fact]
    public void ParallelEvaluationGivesTheSameAnswers()
    {
        PdfFunction f = Ps("{ dup 360 mul sin exch 2 mul add }", range: "[-10 10]");
        double[] expected = Enumerable.Range(0, 1000).Select(i => Eval(f, i / 1000.0)[0]).ToArray();
        Parallel.For(0, 8, _ =>
        {
            var o = new double[1];
            for (int rep = 0; rep < 50; rep++)
            {
                for (int i = 0; i < 1000; i++)
                {
                    f.Evaluate([i / 1000.0], o);
                    Assert.Equal(expected[i], o[0]);
                }
            }
        });
    }

    [Fact]
    public void RandomProgramsNeverThrow()
    {
        string[] words = ["1", "2", "0", "-1", "0.5", "add", "sub", "mul", "div", "idiv", "mod", "exch", "dup", "pop", "copy", "index", "roll", "if", "ifelse", "{", "}", "eq", "lt", "and", "not", "cvi", "sqrt", "ln", "exp", "atan", "bitshift", "true", "sin", "neg", "abs", "round"];
        var rnd = new Random(77);
        for (int i = 0; i < 20_000; i++)
        {
            var sb = new StringBuilder("{ ");
            int n = rnd.Next(1, 30);
            for (int k = 0; k < n; k++)
            {
                sb.Append(words[rnd.Next(words.Length)]).Append(' ');
            }

            sb.Append('}');
            PdfFunction f = Ps(sb.ToString(), "[0 1 0 1]", "[-5 5 -5 5]");
            var o = new double[2];
            f.Evaluate([rnd.NextDouble(), rnd.NextDouble()], o);
            Assert.True(double.IsFinite(o[0]) && double.IsFinite(o[1]));
        }
    }
}
