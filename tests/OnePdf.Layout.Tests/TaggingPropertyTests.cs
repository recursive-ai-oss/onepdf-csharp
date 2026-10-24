namespace OnePdf.Layout.Tests;

public class TaggingPropertyTests
{
    public static int Seeds => 400;

    private static LayoutDocument BuildTagged(int seed, out TreeGen gen)
    {
        LayoutDocument doc = PropertyTests.Build(seed, out gen);
        // Exercise the document-level direction as well as the section's.
        if (doc.Pages[0].Direction == LayoutDirection.RightToLeft && seed % 2 == 0)
        {
            doc.Direction = LayoutDirection.RightToLeft;
            doc.Pages[0].Direction = null;
        }

        return doc;
    }

    [Fact]
    public void GeneratedTreesAreTaggedConsistently()
    {
        int trees = 0;
        int rtlTrees = 0;
        int pages = 0;
        int mcids = 0;
        int rows = 0;
        int items = 0;
        for (int seed = 1; seed <= Seeds; seed++)
        {
            LayoutDocument doc = BuildTagged(seed, out TreeGen gen);
            string who = $"seed {seed}";
            byte[] bytes = Support.Save(doc);
            using PdfDocument back = PdfDocument.Open(bytes);
            pages += back.Pages.Count;
            SNode tree = TagSupport.ReadTree(back);

            // Everything painted is tagged content or an artifact.
            (List<string> failures, List<int>[] used) = TagSupport.Scan(back);
            Assert.True(failures.Count == 0, $"{who}: {string.Join("; ", failures)}");

            // Every marked-content sequence on a page belongs to exactly one structure element, and every content item of the tree is on its page.
            var inTree = new List<int>[back.Pages.Count];
            for (int i = 0; i < inTree.Length; i++)
            {
                inTree[i] = [];
            }

            foreach (SNode n in tree.Descendants())
            {
                foreach ((int page, int mcid) in n.Content)
                {
                    inTree[page].Add(mcid);
                }

                // Parent links agree with the tree.
                foreach (SNode k in n.Kids)
                {
                    Assert.True(k.Dict.GetRaw("P") is PdfReference r && r.Number == n.Number, $"{who}: {k.Type} does not name its parent {n.Type}");
                }
            }

            for (int i = 0; i < inTree.Length; i++)
            {
                int[] a = [.. used[i].Order()];
                int[] b = [.. inTree[i].Order()];
                Assert.True(a.AsSpan().SequenceEqual(b), $"{who} page {i}: content ids {string.Join(",", a)} vs tree {string.Join(",", b)}");
                Assert.True(a.Distinct().Count() == a.Length, $"{who} page {i}: an id is used twice");
                mcids += a.Length;
            }

            // The tree has one element for each table row, section and list entry of the element tree.
            int bodyRows = gen.Tables.Sum(t => t.Table.Rows.Count);
            int headRows = gen.Tables.Sum(t => t.Table.HeaderRows.Count);
            int footRows = gen.Tables.Sum(t => t.Table.FooterRows.Count);
            SNode[] all = [.. tree.Descendants()];
            Assert.Equal(gen.Tables.Count, all.Count(n => n.Type == "Table"));
            Assert.Equal(bodyRows, all.Where(n => n.Type == "TBody").Sum(n => n.Kids.Count(k => k.Type == "TR")));
            Assert.Equal(headRows, all.Where(n => n.Type == "THead").Sum(n => n.Kids.Count(k => k.Type == "TR")));
            Assert.Equal(footRows, all.Where(n => n.Type == "TFoot").Sum(n => n.Kids.Count(k => k.Type == "TR")));
            Assert.Equal(gen.Lists.Count, all.Count(n => n.Type == "L"));
            Assert.Equal(gen.Lists.Sum(l => l.Items.Count), all.Count(n => n.Type == "LI"));
            Assert.All(all.Where(n => n.Type == "LI"), li => Assert.True(li.Kids.Count is 1 or 2 && li.Kids[^1].Type == "LBody", $"{who}: LI shape {li.Shape()}"));
            Assert.All(all.Where(n => n.Type == "TR"), tr => Assert.All(tr.Kids, c => Assert.True(c.Type is "TD" or "TH", $"{who}: {c.Type} inside TR")));
            Assert.All(all.Where(n => n.Type is "TD" or "TH"), c => Assert.True(c.Parent!.Type == "TR"));
            rows += bodyRows;
            items += gen.Lists.Sum(l => l.Items.Count);

            // Right-to-left trees put row items from the right; left-to-right trees from the left.
            bool rtl = doc.Direction == LayoutDirection.RightToLeft || doc.Pages[0].Direction == LayoutDirection.RightToLeft;
            if (rtl)
            {
                rtlTrees++;
            }

            List<Placed> bodies = Support.Pages(BuildTagged(seed, out _), out _);
            foreach (Placed n in bodies.SelectMany(Support.Walk).Where(n => n.Element is Row))
            {
                for (int i = 1; i < n.Children.Count; i++)
                {
                    double prev = n.Children[i - 1].X;
                    double cur = n.Children[i].X;
                    Assert.True(rtl ? cur <= prev + 1e-6 : cur >= prev - 1e-6, $"{who}: row items out of order (rtl={rtl}): {prev} then {cur}");
                }
            }

            // Output is byte-identical between runs.
            Assert.True(bytes.AsSpan().SequenceEqual(Support.Save(BuildTagged(seed, out _))), $"{who}: two runs differ");
            trees++;
        }

        TestContext.Current.SendDiagnosticMessage($"trees={trees} rtl={rtlTrees} pages={pages} content sequences={mcids} body rows={rows} list items={items}");
        Assert.True(rtlTrees > 40 && rows > 500 && items > 500);
    }
}
