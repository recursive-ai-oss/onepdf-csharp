using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.Tagging;

/// <summary>
/// Tags the committed real-world files with every block a paragraph (everything else becomes an artifact) and checks that nothing a
/// reader sees changes: the same extraction, the same display list apart from marked content. Files that are tagged already are refused, as they must be.
/// </summary>
public class TaggingRealWorldTests
{
    private static string Dir([CallerFilePath] string source = "")
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

    [Fact]
    public void EveryCommittedFileKeepsItsTextAndDrawingWhenTagged()
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        int tagged = 0, refused = 0;
        foreach (string file in Directory.GetFiles(Dir(), "*.pdf").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            try
            {
                using PdfDocument doc = PdfDocument.Open(File.ReadAllBytes(file), new PdfOpenOptions { AllowEditing = true });
                string extractBefore = TagSupport.ExtractionJson(doc);
                string displayBefore = TagSupport.DisplayText(doc);
                IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
                PdfTaggingPlan plan = doc.CreateTaggingPlan();
                foreach (PdfPageText p in pages)
                {
                    foreach (PdfTextBlock b in p.Blocks)
                    {
                        plan.Add(PdfTagRole.P, PdfTagContent.Block(p, b));
                    }
                }

                PdfTaggingResult r;
                try
                {
                    r = doc.ApplyTagging(plan);
                }
                catch (PdfTaggingException ex)
                {
                    refused++;
                    if (ex.Reason != PdfTaggingFailure.AlreadyTagged)
                    {
                        failures.Add(name + ": refused for " + ex.Reason + ": " + ex.Message);
                    }

                    report.Append(name).Append(": refused ").Append(ex.Reason).Append(": ").Append(ex.Message).Append('\n');
                    continue;
                }

                byte[] saved = TagSupport.Save(doc);
                using PdfDocument after = PdfDocument.Open(saved);
                bool same = TagSupport.Same(extractBefore, TagSupport.ExtractionJson(after));
                bool drawn = TagSupport.Same(displayBefore, TagSupport.DisplayText(after));
                tagged++;
                report.Append(name).Append(": tagged, ").Append(pages.Sum(static p => p.Blocks.Count)).Append(" blocks, ").Append(r.ContentSequences).Append(" sequences, ")
                    .Append(r.ArtifactSequences).Append(" artifacts, ").Append(r.FormsCopied).Append(" form copies, text ").Append(same ? "same" : "DIFFERENT").Append(", drawing ").Append(drawn ? "same" : "DIFFERENT").Append('\n');
                if (!same || !drawn)
                {
                    if (Environment.GetEnvironmentVariable("ONEPDF_TAGGED_OUT") is { Length: > 0 } od)
                    {
                        Directory.CreateDirectory(od);
                        File.WriteAllText(Path.Combine(od, name + ".extract.before"), extractBefore);
                        File.WriteAllText(Path.Combine(od, name + ".extract.after"), TagSupport.ExtractionJson(after));
                        File.WriteAllText(Path.Combine(od, name + ".display.before"), displayBefore);
                        File.WriteAllText(Path.Combine(od, name + ".display.after"), TagSupport.DisplayText(after));
                    }

                    failures.Add(name + (same ? string.Empty : " extraction") + (drawn ? string.Empty : " display list"));
                }
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                failures.Add(name + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        if (Environment.GetEnvironmentVariable("ONEPDF_TAGGED_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "realworld.txt"), $"tagged {tagged}, refused {refused}\n{report}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(tagged >= 55, $"only {tagged} files tagged, {refused} refused");
    }
}
