using System.Linq;
using OnePdf.Text;

namespace OnePdf.Tagging;

/// <summary>
/// The happy path of tagging: maps the role a caller gives each block, plus the options, onto a plan of <see cref="PdfTagElement"/>s.
/// It holds no logic beyond that mapping; the rewriting is the plan's. The mapping is described in <c>docs/tagging.md</c>.
/// </summary>
internal sealed class HappyPath
{
    private readonly PdfDocument _doc;
    private readonly IReadOnlyList<PdfPageText> _text;
    private readonly Func<PdfTagBlock, PdfBlockRole?> _roles;
    private readonly PdfTagOptions _o;
    private readonly PdfTaggingPlan _plan;
    private readonly List<string> _warnings = [];
    private PdfTagElement? _list;
    private PdfTagElement? _table;
    private int _tableRows;

    private HappyPath(PdfDocument doc, IReadOnlyList<PdfPageText> text, Func<PdfTagBlock, PdfBlockRole?> roles, PdfTagOptions o)
    {
        _doc = doc;
        _text = text;
        _roles = roles;
        _o = o;
        _plan = doc.CreateTaggingPlan();
    }

    internal static PdfTaggingPlan Build(PdfDocument doc, IReadOnlyList<PdfPageText> text, Func<PdfTagBlock, PdfBlockRole?> roles, PdfTagOptions o, out IReadOnlyList<string> warnings)
    {
        var h = new HappyPath(doc, text, roles, o);
        h.Run();
        warnings = h._warnings;
        return h._plan;
    }

    private void Run()
    {
        _plan.Title = _o.Title;
        _plan.Language = _o.Language;
        foreach (PdfPageText page in _text)
        {
            ArgumentNullException.ThrowIfNull(page);
            PdfTagFigure[] figures = [.. _o.Figures.Where(f => f.PageIndex == page.PageIndex).OrderByDescending(static f => f.Area.Top)];
            int nextFigure = 0;
            PageLinks links = PageLinks.Find(_doc, page, _o.TagLinks);
            for (int b = 0; b < page.Blocks.Count; b++)
            {
                PdfTextBlock block = page.Blocks[b];
                double centre = (block.Bounds.Top + block.Bounds.Bottom) / 2;
                while (nextFigure < figures.Length && (figures[nextFigure].Area.Top + figures[nextFigure].Area.Bottom) / 2 >= centre)
                {
                    AddFigure(page, figures[nextFigure++]);
                }

                Block(page, b, links);
            }

            while (nextFigure < figures.Length)
            {
                AddFigure(page, figures[nextFigure++]);
            }

            links.ReportUnused(page.PageIndex, _warnings);
            if (_o.TagAnnotations)
            {
                CloseRuns();
                foreach (PdfAnnotation a in _doc.Pages[page.PageIndex].Annotations)
                {
                    if (a.Subtype is not ("Link" or "Widget" or "Popup" or "TrapNet" or "PrinterMark") && a.Dictionary.Reference is not null)
                    {
                        _plan.Add(PdfTagRole.Annot, PdfTagContent.Annotation(a));
                    }
                }
            }
        }
    }

    private void AddFigure(PdfPageText page, PdfTagFigure f)
    {
        CloseRuns();
        PdfTagElement e = _plan.Add(PdfTagRole.Figure, PdfTagContent.Graphics(page, f.Area));
        e.Alt = f.Alt;
    }

    private void CloseRuns()
    {
        _list = null;
        _table = null;
    }

    private PdfTagElement New(PdfTagElement? parent, PdfTagRole role, PdfTagBlock tb, params PdfTagContent[] content)
    {
        PdfTagElement e = parent is null ? _plan.Add(role, content) : parent.Add(role, content);
        _o.Customize?.Invoke(e, tb);
        return e;
    }

    private void Block(PdfPageText page, int index, PageLinks links)
    {
        PdfTextBlock block = page.Blocks[index];
        if (block.Role != PdfTextRole.Body && !_o.KeepHeadersAndFooters)
        {
            _plan.Artifact(PdfTagContent.Block(page, index), block.Role == PdfTextRole.Header ? PdfArtifactKind.Header : PdfArtifactKind.Footer);
            return;
        }

        var tb = new PdfTagBlock(page, index);
        PdfBlockRole role = _roles(tb) ?? PdfBlockRole.Artifact;
        switch (role.Kind)
        {
            case PdfBlockKind.Artifact:
                _plan.Artifact(PdfTagContent.Block(page, index), PdfArtifactKind.Plain);
                return;
            case PdfBlockKind.Heading:
                CloseRuns();
                Text(New(null, HeadingRole(role.Level), tb), tb, links);
                return;
            case PdfBlockKind.Paragraph:
                CloseRuns();
                Text(New(null, PdfTagRole.P, tb), tb, links);
                return;
            case PdfBlockKind.Figure:
                CloseRuns();
                PdfTagElement fig = New(null, PdfTagRole.Figure, tb, PdfTagContent.Block(page, index));
                fig.Alt = role.Alt;
                return;
            case PdfBlockKind.ListItem:
                _table = null;
                ListItems(tb, links);
                return;
            case PdfBlockKind.Table:
            case PdfBlockKind.TableContinued:
                _list = null;
                Rows(tb, links, continued: role.Kind == PdfBlockKind.TableContinued && _table is not null);
                return;
        }
    }

    private static PdfTagRole HeadingRole(int level) => (PdfTagRole)((int)PdfTagRole.H1 + level - 1);

    // ---- text with links ----

    private void Text(PdfTagElement el, PdfTagBlock tb, PageLinks links)
    {
        if (!links.HasLinks(tb.BlockIndex))
        {
            el.Add(PdfTagContent.Block(tb.Page, tb.BlockIndex));
            return;
        }

        var runs = new List<(int Line, int From, int Count)>();
        for (int l = 0; l < tb.Block.Lines.Count; l++)
        {
            runs.Add((l, 0, tb.Block.Lines[l].Words.Count));
        }

        Runs(el, tb, links, runs);
    }

    // Adds word runs to an element, cutting out the words a link annotation covers into Link elements.
    private void Runs(PdfTagElement el, PdfTagBlock tb, PageLinks links, List<(int Line, int From, int Count)> runs)
    {
        var made = new Dictionary<int, PdfTagElement>();
        foreach ((int line, int from, int count) in runs)
        {
            PdfTextLine tl = tb.Block.Lines[line];
            int w = from, end = from + count;
            while (w < end)
            {
                int link = links.LinkOf(tb.BlockIndex, line, w);
                int e = w + 1;
                while (e < end && links.LinkOf(tb.BlockIndex, line, e) == link)
                {
                    e++;
                }

                PdfTagContent words = PdfTagContent.Words(tb.Page, tl, w, e - w);
                if (link < 0)
                {
                    el.Add(words);
                }
                else if (made.TryGetValue(link, out PdfTagElement? le))
                {
                    le.Add(words);
                }
                else
                {
                    le = New(el, PdfTagRole.Link, tb, words, PdfTagContent.Annotation(links.Annotation(link)));
                    made[link] = le;
                    links.Use(link);
                }

                w = e;
            }
        }
    }

    // ---- lists ----

    private bool IsLabel(string word) => _o.IsListLabel is { } f ? f(word) : ListLabels.IsLabel(word);

    private void ListItems(PdfTagBlock tb, PageLinks links)
    {
        _list ??= New(null, PdfTagRole.L, tb);
        IReadOnlyList<PdfTextLine> lines = tb.Block.Lines;
        var starts = new List<int>();
        if (_o.SplitListBlocks)
        {
            for (int l = 0; l < lines.Count; l++)
            {
                if (lines[l].Words.Count > 0 && IsLabel(lines[l].Words[0].Text))
                {
                    starts.Add(l);
                }
            }
        }

        if (starts.Count == 0 || starts[0] != 0)
        {
            starts.Insert(0, 0);
        }

        for (int s = 0; s < starts.Count; s++)
        {
            int first = starts[s];
            int last = s + 1 < starts.Count ? starts[s + 1] : lines.Count;
            PdfTagElement li = New(_list, PdfTagRole.LI, tb);
            var runs = new List<(int, int, int)>();
            PdfTextLine head = lines[first];
            int bodyFrom = 0;
            if (_o.SplitListLabels && head.Words.Count > 0 && IsLabel(head.Words[0].Text))
            {
                New(li, PdfTagRole.Lbl, tb, PdfTagContent.Words(tb.Page, head, 0));
                bodyFrom = 1;
            }

            if (head.Words.Count > bodyFrom)
            {
                runs.Add((first, bodyFrom, head.Words.Count - bodyFrom));
            }

            for (int l = first + 1; l < last; l++)
            {
                if (lines[l].Words.Count > 0)
                {
                    runs.Add((l, 0, lines[l].Words.Count));
                }
            }

            if (runs.Count > 0)
            {
                Runs(New(li, PdfTagRole.LBody, tb), tb, links, runs);
            }
        }
    }

    // ---- tables ----

    private void Rows(PdfTagBlock tb, PageLinks links, bool continued)
    {
        if (!continued)
        {
            _table = New(null, PdfTagRole.Table, tb);
            _tableRows = 0;
        }

        IReadOnlyList<PdfTextLine> lines = tb.Block.Lines;
        for (int l = 0; l < lines.Count; l++)
        {
            PdfTextLine line = lines[l];
            if (line.Words.Count == 0)
            {
                continue;
            }

            bool headerRow = !continued && _tableRows < _o.TableHeaderRows;
            _tableRows++;
            PdfTagElement tr = New(_table, PdfTagRole.TR, tb);
            List<int> starts = [.. line.CellStarts.Where(s => s >= 0 && s < line.Words.Count).Distinct().Order()];
            if (starts.Count == 0 || starts[0] != 0)
            {
                starts.Insert(0, 0);
            }

            for (int c = 0; c < starts.Count; c++)
            {
                int end = c + 1 < starts.Count ? starts[c + 1] : line.Words.Count;
                PdfTagElement cell;
                if (headerRow)
                {
                    cell = New(tr, PdfTagRole.TH, tb);
                    cell.Scope = _o.HeaderRowScope;
                }
                else if (c < _o.TableHeaderColumns)
                {
                    cell = New(tr, PdfTagRole.TH, tb);
                    cell.Scope = _o.HeaderColumnScope;
                }
                else
                {
                    cell = New(tr, PdfTagRole.TD, tb);
                }

                Runs(cell, tb, links, [(l, starts[c], end - starts[c])]);
            }
        }
    }

    // ---- links ----

    /// <summary>The link annotations of a page and the words each covers.</summary>
    private sealed class PageLinks
    {
        private readonly Dictionary<(int Block, int Line, int Word), int> _words = [];
        private readonly HashSet<int> _blocks = [];
        private readonly List<PdfAnnotation> _annotations = [];
        private readonly List<bool> _used = [];
        private readonly List<string> _unmatched = [];

        public static PageLinks Find(PdfDocument doc, PdfPageText text, bool enabled)
        {
            var r = new PageLinks();
            if (!enabled)
            {
                return r;
            }

            PdfPage page = doc.Pages[text.PageIndex];
            IReadOnlyList<PdfAnnotation> annots = page.Annotations;
            for (int a = 0; a < annots.Count; a++)
            {
                if (annots[a].Subtype != "Link" || annots[a].Dictionary.Reference is null)
                {
                    continue;
                }

                int id = r._annotations.Count;
                r._annotations.Add(annots[a]);
                r._used.Add(false);
                r.Cover(page, text, annots[a], id, a);
            }

            return r;
        }

        private void Cover(PdfPage page, PdfPageText text, PdfAnnotation annot, int id, int annotIndex)
        {
            // Rect is in default user space; extraction reports upright space with the origin at the crop box corner (ISO 32000-2 8.3.2.3).
            PdfRectangle rect = annot.Rect;
            PdfRectangle box = page.CropBox;
            double w = box.Width, h = box.Height;
            (double X, double Y) P(double x, double y)
            {
                double dx = x - box.Left, dy = y - box.Bottom;
                return page.Rotate switch
                {
                    90 => (dy, w - dx),
                    180 => (w - dx, h - dy),
                    270 => (h - dy, dx),
                    _ => (dx, dy),
                };
            }

            (double x0, double y0) = P(Math.Min(rect.Left, rect.Right), Math.Min(rect.Bottom, rect.Top));
            (double x1, double y1) = P(Math.Max(rect.Left, rect.Right), Math.Max(rect.Bottom, rect.Top));
            double left = Math.Min(x0, x1), right = Math.Max(x0, x1), bottom = Math.Min(y0, y1), top = Math.Max(y0, y1);
            int bestBlock = -1, bestCount = 0;
            for (int b = 0; b < text.Blocks.Count; b++)
            {
                PdfRectangle bb = text.Blocks[b].Bounds;
                if (bb.Right < left || bb.Left > right || bb.Top < bottom || bb.Bottom > top)
                {
                    continue;
                }

                int n = 0;
                foreach (PdfTextLine line in text.Blocks[b].Lines)
                {
                    foreach (PdfTextWord word in line.Words)
                    {
                        if (Inside(word.Bounds, left, bottom, right, top))
                        {
                            n++;
                        }
                    }
                }

                if (n > bestCount)
                {
                    bestCount = n;
                    bestBlock = b;
                }
            }

            if (bestBlock < 0)
            {
                _unmatched.Add($"Link annotation {annotIndex} covers no text; it was left untagged.");
                return;
            }

            IReadOnlyList<PdfTextLine> lines = text.Blocks[bestBlock].Lines;
            int claimed = 0;
            for (int l = 0; l < lines.Count; l++)
            {
                IReadOnlyList<PdfTextWord> words = lines[l].Words;
                for (int k = 0; k < words.Count; k++)
                {
                    if (Inside(words[k].Bounds, left, bottom, right, top) && _words.TryAdd((bestBlock, l, k), id))
                    {
                        claimed++;
                    }
                }
            }

            if (claimed > 0)
            {
                _blocks.Add(bestBlock);
            }
        }

        private static bool Inside(PdfRectangle b, double left, double bottom, double right, double top)
        {
            double cx = (b.Left + b.Right) / 2, cy = (b.Bottom + b.Top) / 2;
            return cx >= left && cx <= right && cy >= bottom && cy <= top;
        }

        public bool HasLinks(int block) => _blocks.Contains(block);

        public int LinkOf(int block, int line, int word) => _words.TryGetValue((block, line, word), out int id) ? id : -1;

        public PdfAnnotation Annotation(int id) => _annotations[id];

        public void Use(int id) => _used[id] = true;

        public void ReportUnused(int pageIndex, List<string> warnings)
        {
            foreach (string u in _unmatched)
            {
                warnings.Add($"Page {pageIndex + 1}: {u}");
            }

            for (int i = 0; i < _used.Count; i++)
            {
                if (!_used[i] && _words.ContainsValue(i))
                {
                    warnings.Add($"Page {pageIndex + 1}: a link annotation lies over text that is an artifact or a list label; it was left untagged.");
                }
            }
        }
    }
}

/// <summary>The built-in test for a list label word.</summary>
internal static class ListLabels
{
    private const string Bullets = "•◦▪▫■□●○‣⁃∙·–—-*►▶➢✓✔";

    public static bool IsLabel(string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        if (word.Length == 1)
        {
            return Bullets.Contains(word[0], StringComparison.Ordinal);
        }

        string core = word;
        bool bracketed = core[0] == '(' && core[^1] == ')';
        if (bracketed)
        {
            core = core[1..^1];
        }
        else if (core[^1] is '.' or ')')
        {
            core = core[..^1];
        }
        else
        {
            return false;
        }

        if (core.Length == 0 || core.Length > 6)
        {
            return false;
        }

        bool digits = true, roman = true;
        foreach (char ch in core)
        {
            digits &= ch is >= '0' and <= '9';
            roman &= "ivxlcdmIVXLCDM".Contains(ch, StringComparison.Ordinal);
        }

        return (digits && core.Length <= 3) || roman || (core.Length == 1 && char.IsAsciiLetter(core[0]));
    }
}
