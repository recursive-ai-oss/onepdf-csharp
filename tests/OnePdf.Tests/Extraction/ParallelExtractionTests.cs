using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>Whole-document extraction at several degrees of parallelism: same result, cancellation, failures.</summary>
public class ParallelExtractionTests
{
    private sealed class HookSource(PdfPageGlyphs[] pages, Action<int>? onGetPage = null) : IGlyphSource
    {
        public int PageCount => pages.Length;

        public PdfPageGlyphs GetPage(int pageIndex, CancellationToken cancellationToken)
        {
            onGetPage?.Invoke(pageIndex);
            cancellationToken.ThrowIfCancellationRequested();
            return pages[pageIndex];
        }
    }

    private static PdfPageGlyphs[] Document(int pages)
    {
        PdfPageGlyphs[] doc = new PdfPageGlyphs[pages];
        for (int i = 0; i < pages; i++)
        {
            List<PdfTextGlyph> g = [];
            g.AddRange(Syn.Line("Quarterly Harbor Report", 50, 760));
            g.AddRange(i % 3 == 0 ? GoldenTests.MixedPage(i) : GoldenTests.TwoColumnPage(8 + i % 17, i));
            g.AddRange(Syn.Line($"Page {i + 1}", 280, 30));
            doc[i] = Syn.Page(g, i);
        }

        return doc;
    }

    private static string Fingerprint(IReadOnlyList<PdfPageText> pages)
    {
        // The JSON carries blocks, lines, words, roles, glyphs and the reading-order text; the other modes are added.
        string json = PdfExtractionJson.ToJson(pages);
        return json + string.Concat(pages.Select(p => p.GetText(PdfTextMode.Layout) + "\u0001" + p.GetText(PdfTextMode.ContentOrder)));
    }

    private static IReadOnlyList<PdfPageText> Extract(PdfPageGlyphs[] doc, int degree) =>
        new PdfTextExtractor(new HookSource(doc), null, new PdfTextOptions { MaxDegreeOfParallelism = degree }).ExtractDocument();

    [Fact]
    public void ResultsAreIdenticalAtEveryDegree()
    {
        PdfPageGlyphs[] doc = Document(60);
        string expected = Fingerprint(Extract(doc, 1));
        foreach (int degree in new[] { 4, 16, 16, 4 })
        {
            Assert.Equal(expected, Fingerprint(Extract(doc, degree)));
        }

        // The default degree (0) is min(4, cores) and gives the same too.
        Assert.Equal(expected, Fingerprint(Extract(doc, 0)));
    }

    [Fact]
    public void ManyExtractorsRunAtOnceOnSharedInput()
    {
        // Independent whole-document calls over the same page objects, each parallel inside: nothing is shared but the buffer pool.
        PdfPageGlyphs[] doc = Document(24);
        string expected = Fingerprint(Extract(doc, 1));
        string[] results = new string[6];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 6 }, i => results[i] = Fingerprint(Extract(doc, 1 + i % 4)));
        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void CancellationInTheMiddleOfARunIsHonoured(int degree)
    {
        PdfPageGlyphs[] doc = Document(120);
        using CancellationTokenSource cts = new();
        int served = 0;
        HookSource src = new(doc, _ =>
        {
            if (Interlocked.Increment(ref served) == 20)
            {
                cts.Cancel();
            }
        });
        PdfTextExtractor ex = new(src, null, new PdfTextOptions { MaxDegreeOfParallelism = degree });
        Assert.Throws<OperationCanceledException>(() => ex.ExtractDocument(cts.Token));
        // Work stops soon after the request: far fewer pages than the document has were taken.
        Assert.True(Volatile.Read(ref served) < doc.Length, $"served {served}");
    }

    [Fact]
    public void CancellationInsideLayoutOfALargePageIsHonoured()
    {
        using CancellationTokenSource cts = new();
        PdfPageGlyphs[] doc = Document(8);
        PdfTextExtractor ex = new(new HookSource(doc, i =>
        {
            if (i == 3)
            {
                cts.Cancel();
            }
        }), null, new PdfTextOptions { MaxDegreeOfParallelism = 1 });
        Assert.Throws<OperationCanceledException>(() => ex.ExtractDocument(cts.Token));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void AnExceptionFromTheSourceSurfacesAsItself(int degree)
    {
        PdfPageGlyphs[] doc = Document(60);
        PdfTextExtractor ex = new(new HookSource(doc, i =>
        {
            if (i == 17)
            {
                throw new InvalidOperationException("page 17 failed");
            }
        }), null, new PdfTextOptions { MaxDegreeOfParallelism = degree });
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => ex.ExtractDocument());
        Assert.Equal("page 17 failed", e.Message);

        // The extractor is still usable, and a failing page does not poison the pool for the next call.
        PdfTextExtractor ok = new(new HookSource(doc), null, new PdfTextOptions { MaxDegreeOfParallelism = degree });
        Assert.Equal(doc.Length, ok.ExtractDocument().Count);
    }

    [Fact]
    public void SinglePagesFromManyThreadsMatchTheDocumentRun()
    {
        PdfPageGlyphs[] doc = Document(30);
        IReadOnlyList<PdfPageText> whole = Extract(doc, 1);
        PdfTextExtractor ex = new(new HookSource(doc), null, new PdfTextOptions { DetectHeadersAndFooters = false });
        PdfPageText[] each = new PdfPageText[doc.Length];
        Parallel.For(0, doc.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => each[i] = ex.ExtractPage(i));
        for (int i = 0; i < doc.Length; i++)
        {
            Assert.Equal(whole[i].GetText(PdfTextMode.ReadingOrder), each[i].GetText(PdfTextMode.ReadingOrder));
            Assert.Equal(whole[i].Glyphs.Count, each[i].Glyphs.Count);
        }
    }
}
