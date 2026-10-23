using System.Text;

namespace OnePdf.Tests.Content;

/// <summary>Text knockout state and the combined fill-and-stroke call (ISO 32000-2 9.3.8, 11.7.4.4).</summary>
public class KnockoutTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "testdata", "generated", "pdf"));

    private static string Interpret(string file)
    {
        using PdfDocument doc = PdfDocument.Open(Path.Combine(Root(), file));
        doc.FontProvider = OnePdf.Fonts.FontsPackage.Provider;
        return TestPdf.Record(doc).ToText();
    }

    private static string[] Order(string text) =>
        [.. text.Split('\n').Select(l => l.TrimStart()).Where(l => l.StartsWith("text ", StringComparison.Ordinal) || l.StartsWith("gs ", StringComparison.Ordinal))
            .Select(l => l.StartsWith("gs ", StringComparison.Ordinal) ? (l.Contains(" tk=0", StringComparison.Ordinal) ? "gs-off" : "gs-on") : "text")];

    // The state line that applies to the text record showing the given string (hex of its codes).
    private static bool TkOffFor(string text, string str)
    {
        string hex = Convert.ToHexString(Encoding.Latin1.GetBytes(str)).ToLowerInvariant();
        string? gs = null;
        foreach (string raw in text.Split('\n'))
        {
            string l = raw.TrimStart();
            if (l.StartsWith("gs ", StringComparison.Ordinal))
            {
                gs = l;
            }
            else if (l.StartsWith("text ", StringComparison.Ordinal) && l.Contains("codes=" + hex + " ", StringComparison.Ordinal))
            {
                Assert.NotNull(gs);
                return gs.Contains(" tk=0", StringComparison.Ordinal);
            }
        }

        throw new InvalidOperationException("no text record for " + str);
    }

    [Fact]
    public void TextKnockoutFollowsGsQAndFormsAndGroups()
    {
        string t = Interpret("raw-knockout-tk.pdf");
        Assert.False(TkOffFor(t, "default"));
        Assert.True(TkOffFor(t, "off in q"));
        Assert.False(TkOffFor(t, "after Q"));
        Assert.True(TkOffFor(t, "off by gs"));
        Assert.False(TkOffFor(t, "on in q"));
        Assert.True(TkOffFor(t, "off restored"));
        Assert.True(TkOffFor(t, "inherit"));      // a form inherits the caller's state
        Assert.False(TkOffFor(t, "formon"));      // and may change it
        Assert.True(TkOffFor(t, "groupoff"));     // also inside a transparency group
        Assert.False(TkOffFor(t, "on again"));    // the form's changes did not leak out
    }

    [Fact]
    public void TkDefaultsToTrueAndOnlyAnExplicitFalseTurnsItOff()
    {
        var state = default(PdfGraphicsState);
        Assert.False(state.TextKnockout); // a default struct is not a document state
        string on = TestPdf.Text("BT 1 0 0 1 10 10 Tm ET");
        Assert.DoesNotContain("tk=", on);
        string t = TestPdf.Text("/G gs 0 0 10 10 re f", extra: b => b.Obj(5, "<< /TK false >>"), pageEntries: "/Resources << /ExtGState << /G 5 0 R >> >>", objects: 6);
        Assert.Contains(" tk=0", t);
    }

    [Fact]
    public void CombinedOperatorsReachTheDeviceAsOneCall()
    {
        string t = Interpret("raw-knockout-paint.pdf");
        string[] fs = TestPdf.Lines(t, "fillstroke");
        // B, B*, b, b*, the clipped B twice (W B, and B after W n).
        Assert.Equal(6, fs.Length);
        Assert.StartsWith("fillstroke nonzero ", fs[0]);
        Assert.StartsWith("fillstroke evenodd ", fs[1]);
        Assert.EndsWith("Z", fs[2]); // b closes the path
        Assert.EndsWith("Z", fs[3]);
        Assert.StartsWith("fillstroke evenodd ", fs[3]);
        // f then S stays two records; f* alone stays a fill.
        Assert.Equal(2, TestPdf.Lines(t, "fill").Length);
        Assert.Single(TestPdf.Lines(t, "stroke"));
        Assert.Contains("fa=0.5 sa=0.5", t);
        // The clip pushed by W with B is still delivered, after the paint.
        Assert.Equal(2, TestPdf.Lines(t, "clip").Length);
    }

    [Fact]
    public void ClipWithBPaintsBeforeClipping()
    {
        string t = TestPdf.Text("0 0 10 10 re W B");
        string[] lines = [.. t.Split('\n').Select(l => l.TrimStart()).Where(l => l.StartsWith("fillstroke", StringComparison.Ordinal) || l.StartsWith("clip", StringComparison.Ordinal))];
        Assert.StartsWith("fillstroke", lines[0]);
        Assert.StartsWith("clip", lines[1]);
    }

    private sealed class Counting : IPdfDevice
    {
        public List<string> Calls { get; } = [];

        public void BeginPage(in PdfPageInfo page) { }
        public void EndPage() { }
        public void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Calls.Add("fill");
        public void StrokePath(in PdfPath path, in PdfGraphicsState state) => Calls.Add("stroke");
        public void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Calls.Add("clip");
        public void PushTextClip(in PdfGraphicsState state) { }
        public void PopClip() { }
        public void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state) { }
        public void BeginType3Glyph(in PdfType3GlyphInfo glyph, in PdfGraphicsState state) { }
        public void EndType3Glyph() { }
        public void DrawImage(in PdfImage image, in PdfGraphicsState state) { }
        public void PaintShading(PdfShading shading, in PdfGraphicsState state) { }
        public void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state) { }
        public void EndGroup() { }
        public void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state) { }
        public void EndSoftMask() { }
        public void BeginMarkedContent(string tag, PdfDictionary? properties) { }
        public void EndMarkedContent() { }
        public void BeginAnnotation(in PdfAnnotationInfo annotation) { }
        public void EndAnnotation() { }
    }

    [Fact]
    public void ADeviceWithoutAnOverrideSeesFillThenStroke()
    {
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("0 0 10 10 re B  0 0 5 5 re f  0 0 5 5 re S  0 0 10 10 re b*"));
        var dev = new Counting();
        doc.Pages[0].Interpret(dev);
        Assert.Equal(["fill", "stroke", "fill", "stroke", "fill", "stroke"], dev.Calls);
    }

    private sealed class Overriding : PdfDevice
    {
        public List<string> Calls { get; } = [];

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Calls.Add("fill");
        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => Calls.Add("stroke");
        public override void FillStrokePath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Calls.Add("fillstroke" + (evenOdd ? "*" : string.Empty));
    }

    [Fact]
    public void PdfDeviceBaseDefaultsToFillThenStrokeAndCanOverride()
    {
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("0 0 10 10 re B*  0 0 5 5 re f"));
        var dev = new Overriding();
        doc.Pages[0].Interpret(dev);
        Assert.Equal(["fillstroke*", "fill"], dev.Calls);
    }

    [Fact]
    public void DisplayListReplaysTheCombinedItemAsOneCall()
    {
        using PdfDocument doc = TestPdf.Open(TestPdf.Build("0 0 10 10 re B  20 20 10 10 re f  20 20 10 10 re S"));
        PdfDisplayList list = TestPdf.Record(doc);
        Assert.Equal("FillStroke", list.GetItemKind(1));
        Assert.Equal("Fill", list.GetItemKind(2));
        Assert.Equal("Stroke", list.GetItemKind(3));
        PdfBounds b = list.GetItemBounds(1);
        PdfBounds fillOnly = list.GetItemBounds(2);
        Assert.True(b.Right - b.Left > fillOnly.Right - fillOnly.Left, "box covers the stroke");

        var dev = new Overriding();
        list.Replay(dev);
        Assert.Equal(["fillstroke", "fill", "stroke"], dev.Calls);

        var plain = new Counting();
        list.Replay(plain);
        Assert.Equal(["fill", "stroke", "fill", "stroke"], plain.Calls);
    }
}
