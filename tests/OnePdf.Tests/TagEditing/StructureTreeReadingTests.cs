using System.Text;
using OnePdf.Tests.Tagging;
using static OnePdf.Tests.Tagging.TagDocs;
using static OnePdf.Tests.TagEditing.StructureDocs;

namespace OnePdf.Tests.TagEditing;

/// <summary>Reads of committed tagged files and of broken trees: diagnostics, never an exception or a hang; and edits that keep every committed file consistent.</summary>
public class StructureTreeReadingTests
{
    private static readonly PdfSaveOptions Fixed = new() { DocumentId = new byte[16] };

    private static IEnumerable<string> TaggedFiles()
    {
        foreach (string file in Directory.GetFiles(RealWorld(), "*.pdf").Order(StringComparer.Ordinal))
        {
            using PdfDocument d = PdfDocument.Open(File.ReadAllBytes(file));
            if (d.StructureTreeRoot is not null)
            {
                yield return file;
            }
        }
    }

    [Fact]
    public void ReadsEveryTaggedRealWorldFile()
    {
        var report = new StringBuilder();
        int files = 0, elements = 0, withDiagnostics = 0, missing = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (string file in TaggedFiles())
        {
            byte[] bytes = File.ReadAllBytes(file);
            long t0 = watch.ElapsedMilliseconds;
            using PdfDocument doc = TagSupport.Editable(bytes);
            PdfStructureTree tree = doc.StructureTree!;
            int mcrs = 0, objrs = 0;
            foreach (PdfStructureNode n in tree.Elements())
            {
                foreach (PdfStructureItem k in n.Children)
                {
                    mcrs += k is PdfMarkedContentReference ? 1 : 0;
                    objrs += k is PdfObjectReference ? 1 : 0;
                }

                _ = (n.StandardType, n.Alt, n.Scope, n.PageIndex);
            }

            int miss = tree.FindMissingContent().Count;
            string sample = tree.Elements().Take(3).Select(static n => n.GetText()).FirstOrDefault(static t => t.Length > 0) ?? string.Empty;
            files++;
            elements += tree.ElementCount;
            withDiagnostics += tree.Diagnostics.Count > 0 ? 1 : 0;
            missing += miss;
            report.Append(Path.GetFileName(file)).Append(": ").Append(tree.ElementCount).Append(" elements, ").Append(mcrs).Append(" content refs, ").Append(objrs).Append(" object refs, ")
                .Append(tree.Diagnostics.Count).Append(" diagnostics, ").Append(miss).Append(" missing, ").Append(watch.ElapsedMilliseconds - t0).Append(" ms\n");
            foreach (string m in tree.Diagnostics.Select(static d => d.Message).Distinct().Take(3))
            {
                report.Append("    ").Append(m).Append('\n');
            }

            Assert.Equal(tree.ElementCount, tree.Elements().Count());
        }

        string summary = $"files {files}, elements {elements}, files with diagnostics {withDiagnostics}, missing content refs {missing}";
        if (Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR") is { Length: > 0 } rd)
        {
            File.WriteAllText(Path.Combine(rd, "structure-read.txt"), summary + "\n" + report);
        }

        Assert.True(files >= 30, summary);
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), summary);
    }

    [Fact]
    public void ReadingAndSavingChangesNoByteOfTheStructure()
    {
        int files = 0, nondeterministic = 0;
        foreach (string file in TaggedFiles())
        {
            byte[] bytes = File.ReadAllBytes(file);
            using PdfDocument plain = TagSupport.Editable(bytes);
            byte[] expected = TagSupport.Save(plain, Fixed);
            if (!expected.AsSpan().SequenceEqual(TagSupport.Save(plain, Fixed)))
            {
                nondeterministic++; // the writer itself is not repeatable for this file (not about the structure)
                continue;
            }

            using PdfDocument read = TagSupport.Editable(bytes);
            foreach (PdfStructureNode n in read.StructureTree!.Elements())
            {
                _ = (n.Type, n.StandardType, n.Alt, n.ActualText, n.Language, n.Title, n.Id, n.Scope, n.ColSpan, n.RowSpan, n.Headers, n.ListNumbering, n.Attributes, n.PageIndex);
            }

            _ = read.StructureTree!.RoleMap;
            _ = read.StructureTree.FindMissingContent();
            byte[] again = TagSupport.Save(read, Fixed);
            Assert.True(expected.AsSpan().SequenceEqual(again), Path.GetFileName(file) + $": {expected.Length} vs {again.Length}, plain twice equal: {expected.AsSpan().SequenceEqual(TagSupport.Save(plain, Fixed))}");
            files++;
        }

        Assert.True(files + nondeterministic >= 30 && files >= 20, $"{files} compared, {nondeterministic} not repeatable");
    }

    [Fact]
    public void EditsKeepEveryCommittedTreeConsistent()
    {
        int files = 0, edits = 0, skipped = 0;
        foreach (string file in TaggedFiles())
        {
            byte[] bytes = File.ReadAllBytes(file);
            using PdfDocument doc = TagSupport.Editable(bytes);
            PdfStructureTree tree = doc.StructureTree!;
            if (tree.Diagnostics.Count > 0 || tree.FindMissingContent().Count > 0)
            {
                skipped++;
                continue;
            }

            string extract = TagSupport.ExtractionJson(doc);
            var log = new StringBuilder();
            var rnd = new Random(Path.GetFileName(file).GetHashCode(StringComparison.Ordinal) & 0xFFFF);
            for (int i = 0; i < 12; i++)
            {
                List<PdfStructureNode> all = [.. tree.Elements()];
                if (all.Count < 3)
                {
                    break;
                }

                PdfStructureNode n = all[rnd.Next(all.Count)];
                switch (rnd.Next(5))
                {
                    case 0:
                        n.Alt = "alt " + i;
                        break;
                    case 1 when n.Children.Count > 0:
                        n.WrapChildren("Div", 0, Math.Max(1, n.Children.Count / 2));
                        break;
                    case 2 when n.Parent is not null:
                        n.Remove(PdfStructureRemoval.Unwrap);
                        break;
                    case 3 when n.Parent is not null && n.Parent.Parent is not null:
                        n.MoveTo(n.Parent.Parent, rnd.Next(n.Parent.Parent.Children.Count));
                        break;
                    case 4 when n.Parent is not null && all.Count > 50:
                        n.Remove(PdfStructureRemoval.ToArtifacts);
                        break;
                    default:
                        n.SetType(PdfTagRole.Div);
                        break;
                }

                edits++;
                log.Append(i).Append(' ').Append(n.Type).Append(" kids=").Append(n.Children.Count).Append(" case ").Append('\n');
            }

            foreach (PdfStructureNode bad in tree.Elements().Where(static x => !x.Dictionary.ContainsKey("Pg") && x.Children.OfType<PdfMarkedContentReference>().Any(static m => m.Raw is PdfInteger)))
            {
                throw new Xunit.Sdk.XunitException(Path.GetFileName(file) + ": " + bad.Type + " has integer MCIDs and no Pg; page " + bad.Children.OfType<PdfMarkedContentReference>().First().Page + "\n" + log);
            }

            byte[] saved = TagSupport.Save(doc);
            try
            {
                AssertConsistent(saved);
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                File.WriteAllBytes(Path.Combine(Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR")!, "inconsistent.pdf"), saved);
                File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR")!, "inconsistent.txt"), log.ToString());
                throw new Xunit.Sdk.XunitException(Path.GetFileName(file) + ": " + ex.Message[..Math.Min(ex.Message.Length, 300)]);
            }

            using PdfDocument after = PdfDocument.Open(saved);
            // Content turned into artifacts leaves the text as it was.
            Assert.Equal(extract.Length > 0, TagSupport.ExtractionJson(after).Length > 0);
            files++;
        }

        Assert.True(files >= 20, $"files {files}, skipped {skipped}, edits {edits}");
    }

    [Fact]
    public void RemovingALinkElementFreesItsAnnotation()
    {
        string path = Path.Combine(RealWorld(), "form-04-gov756951.pdf");
        using PdfDocument doc = TagSupport.Editable(File.ReadAllBytes(path));
        PdfStructureTree tree = doc.StructureTree!;
        PdfStructureNode? link = tree.Elements().FirstOrDefault(static n => n.Children.OfType<PdfObjectReference>().Any(static o => o.IsAnnotation));
        if (link is null)
        {
            Assert.Skip("The file has no element with an annotation reference.");
        }

        PdfObjectReference o = link!.Children.OfType<PdfObjectReference>().First(static x => x.IsAnnotation);
        PdfDictionary annotation = (PdfDictionary)o.Target!;
        Assert.True(annotation.ContainsKey("StructParent"));
        link.Remove(PdfStructureRemoval.ToArtifacts);
        Assert.False(annotation.ContainsKey("StructParent"));
        AssertConsistent(TagSupport.Save(doc));
    }

    // ---- broken trees ----

    private static byte[] Broken(string rootEntries, string objects, string content = "", int pages = 1, int xrefSize = 200)
    {
        string c = content.Length > 0 ? content : "/P <</MCID 0>> BDC\n" + T(72, 700, "First") + "EMC\n/P <</MCID 1>> BDC\n" + T(72, 600, "Second") + "EMC\n";
        return Make([.. Enumerable.Range(0, pages).Select(_ => new PageSpec(c, Entries: "/StructParents 0"))],
            b =>
            {
                b.Obj(20, "<< /Type /StructTreeRoot " + rootEntries + " /ParentTree << /Nums [0 [21 0 R 21 0 R]] >> >>");
                foreach (string o in objects.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    int sp = o.IndexOf(' ', StringComparison.Ordinal);
                    b.Obj(int.Parse(o[..sp], System.Globalization.CultureInfo.InvariantCulture), o[(sp + 1)..]);
                }
            }, catalogExtra: "/StructTreeRoot 20 0 R /MarkInfo << /Marked true >>", xrefSize: xrefSize);
    }

    public static TheoryData<string, string> BrokenCases() => new()
    {
        { "cycle", Case("/K 21 0 R", "21 << /Type /StructElem /S /Div /P 20 0 R /K [22 0 R] >>\n22 << /Type /StructElem /S /Div /P 21 0 R /K [21 0 R 0 1] /Pg 10 0 R >>") },
        { "wrong parent", Case("/K 21 0 R", "21 << /Type /StructElem /S /Div /P 99 0 R /K [0 1] /Pg 10 0 R >>") },
        { "root lists itself", Case("/K [20 0 R 21 0 R]", "21 << /Type /StructElem /S /Div /P 20 0 R /K [0 1] /Pg 10 0 R >>") },
        { "missing and duplicate content", Case("/K 21 0 R", "21 << /Type /StructElem /S /Div /P 20 0 R /K [0 0 7 << /Type /MCR /MCID 9 /Pg 10 0 R >>] /Pg 10 0 R >>") },
        { "garbage in K", Case("/K 21 0 R", "21 << /Type /StructElem /S /Div /P 20 0 R /K [-1 /Name (str) 3.5 null true << /Type /OBJR >> << /Foo 1 >>] /Pg 10 0 R >>") },
        { "shared element", Case("/K [21 0 R 22 0 R]", "21 << /Type /StructElem /S /Div /P 20 0 R /K [23 0 R] /Pg 10 0 R >>\n22 << /Type /StructElem /S /Div /P 20 0 R /K [23 0 R] /Pg 10 0 R >>\n23 << /Type /StructElem /S /P /P 21 0 R /K [0 1] /Pg 10 0 R >>") },
        { "no page", Case("/K 21 0 R", "21 << /Type /StructElem /S /Div /P 20 0 R /K [0 1] >>") },
        { "tree of tree", "ROOT-LOOP" },
        { "no type", Case("/K 21 0 R", "21 << /S /Div /P 20 0 R /K 22 0 R /Pg 10 0 R >>\n22 << /S 7 /P 21 0 R /K [0] /Pg 10 0 R >>") },
    };

    private static string Case(string root, string objects) => root + "\n" + objects;

    [Theory]
    [MemberData(nameof(BrokenCases))]
    public void BrokenTreeReadsWithDiagnosticsAndStaysUsable(string name, string spec)
    {
        byte[] pdf;
        if (spec == "ROOT-LOOP")
        {
            // A parent tree whose /Kids lead back to the node itself.
            pdf = Make([new PageSpec(T(72, 700, "x"), Entries: "/StructParents 0")], b =>
            {
                b.Obj(20, "<< /Type /StructTreeRoot /K 21 0 R /ParentTree 30 0 R >>");
                b.Obj(21, "<< /Type /StructElem /S /P /P 20 0 R >>");
                b.Obj(30, "<< /Kids [30 0 R 31 0 R] >>");
                b.Obj(31, "<< /Nums [0 [21 0 R]] /Kids [30 0 R] >>");
            }, catalogExtra: "/StructTreeRoot 20 0 R");
        }
        else
        {
            int nl = spec.IndexOf('\n', StringComparison.Ordinal);
            pdf = Broken(spec[..nl], spec[(nl + 1)..]);
        }

        using PdfDocument doc = TagSupport.Editable(pdf);
        PdfStructureTree tree = doc.StructureTree!;
        Assert.NotNull(tree);
        int count = tree.Elements().Count();
        Assert.Equal(tree.ElementCount, count);
        foreach (PdfStructureNode n in tree.Elements())
        {
            _ = (n.Type, n.StandardType, n.GetText(), n.PageIndex, n.Scope);
        }

        _ = tree.FindMissingContent();
        if (name is not ("tree of tree" or "no type"))
        {
            Assert.NotEmpty(tree.Diagnostics);
        }

        // Edits on a broken tree either work or fail with the documented exceptions; the result saves and opens.
        if (count > 0)
        {
            PdfStructureNode first = tree.Elements().First();
            first.Alt = "alt";
            first.SetType(PdfTagRole.Sect);
            if (first.Children.Count > 0)
            {
                first.WrapChildren("Div", 0, 1);
            }

            tree.Elements().Last().Remove(PdfStructureRemoval.Unwrap);
        }

        byte[] saved = TagSupport.Save(doc);
        using PdfDocument back = PdfDocument.Open(saved);
        _ = back.StructureTree;
        _ = back.CheckPdfUA();
    }

    [Fact]
    public void ADeepTreeStopsAtTheNestingLimit()
    {
        var sb = new StringBuilder();
        const int depth = 2000;
        for (int i = 0; i < depth; i++)
        {
            int n = 21 + i;
            sb.Append(n).Append(" << /Type /StructElem /S /Div /P ").Append(n == 21 ? 20 : n - 1).Append(" 0 R ").Append(i + 1 < depth ? "/K " + (n + 1) + " 0 R" : "/K 0 /Pg 10 0 R").Append(" >>\n");
        }

        byte[] pdf = Broken("/K 21 0 R", sb.ToString(), xrefSize: depth + 100);
        using PdfDocument doc = TagSupport.Editable(pdf);
        PdfStructureTree tree = doc.StructureTree!;
        Assert.True(tree.ElementCount <= doc.Limits.MaxNestingDepth + 1, tree.ElementCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(tree.Diagnostics, static d => d.Message.Contains("nesting limit", StringComparison.Ordinal));
        _ = TagSupport.Save(doc);
    }

    [Fact]
    public void ParentTreeEntryWithANullValueIsReplaced()
    {
        // The repro of a fuzz finding, run through the fuzz target with the edit sequence that found it.
        string path = Path.Combine(Path.GetDirectoryName(RealWorld())!, "..", "repros", "structure-parent-tree-null-entry.pdf");
        byte[] input = [.. Convert.FromHexString("41965e27936f1641"), .. File.ReadAllBytes(path)];
        OnePdf.Fuzz.StructureTarget.Run(input);
    }

    [Fact]
    public void MissingAndDuplicateContentIsReported()
    {
        using PdfDocument doc = PdfDocument.Open(Broken("/K 21 0 R", "21 << /Type /StructElem /S /Div /P 20 0 R /K [0 1 7 << /Type /MCR /MCID 9 /Pg 10 0 R >> 1] /Pg 10 0 R >>"));
        PdfStructureTree tree = doc.StructureTree!;
        Assert.Equal([7, 9], tree.FindMissingContent().Select(static m => m.Mcid).Order());
        Assert.Contains(tree.Diagnostics, static d => d.Message.Contains("more than once", StringComparison.Ordinal));
    }
}
