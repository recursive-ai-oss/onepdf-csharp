using System.Runtime.ExceptionServices;
using System.Text;

namespace OnePdf.Text;

/// <summary>Supplies the glyphs of a page. The producer that joins the content interpreter to the PDF fonts implements this.</summary>
internal interface IGlyphSource
{
    /// <summary>The number of pages.</summary>
    int PageCount { get; }

    /// <summary>The glyphs of one page in content order, in upright page space (see <see cref="PdfTextGlyph"/>).</summary>
    PdfPageGlyphs GetPage(int pageIndex, CancellationToken cancellationToken);
}

/// <summary>
/// Text extraction: glyphs to words, lines and blocks in reading order, plain text in three modes, and the JSON form. The glyphs come from
/// a producer; <see cref="Analyze(PdfPageGlyphs, PdfTextOptions?, CancellationToken)"/> works on any list of glyphs.
/// </summary>
public sealed class PdfTextExtractor
{
    private readonly IGlyphSource _source;
    private readonly PdfDocument? _doc;
    private readonly PdfTextOptions _options;

    internal PdfTextExtractor(IGlyphSource source, PdfDocument? document, PdfTextOptions? options)
    {
        _source = source;
        _doc = document;
        _options = options ?? new PdfTextOptions();
    }

    /// <summary>
    /// Creates an extractor for a document: its pages are interpreted for glyphs (fonts decoded, positions and advances resolved)
    /// and analyzed into words, lines and blocks in reading order. The extractor holds no state beyond the document and can be
    /// used from several threads.
    /// </summary>
    /// <param name="document">The open document.</param>
    /// <param name="options">Options; null for the defaults.</param>
    public static PdfTextExtractor Create(PdfDocument document, PdfTextOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PdfTextExtractor(new PdfGlyphSource(document), document, options);
    }

    /// <summary>Same as <see cref="Create"/>; kept for callers written against the earlier shape. Never returns null.</summary>
    /// <param name="document">The open document.</param>
    /// <param name="options">Options; null for the defaults.</param>
    public static PdfTextExtractor? TryCreate(PdfDocument document, PdfTextOptions? options = null) => Create(document, options);

    private StructureOrder? Structure(CancellationToken ct) =>
        _doc is not null && _options.UseStructureTree ? _doc.GetStructureOrder(ct) : null;

    /// <summary>The number of pages.</summary>
    public int PageCount => _source.PageCount;

    /// <summary>Extracts one page (zero-based). Headers and footers are not flagged on a single page.</summary>
    /// <param name="pageIndex">Zero-based page index.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public PdfPageText ExtractPage(int pageIndex, CancellationToken cancellationToken = default)
    {
        if ((uint)pageIndex >= (uint)_source.PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        StructureOrder? structure = Structure(cancellationToken);
        return AnalyzeCore(_source.GetPage(pageIndex, cancellationToken), _options, structure?.ForPage(pageIndex), structure is not null, cancellationToken);
    }

    /// <summary>Extracts every page, in parallel (see <see cref="PdfTextOptions.MaxDegreeOfParallelism"/>), and flags headers and footers by their repetition.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    public IReadOnlyList<PdfPageText> ExtractDocument(CancellationToken cancellationToken = default)
    {
        int n = _source.PageCount;
        PdfPageText[] pages = new PdfPageText[n];
        int dop = _options.MaxDegreeOfParallelism > 0 ? _options.MaxDegreeOfParallelism : Math.Min(4, Environment.ProcessorCount);
        _ = Structure(cancellationToken); // read the tree once, before the pages run
        if (dop <= 1 || n <= 1)
        {
            for (int i = 0; i < n; i++)
            {
                pages[i] = ExtractPage(i, cancellationToken);
            }
        }
        else
        {
            try
            {
                Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = cancellationToken }, i => pages[i] = ExtractPage(i, cancellationToken));
            }
            catch (AggregateException ae) when (ae.InnerExceptions.Count > 0)
            {
                ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
            }
        }

        if (_options.DetectHeadersAndFooters)
        {
            Furniture.Flag(pages);
        }

        return pages;
    }

    /// <summary>Analyzes the glyphs of one page: exclusions, <c>/ActualText</c>, ligatures and duplicates, then words, lines and blocks in reading order.</summary>
    /// <param name="page">The glyphs and page size.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public static PdfPageText Analyze(PdfPageGlyphs page, PdfTextOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return AnalyzeCore(page, options ?? new PdfTextOptions(), null, false, cancellationToken);
    }

    /// <summary>Flags headers and footers across a set of analyzed pages by their repetition (the step <see cref="ExtractDocument"/> performs).</summary>
    /// <param name="pages">The pages of one document.</param>
    public static void FlagHeadersAndFooters(IReadOnlyList<PdfPageText> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        Furniture.Flag(pages);
    }

    internal static PdfPageText AnalyzeCore(PdfPageGlyphs page, PdfTextOptions o, PageStructure? structure, bool tagged, CancellationToken ct)
    {
        PdfTextGlyph[] g = GlyphPrep.Prepare(page, o, ct);
        List<PdfDiagnostic> diag = [];
        List<PdfTextBlock>? blocks = null;
        bool used = false;

        if (tagged && o.UseStructureTree)
        {
            if (structure is not null)
            {
                int total = 0, covered = 0;
                foreach (PdfTextGlyph x in g)
                {
                    if (!x.IsVisible || IsBlank(x.Text))
                    {
                        continue;
                    }

                    total++;
                    if (x.MarkedContentId >= 0 && structure.Map.ContainsKey(x.MarkedContentId))
                    {
                        covered++;
                    }
                }

                if (total > 0 && covered >= LayoutParameters.StructureCoverage * total)
                {
                    blocks = ByStructure(g, structure, page.Height, ct);
                    used = true;
                }
                else if (total > 0)
                {
                    diag.Add(new PdfDiagnostic(PdfSeverity.Info, -1, -1,
                        $"Page {page.PageIndex}: the structure tree covers {covered} of {total} visible glyphs; reading order falls back to the page geometry."));
                }
            }
            else
            {
                diag.Add(new PdfDiagnostic(PdfSeverity.Info, -1, -1, $"Page {page.PageIndex}: the structure tree has no marked content for this page; reading order falls back to the page geometry."));
            }
        }

        if (blocks is null)
        {
            blocks = TextLayout.RunAll(g, ct);
        }

        return new PdfPageText(page.PageIndex, page.Width, page.Height, g, blocks.ToArray(), used, diag, o);
    }

    private static bool IsBlank(string t)
    {
        foreach (char c in t)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    private static List<PdfTextBlock> ByStructure(PdfTextGlyph[] g, PageStructure s, double pageHeight, CancellationToken ct)
    {
        SortedDictionary<int, List<int>> groups = [];
        List<int> orphans = [];
        for (int i = 0; i < g.Length; i++)
        {
            if (g[i].MarkedContentId >= 0 && s.Map.TryGetValue(g[i].MarkedContentId, out (int Rank, int Group) v))
            {
                if (!groups.TryGetValue(v.Group, out List<int>? l))
                {
                    groups[v.Group] = l = [];
                }

                l.Add(i);
            }
            else
            {
                orphans.Add(i);
            }
        }

        List<PdfTextBlock> result = [];
        List<PdfTextBlock> tail = [];
        foreach (PdfTextBlock b in TextLayout.Run(g, orphans.ToArray(), ct))
        {
            // Untagged text (artifacts such as running heads) goes where it sits: above the content or below it.
            if ((b.Bounds.Bottom + b.Bounds.Top) / 2 > pageHeight * (1 - 2 * LayoutParameters.FurnitureBand))
            {
                result.Add(b);
            }
            else
            {
                tail.Add(b);
            }
        }

        foreach (List<int> group in groups.Values)
        {
            result.AddRange(TextLayout.Run(g, group.ToArray(), ct));
        }

        result.AddRange(tail);
        return result;
    }
}

/// <summary>Header and footer detection by repetition across the pages of a document.</summary>
internal static class Furniture
{
    private static string Key(PdfTextBlock b)
    {
        StringBuilder sb = new();
        foreach (PdfTextLine l in b.Lines)
        {
            foreach (char c in l.Text)
            {
                if (char.IsDigit(c))
                {
                    if (sb.Length == 0 || sb[^1] != '#')
                    {
                        sb.Append('#');
                    }
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0 && sb[^1] != ' ')
                    {
                        sb.Append(' ');
                    }
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            sb.Append(' ');
        }

        return sb.ToString().Trim();
    }

    internal static void Flag(IReadOnlyList<PdfPageText> pages)
    {
        int n = pages.Count;
        if (n < 2)
        {
            return;
        }

        Dictionary<string, int> top = [];
        Dictionary<string, int> bottom = [];
        foreach (PdfPageText p in pages)
        {
            HashSet<string> seenTop = [], seenBottom = [];
            foreach (PdfTextBlock b in p.Blocks)
            {
                if (b.Bounds.Bottom >= p.Height * (1 - LayoutParameters.FurnitureBand))
                {
                    string k = Key(b);
                    if (k.Length > 0 && seenTop.Add(k))
                    {
                        top[k] = top.GetValueOrDefault(k) + 1;
                    }
                }
                else if (b.Bounds.Top <= p.Height * LayoutParameters.FurnitureBand)
                {
                    string k = Key(b);
                    if (k.Length > 0 && seenBottom.Add(k))
                    {
                        bottom[k] = bottom.GetValueOrDefault(k) + 1;
                    }
                }
            }
        }

        int need = Math.Max(2, (int)Math.Ceiling(LayoutParameters.FurnitureRepeat * n));
        foreach (PdfPageText p in pages)
        {
            foreach (PdfTextBlock b in p.Blocks)
            {
                if (b.Bounds.Bottom >= p.Height * (1 - LayoutParameters.FurnitureBand) && top.GetValueOrDefault(Key(b)) >= need)
                {
                    b.Role = PdfTextRole.Header;
                }
                else if (b.Bounds.Top <= p.Height * LayoutParameters.FurnitureBand && bottom.GetValueOrDefault(Key(b)) >= need)
                {
                    b.Role = PdfTextRole.Footer;
                }
            }
        }
    }
}
