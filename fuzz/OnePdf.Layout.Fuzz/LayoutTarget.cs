using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OneFont;
using OnePdf;
using OnePdf.Layout.Tests;
using OnePdf.Text;

namespace OnePdf.Layout.Fuzz;

/// <summary>Builds a random element tree from the script bytes of a fuzz input, lays it out twice and checks what comes out.</summary>
internal static class LayoutTarget
{
    private const int MaxElements = 80;
    private const int MaxDepth = 6;

    // A 1 x 1 PNG.
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static readonly string[][] Words =
    [
        ["alpha", "beta", "gamma", "delta", "x", "ab cd", "wide-word-with-hyphens", "Verylongunbreakablewordthatwillnotfitanarrowcolumn"],
        ["\u0645\u0631\u062D\u0628\u0627", "\u0628\u0627\u0644\u0639\u0627\u0644\u0645", "\u0644\u0627", "\u0661\u0662\u0663"],
        ["\u05E9\u05DC\u05D5\u05DD", "\u05E2\u05D5\u05DC\u05DD", "\u05D0"],
        ["\u0928\u092E\u0938\u094D\u0924\u0947", "\u0915\u094D\u0937", "\u0939\u093F\u0928\u094D\u0926\u0940"],
        ["\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35", "\u0E01\u0E32\u0E23"],
        ["\u6F22\u5B57\u3068\u3072\u3089\u304C\u306A", "\u4E2D\u6587\u6587\u672C", "\uD55C\uAE00"],
        ["\uD83D\uDE00", "\uD83D\uDE01", "\uD83D\uDE02", "e\u0301", "\u202B", "\u202C", "\u2067x\u2069", "\u200D", "\u00AD", "\u2028"],
    ];

    private static readonly string[] Families = [null!, "Serif", "Mono", "Helvetica", "Times-Bold", "Fz", "Nope"];

    private sealed class Script(ReadOnlySpan<byte> bytes)
    {
        private readonly byte[] _bytes = bytes.ToArray();
        private int _at;

        public int Next(int max)
        {
            if (_bytes.Length == 0 || max <= 1)
            {
                return 0;
            }

            byte b = _bytes[_at];
            _at = (_at + 1) % _bytes.Length;
            return b % max;
        }

        public bool Flag() => Next(2) == 1;
    }

    private sealed class Builder(Script s, bool justify, bool hostileFont)
    {
        public int Count;
        public int Anchors;
        public readonly List<string> Tokens = [];

        /// <summary>Tokens of content that is drawn again on every page it spans (headers, repeated table rows, back layers).</summary>
        public readonly List<string> Repeated = [];

        private int _repeating;
        private int _unchecked;

        // A layer behind the primary one is cut off at the primary's height, so what is in it may not be drawn at all.
        private Element Unchecked(Func<Element> make)
        {
            _unchecked++;
            try
            {
                return make();
            }
            finally
            {
                _unchecked--;
            }
        }

        public Element Repeating(Func<Element> make)
        {
            _repeating++;
            try
            {
                return make();
            }
            finally
            {
                _repeating--;
            }
        }

        private int _tokenId;

        public Element Make(int depth)
        {
            Count++;
            bool leaf = depth >= MaxDepth || Count > MaxElements;
            int pick = s.Next(leaf ? 6 : 22);
            switch (pick)
            {
                case 0:
                case 1:
                case 2:
                case 3:
                    return NewText();
                case 4:
                    return s.Flag() ? new Spacer { Height = s.Next(40), Width = s.Flag() ? s.Next(80) : null } : new PageBreak();
                case 5:
                    return s.Flag()
                        ? new Canvas { Height = 5 + s.Next(40), Paint = c => { c.Builder.Rectangle(0, 0, c.Width, c.Height); c.Builder.Stroke(); } }
                        : new Image { Png = TinyPng, Fit = (ImageFit)s.Next(5), Dpi = 1 + s.Next(300), AltText = "pixel" };
                case 6:
                case 7:
                {
                    var b = new Box { Child = Make(depth + 1) };
                    b.Padding = Insets.All(s.Next(12));
                    b.Margin = Insets.Symmetric(s.Next(8), s.Next(8));
                    if (s.Flag())
                    {
                        b.SetBorders(new Border(0.25 + s.Next(4), new LayoutColor(0, 0, 0), (BorderStyle)s.Next(3)));
                    }

                    if (s.Flag())
                    {
                        b.Background = new LayoutColor(0.8, 0.9, 1);
                    }

                    b.CornerRadius = s.Next(6);
                    b.HorizontalAlignment = (HorizontalAlignment)s.Next(5);
                    b.VerticalAlignment = (VerticalAlignment)s.Next(3);
                    b.Opacity = 0.5 + (s.Next(6) / 10.0);
                    if (s.Next(4) == 0)
                    {
                        b.MinWidth = s.Next(120);
                    }

                    if (s.Next(4) == 0)
                    {
                        b.MaxWidth = 20 + s.Next(200);
                    }

                    if (s.Next(5) == 0)
                    {
                        b.Width = 20 + s.Next(200);
                    }

                    return b;
                }

                case 8:
                case 9:
                {
                    var c = new Column { Spacing = s.Next(10), Alignment = (HorizontalAlignment)s.Next(5) };
                    int n = 1 + s.Next(5);
                    for (int i = 0; i < n; i++)
                    {
                        c.Items.Add(Make(depth + 1));
                    }

                    return c;
                }

                case 10:
                {
                    var r = new Row { Spacing = s.Next(8), Alignment = (VerticalAlignment)s.Next(3), Compact = s.Next(4) == 0 };
                    int n = 1 + s.Next(3);
                    for (int i = 0; i < n; i++)
                    {
                        Element e = Make(depth + 1);
                        r.Items.Add(s.Next(3) switch
                        {
                            0 => new RowItem(e),
                            1 => RowItem.Constant(10 + s.Next(120), e),
                            _ => RowItem.Relative(1 + s.Next(3), e),
                        });
                    }

                    return r;
                }

                case 11:
                    return MakeTable(depth);
                case 12:
                {
                    var l = new ItemList { Kind = (ListKind)s.Next(2), NumberStyle = (NumberingStyle)s.Next(5), Start = s.Next(40) - 5, Spacing = s.Next(6), Size = 8 + s.Next(8) };
                    int n = 1 + s.Next(4);
                    for (int i = 0; i < n; i++)
                    {
                        l.Items.Add(new ListItem(Make(depth + 1)) { MarkerText = s.Next(6) == 0 ? "\u2605" : null });
                    }

                    return l;
                }

                case 13:
                {
                    var l = new Layers { PrimaryIndex = s.Next(2) };
                    for (int i = 0; i < 2; i++)
                    {
                        l.Items.Add(i == l.PrimaryIndex ? Make(depth + 1) : Unchecked(() => Make(depth + 1)));
                    }

                    return l;
                }

                case 14:
                    return new Rotate { Degrees = 90 * s.Next(4), Child = Make(depth + 1) };
                case 15:
                    return new Scale { X = 0.5 + (s.Next(8) / 4.0), Y = 0.5 + (s.Next(8) / 4.0), Child = Make(depth + 1) };
                case 16:
                    return new Translate { X = s.Next(40) - 20, Y = s.Next(40) - 20, Child = Make(depth + 1) };
                case 17:
                {
                    int a = Anchors++;
                    return new Anchor { Id = "a" + a, Child = new Bookmark { Title = "bm " + a, Level = s.Next(4), Child = Make(depth + 1) } };
                }

                case 18:
                    return new Link { Uri = s.Flag() ? "https://example.com/" + s.Next(100) : null, Destination = "a" + s.Next(Math.Max(1, Anchors + 2)), Child = Make(depth + 1) };
                case 19:
                    return new Heading { Level = s.Next(8), Child = Make(depth + 1) };
                case 20:
                    return s.Flag() ? new KeepTogether { Child = Make(depth + 1) } : new EnsureSpace { Height = s.Next(300), Child = Make(depth + 1) };
                default:
                {
                    Element e = Make(depth + 1);
                    e.KeepWithNext = s.Flag();
                    e.AltText = s.Next(5) == 0 ? "alt" : null;
                    e.Artifact = s.Next(8) == 0;
                    e.Language = s.Next(6) == 0 ? "ar" : null;
                    e.Direction = s.Next(5) == 0 ? (LayoutDirection)s.Next(2) : null;
                    return e;
                }
            }
        }

        private Table MakeTable(int depth)
        {
            var t = new Table { CellPadding = Insets.All(s.Next(6)), CellBorder = new Border(s.Next(3) * 0.5, new LayoutColor(0, 0, 0), (BorderStyle)s.Next(3)) };
            int cols = 1 + s.Next(3);
            if (s.Flag())
            {
                for (int i = 0; i < cols; i++)
                {
                    t.Columns.Add(s.Next(3) switch { 0 => TableColumn.Auto(), 1 => TableColumn.Constant(10 + s.Next(100)), _ => TableColumn.Relative(1 + s.Next(3)) });
                }
            }

            TableRow Row()
            {
                var r = new TableRow { KeepTogether = s.Next(4) == 0 };
                int n = 1 + s.Next(cols + 1);
                for (int i = 0; i < n; i++)
                {
                    r.Cells.Add(new TableCell(Make(depth + 2)) { ColumnSpan = 1 + s.Next(2), RowSpan = s.Next(5) == 0 ? 2 : 1, IsHeader = s.Next(6) == 0, VerticalAlignment = (VerticalAlignment)s.Next(3) });
                }

                return r;
            }

            int head = s.Next(2);
            for (int i = 0; i < head; i++)
            {
                t.HeaderRows.Add(RepeatingRow(Row));
            }

            int body = 1 + s.Next(5);
            for (int i = 0; i < body; i++)
            {
                t.Rows.Add(Row());
            }

            if (s.Next(3) == 0)
            {
                t.FooterRows.Add(RepeatingRow(Row));
            }

            return t;
        }

        private TableRow RepeatingRow(Func<TableRow> make)
        {
            _repeating++;
            try
            {
                return make();
            }
            finally
            {
                _repeating--;
            }
        }

        private Text NewText()
        {
            string token = "q" + _tokenId++.ToString(CultureInfo.InvariantCulture) + "z";

            var sb = new StringBuilder(token);
            int words = s.Next(14);
            string[] list = Words[s.Next(Words.Length)];
            for (int i = 0; i < words; i++)
            {
                sb.Append(' ').Append(list[s.Next(list.Length)]);
                if (s.Next(9) == 0)
                {
                    sb.Append(Words[s.Next(Words.Length)][0]);
                }
            }

            var t = new Text
            {
                Size = 6 + s.Next(14),
                Weight = s.Flag() ? 700 : 400,
                Italic = s.Next(4) == 0,
                Alignment = (HorizontalAlignment)s.Next(5),
                Justify = justify || s.Next(8) == 0,
                FirstLineIndent = s.Next(4) == 0 ? s.Next(30) : 0,
                LineSpacing = 0.8 + (s.Next(10) / 10.0),
                FontName = hostileFont && s.Next(3) == 0 ? "Fz" : Families[s.Next(Families.Length - 1)],
                Language = s.Next(8) == 0 ? "tr" : null,
            };

            // A text set in the hostile font alone has no Latin letters to draw: they come out as .notdef, which extracts as U+FFFD.
            if (_unchecked == 0 && t.FontName != "Fz")
            {
                (_repeating > 0 ? Repeated : Tokens).Add(token);
            }

            if (s.Next(3) == 0)
            {
                // Spans in different styles; the token stays whole inside the first span.
                t.Spans.Add(new TextSpan(token, null));
                string rest = sb.ToString(token.Length, sb.Length - token.Length);
                int cut = s.Next(rest.Length + 1);
                var style = new TextStyle
                {
                    Size = t.Size,
                    Underline = s.Flag(),
                    Strike = s.Next(4) == 0,
                    Position = (ScriptPosition)s.Next(3),
                    LetterSpacing = s.Next(4) == 0 ? s.Next(3) : 0,
                    Families = hostileFont ? ["Fz", "Serif"] : [],
                };
                t.Spans.Add(new TextSpan(rest[..cut], style));
                t.Spans.Add(new TextSpan(rest[cut..], null));
            }
            else
            {
                t.Content = sb.ToString();
            }

            return t;
        }
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            return;
        }

        int flags = data[0];
        int scriptLength = Math.Min(data[1] | (data[2] << 8), data.Length - 3);
        var script = new Script(data.Slice(3, scriptLength));
        ReadOnlySpan<byte> fontBytes = data[(3 + scriptLength)..];
        bool hostile = (flags & 1) != 0 && fontBytes.Length >= 12;
        if ((flags & 64) != 0)
        {
            // A colour font whose COLR and CPAL tables are the input: the first byte says where the COLR bytes end and the CPAL bytes begin
            // (empty input keeps the valid tables).
            ReadOnlySpan<byte> tables = fontBytes.Length > 1 ? fontBytes[1..] : default;
            int cut = fontBytes.Length > 1 ? fontBytes[0] * tables.Length / 256 : 0;
            byte[] colr = tables[..cut].ToArray();
            byte[] cpal = tables[cut..].ToArray();
            bool replace = tables.Length > 0;
            fontBytes = ColorFont.Build(version1: (flags & 128) != 0, edit: p =>
            {
                if (replace)
                {
                    p.Colr = colr;
                    p.Cpal = cpal;
                }
            });
            hostile = true;
        }

        var doc = new LayoutDocument { Title = "Fuzz", Language = "en" };
        doc.Options.Tagged = (flags & 4) == 0;
        doc.Options.Debug = (flags & 32) != 0;
        doc.Options.MaxPages = 60;
        if ((flags & 2) != 0)
        {
            doc.Direction = LayoutDirection.RightToLeft;
        }

        if (hostile)
        {
            var fonts = TextFontRegistry.CreateDefault();
            try
            {
                fonts.Add(fontBytes.ToArray(), "Fz");
                fonts.FallbackFamilies.Add("Fz");
            }
            catch (FontFormatException)
            {
                hostile = false;
            }

            doc.Options.Fonts = fonts;
        }

        var b = new Builder(script, (flags & 8) != 0, hostile);
        Element content = b.Make(0);
        Element header = script.Flag() ? b.Repeating(() => b.Make(MaxDepth - 2)) : new Spacer();
        var page = new LayoutPage
        {
            Size = new PageSize(200 + script.Next(200), 250 + script.Next(300)),
            Orientation = (flags & 16) != 0 ? PageOrientation.Landscape : PageOrientation.Portrait,
            Margin = Insets.All(10 + script.Next(30)),
            Content = content,
            Footer = new PageNumber { Kind = (PageNumberKind)script.Next(3), Size = 8 },
        };
        if (header is not Spacer)
        {
            page.Header = header;
        }

        doc.Pages.Add(page);
        if (script.Next(4) == 0)
        {
            doc.Pages.Add(new LayoutPage { Size = PageSize.A5, NumberStyle = (NumberingStyle)script.Next(5), LabelPrefix = "S-", Content = b.Make(MaxDepth - 1) });
        }

        var options = new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true };
        byte[] first;
        try
        {
            using var ms = new MemoryStream();
            doc.Save(ms, options);
            first = ms.ToArray();
        }
        catch (PdfException)
        {
            // PdfLayoutException (something cannot be placed, too many pages) and the other typed errors are the library's answer.
            return;
        }
        catch (FontFormatException) when (hostile)
        {
            return;
        }

        // The same tree, laid out again, gives the same bytes.
        using (var ms2 = new MemoryStream())
        {
            doc.Save(ms2, options);
            if (!first.AsSpan().SequenceEqual(ms2.ToArray()))
            {
                throw new InvalidOperationException("A second layout of the same tree gave different bytes.");
            }
        }

        using PdfDocument back = PdfDocument.Open(first);
        _ = back.StructureTreeRoot;
        var ropts = new PdfTextOptions { IncludeClippedAndOffPage = true, CollapseDuplicates = false, DetectHeadersAndFooters = false };
        var all = new StringBuilder();
        foreach (PdfPageText p in back.ExtractText(ropts))
        {
            all.Append(p.GetText(PdfTextMode.ContentOrder)).Append('\n');
        }

        // Every text leaf is placed exactly once: its token is drawn once.
        string text = all.ToString();
        if (Environment.GetEnvironmentVariable("ONEPDF_FUZZ_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllBytes(dump, first);
            var tree = new StringBuilder();
            foreach (LayoutPage lp in doc.Pages)
            {
                Describe(tree, "header", lp.Header, 0);
                Describe(tree, "content", lp.Content, 0);
            }

            tree.Append("pages: ").Append(back.Pages.Count).Append(' ').Append(string.Join(" / ", doc.Diagnostics.Select(d => d.Path + ": " + d.Message))).Append('\n');
            File.WriteAllText(dump + ".tree", tree.ToString());
            File.WriteAllText(dump + ".txt", text + "\n--- tokens: " + string.Join(" ", b.Tokens) + " | repeated: " + string.Join(" ", b.Repeated));
        }
        foreach (string token in b.Repeated)
        {
            if (Count(text, token) < 1)
            {
                throw new InvalidOperationException($"Repeated token {token} was never drawn.");
            }
        }

        foreach (string token in b.Tokens)
        {
            int count = Count(text, token);
            if (count != 1)
            {
                throw new InvalidOperationException($"Token {token} appears {count} times in the extracted text.");
            }
        }
    }

    private static void Describe(StringBuilder sb, string label, Element? e, int depth)
    {
        if (e is null)
        {
            return;
        }

        sb.Append(' ', depth * 2).Append(label).Append(": ").Append(e.GetType().Name);
        if (e is Text t)
        {
            sb.Append(" '").Append(t.Content.Length > 12 ? t.Content[..12] : t.Content).Append("' size ").Append(t.Size);
        }

        if (e is Box bx)
        {
            sb.Append(" w=").Append(bx.Width).Append(" minw=").Append(bx.MinWidth).Append(" maxw=").Append(bx.MaxWidth);
        }

        sb.Append('\n');
        switch (e)
        {
            case SingleChildElement sc:
                Describe(sb, "child", sc.Child, depth + 1);
                break;
            case Column c:
                foreach (Element i in c.Items) { Describe(sb, "item", i, depth + 1); }
                break;
            case Row r:
                foreach (RowItem i in r.Items) { Describe(sb, "cell", i.Content, depth + 1); }
                break;
            case Layers l:
                sb.Append(' ', depth * 2).Append("primary ").Append(l.PrimaryIndex).Append('\n');
                foreach (Element i in l.Items) { Describe(sb, "layer", i, depth + 1); }
                break;
            case Table tb:
                foreach (TableRow row in tb.HeaderRows.Concat(tb.Rows).Concat(tb.FooterRows)) { foreach (TableCell cell in row.Cells) { Describe(sb, "cell", cell.Content, depth + 1); } }
                break;
            case ItemList il:
                foreach (ListItem i in il.Items) { Describe(sb, "entry", i.Content, depth + 1); }
                break;
        }
    }

    private static int Count(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
