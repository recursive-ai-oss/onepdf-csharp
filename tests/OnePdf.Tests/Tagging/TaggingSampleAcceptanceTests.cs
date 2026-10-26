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
}
