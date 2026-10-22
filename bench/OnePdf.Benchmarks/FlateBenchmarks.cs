using System;
using System.IO;
using System.IO.Compression;
using BenchmarkDotNet.Attributes;
using OnePdf.Filters;

namespace OnePdf.Benchmarks;

/// <summary>Flate decode of a ~10 MB stream: OnePDF's inflater against the framework's ZLibStream.</summary>
[MemoryDiagnoser]
public class FlateBenchmarks
{
    private byte[] _plain = [];
    private byte[] _zlib = [];
    private byte[] _png = [];
    private byte[] _pngZlib = [];
    private byte[] _work = [];

    [Params("text", "mixed")]
    public string Data { get; set; } = "text";

    [GlobalSetup]
    public void Setup()
    {
        var rnd = new Random(7);
        _plain = new byte[10_000_000];
        if (Data == "text")
        {
            // Word-like text: runs and repeats at many distances.
            string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "stream", "object", "endobj", "0 0 612 792 re f"];
            int p = 0;
            while (p < _plain.Length)
            {
                string w = words[rnd.Next(words.Length)] + (rnd.Next(9) == 0 ? "\n" : " ");
                for (int i = 0; i < w.Length && p < _plain.Length; i++)
                {
                    _plain[p++] = (byte)w[i];
                }
            }
        }
        else
        {
            // Half noise, half repetition: stresses literals and short matches together.
            for (int i = 0; i < _plain.Length; i++)
            {
                _plain[i] = (i / 4096 % 2 == 0) ? (byte)rnd.Next(64) : (byte)(i % 251);
            }
        }

        _zlib = Compress(_plain);
        int rowBytes = 3000;
        int rows = _plain.Length / rowBytes;
        _png = new byte[rows * (rowBytes + 1)];
        for (int r = 0; r < rows; r++)
        {
            _png[r * (rowBytes + 1)] = (byte)(r % 5);
            Array.Copy(_plain, r * rowBytes, _png, (r * (rowBytes + 1)) + 1, rowBytes);
        }

        _pngZlib = Compress(_png);
        _work = new byte[_png.Length];
    }

    private static byte[] Compress(byte[] d)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(d);
        }

        return ms.ToArray();
    }

    [Benchmark(Baseline = true)]
    public int BclZLibStreamIntoExactArray()
    {
        var dst = new byte[_plain.Length];
        using var z = new ZLibStream(new MemoryStream(_zlib), CompressionMode.Decompress);
        z.ReadExactly(dst);
        return dst.Length;
    }

    [Benchmark]
    public int BclZLibStreamCopyToMemoryStream()
    {
        using var z = new ZLibStream(new MemoryStream(_zlib), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        z.CopyTo(ms);
        return (int)ms.Length;
    }

    [Benchmark]
    public int OnePdfFlate() => StreamFilters.Decode("FlateDecode", _zlib, null, DecodeLimits.Default).Data.Length;

    [Benchmark]
    public int OnePdfFlateWithPngPredictor() =>
        StreamFilters.Decode("FlateDecode", _pngZlib, new FilterParameters(Predictor: 15, Columns: 3000), DecodeLimits.Default).Data.Length;

    /// <summary>Cost of refreshing the working copy; subtract it from <see cref="OnePdfPngPredictorOnly"/>.</summary>
    [Benchmark]
    public int CopyOnlyForPredictorRow()
    {
        Array.Copy(_png, _work, _png.Length);
        return _work.Length;
    }

    /// <summary>The PNG predictor alone (rows cycling through all five filter tags), on a fresh copy of the encoded rows.</summary>
    [Benchmark]
    public int OnePdfPngPredictorOnly()
    {
        Array.Copy(_png, _work, _png.Length);
        FilterIssues issues = FilterIssues.None;
        string? message = null;
        return Predictors.Undo(_work, _work.Length, new FilterParameters(Predictor: 15, Columns: 3000), ref issues, ref message);
    }
}
