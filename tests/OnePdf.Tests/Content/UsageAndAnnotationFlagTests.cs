using System.Text;

namespace OnePdf.Tests.Content;

/// <summary>Optional content usage application (<c>/AS</c>) and the NoZoom / NoRotate annotation flags.</summary>
public class UsageAndAnnotationFlagTests
{
    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    // ---- optional content: /AS ----

    private const string Props = "/Resources << /Properties << /A 10 0 R /B 11 0 R /C 12 0 R >> >>";
    private const string Content = "/OC /A BDC 0 0 1 1 re f EMC /OC /B BDC 0 0 2 2 re f EMC /OC /C BDC 0 0 3 3 re f EMC";

    private static byte[] LayerPdf(string auto) => TestPdf.Build(Content, Props, b =>
    {
        // A: on in the configuration, but its usage says it is off when viewing.
        b.Obj(10, "<< /Type /OCG /Name (A) /Usage << /View << /ViewState /OFF >> >> >>");
        // B: off in the configuration, but its usage says it is on when viewing.
        b.Obj(11, "<< /Type /OCG /Name (B) /Usage << /View << /ViewState /ON >> /Print << /PrintState /OFF >> >> >>");
        // C: usage says off when viewing, but no usage application names it for the View event.
        b.Obj(12, "<< /Type /OCG /Name (C) /Usage << /View << /ViewState /OFF >> >> >>");
    }, objects: 13, catalogEntries: $"/OCProperties << /OCGs [10 0 R 11 0 R 12 0 R] /D << /OFF [11 0 R] {auto} >> >>");

    private static string[] Fills(byte[] data, PdfInterpretOptions? options = null) => TestPdf.Lines(TestPdf.Text(data, options), "fill");

    [Fact]
    public void ViewUsageApplicationOverridesTheConfiguredState()
    {
        string auto = "/AS [ << /Event /View /Category [/View] /OCGs [10 0 R 11 0 R] >> << /Event /Print /Category [/Print] /OCGs [12 0 R] >> ]";
        string[] fills = Fills(LayerPdf(auto));
        Assert.Equal(2, fills.Length);
        Assert.Contains("L 2 0", fills[0], StringComparison.Ordinal); // B is now visible
        Assert.Contains("L 3 0", fills[1], StringComparison.Ordinal); // C is untouched
    }

    [Fact]
    public void WithoutUsageApplicationTheConfigurationDecides()
    {
        string[] fills = Fills(LayerPdf(string.Empty));
        Assert.Equal(2, fills.Length);
        Assert.Contains("L 1 0", fills[0], StringComparison.Ordinal); // A visible, B hidden
        Assert.Contains("L 3 0", fills[1], StringComparison.Ordinal);
    }

    [Fact]
    public void UsageApplicationWithoutTheViewCategoryChangesNothing()
    {
        string auto = "/AS [ << /Event /View /Category [/Zoom /Language] /OCGs [10 0 R 11 0 R] >> ]";
        Assert.Equal(Fills(LayerPdf(string.Empty)), Fills(LayerPdf(auto)));
    }

    [Fact]
    public void CallerOverrideStillWinsOverUsageApplication()
    {
        string auto = "/AS [ << /Event /View /Category [/View] /OCGs [10 0 R 11 0 R] >> ]";
        var options = new PdfInterpretOptions { OptionalContentOverride = g => g.GetString("Name")?.GetText() == "A" ? true : null };
        Assert.Equal(3, Fills(LayerPdf(auto), options).Length);
    }

    // ---- annotations: NoZoom / NoRotate ----

    private static byte[] AnnotPdf(string annotEntries, string pageEntries = "") => TestPdf.Build("", "/Annots [10 0 R] " + pageEntries, b =>
    {
        b.Obj(10, $"<< /Type /Annot /Subtype /Stamp /Rect [20 30 70 55] /AP << /N 11 0 R >> {annotEntries} >>");
        b.Stream(11, "/Type /XObject /Subtype /Form /BBox [0 0 100 50]", Bytes("0 0 100 50 re f"));
    }, objects: 12);

    private static string Ctm(byte[] data, PdfInterpretOptions? options = null)
    {
        string t = TestPdf.Text(data, options);
        int at = t.IndexOf("begin-annot", StringComparison.Ordinal);
        Assert.True(at >= 0, t);
        int c = t.IndexOf("ctm=[", at, StringComparison.Ordinal);
        return t.Substring(c, t.IndexOf(']', c) - c + 1);
    }

    [Fact]
    public void PlainAnnotationScalesWithTheOutput()
    {
        Assert.Equal("ctm=[1 0 0 -1 40 340]", Ctm(AnnotPdf(""), new PdfInterpretOptions { Scale = 2 }));
    }

    [Fact]
    public void NoZoomKeepsTheUnscaledSizeAtTheUpperLeftCorner()
    {
        // Scale 2: the page maps the rect's upper-left corner (20, 55) to (40, 290). The appearance stays at 1:1 (BBox 100x50 onto
        // a 50x25 rect = 0.5) and hangs from that point.
        Assert.Equal("ctm=[0.5 0 0 -0.5 40 315]", Ctm(AnnotPdf("/F 8"), new PdfInterpretOptions { Scale = 2 }));
    }

    [Fact]
    public void NoZoomChangesNothingAtUnitScale()
    {
        Assert.Equal(Ctm(AnnotPdf("")), Ctm(AnnotPdf("/F 8")));
    }

    [Fact]
    public void NoRotateKeepsTheAppearanceUprightOnARotatedPage()
    {
        // /Rotate 90 puts the rect's upper-left corner (20, 55) at (55, 20); the appearance is not turned.
        Assert.Equal("ctm=[0 0.5 0.5 0 30 20]", Ctm(AnnotPdf("", "/Rotate 90")));
        Assert.Equal("ctm=[0.5 0 0 -0.5 55 45]", Ctm(AnnotPdf("/F 16", "/Rotate 90")));
    }

    [Fact]
    public void NoRotateAndNoZoomCombine()
    {
        // Rotate 90 and scale 2: corner (20, 55) -> (110, 40); upright and unscaled from there.
        Assert.Equal("ctm=[0.5 0 0 -0.5 110 65]", Ctm(AnnotPdf("/F 24", "/Rotate 90"), new PdfInterpretOptions { Scale = 2 }));
    }
}
