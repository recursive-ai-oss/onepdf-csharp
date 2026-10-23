using System.Text.RegularExpressions;
using OnePdf.Text;
using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Pages;

/// <summary>
/// Every committed test file goes through the page operations: merged pairwise, merged together with all the generated files,
/// split into single pages and put back together. What comes out must read page for page like what went in.
/// </summary>
public class CorpusPageOperationTests
{
    // Pages compared per source: the first few and the last. The page counts are compared for all.
    private const int Head = 3;

    private static readonly object ReportLock = new();

    private static PdfDocument? TryOpen(string file)
    {
        try
        {
            return PdfDocument.Open(file, new PdfOpenOptions { Password = WriterSupport.PasswordFor(file) });
        }
        catch (PdfException)
        {
            return null;
        }
    }

    private static List<string> Files()
    {
        var list = new List<string>();
        foreach (string f in WriterSupport.AllPdfs())
        {
            using PdfDocument? d = TryOpen(f);
            if (d is not null && SafePageCount(d) > 0)
            {
                list.Add(f);
            }
        }

        return list;
    }

    private static int SafePageCount(PdfDocument d)
    {
        try
        {
            return d.PageCount;
        }
        catch (PdfException)
        {
            return 0;
        }
    }

    private static string Rel(string file) => Path.GetRelativePath(WriterSupport.Root(), file).Replace('\\', '/');

    public static IEnumerable<object[]> Pairs()
    {
        List<string> files = Files();
        for (int i = 0; i < files.Count; i += 2)
        {
            yield return [Rel(files[i]), Rel(files[(i + 1) % files.Count])];
        }
    }

    public static IEnumerable<object[]> Singles() => Files().Select(f => new object[] { Rel(f) });

    /// <summary>One page's text (reading order) and display list, with the numbers that depend on object numbering taken out.</summary>
    internal static string PageSnapshot(PdfDocument doc, int index)
    {
        PdfPage page = doc.Pages[index];
        string text = page.ExtractText().GetText(PdfTextMode.ReadingOrder);
        var list = new PdfDisplayList();
        page.Interpret(list);
        string display = Regex.Replace(list.ToText(), @"\d+ \d+ R|fnv=[0-9a-f]+|(?<=/Length )\d+", "#");
        return text + "\n" + display;
    }

    private static IEnumerable<int> Sampled(int count)
    {
        for (int i = 0; i < Math.Min(Head, count); i++)
        {
            yield return i;
        }

        if (count > Head)
        {
            yield return count - 1;
        }
    }

    private static void AssertPagesEqual(PdfDocument source, int sourceCount, PdfDocument result, int offset, string what)
    {
        foreach (int i in Sampled(sourceCount))
        {
            string expected = PageSnapshot(source, i);
            string actual = PageSnapshot(result, offset + i);
            if (expected != actual)
            {
                int at = 0;
                while (at < expected.Length && at < actual.Length && expected[at] == actual[at])
                {
                    at++;
                }

                int from = Math.Max(0, at - 80);
                Assert.Fail($"{what}: page {i} differs from offset {at}\n  source: {expected.AsSpan(from, Math.Min(240, expected.Length - from)).ToString()}\n  result: {actual.AsSpan(from, Math.Min(240, actual.Length - from)).ToString()}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void MergedPairsReadPageForPage(string first, string second)
    {
        string fa = Path.Combine(WriterSupport.Root(), first);
        string fb = Path.Combine(WriterSupport.Root(), second);
        using PdfDocument? a = TryOpen(fa);
        using PdfDocument? b = TryOpen(fb);
        if (a is null || b is null)
        {
            return;
        }

        int na = a.PageCount;
        int nb = b.PageCount;
        using PdfDocument merged = PdfDocument.Merge([a, b]);
        Assert.Equal(na + nb, merged.PageCount);
        byte[] bytes = WriterSupport.Save(merged, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
        using PdfDocument again = PdfDocument.Open(bytes);
        Assert.Equal(na + nb, again.PageCount);
        AssertPagesEqual(a, na, again, 0, first + " in " + first + "+" + second);
        AssertPagesEqual(b, nb, again, na, second + " in " + first + "+" + second);

        // qpdf finds no more wrong with the result than with the worse of the inputs; a second reader opens it.
        byte[] ba = File.ReadAllBytes(fa);
        byte[] bb = File.ReadAllBytes(fb);
        int inputs = Math.Max(WriterSupport.QpdfRun(ba, WriterSupport.PasswordFor(fa)).Code, WriterSupport.QpdfRun(bb, WriterSupport.PasswordFor(fb)).Code);
        (int code, string text) = WriterSupport.QpdfRun(bytes);
        Assert.True(code <= inputs, $"{first} + {second}: qpdf exit {code} (inputs {inputs}): {text}");
        if (CorpusRoundTripTests.PigOpens(ba, WriterSupport.PasswordFor(fa)) && CorpusRoundTripTests.PigOpens(bb, WriterSupport.PasswordFor(fb)))
        {
            Assert.True(CorpusRoundTripTests.PigOpens(bytes, null), $"{first} + {second}: PdfPig opened the inputs but not the merge");
        }
    }

    [Fact]
    public void AllGeneratedFilesMergeIntoOneDocument()
    {
        string dir = Path.GetDirectoryName(WriterSupport.Generated("x.pdf"))!;
        var docs = new List<PdfDocument>();
        var names = new List<string>();
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.pdf").Order(StringComparer.Ordinal))
            {
                if (TryOpen(f) is { } d)
                {
                    names.Add(Path.GetFileName(f));
                    docs.Add(d);
                }
            }

            Assert.True(docs.Count > 20);
            using PdfDocument merged = PdfDocument.Merge(docs);
            int total = docs.Sum(static d => d.PageCount);
            Assert.Equal(total, merged.PageCount);
            byte[] bytes = WriterSupport.Save(merged, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
            using PdfDocument again = PdfDocument.Open(bytes);
            Assert.Equal(total, again.PageCount);
            int offset = 0;
            for (int n = 0; n < docs.Count; n++)
            {
                PdfDocument d = docs[n];
                AssertPagesEqual(d, d.PageCount, again, offset, "generated set, file " + names[n]);
                offset += d.PageCount;
            }

            (int code, string text) = WriterSupport.QpdfRun(bytes);
            Assert.True(code <= 3, text);
            Assert.True(CorpusRoundTripTests.PigOpens(bytes, null));
            TestContext.Current.SendDiagnosticMessage($"{docs.Count} generated files, {total} pages, {bytes.Length} bytes, qpdf exit {code}");
        }
        finally
        {
            foreach (PdfDocument d in docs)
            {
                d.Dispose();
            }
        }
    }

    [Theory]
    [MemberData(nameof(Singles))]
    public void SplitIntoPagesAndMergedBackReadTheSame(string relative)
    {
        string file = Path.Combine(WriterSupport.Root(), relative);
        using PdfDocument? source = TryOpen(file);
        if (source is null)
        {
            return;
        }

        int n = source.PageCount;
        int per = Math.Max(1, (n + 29) / 30); // single pages up to 30 pages; longer files in 30 parts
        IReadOnlyList<PdfDocument> parts = PdfDocument.Split(source, per);
        try
        {
            byte[] whole = WriterSupport.Save(source, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
            long partBytes = 0;
            long maxPart = 0;
            foreach (PdfDocument part in parts)
            {
                byte[] bytes = WriterSupport.Save(part, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
                partBytes += bytes.Length;
                maxPart = Math.Max(maxPart, bytes.Length);
            }

            // A part holds what its pages reach: none is as big as the whole file saved the same way (unless there is one part).
            if (parts.Count > 1)
            {
                Assert.True(maxPart <= whole.Length, $"{relative}: a part of {maxPart} bytes from a file saved at {whole.Length}");
            }

            using PdfDocument back = PdfDocument.Merge(parts);
            Assert.Equal(n, back.PageCount);
            byte[] backBytes = WriterSupport.Save(back, new PdfSaveOptions { XrefFormat = PdfXrefFormat.Stream });
            using PdfDocument again = PdfDocument.Open(backBytes);
            Assert.Equal(n, again.PageCount);
            AssertPagesEqual(source, n, again, 0, relative + " split and merged");
            string line = $"{relative}\t{n}\t{whole.Length}\t{parts.Count}\t{partBytes}\t{maxPart}\t{backBytes.Length}";
            TestContext.Current.SendDiagnosticMessage(line);
            if (Environment.GetEnvironmentVariable("ONEPDF_PAGEOPS_REPORT") is { Length: > 0 } report)
            {
                lock (ReportLock)
                {
                    File.AppendAllText(report, line + "\n");
                }
            }
        }
        finally
        {
            foreach (PdfDocument d in parts)
            {
                d.Dispose();
            }
        }
    }
}
