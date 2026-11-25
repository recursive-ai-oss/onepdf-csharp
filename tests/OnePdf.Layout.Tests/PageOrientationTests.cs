namespace OnePdf.Layout.Tests;

public class PageOrientationTests
{
    private static (double W, double H) Media(Action<PageComposer> page)
    {
        LayoutDocument doc = LayoutDocument.Compose(d => d.Page(p =>
        {
            page(p);
            p.Content().Text("x");
        }));
        using PdfDocument back = PdfDocument.Open(Support.Save(doc));
        return (back.Pages[0].MediaBox.Width, back.Pages[0].MediaBox.Height);
    }

    [Fact]
    public void ExplicitSizeIsUsedAsGiven() => Assert.Equal((1260.0, 810.0), Media(p => p.Size(1260, 810)));

    [Fact]
    public void PortraitTurnsALandscapeSizeInEitherOrder()
    {
        Assert.Equal((810.0, 1260.0), Media(p => p.Size(1260, 810).Portrait()));
        Assert.Equal((810.0, 1260.0), Media(p => p.Portrait().Size(1260, 810)));
    }

    [Fact]
    public void A4StaysPortraitAndLandscapeTurnsItInEitherOrder()
    {
        Assert.Equal((PageSize.A4.Width, PageSize.A4.Height), Media(p => p.Size(PageSize.A4)));
        Assert.Equal((PageSize.A4.Height, PageSize.A4.Width), Media(p => p.Size(PageSize.A4).Landscape()));
        Assert.Equal((PageSize.A4.Height, PageSize.A4.Width), Media(p => p.Landscape().Size(PageSize.A4)));
        Assert.Equal((PageSize.A4.Width, PageSize.A4.Height), Media(p => p.Landscape().Portrait()));
    }

    [Fact]
    public void LayoutPageOrientationPropertyBehavesTheSame()
    {
        static (double, double) Run(LayoutPage page)
        {
            var doc = new LayoutDocument();
            doc.Pages.Add(page);
            using PdfDocument back = PdfDocument.Open(Support.Save(doc));
            return (back.Pages[0].MediaBox.Width, back.Pages[0].MediaBox.Height);
        }

        Assert.Equal((1260.0, 810.0), Run(new LayoutPage { Size = new PageSize(1260, 810) }));
        Assert.Equal((810.0, 1260.0), Run(new LayoutPage { Size = new PageSize(1260, 810), Orientation = PageOrientation.Portrait }));
        Assert.Equal((PageSize.A4.Height, PageSize.A4.Width), Run(new LayoutPage { Orientation = PageOrientation.Landscape }));
    }
}
