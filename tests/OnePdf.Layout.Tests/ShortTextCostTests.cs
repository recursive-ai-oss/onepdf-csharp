using System.Text;

namespace OnePdf.Layout.Tests;

/// <summary>
/// Short texts repeat across a document, and the layout shares their shaping. These tests check that sharing never mixes up texts
/// that look alike but are set differently, and that the cost of one more short text in a table stays small.
/// </summary>
public class ShortTextCostTests
{
    private static string Content(byte[] pdf, int page)
    {
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfStream s = doc.Pages[page].Dictionary.GetStream("Contents")!;
        return Encoding.Latin1.GetString(s.GetDecodedBytes().Span);
    }

    [Fact]
    public void EqualTextsWithDifferentSettingsAreNotShared()
    {
        var col = new Column { Spacing = 2 };
        col.Items.Add(new Text { Content = "Total", Size = 10, Color = new LayoutColor(1, 0, 0) });
        col.Items.Add(new Text { Content = "Total", Size = 10, Color = new LayoutColor(0, 0, 1), Alignment = HorizontalAlignment.End });
        col.Items.Add(new Text { Content = "Total", Size = 20 });
        col.Items.Add(new Text { Content = "Total", Size = 10, Color = new LayoutColor(1, 0, 0) });
        LayoutDocument doc = Support.Single(col);

        List<Placed> pages = Support.Pages(doc, out _);
        List<Text.Fragment> fragments = Support.Walk(pages[0]).Select(p => p.Data).OfType<Text.Fragment>().ToList();
        Assert.Equal(4, fragments.Count);
        double[] x = [.. fragments.Select(f => f.Block.LineX(0))];
        double[] width = [.. fragments.Select(f => f.Block.LineWidth(0))];

        Assert.Equal(0, x[0], 6);
        Assert.True(x[1] > 100, "the second text ends at the end edge: " + x[1]);
        Assert.Equal(0, x[2], 6);
        Assert.Equal(width[0] * 2, width[2], 6);
        Assert.Equal(width[0], width[1], 6);
        Assert.Equal(x[0], x[3], 6);

        string content = Content(Support.Save(doc), 0);
        Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
        Assert.Contains("0 0 1 rg", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedShapingGivesTheSameLayoutAtAnyWidthAndInAnyOrder()
    {
        // The same label in cells of different widths: each must lay out for its own width, whichever came first.
        var table = new Table { CellPadding = Insets.All(0) };
        table.Columns.Add(TableColumn.Constant(30));
        table.Columns.Add(TableColumn.Constant(200));
        for (int i = 0; i < 3; i++)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell(new Text { Content = "alpha beta gamma", Size = 10 }));
            row.Cells.Add(new TableCell(new Text { Content = "alpha beta gamma", Size = 10 }));
            table.Rows.Add(row);
        }

        List<Placed> pages = Support.Pages(Support.Single(table, margin: 10), out _);
        List<Text.Fragment> fragments = Support.Walk(pages[0]).Select(p => p.Data).OfType<Text.Fragment>().ToList();
        Assert.Equal(6, fragments.Count);
        for (int i = 0; i < 6; i += 2)
        {
            Assert.True(fragments[i].Block.LineCount >= 3, "the narrow cell wraps: " + fragments[i].Block.LineCount);
            Assert.Equal(1, fragments[i + 1].Block.LineCount);
        }
    }

    // Marginal allocation of one more short text, from two sizes of the same document; no timing.
    private static long Allocated(int rows, bool tagged)
    {
        byte[] font = File.ReadAllBytes(Path.Combine(Support.Root(), "src", "OnePdf.Fonts", "Fonts", "LiberationSans-Regular.ttf"));
        string[] words = ["22.12.2023.", "678.784.474", "ISK", "12.500"];
        void Build()
        {
            var doc = LayoutDocument.Compose(d =>
            {
                d.Tagged(tagged);
                d.Fonts.Add(font, "Report Sans");
                d.Fonts.DefaultFamily = "Report Sans";
                d.Page(p =>
                {
                    p.Content().Table(t =>
                    {
                        t.Columns(c =>
                        {
                            c.Relative();
                            c.Relative();
                            c.Relative();
                        });
                        t.CellPadding(0);
                        for (int r = 0; r < rows; r++)
                        {
                            int row = r;
                            t.Row(tr =>
                            {
                                for (int c = 0; c < 3; c++)
                                {
                                    tr.Cell().Text(words[(row + c) % words.Length]).FontSize(8);
                                }
                            });
                        }
                    });
                });
            });
            doc.ToBytes(Support.Fixed);
        }

        Build(); // fonts, JIT and static tables
        long before = GC.GetAllocatedBytesForCurrentThread();
        Build();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void AnotherShortTextInATableCellCostsLittleMemory()
    {
        foreach (bool tagged in new[] { false, true })
        {
            long small = Allocated(150, tagged);
            long large = Allocated(600, tagged);
            double perText = (large - small) / (450.0 * 3);
            TestContext.Current.SendDiagnosticMessage($"tagged {tagged}: {perText:0} bytes per short text");

            // Measured about 2.2 KB untagged and 4.6 KB tagged; the bound leaves room for runtime differences, and is far below the 6.5 and 10.8 KB it replaced.
            Assert.True(perText < (tagged ? 7000 : 3500), $"tagged {tagged}: {perText:0} bytes per text");
        }
    }
}
