using System.Text.RegularExpressions;
using OnePdf.Tests.PdfA;

namespace OnePdf.Tests.PdfUA;

/// <summary>
/// ISO 14289-1 7.21.8: text operators that show the .notdef glyph. Documents come from the writer with a font that lacks a character;
/// veraPDF (black box, ONEPDF_VERAPDF) is asked about the same files when it is installed.
/// </summary>
[Collection("PdfUA structure and veraPDF")]
public class NotdefTextTests
{
    private static bool VeraHasNotdef(byte[] pdf) => VeraPdf.Validate(pdf, "ua1").Exists(static f => f.StartsWith("7.21.8", StringComparison.Ordinal));

    [Fact]
    public void FindListsHiddenAndVisibleRunsWithTheirFacts()
    {
        using PdfDocument doc = PdfDocument.Open(NotdefDocs.Build());
        IReadOnlyList<PdfNotdefRun> runs = doc.FindNotdefText();
        Assert.Equal(2, runs.Count);

        PdfNotdefRun hidden = runs.Single(static r => !r.IsVisible);
        Assert.Equal(PdfTextRenderMode.Invisible, hidden.RenderMode);
        Assert.Equal(PdfNotdefSource.PageContent, hidden.Source);
        Assert.Equal(0, hidden.PageIndex);
        Assert.Equal(-1, hidden.StreamObject);
        Assert.Equal([0], hidden.Codes);
        Assert.Equal(5, hidden.ShownCodes.Count);
        Assert.True(hidden.OptionalContentVisible);
        Assert.Empty(hidden.OptionalContentGroups);
        Assert.StartsWith("ab", hidden.Text, StringComparison.Ordinal);
        Assert.Contains("LiberationSans", hidden.BaseFont, StringComparison.Ordinal);

        PdfNotdefRun visible = runs.Single(static r => r.IsVisible);
        Assert.Equal(PdfTextRenderMode.Fill, visible.RenderMode);
        Assert.True(visible.Offset > hidden.Offset);
    }

    [Fact]
    public void ADocumentWithoutNotdefTextHasNoRunsAndNothingChanges()
    {
        byte[] pdf = NotdefDocs.Build(hiddenOnPage: false, visibleOnPage: false);
        using PdfDocument doc = NotdefDocs.Editable(pdf);
        Assert.Empty(doc.FindNotdefText());
        PdfNotdefRemoval r = doc.RemoveNotdefText();
        Assert.Equal(0, r.GlyphsRemoved);
        Assert.Equal(0, r.StreamsChanged);
        Assert.Empty(r.Removed);
        Assert.Empty(r.Kept);
    }

    [Fact]
    public void DefaultScopeRemovesHiddenTextOnlyAndKeepsEveryOtherGlyphInPlace()
    {
        byte[] pdf = NotdefDocs.Build();
        using PdfDocument doc = NotdefDocs.Editable(pdf);
        List<string> before = NotdefDocs.Glyphs(doc);
        string textBefore = ExtractAll(doc);

        PdfNotdefRemoval r = doc.RemoveNotdefText();
        Assert.Single(r.Removed);
        Assert.Equal(1, r.GlyphsRemoved);
        Assert.Equal(1, r.OperatorsChanged);
        Assert.Equal(1, r.StreamsChanged);
        PdfNotdefKept kept = Assert.Single(r.Kept);
        Assert.Equal(PdfNotdefKeepReason.Visible, kept.Reason);

        byte[] saved = NotdefDocs.Save(doc);
        using PdfDocument back = PdfDocument.Open(saved);
        List<string> after = NotdefDocs.Glyphs(back);
        Assert.Equal(before.Count - 1, after.Count);
        // Everything but the one removed glyph (code 0, mode 3) is where it was, drawn the same.
        List<string> expected = [.. before];
        expected.Remove(before.First(static g => g.StartsWith("0/3@", StringComparison.Ordinal)));
        Assert.Equal(expected, after);
        Assert.Equal(NotdefDocs.Glyphs(doc, visibleOnly: true), NotdefDocs.Glyphs(back, visibleOnly: true));

        // The glyphs around the removed one stay put, so extraction reads the gap as a word space where the code was.
        string textAfter = ExtractAll(back);
        string lost = r.Removed[0].NotdefText;
        Assert.Equal(textBefore.Replace("ab" + lost + "cd", "ab cd", StringComparison.Ordinal), textAfter);

        IReadOnlyList<PdfNotdefRun> left = back.FindNotdefText();
        Assert.Single(left);
        Assert.True(left[0].IsVisible);
    }

    [Fact]
    public void ScopeAllRemovesVisibleTextToo()
    {
        using PdfDocument doc = NotdefDocs.Editable(NotdefDocs.Build());
        PdfNotdefRemoval r = doc.RemoveNotdefText(new PdfNotdefRemoveOptions { Scope = PdfNotdefScope.All });
        Assert.Equal(2, r.Removed.Count);
        Assert.Empty(r.Kept);
        using PdfDocument back = PdfDocument.Open(NotdefDocs.Save(doc));
        Assert.Empty(back.FindNotdefText());
    }

    [Fact]
    public void IncludeCanKeepRuns()
    {
        using PdfDocument doc = NotdefDocs.Editable(NotdefDocs.Build());
        PdfNotdefRemoval r = doc.RemoveNotdefText(new PdfNotdefRemoveOptions { Scope = PdfNotdefScope.All, Include = static run => run.IsVisible });
        PdfNotdefRun removed = Assert.Single(r.Removed);
        Assert.True(removed.IsVisible);
        PdfNotdefKept kept = Assert.Single(r.Kept);
        Assert.Equal(PdfNotdefKeepReason.ExcludedByCaller, kept.Reason);
        Assert.False(kept.Run.IsVisible);
    }

    [Fact]
    public void DryRunReportsWithoutChangingAndWorksOnAReadOnlyDocument()
    {
        byte[] pdf = NotdefDocs.Build();
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfNotdefRemoval r = doc.RemoveNotdefText(new PdfNotdefRemoveOptions { DryRun = true });
        Assert.Single(r.Removed);
        Assert.Equal(1, r.StreamsChanged);
        Assert.Equal(2, doc.FindNotdefText().Count);
        Assert.Throws<InvalidOperationException>(() => doc.RemoveNotdefText());
    }

    [Fact]
    public void QuoteAndDoubleQuoteOperatorsKeepTheirLineAndSpacingEffects()
    {
        foreach (string op in new[] { "'", "\"" })
        {
            using PdfDocument doc = NotdefDocs.Editable(NotdefDocs.Build(visibleOnPage: false));
            string content = NotdefDocs.PageContent(doc);
            // The hidden string is the only one shown after "3 Tr".
            Match m = Regex.Match(content, @"3 Tr[\s\S]*?(<[0-9A-Fa-f]+>)\s*Tj");
            Assert.True(m.Success, content);
            string show = op == "'" ? m.Groups[1].Value + " '" : "2 1 " + m.Groups[1].Value + " \"";
            string rewritten = content.Remove(m.Groups[1].Index, m.Length - (m.Groups[1].Index - m.Index)).Insert(m.Groups[1].Index, show);
            doc.Pages[0].Dictionary.SetEntry("Contents", doc.AddObject(doc.CreateStream(System.Text.Encoding.Latin1.GetBytes(rewritten), compress: false)));
            using PdfDocument reread = NotdefDocs.Editable(NotdefDocs.Save(doc));
            List<string> before = NotdefDocs.Glyphs(reread);
            Assert.Single(reread.FindNotdefText());

            PdfNotdefRemoval r = reread.RemoveNotdefText();
            Assert.Equal(1, r.GlyphsRemoved);
            string after = NotdefDocs.PageContent(reread);
            Assert.Contains(" TJ", after, StringComparison.Ordinal);
            Assert.Contains("T*", after, StringComparison.Ordinal);
            if (op == "\"")
            {
                Assert.Contains("2 Tw 1 Tc T*", after, StringComparison.Ordinal);
            }

            using PdfDocument back = PdfDocument.Open(NotdefDocs.Save(reread));
            List<string> glyphsAfter = NotdefDocs.Glyphs(back);
            List<string> expected = [.. before];
            expected.Remove(before.First(static g => g.StartsWith("0/3@", StringComparison.Ordinal)));
            Assert.Equal(expected, glyphsAfter);
        }
    }

    [Fact]
    public void ASharedFormWhoseUsesDisagreeAboutVisibilityIsLeftAlone()
    {
        // Page 1 draws the form in mode 3, page 2 draws it visible: the same operator is hidden in one use and drawn in the other.
        byte[] pdf = NotdefDocs.ShareForm(NotdefDocs.Build(form: true, hiddenOnPage: false, visibleOnPage: false), hiddenOnFirstPage: true, hiddenOnSecondPage: false);
        using PdfDocument doc = NotdefDocs.Editable(pdf);
        IReadOnlyList<PdfNotdefRun> runs = doc.FindNotdefText();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, static r => Assert.Equal(PdfNotdefSource.Form, r.Source));
        Assert.Equal(runs[0].StreamObject, runs[1].StreamObject);
        Assert.True(runs[0].StreamObject > 0);
        Assert.Equal([false, true], [.. runs.Select(static r => r.IsVisible)]);
        string before = NotdefDocs.PageContent(doc);

        PdfNotdefRemoval keep = doc.RemoveNotdefText();
        Assert.Empty(keep.Removed);
        Assert.Equal(0, keep.StreamsChanged);
        Assert.Equal([PdfNotdefKeepReason.SharedFormDisagrees, PdfNotdefKeepReason.Visible], [.. keep.Kept.Select(static k => k.Reason)]);
        Assert.Equal(2, NotdefDocs.Glyphs(doc).Count(static g => g.StartsWith("0/", StringComparison.Ordinal)));
        Assert.Equal(before, NotdefDocs.PageContent(doc));

        // With the whole scope the visible use can go, and then the hidden one goes with it.
        PdfNotdefRemoval all = doc.RemoveNotdefText(new PdfNotdefRemoveOptions { Scope = PdfNotdefScope.All });
        Assert.Equal(2, all.Removed.Count);
        Assert.Equal(1, all.StreamsChanged);
    }

    [Fact]
    public void ASharedFormUsedOnlyHiddenIsEditedOnceForAllPages()
    {
        byte[] pdf = NotdefDocs.ShareForm(NotdefDocs.Build(form: true, hiddenOnPage: false, visibleOnPage: false), hiddenOnFirstPage: true, hiddenOnSecondPage: true);
        using PdfDocument doc = NotdefDocs.Editable(pdf);
        Assert.Equal(2, doc.FindNotdefText().Count);
        PdfNotdefRemoval r = doc.RemoveNotdefText();
        Assert.Equal(2, r.Removed.Count);
        Assert.Equal([0, 1], [.. r.Removed.Select(static x => x.PageIndex)]);
        Assert.Equal(1, r.OperatorsChanged);
        Assert.Equal(1, r.GlyphsRemoved);
        Assert.Equal(1, r.StreamsChanged);
        using PdfDocument back = PdfDocument.Open(NotdefDocs.Save(doc));
        Assert.Empty(back.FindNotdefText());
        Assert.Equal(NotdefDocs.Glyphs(doc), NotdefDocs.Glyphs(back));
    }

    [Fact]
    public void OptionalContentThatIsSwitchedOffCountsAsHidden()
    {
        using PdfDocument doc = NotdefDocs.Editable(NotdefDocs.Build(hiddenOnPage: false));
        PdfPage page = doc.Pages[0];
        string content = NotdefDocs.PageContent(doc);
        var ocg = new PdfDictionary();
        ocg.SetEntry("Type", new PdfName("OCG"));
        ocg.SetEntry("Name", PdfString.FromText("OCR layer"));
        PdfReference ocgRef = doc.AddObject(ocg);
        var props = new PdfDictionary();
        props.SetEntry("oc1", ocgRef);
        PdfDictionary res = page.Dictionary.GetDictionary("Resources")!;
        res.SetEntry("Properties", props);
        var d = new PdfDictionary();
        d.SetEntry("OFF", NewArray(ocgRef));
        var ocp = new PdfDictionary();
        ocp.SetEntry("OCGs", NewArray(ocgRef));
        ocp.SetEntry("D", d);
        doc.Catalog.SetEntry("OCProperties", ocp);
        string wrapped = "/OC /oc1 BDC\n" + content + "\nEMC\n";
        page.Dictionary.SetEntry("Contents", doc.AddObject(doc.CreateStream(System.Text.Encoding.Latin1.GetBytes(wrapped), compress: false)));

        using PdfDocument reread = NotdefDocs.Editable(NotdefDocs.Save(doc));
        PdfNotdefRun run = Assert.Single(reread.FindNotdefText());
        Assert.False(run.IsVisible);
        Assert.False(run.OptionalContentVisible);
        Assert.Equal(PdfTextRenderMode.Fill, run.RenderMode);
        Assert.Equal(["OCR layer"], run.OptionalContentGroups);
        Assert.Single(reread.RemoveNotdefText().Removed);
    }

    [Fact]
    public void CheckPdfUAReportsTheTextAndTheRemovalClearsIt()
    {
        using PdfDocument doc = NotdefDocs.Editable(NotdefDocs.Build(visibleOnPage: false));
        PdfUAViolation v = Assert.Single(doc.CheckPdfUA(), static x => x.Clause == "7.21.8");
        Assert.Contains("none of that text is drawn", v.Message, StringComparison.Ordinal);
        Assert.Equal(0, v.PageIndex);
        doc.RemoveNotdefText();
        Assert.DoesNotContain(doc.CheckPdfUA(), static x => x.Clause == "7.21.8");

        using PdfDocument visible = NotdefDocs.Editable(NotdefDocs.Build(hiddenOnPage: false));
        PdfUAViolation vv = Assert.Single(visible.CheckPdfUA(), static x => x.Clause == "7.21.8");
        Assert.DoesNotContain("none of that text", vv.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreparePdfUARemovesInvisibleNotdefTextOnlyWhenAsked()
    {
        using PdfDocument doc = NotdefDocs.Editable(NotdefDocs.Build());
        Assert.Contains(doc.PreparePdfUA(new PdfUAOptions { Language = "en" }), static x => x.Clause == "7.21.8");
        Assert.Equal(2, doc.FindNotdefText().Count);

        IReadOnlyList<PdfUAViolation> left = doc.PreparePdfUA(new PdfUAOptions { Language = "en", RemoveInvisibleNotdefText = true });
        PdfUAViolation still = Assert.Single(left, static x => x.Clause == "7.21.8");
        Assert.DoesNotContain("none of that text", still.Message, StringComparison.Ordinal);
        Assert.Single(doc.FindNotdefText());
    }

    [Fact]
    public void VeraAgreesBeforeAndAfter()
    {
        VeraPdf.Require();
        byte[] pdf = NotdefDocs.Build(visibleOnPage: false);
        Assert.True(VeraHasNotdef(pdf));
        using PdfDocument doc = NotdefDocs.Editable(pdf);
        doc.RemoveNotdefText();
        byte[] saved = NotdefDocs.Save(doc);
        Assert.False(VeraHasNotdef(saved));

        // Visible text is reported by veraPDF too, and is kept by default.
        byte[] both = NotdefDocs.Build();
        using PdfDocument d2 = NotdefDocs.Editable(both);
        d2.RemoveNotdefText();
        Assert.True(VeraHasNotdef(NotdefDocs.Save(d2)));
        d2.RemoveNotdefText(new PdfNotdefRemoveOptions { Scope = PdfNotdefScope.All });
        Assert.False(VeraHasNotdef(NotdefDocs.Save(d2)));
    }

    private static PdfArray NewArray(PdfReference r)
    {
        var a = new PdfArray();
        a.Append(r);
        return a;
    }

    private static string ExtractAll(PdfDocument doc) =>
        string.Join("\n", doc.ExtractText(new OnePdf.Text.PdfTextOptions { UseStructureTree = false }).Select(static p => p.Text));
}
