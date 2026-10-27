using System.Text.Json;
using OnePdf.Tests.PdfA;
using OnePdf.Text;

namespace OnePdf.Tests.Tagging;

/// <summary>
/// Tags every sample of <c>testdata/tagging</c> with the roles in its <c>*.roles.json</c>, saves it as PDF/UA-1 and checks that the
/// result passes veraPDF, extracts the same text and draws the same display list.
/// </summary>
public class TaggingSampleAcceptanceTests
{
    public static TheoryData<string> Names() => TaggingSamples.TaggingSampleRolesTests.SampleNames();

    /// <summary>Builds the plan for a roles file against an extraction of the same document.</summary>
    internal static void Build(PdfDocument doc, IReadOnlyList<PdfPageText> pages, JsonElement root, PdfTaggingPlan plan)
    {
        plan.Title = root.GetProperty("title").GetString();
        plan.Language = root.GetProperty("language").GetString();
        foreach (JsonElement e in root.GetProperty("elements").EnumerateArray())
        {
            Element(doc, pages, plan, null, e);
        }
    }

    private static void Element(PdfDocument doc, IReadOnlyList<PdfPageText> pages, PdfTaggingPlan plan, PdfTagElement? parent, JsonElement e)
    {
        string role = e.GetProperty("role").GetString()!;
        JsonElement children = e.GetProperty("children");
        if (role == "Artifact")
        {
            var kind = Enum.Parse<PdfArtifactKind>(e.GetProperty("artifact").GetString()!);
            foreach (JsonElement c in children.EnumerateArray())
            {
                plan.Artifact(Content(doc, pages, c), kind);
            }

            return;
        }

        PdfTagRole r = Enum.Parse<PdfTagRole>(role);
        PdfTagElement el = parent is null ? plan.Add(r) : parent.Add(r);
        if (e.TryGetProperty("alt", out JsonElement alt))
        {
            el.Alt = alt.GetString();
        }

        if (e.TryGetProperty("lang", out JsonElement lang))
        {
            el.Lang = lang.GetString();
        }

        if (e.TryGetProperty("scope", out JsonElement scope))
        {
            el.Scope = Enum.Parse<PdfTagScope>(scope.GetString()!);
        }

        if (e.TryGetProperty("rowSpan", out JsonElement rs))
        {
            el.RowSpan = rs.GetInt32();
        }

        if (e.TryGetProperty("colSpan", out JsonElement cs))
        {
            el.ColSpan = cs.GetInt32();
        }

        foreach (JsonElement c in children.EnumerateArray())
        {
            if (c.TryGetProperty("role", out _))
            {
                Element(doc, pages, plan, el, c);
            }
            else
            {
                el.Add(Content(doc, pages, c));
            }
        }
    }

    private static PdfTagContent Content(PdfDocument doc, IReadOnlyList<PdfPageText> pages, JsonElement r)
    {
        int page = r.GetProperty("page").GetInt32();
        PdfPageText text = pages[page];
        if (r.TryGetProperty("annotation", out JsonElement a))
        {
            return PdfTagContent.Annotation(doc.Pages[page].Annotations[a.GetInt32()]);
        }

        if (r.TryGetProperty("graphics", out JsonElement g))
        {
            double[] b = [.. g.EnumerateArray().Select(static v => v.GetDouble())];
            return PdfTagContent.Graphics(text, new PdfRectangle(b[0], b[1], b[2], b[3]));
        }

        int block = r.GetProperty("block").GetInt32();
        PdfTextBlock bl = text.Blocks[block];
        if (r.TryGetProperty("lines", out JsonElement lines))
        {
            return PdfTagContent.Lines(text, bl, lines[0].GetInt32(), lines[1].GetInt32() - lines[0].GetInt32() + 1);
        }

        if (r.TryGetProperty("line", out JsonElement le))
        {
            PdfTextLine line = bl.Lines[le.GetInt32()];
            if (r.TryGetProperty("words", out JsonElement w))
            {
                return PdfTagContent.Words(text, line, w[0].GetInt32(), w[1].GetInt32() - w[0].GetInt32() + 1);
            }

            return PdfTagContent.Cell(text, line, r.GetProperty("cell").GetInt32());
        }

        return PdfTagContent.Block(text, block);
    }

    private static bool FontClause(string clause) => clause.StartsWith("7.21", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Names))]
    public void TaggedSampleIsEquivalentAndConforms(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(TaggingSamples.TaggingSampleRolesTests.Dir(), file)));
        JsonElement root = json.RootElement;
        string path = Path.GetFullPath(Path.Combine(TaggingSamples.TaggingSampleRolesTests.Dir(), root.GetProperty("source").GetString()!));
        byte[] original = File.ReadAllBytes(path);

        using PdfDocument doc = TagSupport.Editable(original);
        string extractBefore = TagSupport.ExtractionJson(doc);
        string displayBefore = TagSupport.DisplayText(doc);
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);

        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        Build(doc, pages, root, plan);
        PdfTaggingResult result = doc.ApplyTagging(plan);
        Assert.True(result.ElementsWritten > 0);

        byte[] tagged;
        bool fontsOnly = false;
        try
        {
            tagged = TagSupport.Save(doc, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        }
        catch (PdfUAException ex) when (ex.Violations.All(static v => FontClause(v.Clause)))
        {
            // The sample uses fonts that are not embedded: PDF/UA-1 7.21.4.1 and 7.21.7 are about the source file, not the tags. The tagged
            // file is saved without the conformance gate and must then fail on those rules alone.
            fontsOnly = true;
            tagged = TagSupport.Save(doc);
        }
        catch (PdfUAException ex)
        {
            throw new Xunit.Sdk.XunitException($"{file}: {string.Join(" | ", ex.Violations.Select(static v => v.ToString()))}");
        }
        string? outDir = Environment.GetEnvironmentVariable("ONEPDF_TAGGED_OUT");
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, Path.GetFileName(path)), tagged);
        }

        using PdfDocument after = PdfDocument.Open(tagged);
        IReadOnlyList<PdfUAViolation> check = after.CheckPdfUA();
        Assert.True(check.All(v => fontsOnly && FontClause(v.Clause)), $"{file}: {string.Join(" | ", check.Select(static v => v.ToString()))}");
        TagSupport.AssertSame(extractBefore, TagSupport.ExtractionJson(after));
        TagSupport.AssertSame(displayBefore, TagSupport.DisplayText(after));

        if (VeraPdf.Path is not null)
        {
            List<string> failures = VeraPdf.Validate(tagged, "ua1");
            Assert.True(failures.All(f => fontsOnly && f.StartsWith("7.21", StringComparison.Ordinal)), $"{file}: veraPDF ua1: {string.Join("; ", failures.Take(6))}");
        }
    }

    /// <summary>Samples whose blocks each have one role, so the per-block happy path can express them.</summary>
    public static TheoryData<string> HappyNames() => new() { "chrome-columns.roles.json", "chrome-figures.roles.json", "govdocs-210477.roles.json", "chrome-table.roles.json" };

    // Reads the roles file as per-block roles: a heading or paragraph element holding whole blocks, a figure, an artifact, a table whose
    // first block holds the header row. Graphics areas of figures become PdfTagOptions.Figures.
    private static (Dictionary<(int Page, int Block), PdfBlockRole> Roles, List<PdfTagFigure> Figures) Derive(JsonElement root)
    {
        var roles = new Dictionary<(int, int), PdfBlockRole>();
        var figures = new List<PdfTagFigure>();
        bool tableStarted = false;
        foreach (JsonElement e in root.GetProperty("elements").EnumerateArray())
        {
            string role = e.GetProperty("role").GetString()!;
            foreach ((int page, int block, JsonElement r) in References(e))
            {
                if (r.TryGetProperty("graphics", out JsonElement g))
                {
                    double[] b = [.. g.EnumerateArray().Select(static v => v.GetDouble())];
                    figures.Add(new PdfTagFigure(page, new PdfRectangle(b[0], b[1], b[2], b[3]), e.GetProperty("alt").GetString()!));
                    continue;
                }

                bool whole = !r.TryGetProperty("line", out _); // a "lines" range is the whole block in these files
                PdfBlockRole value;
                switch (role)
                {
                    case "H1" or "H2" or "H3" or "H4" or "H5" or "H6":
                        Assert.True(whole);
                        value = PdfBlockRole.Heading(role[1] - '0');
                        break;
                    case "P":
                        Assert.True(whole);
                        value = PdfBlockRole.Paragraph;
                        break;
                    case "Figure":
                        value = PdfBlockRole.Figure(e.GetProperty("alt").GetString()!);
                        break;
                    case "Artifact":
                        value = PdfBlockRole.Artifact;
                        break;
                    case "Table":
                        value = tableStarted ? PdfBlockRole.TableContinued : PdfBlockRole.Table;
                        break;
                    default:
                        throw new InvalidOperationException("role " + role + " has no per-block form");
                }

                if (role == "Table")
                {
                    tableStarted = true;
                    if (roles.ContainsKey((page, block)))
                    {
                        continue;
                    }
                }

                roles[(page, block)] = value;
            }
        }

        return (roles, figures);
    }

    private static IEnumerable<(int Page, int Block, JsonElement Ref)> References(JsonElement e)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(e);
        var found = new List<(int, int, JsonElement)>();
        while (stack.Count > 0)
        {
            JsonElement x = stack.Pop();
            if (!x.TryGetProperty("children", out JsonElement kids))
            {
                continue;
            }

            foreach (JsonElement c in kids.EnumerateArray())
            {
                if (c.TryGetProperty("role", out _))
                {
                    stack.Push(c);
                }
                else
                {
                    found.Add((c.GetProperty("page").GetInt32(), c.TryGetProperty("block", out JsonElement bl) ? bl.GetInt32() : -1, c));
                }
            }
        }

        return found.Where(static f => f.Item2 >= 0 || f.Item3.TryGetProperty("graphics", out _));
    }

    [Theory]
    [MemberData(nameof(HappyNames))]
    public void HappyPathTagsSamplesEquivalentlyAndConforms(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(TaggingSamples.TaggingSampleRolesTests.Dir(), file)));
        JsonElement root = json.RootElement;
        string path = Path.GetFullPath(Path.Combine(TaggingSamples.TaggingSampleRolesTests.Dir(), root.GetProperty("source").GetString()!));
        (Dictionary<(int Page, int Block), PdfBlockRole> roles, List<PdfTagFigure> figures) = Derive(root);

        using PdfDocument doc = TagSupport.Editable(File.ReadAllBytes(path));
        string extractBefore = TagSupport.ExtractionJson(doc);
        string displayBefore = TagSupport.DisplayText(doc);
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        var options = new PdfTagOptions { Title = root.GetProperty("title").GetString(), Language = root.GetProperty("language").GetString() };
        foreach (PdfTagFigure f in figures)
        {
            options.Figures.Add(f);
        }

        doc.Tag(pages, b => roles.TryGetValue((b.PageIndex, b.BlockIndex), out PdfBlockRole r) ? r : null, options);
        byte[] tagged;
        bool fontsOnly = false;
        try
        {
            tagged = TagSupport.Save(doc, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        }
        catch (PdfUAException ex) when (ex.Violations.All(static v => FontClause(v.Clause)))
        {
            // Fonts that are not embedded are the source file's defect (7.21.4.1, 7.21.7), not the tags'.
            fontsOnly = true;
            tagged = TagSupport.Save(doc);
        }

        if (Environment.GetEnvironmentVariable("ONEPDF_TAGGED_OUT") is { Length: > 0 } outDir)
        {
            Directory.CreateDirectory(Path.Combine(outDir, "happy"));
            File.WriteAllBytes(Path.Combine(outDir, "happy", Path.GetFileName(path)), tagged);
        }

        using PdfDocument after = PdfDocument.Open(tagged);
        IReadOnlyList<PdfUAViolation> check = after.CheckPdfUA();
        Assert.True(check.All(v => fontsOnly && FontClause(v.Clause)), string.Join(" | ", check.Select(static v => v.ToString())));
        TagSupport.AssertSame(extractBefore, TagSupport.ExtractionJson(after));
        TagSupport.AssertSame(displayBefore, TagSupport.DisplayText(after));
        if (VeraPdf.Path is not null)
        {
            List<string> failures = VeraPdf.Validate(tagged, "ua1");
            Assert.True(failures.All(f => fontsOnly && f.StartsWith("7.21", StringComparison.Ordinal)), $"{file}: veraPDF ua1: {string.Join("; ", failures.Take(6))}");
        }
    }
}
