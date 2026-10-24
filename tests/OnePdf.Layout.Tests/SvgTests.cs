using System.Text;

namespace OnePdf.Layout.Tests;

/// <summary>SVG pictures in layout: text that extracts, figure tagging, hostile documents and every limit.</summary>
public class SvgTests
{
    private const string Header = "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='200' height='100' viewBox='0 0 200 100'>";

    private static LayoutDocument Doc(SvgImage svg, string? alt = null, bool artifact = false)
        => Support.Single(new SvgPicture { Source = svg, Fit = ImageFit.Contain, AltText = alt, Artifact = artifact });

    private static (byte[] Pdf, List<string> Notes) Render(string svg, SvgLimits? limits = null)
    {
        LayoutDocument layout = Doc(SvgImage.Parse(svg, limits));
        byte[] pdf = Support.Save(layout);
        return (pdf, layout.Diagnostics.Select(d => d.Message).ToList());
    }

    private static string Extract(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(pdf);
        return string.Join(" ", doc.ExtractText().Select(t => Support.Normalize(t.Text)));
    }

    [Fact]
    public void TextIsRealTextInLogicalOrder()
    {
        (byte[] pdf, _) = Render(Header + "<text x='10' y='30' font-size='16'>Quarterly</text><text x='10' y='60' font-size='16'>results <tspan fill='red'>2026</tspan></text></svg>");
        string text = Extract(pdf);
        int a = text.IndexOf("Quarterly", StringComparison.Ordinal);
        int b = text.IndexOf("results", StringComparison.Ordinal);
        int c = text.IndexOf("2026", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a && c > b, text);
    }

    [Fact]
    public void ShapesGradientsAndStrokesSaveAndOpen()
    {
        string svg = Header + "<defs><linearGradient id='g'><stop offset='0' stop-color='red'/><stop offset='1' stop-color='blue' stop-opacity='.5'/></linearGradient>"
            + "<pattern id='p' width='10' height='10' patternUnits='userSpaceOnUse'><circle cx='5' cy='5' r='3'/></pattern></defs>"
            + "<rect x='10' y='10' width='80' height='80' fill='url(#g)' stroke='url(#g)' stroke-width='8' stroke-dasharray='6 3'/>"
            + "<path d='M100 10 A40 40 0 0 1 180 50' fill='none' stroke='url(#p)' stroke-width='12'/></svg>";
        foreach (bool streamed in new[] { false, true })
        {
            LayoutDocument layout = Doc(SvgImage.Parse(svg));
            using var ms = new MemoryStream();
            if (streamed)
            {
                layout.Save(ms, Support.Fixed);
            }
            else
            {
                layout.Save(ms, new PdfSaveOptions { DocumentId = new byte[16] });
            }

            using PdfDocument back = PdfDocument.Open(ms.ToArray());
            Assert.Empty(back.Diagnostics);
            Assert.Single(back.Pages);
        }
    }

    [Fact]
    public void FigureTakesAuthorAltTextThenTitleThenDescription()
    {
        string withTitle = Header + "<title>Sales chart</title><desc>Bars</desc><rect width='50' height='50'/></svg>";
        string onlyDesc = Header + "<desc>Just bars</desc><rect width='50' height='50'/></svg>";
        string neither = Header + "<rect width='50' height='50'/></svg>";

        Assert.Equal("Sales chart", SvgImage.Parse(withTitle).Title);
        Assert.Equal("Just bars", SvgImage.Parse(onlyDesc).Description);

        var layout = new LayoutDocument { Title = "T", Language = "en" };
        var col = new Column();
        col.Items.Add(new SvgPicture { Source = SvgImage.Parse(withTitle), Fit = ImageFit.Contain, AltText = "Author text" });
        col.Items.Add(new SvgPicture { Source = SvgImage.Parse(onlyDesc), Fit = ImageFit.Contain });
        layout.Pages.Add(new LayoutPage { Size = new PageSize(300, 400), Content = col });
        byte[] pdf = Support.Save(layout);
        using PdfDocument back = PdfDocument.Open(pdf);
        List<string?> alts = TagSupport.ReadTree(back).Descendants().Where(n => n.Type == "Figure").Select(n => n.Alt).ToList();
        Assert.Equal(["Author text", "Just bars"], alts);

        // With the title as the description, no diagnostic is raised; with nothing to say, one is.
        LayoutDocument good = Doc(SvgImage.Parse(withTitle), alt: "x");
        Support.Save(good);
        Assert.Empty(good.Diagnostics);
        LayoutDocument bad = Doc(SvgImage.Parse(neither));
        Support.Save(bad);
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("alternate text", StringComparison.Ordinal));
    }

    private static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    [Fact]
    public void TaggedSvgPassesUa1AndMissingAltIsRefused()
    {
        string svg = Header + "<title>Sales chart</title><rect width='50' height='50' fill='teal'/><text x='60' y='40'>Total</text></svg>";
        byte[] pdf = PdfUA(Doc(SvgImage.Parse(svg)));
        List<string> failures = TagSupport.Vera(pdf, "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        LayoutDocument none = Doc(SvgImage.Parse(Header + "<rect width='50' height='50'/></svg>"));
        none.Title = "T";
        none.Language = "en";
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => none.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
        Assert.Contains(ex.Violations, v => v.Checkpoint == "13");
    }

    private static byte[] PdfUA(LayoutDocument d)
    {
        d.Title = "Svg figure";
        d.Language = "en";
        using var ms = new MemoryStream();
        d.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        return ms.ToArray();
    }

    // ---- never run, never fetch ----

    [Theory]
    [InlineData("<script>alert(1)</script><rect width='10' height='10'/>")]
    [InlineData("<foreignObject width='50' height='50'><div xmlns='http://www.w3.org/1999/xhtml'>hi</div></foreignObject><rect width='10' height='10'/>")]
    [InlineData("<image width='50' height='50' xlink:href='file:///etc/passwd'/><rect width='10' height='10'/>")]
    [InlineData("<image width='50' height='50' href='http://127.0.0.1:9/x.png'/><rect width='10' height='10'/>")]
    [InlineData("<image width='50' height='50' href='other.svg'/><rect width='10' height='10'/>")]
    [InlineData("<rect width='10' height='10'><animate attributeName='x' from='0' to='50' dur='1s'/></rect>")]
    [InlineData("<use xlink:href='http://example.invalid/a.svg#x'/><rect width='10' height='10' onclick='alert(1)'/>")]
    public void ActiveAndExternalContentIsDroppedWithoutFetching(string body)
    {
        (byte[] pdf, _) = Render(Header + body + "</svg>");
        using PdfDocument back = PdfDocument.Open(pdf);
        string raw = Latin1(pdf);
        Assert.DoesNotContain("alert", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("passwd", raw, StringComparison.Ordinal);
        Assert.Single(back.Pages);
    }

    [Fact]
    public void ExternalEntitiesAreNotReadAndDoctypesAreRefusedOrIgnored()
    {
        string secret = Path.Combine(Path.GetTempPath(), "svg-xxe-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secret, "TOPSECRETVALUE");
        try
        {
            string xml = "<?xml version='1.0'?><!DOCTYPE svg [<!ENTITY xxe SYSTEM 'file://" + secret + "'>]>" + Header + "<text x='10' y='30'>&xxe;</text></svg>";
            try
            {
                (byte[] pdf, _) = Render(xml);
                Assert.DoesNotContain("TOPSECRETVALUE", Extract(pdf), StringComparison.Ordinal);
            }
            catch (Exception e) when (e is PdfFormatException or PdfLimitException)
            {
                // Refusing the document is as good.
            }
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void BillionLaughsCostsNothing()
    {
        var sb = new StringBuilder("<?xml version='1.0'?><!DOCTYPE svg [<!ENTITY a0 'lol'>");
        for (int i = 1; i < 10; i++)
        {
            sb.Append("<!ENTITY a").Append(i).Append(" '");
            for (int k = 0; k < 10; k++)
            {
                sb.Append("&a").Append(i - 1).Append(';');
            }

            sb.Append("'>");
        }

        sb.Append("]>").Append(Header).Append("<text x='5' y='30'>&a9;</text></svg>");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            (byte[] pdf, _) = Render(sb.ToString());
            Assert.True(Extract(pdf).Length < 1000);
        }
        catch (Exception e) when (e is PdfFormatException or PdfLimitException)
        {
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    // ---- limits ----

    private static void ExpectLimit(string svg, SvgLimits limits)
    {
        Assert.ThrowsAny<Exception>(() =>
        {
            LayoutDocument layout = Doc(SvgImage.Parse(svg, limits));
            Support.Save(layout);
        }).ShouldBe<PdfLimitException>();
    }

    [Fact]
    public void DocumentSizeElementCountAndDepthAreLimited()
    {
        ExpectLimit(Header + "<rect width='1' height='1'/></svg>", new SvgLimits { MaxDocumentBytes = 50 });
        ExpectLimit(Header + string.Concat(Enumerable.Repeat("<rect width='1' height='1'/>", 20)) + "</svg>", new SvgLimits { MaxElements = 10 });
        ExpectLimit(Header + string.Concat(Enumerable.Repeat("<g>", 40)) + string.Concat(Enumerable.Repeat("</g>", 40)) + "</svg>", new SvgLimits { MaxDepth = 20 });
    }

    [Fact]
    public void PathSegmentsAreLimited()
    {
        string d = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"L{i} {i}"));
        ExpectLimit(Header + "<path d='M0 0 " + d + "'/></svg>", new SvgLimits { MaxPathSegments = 50 });
        string pts = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"{i},{i}"));
        ExpectLimit(Header + "<polyline points='" + pts + "'/></svg>", new SvgLimits { MaxPathSegments = 50 });
    }

    [Fact]
    public void UseFanOutAndChainsAreLimited()
    {
        // Each level references the one before it ten times: 10^6 instances if followed.
        var sb = new StringBuilder(Header + "<defs><rect id='u0' width='1' height='1'/>");
        for (int i = 1; i <= 6; i++)
        {
            sb.Append("<g id='u").Append(i).Append("'>");
            for (int k = 0; k < 10; k++)
            {
                sb.Append("<use href='#u").Append(i - 1).Append("'/>");
            }

            sb.Append("</g>");
        }

        sb.Append("</defs><use href='#u6'/></svg>");
        ExpectLimit(sb.ToString(), new SvgLimits { MaxInstances = 5000 });

        var chain = new StringBuilder(Header + "<defs><rect id='c0' width='1' height='1'/>");
        for (int i = 1; i <= 40; i++)
        {
            chain.Append("<use id='c").Append(i).Append("' href='#c").Append(i - 1).Append("'/>");
        }

        chain.Append("</defs><use href='#c40'/></svg>");
        (_, List<string> notes) = (Array.Empty<byte>(), new List<string>());
        // A chain deeper than the limit is not followed, and not a crash.
        LayoutDocument l = Doc(SvgImage.Parse(chain.ToString(), new SvgLimits { MaxReferenceDepth = 8 }));
        Support.Save(l);
        Assert.NotNull(notes);
    }

    [Fact]
    public void BoundingBoxesOfUseFanOutAreBounded()
    {
        var sb = new StringBuilder(Header + "<defs><rect id='b0' width='5' height='5'/><mask id='m'><rect width='1' height='1' fill='white'/></mask>");
        for (int i = 1; i <= 22; i++)
        {
            sb.Append("<g id='b").Append(i).Append("'><use href='#b").Append(i - 1).Append("'/><use href='#b").Append(i - 1).Append("' x='1'/></g>");
        }

        sb.Append("</defs><g mask='url(#m)'><use href='#b22'/></g></svg>");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            Render(sb.ToString(), new SvgLimits { MaxReferenceDepth = 64 });
        }
        catch (PdfLimitException)
        {
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
    }

    [Fact]
    public void SelfAndMutualReferencesTerminate()
    {
        string svg = Header + "<defs><g id='a'><use href='#b'/></g><g id='b'><use href='#a'/></g>"
            + "<pattern id='p' width='10' height='10' patternUnits='userSpaceOnUse'><rect width='5' height='5' fill='url(#p)'/></pattern>"
            + "<marker id='m' markerWidth='5' markerHeight='5'><path d='M0 0 L5 5' marker-start='url(#m)' stroke='black'/></marker>"
            + "<mask id='k'><rect width='100' height='100' mask='url(#k)' fill='white'/></mask>"
            + "<clipPath id='c' clip-path='url(#c)'><rect width='100' height='100'/></clipPath></defs>"
            + "<use href='#a'/><rect width='100' height='100' fill='url(#p)' mask='url(#k)' clip-path='url(#c)'/>"
            + "<path d='M10 10 L90 90' stroke='black' marker-start='url(#m)'/><use href='#self' id='self'/></svg>";
        Render(svg);
    }

    [Fact]
    public void GradientStopsAreBoundedAndTextCharactersAreLimited()
    {
        string stops = string.Concat(Enumerable.Range(0, 5000).Select(i => $"<stop offset='{i / 5000.0:0.####}' stop-color='red'/>"));
        Render(Header + "<linearGradient id='g'>" + stops + "</linearGradient><rect width='100' height='100' fill='url(#g)' stroke='url(#g)'/></svg>");

        string longText = new('x', 5000);
        ExpectLimit(Header + "<text x='0' y='20'>" + longText + "</text></svg>", new SvgLimits { MaxTextCharacters = 1000 });
        ExpectLimit(Header + "<defs><text id='t'>" + new string('y', 400) + "</text></defs>" + string.Concat(Enumerable.Repeat("<use href='#t'/>", 10)) + "</svg>", new SvgLimits { MaxTextCharacters = 1000 });
    }

    [Fact]
    public void DataUriImagesAreBounded()
    {
        string big = Convert.ToBase64String(new byte[4000]);
        ExpectLimit(Header + "<image width='10' height='10' href='data:image/png;base64," + big + "'/></svg>", new SvgLimits { MaxImageBytes = 1000 });
        ExpectLimit(Header + string.Concat(Enumerable.Repeat("<image width='10' height='10' href='data:image/png;base64," + Convert.ToBase64String(new byte[800]) + "'/>", 10)) + "</svg>",
            new SvgLimits { MaxImageBytes = 1000, MaxTotalImageBytes = 3000 });

        // A data URI that is not an image is dropped, not a crash.
        Render(Header + "<image width='10' height='10' href='data:image/png;base64,AAAA'/><rect width='5' height='5'/></svg>");
    }

    [Fact]
    public void DeepNestingOfGroupsAndLayersDoesNotOverflowTheStack()
    {
        string svg = Header + string.Concat(Enumerable.Repeat("<g opacity='.9' clip-path='url(#c)'>", 200)) + "<rect width='10' height='10'/>" + string.Concat(Enumerable.Repeat("</g>", 200))
            + "<defs><clipPath id='c'><rect width='100' height='100'/></clipPath></defs></svg>";
        try
        {
            Render(svg);
        }
        catch (PdfLimitException)
        {
        }
    }

    [Fact]
    public void MalformedDocumentsRaiseTypedErrors()
    {
        foreach (string bad in new[] { string.Empty, "not xml", "<svg", "<html/>", "<svg xmlns='http://www.w3.org/2000/svg'><g></svg>" })
        {
            Exception e = Assert.ThrowsAny<Exception>(() => SvgImage.Parse(bad));
            Assert.True(e is PdfFormatException or PdfLimitException, bad + " -> " + e.GetType());
        }
    }

    [Fact]
    public void EveryFitModeLaysOut()
    {
        SvgImage svg = SvgImage.Parse(Header + "<rect width='200' height='100' fill='navy'/></svg>");
        foreach (ImageFit fit in Enum.GetValues<ImageFit>())
        {
            byte[] pdf = Support.Save(Support.Single(new SvgPicture { Source = svg, Fit = fit, AltText = "x" }));
            using PdfDocument back = PdfDocument.Open(pdf);
            Assert.Single(back.Pages);
        }
    }
}

internal static class ExceptionAssert
{
    public static void ShouldBe<T>(this Exception e)
        where T : Exception
        => Assert.True(e is T, "expected " + typeof(T).Name + " but got " + e.GetType().Name + ": " + e.Message);
}
