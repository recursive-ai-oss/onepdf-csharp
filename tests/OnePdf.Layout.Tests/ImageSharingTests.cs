namespace OnePdf.Layout.Tests;

/// <summary>A picture's bytes are embedded once per document, however often, wherever and from whichever array they are used.</summary>
public class ImageSharingTests
{
    private static (int Images, int Masks) Count(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        int images = 0;
        int masks = 0;
        for (int i = 1; i < d.ObjectCount; i++)
        {
            if (d.GetObject(i) is PdfStream s && s.Dictionary.GetName("Subtype") == "Image")
            {
                images++;
                if (IsMask(d, i))
                {
                    masks++;
                }
            }
        }

        return (images, masks);
    }

    // A soft mask is an image that another image names in /SMask.
    private static bool IsMask(PdfDocument d, int number)
    {
        for (int i = 1; i < d.ObjectCount; i++)
        {
            if (d.GetObject(i) is PdfStream s && s.Dictionary.GetRaw("SMask") is PdfReference r && r.Number == number)
            {
                return true;
            }
        }

        return false;
    }

    private static int Images(LayoutDocument doc) => Count(Support.Save(doc)).Images;

    [Fact]
    public void SameBytesTwiceOnOnePageGiveOneImage()
    {
        byte[] png = FacadeTests.TinyPng();
        Assert.Equal(1, Images(LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 300);
            page.Content().Column(c =>
            {
                c.Item().Width(50).Image(png).AltText("a");
                c.Item().Width(50).Image(png).AltText("b");
            });
        }))));
    }

    [Fact]
    public void SameBytesOnTwoPagesAndInTwoSectionsGiveOneImage()
    {
        byte[] jpeg = Support.Jpeg();
        Assert.Equal(1, Images(LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(200, 100);
            page.Content().Column(c =>
            {
                c.Item().Height(80).Image(jpeg).Contain().AltText("a");
                c.Item().PageBreak();
                c.Item().Height(80).Image(jpeg).Contain().AltText("b");
            });
        }))));

        Assert.Equal(1, Images(LayoutDocument.Compose(doc =>
        {
            doc.Page(page =>
            {
                page.Size(200, 200);
                page.Content().Width(50).Image(jpeg).AltText("a");
            });
            doc.Page(page =>
            {
                page.Size(200, 200);
                page.Content().Width(50).Image(jpeg).AltText("b");
            });
        })));
    }

    [Fact]
    public void EqualContentFromDistinctArraysGivesOneImageAndDifferentBytesTwo()
    {
        byte[] a = FacadeTests.TinyPng();
        byte[] b = (byte[])a.Clone();
        byte[] other = FacadeTests.TinyPng(16);
        Assert.NotSame(a, b);
        Assert.Equal(1, Images(LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 300);
            page.Content().Column(c =>
            {
                c.Item().Width(50).Image(a).AltText("a");
                c.Item().Width(50).Image(b).AltText("b");
            });
        }))));

        Assert.Equal(2, Images(LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 300);
            page.Content().Column(c =>
            {
                c.Item().Width(50).Image(a).AltText("a");
                c.Item().Width(50).Image(other).AltText("b");
            });
        }))));
    }

    [Fact]
    public void AlphaPngMaskIsSharedToo()
    {
        byte[] a = FacadeTests.TinyPng(8, alpha: true);
        byte[] b = (byte[])a.Clone();
        (int images, int masks) = Count(Support.Save(LayoutDocument.Compose(doc =>
        {
            doc.Page(page =>
            {
                page.Size(300, 300);
                page.Content().Column(c =>
                {
                    c.Item().Width(50).Image(a).AltText("a");
                    c.Item().Width(50).Image(b).AltText("b");
                });
            });
            doc.Page(page =>
            {
                page.Size(300, 300);
                page.Content().Width(50).Image(a).AltText("c");
            });
        })));
        Assert.Equal(2, images); // the picture and its soft mask
        Assert.Equal(1, masks);
    }

    [Fact]
    public void InlineImagesShareWithBlockImages()
    {
        byte[] jpeg = Support.Jpeg();
        byte[] copy = (byte[])jpeg.Clone();
        Assert.Equal(1, Images(LayoutDocument.Compose(doc => doc.Page(page =>
        {
            page.Size(300, 300);
            page.Content().Column(c =>
            {
                c.Item().Width(50).Image(jpeg).AltText("a");
                c.Item().Text(t =>
                {
                    t.Span("x ");
                    t.InlineImage(copy, 12, 12).AltText("b");
                });
            });
        }))));
    }
}
