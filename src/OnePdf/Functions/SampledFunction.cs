using System;

namespace OnePdf.Functions;

/// <summary>
/// Type 0, sampled (ISO 32000-2 7.10.2): a table of samples interpolated linearly in every input dimension.
/// </summary>
/// <remarks>
/// /Order 3 (cubic) is read as linear: the specification allows a consumer to choose linear interpolation, and the
/// two agree exactly at the sample points. Samples are read straight from the decoded stream bits on each call, 1 to 32
/// bits each, first dimension varying fastest. A table shorter than its declared size reads as zeros after its end and
/// <see cref="PdfFunction.Problem"/> says so. At most <see cref="PdfFunction.MaxSampledInputs"/> input dimensions.
/// </remarks>
internal sealed class SampledFunction : PdfFunction
{
    private readonly int _m;
    private readonly int[] _size;
    private readonly long[] _stride; // in samples, per input dimension
    private readonly double[] _encode;
    private readonly double[] _domain;
    private readonly double[] _decodeMin;
    private readonly double[] _decodeScale; // (Decode_max - Decode_min) / (2^bps - 1)
    private readonly int _bps;
    private readonly ReadOnlyMemory<byte> _data;

    private SampledFunction(int m, int n, double[] domain, double[]? range, int[] size, long[] stride, double[] encode, double[] decodeMin, double[] decodeScale, int bps, ReadOnlyMemory<byte> data)
        : base(m, n, domain, range)
    {
        _m = m;
        _domain = domain;
        _size = size;
        _stride = stride;
        _encode = encode;
        _decodeMin = decodeMin;
        _decodeScale = decodeScale;
        _bps = bps;
        _data = data;
    }

    public static SampledFunction? TryCreate(PdfDictionary dict, ReadOnlyMemory<byte> data, double[] domain, double[]? range, Action<string> fail)
    {
        int m = domain.Length / 2;
        if (m > MaxSampledInputs)
        {
            fail($"A Type 0 function with {m} inputs is not supported (at most {MaxSampledInputs}).");
            return null;
        }

        if (range is null)
        {
            fail("A Type 0 function needs a /Range.");
            return null;
        }

        int n = range.Length / 2;
        if (n > 256)
        {
            fail("A Type 0 function with more than 256 outputs is not supported.");
            return null;
        }

        double[]? sizeNums = ReadNumbers(dict, "Size");
        if (sizeNums is null || sizeNums.Length < m)
        {
            fail("A Type 0 function needs a /Size entry per input.");
            return null;
        }

        long bps = dict.GetInt("BitsPerSample", 0);
        if (bps < 1 || bps > 32)
        {
            fail("A Type 0 function's /BitsPerSample must be 1 to 32.");
            return null;
        }

        var size = new int[m];
        var stride = new long[m];
        long total = n; // samples including the output components
        for (int i = 0; i < m; i++)
        {
            double s = sizeNums[i];
            if (!(s >= 1) || s > int.MaxValue)
            {
                fail("A Type 0 function's /Size entries must be positive integers.");
                return null;
            }

            size[i] = (int)s;
            stride[i] = total / n;
            if (total > (1L << 40) / size[i])
            {
                fail("A Type 0 function's sample table is implausibly large.");
                return null;
            }

            total *= size[i];
        }

        double[] encode = ReadNumbers(dict, "Encode") ?? [];
        if (encode.Length < 2 * m)
        {
            double[] e = new double[2 * m];
            for (int i = 0; i < m; i++)
            {
                bool have = encode.Length >= (2 * i) + 2;
                e[2 * i] = have ? encode[2 * i] : 0;
                e[(2 * i) + 1] = have ? encode[(2 * i) + 1] : size[i] - 1;
            }

            encode = e;
        }

        double[] dec = ReadNumbers(dict, "Decode") ?? range;
        if (dec.Length < 2 * n)
        {
            dec = range;
        }

        double maxSample = bps == 32 ? 4294967295.0 : (double)((1L << (int)bps) - 1);
        var decMin = new double[n];
        var decScale = new double[n];
        for (int j = 0; j < n; j++)
        {
            decMin[j] = dec[2 * j];
            decScale[j] = (dec[(2 * j) + 1] - dec[2 * j]) / maxSample;
        }

        var f = new SampledFunction(m, n, domain, range, size, stride, encode, decMin, decScale, (int)bps, data);
        long needBits = total * bps;
        if (needBits > (long)data.Length * 8)
        {
            f.Problem = "The sample table of a Type 0 function is shorter than its /Size and /BitsPerSample declare; the missing samples read as 0.";
        }

        return f;
    }

    public override FunctionStatus Evaluate(ReadOnlySpan<double> input, Span<double> output)
    {
        FunctionStatus status = CheckArguments(input, output);
        int m = _m;
        int n = OutputCount;
        Span<int> lo = stackalloc int[m];
        Span<double> frac = stackalloc double[m];
        for (int i = 0; i < m; i++)
        {
            double x = ClipInput(i, i < input.Length ? input[i] : 0);
            double e = Interpolate(x, _domain[2 * i], _domain[(2 * i) + 1], _encode[2 * i], _encode[(2 * i) + 1]);
            double top = _size[i] - 1;
            if (!(e > 0))
            {
                e = 0;
            }
            else if (e > top)
            {
                e = top;
            }

            double fl = Math.Floor(e);
            lo[i] = (int)fl;
            frac[i] = e - fl;
        }

        output.Slice(0, n).Clear();
        ReadOnlySpan<byte> data = _data.Span;
        int corners = 1 << m;
        for (int mask = 0; mask < corners; mask++)
        {
            double w = 1.0;
            long index = 0;
            for (int i = 0; i < m; i++)
            {
                if (((mask >> i) & 1) != 0)
                {
                    w *= frac[i];
                    index += Math.Min(lo[i] + 1, _size[i] - 1) * _stride[i];
                }
                else
                {
                    w *= 1.0 - frac[i];
                    index += lo[i] * _stride[i];
                }
            }

            if (w == 0)
            {
                continue;
            }

            long first = index * n;
            for (int j = 0; j < n; j++)
            {
                output[j] += w * ReadSample(data, first + j);
            }
        }

        for (int j = 0; j < n; j++)
        {
            output[j] = _decodeMin[j] + (output[j] * _decodeScale[j]);
        }

        ClipOutputs(output);
        return status;
    }

    // Samples are packed most significant bit first with no padding between them (ISO 32000-2 7.10.2).
    private double ReadSample(ReadOnlySpan<byte> data, long sampleIndex)
    {
        long bitPos = sampleIndex * _bps;
        long at = bitPos >> 3;
        if (at >= data.Length)
        {
            return 0;
        }

        int i = (int)at;
        if (_bps == 8)
        {
            return data[i];
        }

        int shift = (int)(bitPos & 7);
        ulong v = 0;
        for (int k = 0; k < 5; k++)
        {
            v = (v << 8) | (i + k < data.Length ? data[i + k] : (uint)0);
        }

        // 40 bits held; the sample starts `shift` bits in and is _bps wide.
        v >>= 40 - shift - _bps;
        return (double)(v & ((1UL << _bps) - 1));
    }
}
