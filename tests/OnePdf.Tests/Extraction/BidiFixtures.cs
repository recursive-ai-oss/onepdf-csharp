using System.Text;
using OnePdf.Text;

namespace OnePdf.Tests.Extraction;

/// <summary>
/// Builds the small PDFs under <c>testdata/repros/</c> that the right-to-left, joiner and kashida tests read. Each page is hand-written: a Type 3
/// font whose glyphs are plain boxes (so no font program is needed or shipped), a <c>/ToUnicode</c> map that gives each glyph its text, and one text
/// object per glyph placed at an explicit position. The text layer is real; only the pictures are boxes.
/// Set <c>ONEPDF_REGEN_FIXTURES=1</c> to rewrite the committed files from these definitions.
/// </summary>
internal static class BidiFixtures
{
    internal const double Size = 20;

    /// <summary>One shown glyph: its Unicode text, how far the layout moves on after it and the width its font declares (both in em).</summary>
    internal sealed record Cell(string Text, double Layout = 0.5, double Width = 0.5);

    /// <summary>A line of glyphs given in logical order. <paramref name="Spans"/> wrap runs of cells (first and last index, inclusive) in <c>/ActualText</c>.</summary>
    internal sealed record Line(Cell[] Cells, int BaseDirection, double Y, (int First, int Last, string Text)[]? Spans = null);

    private static readonly Cell Space = new(" ", 0.25, 0.25);

    /// <summary>Letters one cell each, with spaces between words.</summary>
    internal static Cell[] Text(string s)
    {
        List<Cell> r = [];
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.ConvertToUtf32(s, i);
            if (cp > 0xFFFF)
            {
                i++;
            }

            r.Add(cp == ' ' ? Space : cp > 0xFFFF ? new Cell(char.ConvertFromUtf32(cp), 1, 1) : new Cell(((char)cp).ToString(), cp is '(' or ')' ? 0.33 : 0.5, cp is '(' or ')' ? 0.33 : 0.5));
        }

        return [.. r];
    }

    internal static Cell[] Join(params Cell[][] parts) => [.. parts.SelectMany(p => p)];

    internal const string Ar1 = "مرحبا", Ar2 = "بالعالم", Ar3 = "كتاب", He1 = "שלום", He2 = "עולם";

    /// <summary>Lines of text in English and Arabic or Hebrew; <paramref name="visualStream"/> picks the order the glyphs are shown in.</summary>
    internal static byte[] BidiPage(bool visualStream) => Build(visualStream,
    [
        new Line(Text("abc " + Ar1 + " " + Ar2 + " def"), 0, 360),
        new Line(Text("Hi " + He1 + " " + He2 + " bye"), 0, 315),
        new Line(Text("abc " + Ar1 + " 2026 " + Ar2 + " def"), 0, 270),
        new Line(Text("abc " + Ar1 + " " + Ar2 + " (" + Ar3 + ") def"), 0, 225),
        new Line(Text(Ar1 + " hello " + Ar2), 1, 180),
        new Line(Text(Ar1 + " (" + Ar2 + ") 2026 " + Ar3), 1, 135),
        new Line(Text(He1 + " world " + He2), 1, 90),
    ]);

    /// <summary>Arabic words with tatweel, and with a kashida that is only a stretch of the positions; every word space is a space glyph.</summary>
    internal static byte[] TatweelPage() => Build(false,
    [
        // Tatweel glyphs drawn 0.3 em wider than their declared width, as a justified line stretches them.
        new Line(Join(Text(Ar1 + " ك"), [new Cell("ـ", 0.8, 0.5), new Cell("ـ", 0.8, 0.5), new Cell("ـ", 0.8, 0.5)], Text("تاب " + Ar2)), 1, 360),
        // Position-only stretch: the two joined letters ك and ت are 0.4 em apart, nothing is drawn between them.
        new Line(Join(Text(Ar1 + " ك"), [new Cell("ت", 0.9, 0.5)], Text("اب " + Ar2)), 1, 315),
    ]);

    /// <summary>Persian words with ZWNJ in three forms: a zero-width glyph, a letter whose text ends with it, and no glyph at all (only in <c>/ActualText</c>).</summary>
    internal static byte[] ZwnjPage() => Build(false,
    [
        // می‌خواهم: the ZWNJ glyph has width 0 and leaves 0.35 em of visible gap.
        new Line(Join(Text("می"), [new Cell("\u200C", 0.35, 0)], Text("خواهم " + Ar2)), 1, 360),
        // The same word where the glyph of ی carries the ZWNJ in its ToUnicode text and the gap follows it.
        new Line(Join(Text("م"), [new Cell("ی\u200C", 0.5, 0.5), new Cell("خ", 0.85, 0.5)], Text("واهم " + Ar2)), 1, 315),
        // No ZWNJ glyph and none in ToUnicode: the word is wrapped in /ActualText, and the gap is a position only.
        new Line(Join(Text("م"), [new Cell("ی", 0.5, 0.5), new Cell("خ", 0.85, 0.5)], Text("واهم " + Ar2)), 1, 270, [(0, 6, "می\u200Cخواهم")]),
        // A ZWNJ inside an English-embedded Persian run, and کتاب‌ها (ZWNJ after ب).
        new Line(Join(Text("abc " + "کتاب"), [new Cell("\u200C", 0.35, 0)], Text("ها def")), 0, 225),
    ]);

    internal const string Family = "👨\u200D👩\u200D👧";

    /// <summary>An emoji ZWJ sequence in three forms: glyph per code point, <c>/ActualText</c> over three glyphs, one glyph with the whole sequence in its ToUnicode text.</summary>
    internal static byte[] EmojiPage() => Build(false,
    [
        new Line(Join(Text("a "), [new Cell("👨", 1, 1), new Cell("\u200D", 0, 0), new Cell("👩", 1, 1), new Cell("\u200D", 0, 0), new Cell("👧", 1, 1)], Text(" b")), 0, 360),
        new Line(Join(Text("a "), [new Cell("👨", 1, 1), new Cell("👩", 1, 1), new Cell("👧", 1, 1)], Text(" b")), 0, 315, [(2, 4, Family)]),
        new Line(Join(Text("a "), [new Cell(Family, 1, 1)], Text(" b")), 0, 270),
    ]);

    internal static IEnumerable<(string Name, byte[] Data)> All()
    {
        yield return ("rtl-bidi-logical-stream.pdf", BidiPage(false));
        yield return ("rtl-bidi-visual-stream.pdf", BidiPage(true));
        yield return ("rtl-tatweel.pdf", TatweelPage());
        yield return ("rtl-zwnj.pdf", ZwnjPage());
        yield return ("emoji-zwj.pdf", EmojiPage());
    }

    /// <summary>
    /// Lays the cells of each line out in visual order (UAX #9 on the first code point of each cell, with the removed controls kept in place) and
    /// shows them either in visual order (<paramref name="visualStream"/>) or in logical order at the same positions.
    /// </summary>
    private static byte[] Build(bool visualStream, Line[] lines)
    {
        // The font: one glyph per distinct (text, declared width).
        List<(string Text, double Width)> glyphs = [];
        int CodeOf(Cell c)
        {
            int i = glyphs.IndexOf((c.Text, c.Width));
            if (i < 0)
            {
                glyphs.Add((c.Text, c.Width));
                i = glyphs.Count - 1;
            }

            return i + 1;
        }

        StringBuilder content = new();
        foreach (Line line in lines)
        {
            int n = line.Cells.Length;
            int[] cps = new int[n];
            for (int i = 0; i < n; i++)
            {
                cps[i] = char.ConvertToUtf32(line.Cells[i].Text, 0);
            }

            byte[] levels = new byte[n];
            Bidi.ResolveLevels(cps, line.BaseDirection, levels);
            Bidi.KeepRemoved(levels, line.BaseDirection);
            int[] visual = new int[n];
            Bidi.VisualOrder(levels, visual);

            double total = line.Cells.Sum(c => c.Layout) * Size;
            double x = line.BaseDirection == 1 ? 380 - total : 20;
            double[] at = new double[n];
            foreach (int idx in visual)
            {
                at[idx] = x;
                x += line.Cells[idx].Layout * Size;
            }

            int[] order = visualStream ? visual : [.. Enumerable.Range(0, n)];
            (int First, int Last, string Text)[] spans = line.Spans ?? [];
            foreach (int idx in order)
            {
                foreach (var sp in spans)
                {
                    if (idx == EdgeOfSpan(order, sp.First, sp.Last, first: true))
                    {
                        content.Append($"/Span << /ActualText <FEFF{Utf16Hex(sp.Text)}> >> BDC\n");
                    }
                }

                content.Append(FormattableString.Invariant($"BT /F1 {Size} Tf 1 0 0 1 {at[idx]:0.###} {line.Y:0.###} Tm <{CodeOf(line.Cells[idx]):X2}> Tj ET\n"));
                foreach (var sp in spans)
                {
                    if (idx == EdgeOfSpan(order, sp.First, sp.Last, first: false))
                    {
                        content.Append("EMC\n");
                    }
                }
            }
        }

        // Font objects.
        StringBuilder diffs = new(), widths = new(), cmap = new();
        for (int i = 0; i < glyphs.Count; i++)
        {
            int code = i + 1;
            diffs.Append($"/g{code} ");
            widths.Append(FormattableString.Invariant($"{glyphs[i].Width * 1000:0} "));
            cmap.Append($"<{code:X2}> <{Utf16Hex(glyphs[i].Text)}>\n");
        }

        PdfBuilder b = new();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        b.Stream(4, "", Encoding.Latin1.GetBytes(content.ToString()));
        StringBuilder charProcs = new();
        for (int i = 0; i < glyphs.Count; i++)
        {
            charProcs.Append($"/g{i + 1} {7 + i} 0 R ");
        }

        b.Obj(5, $"<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 800] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << {charProcs}>> " +
            $"/Encoding << /Type /Encoding /Differences [1 {diffs}] >> /FirstChar 1 /LastChar {glyphs.Count} /Widths [{widths}] /ToUnicode 6 0 R >>");
        string cm = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Adobe-Identity-UCS def /CMapType 2 def\n" +
            $"1 begincodespacerange <00> <FF> endcodespacerange\n{glyphs.Count} beginbfchar\n{cmap}endbfchar\nendcmap CMapName currentdict /CMap defineresource pop end end\n";
        b.Stream(6, "", Encoding.Latin1.GetBytes(cm));
        for (int i = 0; i < glyphs.Count; i++)
        {
            int w = (int)Math.Round(glyphs[i].Width * 1000);
            string proc = w == 0 ? "0 0 d0\n" : $"{w} 0 d0\n40 0 {Math.Max(w - 80, 10)} 700 re f\n";
            b.Stream(7 + i, "", Encoding.Latin1.GetBytes(proc));
        }

        int size = 7 + glyphs.Count;
        long xref = b.XrefTable(size);
        b.Trailer($"/Size {size} /Root 1 0 R", xref);
        return b.ToArray();
    }

    /// <summary>The first or last cell of the span in the order the cells are shown.</summary>
    private static int EdgeOfSpan(int[] order, int from, int to, bool first)
    {
        int found = -1;
        foreach (int o in order)
        {
            if (o >= from && o <= to)
            {
                found = o;
                if (first)
                {
                    break;
                }
            }
        }

        return found;
    }

    private static string Utf16Hex(string s)
    {
        StringBuilder sb = new();
        foreach (char c in s)
        {
            sb.Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
