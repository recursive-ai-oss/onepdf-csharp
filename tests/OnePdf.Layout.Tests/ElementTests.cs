namespace OnePdf.Layout.Tests;

public class ElementTests
{
    private static Spacer Block(double h, string? name = null) => new() { Height = h, Name = name };

    [Fact]
    public void ColumnSplitsBetweenItemsAndKeepsOrder()
    {
        var col = new Column();
        for (int i = 0; i < 10; i++)
        {
            col.Items.Add(Block(100, "B" + i));
        }

        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        // Body is 360 high: three 100 pt blocks per page.
        Assert.Equal(4, pages.Count);
        Assert.Equal([3, 3, 3, 1], pages.Select(p => p.Children.Count));
        string[] order = [.. pages.SelectMany(p => p.Children.Select(c => c.Element.Name!))];
        Assert.Equal(Enumerable.Range(0, 10).Select(i => "B" + i), order);
    }

    [Fact]
    public void SpacingIsNotAddedAtPageTop()
    {
        var col = new Column { Spacing = 10 };
        for (int i = 0; i < 4; i++)
        {
            col.Items.Add(Block(150));
        }

        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        Assert.Equal(2, pages.Count);
        Assert.Equal(0, pages[1].Children[0].Y);
        Assert.Equal(160, pages[0].Children[1].Y);
    }

    [Fact]
    public void RowSharesWidthAndFollowsDirection()
    {
        Row Make() => new()
        {
            Spacing = 10,
            Items = { RowItem.Constant(50, Block(10, "fixed")), RowItem.Relative(1, Block(10, "a")), RowItem.Relative(3, Block(10, "b")) },
        };

        var doc = Support.Single(Make(), new PageSize(300, 400), margin: 0);
        Placed row = Support.Pages(doc, out _)[0].Children[0];
        Assert.Equal(0, row.Children[0].X);
        Assert.Equal(60, row.Children[1].X);
        // (300 - 20 gaps - 50 fixed) = 230 shared 1:3.
        Assert.Equal(57.5, row.Children[1].Width, 6);
        Assert.Equal(172.5, row.Children[2].Width, 6);

        var rtl = Support.Single(Make(), margin: 0, setup: p => p.Direction = LayoutDirection.RightToLeft);
        Placed mirrored = Support.Pages(rtl, out _)[0].Children[0];
        Assert.Equal(250, mirrored.Children[0].X, 6);
        Assert.Equal(300 - 60 - mirrored.Children[1].Width, mirrored.Children[1].X, 6);
    }

    [Fact]
    public void BoxSizesAndAlignment()
    {
        var box = new Box
        {
            Margin = Insets.All(5),
            Padding = Insets.All(10),
            Width = 100,
            Height = 60,
            HorizontalAlignment = HorizontalAlignment.End,
            VerticalAlignment = VerticalAlignment.Middle,
            Child = new Spacer { Width = 20, Height = 10 },
        };
        Placed p = Support.Pages(Support.Single(box), out _)[0].Children[0];
        Assert.Equal(110, p.Width);
        Assert.Equal(70, p.Height);
        Placed child = p.Children[0];
        // Inner area is 80 x 40 starting at (15, 15); child 20 x 10 at end / middle.
        Assert.Equal(15 + 60, child.X, 6);
        Assert.Equal(15 + 15, child.Y, 6);

        var ratio = new Box { AspectRatio = 2, Width = 100 };
        Assert.Equal(50, Support.Pages(Support.Single(ratio), out _)[0].Children[0].Height, 6);
    }

    [Fact]
    public void LogicalInsetsFollowDirection()
    {
        Box Make() => new() { PaddingStart = 30, Child = new Spacer { Width = 10, Height = 5 } };
        Placed ltr = Support.Pages(Support.Single(Make(), margin: 0), out _)[0].Children[0];
        Assert.Equal(30, ltr.Children[0].X);
        Placed rtl = Support.Pages(Support.Single(Make(), margin: 0, setup: p => p.Direction = LayoutDirection.RightToLeft), out _)[0].Children[0];
        // Start is the right edge: 30 points of padding sit there and the child, at the start of what is left, ends beside it.
        Assert.Equal(260, rtl.Children[0].X);
    }

    [Fact]
    public void BoxSplitsWithItsChildAndKeepsOuterEdgesOnly()
    {
        var col = new Column();
        for (int i = 0; i < 8; i++)
        {
            col.Items.Add(Block(100));
        }

        var box = new Box { Padding = Insets.All(10), Margin = Insets.All(4), Child = col };
        List<Placed> pages = Support.Pages(Support.Single(box), out _);
        Assert.Equal(3, pages.Count);
        var first = (Box.BoxData)pages[0].Children[0].Data!;
        var last = (Box.BoxData)pages[^1].Children[0].Data!;
        Assert.True(first.First && !first.Last);
        Assert.True(!last.First && last.Last);
        Assert.Equal(314, pages[0].Children[0].Height, 6);
    }

    [Fact]
    public void PageBreakStartsANewPageAndIsIgnoredAtTheTop()
    {
        var col = new Column();
        col.Items.Add(new PageBreak());
        col.Items.Add(Block(10, "a"));
        col.Items.Add(new PageBreak());
        col.Items.Add(Block(10, "b"));
        col.Items.Add(new PageBreak());
        col.Items.Add(new PageBreak());
        col.Items.Add(Block(10, "c"));
        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        Assert.Equal(["a", "b", "c"], pages.Select(p => p.Children.Single().Element.Name));
    }

    [Fact]
    public void EnsureSpaceMovesToNextPage()
    {
        var col = new Column();
        col.Items.Add(Block(300, "a"));
        col.Items.Add(new EnsureSpace { Height = 100, Child = Block(10, "b") });
        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        Assert.Equal(2, pages.Count);
        Assert.Equal("b", pages[1].Children[0].Element.Name);
    }

    [Fact]
    public void KeepWithNextMovesTheHeadingWithItsParagraph()
    {
        var col = new Column();
        col.Items.Add(Block(300, "filler"));
        col.Items.Add(new Spacer { Height = 20, Name = "heading", KeepWithNext = true });
        col.Items.Add(Block(50, "para"));
        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        Assert.Equal(2, pages.Count);
        Assert.Equal(["heading", "para"], pages[1].Children.Select(c => c.Element.Name));
    }

    [Fact]
    public void KeepTogetherMovesWholeAndSplitsWithANoteWhenTooTall()
    {
        Column Group(int n, double h)
        {
            var g = new Column();
            for (int i = 0; i < n; i++)
            {
                g.Items.Add(Block(h));
            }

            return g;
        }

        var col = new Column();
        col.Items.Add(Block(200));
        col.Items.Add(new KeepTogether { Child = Group(3, 60) });
        List<Placed> pages = Support.Pages(Support.Single(col), out List<LayoutDiagnostic> notes);
        Assert.Equal(2, pages.Count);
        Assert.Empty(notes);
        Assert.Equal(3, Support.Walk(pages[1]).Count(p => p.Element is Spacer));

        var tall = new KeepTogether { Child = Group(8, 100) };
        pages = Support.Pages(Support.Single(tall), out notes);
        Assert.Equal(3, pages.Count);
        Assert.Contains(notes, n => n.Message.Contains("kept together", StringComparison.Ordinal));
    }

    [Fact]
    public void ImageThatCannotFitAnyPageRaisesWithPathAndSizes()
    {
        using PdfDocument target = PdfDocument.CreateEmpty();
        PdfCreatedImage img = target.AddRawImage(10, 100, PdfImageColor.Gray, new byte[1000]);
        var col = new Column();
        col.Items.Add(Block(10));
        col.Items.Add(Block(10));
        col.Items.Add(new Image { Created = img, Fit = ImageFit.FitWidth });
        // 260 wide page body, so the image is 260 x 2600 on a 360 pt body.
        PdfLayoutException ex = Assert.Throws<PdfLayoutException>(() => Support.Single(col).Render(target));
        Assert.Equal("Page[1] > Content > Column > Item 2 > Image", ex.ElementPath);
        Assert.Equal(260, ex.AvailableWidth, 6);
        Assert.Equal(2600, ex.RequestedHeight, 6);
        Assert.Contains("Page[1] > Content > Column > Item 2 > Image", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedElementsAppearInPaths()
    {
        var col = new Column { Name = "Body" };
        col.Items.Add(Block(20));
        col.Items.Add(new Box { Name = "Card", Height = 9999 });
        PdfLayoutException ex = Assert.Throws<PdfLayoutException>(() => Support.Single(col).Save(new MemoryStream()));
        Assert.Equal("Page[1] > Content > Body > Item 1 > Card", ex.ElementPath);
    }

    [Fact]
    public void ImageFitModes()
    {
        using PdfDocument target = PdfDocument.CreateEmpty();
        PdfCreatedImage img = target.AddRawImage(40, 20, PdfImageColor.Gray, new byte[800]);
        Placed Place(ImageFit fit, double w, double h)
        {
            var box = new Box { Width = w, Height = h, Child = new Image { Created = img, Fit = fit } };
            var bodies = new List<Placed>();
            Support.Single(box, margin: 0).RenderCore(target, (_, _, body, _, _) => bodies.Add(body!));
            return bodies[0].Children[0].Children[0];
        }

        Placed fw = Place(ImageFit.FitWidth, 100, 100);
        Assert.Equal((100, 50), (fw.Width, fw.Height));
        Placed contain = Place(ImageFit.Contain, 100, 100);
        var cd = (Image.Data)contain.Data!;
        Assert.Equal((100, 50, 0, 25), (cd.W, cd.H, cd.X, cd.Y));
        Placed cover = Place(ImageFit.Cover, 100, 100);
        var cv = (Image.Data)cover.Data!;
        Assert.Equal((200, 100, -50, 0), (cv.W, cv.H, cv.X, cv.Y));
        Assert.True(cv.Clip);
        Placed stretch = Place(ImageFit.Stretch, 100, 100);
        var sd = (Image.Data)stretch.Data!;
        Assert.Equal((100, 100), (sd.W, sd.H));
        Placed fh = Place(ImageFit.FitHeight, 300, 100);
        Assert.Equal((200, 100), (fh.Width, fh.Height));
    }

    [Fact]
    public void JpegImagesAreAddedOnceAndDrawn()
    {
        var col = new Column();
        col.Items.Add(new Box { Width = 64, Height = 64, Child = new Image { Jpeg = Support.Jpeg(), Fit = ImageFit.Contain } });
        byte[] pdf = Support.Save(Support.Single(col));
        using PdfDocument back = PdfDocument.Open(pdf);
        var list = new PdfDisplayList();
        back.Pages[0].Interpret(list);
        Assert.Contains("image", list.ToText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransformsGiveBoundingBoxes()
    {
        var rot = new Rotate { Degrees = 90, Child = new Spacer { Width = 100, Height = 20 } };
        Placed p = Support.Pages(Support.Single(rot), out _)[0].Children[0];
        Assert.Equal(20, p.Width, 6);
        Assert.Equal(100, p.Height, 6);
        var scale = new Scale { X = 2, Y = 0.5, Child = new Spacer { Width = 50, Height = 40 } };
        Placed s = Support.Pages(Support.Single(scale), out _)[0].Children[0];
        Assert.Equal((100, 20), (s.Width, s.Height));
        var tr = new Translate { X = 7, Y = 3, Child = Block(10) };
        Placed t = Support.Pages(Support.Single(tr), out _)[0].Children[0];
        Assert.Equal(10, t.Height);
        Assert.Equal((7, 3), (t.Children[0].X, t.Children[0].Y));
    }

    [Fact]
    public void LayersSizeFromThePrimary()
    {
        var layers = new Layers
        {
            PrimaryIndex = 1,
            Items = { new Box { ExpandHeight = true, Background = new LayoutColor(1, 0, 0) }, Block(80, "main") },
        };
        Placed p = Support.Pages(Support.Single(layers), out _)[0].Children[0];
        Assert.Equal(80, p.Height);
        Assert.Equal(80, p.Children[0].Height, 6);
    }

    [Fact]
    public void ComponentsComposeOnce()
    {
        var c = new Greeting();
        var col = new Column { Items = { c, c } };
        Placed body = Support.Pages(Support.Single(col), out _)[0];
        Assert.Equal(2, body.Children.Count);
        Assert.Equal(1, c.Composed);
    }

    private sealed class Greeting : Component
    {
        public int Composed { get; private set; }

        protected override Element Compose()
        {
            Composed++;
            return new Box { Height = 12, Padding = Insets.All(1) };
        }
    }

    [Fact]
    public void TextWrapsSplitsAndHonoursWidowsAndOrphans()
    {
        string para = string.Join(" ", Enumerable.Repeat("word", 400));
        var col = new Column { Items = { new Text { Content = para, Size = 10 } } };
        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        Assert.True(pages.Count > 1);
        var frags = pages.Select(p => (Text.Fragment)p.Children[0].Data!).ToList();
        int next = 0;
        foreach (Text.Fragment f in frags)
        {
            Assert.Equal(next, f.First);
            Assert.True(f.Count >= 2, "no orphan or widow of one line");
            next += f.Count;
        }

        Assert.Equal(frags[0].Block.LineCount, next);
    }

    [Fact]
    public void WidowControlPullsALineFromThePreviousPage()
    {
        // 10 pt text, 12 pt lines, body 360 = 30 lines. A 31-line paragraph would leave 1 widow: 29 + 2 instead.
        string para = string.Join("\n", Enumerable.Range(0, 31).Select(i => "line " + i));
        var col = new Column { Items = { new Text { Content = para, Size = 10 } } };
        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        Assert.Equal(2, pages.Count);
        Assert.Equal(29, ((Text.Fragment)pages[0].Children[0].Data!).Count);
        Assert.Equal(2, ((Text.Fragment)pages[1].Children[0].Data!).Count);
    }

    [Fact]
    public void OrphanControlMovesAParagraphStartingOnTheLastLine()
    {
        var col = new Column();
        col.Items.Add(Block(348));
        col.Items.Add(new Text { Content = "a\nb\nc\nd\ne", Size = 10 });
        List<Placed> pages = Support.Pages(Support.Single(col), out _);
        Assert.Equal(2, pages.Count);
        Assert.Equal(5, ((Text.Fragment)pages[1].Children[0].Data!).Count);
    }

    [Fact]
    public void DebugModeDrawsBoxesAndNames()
    {
        var col = new Column { Name = "Body" };
        col.Items.Add(Block(20));
        col.Items.Add(new Box { Name = "Card", Height = 30, Background = new LayoutColor(0.9, 0.9, 0.9) });
        var doc = Support.Single(col);
        doc.Options.Debug = true;
        byte[] pdf = Support.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        var list = new PdfDisplayList();
        back.Pages[0].Interpret(list);
        string text = list.ToText();
        // One red outline per placed element, children before their parent, in device space (y down).
        string[] outlines = [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("stroke", StringComparison.Ordinal))];
        Assert.Equal(
            [
                "stroke M 20 360 L 280 360 L 280 380 L 20 380 Z",
                "stroke M 20 330 L 280 330 L 280 360 L 20 360 Z",
                "stroke M 20 330 L 280 330 L 280 380 L 20 380 Z",
            ],
            outlines);
        // Each box is labelled with its name.
        Assert.Equal(3, text.Split('\n').Count(l => l.TrimStart().StartsWith("text font", StringComparison.Ordinal)));
    }
}
