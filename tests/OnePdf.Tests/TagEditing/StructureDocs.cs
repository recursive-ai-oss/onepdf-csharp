using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.Tests.Creation;
using OnePdf.Tests.PdfA;
using OnePdf.Tests.Tagging;

namespace OnePdf.Tests.TagEditing;

/// <summary>Documents with the structure defects that remediation fixes, built by the document writer, and helpers to judge an edit.</summary>
internal static class StructureDocs
{
    public static string RealWorld([CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "realworld", "pdf");
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        throw new DirectoryNotFoundException("testdata/realworld/pdf");
    }

    /// <summary>
    /// A one-page document with a heading, a paragraph, a custom <c>Strong</c> and <c>Em</c> inside it, a figure without alternate text,
    /// a table whose header cells have no scope, a list with a Span between the list and its items, and an empty paragraph.
    /// </summary>
    public static byte[] Defective()
    {
        byte[] font = CreationSupport.RequireFont("LiberationSans-Regular.ttf");
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], Language = "en-US", DisplayDocumentTitle = true, LeaveOpen = true }))
        {
            w.SetInfo("Title", "Defective");
            PdfCreatedFont f = w.AddFont(font, new PdfEmbedFontOptions());
            PdfStructureElement doc = w.BeginStructureElement("Document");
            PdfStreamPageBuilder p = w.BeginPage(300, 500);
            p.SetFont(f, 12);
            double y = 470;

            void Leaf(PdfStructureElement parent, string type, string text, PdfStructureElementOptions? options = null)
            {
                PdfStructureElement e = w.BeginStructureElement(parent, type, options);
                p.BeginTaggedContent(e);
                p.DrawText(text, 20, y);
                p.EndMarkedContent();
                y -= 20;
                w.EndStructureElement(e);
            }

            Leaf(doc, "H1", "Annual report");
            PdfStructureElement para = w.BeginStructureElement(doc, "P");
            p.BeginTaggedContent(para);
            p.DrawText("Body text with", 20, y);
            p.EndMarkedContent();
            y -= 20;
            Leaf(para, "Strong", "strong words");
            Leaf(para, "Em", "emphasis");
            w.EndStructureElement(para);

            PdfStructureElement fig = w.BeginStructureElement(doc, "Figure");
            p.BeginTaggedContent(fig);
            p.SetFillRgb(0.2, 0.4, 0.8);
            p.Rectangle(20, y - 30, 60, 30);
            p.Fill();
            p.SetFillGray(0);
            p.EndMarkedContent();
            w.EndStructureElement(fig);
            y -= 50;

            PdfStructureElement table = w.BeginStructureElement(doc, "Table");
            PdfStructureElement r1 = w.BeginStructureElement(table, "TR");
            double x = 20;
            foreach (string head in new[] { "Name", "Value" })
            {
                PdfStructureElement th = w.BeginStructureElement(r1, "TH");
                p.BeginTaggedContent(th);
                p.DrawText(head, x, y);
                p.EndMarkedContent();
                w.EndStructureElement(th);
                x += 100;
            }

            w.EndStructureElement(r1);
            y -= 20;
            PdfStructureElement r2 = w.BeginStructureElement(table, "TR");
            x = 20;
            foreach (string cell in new[] { "alpha", "1" })
            {
                PdfStructureElement td = w.BeginStructureElement(r2, "TD");
                p.BeginTaggedContent(td);
                p.DrawText(cell, x, y);
                p.EndMarkedContent();
                w.EndStructureElement(td);
                x += 100;
            }

            w.EndStructureElement(r2);
            w.EndStructureElement(table);
            y -= 30;

            PdfStructureElement list = w.BeginStructureElement(doc, "L");
            PdfStructureElement span = w.BeginStructureElement(list, "Span");
            PdfStructureElement li = w.BeginStructureElement(span, "LI");
            PdfStructureElement lbl = w.BeginStructureElement(li, "Lbl");
            p.BeginTaggedContent(lbl);
            p.DrawText("1.", 20, y);
            p.EndMarkedContent();
            w.EndStructureElement(lbl);
            PdfStructureElement body = w.BeginStructureElement(li, "LBody");
            p.BeginTaggedContent(body);
            p.DrawText("first item", 50, y);
            p.EndMarkedContent();
            w.EndStructureElement(body);
            w.EndStructureElement(li);
            w.EndStructureElement(span);
            w.EndStructureElement(list);
            y -= 30;

            w.EndStructureElement(w.BeginStructureElement(doc, "P")); // empty
            p.Commit();
            w.EndStructureElement(doc);
            w.Finish();
        }

        return ms.ToArray();
    }

    public static PdfStructureNode Find(PdfStructureTree tree, string type, int nth = 0) =>
        tree.Elements().Where(n => n.Type == type).Skip(nth).First();

    /// <summary>The findings of the checker, without object numbers, so two files can be compared. The missing XMP of a writer document is left out: only a PDF/UA save adds it.</summary>
    public static List<string> Findings(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return [.. d.CheckPdfUA().Where(static v => !v.Message.Contains("/Metadata", StringComparison.Ordinal)).Select(static v => v.Clause + " " + System.Text.RegularExpressions.Regex.Replace(v.Message, "[0-9]+ 0 R", "N 0 R"))];
    }

    public static List<string> Vera(byte[] pdf) =>
        VeraPdf.Path is null ? [] : [.. VeraPdf.Validate(pdf, "ua1").Select(static f => f.Split(':')[0]).Where(static r => r != "7.1-8").Distinct()];

    /// <summary>
    /// Reads a saved document's tree and checks, with its own simple walk of the parent tree, that every marked-content reference is found
    /// from its page (or form) and identifier, and leads back to the element that holds it.
    /// </summary>
    public static void AssertConsistent(byte[] saved)
    {
        using PdfDocument d = PdfDocument.Open(saved);
        PdfStructureTree? tree = d.StructureTree;
        Assert.NotNull(tree);
        Assert.True(tree!.Diagnostics.Count == 0, string.Join("; ", tree.Diagnostics.Select(static x => x.Message)));
        Assert.Empty(tree.FindMissingContent());
        var parentTree = new Dictionary<long, PdfObject>();
        var stack = new Stack<PdfDictionary>();
        if (d.StructureTreeRoot!.GetDictionary("ParentTree") is { } ptRoot)
        {
            stack.Push(ptRoot);
        }

        while (stack.Count > 0)
        {
            PdfDictionary t = stack.Pop();
            if (t.GetArray("Nums") is { } nums)
            {
                for (int i = 0; i + 1 < nums.Count; i += 2)
                {
                    parentTree[nums.GetInt(i)] = nums.GetRaw(i + 1);
                }
            }

            if (t.GetArray("Kids") is { } kids)
            {
                for (int i = 0; i < kids.Count; i++)
                {
                    stack.Push((PdfDictionary)kids[i]!);
                }
            }
        }

        foreach (PdfStructureNode n in tree.Elements())
        {
            Assert.True(n.Dictionary.GetRaw("P") is PdfReference pr && pr.Number == (n.Parent?.Dictionary.Reference ?? d.StructureTreeRoot!.Reference)!.Number, "/P of " + n.Type);
            foreach (PdfStructureItem k in n.Children)
            {
                if (k is PdfMarkedContentReference m)
                {
                    PdfDictionary holder = m.Stream?.Dictionary ?? d.Pages[m.PageIndex].Dictionary;
                    long key = holder.GetInt("StructParents", -1);
                    Assert.True(parentTree.TryGetValue(key, out PdfObject? entry), $"no parent tree entry for key {key}");
                    PdfArray arr = (PdfArray)(entry is PdfReference r ? d.GetObject(r.Number)! : entry!);
                    Assert.True(arr.GetRaw(m.Mcid) is PdfReference owner && owner.Number == n.Dictionary.Reference!.Number, $"parent tree of MCID {m.Mcid} does not lead to its {n.Type}");
                }
                else if (k is PdfObjectReference o && o.Target is PdfDictionary od)
                {
                    long key = od.GetInt("StructParent", -1);
                    Assert.True(parentTree.TryGetValue(key, out PdfObject? entry), $"no parent tree entry for annotation key {key}");
                    Assert.True(entry is PdfReference orf && orf.Number == n.Dictionary.Reference!.Number, "parent tree of an annotation does not lead to its element");
                }
            }
        }
    }

    public static string Roles(PdfStructureTree tree)
    {
        var sb = new StringBuilder();
        var stack = new Stack<(PdfStructureItem Item, int Depth)>();
        for (int i = tree.Children.Count - 1; i >= 0; i--)
        {
            stack.Push((tree.Children[i], 0));
        }

        while (stack.Count > 0)
        {
            (PdfStructureItem item, int depth) = stack.Pop();
            if (item is PdfStructureNode n)
            {
                sb.Append(' ', depth * 2).Append(n.Type).Append('\n');
                for (int i = n.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push((n.Children[i], depth + 1));
                }
            }
        }

        return sb.ToString();
    }
}
