using System.Diagnostics;

namespace OnePdf.Tests.Creation;

/// <summary>Other tools' view of created files (skipped where the tool is not installed): poppler renders and lists the fonts, fontTools parses the subset.</summary>
public class BlackBoxTests
{
    private static string? Tool(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                return p;
            }
        }

        return null;
    }

    private static string Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    private static (int W, int H, byte[] Rgb) Render(string pdftoppm, byte[] pdf)
    {
        string dir = Path.Combine(Path.GetTempPath(), "onepdf-bb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(file, pdf);
            Run(pdftoppm, "-r", "72", "-f", "1", "-l", "1", file, Path.Combine(dir, "out"));
            string ppm = Directory.GetFiles(dir, "out*.ppm").Single();
            byte[] d = File.ReadAllBytes(ppm);
            // P6\nW H\n255\n
            int pos = 0;
            string[] header = new string[4];
            for (int i = 0; i < 4; i++)
            {
                int start = pos;
                while (d[pos] > 32)
                {
                    pos++;
                }

                header[i] = System.Text.Encoding.ASCII.GetString(d, start, pos - start);
                pos++;
            }

            return (int.Parse(header[1]), int.Parse(header[2]), d[pos..]);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static (int R, int G, int B) Pixel((int W, int H, byte[] Rgb) img, double x, double yFromBottom, double pageHeight)
    {
        int px = (int)Math.Round(x);
        int py = (int)Math.Round(pageHeight - yFromBottom);
        int o = ((py * img.W) + px) * 3;
        return (img.Rgb[o], img.Rgb[o + 1], img.Rgb[o + 2]);
    }

    private static byte[] Sample(out PdfDocument doc)
    {
        doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 300, 200);
        byte[] lib = CreationSupport.Font("LiberationSans-Regular.ttf") ?? SyntheticFont.Build();
        PdfCreatedFont emb = doc.AddFont(lib);
        PdfCreatedFont std = doc.AddStandardFont("Helvetica");
        byte[] green = new byte[4 * 4 * 3];
        for (int i = 0; i < green.Length; i += 3)
        {
            green[i + 1] = 255;
        }

        PdfCreatedImage img = doc.AddRawImage(4, 4, PdfImageColor.Rgb, green);
        PdfCreatedImage black = doc.AddRawImage(1, 1, PdfImageColor.Gray, [0]);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFillRgb(1, 0, 0);
            b.Rectangle(20, 20, 40, 40);
            b.Fill();
            b.DrawImage(img, 100, 20, 40, 40);
            b.SetFillGray(0);
            b.SetFont(emb, 24);
            b.DrawText(lib.Length > 2000 ? "Embedded Text" : "ABCD", 10, 150);
            b.SetFont(std, 24);
            b.DrawText("Standard", 10, 110);
            b.Commit();
        }

        doc.AddImageStamp(black, 60, 60, new PdfStampOptions { Opacity = 0.5, CenterX = 220, CenterY = 40 });
        return CreationSupport.Save(doc);
    }

    [Fact]
    public void PopplerRendersTextShapesImagesAndOpacity()
    {
        string? pdftoppm = Tool("pdftoppm");
        if (pdftoppm is null)
        {
            Assert.Skip("pdftoppm is not installed.");
        }

        byte[] pdf = Sample(out PdfDocument doc);
        using (doc)
        {
            var img = Render(pdftoppm!, pdf);
            Assert.Equal(300, img.W);
            Assert.Equal(200, img.H);
            (int r, int g, int b) = Pixel(img, 40, 40, 200);
            Assert.True(r > 240 && g < 20 && b < 20, $"red rectangle: {r},{g},{b}");
            (r, g, b) = Pixel(img, 120, 40, 200);
            Assert.True(r < 20 && g > 235 && b < 20, $"image: {r},{g},{b}");
            (r, g, b) = Pixel(img, 220, 40, 200);
            Assert.InRange(r, 120, 135); // black at 50% over white
            Assert.InRange(g, 120, 135);

            int dark = 0;
            for (int y = 30; y < 60; y++)
            {
                for (int x = 10; x < 200; x++)
                {
                    int o = ((y * img.W) + x) * 3;
                    if (img.Rgb[o] < 100)
                    {
                        dark++;
                    }
                }
            }

            Assert.True(dark > 150, $"embedded text pixels: {dark}");
            dark = 0;
            for (int y = 70; y < 95; y++)
            {
                for (int x = 10; x < 150; x++)
                {
                    int o = ((y * img.W) + x) * 3;
                    if (img.Rgb[o] < 100)
                    {
                        dark++;
                    }
                }
            }

            Assert.True(dark > 150, $"standard text pixels: {dark}");
        }
    }

    [Fact]
    public void PopplerRendersCjkSubset()
    {
        string? pdftoppm = Tool("pdftoppm");
        if (pdftoppm is null)
        {
            Assert.Skip("pdftoppm is not installed.");
        }

        byte[] font = CreationSupport.RequireFont("NotoSansJP-Regular.otf");
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 300, 100);
        PdfCreatedFont f = doc.AddFont(font);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(f, 40);
            b.DrawText("\u65e5\u672c\u8a9e", 10, 40);
            b.Commit();
        }

        var img = Render(pdftoppm!, CreationSupport.Save(doc));
        int dark = 0;
        for (int i = 0; i < img.Rgb.Length; i += 3)
        {
            if (img.Rgb[i] < 100)
            {
                dark++;
            }
        }

        Assert.True(dark > 600, $"dark pixels {dark}");
    }

    [Fact]
    public void PdffontsListsEmbeddedSubsets()
    {
        string? pdffonts = Tool("pdffonts");
        if (pdffonts is null)
        {
            Assert.Skip("pdffonts is not installed.");
        }

        byte[] pdf = Sample(out PdfDocument doc);
        doc.Dispose();
        string path = Path.Combine(Path.GetTempPath(), "onepdf-bb-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            string output = Run(pdffonts, path);
            Assert.Contains("CID TrueType", output);
            Assert.Contains("Identity-H", output);
            string embedded = output.Split('\n').First(l => l.Contains("CID TrueType", StringComparison.Ordinal));
            Assert.Matches(@"\syes\s+yes\s+yes\s", embedded); // embedded, subset, unicode
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FontToolsParsesTheSubsetTables()
    {
        string? ttx = Tool("ttx");
        if (ttx is null)
        {
            Assert.Skip("ttx is not installed.");
        }

        byte[] font = CreationSupport.RequireFont("LiberationSans-Regular.ttf");
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 300, 100);
        PdfCreatedFont f = doc.AddFont(font);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(f, 20);
            b.DrawText("H\u00e9llo \u0391\u03b2 \u00c5", 10, 40); // \u00c5 is a composite glyph in Liberation
            b.Commit();
        }

        byte[] pdf = CreationSupport.Save(doc);
        using PdfDocument back = PdfDocument.Open(pdf);
        PdfDictionary fonts = back.Pages[0].Resources.GetDictionary("Font")!;
        PdfDictionary type0 = fonts.GetDictionary(fonts.Keys.First())!;
        PdfDictionary cid = (PdfDictionary)((PdfArray)type0.Get("DescendantFonts")!)[0]!;
        byte[] program = cid.GetDictionary("FontDescriptor")!.GetStream("FontFile2")!.GetDecodedBytes().ToArray();
        string dir = Path.Combine(Path.GetTempPath(), "onepdf-ttx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ttf = Path.Combine(dir, "subset.ttf");
            File.WriteAllBytes(ttf, program);
            string output = Run(ttx, "-q", "-o", Path.Combine(dir, "subset.ttx"), ttf);
            Assert.DoesNotContain("Error", output, StringComparison.OrdinalIgnoreCase);
            string xml = File.ReadAllText(Path.Combine(dir, "subset.ttx"));
            Assert.Contains("<glyf>", xml);
            Assert.Contains("<hmtx>", xml);
            Assert.Contains("<head>", xml);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
