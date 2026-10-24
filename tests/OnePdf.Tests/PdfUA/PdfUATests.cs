using OnePdf.Tests.Creation;
using OnePdf.Tests.PdfA;

namespace OnePdf.Tests.PdfUA;

/// <summary>
/// PDF/UA-1 and PDF/A level A on documents made with the streaming writer: the veraPDF validator (black box) accepts what a save
/// produces, and each requirement OnePDF refuses is refused with its reason.
/// </summary>
public class PdfUATests
{
    /// <summary>What goes into a generated tagged document; the defaults are a document that meets PDF/UA.</summary>
    private sealed class Spec
    {
        // Heading levels in order; 0 stands for the unnumbered H.
        public bool Title = true;
        public string? Language = "en-US";
        public bool FigureAlt = true;
        public int[] Headings = [1, 2];
        public bool TableHeader = true;
        public bool UnmappedType;
        public bool UntaggedText;
        public bool EmbeddedFont = true;
        public bool LinkInTree = true;
        public bool ListWrongChildren;
        public bool IrregularTable;
    }

    private static byte[] Build(Spec s)
    {
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true, Language = s.Language }))
        {
            if (s.Title)
            {
                w.SetInfo("Title", "Accessible test document");
            }

            w.SetInfo("Author", "A. Writer");
            PdfCreatedFont font = s.EmbeddedFont ? w.AddFont(CreationSupport.RequireFont(PdfADocs.Serif)) : w.AddStandardFont("Helvetica");
            if (s.UnmappedType)
            {
                // Left unmapped on purpose.
            }
            else
            {
                w.MapRole("Callout", "P");
            }

            PdfCreatedImage image = w.AddRawImage(2, 2, PdfImageColor.Rgb, new byte[12]);
            PdfStructureElement doc = w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(300, 400);
            p.SetFont(font, 11);
            double y = 370;
            foreach (int level in s.Headings)
            {
                PdfStructureElement h = w.BeginStructureElement(level == 0 ? "H" : "H" + level);
                p.BeginTaggedContent(h);
                p.DrawText("Heading " + level, 20, y);
                p.EndMarkedContent();
                w.EndStructureElement();
                y -= 20;
            }

            PdfStructureElement para = w.BeginStructureElement(s.UnmappedType ? "Callout" : "P");
            p.BeginTaggedContent(para);
            p.DrawText("A paragraph of text.", 20, y);
            p.EndMarkedContent();
            w.EndStructureElement();
            y -= 20;

            PdfStructureElement list = w.BeginStructureElement("L");
            for (int i = 0; i < 2; i++)
            {
                PdfStructureElement li = w.BeginStructureElement(s.ListWrongChildren ? "P" : "LI");
                PdfStructureElement lbl = w.BeginStructureElement("Lbl");
                p.BeginTaggedContent(lbl);
                p.DrawText("-", 20, y);
                p.EndMarkedContent();
                w.EndStructureElement();
                PdfStructureElement body = w.BeginStructureElement("LBody");
                p.BeginTaggedContent(body);
                p.DrawText("item " + i, 32, y);
                p.EndMarkedContent();
                w.EndStructureElement();
                w.EndStructureElement();
                y -= 14;
            }

            w.EndStructureElement();

            PdfStructureElement table = w.BeginStructureElement("Table");
            for (int r = 0; r < 2; r++)
            {
                PdfStructureElement tr = w.BeginStructureElement("TR");
                int cols = s.IrregularTable && r == 1 ? 1 : 2;
                for (int c = 0; c < cols; c++)
                {
                    bool header = r == 0 && s.TableHeader;
                    PdfStructureElement cell = w.BeginStructureElement(header ? "TH" : "TD", header ? new PdfStructureElementOptions { Attributes = PdfStructureAttributes.TableHeader("Column") } : null);
                    p.BeginTaggedContent(cell);
                    p.DrawText($"r{r}c{c}", 20 + (c * 60), y);
                    p.EndMarkedContent();
                    w.EndStructureElement();
                }

                w.EndStructureElement();
                y -= 14;
            }

            w.EndStructureElement();

            PdfStructureElement fig = w.BeginStructureElement("Figure", s.FigureAlt ? new PdfStructureElementOptions { AlternateText = "A black square" } : null);
            p.BeginTaggedContent(fig);
            p.DrawImage(image, 20, y - 50, 50, 50);
            p.EndMarkedContent();
            w.EndStructureElement();
            y -= 70;

            PdfStructureElement link = w.BeginStructureElement("Link");
            p.BeginTaggedContent(link);
            p.DrawText("The project page", 20, y);
            p.EndMarkedContent();
            p.AddLink(new PdfRectangle(20, y - 2, 120, y + 10), "https://example.com/project", s.LinkInTree ? link : null);
            w.EndStructureElement();

            if (s.UntaggedText)
            {
                p.DrawText("loose text", 20, 20);
            }

            p.Commit();
            w.EndStructureElement(); // Document
            w.Finish();
        }

        return ms.ToArray();
    }

    private static PdfDocument Editable(byte[] pdf) => PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });

    private static byte[] Save(byte[] built, PdfSaveOptions options)
    {
        using PdfDocument doc = Editable(built);
        using var ms = new MemoryStream();
        doc.Save(ms, options);
        return ms.ToArray();
    }

    // ---- flattening a tagged form ----

    // The generated tagged document plus one text field: the widget has an appearance, a /StructParent, and a Form element holding its OBJR.
    private static PdfDocument WithTaggedField(byte[] built)
    {
        PdfDocument doc = Editable(built);
        PdfDictionary root = doc.StructureTreeRoot!;
        PdfDictionary page = doc.Pages[0].Dictionary;
        PdfReference pageRef = page.Reference!;

        PdfStream ap = doc.CreateStream("0.9 g 0 0 100 20 re f"u8, false);
        ap.Dictionary.SetEntry("Type", new PdfName("XObject"));
        ap.Dictionary.SetEntry("Subtype", new PdfName("Form"));
        var bbox = new PdfArray();
        foreach (int v in new[] { 0, 0, 100, 20 })
        {
            bbox.Append(new PdfInteger(v));
        }

        ap.Dictionary.SetEntry("BBox", bbox);
        PdfReference apRef = doc.AddObject(ap);

        var rect = new PdfArray();
        foreach (int v in new[] { 150, 20, 250, 40 })
        {
            rect.Append(new PdfInteger(v));
        }

        var widget = new PdfDictionary();
        widget.SetEntry("Type", new PdfName("Annot"));
        widget.SetEntry("Subtype", new PdfName("Widget"));
        widget.SetEntry("FT", new PdfName("Tx"));
        widget.SetEntry("T", new PdfString("name"u8.ToArray()));
        widget.SetEntry("TU", new PdfString("Your name"u8.ToArray()));
        widget.SetEntry("Rect", rect);
        widget.SetEntry("F", new PdfInteger(4));
        widget.SetEntry("P", pageRef);
        var n = new PdfDictionary();
        n.SetEntry("N", apRef);
        widget.SetEntry("AP", n);
        long key = 900;
        widget.SetEntry("StructParent", new PdfInteger(key));
        PdfReference widgetRef = doc.AddObject(widget);

        var annots = new PdfArray();
        if (page.GetArray("Annots") is { } old)
        {
            for (int i = 0; i < old.Count; i++)
            {
                annots.Append(old.GetRaw(i));
            }
        }

        annots.Append(widgetRef);
        page.SetEntry("Annots", annots);

        var fields = new PdfArray();
        fields.Append(widgetRef);
        var acro = new PdfDictionary();
        acro.SetEntry("Fields", fields);
        doc.Catalog.SetEntry("AcroForm", doc.AddObject(acro));

        // The Form element goes under the Document element, with the OBJR as its only content.
        PdfDictionary documentElement = root.Get("K") switch { PdfArray a => (PdfDictionary)a[0]!, PdfDictionary d => d, _ => throw new InvalidOperationException() };
        var objr = new PdfDictionary();
        objr.SetEntry("Type", new PdfName("OBJR"));
        objr.SetEntry("Obj", widgetRef);
        objr.SetEntry("Pg", pageRef);
        var formElement = new PdfDictionary();
        formElement.SetEntry("Type", new PdfName("StructElem"));
        formElement.SetEntry("S", new PdfName("Form"));
        formElement.SetEntry("P", documentElement.Reference!);
        formElement.SetEntry("Pg", pageRef);
        formElement.SetEntry("K", objr);
        PdfReference formRef = doc.AddObject(formElement);
        var kids = new PdfArray();
        if (documentElement.Get("K") is PdfArray existing)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                kids.Append(existing.GetRaw(i));
            }
        }
        else if (documentElement.GetRaw("K") is { } one)
        {
            kids.Append(one);
        }

        kids.Append(formRef);
        documentElement.SetEntry("K", kids);

        var nums = new PdfArray();
        var entries = new List<(long, PdfObject)>();
        foreach (KeyValuePair<PdfObject, PdfObject> kv in doc.ReadTree(root.GetDictionary("ParentTree"), true, "parent tree"))
        {
            entries.Add((((PdfInteger)kv.Key).Value, kv.Value));
        }

        entries.Add((key, formRef));
        entries.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        foreach ((long k, PdfObject v) in entries)
        {
            nums.Append(new PdfInteger(k));
            nums.Append(v);
        }

        var leaf = new PdfDictionary();
        leaf.SetEntry("Nums", nums);
        root.SetEntry("ParentTree", leaf);
        return doc;
    }

    // Every OBJR in the tree names an annotation that is still in its page's /Annots; every parent tree value is an element of the tree.
    private static (int Objrs, int Keys) CheckTree(PdfDocument doc)
    {
        PdfDictionary root = doc.StructureTreeRoot!;
        var annots = new HashSet<int>();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            foreach (PdfAnnotation a in doc.Pages[i].Annotations)
            {
                annots.Add(a.Dictionary.Reference!.Number);
            }
        }

        int objrs = 0;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<PdfDictionary>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            PdfDictionary el = stack.Pop();
            if (!seen.Add(el))
            {
                continue;
            }

            var kids = new List<PdfObject?>();
            if (el.Get("K") is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    kids.Add(arr[i]);
                }
            }
            else
            {
                kids.Add(el.Get("K"));
            }

            foreach (PdfObject? k in kids)
            {
                if (k is PdfDictionary kd && kd.GetName("Type") == "OBJR")
                {
                    objrs++;
                    Assert.True(kd.GetRaw("Obj") is PdfReference r && annots.Contains(r.Number), "an OBJR points at an annotation that is gone");
                }
                else if (k is PdfDictionary ke && ke.GetName("Type") != "MCR")
                {
                    stack.Push(ke);
                }
            }
        }

        int keys = 0;
        foreach (KeyValuePair<PdfObject, PdfObject> kv in doc.ReadTree(root.GetDictionary("ParentTree"), true, "parent tree"))
        {
            keys++;
            PdfObject? v = doc.Resolve(kv.Value);
            Assert.True(v is PdfDictionary or PdfArray, "a parent tree value is neither an element nor an array");
        }

        return (objrs, keys);
    }

    [Fact]
    public void FlatteningATaggedFormRemovesTheObjrAndTheParentTreeEntry()
    {
        byte[] built = Build(new Spec());
        using PdfDocument doc = WithTaggedField(built);
        (int before, int keysBefore) = CheckTree(doc);
        Assert.True(before >= 2); // the link and the field
        PdfFlattenResult r = doc.Flatten();
        Assert.Equal(1, r.Removed);
        (int after, int keysAfter) = CheckTree(doc);
        Assert.Equal(before - 1, after);
        Assert.Equal(keysBefore - 1, keysAfter);
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16] });
        using PdfDocument back = PdfDocument.Open(ms.ToArray());
        Assert.Equal((after, keysAfter), CheckTree(back));

        // The baked-in art is an artifact, so the page content stays tagged or artifact.
        string content = string.Concat(back.Pages[0].Dictionary.Get("Contents") switch
        {
            PdfArray a => Enumerable.Range(0, a.Count).Select(i => System.Text.Encoding.Latin1.GetString(((PdfStream)a[i]!).GetDecodedBytes().Span)),
            PdfStream s => [System.Text.Encoding.Latin1.GetString(s.GetDecodedBytes().Span)],
            _ => [],
        });
        Assert.Contains("/Artifact BMC q", content, StringComparison.Ordinal);
    }

    [Fact]
    public void VeraPdfAcceptsPdfUA1BeforeAndAfterFlatteningATaggedForm()
    {
        VeraPdf.Require();
        var options = new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions { Language = "en-US" } };
        byte[] built = Build(new Spec { Language = null });
        using (PdfDocument doc = WithTaggedField(built))
        {
            using var ms = new MemoryStream();
            doc.Save(ms, options);
            List<string> failures = VeraPdf.Validate(ms.ToArray(), "ua1");
            Assert.True(failures.Count == 0, "before flattening: " + string.Join("\n", failures));
        }

        using (PdfDocument doc = WithTaggedField(built))
        {
            doc.Flatten();
            using var ms = new MemoryStream();
            doc.Save(ms, options);
            List<string> failures = VeraPdf.Validate(ms.ToArray(), "ua1");
            Assert.True(failures.Count == 0, "after flattening: " + string.Join("\n", failures));
        }
    }

    // ---- veraPDF ----

    [Fact]
    public void VeraPdfAcceptsPdfUA1()
    {
        VeraPdf.Require();
        byte[] pdf = Save(Build(new Spec { Language = null }), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions { Language = "en-US" } });
        List<string> failures = VeraPdf.Validate(pdf, "ua1");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(PdfAPart.Part2, "2a")]
    [InlineData(PdfAPart.Part3, "3a")]
    public void VeraPdfAcceptsPdfALevelA(PdfAPart part, string flavour)
    {
        VeraPdf.Require();
        byte[] pdf = Save(Build(new Spec()), new PdfSaveOptions { DocumentId = new byte[16], PdfA = new PdfAOptions { Part = part, Conformance = PdfAConformance.A } });
        List<string> failures = VeraPdf.Validate(pdf, flavour);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(PdfAPart.Part2, "2a")]
    [InlineData(PdfAPart.Part3, "3a")]
    public void VeraPdfAcceptsPdfAAndUATogether(PdfAPart part, string flavour)
    {
        VeraPdf.Require();
        byte[] pdf = Save(Build(new Spec()), new PdfSaveOptions
        {
            DocumentId = new byte[16],
            PdfA = new PdfAOptions { Part = part, Conformance = PdfAConformance.A },
            PdfUA = new PdfUAOptions(),
        });
        Assert.True(VeraPdf.Validate(pdf, flavour).Count == 0, flavour);
        List<string> ua = VeraPdf.Validate(pdf, "ua1");
        Assert.True(ua.Count == 0, string.Join("\n", ua));
    }

    [Fact]
    public void SavedFileChecksCleanAndKeepsItsClaims()
    {
        byte[] pdf = Save(Build(new Spec()), new PdfSaveOptions { DocumentId = new byte[16], PdfUA = new PdfUAOptions() });
        using PdfDocument back = PdfDocument.Open(pdf);
        Assert.Empty(back.CheckPdfUA());
        Assert.Equal(1, back.Xmp!.PdfUAPart);
        Assert.Equal("Accessible test document", back.Xmp.Title);
        Assert.True(back.Catalog.GetDictionary("ViewerPreferences")!.GetBool("DisplayDocTitle"));
        PdfDictionary link = back.Pages[0].Links[0].Dictionary;
        Assert.Equal("https://example.com/project", link.GetString("Contents")!.GetText()); // no text was given: the address describes it
        Assert.Equal("S", back.Pages[0].Dictionary.GetName("Tabs"));
    }

    // ---- refusals ----

    public static IEnumerable<object[]> Refusals()
    {
        yield return ["no title", new Spec { Title = false }, "dc:title"];
        yield return ["no language", new Spec { Language = null }, "/Lang"];
        yield return ["figure without alt text", new Spec { FigureAlt = false }, "Figure"];
        yield return ["skipped heading level", new Spec { Headings = [1, 3] }, "skipped"];
        yield return ["first heading not H1", new Spec { Headings = [2, 3] }, "first heading"];
        yield return ["H mixed with Hn", new Spec { Headings = [1, 0] }, "mixes"];
        yield return ["table without header cells", new Spec { TableHeader = false }, "no header cells"];
        yield return ["irregular table", new Spec { IrregularTable = true }, "same number of columns"];
        yield return ["unmapped structure type", new Spec { UnmappedType = true }, "not a standard type"];
        yield return ["untagged content", new Spec { UntaggedText = true }, "neither tagged"];
        yield return ["unembedded font", new Spec { EmbeddedFont = false }, "not embedded"];
        yield return ["link outside a Link element", new Spec { LinkInTree = false }, "not part of a Link"];
        yield return ["list with a wrong child", new Spec { ListWrongChildren = true }, "not a child of"];
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void EachViolationIsRefusedWithItsReason(string name, object spec, string expected)
    {
        byte[] built = Build((Spec)spec);
        using PdfDocument doc = Editable(built);
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => doc.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
        Assert.True(ex.Violations.Any(v => v.Message.Contains(expected, StringComparison.Ordinal)), name + ": " + ex.Message);
        Assert.Equal(0, ms.Length); // nothing is written
    }

    [Fact]
    public void UnnumberedHeadingsAloneAreAllowed()
    {
        using PdfDocument doc = Editable(Build(new Spec { Headings = [0, 0] }));
        Assert.DoesNotContain(doc.CheckPdfUA(), v => v.Checkpoint == "14");
    }

    [Fact]
    public void CheckListsEveryViolationOfABadDocumentAndChangesNothing()
    {
        var bad = new Spec { Title = false, Language = null, FigureAlt = false, Headings = [2, 4], TableHeader = false, UnmappedType = true, UntaggedText = true, EmbeddedFont = false, LinkInTree = false, ListWrongChildren = true };
        byte[] built = Build(bad);
        using PdfDocument doc = PdfDocument.Open(built);
        IReadOnlyList<PdfUAViolation> v = doc.CheckPdfUA();
        string all = string.Join("\n", v);
        foreach (string expected in new[] { "/Metadata", "/Lang", "Figure", "skipped", "first heading", "no header cells", "not a standard type", "neither tagged", "not embedded", "not part of a Link", "not a child of", "DisplayDocTitle", "PDF/UA identification" })
        {
            Assert.Contains(expected, all);
        }

        Assert.All(v, x => Assert.False(string.IsNullOrEmpty(x.Checkpoint)));
        Assert.Equal(all, string.Join("\n", doc.CheckPdfUA())); // reports only: the same again
    }

    [Fact]
    public void LevelARefusesWhatItCanCheck()
    {
        using PdfDocument doc = Editable(Build(new Spec { Language = null, FigureAlt = false, UnmappedType = true, UntaggedText = true }));
        using var ms = new MemoryStream();
        PdfAException ex = Assert.Throws<PdfAException>(() => doc.Save(ms, new PdfSaveOptions { PdfA = new PdfAOptions { Conformance = PdfAConformance.A } }));
        string all = string.Join("\n", ex.Violations);
        Assert.Contains("6.7.4", all);
        Assert.Contains("6.7.3.4", all);
        Assert.Contains("neither tagged", all);
        Assert.Contains("Figure", all);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void UntaggedDocumentsAreRefusedNotTagged()
    {
        using PdfDocument d = PdfADocs.New();
        PdfPage page = d.InsertBlankPage(0, 300, 300);
        using (PdfPageContentBuilder b = page.BeginContent())
        {
            b.SetFont(d.AddFont(CreationSupport.RequireFont(PdfADocs.Serif)), 12);
            b.DrawText("plain", 20, 200);
            b.Commit();
        }

        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => d.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions { Language = "en" } }));
        Assert.Contains(ex.Violations, v => v.Message.Contains("StructTreeRoot", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionsConflictsAreArgumentErrors()
    {
        using PdfDocument doc = Editable(Build(new Spec()));
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => doc.SaveIncremental(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));
        Assert.Throws<ArgumentException>(() => doc.Save(ms, new PdfSaveOptions
        {
            PdfA = new PdfAOptions(),
            PdfUA = new PdfUAOptions(),
            Encryption = PdfEncryptionAction.Set,
            NewEncryption = new PdfEncryptionOptions(),
        }));
    }

    [Fact]
    public void EncryptionThatBlocksAssistiveTechnologyIsRefused()
    {
        using PdfDocument doc = Editable(Build(new Spec()));
        using var ms = new MemoryStream();
        PdfUAException ex = Assert.Throws<PdfUAException>(() => doc.Save(ms, new PdfSaveOptions
        {
            PdfUA = new PdfUAOptions(),
            Encryption = PdfEncryptionAction.Set,
            NewEncryption = new PdfEncryptionOptions { Permissions = PdfPermissions.Print },
        }));
        Assert.Contains(ex.Violations, v => v.Checkpoint == "26");

        using var ok = new MemoryStream();
        doc.Save(ok, new PdfSaveOptions { PdfUA = new PdfUAOptions(), Encryption = PdfEncryptionAction.Set, NewEncryption = new PdfEncryptionOptions { Permissions = PdfPermissions.All } });
        Assert.True(ok.Length > 0);
    }
}
