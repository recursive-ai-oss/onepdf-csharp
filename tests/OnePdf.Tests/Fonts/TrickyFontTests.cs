using System.Runtime.CompilerServices;
using OnePdf.FontPrograms;
using OnePdf.PdfFonts;
using OnePdf.Tests.PdfFonts;

namespace OnePdf.Tests.Fonts;

/// <summary>
/// Fonts whose composite glyphs are only placed correctly by their own instructions (MingLiU and relatives). The font under test is
/// the MingLiU subset embedded in a committed real-world file: its glyph 3784 (U+6700) is a composite of 14 components whose
/// stored offsets are provisional. The expected boxes were checked against a black-box render of the page: at 10 pt and 1200 dpi
/// the glyph occupies the same pixels as the program's outline (overlap 0.91 against 0.46 for the plain outline).
/// </summary>
public class TrickyFontTests
{
    private const string File1 = "cjk-04-zen2631893.pdf";

    // Object numbers of the font files in that PDF: MingLiU and PMingLiU (DynaLab, 2001), DFKaiShu-SB-Estd-BF.
    private const int MingLiU = 73;
    private const int PMingLiU = 70;
    private const int DfKai = 65;

    private static string PdfPath([CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "realworld", "pdf", File1);
            if (File.Exists(c))
            {
                return c;
            }
        }

        throw new FileNotFoundException(File1);
    }

    private static byte[] FontFile(int objectNumber)
    {
        using PdfDocument doc = PdfDocument.Open(PdfPath());
        return ((PdfStream)doc.GetObject(objectNumber)!).GetDecodedBytes().ToArray();
    }

    private static (float MinX, float MinY, float MaxX, float MaxY) Box(byte[] font, GlyphPrograms programs, int gid = 3784) =>
        SfntFont.Parse(font, 0, programs).GetOutline(gid).ControlBox();

    private static void AssertBox((float MinX, float MinY, float MaxX, float MaxY) box, float minX, float minY, float maxX, float maxY)
    {
        Assert.InRange(box.MinX, minX - 1, minX + 1);
        Assert.InRange(box.MinY, minY - 1, minY + 1);
        Assert.InRange(box.MaxX, maxX - 1, maxX + 1);
        Assert.InRange(box.MaxY, maxY - 1, maxY + 1);
    }

    [Fact]
    public void ProgramPlacesTheComponentsOfTheCompositeGlyph()
    {
        byte[] font = FontFile(MingLiU);

        // Plain outline (what the font gave before): components at their stored offsets, a lopsided glyph.
        AssertBox(Box(font, GlyphPrograms.Never), 32, -148, 925, 773);

        // Detected by name, so the default path runs the programs: 964 x 941 units, as the reference render measures (10 pt at 1200 dpi: 156 x 153 px).
        var detected = SfntFont.Parse(font, 0);
        Assert.True(detected.RunsGlyphPrograms);
        Assert.Null(detected.GlyphProgramFailure);
        AssertBox(detected.GetOutline(3784).ControlBox(), 32, -168, 996, 773);
    }

    [Fact]
    public void PageGlyphUsesTheProgramOutline()
    {
        using PdfDocument doc = PdfDocument.Open(PdfPath());
        var device = new GlyphCatcher(3784);
        doc.Pages[0].Interpret(device, null);
        Assert.NotNull(device.Bounds);

        // Em units of a 1024-unit font.
        PdfBounds b = device.Bounds.Value;
        Assert.InRange(b.Left * 1024, 31, 33);
        Assert.InRange(b.Right * 1024, 995, 997);
        Assert.InRange(b.Bottom * 1024, -169, -167);
        Assert.InRange(b.Top * 1024, 772, 774);
    }

    [Fact]
    public void RelatedFontsAreRecognisedAndOtherFontsAreNot()
    {
        Assert.True(SfntFont.Parse(FontFile(PMingLiU), 0).RunsGlyphPrograms);
        Assert.True(SfntFont.Parse(FontFile(DfKai), 0).RunsGlyphPrograms);
    }

    // Subsets often lose or rename the name table; the program tables are copied unchanged, so their checksums still identify the font.
    [Fact]
    public void FontWithoutNamesIsRecognisedByItsProgramChecksums()
    {
        foreach (int obj in new[] { MingLiU, PMingLiU, DfKai })
        {
            byte[] font = FontFile(obj);
            Assert.True(SfntFont.Parse(font, 0).RunsGlyphPrograms);

            byte[] anonymous = (byte[])font.Clone();
            RenameTable(anonymous, "name", "nam_");
            var f = SfntFont.Parse(anonymous, 0);
            Assert.True(f.RunsGlyphPrograms, "recognised by checksums: object " + obj);

            // One changed byte in fpgm and no names: an unrelated font, plain outlines, nothing executed.
            byte[] other = (byte[])anonymous.Clone();
            other[TableOffset(other, "fpgm") + 3] ^= 0x55;
            Assert.False(SfntFont.Parse(other, 0).RunsGlyphPrograms);
        }
    }

    [Fact]
    public void ChecksumMatchesTheSpecification()
    {
        // OpenType 'head' checksum rules: big-endian words, the last partial word zero padded.
        Assert.Equal(0x01020304u, TrickyFonts.Checksum([1, 2, 3, 4]));
        Assert.Equal(0x01020304u + 0x05000000u, TrickyFonts.Checksum([1, 2, 3, 4, 5]));
        Assert.Equal(0u, TrickyFonts.Checksum([]));
        Assert.Equal(0u, TrickyFonts.Checksum([0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 1])); // the sum wraps modulo 2^32
    }

    // The same glyph-program failure paths with hostile programs: the glyph falls back to its plain outline, quickly, with a reason.
    [Fact]
    public void LoopingGlyphProgramFallsBackToThePlainOutline()
    {
        byte[] font = FontFile(MingLiU);
        byte[] plain = (byte[])font.Clone();
        byte[] hostile = (byte[])font.Clone();

        // The composite's 62 instruction bytes start "ba 01 22 01 61": replace them with an endless backward jump.
        int at = IndexOf(hostile, [0xBA, 0x01, 0x22, 0x01, 0x61, 0x01, 0x5D]);
        Assert.True(at > 0);
        hostile[at] = 0xB8;
        hostile[at + 1] = 0xFF;
        hostile[at + 2] = 0xFD; // PUSHW -3
        hostile[at + 3] = 0x1C; // JMPR from here back to the start

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = SfntFont.Parse(hostile, 0);
        Assert.True(f.RunsGlyphPrograms);
        GlyphOutline o = f.GetOutline(3784);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "took " + sw.Elapsed);
        Assert.NotNull(f.GlyphProgramFailure);
        GlyphOutline raw = SfntFont.Parse(plain, 0, GlyphPrograms.Never).GetOutline(3784);
        Assert.Equal(raw.Verbs, o.Verbs);
        Assert.Equal(raw.Coords, o.Coords);
    }

    [Fact]
    public void StackOverflowingGlyphProgramFallsBackToThePlainOutline()
    {
        byte[] font = FontFile(MingLiU);
        int at = IndexOf(font, [0xBA, 0x01, 0x22, 0x01, 0x61, 0x01, 0x5D]);
        Assert.True(at > 0);
        byte[] hostile = (byte[])font.Clone();
        byte[] code = [0xB0, 0x01, 0xB8, 0xFF, 0xFB, 0x1C]; // push 1; push -5; JMPR back to the start: the stack only grows
        code.CopyTo(hostile, at);
        var f = SfntFont.Parse(hostile, 0);
        GlyphOutline o = f.GetOutline(3784);
        Assert.NotNull(f.GlyphProgramFailure);
        Assert.Equal(SfntFont.Parse(font, 0, GlyphPrograms.Never).GetOutline(3784).Coords, o.Coords);
    }

    [Fact]
    public void LoopingControlValueProgramLeavesPlainOutlines()
    {
        byte[] font = FontFile(MingLiU);
        byte[] hostile = (byte[])font.Clone();
        int prep = TableOffset(hostile, "prep");
        hostile[prep] = 0xB8;
        hostile[prep + 1] = 0xFF;
        hostile[prep + 2] = 0xFD;
        hostile[prep + 3] = 0x1C;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = SfntFont.Parse(hostile, 0);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "took " + sw.Elapsed);
        Assert.False(f.RunsGlyphPrograms);
        Assert.NotNull(f.GlyphProgramFailure);
        Assert.Equal(SfntFont.Parse(font, 0, GlyphPrograms.Never).GetOutline(3784).Coords, f.GetOutline(3784).Coords);
    }

    [Fact]
    public void BrokenProgramsGiveOneWarningAndTheGlyphIsStillDrawn()
    {
        byte[] font = FontFile(MingLiU);
        byte[] hostile = (byte[])font.Clone();
        int at = IndexOf(hostile, [0xBA, 0x01, 0x22, 0x01, 0x61, 0x01, 0x5D]);
        new byte[] { 0xB8, 0xFF, 0xFD, 0x1C }.CopyTo(hostile, at);
        using PdfDocument doc = FontPdf.Open(b =>
        {
            b.Obj(10, "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+MingLiU /Encoding /Identity-H /DescendantFonts [11 0 R] >>");
            b.Obj(11, "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+MingLiU /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 12 0 R /CIDToGIDMap /Identity /DW 1000 >>");
            b.Obj(12, "<< /Type /FontDescriptor /FontName /ABCDEF+MingLiU /Flags 4 /FontFile2 13 0 R >>");
            b.Stream(13, "/Length1 " + hostile.Length, hostile);
        });
        PdfFont f = FontPdf.Font(doc, 10);
        List<FontGlyph> glyphs = FontPdf.Decode(f, 0x0E, 0xC8, 0x0E, 0xC8);
        Assert.Equal(2, glyphs.Count);
        Assert.Equal(3784, glyphs[0].Gid);
        Assert.False(glyphs[0].GetOutline().IsEmpty);
        Assert.False(glyphs[1].GetOutline().IsEmpty);
        Assert.Single(doc.Diagnostics, d => d.Message.Contains("TrueType instructions", StringComparison.Ordinal));
    }

    [Fact]
    public void PlainFontsNeverRunTheirPrograms()
    {
        // JhengHei and Calibri subsets in the same file have programs but no tricky signature: byte-identical to the plain path.
        using PdfDocument doc = PdfDocument.Open(PdfPath());
        int checkedFonts = 0;
        for (int n = 1; n < 140; n++)
        {
            if (doc.GetObject(n) is not PdfStream s || s.Dictionary.Get("Length1") is null)
            {
                continue;
            }

            byte[] data = s.GetDecodedBytes().ToArray();
            if (data.Length < 12 || data[0] != 0 || data[1] != 1)
            {
                continue;
            }

            SfntFont f = SfntFont.Parse(data, 0);
            if (n is MingLiU or PMingLiU or DfKai)
            {
                continue;
            }

            Assert.False(f.RunsGlyphPrograms, "object " + n);
            checkedFonts++;
        }

        Assert.True(checkedFonts >= 5, "fonts checked: " + checkedFonts);
    }

    [Fact]
    public void ProgramsOfTheFontsInTheFileRunWithoutFailureOnEveryGlyph()
    {
        foreach (int obj in new[] { MingLiU, PMingLiU, DfKai })
        {
            var f = SfntFont.Parse(FontFile(obj), 0);
            for (int g = 0; g < f.GlyphCount; g++)
            {
                f.GetOutline(g);
            }

            Assert.Null(f.GlyphProgramFailure);
        }
    }

    private static int TableOffset(byte[] font, string tag)
    {
        int n = (font[4] << 8) | font[5];
        for (int i = 0; i < n; i++)
        {
            int e = 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(font, e, 4) == tag)
            {
                return (font[e + 8] << 24) | (font[e + 9] << 16) | (font[e + 10] << 8) | font[e + 11];
            }
        }

        throw new InvalidOperationException(tag);
    }

    private static void RenameTable(byte[] font, string tag, string to)
    {
        int n = (font[4] << 8) | font[5];
        for (int i = 0; i < n; i++)
        {
            int e = 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(font, e, 4) == tag)
            {
                System.Text.Encoding.ASCII.GetBytes(to).CopyTo(font, e);
                return;
            }
        }

        throw new InvalidOperationException(tag);
    }

    private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);

    // Collects the control box (in em units) of the outline of one glyph id.
    private sealed class GlyphCatcher(int glyphId) : PdfDevice
    {
        public PdfBounds? Bounds { get; private set; }

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            foreach (PdfGlyph g in run.Glyphs.Span)
            {
                if (g.GlyphId == glyphId && !g.Outline.IsEmpty && Bounds is null)
                {
                    Bounds = g.Outline.ControlBounds();
                }
            }
        }
    }
}
