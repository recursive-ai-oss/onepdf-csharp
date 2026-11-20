namespace OnePdf.Layout.Tests;

/// <summary>ShowOnce, SkipOnce and ShowIf: what repeats, what is hidden, and that hidden content leaves no trace in the file.</summary>
public class ShowOnceTests
{
    private static string[] PageTexts(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return d.ExtractText().Select(t => t.Text).ToArray();
    }

    private static void Fill(Slot content, int lines)
    {
        content.Column(c =>
        {
            for (int i = 0; i < lines; i++)
            {
                c.Item().Text($"Body line {i}").FontSize(12);
            }
        });
    }

    private static LayoutDocument Report(Action<PageComposer>? more = null, int sections = 1)
    {
        return LayoutDocument.Compose(doc =>
        {
            doc.Title("Report").Language("en-US");
            for (int s = 0; s < sections; s++)
            {
                doc.Page(page =>
                {
                    page.Size(300, 200);
                    page.Margin(20);
                    page.Header().ShowOnce().Text("HEAD-ONCE");
                    page.Footer().SkipOnce().Text("FOOT-REPEAT");
                    Fill(page.Content(), 40);
                    more?.Invoke(page);
                });
            }
        });
    }

    [Fact]
    public void ShowOnceHeaderIsOnPageOneAndSkipOnceFooterOnTheOthers()
    {
        string[] pages = PageTexts(Support.Save(Report()));
        Assert.True(pages.Length > 2);
        Assert.Contains("HEAD-ONCE", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("FOOT-REPEAT", pages[0], StringComparison.Ordinal);
        for (int i = 1; i < pages.Length; i++)
        {
            Assert.DoesNotContain("HEAD-ONCE", pages[i], StringComparison.Ordinal);
            Assert.Contains("FOOT-REPEAT", pages[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachSectionStartsOver()
    {
        LayoutDocument doc = Report(sections: 2);
        string[] pages = PageTexts(Support.Save(doc));
        int heads = pages.Count(p => p.Contains("HEAD-ONCE", StringComparison.Ordinal));
        Assert.Equal(2, heads);
        int skipped = pages.Count(p => !p.Contains("FOOT-REPEAT", StringComparison.Ordinal));
        Assert.Equal(2, skipped);
        // The second section's first page is the one after the first section's pages.
        int first = Array.FindIndex(pages, p => p.Contains("HEAD-ONCE", StringComparison.Ordinal));
        int second = Array.FindLastIndex(pages, p => p.Contains("HEAD-ONCE", StringComparison.Ordinal));
        Assert.True(second > first);
        Assert.DoesNotContain("FOOT-REPEAT", pages[second], StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenBandsTakeNoSpace()
    {
        // Same body with and without the hidden header: page one holds the same lines when the header is left out entirely.
        string[] withHidden = PageTexts(Support.Save(LayoutDocument.Compose(doc => doc.Page(p =>
        {
            p.Size(300, 200);
            p.Margin(20);
            p.Header().SkipOnce().Text("HIDDEN");
            Fill(p.Content(), 40);
        }))));
        string[] without = PageTexts(Support.Save(LayoutDocument.Compose(doc => doc.Page(p =>
        {
            p.Size(300, 200);
            p.Margin(20);
            Fill(p.Content(), 40);
        }))));
        Assert.Equal(without[0], withHidden[0]);
        Assert.DoesNotContain("HIDDEN", withHidden[0], StringComparison.Ordinal);
        Assert.Contains("HIDDEN", withHidden[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundAndForegroundFollowTheSameRule()
    {
        string[] pages = PageTexts(Support.Save(Report(p =>
        {
            p.Background().ShowOnce().AlignBottom().Text("BG-ONCE");
            p.Foreground().SkipOnce().AlignBottom().Text("FG-REPEAT");
        })));
        Assert.Contains("BG-ONCE", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("FG-REPEAT", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("BG-ONCE", pages[1], StringComparison.Ordinal);
        Assert.Contains("FG-REPEAT", pages[1], StringComparison.Ordinal);
    }

    private static LayoutDocument TableDoc(bool withParts)
    {
        return LayoutDocument.Compose(doc =>
        {
            doc.Title("Table").Language("en-US");
            doc.Page(page =>
            {
                page.Size(300, 200);
                page.Margin(20);
                page.Content().Table(t =>
                {
                    t.Columns(c => { c.Constant(80); c.Relative(); });
                    t.CellPadding(2);
                    t.HeaderRow(r =>
                    {
                        r.Cell().Text("Id").Bold();
                        r.Cell().Column(c =>
                        {
                            c.Item().Text("Name").Bold();
                            if (withParts)
                            {
                                c.Item().ShowOnce().Text("first-piece-note").FontSize(8);
                                c.Item().SkipOnce().Text("continued-note").FontSize(8);
                            }
                        });
                    });
                    for (int i = 0; i < 30; i++)
                    {
                        int n = i;
                        t.Row(r =>
                        {
                            r.Cell().Text($"{n}");
                            r.Cell().Text($"Item number {n}");
                        });
                    }
                });
            });
        });
    }

    [Fact]
    public void RepeatedTableHeaderRowsShowTheirOncePartsOnlyOnTheFirstPiece()
    {
        string[] pages = PageTexts(Support.Save(TableDoc(true)));
        Assert.True(pages.Length >= 3);
        Assert.Contains("first-piece-note", pages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("continued-note", pages[0], StringComparison.Ordinal);
        for (int i = 1; i < pages.Length; i++)
        {
            Assert.DoesNotContain("first-piece-note", pages[i], StringComparison.Ordinal);
            Assert.Contains("continued-note", pages[i], StringComparison.Ordinal);
            Assert.Contains("Name", pages[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TableMovedToTheNextPageIsStillFirst()
    {
        LayoutDocument doc = LayoutDocument.Compose(d => d.Page(page =>
        {
            page.Size(300, 200);
            page.Margin(20);
            page.Content().Column(c =>
            {
                c.Item().Text("Intro").FontSize(12);
                c.Item().KeepTogether().Table(t =>
                {
                    t.Columns(cc => cc.Relative());
                    t.HeaderRow(r => r.Cell().Column(cc =>
                    {
                        cc.Item().Text("Head");
                        cc.Item().ShowOnce().Text("only-first");
                    }));
                    for (int i = 0; i < 6; i++)
                    {
                        int n = i;
                        t.Row(r => r.Cell().Text($"row {n}").FontSize(14));
                    }
                });
            });
        }));
        string all = string.Join(' ', PageTexts(Support.Save(doc)));
        Assert.Contains("only-first", all, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitElementContinuesWhateverItsWrapperSays()
    {
        // A paragraph that splits over pages is one showing: ShowOnce does not cut it off, and SkipOnce hides it where nothing repeats.
        string words = string.Join(' ', Enumerable.Range(0, 400).Select(i => "w" + i));
        LayoutDocument once = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(300, 200);
            p.Margin(20);
            p.Content().ShowOnce().Text(words).FontSize(10);
        }));
        string[] pages = PageTexts(Support.Save(once));
        Assert.True(pages.Length > 1);
        Assert.Contains("w399", string.Join(' ', pages), StringComparison.Ordinal);

        LayoutDocument skip = LayoutDocument.Compose(d => d.Page(p =>
        {
            p.Size(300, 200);
            p.Content().Column(c =>
            {
                c.Item().Text("kept");
                c.Item().SkipOnce().Text("gone");
            });
        }));
        string text = string.Join(' ', PageTexts(Support.Save(skip)));
        Assert.Contains("kept", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gone", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowIfFalseTakesNoSpaceAndShowIfTrueIsTransparent()
    {
        static string[] Pages(bool? condition)
        {
            return PageTexts(Support.Save(LayoutDocument.Compose(d => d.Page(p =>
            {
                p.Size(300, 200);
                p.Margin(20);
                p.Content().Column(c =>
                {
                    if (condition is { } on)
                    {
                        c.Item().ShowIf(on).Column(h =>
                        {
                            h.Item().Text("COND-A").FontSize(30);
                            h.Item().Text("COND-B").FontSize(30);
                        });
                    }

                    for (int i = 0; i < 20; i++)
                    {
                        c.Item().Text($"Body line {i}").FontSize(12);
                    }
                });
            }))));
        }

        string[] none = Pages(null);
        string[] off = Pages(false);
        string[] on = Pages(true);
        Assert.Equal(none, off);
        Assert.DoesNotContain("COND-A", string.Join(' ', off), StringComparison.Ordinal);
        Assert.Contains("COND-B", on[0], StringComparison.Ordinal);
        Assert.NotEqual(none[0], on[0]);
    }

    [Fact]
    public void HiddenItemsAddNoColumnSpacing()
    {
        static string[] Pages(bool hidden)
        {
            return PageTexts(Support.Save(LayoutDocument.Compose(d => d.Page(p =>
            {
                p.Size(300, 200);
                p.Margin(20);
                p.Content().Column(c =>
                {
                    c.Spacing(10);
                    if (hidden)
                    {
                        c.Item().ShowIf(false).Text("x");
                        c.Item().SkipOnce().Text("y");
                    }

                    for (int i = 0; i < 20; i++)
                    {
                        c.Item().Text($"Body line {i}").FontSize(12);
                    }
                });
            }))));
        }

        Assert.Equal(Pages(false), Pages(true));
    }

    [Fact]
    public void ShowIfPredicateSeesThePage()
    {
        var seen = new List<int>();
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Page(p =>
            {
                p.Size(300, 200);
                p.Margin(20);
                p.Footer().ShowIf(pos => pos.Number % 2 == 0).Text("EVEN-PAGE");
                p.Header().ShowIf(pos =>
                {
                    seen.Add(pos.Index);
                    return pos.IsFirstInSection;
                }).Text("OPENING");
                Fill(p.Content(), 40);
            });
        });
        string[] pages = PageTexts(Support.Save(doc));
        Assert.Contains("OPENING", pages[0], StringComparison.Ordinal);
        for (int i = 0; i < pages.Length; i++)
        {
            Assert.Equal(i % 2 == 1, pages[i].Contains("EVEN-PAGE", StringComparison.Ordinal));
            Assert.Equal(i == 0, pages[i].Contains("OPENING", StringComparison.Ordinal));
        }

        Assert.Contains(pages.Length - 1, seen);
        Assert.Throws<ArgumentNullException>(() => LayoutDocument.Compose(d => d.Page(p => p.Content().ShowIf((Func<PagePosition, bool>)null!).Text("x"))));
    }

    [Fact]
    public void ResultDoesNotDependOnHowOftenTheTreeIsLaidOut()
    {
        LayoutDocument doc = Report();
        Assert.Equal(Support.Save(doc), Support.Save(doc));
    }

    [Fact]
    public void HiddenContentWritesNoStructureAndTheFileIsCompliant()
    {
        LayoutDocument Doc(bool extras) => LayoutDocument.Compose(d =>
        {
            d.Title("Tagged").Language("en-US");
            d.Page(p =>
            {
                p.Size(300, 200);
                p.Margin(20);
                p.Header().Artifact().Text("running head").FontSize(8);
                p.Footer().Artifact().Text("running foot").FontSize(8);
                if (extras)
                {
                    p.Foreground().ShowIf(false).Artifact().Text("NEVER");
                }

                p.Content().Column(c =>
                {
                    c.Item().Heading(1).Text("Title").FontSize(16);
                    if (extras)
                    {
                        c.Item().ShowIf(false).Text("hidden paragraph");
                        c.Item().SkipOnce().Text("hidden by skip");
                    }

                    for (int i = 0; i < 30; i++)
                    {
                        c.Item().Text($"Body line {i}").FontSize(12);
                    }
                });
            });
        });

        byte[] with = Support.Save(Doc(true));
        byte[] without = Support.Save(Doc(false));
        using (PdfDocument a = PdfDocument.Open(with))
        using (PdfDocument b = PdfDocument.Open(without))
        {
            Assert.Equal(TagSupport.ReadTree(b).Descendants().Select(n => n.Type).ToArray(), TagSupport.ReadTree(a).Descendants().Select(n => n.Type).ToArray());
        }

        Assert.Equal(without, with);
        using var ms = new MemoryStream();
        Doc(true).Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        List<string> failures = TagSupport.Vera(ms.ToArray(), "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void RepeatedBandsStayArtifactsWithOnceParts()
    {
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Title("Tagged").Language("en-US");
            d.Page(p =>
            {
                p.Size(300, 200);
                p.Margin(20);
                p.Header().Column(h =>
                {
                    h.Item().Text("Company").FontSize(8);
                    h.Item().SkipOnce().Text("(continued)").FontSize(8);
                });
                p.Footer().ShowOnce().Text("first page only").FontSize(8);
                p.Content().Column(c =>
                {
                    c.Item().Heading(1).Text("Title").FontSize(16);
                    for (int i = 0; i < 40; i++)
                    {
                        c.Item().Text($"Body line {i}").FontSize(12);
                    }
                });
            });
        });
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        byte[] pdf = ms.ToArray();
        string[] pages = PageTexts(pdf);
        Assert.DoesNotContain("(continued)", pages[0], StringComparison.Ordinal);
        Assert.Contains("(continued)", pages[1], StringComparison.Ordinal);
        List<string> failures = TagSupport.Vera(pdf, "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ElementObjectsWorkWithoutTheFacade()
    {
        var section = new LayoutPage
        {
            Size = new PageSize(300, 200),
            Margin = Insets.All(20),
            Header = new ShowOnce { Child = new Text { Content = "OBJ-HEAD", Size = 10 } },
            Content = new Column { Items = { new ShowIf { Condition = false, Child = new Text { Content = "OBJ-OFF" } }, new Text { Content = "OBJ-BODY", Size = 10 } } },
        };
        var doc = new LayoutDocument();
        doc.Pages.Add(section);
        string text = string.Join(' ', PageTexts(Support.Save(doc)));
        Assert.Contains("OBJ-HEAD", text, StringComparison.Ordinal);
        Assert.Contains("OBJ-BODY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("OBJ-OFF", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataReachesInfoAndXmpAndPassesPdfAAndUa()
    {
        LayoutDocument doc = LayoutDocument.Compose(d =>
        {
            d.Title("Metadata").Author("A. Writer").Subject("A subject").Keywords(["alpha", "", "beta", "gamma"]).Language("en-US");
            d.Page(p => p.Content().Heading(1).Text("Metadata"));
        });
        Assert.Equal("alpha, beta, gamma", doc.Keywords);

        using var plain = new MemoryStream();
        doc.Save(plain);
        using (PdfDocument back = PdfDocument.Open(plain.ToArray()))
        {
            Assert.Equal(("A. Writer", "A subject", "alpha, beta, gamma"), (back.Info.Author, back.Info.Subject, back.Info.Keywords));
        }

        using var pdfa = new MemoryStream();
        doc.Save(pdfa, new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        using (PdfDocument back = PdfDocument.Open(pdfa.ToArray()))
        {
            Assert.Equal(("A. Writer", "A subject", "alpha, beta, gamma"), (back.Info.Author, back.Info.Subject, back.Info.Keywords));
            Assert.Equal(["A. Writer"], back.Xmp!.Creators);
            Assert.Equal("A subject", back.Xmp.Description);
            Assert.Equal("alpha, beta, gamma", back.Xmp.Keywords);
        }

        List<string> failures = TagSupport.Vera(pdfa.ToArray(), "2b");
        Assert.True(failures.Count == 0, "2b:\n" + string.Join("\n", failures));

        using var ua = new MemoryStream();
        doc.Save(ua, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        failures = TagSupport.Vera(ua.ToArray(), "ua1");
        Assert.True(failures.Count == 0, "ua1:\n" + string.Join("\n", failures));
        using PdfDocument uaBack = PdfDocument.Open(ua.ToArray());
        Assert.Equal("alpha, beta, gamma", uaBack.Xmp!.Keywords);
    }

    [Fact]
    public void DocumentsWithoutMetadataAreUnchanged()
    {
        byte[] a = Support.Save(Report());
        LayoutDocument doc = Report();
        doc.SetKeywords([]);
        Assert.Null(doc.Keywords);
        Assert.Equal(a, Support.Save(doc));
        Assert.Throws<ArgumentNullException>(() => doc.SetKeywords(null!));
        Assert.Throws<ArgumentNullException>(() => doc.SetKeywords(["a", null!]));
    }
}
