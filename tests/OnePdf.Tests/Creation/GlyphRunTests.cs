using OnePdf.FontPrograms;

namespace OnePdf.Tests.Creation;

public class GlyphRunTests
{
    private static byte[] TestFont(string name) => File.ReadAllBytes(Path.Combine(CreationSupport.Root(), "testdata", "fonts", name));

    private static int Gid(byte[] font, int codePoint)
    {
        SfntFont f = SfntFont.Parse(font, 0, GlyphPrograms.Never);
        int g = f.FindUnicodeCmap()!.Lookup(codePoint);
        Assert.True(g > 0, $"U+{codePoint:X4} has no glyph in the test font");
        return g;
    }

    private static double Advance(byte[] font, int codePoint)
    {
        SfntFont f = SfntFont.Parse(font, 0, GlyphPrograms.Never);
        Assert.True(f.TryGetAdvance(f.FindUnicodeCmap()!.Lookup(codePoint), out float a));
        return a * 1000.0 / f.UnitsPerEm;
    }

    private sealed class GlyphPositions : PdfDevice
    {
        public List<(double X, double Y)> Glyphs { get; } = [];

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            for (int i = 0; i < run.Glyphs.Length; i++)
            {
                PdfMatrix m = run.GlyphMatrix(i).Then(state.Ctm);
                Glyphs.Add((m.E, m.F));
            }
        }
    }

    private static byte[] Draw(byte[] font, Action<PdfPageContentBuilder, PdfCreatedFont> draw, out PdfDocument doc)
    {
        doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 300, 200);
        PdfCreatedFont f = doc.AddFont(font);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            draw(b, f);
            b.Commit();
        }

        return CreationSupport.Save(doc);
    }

    private static string Pdftotext(byte[] pdf)
    {
        if (!File.Exists("/usr/bin/pdftotext"))
        {
            return "";
        }

        string path = Path.Combine(Path.GetTempPath(), "gr-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/pdftotext", ["-enc", "UTF-8", path, "-"]) { RedirectStandardOutput = true };
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            // pdftotext wraps right-to-left runs in embedding controls; they are not part of the text.
            return new string([.. o.Where(ch => ch is not ((>= (char)0x202A and <= (char)0x202E) or (char)0x200E or (char)0x200F or (>= (char)0x2066 and <= (char)0x2069)))]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReorderedDevanagariSyllableExtractsInLogicalOrder()
    {
        byte[] font = TestFont("NotoSansDevanagari-Subset-Regular.ttf");
        int ka = Gid(font, 0x0915);
        int matraI = Gid(font, 0x093F);
        const string text = "कि"; // KA + VOWEL SIGN I
        byte[] pdf = Draw(font, (b, f) =>
        {
            b.SetFont(f, 24);
            // The I-matra is drawn left of the consonant although it follows it in the text: one cluster of two glyphs.
            b.DrawGlyphRun(50, 100, [new(matraI, 0, 400), new(ka, 0, 600)], text);
        }, out PdfDocument doc);
        using (doc)
        {
            Assert.Equal(text, CreationSupport.Normalize(CreationSupport.AllText(pdf)));
            string stream = Decoded(pdf);
            Assert.Contains("/ActualText", stream, StringComparison.Ordinal);
            string tool = Pdftotext(pdf);
            if (tool.Length > 0)
            {
                Assert.Equal(text, CreationSupport.Normalize(tool));
            }
        }
    }

    [Fact]
    public void ArabicRunInVisualOrderExtractsInLogicalOrder()
    {
        byte[] font = TestFont("NotoSansArabic-Subset-Regular.ttf");
        const string text = "سلام";
        // Logical order seen, lam, alef, meem; drawn right to left, so the glyphs are given meem first.
        PdfShapedGlyph[] glyphs =
        [
            new(Gid(font, 0x0645), 3, Advance(font, 0x0645)),
            new(Gid(font, 0x0627), 2, Advance(font, 0x0627)),
            new(Gid(font, 0x0644), 1, Advance(font, 0x0644)),
            new(Gid(font, 0x0633), 0, Advance(font, 0x0633)),
        ];
        byte[] pdf = Draw(font, (b, f) =>
        {
            b.SetFont(f, 24);
            b.DrawGlyphRun(50, 100, glyphs, text);
        }, out PdfDocument doc);
        using (doc)
        {
            Assert.Equal(text, CreationSupport.Normalize(CreationSupport.AllText(pdf)));
            string tool = Pdftotext(pdf);
            if (tool.Length > 0)
            {
                Assert.Equal(text, CreationSupport.Normalize(tool));
            }

            // Single-glyph clusters need no ActualText: the ToUnicode map carries them.
            Assert.DoesNotContain("/ActualText", Decoded(pdf), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LigatureGlyphMapsToItsCharactersThroughToUnicode()
    {
        byte[] font = CreationSupport.RequireFont("LiberationSerif-Regular.ttf");
        int fi = Gid(font, 0xFB01);
        int o = Gid(font, 'o');
        int f2 = Gid(font, 'f');
        byte[] pdf = Draw(font, (b, f) =>
        {
            b.SetFont(f, 20);
            b.DrawGlyphRun(40, 100, [new(f2, 0, 333), new(o, 1, 500), new(fi, 2, 556), new(o, 4, 500)], "fofio");
        }, out PdfDocument doc);
        using (doc)
        {
            Assert.Equal("fofio", CreationSupport.Normalize(CreationSupport.AllText(pdf)));
            Assert.DoesNotContain("/ActualText", Decoded(pdf), StringComparison.Ordinal);
            string tool = Pdftotext(pdf);
            if (tool.Length > 0)
            {
                Assert.Equal("fofio", CreationSupport.Normalize(tool));
            }
        }
    }

    [Fact]
    public void GlyphUsedForDifferentTextGetsActualTextAndEmptyClusterIsAllowed()
    {
        byte[] font = CreationSupport.RequireFont("LiberationSerif-Regular.ttf");
        int a = Gid(font, 'a');
        byte[] pdf = Draw(font, (b, f) =>
        {
            b.SetFont(f, 20);
            b.DrawGlyphRun(40, 100, [new(a, 0, 500), new(a, 1, 500)], "ax"); // the second 'a' stands for "x"
        }, out PdfDocument doc);
        using (doc)
        {
            Assert.Equal("ax", CreationSupport.Normalize(CreationSupport.AllText(pdf)));
            Assert.Contains("/ActualText", Decoded(pdf), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AdvancesAndOffsetsPlaceEachGlyph()
    {
        byte[] font = CreationSupport.RequireFont("LiberationSerif-Regular.ttf");
        int a = Gid(font, 'a');
        int b2 = Gid(font, 'b');
        byte[] pdf = Draw(font, (b, f) =>
        {
            b.SetFont(f, 10);
            b.SetCharacterSpacing(1.5);
            double moved = b.DrawGlyphRun(10, 50, [new(a, 0, 1000), new(b2, 1, 250, OffsetX: 100, OffsetY: 200), new(a, 2, 500)], "aba");
            Assert.Equal(17.5, moved, 1e-9);
        }, out PdfDocument doc);
        using (doc)
        {
            using PdfDocument back = PdfDocument.Open(pdf);
            var c = new GlyphPositions();
            back.Pages[0].Interpret(c, null);
            // The page is 200 high and output space is y-down. Pen moves by the advances (10 pt, then 2.5 pt) whatever
            // the font widths and the 1.5 pt character spacing are; glyph two is shifted by (1, 2) from its pen position.
            Assert.Equal(3, c.Glyphs.Count);
            Assert.Equal((10, 150), (Math.Round(c.Glyphs[0].X, 2), Math.Round(c.Glyphs[0].Y, 2)));
            Assert.Equal((21, 148), (Math.Round(c.Glyphs[1].X, 2), Math.Round(c.Glyphs[1].Y, 2)));
            Assert.Equal((22.5, 150), (Math.Round(c.Glyphs[2].X, 2), Math.Round(c.Glyphs[2].Y, 2)));
        }
    }

    [Fact]
    public void GlyphRunRejectsBadInput()
    {
        byte[] font = CreationSupport.RequireFont("LiberationSerif-Regular.ttf");
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 100, 100);
        PdfCreatedFont f = doc.AddFont(font);
        using PdfPageContentBuilder b = page.BeginContent();
        Assert.Throws<InvalidOperationException>(() => b.DrawGlyphRun(0, 0, [new(1, 0, 500)], "a")); // no font set
        b.SetFont(doc.AddStandardFont("Helvetica"), 10);
        Assert.Throws<InvalidOperationException>(() => b.DrawGlyphRun(0, 0, [new(1, 0, 500)], "a")); // not embedded
        b.SetFont(f, 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => b.DrawGlyphRun(0, 0, [new(1 << 20, 0, 500)], "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.DrawGlyphRun(0, 0, [new(1, 5, 500)], "a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.DrawGlyphRun(0, 0, [new(1, 0, double.NaN)], "a"));
        Assert.Equal(0, b.DrawGlyphRun(0, 0, [], ""));
        // The missing-glyph box (glyph 0) is drawable.
        b.DrawGlyphRun(0, 0, [new(0, 0, 500)], "\uFFFD");
    }

    [Fact]
    public void MarkedContentMustNestWithSavedStates()
    {
        using PdfDocument doc = PdfDocument.CreateEmpty();
        PdfPage page = doc.InsertBlankPage(0, 100, 100);
        using PdfPageContentBuilder b = page.BeginContent();
        Assert.Throws<InvalidOperationException>(b.EndMarkedContent);
        b.BeginMarkedContent("P", 0);
        b.Save();
        Assert.Throws<InvalidOperationException>(b.EndMarkedContent);
        b.Restore();
        b.BeginMarkedContent("Sp an");
        Assert.Equal(2, b.MarkedContentDepth);
        b.Save();
        b.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Footer);
        Assert.Throws<InvalidOperationException>(b.Restore);
        b.EndMarkedContent();
        b.Restore();
        b.BeginSpan("a (b)", language: "en-US");
        b.Commit(); // closes what is open
        string s = Decoded(CreationSupport.Save(doc));
        Assert.Contains("/P <</MCID 0>> BDC", s, StringComparison.Ordinal);
        Assert.Contains("/Sp#20an BMC", s, StringComparison.Ordinal);
        Assert.Contains("/Artifact <</Type /Pagination/Subtype /Footer>> BDC", s, StringComparison.Ordinal);
        Assert.Equal(CountOf(s, "BDC") + CountOf(s, "BMC"), CountOf(s, "EMC"));
    }

    private static int CountOf(string s, string token)
    {
        int n = 0;
        for (int i = s.IndexOf(token, StringComparison.Ordinal); i >= 0; i = s.IndexOf(token, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    private static string Decoded(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        var sb = new System.Text.StringBuilder();
        PdfObject? contents = d.Pages[0].Dictionary.Get("Contents");
        if (contents is PdfArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                if (arr[i] is PdfStream s)
                {
                    sb.Append(System.Text.Encoding.Latin1.GetString(s.GetDecodedBytes().Span)).Append('\n');
                }
            }
        }
        else if (contents is PdfStream one)
        {
            sb.Append(System.Text.Encoding.Latin1.GetString(one.GetDecodedBytes().Span));
        }

        return sb.ToString();
    }
}
