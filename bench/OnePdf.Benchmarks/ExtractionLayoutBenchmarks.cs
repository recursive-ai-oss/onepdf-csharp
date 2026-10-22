using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using BenchmarkDotNet.Attributes;
using OnePdf.Text;

namespace OnePdf.Benchmarks;

/// <summary>Feeds prepared glyph pages to the extractor, so the numbers are the layout and not a producer.</summary>
internal sealed class PreparedGlyphs(PdfPageGlyphs[] pages) : IGlyphSource
{
    public int PageCount => pages.Length;

    public PdfPageGlyphs GetPage(int pageIndex, CancellationToken cancellationToken) => pages[pageIndex];
}

/// <summary>
/// The layout pipeline on synthetic two-column pages of about 3,400 glyphs: one page, a 300-page document through
/// <c>ExtractDocument</c> at degree 1 and at the default degree, and the JSON writer.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ExtractionLayoutBenchmarks
{
    private static readonly string[] Words = ["harbor", "lemons", "ferry", "the", "of", "and", "market", "morning", "tower", "bell", "office", "final"];

    private PdfPageGlyphs _page = null!;
    private PdfPageGlyphs[] _doc = null!;
    private PdfPageText[] _analyzed = null!;

    internal static PdfPageGlyphs MakePage(int index, int seed)
    {
        Random rng = new(seed);
        List<PdfTextGlyph> g = new(3600);
        void Line(string text, double x, double y, double size)
        {
            foreach (char c in text)
            {
                if (c != ' ')
                {
                    g.Add(new PdfTextGlyph
                    {
                        Text = c.ToString(),
                        Matrix = new PdfTextMatrix(size, 0, 0, size, x, y),
                        Advance = 0.5,
                        FontId = 1,
                        FontName = "Synthetic",
                        FillColor = 0,
                    });
                }

                x += (c == ' ' ? 0.25 : 0.5) * size;
            }
        }

        Line("An Article About a Harbor Town", 150, 750, 16);
        double[] xs = [50, 320];
        for (int i = 0; i < 53; i++)
        {
            for (int c = 0; c < 2; c++)
            {
                StringBuilder sb = new();
                while (sb.Length < 38)
                {
                    sb.Append(Words[rng.Next(Words.Length)]).Append(' ');
                }

                Line(sb.ToString().TrimEnd(), xs[c], 720 - i * 12, 9);
            }
        }

        Line("Page " + (index + 1), 290, 30, 9);
        return new PdfPageGlyphs(index, 612, 792, g);
    }

    [GlobalSetup]
    public void Setup()
    {
        _page = MakePage(0, 1);
        _doc = new PdfPageGlyphs[300];
        for (int i = 0; i < _doc.Length; i++)
        {
            _doc[i] = MakePage(i, i + 1);
        }

        _analyzed = new PdfPageText[20];
        for (int i = 0; i < _analyzed.Length; i++)
        {
            _analyzed[i] = PdfTextExtractor.Analyze(_doc[i]);
        }
    }

    [Benchmark]
    public int LayoutPage() => PdfTextExtractor.Analyze(_page).Blocks.Count;

    [Benchmark]
    public int LayoutPageAndText() => PdfTextExtractor.Analyze(_page).Text.Length;

    [Benchmark]
    public int Document300Degree1() => Document(1);

    [Benchmark]
    public int Document300DefaultDegree() => Document(0);

    private int Document(int degree)
    {
        PdfTextExtractor ex = new(new PreparedGlyphs(_doc), null, new PdfTextOptions { MaxDegreeOfParallelism = degree });
        int n = 0;
        foreach (PdfPageText p in ex.ExtractDocument())
        {
            n += p.Blocks.Count;
        }

        return n;
    }

    [Benchmark]
    public int Json20Pages() => PdfExtractionJson.ToJson(_analyzed).Length;
}
