using System.Globalization;
using System.Text.RegularExpressions;

namespace OnePdf.Layout.Tests;

/// <summary>Values computed from what landed on each page: carried and brought forward, page subtotals, markers, in every flow.</summary>
public partial class DynamicContentTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Money(decimal v) => v.ToString("N2", Inv);

    [GeneratedRegex(@"[\d,]+\.\d\d")]
    private static partial Regex AmountRx();

    [GeneratedRegex(@"Item (\d+)")]
    private static partial Regex ItemRx();

    // Cents that are not round, so sums expose any double rounding.
    private static decimal AmountOf(int i) => (i * 37 % 91 + 1) * 1.07m + (i % 3) * 0.01m;

    private enum Style
    {
        Table,
        KeepGroups,
        MultiColumn,
    }

    private static LayoutDocument Statement(int rows, Style style = Style.Table, bool toc = false, bool rtl = false, Action? onFormat = null, bool title = false, bool keep = true)
    {
        return LayoutDocument.Compose(doc =>
        {
            if (rtl)
            {
                doc.RightToLeft();
            }

            if (title)
            {
                doc.Title("Statement").Language("en");
            }

            doc.Page(page =>
            {
                page.Size(260, 230);
                page.Margin(16);
                page.Header().DynamicText(v =>
                {
                    onFormat?.Invoke();
                    return "Brought forward: " + Money(v.BroughtForward("amt"));
                }).FontSize(9).Bold();
                page.Footer().Column(c =>
                {
                    c.Item().DynamicText(v => "Page subtotal: " + Money(v.Sum("amt"))).FontSize(9);
                    c.Item().DynamicText(v => "Carried forward: " + Money(v.Total("amt"))).FontSize(9).Bold();
                });
                page.Content().Column(col =>
                {
                    col.Spacing(3);
                    if (toc)
                    {
                        col.Item().Heading(1).Text("Statement").FontSize(14);
                        col.Item().TableOfContents(null);
                        col.Item().PageBreak();
                    }

                    switch (style)
                    {
                        case Style.Table:
                            col.Item().Table(t =>
                            {
                                t.Columns(c => { c.Relative(); c.Constant(70); });
                                t.CellPadding(2);
                                t.CellBorder(0.3, Colors.LightGrey);
                                t.HeaderRow(r =>
                                {
                                    r.Cell().Text("Entry").FontSize(8).Bold();
                                    r.Cell().AlignEnd().Text("Amount").FontSize(8).Bold();
                                });
                                for (int i = 0; i < rows; i++)
                                {
                                    int n = i;
                                    t.Row(r =>
                                    {
                                        r.KeepTogether();
                                        r.Cell().Text(n % 7 == 3 ? $"Item {n}\nremark for item {n}" : $"Item {n}").FontSize(9);
                                        r.Cell().Accumulate("amt", AmountOf(n)).AlignEnd().Text(Money(AmountOf(n))).FontSize(9);
                                    });
                                }
                            });
                            break;
                        case Style.KeepGroups:
                            for (int i = 0; i < rows; i++)
                            {
                                int n = i;
                                (keep ? col.Item().KeepTogether() : col.Item()).Accumulate("amt", AmountOf(n)).Column(g =>
                                {
                                    g.Item().Text($"Item {n}   {Money(AmountOf(n))}").FontSize(9);
                                    for (int k = 0; k < n % 4; k++)
                                    {
                                        g.Item().Text($"detail {k} of entry {n}").FontSize(7);
                                    }
                                });
                            }

                            break;
                        default:
                            col.Item().MultiColumn(m =>
                            {
                                m.Count(2).Gap(10).Spacing(2);
                                for (int i = 0; i < rows; i++)
                                {
                                    int n = i;
                                    m.Item().KeepTogether().Accumulate("amt", AmountOf(n)).Column(g =>
                                    {
                                        g.Item().Text($"Item {n}  {Money(AmountOf(n))}").FontSize(9);
                                        for (int k = 0; k < n % 3; k++)
                                        {
                                            g.Item().Text($"detail {k}").FontSize(7);
                                        }
                                    });
                                }
                            });
                            break;
                    }
                });
            });
        });
    }

    private static string[] PageTexts(byte[] pdf, out int count)
    {
        using PdfDocument doc = PdfDocument.Open(pdf);
        count = doc.Pages.Count;
        var texts = new string[count];
        foreach (OnePdf.Text.PdfPageText t in doc.ExtractText())
        {
            texts[t.PageIndex] = t.Text;
        }

        return texts;
    }

    private static decimal Parse(string amount) => decimal.Parse(amount, NumberStyles.Number, Inv);

    private static decimal Find(string text, string label)
    {
        Match m = Regex.Match(text, Regex.Escape(label) + @":\s*([\d,]+\.\d\d)");
        Assert.True(m.Success, $"'{label}' not found in:\n{text}");
        return Parse(m.Groups[1].Value);
    }

    // The amounts of the "Item n" lines found in the extracted text of a page, independent of the library's own bookkeeping. In a table the
    // amount follows the label on the line (or precedes it in right-to-left content).
    private static decimal RowSum(string text, List<int>? items = null)
    {
        decimal sum = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.Contains("remark", StringComparison.Ordinal) || line.Contains("forward", StringComparison.Ordinal) || line.Contains("subtotal", StringComparison.Ordinal))
            {
                continue;
            }

            MatchCollection labels = ItemRx().Matches(line);
            MatchCollection amounts = AmountRx().Matches(line);
            Assert.Equal(labels.Count, amounts.Count);
            for (int k = 0; k < labels.Count; k++)
            {
                int n = int.Parse(labels[k].Groups[1].Value, Inv);
                items?.Add(n);
                // Pairs are in line order for left-to-right content and mirrored for right-to-left: the amount must be this item's own.
                Assert.Contains(amounts, a => a.Value == Money(AmountOf(n)));
                sum += Parse(Money(AmountOf(n)));
            }
        }

        return sum;
    }

    private static void AssertStatement(byte[] pdf, int rows, int minPages = 3)
    {
        string[] texts = PageTexts(pdf, out int pages);
        Assert.True(pages >= minPages, "pages: " + pages);
        decimal running = 0;
        var seen = new List<int>();
        for (int p = 0; p < pages; p++)
        {
            decimal rowSum = RowSum(texts[p], seen);
            Assert.Equal(running, Find(texts[p], "Brought forward"));
            Assert.Equal(rowSum, Find(texts[p], "Page subtotal"));
            running += rowSum;
            Assert.Equal(running, Find(texts[p], "Carried forward"));
        }

        Assert.Equal(Enumerable.Range(0, rows).Order(), seen.Order());
        decimal all = Enumerable.Range(0, rows).Sum(i => AmountOf(i));
        Assert.Equal(all, running);
    }

    [Fact]
    public void TableRowsCarryForwardWhatLandedOnEachPage() => AssertStatement(Support.Save(Statement(60)), 60);

    private static List<int>[] ItemsPerPage(byte[] pdf)
    {
        string[] texts = PageTexts(pdf, out int pages);
        var result = new List<int>[pages];
        for (int p = 0; p < pages; p++)
        {
            result[p] = [];
            RowSum(texts[p], result[p]);
        }

        return result;
    }

    [Fact]
    public void GroupsPushedByKeepRulesTakeTheirAmountToTheNextPage()
    {
        byte[] kept = Support.Save(Statement(45, Style.KeepGroups, keep: true));
        byte[] loose = Support.Save(Statement(45, Style.KeepGroups, keep: false));
        AssertStatement(kept, 45);
        AssertStatement(loose, 45);

        // Groups split between their lines when they may, and move whole when they must stay together: the two layouts put different
        // items on the pages, and both carry forward exactly what is on them.
        List<int>[] a = ItemsPerPage(kept);
        List<int>[] b = ItemsPerPage(loose);
        Assert.False(a.Length == b.Length && a.Zip(b).All(t => t.First.SequenceEqual(t.Second)), "the keep rule changed nothing");
    }

    [Fact]
    public void ColumnFlowContributesFromEveryColumn() => AssertStatement(Support.Save(Statement(120, Style.MultiColumn)), 120);

    [Fact]
    public void ABookWithATableOfContentsDoesNotCountTwice()
    {
        int calls = 0;
        byte[] pdf = Support.Save(Statement(60, toc: true, onFormat: () => calls++));
        string[] texts = PageTexts(pdf, out int pages);

        // The pages of the contents hold no rows: brought and carried forward are both zero there, then the rows add up exactly once.
        decimal running = 0;
        for (int p = 0; p < pages; p++)
        {
            decimal rows = RowSum(texts[p]);
            Assert.Equal(running, Find(texts[p], "Brought forward"));
            running += rows;
            Assert.Equal(running, Find(texts[p], "Carried forward"));
        }

        Assert.Equal(Enumerable.Range(0, 60).Sum(i => AmountOf(i)), running);

        // The formatting callback ran once per page that was written, not once per layout run of the table of contents.
        Assert.Equal(pages, calls);
    }

    [Fact]
    public void RightToLeftDocumentsGetTheSameNumbers() => AssertStatement(Support.Save(Statement(60, rtl: true)), 60);

    [Fact]
    public void OutputIsDeterministic()
    {
        byte[] a = Support.Save(Statement(60));
        byte[] b = Support.Save(Statement(60));
        Assert.Equal(a, b);
    }

    [Fact]
    public void NoContributionsReadAsZeroAndNull()
    {
        string? seen = "unset";
        decimal total = -1;
        var doc = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Footer().DynamicText(v =>
            {
                seen = v.Current("nothing");
                total = v.Total("nothing");
                return null;
            });
            p.Content().Text("Plain");
        }));
        Support.Save(doc);
        Assert.Null(seen);
        Assert.Equal(0m, total);
    }

    [Fact]
    public void DictionaryHeadersShowFirstAndLastEntryOfThePage()
    {
        const int entries = 150;
        var doc = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(260, 230);
            p.Margin(16);
            p.Header().DynamicText(v => $"From {v.First("entry")} to {v.Last("entry")}");
            p.Content().Column(col =>
            {
                for (int i = 0; i < entries; i++)
                {
                    string name = $"entry{i:D3}";
                    col.Item().Marker("entry", name).Text($"{name} meaning of the word").FontSize(9);
                }
            });
        }));
        string[] texts = PageTexts(Support.Save(doc), out int pages);
        Assert.True(pages >= 3);
        for (int p = 0; p < pages; p++)
        {
            string[] names = Regex.Matches(texts[p], @"entry\d{3}").Select(m => m.Value).ToArray();
            // Line 1 is the header, which names the first and the last entry of the page; the body lists them in order.
            string[] body = names.Skip(2).ToArray();
            Match head = Regex.Match(texts[p], @"From (entry\d{3}) to (entry\d{3})");
            Assert.True(head.Success);
            Assert.Equal(body[0], head.Groups[1].Value);
            Assert.Equal(body[^1], head.Groups[2].Value);
        }
    }

    [Fact]
    public void CurrentSectionTitleFollowsTheSectionsAndTheChaptersWithin()
    {
        var doc = LayoutDocument.Compose(d =>
        {
            for (int s = 1; s <= 3; s++)
            {
                int part = s;
                d.Page(p =>
                {
                    p.Size(260, 230);
                    p.Margin(16);
                    p.Header().DynamicText(v => $"Header: {v.Entering("part") ?? "none"} / {v.Current("part")}");
                    p.Content().Column(col =>
                    {
                        // The marker is on the first line only; later pages of the section inherit it.
                        col.Item().Marker("part", $"Part {part}").Text($"Line 0 of part {part}").FontSize(9);
                        for (int i = 1; i < 40 * part; i++)
                        {
                            col.Item().Text($"Line {i} of part {part}").FontSize(9);
                        }
                    });
                });
            }
        });
        string[] texts = PageTexts(Support.Save(doc), out int pages);
        Assert.True(pages >= 6);
        for (int p = 0; p < pages; p++)
        {
            Match head = Regex.Match(texts[p], @"Header: (Part \d|none) / (Part \d)");
            Assert.True(head.Success, texts[p]);
            string part = Regex.Match(texts[p], @"of part (\d)").Groups[1].Value;
            Assert.Equal("Part " + part, head.Groups[2].Value);
            bool firstOfSection = texts[p].Contains("Line 0 of", StringComparison.Ordinal);
            if (firstOfSection)
            {
                // Entering is the title in force before the page: the previous section's, or none at the very start.
                Assert.Equal(part == "1" ? "none" : "Part " + (int.Parse(part, Inv) - 1), head.Groups[1].Value);
            }
            else
            {
                Assert.Equal("Part " + part, head.Groups[1].Value);
            }
        }
    }

    [Fact]
    public void SumsOfDecimalsAreExact()
    {
        // 0.1 + 0.2 style sums: a thousand 0.10 amounts are exactly 100.00.
        var doc = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(260, 230);
            p.Margin(16);
            p.Footer().DynamicText(v => "Total " + v.Total("x").ToString("F2", Inv));
            p.Content().Column(col =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    col.Item().Accumulate("x", 0.10m).Text(" ").FontSize(2);
                }
            });
        }));
        string[] texts = PageTexts(Support.Save(doc), out int pages);
        Assert.Contains("Total 100.00", texts[pages - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverflowingAccountIsALayoutError()
    {
        var doc = LayoutDocument.Compose(d => d.Page(p => p.Content().Column(col =>
        {
            col.Item().Accumulate("x", decimal.MaxValue).Text("a");
            col.Item().Accumulate("x", decimal.MaxValue).Text("b");
        })));
        Assert.Throws<PdfLayoutException>(() => Support.Save(doc));
    }

    [Fact]
    public void ADynamicTextInTheBodyIsTaggedContentAndOneInTheHeaderIsAnArtifact()
    {
        byte[] pdf = Support.Save(LayoutDocument.Compose(d =>
        {
            d.Title("Statement").Language("en");
            d.Page(p =>
            {
                p.Size(260, 230);
                p.Margin(16);
                p.Header().DynamicText(v => "Header value " + v.PageNumber);
                p.Content().Column(col =>
                {
                    col.Item().Accumulate("a", 5m).Text("Row").FontSize(9);
                    col.Item().DynamicText(v => "Body value " + Money(v.Total("a")));
                });
            });
        }));
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.Empty(TagSupport.Untagged(doc));
        // The row and the body value are tagged paragraphs; the header value is an artifact and holds no structure element.
        SNode root = TagSupport.ReadTree(doc);
        Assert.Equal(2, root.Descendants().Count(n => n.Content.Count > 0));
    }

    private static byte[] Save(LayoutDocument layout, PdfSaveOptions options)
    {
        using var ms = new MemoryStream();
        layout.Save(ms, options);
        return ms.ToArray();
    }

    [Fact]
    public void TaggedOutputPassesPdfA2bAndUa1()
    {
        byte[] ua = Save(Statement(40, title: true), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        Assert.Empty(TagSupport.Vera(ua, "ua1"));
        byte[] a = Save(Statement(40, title: true), new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        Assert.Empty(TagSupport.Vera(a, "2b"));
    }
}
