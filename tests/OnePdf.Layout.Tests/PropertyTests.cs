namespace OnePdf.Layout.Tests;

/// <summary>Builds random but valid element trees that always fit a page, and remembers what the layout must preserve.</summary>
internal sealed partial class TreeGen
{
    private readonly Random _rng;
    private int _id;

    public TreeGen(int seed) => _rng = new Random(seed);

    /// <summary>Atoms (names starting A, K or P) and their heights: each must be placed exactly once.</summary>
    public Dictionary<string, double> Atoms { get; } = [];

    public List<(string[] Names, double Total)> KeepGroups { get; } = [];

    public List<(string A, string B, double Needed)> KeepWithNextPairs { get; } = [];

    public List<Text> Texts { get; } = [];

    private double Next(double lo, double hi) => lo + (_rng.NextDouble() * (hi - lo));

    private int Pick(int n) => _rng.Next(n);

    private string Atom(string prefix, double h)
    {
        string name = prefix + _id++;
        Atoms[name] = h;
        return name;
    }

    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "extraordinarily", "ok", "a"];

    public Column Root(double width)
    {
        var col = new Column { Name = "Root", Spacing = Pick(3) * 4 };
        int n = 3 + Pick(10);
        for (int i = 0; i < n; i++)
        {
            if (Pick(8) == 0)
            {
                double ha = Next(10, 60);
                double hb = Next(10, 60);
                string a = Atom("P", ha);
                string b = Atom("P", hb);
                col.Items.Add(new Spacer { Height = ha, Name = a, KeepWithNext = true });
                col.Items.Add(new Spacer { Height = hb, Name = b });
                KeepWithNextPairs.Add((a, b, ha + col.Spacing + hb));
                continue;
            }

            col.Items.Add(Node(0, width));
        }

        return col;
    }

    private Element Node(int depth, double w)
    {
        int kind = depth >= 3 ? Pick(4) : Pick(17);
        switch (kind)
        {
            case 0:
            {
                double h = Next(5, 120);
                return new Spacer { Height = h, Name = Atom("A", h) };
            }

            case 1:
            {
                double h = Next(5, 80);
                return new Canvas { Height = h, Name = Atom("A", h), Paint = s => { s.Builder.Rectangle(0, 0, s.Width, s.Height); s.Builder.Stroke(); } };
            }

            case 2:
            case 3:
            {
                var t = new Text { Content = Sentence(5 + Pick(120)), Size = 8 + Pick(5), Name = "T" + _id++ };
                Texts.Add(t);
                return t;
            }

            case 4:
            case 5:
            {
                var col = new Column { Spacing = Pick(3) * 3, Alignment = (HorizontalAlignment)Pick(3) };
                int n = 1 + Pick(5);
                for (int i = 0; i < n; i++)
                {
                    col.Items.Add(Pick(12) == 0 ? new PageBreak() : Node(depth + 1, w));
                }

                return col;
            }

            case 6:
            case 7:
            {
                if (w < 120)
                {
                    goto case 4;
                }

                var row = new Row { Spacing = Pick(3) * 3, Alignment = (VerticalAlignment)Pick(3) };
                int n = 2 + Pick(2);
                // A constant cell takes at most half of the free width between them; a relative cell gets at least a sixth of its share.
                double free = w - ((n - 1) * 6);
                for (int i = 0; i < n; i++)
                {
                    row.Items.Add(Pick(3) == 0
                        ? RowItem.Constant(free / (2 * n), Node(depth + 1, free / (2 * n)))
                        : RowItem.Relative(1 + Pick(3), Node(depth + 1, free / (6 * n))));
                }

                return row;
            }

            case 8:
            case 9:
            {
                double pad = Pick(9);
                double mar = Pick(6);
                double bw = Pick(3);
                var box = new Box
                {
                    Padding = Insets.All(pad),
                    Margin = Insets.Symmetric(mar, mar / 2),
                    CornerRadius = Pick(2) * 6,
                    Background = Pick(2) == 0 ? null : new LayoutColor(Next(0, 1), Next(0, 1), Next(0, 1)),
                    Opacity = Pick(4) == 0 ? 0.5 : 1,
                    HorizontalAlignment = (HorizontalAlignment)Pick(3),
                };
                box.SetBorders(new Border(bw, LayoutColor.Black, (BorderStyle)Pick(3)));
                double inner = Math.Max(20, w - (2 * (pad + bw + mar)));
                if (Pick(4) == 0)
                {
                    double ch = Next(5, 60);
                    box.Child = new Spacer { Height = ch, Name = Atom("A", ch) };
                    box.Height = ch + (2 * (pad + bw)) + Pick(10);
                    box.VerticalAlignment = (VerticalAlignment)Pick(3);
                }
                else
                {
                    box.Child = Node(depth + 1, inner);
                }

                return box;
            }

            case 10:
            {
                var g = new Column();
                int n = 2 + Pick(3);
                var names = new string[n];
                double total = 0;
                for (int i = 0; i < n; i++)
                {
                    double h = Next(10, 80);
                    string name = Atom("K", h);
                    names[i] = name;
                    total += h;
                    g.Items.Add(new Spacer { Height = h, Name = name });
                }

                KeepGroups.Add((names, total));
                return new KeepTogether { Child = g };
            }

            case 11:
            {
                double h = Next(5, 60);
                return new EnsureSpace { Height = Next(20, 120), Child = new Spacer { Height = h, Name = Atom("A", h) } };
            }

            case 12:
            {
                double h = Next(10, 80);
                var layers = new Layers { PrimaryIndex = 1 };
                layers.Items.Add(new Box { ExpandHeight = true, Background = new LayoutColor(0.9, 0.9, 0.5), Name = "Back" + _id++ });
                layers.Items.Add(new Spacer { Height = h, Name = Atom("A", h) });
                return layers;
            }

            case 14:
            case 15:
                return w >= 160 ? TableNode(w) : Node(depth + 1, w);

            case 16:
                return ListNode(depth, w);

            default:
            {
                double h = Next(5, 40);
                if (Pick(2) == 0 && w >= 200)
                {
                    return new Rotate { Degrees = Pick(4) * 90 + (Pick(3) == 0 ? 17 : 0), Child = new Spacer { Width = Next(10, 60), Height = h, Name = Atom("A", h) } };
                }

                return new Scale { X = Next(0.5, 2), Y = Next(0.5, 2), Child = new Spacer { Height = h, Name = Atom("A", h) } };
            }
        }
    }

    private string Sentence(int words)
    {
        var parts = new string[words];
        for (int i = 0; i < words; i++)
        {
            parts[i] = Words[Pick(Words.Length)];
        }

        return string.Join(" ", parts);
    }
}

public class PropertyTests
{
    private const double BodyW = 260;
    private const double BodyH = 360;
    private const double Tol = 1e-6;

    public static int Seeds => 2000;

    internal static LayoutDocument Build(int seed, out TreeGen gen)
    {
        gen = new TreeGen(seed);
        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage
        {
            Size = new PageSize(300, 400),
            Margin = Insets.All(20),
            Content = gen.Root(BodyW),
            Direction = seed % 5 == 0 ? LayoutDirection.RightToLeft : LayoutDirection.LeftToRight,
        });
        return doc;
    }

    [Fact]
    public void GeneratedTreesKeepTheLayoutInvariants()
    {
        int trees = 0;
        int pagesTotal = 0;
        int nodes = 0;
        int tables = 0;
        int pieces = 0;
        int lists = 0;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            LayoutDocument doc = Build(seed, out TreeGen gen);
            var bodies = new List<Placed>();
            using (PdfDocument target = PdfDocument.CreateEmpty())
            {
                try
                {
                    doc.RenderCore(target, (_, _, body, _, _) => bodies.Add(body!));
                }
                catch (PdfLayoutException ex)
                {
                    Assert.Fail($"seed {seed}: {ex.Message}");
                }
            }

            string who = $"seed {seed}";
            var pageOf = new Dictionary<string, int>();
            var count = new Dictionary<string, int>();
            for (int p = 0; p < bodies.Count; p++)
            {
                Placed body = bodies[p];
                Assert.True(body.Height <= BodyH + Tol, $"{who} page {p}: body height {body.Height} {Dump(body, 0)}");
                Assert.True(body.Width <= BodyW + Tol, $"{who} page {p}: body width {body.Width}");
                foreach (Placed n in Support.Walk(body))
                {
                    nodes++;
                    CheckNode(n, who, p);
                    if (n.Element.Name is { } name && Starts(name) && n.Element is not Box)
                    {
                        pageOf[name] = p;
                        count[name] = count.GetValueOrDefault(name) + 1;
                    }
                }
            }

            bool rtl = doc.Pages[0].Direction == LayoutDirection.RightToLeft;
            (int tc, int pc, int l) = StructureChecks.Check(gen, bodies, rtl, BodyH, who);
            tables += tc;
            pieces += pc;
            lists += l;
            StructureChecks.CheckWidowsAndOrphans(gen, bodies, doc.Diagnostics.Any(d => d.Message.Contains("widow", StringComparison.Ordinal)), who);

            // Every atom is placed exactly once.
            foreach (string atom in gen.Atoms.Keys)
            {
                Assert.True(count.GetValueOrDefault(atom) == 1, $"{who}: atom {atom} placed {count.GetValueOrDefault(atom)} times");
            }

            // Text is split into contiguous, complete line ranges in page order.
            foreach (Text t in gen.Texts)
            {
                int next = 0;
                int total = -1;
                foreach (Placed n in bodies.SelectMany(Support.Walk).Where(n => ReferenceEquals(n.Element, t)))
                {
                    var f = (Text.Fragment)n.Data!;
                    Assert.True(f.First == next, $"{who}: text {t.Name} fragment starts at {f.First}, expected {next}");
                    Assert.True(f.Count > 0, $"{who}: empty text fragment");
                    next += f.Count;
                    total = f.Block.LineCount;
                }

                Assert.True(next == total, $"{who}: text {t.Name} placed {next} of {total} lines");
            }

            // Keep-together groups sit on one page unless taller than the page.
            foreach ((string[] names, double total) in gen.KeepGroups)
            {
                if (total <= BodyH)
                {
                    Assert.True(names.Select(nm => pageOf[nm]).Distinct().Count() == 1, $"{who}: keep-together group {names[0]} split");
                }
            }

            // A keep-with-next head shares a page with the item after it unless the two cannot fit together.
            foreach ((string a, string b, double needed) in gen.KeepWithNextPairs)
            {
                if (needed <= BodyH)
                {
                    Assert.True(pageOf[a] == pageOf[b], $"{who}: {a} and {b} are on pages {pageOf[a]} and {pageOf[b]}");
                }
            }

            trees++;
            pagesTotal += bodies.Count;
        }

        TestContext.Current.SendDiagnosticMessage($"trees={trees} pages={pagesTotal} nodes={nodes} tables={tables} table pieces={pieces} lists={lists}");
        Assert.True(tables > 500 && pieces > tables && lists > 300, $"tables={tables} pieces={pieces} lists={lists}");
        Assert.Equal(Seeds, trees);
        Assert.True(pagesTotal > trees, "generated trees should span pages");
        Assert.True(nodes > 10_000);
    }

    private static string Dump(Placed n, int depth) =>
        depth > 6 ? "" : $"\n{new string(' ', depth * 2)}{n.Element.Label} y={n.Y:0.##} h={n.Height:0.##}" + string.Concat(n.Children.Select(c => Dump(c, depth + 1)));

    private static bool Starts(string name) => name.Length > 1 && (name[0] == 'A' || name[0] == 'K' || name[0] == 'P') && char.IsDigit(name[1]);

    private static void CheckNode(Placed n, string who, int page)
    {
        Assert.True(double.IsFinite(n.X + n.Y + n.Width + n.Height), $"{who}: non-finite box in {n.Element.Label}");
        Assert.True(n.Width >= -Tol && n.Height >= -Tol, $"{who}: negative size in {n.Element.Label}");
        bool framed = n.Element is Rotate or Scale or Translate;
        foreach (Placed c in n.Children)
        {
            if (framed)
            {
                continue;
            }

            Assert.True(
                c.X >= -Tol && c.Y >= -Tol && c.X + c.Width <= n.Width + Tol && c.Y + c.Height <= n.Height + Tol,
                $"{who} page {page}: {c.Element.Label} ({c.X},{c.Y},{c.Width},{c.Height}) leaves {n.Element.Label} ({n.Width} x {n.Height})");
        }

        if (n.Element is Layers or Translate || framed)
        {
            return;
        }

        for (int i = 0; i < n.Children.Count; i++)
        {
            for (int j = i + 1; j < n.Children.Count; j++)
            {
                Placed a = n.Children[i];
                Placed b = n.Children[j];
                double ox = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
                double oy = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
                Assert.False(ox > 1e-4 && oy > 1e-4, $"{who} page {page}: siblings {a.Element.Label} and {b.Element.Label} overlap in {n.Element.Label}");
            }
        }
    }

    [Fact]
    public void SameTreeGivesIdenticalBytesAndOpens()
    {
        for (int seed = 1; seed <= 100; seed++)
        {
            byte[] one = Support.Save(Build(seed, out _));
            byte[] two = Support.Save(Build(seed, out _));
            Assert.True(one.AsSpan().SequenceEqual(two), $"seed {seed}: two runs differ");
            using PdfDocument back = PdfDocument.Open(one);
            Assert.True(back.Pages.Count >= 1);
            using PdfDocument again = PdfDocument.Open(one);
            Assert.Equal(back.Pages.Count, again.Pages.Count);
        }
    }

    [Fact]
    public void ReusedTreeLaysOutTheSameTwice()
    {
        // Elements hold no layout state: laying the same objects out again gives the same bytes.
        LayoutDocument doc = Build(7, out _);
        byte[] one = Support.Save(doc);
        byte[] two = Support.Save(doc);
        Assert.True(one.AsSpan().SequenceEqual(two));
    }
}
