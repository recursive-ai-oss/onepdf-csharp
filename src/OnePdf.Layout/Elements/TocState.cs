using System.Globalization;

namespace OnePdf.Layout;

/// <summary>One line of the table of contents as a layout run found it.</summary>
/// <param name="Title">The entry's text.</param>
/// <param name="Level">Heading level (1 to 6) or explicit entry level (1 to 9).</param>
/// <param name="Page">Zero-based index of the target's page in the document.</param>
/// <param name="Label">The page label of that page: the number as the section writes it, with its prefix.</param>
internal readonly record struct TocItem(string Title, int Level, int Page, string Label);

/// <summary>
/// What the runs of a document with a table of contents hand to each other: the entries the previous run found (what the table draws now),
/// the entries this run finds, and the names that link them to their targets.
/// </summary>
internal sealed class TocState
{
    /// <summary>The most count-only runs before the numbers are taken as they stand.</summary>
    public const int MaxRuns = 6;

    private readonly Dictionary<TableOfContents, List<TocItem>> _visible = [];
    private readonly Dictionary<TableOfContents, double> _columns = [];
    private readonly Dictionary<TableOfContents, double> _widest = [];
    private readonly Dictionary<(TableOfContents, int), (double, double)> _leaders = [];
    private readonly Dictionary<Placed, string> _targets = [];
    private int _counter;

    /// <summary>What the table draws: the entries of the run before.</summary>
    public List<TocItem> Input { get; private set; } = [];

    /// <summary>What this run found.</summary>
    public List<TocItem> Output { get; } = [];

    /// <summary>True when the runs did not settle.</summary>
    public bool Unsettled { get; set; }

    /// <summary>Starts a run over: nothing found yet.</summary>
    public void Reset()
    {
        Output.Clear();
        _targets.Clear();
        _counter = 0;
    }

    /// <summary>Makes what the last run found what the table draws.</summary>
    public void Advance()
    {
        Input = [.. Output];
        _visible.Clear();
        _columns.Clear();
        _leaders.Clear();
    }

    public static bool Same(List<TocItem> a, List<TocItem> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    public static string NameOf(int index) => "onepdf-toc-" + index.ToString(CultureInfo.InvariantCulture);

    private readonly Dictionary<TableOfContents, List<int>> _visibleIndex = [];

    /// <summary>The entries of <see cref="Input"/> a table lists, by its level range.</summary>
    public IReadOnlyList<TocItem> Visible(TableOfContents toc)
    {
        if (!_visible.TryGetValue(toc, out List<TocItem>? list))
        {
            list = [];
            var index = new List<int>();
            int min = Math.Max(1, toc.MinLevel);
            for (int i = 0; i < Input.Count; i++)
            {
                TocItem it = Input[i];
                if (it.Level >= min && it.Level <= toc.MaxLevel)
                {
                    list.Add(it);
                    index.Add(i);
                }
            }

            _visible[toc] = list;
            _visibleIndex[toc] = index;
        }

        return list;
    }

    /// <summary>The destination name for the <paramref name="n"/>th entry <see cref="Visible"/> returned.</summary>
    public string TargetName(TableOfContents toc, int n) => NameOf(_visibleIndex[toc][n]);

    /// <summary>
    /// The width of the number column: the widest label of the entries listed, in their own styles. It never shrinks from one run to the
    /// next, so a label growing a digit cannot make the runs chase each other.
    /// </summary>
    public double NumberColumn(TableOfContents toc, LayoutContext ctx)
    {
        if (_columns.TryGetValue(toc, out double w))
        {
            return w;
        }

        double widest = _widest.TryGetValue(toc, out double before) ? before : 0;
        foreach (TocItem it in Visible(toc))
        {
            Text label = TableOfContents.MakeText(it.Label, toc.StyleOf(it.Level), HorizontalAlignment.Start);
            widest = Math.Max(widest, ctx.Text.Prepare(label, 1e6, ctx).NaturalWidth);
        }

        _widest[toc] = widest;
        _columns[toc] = widest;
        return widest;
    }

    /// <summary>The distance between two leader marks and the width of one, for a style.</summary>
    public (double Pitch, double Mark) LeaderMetrics(TableOfContents toc, LayoutContext ctx, in TableOfContents.Resolved style)
    {
        int key = HashCode.Combine(style.FontName, style.Weight, style.Italic, style.Size);
        if (_leaders.TryGetValue((toc, key), out (double, double) m))
        {
            return m;
        }

        string leader = toc.Leader!;
        double one = ctx.Text.Prepare(TableOfContents.MakeText(leader, style, HorizontalAlignment.Left), 1e6, ctx).NaturalWidth;
        double two = ctx.Text.Prepare(TableOfContents.MakeText(leader + " " + leader, style, HorizontalAlignment.Left), 1e6, ctx).NaturalWidth;
        m = (two - one, one);
        _leaders[(toc, key)] = m;
        return m;
    }

    /// <summary>
    /// Finds the headings and explicit entries on a page and records them, in the order they are drawn. When <paramref name="name"/> is
    /// true the elements that will be drawn are given the destination names the table links to.
    /// </summary>
    public void Collect(Placed body, int pageIndex, string label, bool name)
    {
        var stack = new Stack<Placed>();
        stack.Push(body);
        while (stack.Count > 0)
        {
            Placed p = stack.Pop();
            string? title = null;
            int level = 0;
            switch (p.Element)
            {
                case Heading h when !p.Continued && h.InToc:
                    level = Math.Clamp(h.Level, 1, 6);
                    title = h.TocTitle is { Length: > 0 } t ? t : Link.TextOf(p, 1000);
                    break;
                case TocEntry e when !p.Continued:
                    level = Math.Clamp(e.Level, 1, 9);
                    title = e.Title is { Length: > 0 } t2 ? t2 : Link.TextOf(p, 1000);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                if (name)
                {
                    _targets[p] = NameOf(_counter);
                }

                _counter++;
                Output.Add(new TocItem(title!, level, pageIndex, label));
            }

            for (int i = p.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(p.Children[i]);
            }
        }
    }

    /// <summary>Forgets the names of the page just drawn.</summary>
    public void EndPage() => _targets.Clear();

    public bool TryGetTarget(Placed p, out string? name) => _targets.TryGetValue(p, out name);

    /// <summary>Records the destination of a heading or entry that has one.</summary>
    public static void DefineTarget(in DrawContext d, Placed p)
    {
        if (d.Ctx.Toc is { } toc && toc.TryGetTarget(p, out string? name))
        {
            d.Ctx.DefineAnchor(d, name!);
        }
    }

    /// <summary>True when the element tree under <paramref name="root"/> holds a table of contents, as far as this package's own containers show.</summary>
    public static bool Contains(Element? root)
    {
        if (root is null)
        {
            return false;
        }

        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Element e = stack.Pop();
            if (!seen.Add(e))
            {
                continue;
            }

            if (e is TableOfContents)
            {
                return true;
            }

            e.PushChildren(stack);
        }

        return false;
    }
}
