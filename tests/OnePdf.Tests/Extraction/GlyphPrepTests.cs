using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

public class GlyphPrepTests
{
    /// <summary>The straightforward duplicate search: sort by x (ties by stream position), then look ahead. The shipped one must keep exactly its result.</summary>
    private static int ReferenceCollapse(PdfTextGlyph[] g, int n)
    {
        int[] order = Enumerable.Range(0, n).ToArray();
        Array.Sort(order, (a, b) =>
        {
            int c = g[a].Matrix.E.CompareTo(g[b].Matrix.E);
            return c != 0 ? c : a.CompareTo(b);
        });
        bool[] drop = new bool[n];
        for (int p = 0; p < n; p++)
        {
            int i = order[p];
            double size = g[i].Size;
            double tol = LayoutParameters.DuplicateOffsetEm * size;
            for (int q = p + 1, steps = 0; q < n && steps < 32; q++, steps++)
            {
                int j = order[q];
                if (g[j].Matrix.E - g[i].Matrix.E > tol)
                {
                    break;
                }

                if (Math.Abs(g[j].Matrix.F - g[i].Matrix.F) > tol)
                {
                    continue;
                }

                if (g[i].Text.Length == 0 || g[i].Text != g[j].Text || g[i].IsVertical != g[j].IsVertical)
                {
                    continue;
                }

                if (Math.Abs(g[j].Size - size) > 0.05 * size)
                {
                    continue;
                }

                drop[Math.Max(i, j)] = true;
            }
        }

        int kept = 0;
        for (int i = 0; i < n; i++)
        {
            if (!drop[i])
            {
                g[kept++] = g[i];
            }
        }

        return kept;
    }

    [Theory]
    [InlineData(1, 40, 30)]
    [InlineData(2, 500, 300)]
    [InlineData(3, 3000, 20)]
    [InlineData(4, 200, 3)] // dense: many glyphs per x window, exercising the 32-step limit and large groups
    public void CollapseKeepsTheStraightforwardResult(int seed, int count, int spread)
    {
        var rnd = new Random(seed);
        string[] letters = ["a", "b", "c", "e", ""];
        PdfTextGlyph[] a = new PdfTextGlyph[count];
        for (int i = 0; i < count; i++)
        {
            // Positions on a coarse grid with sub-tolerance jitter, repeated glyphs, a few -0 and exact x ties.
            double x = rnd.Next(spread) * 2.0 + (rnd.Next(4) == 0 ? rnd.NextDouble() * 0.1 : 0);
            double y = rnd.Next(5) * 12.0 + (rnd.Next(4) == 0 ? rnd.NextDouble() * 0.1 : 0);
            if (rnd.Next(50) == 0)
            {
                x = -0.0;
            }

            a[i] = Syn.Glyph(letters[rnd.Next(letters.Length)], x, y, rnd.Next(3) == 0 ? 10.2 : 10, vertical: rnd.Next(20) == 0);
        }

        PdfTextGlyph[] b = (PdfTextGlyph[])a.Clone();
        int expected = ReferenceCollapse(a, count);
        int actual = GlyphPrep.CollapseDuplicates(b, count);
        Assert.Equal(expected, actual);
        for (int i = 0; i < expected; i++)
        {
            Assert.Equal(a[i].Matrix, b[i].Matrix);
            Assert.Equal(a[i].Text, b[i].Text);
        }
    }

    [Fact]
    public void CollapseHandlesManyGlyphsAtAlmostTheSameX()
    {
        // Hostile input: every glyph within 1e-9 of the same x. Must finish quickly and match the reference.
        PdfTextGlyph[] a = new PdfTextGlyph[20000];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = Syn.Glyph("a", 100 + ((a.Length - i) * 1e-12), i * 0.5);
        }

        PdfTextGlyph[] b = (PdfTextGlyph[])a.Clone();
        Assert.Equal(ReferenceCollapse(a, a.Length), GlyphPrep.CollapseDuplicates(b, b.Length));
    }

    [Fact]
    public void ShortcutForPlainCharactersChangesNothing()
    {
        for (char c = '\0'; c < GlyphPrep.PlainBelow; c++)
        {
            string s = c.ToString();
            Assert.Same(s, GlyphPrep.Decompose(s));
            Assert.Same(s, GlyphPrep.FoldCompatibility(s));
            Assert.Same(s, GlyphPrep.Sanitize(s));
        }
    }
}
