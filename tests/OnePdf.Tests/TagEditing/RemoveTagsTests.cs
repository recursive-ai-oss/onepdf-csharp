using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OnePdf.Tests.PdfA;
using OnePdf.Tests.Tagging;
using OnePdf.Text;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.TagEditing;

/// <summary>
/// <c>RemoveTags</c>: documents tagged by every route in the library, committed real-world files that are tagged, half-tagged files, and
/// forms and annotations. After the call nothing a reader sees changes, no tag is left, and the file can be tagged again.
/// </summary>
public class RemoveTagsTests
{
    private static string RealWorld([CallerFilePath] string source = "")
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

    private static bool FontClause(string clause) => clause.StartsWith("7.21", StringComparison.Ordinal);

    /// <summary>Removes the tags and checks that the document reads and draws the same; then it checks that a second call finds nothing.</summary>
    internal static PdfRemoveTagsResult RemoveAndCompare(PdfDocument doc, PdfRemoveTagsOptions? options = null)
    {
        string extractBefore = TagSupport.ExtractionJson(doc);
        string displayBefore = TagSupport.DisplayText(doc);
        PdfRemoveTagsResult r = doc.RemoveTags(options);
        TagSupport.AssertSame(extractBefore, TagSupport.ExtractionJson(doc));
        TagSupport.AssertSame(displayBefore, TagSupport.DisplayText(doc));
        Assert.Null(doc.Catalog.GetDictionary("StructTreeRoot"));
        Assert.Null(doc.Catalog.GetDictionary("MarkInfo"));

        byte[] saved = TagSupport.Save(doc);
        using PdfDocument again = TagSupport.Editable(saved);
        TagSupport.AssertSame(extractBefore, TagSupport.ExtractionJson(again));
        TagSupport.AssertSame(displayBefore, TagSupport.DisplayText(again));
        PdfRemoveTagsResult second = again.RemoveTags(options);
        Assert.Equal(0, second.SequencesRemoved);
        Assert.Equal(0, second.StructParentsRemoved);
        Assert.Equal(0, second.StreamsChanged);
        Assert.False(second.StructureTreeRemoved);
        return r;
    }

    /// <summary>Tags every block as a paragraph (the weakest tree that is a tree) and returns the saved bytes.</summary>
    private static byte[] TagAllParagraphs(PdfDocument doc, bool pdfUa, out bool fontsOnly)
    {
        doc.Tag(TagSupport.Extract(doc), static _ => PdfBlockRole.Paragraph, new PdfTagOptions { Title = "Test document", Language = "en" });
        fontsOnly = false;
        if (!pdfUa)
        {
            return TagSupport.Save(doc);
        }

        try
        {
            return TagSupport.Save(doc, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        }
        catch (PdfUAException ex) when (ex.Violations.All(static v => SourceClause(v.Clause)))
        {
            fontsOnly = true;
            return TagSupport.Save(doc);
        }
    }

    // Findings about the source file and not about the tags: fonts that are not embedded (7.21), an optional-content configuration without a
    // name (7.10), and link annotations that cover no text, which the paragraph-only roles used here leave untagged (7.18).
    // report-09's page 32 has a damaged content stream in the source file (operators run into text: Tj10.0000f); the text-based tools
    // read it in different ways, so 7.1-3 on that page is a finding about the source, listed by name in the test that uses it.
    private static bool SourceClause(string clause) => clause.StartsWith("7.21", StringComparison.Ordinal) || clause.StartsWith("7.10", StringComparison.Ordinal) || clause.StartsWith("7.18", StringComparison.Ordinal);

    // ---- documents tagged by ApplyTagging, through the roles files ----

    public static TheoryData<string> Names() => TaggingSamples.TaggingSampleRolesTests.SampleNames();

    [Theory]
    [MemberData(nameof(Names))]
    public void TaggedSampleLosesItsTagsAndCanBeTaggedAgain(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(TaggingSamples.TaggingSampleRolesTests.Dir(), file)));
        string path = Path.GetFullPath(Path.Combine(TaggingSamples.TaggingSampleRolesTests.Dir(), json.RootElement.GetProperty("source").GetString()!));
        byte[] original = File.ReadAllBytes(path);

        using PdfDocument plain = TagSupport.Editable(original);
        string extractPlain = TagSupport.ExtractionJson(plain);
        string displayPlain = TagSupport.DisplayText(plain);

        // Tag it with the sample's roles, as a file the library tagged itself.
        byte[] tagged;
        using (PdfDocument doc = TagSupport.Editable(original))
        {
            IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
            PdfTaggingPlan plan = doc.CreateTaggingPlan();
            Tagging.TaggingSampleAcceptanceTests.Build(doc, pages, json.RootElement, plan);
            doc.ApplyTagging(plan);
            tagged = TagSupport.Save(doc);
        }

        using PdfDocument t = TagSupport.Editable(tagged);
        Assert.NotNull(t.StructureTreeRoot);
        PdfRemoveTagsResult r = RemoveAndCompare(t);
        Assert.True(r.StructureTreeRemoved);
        Assert.True(r.MarkInfoRemoved);
        Assert.True(r.SequencesRemoved > 0);
        Assert.True(r.StructParentsRemoved > 0);

        // The same as the original, not only as the tagged file.
        TagSupport.AssertSame(extractPlain, TagSupport.ExtractionJson(t));
        TagSupport.AssertSame(displayPlain, TagSupport.DisplayText(t));

        // Tagged again with the sample's own roles: the flow a remediation run uses.
        IReadOnlyList<PdfPageText> pages2 = TagSupport.Extract(t);
        PdfTaggingPlan plan2 = t.CreateTaggingPlan();
        Tagging.TaggingSampleAcceptanceTests.Build(t, pages2, json.RootElement, plan2);
        t.ApplyTagging(plan2);

        bool fontsOnly = false;
        byte[] retagged;
        try
        {
            retagged = TagSupport.Save(t, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        }
        catch (PdfUAException ex) when (ex.Violations.All(static v => FontClause(v.Clause)))
        {
            fontsOnly = true;
            retagged = TagSupport.Save(t);
        }

        using PdfDocument after = PdfDocument.Open(retagged);
        IReadOnlyList<PdfUAViolation> check = after.CheckPdfUA();
        Assert.True(check.All(v => fontsOnly && FontClause(v.Clause)), $"{file}: {string.Join(" | ", check.Select(static v => v.ToString()))}");
        TagSupport.AssertSame(extractPlain, TagSupport.ExtractionJson(after));
        TagSupport.AssertSame(displayPlain, TagSupport.DisplayText(after));
        if (VeraPdf.Path is not null)
        {
            List<string> failures = VeraPdf.Validate(retagged, "ua1");
            Assert.True(failures.All(f => fontsOnly && FontClause(f)), $"{file}: veraPDF ua1: {string.Join("; ", failures.Take(6))}");
        }
    }

    // ---- committed real-world files that are tagged ----

    [Fact]
    public void TaggedRealWorldFilesLoseTheirTagsAndCanBeTaggedAgain()
    {
        var report = new StringBuilder();
        var failures = new List<string>();
        int tagged = 0, retagged = 0, vera = 0, veraClean = 0, veraFontOnly = 0;
        foreach (string file in Directory.GetFiles(RealWorld(), "*.pdf").Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            byte[] bytes = File.ReadAllBytes(file);
            try
            {
                using PdfDocument doc = TagSupport.Editable(bytes);
                bool treed = doc.StructureTreeRoot is not null || doc.Catalog.GetDictionary("MarkInfo") is not null;
                if (!treed)
                {
                    continue;
                }

                tagged++;
                PdfRemoveTagsResult r = RemoveAndCompare(doc);
                if (Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR") is { Length: > 0 } rdir)
                {
                    File.WriteAllBytes(Path.Combine(rdir, "removed-" + name), TagSupport.Save(doc));
                }

                report.Append(name).Append(": removed ").Append(r.SequencesRemoved).Append(" sequences, ").Append(r.StructParentsRemoved).Append(" struct parents, ").Append(r.StreamsChanged).Append(" streams, ").Append(r.WarningList.Count).Append(" warnings");

                byte[] again = TagAllParagraphs(doc, pdfUa: true, out bool fontsOnly);
                retagged++;
                if (Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR") is { Length: > 0 } od)
                {
                    File.WriteAllBytes(Path.Combine(od, "retagged-" + name), again);
                }

                report.Append("; tagged again");
                if (VeraPdf.Path is not null)
                {
                    vera++;
                    List<string> vf = VeraPdf.Validate(again, "ua1");
                    if (vf.Count == 0)
                    {
                        veraClean++;
                        report.Append("; ua1 clean");
                    }
                    else if (vf.All(x => SourceClause(x) || name.StartsWith("report-09-", StringComparison.Ordinal) && x.StartsWith("7.1-3", StringComparison.Ordinal)))
                    {
                        veraFontOnly++;
                        report.Append("; ua1 source-only: ").Append(string.Join(",", vf.Select(static x => x.Split(':')[0]).Distinct()));
                    }
                    else
                    {
                        failures.Add($"{name}: veraPDF ua1 clauses: {string.Join(", ", vf.Select(static x => x.Split(':')[0]).Distinct())}; first: {vf.First(static x => !SourceClause(x))}");
                    }
                }

                report.Append('\n');
            }
            catch (Exception ex) when (ex is Xunit.Sdk.XunitException or PdfException)
            {
                failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        string summary = $"tagged files: {tagged}, tagged again: {retagged}, veraPDF: {vera} (clean {veraClean}, source-file findings only {veraFontOnly})";
        if (Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR") is { Length: > 0 } rd)
        {
            Directory.CreateDirectory(rd);
            File.WriteAllText(Path.Combine(rd, "removetags-realworld.txt"), summary + "\n" + report);
        }

        Assert.True(tagged >= 10, summary);
        Assert.True(failures.Count == 0, summary + "\n" + string.Join("\n", failures));
    }

    // ---- half-tagged arrivals ----

    private static readonly string Two = T(72, 700, "First block text") + T(72, 500, "Second block text");

    [Fact]
    public void McidsWithoutATreeAreRemoved()
    {
        byte[] src = Make("/P <</MCID 0>> BDC\n" + T(72, 700, "First block text") + "EMC\n/P <</MCID 1>> BDC\n" + T(72, 500, "Second block text") + "EMC\n");
        using PdfDocument doc = TagSupport.Editable(src);
        PdfRemoveTagsResult r = RemoveAndCompare(doc);
        Assert.Equal(2, r.SequencesRemoved);
        Assert.False(r.StructureTreeRemoved);
        TagAllParagraphs(doc, pdfUa: false, out _);
    }

    [Fact]
    public void ATreeWithoutMarkInfoIsRemoved()
    {
        byte[] tagged = Tag(Make(Two), (d, p, plan) =>
        {
            plan.Add(PdfTagRole.P, PdfTagContent.Block(p[0], 0));
            plan.Add(PdfTagRole.P, PdfTagContent.Block(p[0], 1));
        }, out _);
        using PdfDocument doc = TagSupport.Editable(tagged);
        doc.Catalog.RemoveEntry("MarkInfo");
        PdfRemoveTagsResult r = RemoveAndCompare(doc);
        Assert.True(r.StructureTreeRemoved);
        Assert.False(r.MarkInfoRemoved);
        TagAllParagraphs(doc, pdfUa: false, out _);
    }

    [Fact]
    public void MarkInfoAloneIsRemoved()
    {
        using PdfDocument doc = TagSupport.Editable(Make([new PageSpec(Two)], catalogExtra: "/MarkInfo << /Marked true >>"));
        PdfRemoveTagsResult r = RemoveAndCompare(doc);
        Assert.True(r.MarkInfoRemoved);
        TagAllParagraphs(doc, pdfUa: false, out _);
    }

    // ---- what is kept, and the options ----

    private static readonly string Mixed =
        "/Artifact <</Type /Pagination /Subtype /Header>> BDC\n" + T(72, 760, "Header words") + "EMC\n" +
        "/Span BMC\n" + T(72, 700, "Plain marked") + "EMC\n" +
        "/OC /MC0 BDC\n" + T(72, 650, "Optional words") + "EMC\n" +
        "/P <</MCID 0>> BDC\n/Span <</MCID 1>> BDC\n" + T(72, 600, "Nested words") + "EMC\nEMC\n" +
        "/Span <</MCID 2 /ActualText (Actual)>> BDC\n" + T(72, 550, "Replaced") + "EMC\n";

    private static byte[] MixedDoc() => Make([new PageSpec(Mixed)], resourcesExtra: "/Properties << /MC0 << /Type /OCG /Name (Layer) >> /MC1 << /MCID 7 >> >>");

    private static string ContentOf(PdfDocument doc) => Encoding.Latin1.GetString(doc.Pages[0].ContentStreams.SelectMany(static s => s.GetDecodedBytes().ToArray()).ToArray());

    [Fact]
    public void DefaultKeepsArtifactOpticalContentAndPlainMarks()
    {
        using PdfDocument doc = TagSupport.Editable(MixedDoc());
        PdfRemoveTagsResult r = RemoveAndCompare(doc);
        Assert.Equal(3, r.SequencesRemoved);
        Assert.Equal(1, r.ActualTextKept);
        string c = ContentOf(doc);
        Assert.DoesNotContain("MCID", c, StringComparison.Ordinal);
        Assert.Contains("/Artifact", c, StringComparison.Ordinal);
        Assert.Contains("/Span BMC", c, StringComparison.Ordinal);
        Assert.Contains("/OC /MC0 BDC", c, StringComparison.Ordinal);
        Assert.Contains("/ActualText <", c, StringComparison.Ordinal);
        Assert.Equal(CountOf(c, "BDC") + CountOf(c, "BMC"), CountOf(c, "EMC"));
    }

    [Fact]
    public void ConvertToBmcKeepsTheTags()
    {
        using PdfDocument doc = TagSupport.Editable(MixedDoc());
        PdfRemoveTagsResult r = RemoveAndCompare(doc, new PdfRemoveTagsOptions { MarkedContent = PdfMarkedContentRemoval.ConvertToBmc });
        Assert.Equal(3, r.SequencesRemoved);
        string c = ContentOf(doc);
        Assert.DoesNotContain("MCID", c, StringComparison.Ordinal);
        Assert.Contains("/P BMC", c, StringComparison.Ordinal);
        Assert.Equal(CountOf(c, "BDC") + CountOf(c, "BMC"), CountOf(c, "EMC"));
    }

    [Fact]
    public void ArtifactMarksAreRemovedOnRequest()
    {
        using PdfDocument doc = TagSupport.Editable(MixedDoc());
        PdfRemoveTagsResult r = RemoveAndCompare(doc, new PdfRemoveTagsOptions { RemoveArtifactMarks = true });
        Assert.Equal(1, r.ArtifactSequencesRemoved);
        string c = ContentOf(doc);
        Assert.DoesNotContain("/Artifact", c, StringComparison.Ordinal);
        Assert.Equal(CountOf(c, "BDC") + CountOf(c, "BMC"), CountOf(c, "EMC"));
    }

    [Fact]
    public void UnbalancedMarksDoNotBreakTheContent()
    {
        byte[] src = Make("EMC\n/P <</MCID 0>> BDC\n" + Two + "/P <</MCID 1>> BDC\n" + T(72, 300, "Never closed"));
        using PdfDocument doc = TagSupport.Editable(src);
        PdfRemoveTagsResult r = doc.RemoveTags();
        Assert.Equal(2, r.SequencesRemoved);
        string c = ContentOf(doc);
        Assert.DoesNotContain("MCID", c, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(c, "EMC"));
    }

    [Fact]
    public void InlineImageAndStringsDoNotConfuseTheScan()
    {
        string content = "/P <</MCID 0>> BDC\nq 10 0 0 10 100 100 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0001 EMC /P <</MCID 9>> BDC EI Q\n(MCID BDC EMC) Tj\n" + Two + "EMC\n";
        byte[] src = Make(content.Replace("(MCID BDC EMC) Tj\n", "BT /F1 12 Tf 72 400 Td (MCID BDC EMC) Tj ET\n", StringComparison.Ordinal));
        using PdfDocument doc = TagSupport.Editable(src);
        PdfRemoveTagsResult r = doc.RemoveTags();
        Assert.Equal(1, r.SequencesRemoved);
        string c = ContentOf(doc);
        Assert.Contains("(MCID BDC EMC) Tj", c, StringComparison.Ordinal);
        Assert.Contains("ID \u0001 EMC /P <</MCID 9>> BDC EI", c, StringComparison.Ordinal);
    }

    private static int CountOf(string s, string word)
    {
        int n = 0;
        for (int i = s.IndexOf(word, StringComparison.Ordinal); i >= 0; i = s.IndexOf(word, i + 1, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    // ---- forms and annotations ----

    [Fact]
    public void SharedAndNestedFormsLoseTheirMarks()
    {
        // One form drawn on two pages, which draws a second form; both with marked content and their own /StructParents.
        string inner = "/Span <</MCID 0>> BDC\n" + T(72, 300, "Inner form text") + "EMC\n";
        string outer = "/P <</MCID 0>> BDC\n" + T(72, 400, "Outer form text") + "EMC\n/I Do\n";
        byte[] src = Make([new PageSpec("/Fm0 Do\n" + T(72, 700, "Page one text")), new PageSpec("/Fm0 Do\n" + T(72, 700, "Page two text"))],
            b =>
            {
                Form(b, 100, outer, "/StructParents 5", "/I 101 0 R");
                Form(b, 101, inner, "/StructParents 6");
            }, xobjects: "/Fm0 100 0 R");
        using PdfDocument doc = TagSupport.Editable(src);
        PdfRemoveTagsResult r = RemoveAndCompare(doc);
        Assert.Equal(2, r.SequencesRemoved);
        Assert.Equal(2, r.StructParentsRemoved);
        TagAllParagraphs(doc, pdfUa: false, out _);
    }

    [Fact]
    public void TaggedFormUsedOnTwoPagesIsUntaggedOnceAndTaggable()
    {
        string form = T(72, 400, "Shared form text");
        byte[] plain = Make([new PageSpec("/Fm0 Do\n" + T(72, 700, "Page one text")), new PageSpec("/Fm0 Do\n" + T(72, 700, "Page two text"))],
            b => Form(b, 100, form), xobjects: "/Fm0 100 0 R");
        byte[] tagged = Tag(plain, (d, p, plan) =>
        {
            foreach (PdfPageText pg in p)
            {
                foreach (PdfTextBlock bl in pg.Blocks)
                {
                    plan.Add(PdfTagRole.P, PdfTagContent.Block(pg, bl));
                }
            }
        }, out PdfTaggingResult tr);
        using PdfDocument doc = TagSupport.Editable(tagged);
        PdfRemoveTagsResult r = RemoveAndCompare(doc);
        Assert.True(r.SequencesRemoved >= 3, $"{r.SequencesRemoved} sequences; tagging wrote {tr.ContentSequences}");
        TagAllParagraphs(doc, pdfUa: false, out _);
    }

    [Fact]
    public void AnnotationAppearancesAndTheirStructParentLoseTheirMarks()
    {
        string ap = "/Span <</MCID 0>> BDC\nBT /F1 10 Tf 2 2 Td (Link words) Tj ET\nEMC\n";
        byte[] src = Make([new PageSpec(Two, Entries: "/Annots [110 0 R] /StructParents 0")], b =>
        {
            b.Obj(110, "<< /Type /Annot /Subtype /Link /Rect [72 300 200 320] /Border [0 0 0] /StructParent 1 /AP << /N 111 0 R >> /Contents (Link) >>");
            b.Stream(111, "/Type /XObject /Subtype /Form /BBox [0 0 128 20] /Resources << /Font << /F1 3 0 R >> >>", Encoding.Latin1.GetBytes(ap));
        });
        using PdfDocument doc = TagSupport.Editable(src);
        PdfRemoveTagsResult r = RemoveAndCompare(doc);
        Assert.Equal(1, r.SequencesRemoved);
        Assert.Equal(2, r.StructParentsRemoved);
        Assert.False(doc.Pages[0].Dictionary.ContainsKey("StructParents"));
        Assert.False(doc.Pages[0].Annotations[0].Dictionary.ContainsKey("StructParent"));
    }

    [Fact]
    public void ContentSplitAcrossStreamsIsHandledAsOne()
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [10 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        b.Obj(10, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents [50 0 R 51 0 R] /Resources << /Font << /F1 3 0 R >> >> >>");
        b.Stream(50, string.Empty, "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Split) Tj"u8.ToArray());
        b.Stream(51, string.Empty, " ET\nEMC\n"u8.ToArray());
        long x = b.XrefTable(60);
        b.Trailer("/Size 60 /Root 1 0 R", x);
        using PdfDocument doc = TagSupport.Editable(b.ToArray());
        PdfRemoveTagsResult r = doc.RemoveTags();
        Assert.Equal(1, r.SequencesRemoved);
        Assert.DoesNotContain("MCID", ContentOf(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void WriterTaggedDocumentLosesItsTagsAndCanBeTaggedAgain()
    {
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true }))
        {
            PdfCreatedFont f = w.AddStandardFont("Helvetica");
            PdfStructureElement doc = w.BeginStructureElement("Document");
            PdfStructureElement table = w.BeginStructureElement(doc, "Table");
            PdfStructureElement row = w.BeginStructureElement(table, "TR");
            PdfStructureElement left = w.BeginStructureElement(row, "TD");
            PdfStructureElement right = w.BeginStructureElement(row, "TD");
            for (int page = 0; page < 2; page++)
            {
                PdfStreamPageBuilder p = w.BeginPage(200, 200);
                p.SetFont(f, 10);
                p.BeginTaggedContent(left);
                p.DrawText("left cell " + page, 10, 150);
                p.EndMarkedContent();
                p.BeginTaggedContent(right);
                p.DrawText("right cell " + page, 10, 100);
                p.EndMarkedContent();
                p.Commit();
            }

            w.EndStructureElement(left);
            w.EndStructureElement(right);
            w.EndStructureElement(row);
            w.EndStructureElement(table);
            w.EndStructureElement(doc);
            w.Finish();
        }

        using PdfDocument d = TagSupport.Editable(ms.ToArray());
        Assert.NotNull(d.StructureTreeRoot);
        PdfRemoveTagsResult r = RemoveAndCompare(d);
        Assert.Equal(4, r.SequencesRemoved);
        Assert.True(r.StructureTreeRemoved);
        TagAllParagraphs(d, pdfUa: false, out _);
    }

    [Fact]
    public void RequiresAnEditableDocument()
    {
        using PdfDocument doc = PdfDocument.Open(Make(Two));
        Assert.Throws<InvalidOperationException>(() => doc.RemoveTags());
    }

    [Fact]
    public void CancelledCallLeavesTheDocumentAsItWas()
    {
        byte[] src = Make("/P <</MCID 0>> BDC\n" + Two + "EMC\n");
        using PdfDocument doc = TagSupport.Editable(src);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => doc.RemoveTags(null, cts.Token));
        Assert.Contains("MCID", ContentOf(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalSaveKeepsTheOriginalRevision()
    {
        byte[] tagged = Tag(Make(Two), (d, p, plan) => plan.Add(PdfTagRole.P, PdfTagContent.Block(p[0], 0)), out _);
        using PdfDocument doc = TagSupport.Editable(tagged);
        doc.RemoveTags();
        byte[] saved = TagSupport.SaveIncremental(doc);
        Assert.True(saved.Length > tagged.Length);
        Assert.True(saved.AsSpan(0, tagged.Length).SequenceEqual(tagged));
        using PdfDocument after = TagSupport.Editable(saved);
        Assert.Null(after.StructureTreeRoot);
        Assert.Equal(0, after.RemoveTags().StreamsChanged);
    }
}
