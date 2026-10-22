using System.Globalization;
using System.Text.RegularExpressions;
using OnePdf.FontData;

namespace OnePdf.Tests.Fonts;

public partial class FontDataTests
{
    [Theory]
    [InlineData("Lcommaaccent", "\u013B")]
    [InlineData("uni20AC0308", "\u20AC\u0308")]
    [InlineData("u1040C", "\U0001040C")]
    [InlineData("uniD801DC0C", null)]
    [InlineData("uni20ac", null)]
    [InlineData("Lcommaaccent_uni20AC0308_u1040C.alternate", "\u013B\u20AC\u0308\U0001040C")]
    [InlineData("uni013B", "\u013B")]
    [InlineData("u013B", "\u013B")]
    [InlineData("foo", null)]
    [InlineData(".notdef", null)]
    [InlineData("A_B", "AB")]
    [InlineData("f_f_i", "ffi")]
    [InlineData("a.alt", "a")]
    [InlineData("A", "A")]
    [InlineData("ffi", "\uFB03")]
    [InlineData("u110000", null)]
    [InlineData("uniD800", null)]
    [InlineData("union", "\u222A")]
    [InlineData("universal", "\u2200")]
    [InlineData("universe", null)]
    [InlineData("", null)]
    [InlineData("_", null)]
    public void GlyphNamesFollowTheAglSpecification(string name, string? expected)
    {
        bool ok = GlyphList.TryGetUnicode(name, out string text);
        Assert.Equal(expected is not null, ok);
        Assert.Equal(expected ?? string.Empty, text);
    }

    [Fact]
    public void ZapfDingbatsList()
    {
        Assert.True(GlyphList.TryGetZapfDingbats("a1", out string t));
        Assert.Equal("\u2701", t);
        Assert.False(GlyphList.TryGetZapfDingbats("A", out _));
        Assert.True(GlyphList.TryGetListed("Aacute", out string aacute));
        Assert.Equal("\u00C1", aacute);
    }

    [Fact]
    public void EncodingsMatchAnnexD()
    {
        Assert.Equal("A", Encodings.GetName(PredefinedEncoding.Standard, 0x41));
        Assert.Equal("quoteright", Encodings.GetName(PredefinedEncoding.Standard, 0x27));
        Assert.Equal("quoteleft", Encodings.GetName(PredefinedEncoding.Standard, 0x60));
        Assert.Equal("fi", Encodings.GetName(PredefinedEncoding.Standard, 0xAE));
        Assert.Null(Encodings.GetName(PredefinedEncoding.Standard, 0x80));
        Assert.Equal(149, Encodings.Get(PredefinedEncoding.Standard).Count(n => n is not null));

        Assert.Equal("quotesingle", Encodings.GetName(PredefinedEncoding.WinAnsi, 0x27));
        Assert.Equal("Euro", Encodings.GetName(PredefinedEncoding.WinAnsi, 0x80));
        Assert.Equal("bullet", Encodings.GetName(PredefinedEncoding.WinAnsi, 0x95));
        Assert.Equal("bullet", Encodings.GetName(PredefinedEncoding.WinAnsi, 0x7F));
        Assert.Equal("space", Encodings.GetName(PredefinedEncoding.WinAnsi, 0xA0));
        Assert.Equal("hyphen", Encodings.GetName(PredefinedEncoding.WinAnsi, 0xAD));
        Assert.Equal("Zcaron", Encodings.GetName(PredefinedEncoding.WinAnsi, 0x8E));
        Assert.Equal("ydieresis", Encodings.GetName(PredefinedEncoding.WinAnsi, 0xFF));
        Assert.Equal(224, Encodings.Get(PredefinedEncoding.WinAnsi).Count(n => n is not null));

        Assert.Equal("Adieresis", Encodings.GetName(PredefinedEncoding.MacRoman, 0x80));
        Assert.Equal("space", Encodings.GetName(PredefinedEncoding.MacRoman, 0xCA));
        Assert.Equal("currency", Encodings.GetName(PredefinedEncoding.MacRoman, 0xDB));
        Assert.Equal("fi", Encodings.GetName(PredefinedEncoding.MacRoman, 0xDE));

        Assert.Equal("zerooldstyle", Encodings.GetName(PredefinedEncoding.MacExpert, 0x30));
        Assert.Equal("fraction", Encodings.GetName(PredefinedEncoding.MacExpert, 0x2F));
        Assert.Equal("Asmall", Encodings.GetName(PredefinedEncoding.MacExpert, 0x61));

        Assert.Equal("alpha", Encodings.GetName(PredefinedEncoding.Symbol, 0x61));
        Assert.Equal("a1", Encodings.GetName(PredefinedEncoding.ZapfDingbats, 0x21));
        Assert.Equal("bullet", Encodings.GetName(PredefinedEncoding.PdfDoc, 0x80));
        Assert.Equal("breve", Encodings.GetName(PredefinedEncoding.PdfDoc, 0x18));
        Assert.Null(Encodings.GetName(PredefinedEncoding.Standard, 300));
    }

    [Fact]
    public void StandardEncodingGlyphsAreInHelvetica()
    {
        StandardFontMetrics h = Core14.Get("Helvetica");
        string?[] std = Encodings.Get(PredefinedEncoding.Standard);
        for (int c = 0; c < 256; c++)
        {
            Assert.Equal(std[c], h.BuiltInEncoding[c]);
        }
    }

    [Fact]
    public void Core14SpotValues()
    {
        StandardFontMetrics hv = Core14.Get("Helvetica");
        Assert.Equal(278, hv.GetWidth("space"));
        Assert.Equal(667, hv.GetWidth("A"));
        Assert.Equal(718, hv.Ascender);
        Assert.Equal(-207, hv.Descender);
        Assert.Equal(718, hv.CapHeight);
        Assert.Equal(new[] { -166, -225, 1000, 931 }, hv.FontBBox);
        Assert.Equal(722, Core14.Get("Times-Roman").GetWidth("A"));
        Assert.Equal(444, Core14.Get("Times-Roman").GetWidth("a"));
        Assert.All(Core14.Get("Courier-BoldOblique").Glyphs, g => Assert.Equal(600, g.Width));
        Assert.True(Core14.Get("Courier").IsFixedPitch);
        Assert.Equal(-12, Core14.Get("Helvetica-Oblique").ItalicAngle);
        Assert.Equal(631, Core14.Get("Symbol").GetWidth("alpha"));
        Assert.Equal(974, Core14.Get("ZapfDingbats").GetWidth("a1"));
        Assert.Equal("alpha", Core14.Get("Symbol").BuiltInEncoding[0x61]);
        Assert.Equal(14, Core14.Names.Length);
        Assert.Throws<ArgumentException>(() => Core14.Get("Helvetica-Bogus"));
    }

    [Theory]
    [InlineData("Helvetica", "Helvetica")]
    [InlineData("ABCDEF+Helvetica-Bold", "Helvetica-Bold")]
    [InlineData("Arial", "Helvetica")]
    [InlineData("ArialMT", "Helvetica")]
    [InlineData("Arial,Bold", "Helvetica-Bold")]
    [InlineData("Arial-BoldMT", "Helvetica-Bold")]
    [InlineData("Arial-BoldItalicMT", "Helvetica-BoldOblique")]
    [InlineData("Arial,Italic", "Helvetica-Oblique")]
    [InlineData("Arial Narrow", null)]
    [InlineData("TimesNewRoman", "Times-Roman")]
    [InlineData("TimesNewRomanPSMT", "Times-Roman")]
    [InlineData("TimesNewRomanPS-BoldItalicMT", "Times-BoldItalic")]
    [InlineData("TimesNewRoman,Bold", "Times-Bold")]
    [InlineData("Times New Roman", "Times-Roman")]
    [InlineData("CourierNew", "Courier")]
    [InlineData("CourierNewPS-BoldMT", "Courier-Bold")]
    [InlineData("CourierNew,Italic", "Courier-Oblique")]
    [InlineData("Symbol", "Symbol")]
    [InlineData("ZapfDingbats", "ZapfDingbats")]
    [InlineData("Calibri", null)]
    [InlineData("Helvetica-Light", "Helvetica")]
    [InlineData("", null)]
    public void Core14Aliases(string baseFont, string? expected) => Assert.Equal(expected, Core14.Resolve(baseFont));

    [Fact]
    public void Core14WidthsMatchTheAfmsForEveryGlyph()
    {
        string? up = FontEnv.UpstreamDir;
        if (up is null || !Directory.Exists(Path.Combine(up, "afm")))
        {
            Assert.Skip("Set ONEPDF_UPSTREAM to the unpacked upstream data (tools/fontdata/fetch.sh).");
        }

        int total = 0;
        foreach (string name in Core14.Names)
        {
            StandardFontMetrics m = Core14.Get(name);
            var seen = new HashSet<string>();
            foreach (string line in File.ReadLines(Path.Combine(up, "afm", name + ".afm")))
            {
                Match c = AfmChar().Match(line);
                if (!c.Success)
                {
                    continue;
                }

                string gn = c.Groups["n"].Value;
                Assert.True(m.TryGetGlyph(gn, out AfmGlyph g), name + " " + gn);
                Assert.Equal(int.Parse(c.Groups["w"].Value, CultureInfo.InvariantCulture), g.Width);
                Assert.Equal(int.Parse(c.Groups["c"].Value, CultureInfo.InvariantCulture), g.Code);
                Assert.Equal(
                    [int.Parse(c.Groups["a"].Value, CultureInfo.InvariantCulture), int.Parse(c.Groups["b"].Value, CultureInfo.InvariantCulture), int.Parse(c.Groups["x"].Value, CultureInfo.InvariantCulture), int.Parse(c.Groups["y"].Value, CultureInfo.InvariantCulture)],
                    new[] { g.LlX, g.LlY, g.UrX, g.UrY });
                seen.Add(gn);
                total++;
            }

            Assert.Equal(seen.Count, m.Glyphs.Length);
        }

        Assert.True(total > 3000, "glyphs checked: " + total);
    }

    [GeneratedRegex(@"^C\s+(?<c>-?\d+)\s*;\s*WX\s+(?<w>\d+)\s*;\s*N\s+(?<n>\S+)\s*;\s*B\s+(?<a>-?\d+)\s+(?<b>-?\d+)\s+(?<x>-?\d+)\s+(?<y>-?\d+)\s*;")]
    private static partial Regex AfmChar();

    [Fact]
    public void CffStandardData()
    {
        Assert.Equal(".notdef", FontData.CffStandardData.StandardString(0));
        Assert.Equal("space", FontData.CffStandardData.StandardString(1));
        Assert.Equal("Semibold", FontData.CffStandardData.StandardString(390));
        Assert.Equal("001.000", FontData.CffStandardData.StandardString(379));
        Assert.Equal(1, FontData.CffStandardData.StandardSid("space"));
        Assert.Equal(-1, FontData.CffStandardData.StandardSid("nonesuch"));
        Assert.Equal(228, FontData.CffStandardData.PredefinedCharset(0).Length);
        Assert.Equal(165, FontData.CffStandardData.PredefinedCharset(1).Length);
        Assert.Equal(86, FontData.CffStandardData.PredefinedCharset(2).Length);
        Assert.Equal(229, FontData.CffStandardData.PredefinedCharset(1)[1]);
        Assert.Equal(".notdef", FontData.CffStandardData.MacGlyphName(0));
        Assert.Equal("space", FontData.CffStandardData.MacGlyphName(3));
        Assert.Equal("dcroat", FontData.CffStandardData.MacGlyphName(257));
        Assert.Null(FontData.CffStandardData.MacGlyphName(258));
        Assert.Equal("fraction", FontData.CffStandardData.StandardString(FontData.CffStandardData.ExpertEncoding[0x2F]));
    }
}
