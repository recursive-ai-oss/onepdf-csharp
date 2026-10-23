using System.Text;

namespace OnePdf.Tests.Content;

/// <summary>Appearance generation: widgets and simple markup without a stored appearance, <c>/NeedAppearances</c>, opt-out, and hostile input.</summary>
public class AppearanceGenerationTests
{
    internal sealed class AnnotRecord
    {
        public string Subtype = string.Empty;
        public int Obj = -1;
        public StringBuilder Text = new();
        public PdfBounds? TextBounds;
        public PdfBounds? InkBounds;
        public int Paints;

        public PdfBounds? All => TextBounds is { } t ? (InkBounds is { } i ? t.Union(i) : t) : InkBounds;
    }

    /// <summary>Collects, per annotation, the text drawn and the output-space bounds of text and painted paths.</summary>
    internal sealed class Collector : PdfDevice
    {
        public List<AnnotRecord> Records { get; } = [];

        public int GlyphRunsOutsideAnnotations { get; private set; }

        public List<string> Problems { get; } = [];

        private AnnotRecord? _cur;

        public override void BeginAnnotation(in PdfAnnotationInfo annotation)
        {
            _cur = new AnnotRecord { Subtype = annotation.Subtype, Obj = annotation.Annotation.Reference?.Number ?? -1 };
            Records.Add(_cur);
        }

        public override void EndAnnotation() => _cur = null;

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            if (_cur is null)
            {
                GlyphRunsOutsideAnnotations++;
                return;
            }

            ReadOnlySpan<PdfGlyph> gs = run.Glyphs.Span;
            for (int i = 0; i < gs.Length; i++)
            {
                _cur.Text.Append(gs[i].Unicode ?? "?");
                PdfMatrix m = run.GlyphMatrix(i).Then(state.Ctm);
                foreach ((double x, double y) in new[] { (0.0, -0.2), (gs[i].Width, -0.2), (0.0, 0.75), (gs[i].Width, 0.75) })
                {
                    (double ox, double oy) = m.Transform(x, y);
                    if (!double.IsFinite(ox + oy))
                    {
                        Problems.Add("non-finite glyph box");
                    }

                    _cur.TextBounds = Grow(_cur.TextBounds, ox, oy);
                }
            }
        }

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Paint(path, state);

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => Paint(path, state);

        private void Paint(in PdfPath path, in PdfGraphicsState state)
        {
            if (_cur is null)
            {
                return;
            }

            _cur.Paints++;
            ReadOnlySpan<double> p = path.Points.Span;
            for (int i = 0; i + 1 < p.Length; i += 2)
            {
                (double ox, double oy) = state.Ctm.Transform(p[i], p[i + 1]);
                if (!double.IsFinite(ox + oy))
                {
                    Problems.Add("non-finite path point");
                }

                _cur.InkBounds = Grow(_cur.InkBounds, ox, oy);
            }
        }

        private static PdfBounds Grow(PdfBounds? b, double x, double y) =>
            b is { } v ? new PdfBounds(Math.Min(v.Left, x), Math.Min(v.Bottom, y), Math.Max(v.Right, x), Math.Max(v.Top, y)) : new PdfBounds(x, y, x, y);
    }

    internal static string TestDataPath(string relative, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", relative);
            if (File.Exists(c))
            {
                return c;
            }
        }

        throw new FileNotFoundException(relative);
    }

    private static Collector Run(PdfDocument doc, PdfInterpretOptions? options = null)
    {
        var c = new Collector();
        doc.Pages[0].Interpret(c, options);
        Assert.Empty(c.Problems);
        return c;
    }

    private static bool HasDiagnostic(PdfDocument doc, PdfSeverity severity, string fragment) =>
        doc.Diagnostics.Any(d => d.Severity == severity && d.Message.Contains(fragment, StringComparison.Ordinal));

    // ---- generated files ----

    [Fact]
    public void WidgetsWithoutAppearanceAreDrawnForEveryKind()
    {
        using PdfDocument doc = PdfDocument.Open(TestDataPath("generated/pdf/raw-appearance-widgets.pdf"));
        Collector c = Run(doc);
        string all = string.Join("|", c.Records.Select(r => r.Text.ToString()));
        Assert.Contains("Hello World", all);
        Assert.Contains("Right aligned", all);
        Assert.Contains("Centered", all);
        Assert.Contains("Auto size text that is long enough to shrink", all);
        Assert.Contains("12345", all);
        Assert.Contains("******", all);
        Assert.DoesNotContain("secret", all);
        Assert.Contains("Bravo", all); // combo box shows the display text of the exported value
        Assert.Contains("Delta", all);
        Assert.Contains("Submit", all);
        Assert.Contains("Caf\u00e9", all);
        Assert.Contains("Dashed", all);

        // Every widget but the signature field draws something; check marks and radio buttons are vector shapes.
        Assert.Equal(c.Records.Count, c.Records.Count(r => r.All is not null));
        Assert.Equal(22, c.Records.Count);
        Assert.True(HasDiagnostic(doc, PdfSeverity.Info, "generated appearance"));
        Assert.True(HasDiagnostic(doc, PdfSeverity.Warning, "could not be encoded"));
    }

    [Fact]
    public void TurningGenerationOffGivesTheEarlierBehaviour()
    {
        using PdfDocument doc = PdfDocument.Open(TestDataPath("generated/pdf/raw-appearance-widgets.pdf"));
        Collector c = Run(doc, new PdfInterpretOptions { GenerateAppearances = false });
        Assert.Empty(c.Records);
        Assert.True(HasDiagnostic(doc, PdfSeverity.Info, "nothing was drawn"));
        Assert.False(HasDiagnostic(doc, PdfSeverity.Info, "generated appearance"), string.Join("\n", doc.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public void NeedAppearancesReplacesStaleTextAndKeepsStoredButtons()
    {
        using PdfDocument doc = PdfDocument.Open(TestDataPath("generated/pdf/raw-appearance-widgets-needap.pdf"));
        Collector c = Run(doc);
        string all = string.Join("|", c.Records.Select(r => r.Text.ToString()));
        // The push button and the signature field keep their stored (stale) appearance; every text and choice field is rebuilt.
        Assert.Equal(2, all.Split("STALE").Length - 1);
        Assert.Contains("Hello World", all);
        Assert.Contains("Bravo", all);

        using PdfDocument stored = PdfDocument.Open(TestDataPath("generated/pdf/raw-appearance-widgets-needap.pdf"));
        Collector off = Run(stored, new PdfInterpretOptions { GenerateAppearances = false });
        Assert.Contains("STALE", string.Join("|", off.Records.Select(r => r.Text.ToString())));
    }

    [Fact]
    public void EverySimpleMarkupSubtypeIsDrawnInsideThePage()
    {
        foreach (string file in new[] { "raw-appearance-markup.pdf", "raw-appearance-markup-needap.pdf" })
        {
            using PdfDocument doc = PdfDocument.Open(TestDataPath("generated/pdf/" + file));
            Collector c = Run(doc);
            string[] expected = ["Square", "Circle", "Line", "Line", "Polygon", "PolyLine", "Ink", "Highlight", "Underline", "StrikeOut", "Squiggly", "FreeText", "Text", "Text", "Text"];
            Assert.Equal(expected, c.Records.Select(r => r.Subtype).ToArray());
            foreach (AnnotRecord r in c.Records)
            {
                Assert.NotNull(r.All);
                PdfBounds b = r.All!.Value;
                Assert.True(b.Left >= -1 && b.Bottom >= -1 && b.Right <= 501 && b.Top <= 781, r.Subtype + " is off the page");
            }

            Assert.Contains("Free text with a callout", c.Records[11].Text.ToString());
        }
    }

    [Fact]
    public void GeneratedAppearanceIsAvailableAsData()
    {
        using PdfDocument doc = PdfDocument.Open(TestDataPath("generated/pdf/raw-appearance-widgets.pdf"));
        PdfAnnotation first = doc.Pages[0].Annotations[0];
        PdfGeneratedAppearance? g = first.GenerateAppearance();
        Assert.NotNull(g);
        Assert.Equal("text field", g!.Description);
        Assert.Equal("Form", g.Dictionary.GetName("Subtype") is "Form" ? "Form" : null);
        Assert.Equal(first.Rect, g.Rect);
        string content = Encoding.Latin1.GetString(g.Content.Span);
        Assert.Contains("(Hello World) Tj", content);
        Assert.Equal(content, Encoding.Latin1.GetString(first.GenerateAppearance()!.Content.Span)); // deterministic
        Assert.Null(first.GetAppearanceStream()); // the document itself is untouched
    }

    [Fact]
    public void DocumentsWithStoredAppearancesAreNotTouched()
    {
        // The authoring tool sets /NeedAppearances, which asks for regeneration; with it off the stored appearances are used.
        using PdfDocument doc = PdfDocument.Open(TestDataPath("generated/pdf/lo-form-fields.pdf"));
        doc.Catalog.GetDictionary("AcroForm")!.Set("NeedAppearances", OnePdf.Appearance.Cos.Bool(false));
        Run(doc);
        Assert.False(HasDiagnostic(doc, PdfSeverity.Info, "generated appearance"), string.Join("\n", doc.Diagnostics.Select(d => d.Message)));
    }

    // ---- stored appearance versus generated ----

    // Tolerances (points, output space), for widgets whose stored appearance was written by an authoring tool: the generated
    // text equals the stored text (ignoring whitespace) and the bounds of everything drawn match the stored bounds within
    // Tolerance on each edge. Tools place the baseline and size automatically sized text by their own rules, so a few points
    // of difference is expected; the files below agree within about one point except for auto-sized text.
    private const double Tolerance = 4;

    private static readonly string[] AuthoredForms =
    [
        "govdocs/084532.pdf", "realworld/pdf/form-01-gov336501.pdf", "realworld/pdf/form-05-gov336494.pdf", "realworld/pdf/form-06-gov882412.pdf",
        "realworld/pdf/form-07-gov756294.pdf", "realworld/pdf/form-09-gov672040.pdf", "verapdf/7.18.1-t03-pass-f.pdf", "generated/pdf/lo-form-fields.pdf",
        "generated/pdf/raw-acroform.pdf",
    ];

    private static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    [Fact]
    public void GeneratedAppearancesMatchTheStoredOnesOfAuthoringToolForms()
    {
        int compared = 0, textCompared = 0;
        var problems = new StringBuilder();
        foreach (string rel in AuthoredForms)
        {
            string path;
            try
            {
                path = TestDataPath(rel);
            }
            catch (FileNotFoundException)
            {
                continue; // a file that is not in this checkout (the set is a manifest-listed selection)
            }

            using PdfDocument stored = PdfDocument.Open(path);
            using PdfDocument stripped = PdfDocument.Open(path);
            for (int p = 0; p < Math.Min(stored.Pages.Count, 3); p++)
            {
                var s = new Collector();
                stored.Pages[p].Interpret(s, new PdfInterpretOptions { GenerateAppearances = false });
                foreach (PdfAnnotation a in stripped.Pages[p].Annotations)
                {
                    a.Dictionary.Set("AP", PdfNull.Instance);
                }

                var g = new Collector();
                stripped.Pages[p].Interpret(g, null);
                Assert.Empty(g.Problems);
                foreach (AnnotRecord a in s.Records.Where(r => r.All is not null && r.Subtype == "Widget"))
                {
                    AnnotRecord? b = g.Records.FirstOrDefault(r => r.Obj == a.Obj);
                    string where = $"{rel} page {p} object {a.Obj}";
                    if (b?.All is null)
                    {
                        problems.AppendLine(where + ": nothing generated for a stored appearance that draws");
                        continue;
                    }

                    compared++;
                    string ta = Letters(a.Text.ToString()), tb = Letters(b.Text.ToString());
                    if (ta.Length > 0)
                    {
                        textCompared++;
                        if (ta != tb)
                        {
                            problems.AppendLine($"{where}: text '{ta}' vs '{tb}' (the stored text may come from something other than the value)");
                        }
                    }

                    double d = Delta(a.All, b.All);
                    if (ta == tb && d > Tolerance)
                    {
                        problems.AppendLine($"{where}: bounds differ by {d:F1}");
                    }
                }
            }
        }

        // The two allowed exceptions are the radio buttons of raw-acroform.pdf, whose stored off-state draws a shape that
        // nothing in the widget (no /MK) describes.
        Assert.True(compared >= 20 && textCompared >= 10, $"compared {compared}, with text {textCompared}\n{problems}");
        string[] bad = problems.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(bad.Length <= 2 && bad.All(l => l.Contains("nothing generated", StringComparison.Ordinal)), string.Join("\n", bad));
    }

    private static double Delta(PdfBounds? a, PdfBounds? b)
    {
        if (a is not { } x || b is not { } y)
        {
            return a is null && b is null ? 0 : double.PositiveInfinity;
        }

        return new[] { Math.Abs(x.Left - y.Left), Math.Abs(x.Right - y.Right), Math.Abs(x.Bottom - y.Bottom), Math.Abs(x.Top - y.Top) }.Max();
    }

    // ---- hostile input ----

    private static (PdfDocument Doc, Collector Dev) Hostile(string annotations, string extraObjects = "", string catalogEntries = "", PdfInterpretOptions? options = null, PdfLimits? limits = null)
    {
        byte[] data = TestPdf.Build(string.Empty, "/Annots [5 0 R]", b =>
        {
            b.Obj(5, annotations);
            if (extraObjects.Length > 0)
            {
                b.Obj(6, extraObjects);
            }
        }, objects: 7, catalogEntries: catalogEntries);
        PdfDocument doc = PdfDocument.Open(data, limits is null ? null : new PdfOpenOptions { Limits = limits });
        var c = new Collector();
        doc.Pages[0].Interpret(c, options);
        return (doc, c);
    }

    private const string Widget = "<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (t) /Rect [10 10 200 40] ";

    [Fact]
    public void HugeMaxLenCombIsBoundedByTheItemLimit()
    {
        var (doc, c) = Hostile(Widget + "/Ff 16777216 /MaxLen 2147483647 /V (12345) /DA (/Helv 12 Tf 0 g) /MK << /BC [0 0 0] >> >>");
        using (doc)
        {
            Assert.Single(c.Records);
            Assert.Equal("12345", c.Records[0].Text.ToString());
            Assert.Empty(c.Problems);
        }
    }

    [Fact]
    public void ZeroAndNegativeRectanglesAreSkippedOrNormalised()
    {
        foreach (string rect in new[] { "[10 10 10 10]", "[50 50 10 10]", "[0 0 0 0]", "[0 0 100000000000 5]", "[0 0 5]" })
        {
            var (doc, c) = Hostile("<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (t) /V (x) /DA (/Helv 0 Tf 0 g) /Rect " + rect + " >>");
            using (doc)
            {
                Assert.Empty(c.Problems);
                if (rect == "[50 50 10 10]")
                {
                    Assert.Equal("x", c.Records[0].Text.ToString());
                }
                else
                {
                    Assert.True(HasDiagnostic(doc, PdfSeverity.Warning, "rectangle") || c.Records.Count == 0 || HasDiagnostic(doc, PdfSeverity.Warning, "Rect"), rect + "\n" + string.Join("\n", doc.Diagnostics.Select(d => d.Message)) + c.Records.Count);
                }
            }
        }
    }

    [Fact]
    public void MissingDaAndNoAcroFormStillDrawWithAStandardFont()
    {
        var (doc, c) = Hostile("<< /Type /Annot /Subtype /Widget /F 4 /FT /Tx /T (t) /V (no default appearance) /Rect [10 10 200 40] >>");
        using (doc)
        {
            Assert.Equal("no default appearance", c.Records[0].Text.ToString());
        }
    }

    [Fact]
    public void ParentCyclesAndSelfKidsTerminate()
    {
        var (doc, c) = Hostile("<< /Type /Annot /Subtype /Widget /F 4 /T (t) /Parent 6 0 R /Rect [10 10 200 40] >>", "<< /FT /Tx /Parent 5 0 R /Kids [5 0 R 6 0 R] /V (cyclic) >>");
        using (doc)
        {
            Assert.Empty(c.Problems);
            Assert.Equal("cyclic", c.Records[0].Text.ToString());
        }
    }

    [Fact]
    public void ADeepParentChainIsCutAtTheRecursionLimit()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [10 0 R] >>");
        const int depth = 5000;
        b.Obj(10, "<< /Type /Annot /Subtype /Widget /F 4 /T (w) /Parent 11 0 R /Rect [10 10 190 40] >>");
        for (int i = 11; i < 10 + depth; i++)
        {
            b.Obj(i, $"<< /Parent {i + 1} 0 R >>");
        }

        b.Obj(10 + depth, "<< /FT /Tx /V (deep) >>");
        long x = b.XrefTable(11 + depth);
        b.Trailer($"/Size {11 + depth} /Root 1 0 R", x);
        using PdfDocument doc = PdfDocument.Open(b.ToArray());
        var c = new Collector();
        doc.Pages[0].Interpret(c, null);
        Assert.Empty(c.Problems); // no crash; the value lies beyond the limit so the field has no type and is skipped
    }

    [Fact]
    public void AMillionCharacterValueIsCutOffWithADiagnostic()
    {
        string v = new('a', 1_000_000);
        var (doc, c) = Hostile(Widget + "/V (" + v + ") /DA (/Helv 12 Tf 0 g) >>");
        using (doc)
        {
            Assert.Empty(c.Problems);
            Assert.True(HasDiagnostic(doc, PdfSeverity.Warning, "longer than 65536 characters"));
            Assert.True(c.Records[0].Text.Length <= 65536);
        }

        string words = string.Concat(Enumerable.Repeat("word ", 200_000));
        var (doc2, c2) = Hostile(Widget + "/Ff 4096 /V (" + words + ") /DA (/Helv 0 Tf 0 g) >>");
        using (doc2)
        {
            Assert.Empty(c2.Problems);
        }
    }

    [Fact]
    public void MarkupWithHugeOrBrokenGeometryIsBounded()
    {
        string many = string.Join(' ', Enumerable.Range(0, 400_000).Select(i => (i % 100).ToString()));
        foreach (string annot in new[]
        {
            "<< /Type /Annot /Subtype /Ink /F 4 /Rect [0 0 100 100] /InkList [[" + many + "]] >>",
            "<< /Type /Annot /Subtype /Highlight /F 4 /Rect [0 0 100 100] /QuadPoints [" + many + "] >>",
            "<< /Type /Annot /Subtype /Polygon /F 4 /Rect [0 0 100 100] /Vertices [" + many + "] >>",
            "<< /Type /Annot /Subtype /Line /F 4 /Rect [0 0 100 100] /L [0 0 1e300 1e300] >>",
            "<< /Type /Annot /Subtype /Squiggly /F 4 /Rect [0 0 100 100] /QuadPoints [0 5000000 1e7 5000000 0 0 1e7 0] >>",
            "<< /Type /Annot /Subtype /Square /F 4 /Rect [0 0 100 100] /BS << /W 1e9 >> >>",
            "<< /Type /Annot /Subtype /Square /F 4 /Rect [0 0 100 100] /RD [1e9 -5 NaN 0] /C [] /IC [1 2] >>",
        })
        {
            var (doc, c) = Hostile(annot);
            using (doc)
            {
                Assert.Empty(c.Problems);
            }
        }
    }

    [Fact]
    public void ALineEndingArrayShorterThanTwoIsAccepted()
    {
        // Found by fuzzing: /LE [/OpenArrow] and /LE [] used to index past the end of the array.
        using PdfDocument doc = PdfDocument.Open(TestDataPath("repros/appearance-short-line-ending-array.pdf"));
        Collector c = Run(doc);
        Assert.Equal(2, c.Records.Count);
    }

    [Fact]
    public void ProblemsAreRecordedNotThrownWhenFontsAreMissing()
    {
        var (doc, c) = Hostile(Widget + "/V (x) /DA (/Nope 12 Tf 0 g) >>", catalogEntries: "/AcroForm << /Fields [5 0 R] /DR << /Font << /Bad 6 0 R >> >> >>", extraObjects: "<< /Type /Font /Subtype /Type0 /BaseFont /X >>");
        using (doc)
        {
            Assert.Equal("x", c.Records[0].Text.ToString());
            Assert.True(HasDiagnostic(doc, PdfSeverity.Info, "/Nope"));
        }
    }
}
