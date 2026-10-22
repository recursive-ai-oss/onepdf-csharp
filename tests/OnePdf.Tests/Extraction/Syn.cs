using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>Builds synthetic glyph pages: a fixed-pitch font of half an em per letter, a quarter em per space.</summary>
internal static class Syn
{
    internal const double PageW = 612, PageH = 792;

    internal static PdfTextGlyph Glyph(string t, double x, double y, double size = 10, double adv = 0.5, bool vertical = false) => new()
    {
        Text = t,
        Matrix = new PdfTextMatrix(size, 0, 0, size, x, y),
        Advance = adv,
        IsVertical = vertical,
        FontId = 1,
        FontName = "Synthetic",
        FillColor = 0,
    };

    /// <summary>A horizontal line left to right; spaces advance without a glyph, as many producers do.</summary>
    internal static List<PdfTextGlyph> Line(string text, double x, double y, double size = 10, bool spaceGlyphs = false)
    {
        List<PdfTextGlyph> r = [];
        foreach (char c in text)
        {
            if (c == ' ')
            {
                if (spaceGlyphs)
                {
                    r.Add(Glyph(" ", x, y, size, 0.25));
                }

                x += 0.25 * size;
            }
            else
            {
                r.Add(Glyph(c.ToString(), x, y, size));
                x += 0.5 * size;
            }
        }

        return r;
    }

    /// <summary>Lines of one block, each <paramref name="pitch"/> below the last.</summary>
    internal static List<PdfTextGlyph> Lines(string[] lines, double x, double y, double pitch = 14, double size = 10)
    {
        List<PdfTextGlyph> r = [];
        for (int i = 0; i < lines.Length; i++)
        {
            r.AddRange(Line(lines[i], x, y - i * pitch, size));
        }

        return r;
    }

    /// <summary>A vertical column (top to bottom), glyph origin at the top centre.</summary>
    internal static List<PdfTextGlyph> Column(string text, double xCentre, double yTop, double size = 10)
    {
        List<PdfTextGlyph> r = [];
        for (int i = 0; i < text.Length; i++)
        {
            r.Add(Glyph(text[i].ToString(), xCentre, yTop - i * size, size, 1.0, vertical: true));
        }

        return r;
    }

    internal static PdfPageGlyphs Page(IEnumerable<PdfTextGlyph> glyphs, int index = 0) => new(index, PageW, PageH, [.. glyphs]);

    internal static PdfPageText Analyze(IEnumerable<PdfTextGlyph> glyphs, PdfTextOptions? o = null) => PdfTextExtractor.Analyze(Page(glyphs), o);

    /// <summary>Mirrors a run for placing right-to-left text in visual order.</summary>
    internal static string Rev(string s)
    {
        char[] a = s.ToCharArray();
        Array.Reverse(a);
        return new string(a);
    }
}

internal sealed class FakeSource(params PdfPageGlyphs[] pages) : IGlyphSource
{
    public int PageCount => pages.Length;

    public PdfPageGlyphs GetPage(int pageIndex, CancellationToken cancellationToken) => pages[pageIndex];
}
