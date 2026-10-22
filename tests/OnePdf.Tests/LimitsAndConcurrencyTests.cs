using System.Text;

namespace OnePdf.Tests;

public class LimitsAndConcurrencyTests
{
    private static byte[] WithObject4(string body)
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Obj(4, body);
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        return b.ToArray();
    }

    [Fact]
    public void NestingBeyondTheLimitIsRejectedBeforeAllocating()
    {
        string deep(int n) => new string('[', n) + new string(']', n);

        using (PdfDocument ok = PdfDocument.Open(WithObject4(deep(256))))
        {
            Assert.NotNull(ok.GetObject(4));
        }

        using PdfDocument doc = PdfDocument.Open(WithObject4(deep(257)));
        PdfLimitException e = Assert.Throws<PdfLimitException>(() => doc.GetObject(4));
        Assert.Equal(4, e.ObjectNumber);

        string dicts = string.Concat(Enumerable.Repeat("<< /A ", 1000)) + "1" + string.Concat(Enumerable.Repeat(" >>", 1000));
        using PdfDocument doc2 = PdfDocument.Open(WithObject4(dicts));
        Assert.Throws<PdfLimitException>(() => doc2.GetObject(4));
    }

    [Fact]
    public void ConfiguredNestingLimitApplies()
    {
        var opt = new PdfOpenOptions { Limits = PdfLimits.Default with { MaxNestingDepth = 3 } };
        using PdfDocument doc = PdfDocument.Open(WithObject4("[[[[1]]]]"), opt);
        Assert.Throws<PdfLimitException>(() => doc.GetObject(4));
    }

    [Fact]
    public void VeryDeepNestingNeverRecurses()
    {
        // Parsed with an explicit stack: 200,000 levels would overflow the call stack if depth became recursion.
        var opt = new PdfOpenOptions { Limits = PdfLimits.Unrestricted with { MaxNestingDepth = 1_000_000 } };
        using PdfDocument doc = PdfDocument.Open(WithObject4(new string('[', 200_000) + new string(']', 200_000)), opt);
        Assert.IsType<PdfArray>(doc.GetObject(4));
    }

    [Fact]
    public void TrailerNestingCountsToo()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.XrefTable(4);
        b.Trailer("/Size 4 /Root 1 0 R /X " + new string('[', 300), x);
        Assert.False(PdfDocument.TryOpen(b.ToArray(), out _, out PdfException? err));
        Assert.IsType<PdfLimitException>(err);
    }

    [Fact]
    public void HugeDeclaredObjectCountIsALimitError()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.Position;
        b.Write("xref\n0 100000000\n0000000000 65535 f \n");
        b.Trailer("/Size 100000000 /Root 1 0 R", x);
        Assert.False(PdfDocument.TryOpen(b.ToArray(), out _, out PdfException? err));
        Assert.IsType<PdfLimitException>(err);
    }

    [Fact]
    public void CountTheFileCannotHoldIsDamageNotAllocation()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        long x = b.Position;
        b.Write("xref\n0 5000000\n0000000000 65535 f \n");
        b.Trailer("/Size 5000000 /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(b.ToArray());
        Assert.Equal(1, doc.PageCount);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("rebuilt", StringComparison.Ordinal));
    }

    [Fact]
    public void ObjectCountLimitAppliesToRebuildScans()
    {
        var b = new PdfBuilder();
        for (int i = 1; i <= 20; i++)
        {
            b.Obj(i, i == 1 ? "<< /Type /Catalog >>" : "<< >>");
        }

        b.Write("trailer\n<< /Root 1 0 R >>\n");
        var opt = new PdfOpenOptions { Limits = PdfLimits.Default with { MaxObjects = 10 } };
        Assert.False(PdfDocument.TryOpen(b.ToArray(), out _, out PdfException? err, opt));
        Assert.IsType<PdfLimitException>(err);
    }

    [Fact]
    public void XrefStreamEntryCountIsLimited()
    {
        var b = new PdfBuilder("1.5");
        b.BasicPages();
        long at = b.Position;
        b.Stream(4, "/Type /XRef /W [1 4 2] /Size 99999999 /Root 1 0 R", [0, 0, 0, 0, 0, 0, 0]);
        b.Write($"startxref\n{at}\n%%EOF\n");
        Assert.False(PdfDocument.TryOpen(b.ToArray(), out _, out PdfException? err));
        Assert.IsType<PdfLimitException>(err);
    }

    [Fact]
    public void DecodedSizeLimitStopsBeforeTheOutputGrows()
    {
        byte[] zeros = new byte[8 << 20];
        var b = new PdfBuilder();
        b.BasicPages();
        b.Stream(4, "/Filter /FlateDecode", PdfBuilder.Deflate(zeros));
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R", x);
        byte[] data = b.ToArray();

        using (PdfDocument ok = PdfDocument.Open(data, new PdfOpenOptions { Limits = PdfLimits.Default with { ExpansionCheckThreshold = 1L << 30 } }))
        {
            Assert.Equal(zeros.Length, ((PdfStream)ok.GetObject(4)!).GetDecodedBytes().Length);
        }

        using PdfDocument small = PdfDocument.Open(data, new PdfOpenOptions { Limits = PdfLimits.Default with { MaxDecodedStreamBytes = 1 << 20 } });
        Assert.Throws<PdfLimitException>(() => ((PdfStream)small.GetObject(4)!).GetDecodedBytes());

        using PdfDocument ratio = PdfDocument.Open(data, new PdfOpenOptions { Limits = PdfLimits.Default with { ExpansionCheckThreshold = 1 << 20, MaxExpansionRatio = 100 } });
        Assert.Throws<PdfLimitException>(() => ((PdfStream)ratio.GetObject(4)!).GetDecodedBytes());
    }

    // ----- concurrency -----

    private static byte[] ManyObjects(int count)
    {
        var b = new PdfBuilder();
        b.BasicPages();
        for (int i = 4; i < count; i++)
        {
            b.Obj(i, $"<< /V {i} /S (value {i}) /R [{i - 1} 0 R] >>");
        }

        long x = b.XrefTable(count);
        b.Trailer($"/Size {count} /Root 1 0 R", x);
        return b.ToArray();
    }

    private static void Hammer(PdfDocument doc, int count)
    {
        int failures = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, t =>
        {
            var rnd = new Random(t);
            for (int iter = 0; iter < 3000; iter++)
            {
                int n = 4 + rnd.Next(count - 4);
                var d = doc.GetObject(n) as PdfDictionary;
                if (d is null || d.GetInt("V") != n || d.GetString("S")!.GetText() != $"value {n}")
                {
                    Interlocked.Increment(ref failures);
                }

                if ((d?.GetArray("R")?.GetRaw(0) as PdfReference)?.Number != n - 1)
                {
                    Interlocked.Increment(ref failures);
                }
            }

            Assert.Equal(1, doc.PageCount);
        });
        Assert.Equal(0, failures);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void ParallelReadersAgreeOnMemoryFileAndStreamSources()
    {
        const int Count = 400;
        byte[] data = ManyObjects(Count);
        // A tiny budget forces constant eviction and re-parsing under contention.
        var opt = new PdfOpenOptions { ObjectCacheBytes = 8 * 1024 };

        using (PdfDocument m = PdfDocument.Open(data, opt))
        {
            Hammer(m, Count);
        }

        string path = Path.Combine(Path.GetTempPath(), $"onepdf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, data);
        try
        {
            using (PdfDocument f = PdfDocument.Open(path, opt))
            {
                Hammer(f, Count);
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using PdfDocument s = PdfDocument.Open(fs, opt);
            Hammer(s, Count);
        }
        finally
        {
            File.Delete(path);
        }

        using PdfDocument roomy = PdfDocument.Open(data);
        Hammer(roomy, Count);
        Assert.Same(roomy.GetObject(10), roomy.GetObject(10));
    }

    [Fact]
    public void ParallelReadersOfCompressedObjects()
    {
        var b = new PdfBuilder("1.5");
        var sb = new StringBuilder();
        var bodies = new StringBuilder();
        int n = 200;
        for (int i = 10; i < 10 + n; i++)
        {
            sb.Append(i).Append(' ').Append(bodies.Length).Append(' ');
            bodies.Append($"<< /V {i} >>\n");
        }

        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>");
        b.Stream(3, $"/Type /ObjStm /N {n} /First {sb.Length} /Filter /FlateDecode", PdfBuilder.Deflate(Encoding.ASCII.GetBytes(sb.ToString() + bodies)));
        long xrefAt = b.Position;
        var rows = new List<(int, long, int)> { (0, 0, 65535), (1, b.Offsets[1], 0), (1, b.Offsets[2], 0), (1, b.Offsets[3], 0) };
        for (int i = 4; i < 10; i++)
        {
            rows.Add((0, 0, 0));
        }

        for (int i = 0; i < n; i++)
        {
            rows.Add((2, 3, i));
        }

        var raw = new List<byte>();
        foreach ((int t, long f2, int f3) in rows)
        {
            raw.AddRange([(byte)t, (byte)(f2 >> 24), (byte)(f2 >> 16), (byte)(f2 >> 8), (byte)f2, (byte)(f3 >> 8), (byte)f3]);
        }

        b.Stream(4, $"/Type /XRef /W [1 4 2] /Size {rows.Count} /Root 1 0 R", [.. raw]);
        b.Write($"startxref\n{xrefAt}\n%%EOF\n");

        using PdfDocument doc = PdfDocument.Open(b.ToArray(), new PdfOpenOptions { ObjectCacheBytes = 4096 });
        int failures = 0;
        Parallel.For(0, 16, _ =>
        {
            for (int i = 10; i < 10 + n; i++)
            {
                if ((doc.GetObject(i) as PdfDictionary)?.GetInt("V") != i)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });
        Assert.Equal(0, failures);
    }
}
