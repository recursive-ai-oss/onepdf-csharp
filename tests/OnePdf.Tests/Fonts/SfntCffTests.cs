using OnePdf.FontPrograms;

namespace OnePdf.Tests.Fonts;

public class SfntCffTests
{
    private static byte[] ReadTable(byte[] file, string tag, int faceOffset = 0)
    {
        int n = (file[faceOffset + 4] << 8) | file[faceOffset + 5];
        for (int i = 0; i < n; i++)
        {
            int e = faceOffset + 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(file, e, 4) == tag)
            {
                uint off = (uint)((file[e + 8] << 24) | (file[e + 9] << 16) | (file[e + 10] << 8) | file[e + 11]);
                uint len = (uint)((file[e + 12] << 24) | (file[e + 13] << 16) | (file[e + 14] << 8) | file[e + 15]);
                return file.AsSpan((int)off, (int)len).ToArray();
            }
        }

        throw new InvalidOperationException("no table " + tag);
    }

    private static string? LiberationSans()
    {
        string? dir = FontEnv.CacheDir;
        return dir is null ? null : FontEnv.CachedFonts().FirstOrDefault(p => p.EndsWith("LiberationSans-Regular.ttf", StringComparison.Ordinal));
    }

    [Fact]
    public void CachedOflFontsParseEveryGlyph()
    {
        string[] fonts = [.. FontEnv.CachedFonts()];
        if (fonts.Length == 0)
        {
            Assert.Skip("Set ONEPDF_FONT_CACHE to a directory with the Liberation and Noto fonts.");
        }

        foreach (string path in fonts)
        {
            FontProgram f = FontProgramLoader.Open(File.ReadAllBytes(path));
            GlyphSweep s = FontEnv.Sweep(f);
            Assert.True(s.Failed == 0, path + ": " + s.FirstError);
            Assert.True(s.Glyphs > 200, path);
            Assert.True(s.Empty < s.Glyphs / 2, path);
            Assert.Equal(0, s.NoAdvance);
        }
    }

    [Fact]
    public void LiberationSansBasics()
    {
        string? path = LiberationSans();
        if (path is null)
        {
            Assert.Skip("Liberation Sans is not in ONEPDF_FONT_CACHE.");
        }

        var f = (SfntFont)FontProgramLoader.Open(File.ReadAllBytes(path));
        Assert.Equal(FontProgramKind.TrueType, f.Kind);
        Assert.Equal(2048, f.UnitsPerEm);
        Assert.Equal(1.0 / 2048, f.FontMatrix[0]);
        Assert.Equal("LiberationSans", f.FontName);
        CmapSubtable uni = f.FindCmap(3, 1)!;
        int a = uni.Lookup('A');
        Assert.True(a > 0);
        Assert.Equal("A", f.GetGlyphName(a));
        Assert.Equal(a, f.FindGlyph("A"));
        Assert.True(f.TryGetAdvance(a, out float adv));
        Assert.Equal(1366, adv);
        Assert.False(f.GetOutline(a).IsEmpty);
        Assert.Equal(0, uni.Lookup(0x1F600));
        Assert.NotNull(f.FindCmap(1, 0));
        Assert.Contains(uni.Enumerate(), p => p.Code == 'A' && p.Glyph == a);
        Assert.Equal(a, f.FindUnicodeCmap()!.Lookup('A'));
    }

    [Fact]
    public void SystemSfntFontsParseEveryGlyph()
    {
        string[] files = [.. FontEnv.SystemFonts(".ttf", ".otf", ".ttc")];
        if (files.Length == 0)
        {
            Assert.Skip("No system fonts installed on this host.");
        }

        int faces = 0, cff = 0, cid = 0, glyphs = 0, bitmapOnly = 0;
        var problems = new List<string>();
        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            int count = SfntFont.FaceCount(data);
            for (int i = 0; i < count; i++)
            {
                SfntFont f;
                try
                {
                    f = SfntFont.Parse(data, i);
                }
                catch (FontProgramException ex) when (ex.Message.Contains("no outlines", StringComparison.Ordinal))
                {
                    bitmapOnly++; // color bitmap fonts (CBDT/sbix) carry no outlines and cannot be PDF font programs
                    continue;
                }
                catch (FontProgramException ex)
                {
                    problems.Add(path + "#" + i + ": " + ex.Message);
                    continue;
                }

                faces++;
                cff += f.Kind == FontProgramKind.OpenTypeCff ? 1 : 0;
                cid += f.IsCidKeyed ? 1 : 0;
                GlyphSweep s = FontEnv.Sweep(f);
                glyphs += s.Glyphs;
                if (s.Failed > 0)
                {
                    problems.Add(path + "#" + i + ": " + s.Failed + " bad glyphs, " + s.FirstError);
                }
            }
        }

        TestContext.Current.SendDiagnosticMessage($"system fonts: {files.Length} files, {faces} faces ({cff} OpenType-CFF, {cid} CID-keyed), {glyphs} glyphs, {bitmapOnly} bitmap-only skipped");
        Assert.True(faces > 50, "faces: " + faces);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.True(cff > 0, "no OpenType-CFF fonts seen");
        Assert.True(glyphs > 10000);
    }

    [Fact]
    public void CidKeyedCffInCollection()
    {
        string? path = FontEnv.SystemFonts(".ttc").FirstOrDefault(p => p.Contains("NotoSansCJK-Regular", StringComparison.Ordinal));
        if (path is null)
        {
            Assert.Skip("Noto Sans CJK collection not installed.");
        }

        byte[] data = File.ReadAllBytes(path);
        Assert.True(SfntFont.FaceCount(data) >= 2);
        var f = SfntFont.Parse(data, 0);
        Assert.Equal(FontProgramKind.OpenTypeCff, f.Kind);
        Assert.True(f.IsCidKeyed);
        Assert.NotNull(f.Cff);
        Assert.Null(f.BuiltInEncoding);
        Assert.True(f.GlyphCount > 30000);
        int gid = f.GlyphForCid(1);
        Assert.True(gid >= 0);
        Assert.Equal(1, f.Cff!.CidOfGlyph(gid));
        Assert.Equal(-1, f.GlyphForCid(10_000_000));
        Assert.True(f.TryGetAdvance(gid, out _));
        Assert.Throws<FontProgramException>(() => SfntFont.Parse(data, 99));
    }

    [Fact]
    public void BareCffFromAnOpenTypeFont()
    {
        string? path = FontEnv.SystemFonts(".otf").FirstOrDefault(p => p.EndsWith("NimbusSans-Regular.otf", StringComparison.Ordinal));
        if (path is null)
        {
            Assert.Skip("NimbusSans-Regular.otf not installed.");
        }

        byte[] file = File.ReadAllBytes(path);
        byte[] cffBytes = ReadTable(file, "CFF ");
        Assert.Equal(FontProgramKind.Cff, FontProgramLoader.Sniff(cffBytes));
        FontProgram bare = FontProgramLoader.Open(cffBytes);
        var sfnt = (SfntFont)FontProgramLoader.Open(file);
        Assert.Equal(sfnt.GlyphCount, bare.GlyphCount);
        Assert.False(bare.IsCidKeyed);
        Assert.NotNull(bare.BuiltInEncoding);
        int a = bare.FindGlyph("A");
        Assert.True(a > 0);
        Assert.Equal("A", bare.BuiltInEncoding![65]);
        Assert.Equal("space", bare.BuiltInEncoding[32]);
        Assert.True(bare.TryGetAdvance(a, out float w));
        Assert.True(sfnt.TryGetAdvance(a, out float w2));
        Assert.Equal(w2, w); // the charstring width and hmtx agree
        Assert.Equal(sfnt.GetOutline(a).Coords, bare.GetOutline(a).Coords);
        Assert.Equal(0.001, bare.FontMatrix[0]);
        Assert.Equal(a, sfnt.FindGlyph("A"));
        Assert.Equal(".notdef", bare.GetGlyphName(0));
        GlyphSweep s = FontEnv.Sweep(bare);
        Assert.Equal(0, s.Failed);
    }

    [Fact]
    public void SubsetWithoutOptionalTablesStillWorks()
    {
        string? path = LiberationSans();
        if (path is null)
        {
            Assert.Skip("Liberation Sans is not in ONEPDF_FONT_CACHE.");
        }

        // Rename the tables a subsetter commonly drops so the reader cannot find them.
        byte[] damaged = File.ReadAllBytes(path);
        int n = (damaged[4] << 8) | damaged[5];
        for (int i = 0; i < n; i++)
        {
            int e = 12 + (16 * i);
            string tag = System.Text.Encoding.ASCII.GetString(damaged, e, 4);
            if (tag is "cmap" or "post" or "name" or "OS/2" or "hhea" or "hmtx" or "head" or "maxp")
            {
                damaged[e] = (byte)'x';
            }
        }

        var f = (SfntFont)FontProgramLoader.Open(damaged);
        Assert.True(f.GlyphCount > 100);
        Assert.Empty(f.CmapSubtables);
        Assert.False(f.TryGetAdvance(5, out _));
        Assert.Equal(-1, f.FindGlyph("A"));
        Assert.Null(f.FontName);
        Assert.False(f.GetOutline(36).IsEmpty);
        Assert.Equal(1000, f.UnitsPerEm);
    }

    [Fact]
    public void TruncatedAndBrokenLocaDoNotCrash()
    {
        string? path = LiberationSans();
        if (path is null)
        {
            Assert.Skip("Liberation Sans is not in ONEPDF_FONT_CACHE.");
        }

        byte[] file = File.ReadAllBytes(path);
        foreach (int cut in new[] { 5000, 40000, file.Length / 2, file.Length - 10 })
        {
            try
            {
                FontProgram f = FontProgramLoader.Open(file.AsMemory(0, cut));
                FontEnv.Sweep(f);
            }
            catch (FontProgramException)
            {
            }
        }

        // Scribble over loca: offsets out of order and past the end must give empty or failed glyphs, never another exception type.
        byte[] loca = (byte[])file.Clone();
        int num = (loca[4] << 8) | loca[5];
        for (int i = 0; i < num; i++)
        {
            int e = 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(loca, e, 4) == "loca")
            {
                int off = (loca[e + 8] << 24) | (loca[e + 9] << 16) | (loca[e + 10] << 8) | loca[e + 11];
                var rnd = new Random(7);
                for (int k = 0; k < 400; k++)
                {
                    loca[off + rnd.Next(2000)] = (byte)rnd.Next(256);
                }
            }
        }

        FontProgram g = FontProgramLoader.Open(loca);
        GlyphSweep s = FontEnv.Sweep(g);
        Assert.True(s.Glyphs > 100);
    }
}
