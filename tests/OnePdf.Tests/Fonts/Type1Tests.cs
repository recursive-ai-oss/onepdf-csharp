using OnePdf.FontPrograms;

namespace OnePdf.Tests.Fonts;

public class Type1Tests
{
    public static TheoryData<string> Variants() => new() { "binary", "hex", "pfb", "leniv0", "leniv-1" };

    private static byte[] Build(string variant) => variant switch
    {
        "hex" => Type1Builder.Font(hexEexec: true),
        "pfb" => Type1Builder.Font(pfb: true),
        "leniv0" => Type1Builder.Font(lenIV: 0),
        "leniv-1" => Type1Builder.Font(lenIV: -1),
        _ => Type1Builder.Font(),
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void ParsesHeaderEncodingAndGlyphs(string variant)
    {
        FontProgram f = FontProgramLoader.Open(Build(variant));
        Assert.Equal(FontProgramKind.Type1, f.Kind);
        Assert.Equal("TestFont", f.FontName);
        Assert.Equal(6, f.GlyphCount);
        Assert.Equal([0.001, 0, 0, 0.001, 0, 0], f.FontMatrix);
        Assert.Equal([0.0, 0.0, 800.0, 800.0], f.FontBBox!);
        Assert.Equal("A", f.BuiltInEncoding![65]);
        Assert.Equal("acute", f.BuiltInEncoding[194]);
        Assert.Null(f.BuiltInEncoding[66]);
        Assert.Equal(1, f.FindGlyph("A"));
        Assert.Equal(0, f.FindGlyph(".notdef"));
        Assert.Equal(-1, f.FindGlyph("nope"));
        Assert.Equal("Aacute", f.GetGlyphName(3));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void SquareOutlineAndWidth(string variant)
    {
        FontProgram f = FontProgramLoader.Open(Build(variant));
        GlyphOutline o = f.GetOutline(f.FindGlyph("A"));
        Assert.Equal(new byte[] { GlyphOutline.Move, GlyphOutline.Line, GlyphOutline.Line, GlyphOutline.Line, GlyphOutline.Close }, o.Verbs);
        Assert.Equal(new float[] { 150, 0, 550, 0, 550, 500, 150, 500 }, o.Coords);
        Assert.True(f.TryGetAdvance(1, out float w));
        Assert.Equal(600, w);
    }

    [Fact]
    public void SeacComposesAccentOverBase()
    {
        FontProgram f = FontProgramLoader.Open(Type1Builder.Font());
        GlyphOutline o = f.GetOutline(f.FindGlyph("Aacute"));
        // Base square 150..550 x 0..500; accent square 30..130 x 600..700 moved by sbx + adx - asb = 220.
        Assert.Equal((150f, 0f, 550f, 700f), o.ControlBox());
        Assert.Equal(2, o.Verbs.Count(v => v == GlyphOutline.Move));
        Assert.Contains(250f, o.Coords);
        Assert.Contains(350f, o.Coords);
        Assert.True(f.TryGetAdvance(f.FindGlyph("Aacute"), out float w));
        Assert.Equal(600, w);
    }

    [Fact]
    public void FlexBecomesTwoCurves()
    {
        FontProgram f = FontProgramLoader.Open(Type1Builder.Font());
        GlyphOutline o = f.GetOutline(f.FindGlyph("F"));
        Assert.Equal(new byte[] { GlyphOutline.Move, GlyphOutline.Cubic, GlyphOutline.Cubic, GlyphOutline.Line, GlyphOutline.Close }, o.Verbs);
        Assert.Equal(new float[] { 0, 0, 50, 0, 100, 20, 150, 30, 200, 20, 250, 0, 300, 0, 300, 100 }, o.Coords);
    }

    [Fact]
    public void HintReplacementAndDivision()
    {
        FontProgram f = FontProgramLoader.Open(Type1Builder.Font());
        GlyphOutline o = f.GetOutline(f.FindGlyph("H"));
        Assert.Equal(new byte[] { GlyphOutline.Move, GlyphOutline.Line, GlyphOutline.Line, GlyphOutline.Close }, o.Verbs);
        Assert.Equal(10 + (1000f / 3), o.Coords[2], 0.01);
        Assert.Equal(120, o.Coords[5]);
    }

    [Fact]
    public void OutOfRangeGlyphsAreEmpty()
    {
        FontProgram f = FontProgramLoader.Open(Type1Builder.Font());
        Assert.True(f.GetOutline(-1).IsEmpty);
        Assert.True(f.GetOutline(99).IsEmpty);
        Assert.False(f.TryGetAdvance(99, out _));
    }

    [Fact]
    public void TruncatedFontsThrowTypedErrorsOrParse()
    {
        byte[] full = Type1Builder.Font();
        for (int cut = 0; cut < full.Length; cut += 7)
        {
            try
            {
                FontProgram f = FontProgramLoader.Open(full.AsMemory(0, cut));
                Sweepy(f);
            }
            catch (FontProgramException)
            {
            }
        }

        static void Sweepy(FontProgram f)
        {
            for (int g = 0; g < f.GlyphCount; g++)
            {
                try
                {
                    f.GetOutline(g);
                    f.TryGetAdvance(g, out _);
                }
                catch (FontProgramException)
                {
                }
            }
        }
    }

    [Fact]
    public void SubroutineRecursionIsBounded()
    {
        // A subroutine that calls itself must hit the depth bound, not the stack.
        byte[] loop = Type1Builder.Charstring(0, "callsubr");
        var subrs = new byte[]?[] { loop };
        var interp = new Type1Interpreter(subrs, widthOnly: false);
        Assert.Throws<FontProgramException>(() => interp.Run(Type1Builder.Charstring(0, "callsubr")));
    }

    [Fact]
    public void SystemType1FontsParseEveryGlyph()
    {
        string[] files = [.. FontEnv.SystemFonts(".pfb", ".pfa", ".t1")];
        if (files.Length == 0)
        {
            Assert.Skip("No Type 1 fonts installed on this host.");
        }

        foreach (string path in files)
        {
            FontProgram f = FontProgramLoader.Open(File.ReadAllBytes(path));
            Assert.Equal(FontProgramKind.Type1, f.Kind);
            GlyphSweep s = FontEnv.Sweep(f);
            Assert.True(s.Failed == 0, path + ": " + s.FirstError);
            Assert.True(s.Glyphs > 100, path);
            Assert.True(f.FindGlyph(".notdef") >= 0, path);
            Assert.Equal(0, s.NoAdvance);
            Assert.True(f.FontMatrix[0] > 0);
        }
    }
}
