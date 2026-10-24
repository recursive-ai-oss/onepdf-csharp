using System.Diagnostics;

namespace OnePdf.Layout.Tests;

public class RoundTripTests
{
    private static LayoutDocument Report(int rows)
    {
        var body = new Column { Spacing = 8 };
        body.Items.Add(new Text { Content = "Quarterly report", Size = 20, FontName = "Helvetica-Bold", KeepWithNext = true });
        for (int i = 0; i < rows; i++)
        {
            var row = new Row { Spacing = 10 };
            row.Items.Add(RowItem.Constant(60, new Box { Padding = Insets.All(4), Background = new LayoutColor(0.9, 0.9, 1), Child = new Text { Content = $"Item {i}", Size = 10 } }));
            var cell = new Box { Padding = Insets.All(4), Child = new Text { Content = $"Paragraph {i} explains the figures for item {i} in a few plain words.", Size = 10 } };
            cell.SetBorders(new Border(0.5, new LayoutColor(0.4, 0.4, 0.4), BorderStyle.Dashed));
            row.Items.Add(RowItem.Relative(1, cell));
            body.Items.Add(row);
        }

        body.Items.Add(new Box { Width = 64, Height = 64, Child = new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain } });
        body.Items.Add(new Text { Content = "The end", Size = 12 });

        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage
        {
            Size = PageSize.Letter,
            Margin = Insets.All(54),
            Content = body,
            Header = new Box { Padding = new Insets(0, 0, 6, 0), BorderBottom = new Border(0.5, LayoutColor.Black), Child = new Text { Content = "Acme Corp", Size = 9 } },
            Footer = new Text { Content = "Confidential", Size = 9, Alignment = HorizontalAlignment.End },
        });
        return doc;
    }

    [Fact]
    public void MultiPageDocumentOpensExtractsAndIsAcceptedByOtherReaders()
    {
        LayoutDocument layout = Report(60);
        byte[] pdf = Support.Save(layout);
        using PdfDocument doc = PdfDocument.Open(pdf);
        Assert.True(doc.Pages.Count >= 3, "pages: " + doc.Pages.Count);
        Assert.Empty(doc.Diagnostics);

        var text = new List<string>();
        foreach (OnePdf.Text.PdfPageText t in doc.ExtractText())
        {
            text.Add(Support.Normalize(t.Text));
        }

        string all = string.Join(" ", text);
        // Header and footer on every page, and the body in document order.
        Assert.Equal(doc.Pages.Count, text.Count(t => t.Contains("Acme Corp", StringComparison.Ordinal) && t.Contains("Confidential", StringComparison.Ordinal)));
        int at = -1;
        foreach (string needle in new[] { "Quarterly report", "Item 0", "Paragraph 0", "Item 30", "Paragraph 59", "The end" })
        {
            int found = all.IndexOf(needle, at + 1, StringComparison.Ordinal);
            Assert.True(found > at, $"'{needle}' missing or out of order");
            at = found;
        }

        // Sentences stay in reading order inside the cells.
        Assert.Contains("Paragraph 7 explains the figures for item 7 in a few plain words.", all, StringComparison.Ordinal);

        using UglyToad.PdfPig.PdfDocument pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(doc.Pages.Count, pig.NumberOfPages);
        string pigText = string.Join(" ", pig.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        Assert.Contains("Quarterly", pigText, StringComparison.Ordinal);

        string scratch = Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath();
        (int code, string output) = Support.Qpdf(pdf, scratch);
        Assert.True(code == 0, output);
    }

    [Fact]
    public void BackgroundForegroundAndLandscapeSections()
    {
        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage
        {
            Size = PageSize.A4,
            Content = new Text { Content = "portrait", Size = 12 },
            Foreground = new Rotate { Degrees = 45, Child = new Text { Content = "DRAFT", Size = 60, Color = new LayoutColor(0.8, 0.8, 0.8) } },
            Background = new Box { Background = new LayoutColor(1, 1, 0.9), ExpandHeight = true },
        });
        doc.Pages.Add(new LayoutPage { Size = PageSize.A4, Orientation = PageOrientation.Landscape, Content = new Text { Content = "landscape", Size = 12 } });
        byte[] pdf = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Equal(2, back.Pages.Count);
        Assert.True(back.Pages[0].MediaBox.Height > back.Pages[0].MediaBox.Width);
        Assert.True(back.Pages[1].MediaBox.Width > back.Pages[1].MediaBox.Height);
    }

    [Fact]
    public void ThousandSimplePagesPeakMemoryIsReported()
    {
        var col = new Column();
        for (int i = 0; i < 1000; i++)
        {
            col.Items.Add(new Text { Content = $"Page {i}", Size = 14, KeepWithNext = false });
            if (i < 999)
            {
                col.Items.Add(new PageBreak());
            }
        }

        var layout = new LayoutDocument();
        layout.Pages.Add(new LayoutPage { Content = col });
        GC.Collect();
        long before = Process.GetCurrentProcess().WorkingSet64;
        using var ms = new MemoryStream();
        layout.Save(ms, Support.Fixed);
        long peak = Process.GetCurrentProcess().PeakWorkingSet64;
        TestContext.Current.SendDiagnosticMessage($"1000 pages: output {ms.Length} bytes, working set before {before >> 20} MiB, process peak {peak >> 20} MiB");
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        Assert.Equal(1000, back.Pages.Count);
    }

    [Fact]
    public void RuntimeLimitsStopRunawayDocuments()
    {
        var col = new Column();
        for (int i = 0; i < 50; i++)
        {
            col.Items.Add(new PageBreak());
            col.Items.Add(new Spacer { Height = 1 });
        }

        var layout = new LayoutDocument();
        layout.Options.MaxPages = 10;
        layout.Pages.Add(new LayoutPage { Content = col });
        PdfLayoutException ex = Assert.Throws<PdfLayoutException>(() => layout.Save(new MemoryStream()));
        Assert.Contains("limit", ex.Message, StringComparison.Ordinal);
    }
}
