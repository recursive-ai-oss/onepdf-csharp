using System.Text.RegularExpressions;

namespace OnePdf.Layout.Tests;

public class TableOfContentsTests
{
    private static readonly string[] Fill = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "theta", "kappa"];

    private static string Paragraph(int seed, int words)
    {
        var rng = new Random(seed);
        return string.Join(' ', Enumerable.Range(0, words).Select(_ => Fill[rng.Next(Fill.Length)])) + ".";
    }

    private static Heading H(int level, string text) => new() { Level = level, Child = new Text { Content = text, Size = level == 1 ? 16 : 12, Weight = 700 } };

    private static void Chapters(Column body, int chapters, int paragraphs = 3)
    {
        for (int c = 1; c <= chapters; c++)
        {
            body.Items.Add(H(1, $"Chapter {c}"));
            for (int s = 1; s <= 2; s++)
            {
                body.Items.Add(H(2, $"Section {c}.{s}"));
                for (int p = 0; p < paragraphs; p++)
                {
                    body.Items.Add(new Text { Content = Paragraph(c * 100 + s * 10 + p, 40), Size = 10 });
                }
            }
        }
    }

    /// <summary>A front section (roman, or the same section as the body) holding the table, then the body with chapters 1..n.</summary>
    private static LayoutDocument Book(int chapters = 10, bool separateFront = true, Action<TableOfContents>? tune = null, LayoutDirection direction = LayoutDirection.LeftToRight)
    {
        var toc = new TableOfContents { MinLevel = 1, MaxLevel = 2 };
        tune?.Invoke(toc);
        var doc = new LayoutDocument { Direction = direction };
        var body = new Column { Spacing = 6 };
        if (separateFront)
        {
            doc.Pages.Add(new LayoutPage
            {
                Size = new PageSize(300, 300),
                Margin = Insets.All(24),
                NumberStyle = NumberingStyle.LowerRoman,
                Content = new Column { Spacing = 8, Items = { new Text { Content = "Contents", Size = 18, Weight = 700 }, toc } },
                Footer = new PageNumber { Size = 8, Alignment = HorizontalAlignment.Center },
            });
        }
        else
        {
            body.Items.Add(new Text { Content = "Contents", Size = 18, Weight = 700 });
            body.Items.Add(toc);
            body.Items.Add(new PageBreak());
        }

        Chapters(body, chapters);
        doc.Pages.Add(new LayoutPage
        {
            Size = new PageSize(300, 300),
            Margin = Insets.All(24),
            StartNumber = separateFront ? 1 : null,
            Content = body,
            Footer = new PageNumber { Size = 8, Alignment = HorizontalAlignment.Center },
        });
        return doc;
    }

    private static string[] PageTexts(PdfDocument doc)
    {
        var texts = new string[doc.Pages.Count];
        foreach (OnePdf.Text.PdfPageText t in doc.ExtractText())
        {
            texts[t.PageIndex] = t.Text;
        }

        return texts;
    }

    // The page a heading starts on: the first page after the table whose text has the heading on a line of its own.
    private static int PageOfHeading(string[] texts, string title, int after) =>
        Enumerable.Range(after, texts.Length - after).First(i => texts[i].Split('\n').Any(l => l.Trim() == title));

    private static (int Front, string[] Texts, PdfDocument Doc) Open(LayoutDocument layout, bool separate = true)
    {
        PdfDocument doc = PdfDocument.Open(Support.Save(layout));
        string[] texts = PageTexts(doc);
        // The table pages are those before the first page that has body text.
        int front = Enumerable.Range(0, texts.Length).First(i => Fill.Any(f => texts[i].Contains(f, StringComparison.Ordinal)));
        return (front, texts, doc);
    }

    private static List<(string Title, string Label)> TocLines(string[] texts, int front)
    {
        var entries = new List<(string, string)>();
        var rx = new Regex(@"^(?<t>(Chapter|Section) [\d.]+)[\s.]*?\s(?<n>[ivxlc]+|[A-Z]-\d+|\d+)$");
        for (int i = 0; i < front; i++)
        {
            foreach (string line in texts[i].Split('\n'))
            {
                Match m = rx.Match(line.Trim());
                if (m.Success)
                {
                    entries.Add((m.Groups["t"].Value, m.Groups["n"].Value));
                }
            }
        }

        return entries;
    }

    [Fact]
    public void NumbersAreTheLabelsOfTheHeadingPagesWhenTheTableSpansPagesAndHasItsOwnNumbering()
    {
        (int front, string[] texts, PdfDocument doc) = Open(Book(14));
        using (doc)
        {
            Assert.True(front >= 2, "the table should span more than one page, it spans " + front);
            List<(string Title, string Label)> entries = TocLines(texts, front);
            Assert.Equal(14 * 3, entries.Count);
            foreach ((string title, string label) in entries)
            {
                int page = PageOfHeading(texts, title, front);
                Assert.Equal(doc.Pages[page].Label, label);
                Assert.Equal((page - front + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), label);
            }

            // The front pages are numbered in roman numerals in the page labels, and the first body page is 1.
            Assert.Equal("i", doc.Pages[0].Label);
            Assert.Equal("1", doc.Pages[front].Label);
        }
    }

    [Fact]
    public void NumbersIncludeTheTableWhenItShiftsTheContentOfItsOwnSection()
    {
        (int front, string[] texts, PdfDocument doc) = Open(Book(12, separateFront: false), false);
        using (doc)
        {
            Assert.True(front >= 2);
            List<(string Title, string Label)> entries = TocLines(texts, front);
            Assert.Equal(12 * 3, entries.Count);
            foreach ((string title, string label) in entries)
            {
                int page = PageOfHeading(texts, title, front);
                Assert.Equal((page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), label);
            }

            // The first chapter starts after the table, not on page 1.
            Assert.NotEqual("1", entries[0].Label);
        }
    }

    [Fact]
    public void PrefixedLabelsAreShownAsViewersShowThem()
    {
        LayoutDocument layout = Book(6);
        layout.Pages[1].LabelPrefix = "A-";
        (int front, string[] texts, PdfDocument doc) = Open(layout);
        using (doc)
        {
            List<(string Title, string Label)> entries = TocLines(texts, front);
            Assert.Equal(18, entries.Count);
            Assert.All(entries, e => Assert.Matches(@"^A-\d+$", e.Label));
            Assert.Equal("A-1", entries[0].Label);
            foreach ((string title, string label) in entries)
            {
                Assert.Equal(doc.Pages[PageOfHeading(texts, title, front)].Label, label);
            }
        }
    }

    [Fact]
    public void LevelsFilterAndHeadingOptionsChooseTheEntries()
    {
        LayoutDocument layout = Book(4, tune: t => { t.MinLevel = 1; t.MaxLevel = 1; });
        var body = (Column)layout.Pages[1].Content!;
        body.Items.Add(new Heading { Level = 1, InToc = false, Child = new Text { Content = "Chapter hidden", Weight = 700 } });
        body.Items.Add(new Heading { Level = 1, TocTitle = "Short", Child = new Text { Content = "A heading with a very long title that is not shown in the table", Weight = 700 } });
        body.Items.Add(new TocEntry { Title = "Explicit one", Level = 1, Child = new Text { Content = "Marked by hand" } });
        body.Items.Add(new TocEntry { Level = 2, Child = new Text { Content = "Deep entry" } });
        (int front, string[] texts, PdfDocument doc) = Open(layout);
        using (doc)
        {
            string toc = string.Join('\n', texts.Take(front));
            Assert.Contains("Chapter 1", toc, StringComparison.Ordinal);
            Assert.DoesNotContain("Section 1.1", toc, StringComparison.Ordinal);
            Assert.DoesNotContain("Chapter hidden", toc, StringComparison.Ordinal);
            Assert.DoesNotContain("A heading with", toc, StringComparison.Ordinal);
            Assert.Contains("Short", toc, StringComparison.Ordinal);
            Assert.Contains("Explicit one", toc, StringComparison.Ordinal);
            Assert.DoesNotContain("Deep entry", toc, StringComparison.Ordinal);

            int last = texts.Length - 1;
            Assert.Matches(@"Explicit one[\s.]*\s" + (last - front + 1) + @"\b", toc.Replace('\n', ' '));
        }
    }

    [Fact]
    public void LevelTwoEntriesAreExplicitlyListedWhenTheRangeAllowsThem()
    {
        LayoutDocument layout = Book(2, tune: t => t.MaxLevel = 3);
        ((Column)layout.Pages[1].Content!).Items.Add(new TocEntry { Level = 3, Child = new Text { Content = "Third level text" } });
        (int front, string[] texts, PdfDocument doc) = Open(layout);
        using (doc)
        {
            Assert.Contains("Third level text", string.Join('\n', texts.Take(front)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LinksGoToTheHeadings()
    {
        (int front, string[] texts, PdfDocument doc) = Open(Book(10));
        using (doc)
        {
            var links = doc.Pages.Take(front).SelectMany(p => p.Links).ToList();
            Assert.Equal(10 * 3, links.Count);
            foreach (PdfAnnotation l in links)
            {
                string title = l.Contents!;
                PdfDestination dest = l.Action!.Destination!;
                Assert.Equal(PageOfHeading(texts, title, front), dest.PageIndex);
                Assert.NotNull(dest.Top);
            }

            // The target sits at the top of the heading: its words are just under the destination.
            PdfAnnotation chapter = links.First(l => l.Contents == "Chapter 3");
            OnePdf.Text.PdfPageText page = doc.Pages[chapter.Action!.Destination!.PageIndex].ExtractText();
            OnePdf.Text.PdfTextWord word = page.Words.First(w => w.Text == "Chapter");
            Assert.InRange(chapter.Action.Destination.Top!.Value - word.Bounds.Top, -1, 6);
        }
    }

    [Fact]
    public void LongTitlesWrapAndTheNumberSitsOnTheLastLine()
    {
        LayoutDocument layout = Book(3, tune: t => t.MaxLevel = 1);
        ((Column)layout.Pages[1].Content!).Items.Add(new Heading
        {
            Level = 1,
            Child = new Text { Content = "Closing remarks about everything that was said and a few things that were not", Weight = 700 },
        });
        using PdfDocument doc = PdfDocument.Open(Support.Save(layout));
        OnePdf.Text.PdfPageText page = doc.Pages[0].ExtractText();
        var words = page.Words.ToList();
        OnePdf.Text.PdfTextWord first = words.First(w => w.Text == "Closing");
        OnePdf.Text.PdfTextWord lastWord = words.First(w => w.Text == "not");
        OnePdf.Text.PdfTextWord number = words.Last(w => Regex.IsMatch(w.Text, @"^\d+$") && w.Bounds.Bottom < first.Bounds.Bottom + 1 && w.Bounds.Top <= lastWord.Bounds.Top + 1 && w.Bounds.Right > 200);
        Assert.True(lastWord.Bounds.Top < first.Bounds.Top - 3, "the title wraps onto a second line");
        Assert.InRange(Math.Abs(number.Bounds.Bottom - lastWord.Bounds.Bottom), 0, 1);
        Assert.True(number.Bounds.Right > 250, "the number is at the end of the line");
        Assert.True(lastWord.Bounds.Right < number.Bounds.Left);
    }

    [Fact]
    public void LeaderDotsFillTheSpaceAndLineUp()
    {
        (int front, _, PdfDocument doc) = Open(Book(6));
        using (doc)
        {
            OnePdf.Text.PdfPageText page = doc.Pages[0].ExtractText(new OnePdf.Text.PdfTextOptions { RetainGlyphs = true });
            var dots = page.Glyphs.Where(g => g.Text == ".").ToList();
            Assert.True(dots.Count > 20);
            // Every dot's left edge is a multiple of one pitch from the right margin: the same few columns on every line.
            var columns = dots.Select(g => Math.Round(g.Bounds.Left, 1)).Distinct().Count();
            Assert.True(columns < 40, "dots line up between lines: " + columns);
        }
    }

    [Fact]
    public void NoLeaderLeavesNoDots()
    {
        (_, string[] texts, PdfDocument doc) = Open(Book(4, tune: t => t.Leader = null));
        using (doc)
        {
            Assert.DoesNotContain("..", texts[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LevelStylesChangeSizeAndFont()
    {
        LayoutDocument layout = Book(3, tune: t =>
        {
            t.Style.Size = 10;
            t.LevelStyle(1).Weight = 700;
            t.LevelStyle(1).Size = 14;
            t.LevelStyle(2).SpaceBefore = 4;
        });
        using PdfDocument doc = PdfDocument.Open(Support.Save(layout));
        OnePdf.Text.PdfPageText page = doc.Pages[0].ExtractText(new OnePdf.Text.PdfTextOptions { RetainGlyphs = true });
        double chapterHeight = page.Words.First(w => w.Text == "Chapter").Bounds.Height;
        double sectionHeight = page.Words.First(w => w.Text == "Section").Bounds.Height;
        Assert.True(chapterHeight > sectionHeight + 1.5, $"{chapterHeight} vs {sectionHeight}");
    }

    [Fact]
    public void RightToLeftPutsTheNumbersOnTheLeft()
    {
        LayoutDocument layout = Book(3, direction: LayoutDirection.RightToLeft);
        using PdfDocument doc = PdfDocument.Open(Support.Save(layout));
        OnePdf.Text.PdfPageText page = doc.Pages[0].ExtractText();
        OnePdf.Text.PdfTextWord title = page.Words.First(w => w.Text == "Chapter");
        OnePdf.Text.PdfTextWord number = page.Words.First(w => w.Text == "i" || Regex.IsMatch(w.Text, @"^\d+$") && w.Bounds.Bottom - title.Bounds.Bottom is > -2 and < 2 && w.Bounds.Left < 100);
        Assert.True(number.Bounds.Left < 60, "number at the left: " + number.Bounds.Left);
        Assert.True(title.Bounds.Right > 200, "title at the right: " + title.Bounds.Right);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        Assert.Equal(Support.Save(Book(9)), Support.Save(Book(9)));
    }

    [Fact]
    public void InMemoryRenderingFillsTheNumbersToo()
    {
        LayoutDocument layout = Book(8);
        using PdfDocument target = PdfDocument.CreateEmpty();
        layout.Render(target);
        using var ms = new MemoryStream();
        target.Save(ms, new PdfSaveOptions());
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        string[] texts = PageTexts(back);
        int front = Enumerable.Range(0, texts.Length).First(i => Fill.Any(f => texts[i].Contains(f, StringComparison.Ordinal)));
        List<(string Title, string Label)> entries = TocLines(texts, front);
        Assert.Equal(24, entries.Count);
        foreach ((string title, string label) in entries)
        {
            Assert.Equal((PageOfHeading(texts, title, front) - front + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), label);
        }
    }

    [Fact]
    public void ADocumentWithoutAnyHeadingGetsAnEmptyTable()
    {
        var doc = new LayoutDocument();
        doc.Pages.Add(new LayoutPage { Content = new Column { Items = { new TableOfContents(), new Text { Content = "Just text" } } } });
        using PdfDocument back = PdfDocument.Open(Support.Save(doc));
        Assert.Single(back.Pages.ToList());
        Assert.Contains("Just text", back.Pages[0].ExtractText().Text, StringComparison.Ordinal);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void ATableInsideAComponentIsFound()
    {
        var layout = Book(5);
        var front = (Column)layout.Pages[0].Content!;
        TableOfContents toc = front.Items.OfType<TableOfContents>().Single();
        front.Items.Remove(toc);
        front.Items.Add(new Wrapper(toc));
        (int f, string[] texts, PdfDocument doc) = Open(layout);
        using (doc)
        {
            Assert.Equal(15, TocLines(texts, f).Count);
        }
    }

    private sealed class Wrapper(Element inner) : Component
    {
        protected override Element Compose() => new Box { Padding = Insets.All(2), Child = inner };
    }

    [Fact]
    public void ATableTheSearchCannotFindIsReportedAndCanBeForced()
    {
        var layout = Book(5);
        layout.Options.TableOfContents = false;
        using PdfDocument doc = PdfDocument.Open(Support.Save(layout));
        Assert.Contains(layout.Diagnostics, d => d.Message.Contains("table of contents", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFluentFacadeBuildsATableWithLevelsAndLeader()
    {
        LayoutDocument layout = LayoutDocument.Compose(d =>
        {
            d.Title("Guide").Language("en");
            d.Page(p =>
            {
                p.Size(300, 300);
                p.NumberStyle(NumberingStyle.LowerRoman);
                p.Content().Column(c =>
                {
                    c.Item().Text("Contents").Bold();
                    c.Item().TableOfContents(t => t.Levels(1, 1).Leader("_").Indent(10).Level(1, s => s.Weight = 700));
                });
            });
            d.Page(p =>
            {
                p.Size(300, 300);
                p.StartNumber(1);
                p.Content().Column(c =>
                {
                    c.Item().Heading(1).Text("One").Bold();
                    c.Item().Text("Body of one");
                    c.Item().PageBreak();
                    c.Item().Heading(1, true, "Two, shortened").Text("Two with a long title").Bold();
                    c.Item().Heading(2).Text("Not listed");
                    c.Item().Heading(1, false).Text("Hidden");
                    c.Item().TocEntry("Marked", 1).Text("Marked by hand");
                });
            });
        });
        using PdfDocument doc = PdfDocument.Open(Support.Save(layout));
        string toc = doc.Pages[0].ExtractText().Text;
        Assert.Matches(@"One[\s_]*\s1\b", toc);
        Assert.Matches(@"Two, shortened[\s_]*\s2\b", toc);
        Assert.Matches(@"Marked[\s_]*\s2\b", toc);
        Assert.DoesNotContain("Hidden", toc, StringComparison.Ordinal);
        Assert.DoesNotContain("Not listed", toc, StringComparison.Ordinal);
    }

    [Fact]
    public void StructureIsTocTociReferenceLink()
    {
        (int front, _, PdfDocument doc) = Open(Book(8));
        using (doc)
        {
            SNode root = TagSupport.ReadTree(doc);
            var tocs = root.Descendants().Where(n => n.Type == "TOC").ToList();
            Assert.Single(tocs);
            List<SNode> items = tocs[0].Kids;
            Assert.Equal(24, items.Count);
            Assert.All(items, i => Assert.Equal("TOCI", i.Type));
            Assert.All(items, i => Assert.Equal("Reference(Link(*))", i.Kids.Count == 1 ? i.Kids[0].Shape() : "?"));
            Assert.True(tocs[0].PageSpan() >= 2);
            Assert.Equal(front, tocs[0].Descendants().SelectMany(n => n.Content).Select(c => c.Page).Max() + 1);

            // Every link annotation is tied to its Link element.
            Assert.All(items, i => Assert.Single(i.Kids[0].Kids[0].Objects));
            Assert.Empty(TagSupport.Untagged(doc));
        }
    }

    [Fact]
    public void ManyEntriesSettle()
    {
        LayoutDocument layout = Book(60, tune: t => t.MaxLevel = 2);
        (int front, string[] texts, PdfDocument doc) = Open(layout);
        using (doc)
        {
            Assert.True(front >= 5);
            List<(string Title, string Label)> entries = TocLines(texts, front);
            Assert.Equal(180, entries.Count);
            foreach ((string title, string label) in entries)
            {
                Assert.Equal((PageOfHeading(texts, title, front) - front + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), label);
            }
        }

        Assert.DoesNotContain(layout.Diagnostics, d => d.Message.Contains("settle", StringComparison.Ordinal));
    }

    [Fact]
    public void TableOfContentsPassesPdfUaAndPdfA2a()
    {
        LayoutDocument Make()
        {
            LayoutDocument l = Book(8);
            l.Title = "Book";
            l.Language = "en";
            return l;
        }

        using var ua = new MemoryStream();
        Make().Save(ua, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        List<string> failures = TagSupport.Vera(ua.ToArray(), "ua1");
        Assert.True(failures.Count == 0, "ua1:\n" + string.Join("\n", failures));

        using var a = new MemoryStream();
        Make().Save(a, new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.A } });
        failures = TagSupport.Vera(a.ToArray(), "2a");
        Assert.True(failures.Count == 0, "2a:\n" + string.Join("\n", failures));
    }
}
