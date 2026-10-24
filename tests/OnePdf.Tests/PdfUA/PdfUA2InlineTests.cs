using OnePdf.Tests.Creation;
using OnePdf.Tests.PdfA;
using static OnePdf.Tests.PdfUA.PdfUATests;

namespace OnePdf.Tests.PdfUA;

/// <summary>
/// The PDF/UA-2 structure rules for Em and Strong, Caption, Ruby, Warichu and MathML (ISO 14289-2 8.2.5 and the structure type table):
/// a file that breaks one is refused with the clause, a conforming one passes both the check and veraPDF's ua2 flavour (black box).
/// </summary>
public class PdfUA2InlineTests
{
    private static readonly PdfUAOptions Part2 = new() { Part = PdfUAPart.Part2 };

    // Builds a tagged PDF 2.0 document whose Document element holds what <paramref name="body"/> writes; each helper call draws one line.
    private sealed class Ctx
    {
        public required PdfDocumentWriter W;
        public required PdfStreamPageBuilder P;
        public double Y = 360;

        public void Leaf(string type, string text, PdfStructureElementOptions? options = null)
        {
            PdfStructureElement e = W.BeginStructureElement(type, options);
            P.BeginTaggedContent(e);
            P.DrawText(text, 20, Y);
            P.EndMarkedContent();
            W.EndStructureElement();
            Y -= 16;
        }

        public void Begin(string type, PdfStructureElementOptions? options = null) => W.BeginStructureElement(type, options);

        public void End() => W.EndStructureElement();
    }

    private static byte[] Build(Action<Ctx> body)
    {
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true, Language = "en-US", Version = "2.0" }))
        {
            w.SetInfo("Title", "Inline structure test");
            PdfCreatedFont font = w.AddFont(CreationSupport.RequireFont(PdfADocs.Serif));
            w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(300, 400);
            p.SetFont(font, 11);
            body(new Ctx { W = w, P = p });
            p.Commit();
            w.EndStructureElement();
            w.Finish();
        }

        return ms.ToArray();
    }

    private static PdfStructureElementOptions Alt(string text) => new() { AlternateText = text };

    private static void Ruby(Ctx c, params string[] kids)
    {
        c.Begin("P");
        c.Begin("Ruby");
        foreach (string k in kids)
        {
            c.Leaf(k, k == "RP" ? "(" : k);
        }

        c.End();
        c.End();
    }

    // Opens the built file for editing, lets the test change the dictionaries, then returns it (not yet prepared or saved).
    private static PdfDocument Open(byte[] built, Action<PdfDocument>? change = null)
    {
        PdfDocument doc = Editable(built);
        change?.Invoke(doc);
        return doc;
    }

    private static List<PdfUAViolation> Refusal(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => doc.Save(ms, new PdfSaveOptions { PdfUA = Part2 }));
        Assert.Equal(0, ms.Length);
        Assert.All(ex.Violations, v => Assert.Equal(2, v.Part));
        return [.. ex.Violations];
    }

    private static void AssertAccepted(Action<PdfDocument>? change, byte[] built)
    {
        using PdfDocument doc = Open(built, change);
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        Assert.Empty(back.CheckPdfUA(PdfUAPart.Part2));
        if (VeraPdf.Path is not null)
        {
            List<string> failures = VeraPdf.Validate(ms.ToArray(), "ua2");
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
    }

    // The veraPDF run for a refused file: the same file, prepared and saved without the PDF/UA check, is faulted by veraPDF too.
    private static List<string> VeraFaults(byte[] built, Action<PdfDocument>? change = null)
    {
        VeraPdf.Require();
        using PdfDocument doc = Open(built, change);
        doc.PreparePdfUA(Part2);
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { MinimumVersion = "2.0" });
        return VeraPdf.Validate(ms.ToArray(), "ua2");
    }

    // ---- Ruby (8.2.5.23) ----

    [Fact]
    public void RubyNeedsRbAndRtOrRbRpRtRp()
    {
        AssertAccepted(null, Build(c => Ruby(c, "RB", "RT")));
        AssertAccepted(null, Build(c => Ruby(c, "RB", "RP", "RT", "RP")));
    }

    [Theory]
    [InlineData("RT", "RB")]
    [InlineData("RB")]
    [InlineData("RB", "RP", "RT")]
    [InlineData("RB", "RT", "RT")]
    public void RubyWithAnotherSequenceIsRefused(params string[] kids)
    {
        byte[] built = Build(c => Ruby(c, kids));
        using PdfDocument doc = Open(built);
        Assert.Contains(Refusal(doc), v => v.Clause == "8.2.5.23");
        Assert.Contains(VeraFaults(built), f => f.StartsWith("8.2.5.23-", StringComparison.Ordinal));
    }

    // ---- Warichu (8.2.5.24) ----

    [Fact]
    public void WarichuNeedsWpWtWp()
    {
        AssertAccepted(null, Build(c =>
        {
            c.Begin("P");
            c.Begin("Warichu");
            c.Leaf("WP", "(");
            c.Leaf("WT", "note");
            c.Leaf("WP", ")");
            c.End();
            c.End();
        }));

        byte[] bad = Build(c =>
        {
            c.Begin("P");
            c.Begin("Warichu");
            c.Leaf("WT", "note");
            c.End();
            c.End();
        });
        using PdfDocument doc = Open(bad);
        Assert.Contains(Refusal(doc), v => v.Clause == "8.2.5.24");
        Assert.Contains(VeraFaults(bad), f => f.StartsWith("8.2.5.24-", StringComparison.Ordinal));
    }

    // ---- Caption (8.2.5.27) ----

    private static void Table(Ctx c, params string[] order)
    {
        c.Begin("Table");
        foreach (string part in order)
        {
            if (part == "Caption")
            {
                c.Leaf("Caption", "Results");
            }
            else
            {
                c.Begin("TR");
                c.Leaf("TH", "head", new PdfStructureElementOptions { Attributes = PdfStructureAttributes.TableHeader("Column") });
                c.End();
            }
        }

        c.End();
    }

    [Fact]
    public void CaptionIsTheFirstOrTheLastChild()
    {
        AssertAccepted(null, Build(c => Table(c, "Caption", "TR", "TR")));
        AssertAccepted(null, Build(c => Table(c, "TR", "TR", "Caption")));

        byte[] bad = Build(c => Table(c, "TR", "Caption", "TR"));
        using PdfDocument doc = Open(bad);
        Assert.Contains(Refusal(doc), v => v.Clause == "8.2.5.27");
        Assert.Contains(VeraFaults(bad), f => f.StartsWith("8.2.5.27-", StringComparison.Ordinal));
    }

    // ---- Em and Strong, and the structure type table ----

    [Fact]
    public void EmAndStrongInsideTextPass()
    {
        AssertAccepted(null, Build(c =>
        {
            c.Begin("P");
            c.Leaf("Em", "stressed");
            c.Begin("Strong");
            c.Leaf("Em", "both");
            c.End();
            c.Begin("Span");
            c.Leaf("Strong", "inside a span");
            c.End();
            c.End();
        }));
    }

    [Theory]
    [InlineData("Em", "P")]
    [InlineData("Strong", "L")]
    [InlineData("Em", "Table")]
    [InlineData("Strong", "Sect")]
    public void EmAndStrongMayNotHoldBlockElements(string inline, string block)
    {
        byte[] bad = Build(c =>
        {
            c.Begin("P");
            c.Begin(inline);
            if (block == "L")
            {
                c.Begin("L", new PdfStructureElementOptions { Attributes = PdfStructureAttributes.List("Ordered") });
                c.Begin("LI");
                c.Leaf("Lbl", "1.");
                c.Leaf("LBody", "item");
                c.End();
                c.End();
            }
            else
            {
                c.Begin(block);
                c.Leaf("P", "text");
                c.End();
            }

            c.End();
            c.End();
        });
        using PdfDocument doc = Open(bad);
        Assert.Contains(Refusal(doc), v => v.Clause == "Table 5" && v.Message.Contains($"contains a {block} element", StringComparison.Ordinal));
        Assert.Contains(VeraFaults(bad), f => f.Contains($"Table 5. {inline}-{block}", StringComparison.Ordinal) || f.Contains($"{inline}-{block}", StringComparison.Ordinal));
    }

    [Fact]
    public void EmDirectlyUnderDocumentIsRefused()
    {
        byte[] bad = Build(c => c.Leaf("Em", "loose"));
        using PdfDocument doc = Open(bad);
        Assert.Contains(Refusal(doc), v => v.Clause == "Table 5" && v.Message.Contains("Document element contains a Em", StringComparison.Ordinal));
        Assert.NotEmpty(VeraFaults(bad));
    }

    [Fact]
    public void ATableHoldingMarkedContentDirectlyIsRefused()
    {
        byte[] bad = Build(c =>
        {
            c.Begin("Table");
            // Marked content straight in the Table: tagged content whose element is the table itself.
            PdfStructureElement t = c.W.CurrentStructureElement!;
            c.P.BeginTaggedContent(t);
            c.P.DrawText("direct", 20, c.Y);
            c.P.EndMarkedContent();
            c.Y -= 16;
            c.Begin("TR");
            c.Leaf("TD", "cell");
            c.End();
            c.End();
        });
        using PdfDocument doc = Open(bad);
        Assert.Contains(Refusal(doc), v => v.Clause == "Table 5" && v.Message.Contains("holds marked content directly", StringComparison.Ordinal));
        Assert.NotEmpty(VeraFaults(bad));
    }

    [Fact]
    public void TwoCaptionsInOneTableAreRefused()
    {
        byte[] bad = Build(c => Table(c, "Caption", "TR", "Caption"));
        using PdfDocument doc = Open(bad);
        Assert.Contains(Refusal(doc), v => v.Clause == "Table 5" && v.Message.Contains("more than one Caption", StringComparison.Ordinal));
        Assert.NotEmpty(VeraFaults(bad));
    }

    // ---- MathML (8.2.5.29) ----

    private static void MarkMathML(PdfDocument doc)
    {
        PdfDictionary root = doc.StructureTreeRoot!;
        var ns = new PdfDictionary();
        ns.SetEntry("Type", new PdfName("Namespace"));
        ns.SetEntry("NS", PdfString.FromText("http://www.w3.org/1998/Math/MathML"));
        PdfReference r = doc.AddObject(ns);
        PdfArray list = root.GetArray("Namespaces") ?? new PdfArray();
        if (root.GetArray("Namespaces") is null)
        {
            root.SetEntry("Namespaces", list);
        }

        list.Append(r);
        var stack = new Stack<PdfObject>();
        stack.Push(root.Get("K")!);
        while (stack.Count > 0)
        {
            PdfObject o = stack.Pop();
            if (o is PdfArray a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    stack.Push(a[i]!);
                }
            }
            else if (o is PdfDictionary d)
            {
                if (d.GetName("S") is "math" or "mi" or "mo")
                {
                    d.SetEntry("NS", r);
                }

                if (d.Get("K") is { } k)
                {
                    stack.Push(k);
                }
            }
        }
    }

    private static void Math(Ctx c, string parent)
    {
        c.Begin(parent, parent == "Formula" ? Alt("x plus one") : null);
        c.Begin("math");
        c.Leaf("mi", "x");
        c.Leaf("mo", "+");
        c.End();
        c.End();
    }

    [Fact]
    public void MathMLBelowAFormulaPasses()
    {
        AssertAccepted(MarkMathML, Build(c => Math(c, "Formula")));
    }

    [Fact]
    public void MathMLOutsideAFormulaIsRefused()
    {
        byte[] bad = Build(c => Math(c, "P"));
        using PdfDocument doc = Open(bad, MarkMathML);
        Assert.Contains(Refusal(doc), v => v.Clause == "8.2.5.29" && v.Message.Contains("'math'", StringComparison.Ordinal));
        Assert.Contains(VeraFaults(bad, MarkMathML), f => f.StartsWith("8.2.5.29-", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileAssociatedWithAFormulaNeedsItsRelationshipAndData()
    {
        static Action<PdfDocument> Attach(bool relationship, bool data) => doc =>
        {
            PdfDictionary formula = FindFormula(doc.StructureTreeRoot!);
            var spec = new PdfDictionary();
            spec.SetEntry("Type", new PdfName("Filespec"));
            spec.SetEntry("F", PdfString.FromText("formula.mml"));
            spec.SetEntry("UF", PdfString.FromText("formula.mml"));
            if (relationship)
            {
                spec.SetEntry("AFRelationship", new PdfName("Supplement"));
            }

            if (data)
            {
                PdfStream s = doc.CreateStream("<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><mi>x</mi></math>"u8);
                s.Dictionary.SetEntry("Type", new PdfName("EmbeddedFile"));
                s.Dictionary.SetEntry("Subtype", new PdfName("application#2Fmathml+xml"));
                var ef = new PdfDictionary();
                ef.SetEntry("F", doc.AddObject(s));
                spec.SetEntry("EF", ef);
            }

            var af = new PdfArray();
            af.Append(doc.AddObject(spec));
            formula.SetEntry("AF", af);
        };

        byte[] built = Build(c => c.Leaf("Formula", "x+1", Alt("x plus one")));
        using (PdfDocument ok = Open(built, Attach(true, true)))
        {
            using var ms = new MemoryStream();
            ok.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = Part2 });
        }

        using PdfDocument noRelationship = Open(built, Attach(false, true));
        Assert.Contains(Refusal(noRelationship), v => v.Clause == "8.2.5.29" && v.Message.Contains("/AFRelationship", StringComparison.Ordinal));
        using PdfDocument noData = Open(built, Attach(true, false));
        Assert.Contains(Refusal(noData), v => v.Clause == "8.2.5.29" && v.Message.Contains("/EF", StringComparison.Ordinal));
    }

    private static PdfDictionary FindFormula(PdfDictionary root)
    {
        var stack = new Stack<PdfObject>();
        stack.Push(root.Get("K")!);
        while (stack.Count > 0)
        {
            PdfObject o = stack.Pop();
            if (o is PdfArray a)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    stack.Push(a[i]!);
                }
            }
            else if (o is PdfDictionary d)
            {
                if (d.GetName("S") == "Formula")
                {
                    return d;
                }

                if (d.Get("K") is { } k)
                {
                    stack.Push(k);
                }
            }
        }

        throw new InvalidOperationException("no Formula element");
    }

    [Fact]
    public void PartOneIsNotAffectedByTheseRules()
    {
        // A Caption in the middle and a Ruby with the wrong children are part-2 rules; the part-1 check does not report them.
        byte[] built = Build(c =>
        {
            Table(c, "TR", "Caption", "TR");
            Ruby(c, "RT", "RB");
        });
        using PdfDocument doc = PdfDocument.Open(built);
        Assert.DoesNotContain(doc.CheckPdfUA(PdfUAPart.Part1), v => v.Message.Contains("Caption", StringComparison.Ordinal) || v.Message.Contains("Ruby", StringComparison.Ordinal));
    }
}
