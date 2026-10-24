using System.Globalization;

namespace OnePdf.Layout.Tests;

public class TaggingTests
{
    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "theta", "kappa", "lambda", "sigma"];

    private static string Paragraph(int seed, int words)
    {
        var rng = new Random(seed);
        return string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[rng.Next(Words.Length)])) + ".";
    }

    private static Canvas Rule(string? alt = null) => new()
    {
        Height = 10,
        AltText = alt,
        Paint = s =>
        {
            s.Builder.SetFillRgb(0.8, 0.2, 0.2);
            s.Builder.Rectangle(0, 0, s.Width, s.Height);
            s.Builder.Fill();
        },
    };

    /// <summary>
    /// Headings, paragraphs that split across pages, a table that spans pages with a repeated header, a nested list, figures with and without
    /// alternate text, links, a header, a footer with "page X of Y", and decorative boxes.
    /// </summary>
    internal static LayoutDocument Sample(bool pageNumbers = true)
    {
        var body = new Column { Spacing = 8 };
        body.Items.Add(new Heading { Level = 1, Child = new Text { Content = "Annual Report", Size = 20, Weight = 700 } });
        body.Items.Add(new Box { Background = new LayoutColor(0.9, 0.9, 0.9), BorderBottom = new Border(1, LayoutColor.Black), Padding = Insets.All(4), Child = new Text { Content = "Boxed introduction text", Size = 10 } });
        body.Items.Add(new Link { Uri = "https://example.com/report", Child = new Text { Content = "Visit the project page", Size = 10 } });
        body.Items.Add(new Link { Destination = "appendix", Child = new Text { Content = "Jump to the appendix", Size = 10 } });
        for (int i = 0; i < 14; i++)
        {
            body.Items.Add(new Text { Content = $"Paragraph {i}. " + Paragraph(i, 80), Size = 10 });
        }

        body.Items.Add(new Heading { Level = 2, Child = new Text { Content = "Figures", Size = 14, Weight = 700 } });
        body.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain, AltText = "A red square", Name = "Alt image" });
        body.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain, Name = "No alt image" });
        body.Items.Add(new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain, Artifact = true });
        body.Items.Add(Rule());
        body.Items.Add(Rule("A decorative bar that is described"));

        body.Items.Add(new Heading { Level = 2, Child = new Text { Content = "Items", Size = 14, Weight = 700 } });
        var inner = new ItemList { Kind = ListKind.Numbered, NumberStyle = NumberingStyle.LowerLetters };
        inner.Items.Add(new ListItem(new Text { Content = "Nested one", Size = 10 }));
        inner.Items.Add(new ListItem(new Text { Content = "Nested two", Size = 10 }));
        var list = new ItemList { Kind = ListKind.Numbered };
        list.Items.Add(new ListItem(new Text { Content = "First entry", Size = 10 }));
        list.Items.Add(new ListItem(new Column { Items = { new Text { Content = "Second entry with a sublist", Size = 10 }, inner } }));
        list.Items.Add(new ListItem(new Text { Content = "Third entry", Size = 10 }));
        body.Items.Add(list);

        body.Items.Add(new Anchor { Id = "appendix", Child = new Heading { Level = 2, Child = new Text { Content = "Appendix table", Size = 14, Weight = 700 } } });
        Table t = TableTests.Make(70, 3, (r, c) => c == 0 ? $"R{r}" : $"cell {r}/{c} " + Paragraph(r + c, 2 + ((r + c) % 5)), header: 1, footer: 1);
        t.Rows[3].Cells[0] = new TableCell(new Text { Content = "Row header 3", Size = 10 }) { IsHeader = true };
        t.Rows[5].Cells[1] = new TableCell(new Text { Content = "Spanning two columns", Size = 10 }) { ColumnSpan = 2 };
        t.Rows[5].Cells.RemoveAt(2);
        body.Items.Add(t);
        body.Items.Add(new Text { Content = "The end", Size = 10 });

        var doc = new LayoutDocument { Title = "Annual Report", Language = "en-US" };
        doc.Pages.Add(new LayoutPage
        {
            Size = new PageSize(320, 420),
            Margin = Insets.All(30),
            Header = new Box { Padding = new Insets(0, 0, 4, 0), BorderBottom = new Border(0.5, LayoutColor.Black), Child = new Text { Content = "Acme Corp running header", Size = 8 } },
            Footer = !pageNumbers ? new Text { Content = "Confidential", Size = 8 } : new Row { Spacing = 3, Items = { RowItem.Relative(1, new Spacer()), new RowItem(new Text { Content = "Page", Size = 8 }), new RowItem(new PageNumber { Size = 8 }), new RowItem(new Text { Content = "of", Size = 8 }), new RowItem(new PageNumber { Size = 8, Kind = PageNumberKind.DocumentTotal, ReservedDigits = 3 }) } },
            Content = body,
        });
        return doc;
    }

    private static (PdfDocument Doc, SNode Tree, byte[] Bytes, LayoutDocument Layout) Open()
    {
        LayoutDocument layout = Sample();
        byte[] bytes = Support.Save(layout, "tagged-sample");
        PdfDocument doc = PdfDocument.Open(bytes);
        return (doc, TagSupport.ReadTree(doc), bytes, layout);
    }

    [Fact]
    public void EveryPaintIsTaggedOrAnArtifact()
    {
        var (doc, _, _, _) = Open();
        using (doc)
        {
            Assert.True(doc.Pages.Count >= 4, "pages: " + doc.Pages.Count);
            Assert.Empty(TagSupport.Untagged(doc));
        }
    }

    [Fact]
    public void StructureTreeFollowsTheElementTree()
    {
        var (doc, tree, _, layout) = Open();
        using (doc)
        {
            Assert.True(doc.Catalog!.GetDictionary("MarkInfo")!.GetBool("Marked"));
            Assert.Equal("en-US", doc.Catalog.GetString("Lang")?.ToString());
            Assert.Equal("Annual Report", doc.Info.Title);
            Assert.True(doc.Catalog.GetDictionary("ViewerPreferences")!.GetBool("DisplayDocTitle"));
            Assert.Equal("Document", tree.Type);

            // Top level, in order: heading, three paragraph-like blocks of the intro, paragraphs, headings, figures, list, table.
            string[] types = [.. tree.Kids.Select(k => k.Type)];
            Assert.Equal(["H1", "P", "Link", "Link"], types.Take(4));
            Assert.Equal(14, types.Skip(4).Take(14).Count(t => t == "P"));
            Assert.Equal("H2", types[18]);
            Assert.Equal(["Figure", "Figure", "Figure"], types.Skip(19).Take(3));
            Assert.Equal(["H2", "L", "H2", "Table", "P"], types.Skip(22));

            // Boxes, rules, the artifact image and the repeated header/footer are not in the tree.
            Assert.DoesNotContain(tree.Descendants(), n => n.Type is "Artifact" or "Div" or "Box");
            SNode[] figures = [.. tree.Kids.Where(k => k.Type == "Figure")];
            Assert.Equal(3, figures.Length); // alt image, image without alt, canvas with alt text
            Assert.Equal("A red square", figures[0].Alt);
            Assert.Null(figures[1].Alt);
            Assert.Equal("A decorative bar that is described", figures[2].Alt);
            Assert.All(figures, f => Assert.Single(f.Content));
            Assert.Contains(layout.Diagnostics, n => n.Message.Contains("no alternate text", StringComparison.Ordinal));
            Assert.Single(layout.Diagnostics, n => n.Message.Contains("no alternate text", StringComparison.Ordinal));

            // A paragraph split across pages is one element with content on both.
            SNode[] paragraphs = [.. tree.Kids.Where(k => k.Type == "P" && k.Content.Count > 0)];
            Assert.Contains(paragraphs, p => p.Content.Select(c => c.Page).Distinct().Count() > 1);
            Assert.All(paragraphs, p => Assert.Equal(p.Content.Count, p.Content.Count(c => c.Page >= 0)));

            // Headings hold their text directly.
            SNode h1 = tree.Kids[0];
            Assert.Empty(h1.Kids);
            Assert.NotEmpty(h1.Content);
        }
    }

    [Fact]
    public void TableIsTaggedWithHeadersBodyFooterAndSpans()
    {
        var (doc, tree, _, _) = Open();
        using (doc)
        {
            SNode table = tree.Kids.Single(k => k.Type == "Table");
            Assert.True(table.PageSpan() >= 2, "table spans pages: " + table.PageSpan());
            Assert.Equal(["THead", "TBody", "TFoot"], table.Kids.Select(k => k.Type));

            // The header is one row, once: the copies on later pages are artifacts.
            SNode head = table.Kids[0];
            Assert.Single(head.Kids);
            Assert.All(head.Kids[0].Kids, th => Assert.Equal("TH", th.Type));
            Assert.All(head.Kids[0].Kids, th => Assert.Equal("Column", th.Attributes!.GetName("Scope")));

            SNode body = table.Kids[1];
            Assert.Equal(70, body.Kids.Count);
            Assert.All(body.Kids, tr => Assert.Equal("TR", tr.Type));
            Assert.True(body.PageSpan() >= 2);
            Assert.Equal("TH", body.Kids[3].Kids[0].Type);
            Assert.Equal("Row", body.Kids[3].Kids[0].Attributes!.GetName("Scope"));
            Assert.Equal(2, body.Kids[5].Kids.Count);
            Assert.Equal(2, body.Kids[5].Kids[1].Attributes!.GetInt("ColSpan"));

            // The footer is once, on the last page the table is on.
            SNode foot = table.Kids[2];
            Assert.Single(foot.Kids);
            Assert.Equal(table.Descendants().SelectMany(n => n.Content).Max(c => c.Page), foot.Descendants().SelectMany(n => n.Content).Max(c => c.Page));

            // Every row's cells are in column order and hold their text.
            Assert.All(body.Kids, tr => Assert.All(tr.Descendants().Where(n => n.Type is "TD" or "TH"), c => Assert.NotEmpty(c.Descendants().SelectMany(n => n.Content))));
        }
    }

    [Fact]
    public void ListIsTaggedWithLabelsAndBodies()
    {
        var (doc, tree, _, _) = Open();
        using (doc)
        {
            SNode list = tree.Kids.Single(k => k.Type == "L");
            Assert.Equal("Decimal", list.Attributes!.GetName("ListNumbering"));
            Assert.Equal(3, list.Kids.Count);
            Assert.All(list.Kids, li => Assert.Equal(["Lbl", "LBody"], li.Kids.Select(k => k.Type)));
            Assert.All(list.Kids, li => Assert.NotEmpty(li.Kids[0].Content));
            SNode second = list.Kids[1].Kids[1];
            Assert.Equal(["P", "L"], second.Kids.Select(k => k.Type));
            SNode nested = second.Kids[1];
            Assert.Equal("LowerAlpha", nested.Attributes!.GetName("ListNumbering"));
            Assert.Equal(2, nested.Kids.Count);
        }
    }

    [Fact]
    public void LinksAreTiedToTheirElementsAndResolve()
    {
        var (doc, tree, _, _) = Open();
        using (doc)
        {
            SNode[] links = [.. tree.Kids.Where(k => k.Type == "Link")];
            Assert.Equal(2, links.Length);
            Assert.All(links, l => Assert.Single(l.Objects));
            Assert.All(links, l => Assert.NotEmpty(l.Content));

            PdfDictionary uri = links[0].Objects[0];
            Assert.Equal("Link", uri.GetName("Subtype"));
            Assert.Equal("https://example.com/report", uri.GetDictionary("A")!.GetString("URI")!.ToString());
            Assert.Equal("Visit the project page", uri.GetString("Contents")?.ToString());
            Assert.True(uri.GetInt("StructParent", -1) >= 0);

            PdfDictionary jump = links[1].Objects[0];
            string name = jump.GetDictionary("A")!.GetString("D")!.ToString();
            Assert.Equal("appendix", name);
            Assert.Equal("Jump to the appendix", jump.GetString("Contents")?.ToString());
            PdfDestination? dest = doc.GetNamedDestination(name);
            Assert.NotNull(dest);
        }
    }

    [Fact]
    public void ExtractionFollowsTheTreeInLogicalOrder()
    {
        var (doc, _, _, _) = Open();
        using (doc)
        {
            var text = new List<string>();
            bool used = false;
            foreach (OnePdf.Text.PdfPageText t in doc.ExtractText())
            {
                used |= t.UsedStructureTree;
                text.Add(Support.Normalize(t.Text));
            }

            Assert.True(used, "extraction used the structure tree");
            string all = string.Join(" ", text);
            int at = -1;
            foreach (string needle in new[] { "Annual Report", "Boxed introduction", "Visit the project page", "Paragraph 0.", "Paragraph 13.", "Figures", "First entry", "Nested one", "Third entry", "Appendix table", "Row header 3", "Spanning two columns", "R69", "The end" })
            {
                int found = all.IndexOf(needle, at + 1, StringComparison.Ordinal);
                Assert.True(found > at, $"'{needle}' missing or out of order in: {all[..Math.Min(all.Length, 400)]}");
                at = found;
            }

            // The running header is on every page (artifacts are still text a reader sees).
            Assert.Equal(doc.Pages.Count, Regex_Count(all, "Acme Corp running header"));
        }
    }

    private static int Regex_Count(string text, string needle)
    {
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    [Fact]
    public void TaggedOutputIsDeterministicAndUntaggedIsMarkFree()
    {
        byte[] a = Support.Save(Sample());
        byte[] b = Support.Save(Sample());
        Assert.True(a.AsSpan().SequenceEqual(b), "two runs give the same bytes");

        LayoutDocument plain = Sample();
        plain.Options.Tagged = false;
        byte[] bytes = Support.Save(plain);
        using PdfDocument doc = PdfDocument.Open(bytes);
        Assert.Null(doc.StructureTreeRoot);
        Assert.Null(doc.Catalog!.GetDictionary("MarkInfo"));
        Assert.True(bytes.Length < a.Length);
        var finder = new MarkCounter();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            doc.Pages[i].Interpret(finder);
        }

        Assert.Equal(0, finder.Marks);
    }

    private sealed class MarkCounter : PdfDevice
    {
        public int Marks { get; private set; }

        public override void BeginMarkedContent(string tag, PdfDictionary? properties) => Marks++;
    }

    [Fact]
    public void LanguageIsRecordedWhereItDiffers()
    {
        var doc = new LayoutDocument { Language = "en" };
        var body = new Column { Spacing = 6 };
        body.Items.Add(new Text { Content = "English text", Size = 10 });
        body.Items.Add(new Text { Content = "Deutscher Text", Size = 10, Language = "de" });
        body.Items.Add(new Box { Language = "fr", Child = new Column { Items = { new Text { Content = "Un", Size = 10 }, new Text { Content = "Deux", Size = 10, Language = "fr" } } } });
        doc.Pages.Add(new LayoutPage { Content = body });
        byte[] bytes = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(bytes);
        SNode tree = TagSupport.ReadTree(back);
        Assert.Equal(["P", "P", "P", "P"], tree.Kids.Select(k => k.Type));
        Assert.Null(tree.Kids[0].Lang);
        Assert.Equal("de", tree.Kids[1].Lang);
        Assert.Equal("fr", tree.Kids[2].Lang);
        Assert.Equal("fr", tree.Kids[3].Lang); // a box has no structure element of its own, so each paragraph carries the language
    }

    [Fact]
    public void DebugOverlayIsAnArtifact()
    {
        LayoutDocument layout = Sample();
        layout.Options.Debug = true;
        using PdfDocument doc = PdfDocument.Open(Support.Save(layout));
        Assert.Empty(TagSupport.Untagged(doc));
        // The overlay adds no structure: the same tree as without it.
        Assert.Equal(TagSupport.ReadTree(PdfDocument.Open(Support.Save(Sample()))).Shape(), TagSupport.ReadTree(doc).Shape());
    }

    [Fact]
    public void ArtifactPropertyKeepsAnElementOutOfTheTree()
    {
        var body = new Column { Spacing = 6 };
        body.Items.Add(new Text { Content = "Real text", Size = 10 });
        body.Items.Add(new Text { Content = "Page decoration text", Size = 10, Artifact = true });
        body.Items.Add(new Box { Artifact = true, Padding = Insets.All(3), Child = new Column { Items = { new Text { Content = "Inside decoration", Size = 10 }, new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain } } } });
        var doc = Support.Single(body);
        byte[] bytes = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(bytes);
        SNode tree = TagSupport.ReadTree(back);
        Assert.Single(tree.Kids);
        Assert.Empty(TagSupport.Untagged(back));
        Assert.DoesNotContain(doc.Diagnostics, n => n.Message.Contains("alternate", StringComparison.Ordinal));
    }

    [Fact]
    public void PdfA2bSaveHasNoStructureFailures()
    {
        var (doc, _, bytes, _) = Open();
        using (doc)
        {
            using PdfDocument editable = PdfDocument.Open(Support.Save(Sample(pageNumbers: false)), new PdfOpenOptions { AllowEditing = true });
            using var ms = new MemoryStream();
            editable.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
            List<string> failures = TagSupport.Vera(ms.ToArray(), "2b");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        _ = bytes;
    }

    [Fact]
    public void Ua1ProfileListIsInformation()
    {
        var (doc, _, bytes, _) = Open();
        using (doc)
        {
            List<string> failures = TagSupport.Vera(bytes, "ua1");
            string dir = Environment.GetEnvironmentVariable("ONEPDF_LAYOUT_DUMP") ?? string.Empty;
            if (dir.Length > 0)
            {
                File.WriteAllLines(Path.Combine(dir, "ua1-failures.txt"), failures);
            }

            // Information only: the list is read by a person. These rules belong to the PDF/UA unit (identification in the XMP metadata, a
            // title in the XMP metadata) or to the document (the missing alternate text of the second figure is reported on purpose).
            Assert.DoesNotContain(failures, f => f.StartsWith("7.1-", StringComparison.Ordinal) && f.Contains("untagged", StringComparison.OrdinalIgnoreCase));
        }
    }
}
