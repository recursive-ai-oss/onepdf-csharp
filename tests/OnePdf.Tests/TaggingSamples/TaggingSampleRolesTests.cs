using System.Runtime.CompilerServices;
using System.Text.Json;
using OnePdf.Text;

namespace OnePdf.Tests.TaggingSamples;

/// <summary>
/// Reads the role annotations of <c>testdata/tagging</c> (<c>*.roles.json</c>, format in that folder's README) and checks them against what
/// text extraction returns for the same file: every reference points at something that exists, every word of extracted text is assigned
/// exactly once, and the element tree nests legally. The files are the contract an accessibility-tagging run is checked against.
/// </summary>
public class TaggingSampleRolesTests
{
    private static readonly string[] PromisedRoles = ["H1", "H2", "P", "L", "LI", "Lbl", "LBody", "Table", "TR", "TH", "TD", "Figure", "Link", "Artifact", "Div"];
    private static readonly string[] BlockRoles = ["H1", "H2", "H3", "H4", "H5", "H6", "P", "L", "Table", "Figure", "Div", "Artifact"];

    internal static string Dir([CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata", "tagging");
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        throw new DirectoryNotFoundException("testdata/tagging");
    }

    public static IEnumerable<string> Samples() => Directory.GetFiles(Dir(), "*.roles.json").Select(Path.GetFileName).Order(StringComparer.Ordinal)!;

    public static TheoryData<string> SampleNames()
    {
        var data = new TheoryData<string>();
        foreach (string s in Samples())
        {
            data.Add(s);
        }

        return data;
    }

    [Fact]
    public void TheSetCoversEveryKindOfContentItPromises()
    {
        var roles = new HashSet<string>();
        foreach (string s in Samples())
        {
            using JsonDocument j = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir(), s)));
            CollectRoles(j.RootElement.GetProperty("elements"), roles);
        }

        foreach (string r in PromisedRoles)
        {
            Assert.Contains(r, roles);
        }

        Assert.True(Samples().Count() >= 8);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void RolesMatchTheExtraction(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir(), file)));
        JsonElement root = json.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("language").GetString()));

        string pdf = Path.GetFullPath(Path.Combine(Dir(), root.GetProperty("source").GetString()!));
        Assert.True(File.Exists(pdf), pdf);
        using PdfDocument doc = PdfDocument.Open(pdf);
        IReadOnlyList<PdfPageText> pages = doc.ExtractText(new PdfTextOptions { RetainGlyphs = true });

        // The block count per page is part of the contract: block indices are only meaningful against the same extraction.
        int[] counts = [.. root.GetProperty("blockCounts").EnumerateArray().Select(static e => e.GetInt32())];
        Assert.Equal(counts, pages.Select(static p => p.Blocks.Count).ToArray());

        var validator = new Validator(doc, pages);
        foreach (JsonElement e in root.GetProperty("elements").EnumerateArray())
        {
            validator.Element(e, parent: null, file);
        }

        validator.AssertComplete(file);
    }

    private static void CollectRoles(JsonElement list, HashSet<string> roles)
    {
        foreach (JsonElement e in list.EnumerateArray())
        {
            if (e.TryGetProperty("role", out JsonElement r))
            {
                roles.Add(r.GetString()!);
                if (e.TryGetProperty("children", out JsonElement c))
                {
                    CollectRoles(c, roles);
                }
            }
        }
    }

    private sealed class Validator(PdfDocument doc, IReadOnlyList<PdfPageText> pages)
    {
        private static readonly string[] ArtifactKinds = ["Header", "Footer", "Watermark", "Layout"];
        private static readonly string[] GraphicsParents = ["Figure", "Artifact", "Lbl"];
        private static readonly string[] TextParents = ["P", "H1", "H2", "H3", "H4", "H5", "H6", "Lbl", "LBody", "TH", "TD", "Figure", "Artifact", "Link"];
        private readonly Dictionary<(int Page, int Block, int Line, int Word), int> _words = [];
        private readonly HashSet<(int Page, int Annotation)> _annotations = [];

        // Children each parent may hold (element roles); text, graphics and annotation references are checked separately.
        private static readonly Dictionary<string, string[]> Allowed = new()
        {
            ["L"] = ["LI"],
            ["LI"] = ["Lbl", "LBody"],
            ["Lbl"] = [],
            ["LBody"] = ["P", "L", "Link", "Figure"],
            ["Table"] = ["TR"],
            ["TR"] = ["TH", "TD"],
            ["TH"] = ["P", "Link", "Figure"],
            ["TD"] = ["P", "Link", "Figure"],
            ["P"] = ["Link"],
            ["H1"] = ["Link"],
            ["H2"] = ["Link"],
            ["H3"] = ["Link"],
            ["H4"] = ["Link"],
            ["H5"] = ["Link"],
            ["H6"] = ["Link"],
            ["Link"] = [],
            ["Figure"] = [],
            ["Artifact"] = [],
            ["Div"] = BlockRoles,
        };

        public void Element(JsonElement e, string? parent, string where)
        {
            string role = e.GetProperty("role").GetString()!;
            Assert.True(Allowed.ContainsKey(role), $"{where}: unknown role {role}");
            if (parent is null)
            {
                Assert.True(BlockRoles.Contains(role), $"{where}: {role} cannot be a top-level element");
            }
            else
            {
                Assert.True(Allowed[parent].Contains(role), $"{where}: {role} cannot be inside {parent}");
            }

            if (role == "Figure")
            {
                Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("alt").GetString()), $"{where}: Figure needs alt text");
            }

            if (role == "TH")
            {
                Assert.True(e.TryGetProperty("scope", out JsonElement s) && s.GetString() is "Column" or "Row" or "Both", $"{where}: TH needs a scope");
            }

            if (role == "Artifact")
            {
                Assert.Contains(e.GetProperty("artifact").GetString(), ArtifactKinds);
            }

            Assert.True(e.TryGetProperty("children", out JsonElement children) && children.GetArrayLength() > 0, $"{where}: {role} is empty");
            bool hasAnnotation = false;
            bool hasContent = false;
            foreach (JsonElement c in children.EnumerateArray())
            {
                if (c.TryGetProperty("role", out _))
                {
                    Element(c, role, where);
                    hasContent = true;
                    continue;
                }

                Reference(c, role, where, ref hasAnnotation);
                hasContent = true;
            }

            Assert.True(hasContent, where);
            if (role == "Link")
            {
                Assert.True(hasAnnotation, $"{where}: Link needs an annotation reference");
            }

            if (role is "L")
            {
                Assert.All(children.EnumerateArray(), c => Assert.Equal("LI", c.GetProperty("role").GetString()));
            }

            if (role is "TR" or "Table" or "LI")
            {
                Assert.All(children.EnumerateArray(), c => Assert.True(c.TryGetProperty("role", out _), $"{where}: {role} holds elements only"));
            }
        }

        private void Reference(JsonElement r, string parent, string where, ref bool hasAnnotation)
        {
            int page = r.GetProperty("page").GetInt32();
            Assert.InRange(page, 0, pages.Count - 1);
            PdfPageText text = pages[page];
            if (r.TryGetProperty("annotation", out JsonElement a))
            {
                Assert.Equal("Link", parent);
                int index = a.GetInt32();
                IReadOnlyList<PdfAnnotation> annotations = doc.Pages[page].Annotations;
                Assert.InRange(index, 0, annotations.Count - 1);
                Assert.Equal("Link", annotations[index].Subtype);
                Assert.True(_annotations.Add((page, index)), $"{where}: annotation {page}/{index} used twice");
                hasAnnotation = true;
                return;
            }

            if (r.TryGetProperty("graphics", out JsonElement g))
            {
                Assert.Contains(parent, GraphicsParents);
                double[] box = [.. g.EnumerateArray().Select(static v => v.GetDouble())];
                Assert.Equal(4, box.Length);
                Assert.True(box[0] < box[2] && box[1] < box[3], $"{where}: empty rectangle");
                Assert.True(box[0] >= -1 && box[1] >= -1 && box[2] <= text.Width + 1 && box[3] <= text.Height + 1, $"{where}: rectangle {string.Join(',', box)} outside the page");
                return;
            }

            Assert.Contains(parent, TextParents);
            int block = r.GetProperty("block").GetInt32();
            Assert.InRange(block, 0, text.Blocks.Count - 1);
            PdfTextBlock b = text.Blocks[block];
            if (r.TryGetProperty("lines", out JsonElement lines))
            {
                int from = lines[0].GetInt32();
                int to = lines[1].GetInt32();
                Assert.True(0 <= from && from <= to && to < b.Lines.Count, $"{where}: lines {from}-{to} of block {page}/{block}");
                for (int l = from; l <= to; l++)
                {
                    Mark(page, block, l, 0, b.Lines[l].Words.Count - 1, where);
                }

                return;
            }

            if (r.TryGetProperty("line", out JsonElement lineElement))
            {
                int l = lineElement.GetInt32();
                Assert.InRange(l, 0, b.Lines.Count - 1);
                PdfTextLine line = b.Lines[l];
                if (r.TryGetProperty("words", out JsonElement w))
                {
                    Mark(page, block, l, w[0].GetInt32(), w[1].GetInt32(), where);
                }
                else
                {
                    // A table cell: the words from this slot's start up to the next slot.
                    int c = r.GetProperty("cell").GetInt32();
                    IReadOnlyList<int> starts = line.CellStarts;
                    Assert.InRange(c, 0, starts.Count - 1);
                    int last = c + 1 < starts.Count ? starts[c + 1] - 1 : line.Words.Count - 1;
                    Mark(page, block, l, starts[c], last, where);
                }

                return;
            }

            for (int l = 0; l < b.Lines.Count; l++)
            {
                Mark(page, block, l, 0, b.Lines[l].Words.Count - 1, where);
            }
        }

        private void Mark(int page, int block, int line, int from, int to, string where)
        {
            int count = pages[page].Blocks[block].Lines[line].Words.Count;
            Assert.True(0 <= from && from <= to && to < count, $"{where}: words {from}-{to} of line {page}/{block}/{line} ({count} words)");
            for (int w = from; w <= to; w++)
            {
                _words[(page, block, line, w)] = _words.GetValueOrDefault((page, block, line, w)) + 1;
            }
        }

        public void AssertComplete(string where)
        {
            var problems = new List<string>();
            foreach (PdfPageText p in pages)
            {
                for (int b = 0; b < p.Blocks.Count; b++)
                {
                    for (int l = 0; l < p.Blocks[b].Lines.Count; l++)
                    {
                        for (int w = 0; w < p.Blocks[b].Lines[l].Words.Count; w++)
                        {
                            int n = _words.GetValueOrDefault((p.PageIndex, b, l, w));
                            if (n != 1)
                            {
                                problems.Add($"{p.PageIndex}/{b}/{l}/{w} '{p.Blocks[b].Lines[l].Words[w].Text}' assigned {n}x");
                            }
                        }
                    }
                }

                // Every link annotation is a Link element, so a link is never left without its tag.
                IReadOnlyList<PdfAnnotation> annotations = doc.Pages[p.PageIndex].Annotations;
                for (int a = 0; a < annotations.Count; a++)
                {
                    if (annotations[a].Subtype == "Link" && !_annotations.Contains((p.PageIndex, a)))
                    {
                        problems.Add($"{p.PageIndex}: link annotation {a} not in a Link element");
                    }
                }
            }

            Assert.True(problems.Count == 0, $"{where}: {problems.Count} problem(s), first: {string.Join("; ", problems.Take(8))}");
        }
    }
}
