using System.Diagnostics;
using OnePdf.Tests.PdfA;
using OnePdf.Text;

namespace OnePdf.Tests.Creation;

public class StreamingWriterTests
{
    private static byte[] Serif => CreationSupport.RequireFont("LiberationSerif-Regular.ttf");

    private static readonly byte[] Id = new byte[16];

    private static byte[] Png(int w, int h, int colorType)
    {
        var spec = new Images.PngKit.Spec(w, h, colorType, 8);
        return Images.PngKit.Encode(spec, Images.PngKit.RandomRows(spec, 3));
    }

    private static string Pdftotext(string path)
    {
        if (!File.Exists("/usr/bin/pdftotext"))
        {
            return "";
        }

        var psi = new ProcessStartInfo("/usr/bin/pdftotext", ["-enc", "UTF-8", path, "-"]) { RedirectStandardOutput = true };
        using Process p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    // ---- an ordinary streamed document opens everywhere ----

    [Fact]
    public void StreamedDocumentOpensInOurReaderPdfPigAndQpdf()
    {
        var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = Id, LeaveOpen = true, Language = "en" }))
        {
            PdfCreatedFont font = w.AddFont(Serif);
            PdfCreatedFont helv = w.AddStandardFont("Helvetica");
            PdfCreatedImage png = w.AddPngImage(Png(8, 8, 2));
            PdfCreatedImage rgba = w.AddPngImage(Png(8, 8, 6));
            w.SetInfo("Title", "Streamed");
            PdfFormContentBuilder fb = w.BeginForm(new PdfRectangle(0, 0, 100, 20));
            fb.SetFont(helv, 10);
            fb.DrawText("Stamp", 2, 5);
            PdfFormXObject stamp = fb.Commit();
            PdfFormXObject total = w.ReserveForm(new PdfRectangle(0, 0, 40, 14));

            for (int i = 0; i < 130; i++)
            {
                PdfStreamPageBuilder p = w.BeginPage(300, 200);
                p.SetFont(font, 12);
                p.DrawText($"Page {i + 1} of ", 20, 150);
                p.Save();
                p.Translate(80, 150);
                p.DrawForm(total);
                p.Restore();
                p.DrawImage(png, 20, 20, 40, 40);
                p.DrawImage(rgba, 80, 20, 40, 40);
                p.Save();
                p.Translate(150, 20);
                p.DrawForm(stamp);
                p.Restore();
                if (i % 40 == 0)
                {
                    w.AddBookmark($"Chapter {i / 40}", 0, i, 190);
                    w.AddBookmark($"Section {i / 40}.1", 1, i, 170);
                    w.AddNamedDestination($"d{i}", i, 190);
                }

                p.AddLinkToDestination(new PdfRectangle(20, 140, 100, 160), "d80", description: "Go to page 81");
                p.AddLink(new PdfRectangle(20, 100, 100, 120), "https://example.com/é");
                p.Commit();
            }

            PdfFormContentBuilder tf = w.BeginForm(total);
            tf.SetFont(font, 12);
            tf.DrawText(w.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, 2);
            tf.Commit();
            Assert.Equal(130, w.PageCount);
            w.Finish();
        }

        byte[] pdf = ms.ToArray();
        using PdfDocument d = PdfDocument.Open(pdf);
        Assert.Equal(130, d.Pages.Count);
        Assert.DoesNotContain(d.Diagnostics, x => x.Severity >= PdfSeverity.Warning);
        Assert.Equal("Streamed", d.Info.Title);
        Assert.Equal(["Chapter 0", "Chapter 1", "Chapter 2", "Chapter 3"], d.Outline.Select(o => o.Title).ToArray());
        Assert.Equal("Section 2.1", d.Outline[2].Children[0].Title);
        Assert.Equal(80, d.Outline[2].Destination!.PageIndex);
        Assert.Equal(2, d.Pages[5].Links.Count);
        Assert.Equal("https://example.com/%C3%A9", d.Pages[5].Links[1].Uri);
        Assert.Equal("Page 77 of 130", CreationSupport.Normalize(d.Pages[76].ExtractText().Text).Replace("Stamp", "").Trim());

        if (CreationSupport.HasQpdf)
        {
            (int code, string output) = CreationSupport.Qpdf(pdf);
            Assert.True(code == 0, output);
        }

        using UglyToad.PdfPig.PdfDocument pig = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(130, pig.NumberOfPages);
        Assert.Contains("Page 1 of", pig.GetPage(1).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassicXrefTableOutputIsValidToo()
    {
        var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { XrefFormat = PdfXrefFormat.Table, LeaveOpen = true }))
        {
            PdfCreatedFont f = w.AddStandardFont("Times-Roman");
            for (int i = 0; i < 3; i++)
            {
                PdfStreamPageBuilder p = w.BeginPage(200, 200);
                p.SetFont(f, 12);
                p.DrawText("Hello " + i, 10, 100);
                p.Commit();
            }

            w.Finish();
        }

        byte[] pdf = ms.ToArray();
        Assert.Contains("\nxref\n", System.Text.Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        using PdfDocument d = PdfDocument.Open(pdf);
        Assert.Equal(3, d.Pages.Count);
        Assert.DoesNotContain(d.Diagnostics, x => x.Severity >= PdfSeverity.Warning);
        if (CreationSupport.HasQpdf)
        {
            Assert.Equal(0, CreationSupport.Qpdf(pdf).Code);
        }
    }

    [Fact]
    public void SameCallsGiveSameBytes()
    {
        byte[] Make()
        {
            var ms = new MemoryStream();
            using var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = Id, LeaveOpen = true });
            PdfCreatedFont f = w.AddFont(Serif);
            PdfStreamPageBuilder p = w.BeginPage(200, 200);
            p.SetFont(f, 12);
            p.DrawText("Deterministic", 10, 100);
            p.Commit();
            w.Finish();
            return ms.ToArray();
        }

        Assert.Equal(Make(), Make());
    }

    [Fact]
    public void MisuseIsRefused()
    {
        using var w = new PdfDocumentWriter(new MemoryStream());
        PdfStreamPageBuilder p = w.BeginPage(100, 100);
        Assert.Throws<InvalidOperationException>(() => w.BeginPage(100, 100)); // one page open at a time
        Assert.Throws<InvalidOperationException>(() => w.Finish());
        Assert.Throws<ArgumentOutOfRangeException>(() => w.AddBookmark("x", 0, 5, 0)); // no such page
        Assert.Throws<ArgumentOutOfRangeException>(() => w.AddBookmark("x", 1, 0, 0)); // level jumps
        p.Commit();
        Assert.ThrowsAny<InvalidOperationException>(p.Commit);
        PdfFormXObject reserved = w.ReserveForm(new PdfRectangle(0, 0, 10, 10));
        Assert.Throws<InvalidOperationException>(() => w.Finish()); // reserved form never filled
        w.BeginForm(reserved).Commit();
        Assert.Throws<ArgumentException>(() => w.BeginForm(reserved)); // fills once
        w.BeginStructureElement("Document");
        Assert.Throws<InvalidOperationException>(() => w.Finish()); // element never ended
        w.EndStructureElement();
        Assert.Throws<InvalidOperationException>(w.EndStructureElement);

        // A page dropped without Commit is forgotten; a page with structure bound to it must not be.
        PdfStreamPageBuilder dropped = w.BeginPage(100, 100);
        dropped.Dispose();
        PdfStreamPageBuilder next = w.BeginPage(100, 100);
        next.Commit();
        PdfStructureElement e = w.BeginStructureElement("P");
        PdfStreamPageBuilder tagged = w.BeginPage(100, 100);
        tagged.BeginTaggedContent(e);
        tagged.Dispose();
        Assert.Throws<InvalidOperationException>(() => w.BeginPage(100, 100));
    }

    // ---- 10,000 pages in bounded memory ----

    [Fact]
    public void TenThousandTaggedPagesStreamInBoundedMemory()
    {
        const int Pages = 10_000;
        string path = Path.Combine(Path.GetTempPath(), "onepdf-stream-" + Guid.NewGuid().ToString("N") + ".pdf");
        byte[] font = Serif;
        long managedBefore;
        long managedAfter;
        long peakWorkingSet;
        try
        {
            GC.Collect();
            managedBefore = GC.GetTotalMemory(true);
            long maxManaged = 0;
            using (FileStream fs = File.Create(path))
            using (var w = new PdfDocumentWriter(fs, new PdfDocumentWriterOptions { Language = "en" }))
            {
                PdfCreatedFont f = w.AddFont(font);
                PdfFormXObject total = w.ReserveForm(new PdfRectangle(0, 0, 50, 14));
                PdfStructureElement doc = w.BeginStructureElement("Document");
                for (int i = 0; i < Pages; i++)
                {
                    PdfStreamPageBuilder p = w.BeginPage(595, 842);
                    p.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Footer);
                    p.SetFont(f, 9);
                    p.DrawText($"Page {i + 1} of ", 250, 30);
                    p.Save();
                    p.Translate(300, 30);
                    p.DrawForm(total);
                    p.Restore();
                    p.EndMarkedContent();
                    for (int para = 0; para < 6; para++)
                    {
                        PdfStructureElement e = w.BeginStructureElement("P");
                        p.BeginTaggedContent(e);
                        p.SetFont(f, 11);
                        p.DrawText($"Paragraph {para} of page {i} with some words to fill the line a little more.", 50, 780 - (para * 30));
                        p.EndMarkedContent();
                        w.EndStructureElement();
                    }

                    p.AddLink(new PdfRectangle(50, 700, 150, 720), "https://example.com/" + i);
                    p.Commit();
                    if (i % 500 == 0)
                    {
                        maxManaged = Math.Max(maxManaged, GC.GetTotalMemory(false));
                    }
                }

                w.EndStructureElement();
                GC.Collect();
                managedAfter = GC.GetTotalMemory(true);
                PdfFormContentBuilder tf = w.BeginForm(total);
                tf.SetFont(f, 9);
                tf.DrawText(Pages.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, 2);
                tf.Commit();
                w.Finish();
                _ = doc;
                peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;
                TestContext.Current.SendDiagnosticMessage($"10,000 pages: file {fs.Length / 1048576.0:F1} MiB; managed heap before {managedBefore / 1048576.0:F1} MiB, after the last page {managedAfter / 1048576.0:F1} MiB, sampled max {maxManaged / 1048576.0:F1} MiB; process peak working set {peakWorkingSet / 1048576.0:F1} MiB");
            }

            // Target: 64 MiB plus about 1 KiB per page, measured as live managed memory after the last page.
            Assert.True(managedAfter - managedBefore <= (64L << 20) + (Pages * 1024L), $"live heap grew by {(managedAfter - managedBefore) / 1048576.0:F1} MiB");

            using PdfDocument d = PdfDocument.Open(path);
            Assert.Equal(Pages, d.Pages.Count);
            Assert.DoesNotContain(d.Diagnostics, x => x.Severity >= PdfSeverity.Warning);
            Assert.Contains("Paragraph 3 of page 9999", d.Pages[9999].ExtractText().Text, StringComparison.Ordinal);
            string last = CreationSupport.Normalize(d.Pages[9999].ExtractText().Text);
            Assert.Contains("Page 10000 of", last, StringComparison.Ordinal);
            Assert.Contains("10000", last[(last.IndexOf("Page 10000 of", StringComparison.Ordinal) + 13)..], StringComparison.Ordinal);
            if (CreationSupport.HasQpdf)
            {
                var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ONEPDF_QPDF")!, ["--check", path]) { RedirectStandardOutput = true, RedirectStandardError = true };
                using Process q = Process.Start(psi)!;
                string o = q.StandardOutput.ReadToEnd() + q.StandardError.ReadToEnd();
                q.WaitForExit();
                Assert.True(q.ExitCode == 0, o);
            }

            using UglyToad.PdfPig.PdfDocument pig = UglyToad.PdfPig.PdfDocument.Open(path);
            Assert.Equal(Pages, pig.NumberOfPages);
            Assert.Contains("Paragraph 5 of page 1234", pig.GetPage(1235).Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- tagged output ----

    private static byte[] BuildTagged(bool tableFirstInContentOrder = true)
    {
        var ms = new MemoryStream();
        using var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = Id, LeaveOpen = true, Language = "en-US", DisplayDocumentTitle = true });
        w.SetInfo("Title", "Tagged sample");
        w.MapRole("Callout", "Note");
        PdfCreatedFont f = w.AddFont(Serif);
        PdfCreatedImage img = w.AddPngImage(Png(10, 10, 2));

        PdfStructureElement document = w.BeginStructureElement("Document");
        PdfStreamPageBuilder p = w.BeginPage(300, 400);
        p.SetFont(f, 12);

        // Paragraph 2 sits above paragraph 1 on the page, so only the structure tree gives the right order.
        PdfStructureElement h = w.BeginStructureElement("H1");
        p.BeginTaggedContent(h);
        p.DrawText("Heading", 20, 380);
        p.EndMarkedContent();
        w.EndStructureElement();

        PdfStructureElement p1 = w.BeginStructureElement("P");
        p.BeginTaggedContent(p1);
        p.DrawText("First paragraph", 20, 200);
        p.EndMarkedContent();
        w.EndStructureElement();

        PdfStructureElement p2 = w.BeginStructureElement("P");
        p.BeginTaggedContent(p2);
        p.DrawText("Second paragraph", 20, 300);
        p.EndMarkedContent();
        w.EndStructureElement();

        // A table: header row and one data row.
        PdfStructureElement table = w.BeginStructureElement("Table");
        PdfStructureElement tr1 = w.BeginStructureElement("TR");
        foreach ((string text, double x) in new[] { ("Name", 20.0), ("Qty", 120.0) })
        {
            PdfStructureElement th = w.BeginStructureElement("TH", new PdfStructureElementOptions { Attributes = PdfStructureAttributes.TableHeader("Column") });
            p.BeginTaggedContent(th);
            p.DrawText(text, x, 150);
            p.EndMarkedContent();
            w.EndStructureElement();
        }

        w.EndStructureElement();
        PdfStructureElement tr2 = w.BeginStructureElement("TR");
        foreach ((string text, double x) in new[] { ("Apples", 20.0), ("12", 120.0) })
        {
            PdfStructureElement td = w.BeginStructureElement("TD");
            p.BeginTaggedContent(td);
            p.DrawText(text, x, 130);
            p.EndMarkedContent();
            w.EndStructureElement();
        }

        w.EndStructureElement();
        w.EndStructureElement(); // Table
        _ = table;
        _ = tr1;
        _ = tr2;

        // A list.
        PdfStructureElement list = w.BeginStructureElement("L", new PdfStructureElementOptions { Attributes = PdfStructureAttributes.List("Decimal") });
        for (int i = 1; i <= 2; i++)
        {
            PdfStructureElement li = w.BeginStructureElement("LI");
            PdfStructureElement lbl = w.BeginStructureElement("Lbl");
            p.BeginTaggedContent(lbl);
            p.DrawText(i + ".", 20, 100 - (i * 14));
            p.EndMarkedContent();
            w.EndStructureElement();
            PdfStructureElement body = w.BeginStructureElement("LBody");
            p.BeginTaggedContent(body);
            p.DrawText("Item " + i, 40, 100 - (i * 14));
            p.EndMarkedContent();
            w.EndStructureElement();
            w.EndStructureElement();
        }

        w.EndStructureElement();
        _ = list;

        // A figure with alternate text, a custom type, a link, and artifacts.
        PdfStructureElement fig = w.BeginStructureElement("Figure", new PdfStructureElementOptions { AlternateText = "A small test picture" });
        p.BeginTaggedContent(fig);
        p.DrawImage(img, 200, 20, 40, 40);
        p.EndMarkedContent();
        w.EndStructureElement();

        PdfStructureElement note = w.BeginStructureElement("Callout");
        p.BeginTaggedContent(note, "Callout");
        p.DrawText("Note", 200, 90);
        p.EndMarkedContent();
        w.EndStructureElement();

        PdfStructureElement link = w.BeginStructureElement("Link");
        p.BeginTaggedContent(link);
        p.DrawText("example.com", 20, 40);
        p.EndMarkedContent();
        p.AddLink(new PdfRectangle(20, 36, 90, 50), "https://example.com", link, "Visit example.com");
        w.EndStructureElement();

        p.BeginArtifact(PdfArtifactType.Layout);
        p.SetStrokeGray(0.5);
        p.MoveTo(20, 370);
        p.LineTo(280, 370);
        p.Stroke();
        p.EndMarkedContent();
        p.BeginArtifact(PdfArtifactType.Pagination, PdfArtifactSubtype.Footer);
        p.DrawText("1", 150, 10);
        p.EndMarkedContent();
        p.Commit();

        // Second page continues the same table-less document with a paragraph that belongs to the first section.
        PdfStreamPageBuilder p2b = w.BeginPage(300, 400);
        p2b.SetFont(f, 12);
        PdfStructureElement p3 = w.BeginStructureElement("P");
        p2b.BeginTaggedContent(p3);
        p2b.DrawText("Third paragraph on page two", 20, 350);
        p2b.EndMarkedContent();
        p2b.BeginTaggedContent(p3); // one element, two content items on a page
        p2b.DrawText("continued", 20, 330);
        p2b.EndMarkedContent();
        w.EndStructureElement();
        p2b.Commit();

        w.EndStructureElement(); // Document
        _ = document;
        w.Finish();
        return ms.ToArray();
    }

    [Fact]
    public void TaggedDocumentReadsBackInStructureOrder()
    {
        byte[] pdf = BuildTagged();
        using PdfDocument d = PdfDocument.Open(pdf);
        Assert.DoesNotContain(d.Diagnostics, x => x.Severity >= PdfSeverity.Warning);
        PdfPageText page = d.Pages[0].ExtractText();
        Assert.True(page.UsedStructureTree);
        string text = CreationSupport.Normalize(page.Text);
        int Idx(string s) => text.IndexOf(s, StringComparison.Ordinal);
        Assert.True(Idx("Heading") >= 0 && Idx("Heading") < Idx("First paragraph"), text);
        Assert.True(Idx("First paragraph") < Idx("Second paragraph"), text); // geometry alone would put "Second" first
        Assert.True(Idx("Second paragraph") < Idx("Name"), text);
        Assert.True(Idx("Name") < Idx("Qty") && Idx("Qty") < Idx("Apples") && Idx("Apples") < Idx("12"), text);
        Assert.True(Idx("12") < Idx("1. Item 1") || Idx("1.") > Idx("12"), text);
        Assert.True(Idx("Item 1") < Idx("Item 2") && Idx("Item 2") < Idx("Note") && Idx("Note") < Idx("example.com"), text);

        // The tree itself.
        PdfDictionary root = d.StructureTreeRoot!;
        Assert.Equal("StructTreeRoot", root.GetName("Type"));
        Assert.True(d.Catalog!.GetDictionary("MarkInfo")!.GetBool("Marked"));
        Assert.Equal("Note", root.GetDictionary("RoleMap")!.GetName("Callout"));
        Assert.True(root.GetInt("ParentTreeNextKey") >= 3);
        PdfDictionary documentElement = root.GetDictionary("K")!;
        Assert.Equal("Document", documentElement.GetName("S"));
        Assert.Equal(root.Reference!.Number, ((PdfReference)documentElement.GetRaw("P")!).Number);

        // ParentTree: page 0's array lists the parent element of each MCID in order.
        long key = d.Pages[0].Dictionary.GetInt("StructParents", -1);
        Assert.True(key >= 0);
        var pairs = d.ReadTree(root.GetDictionary("ParentTree"), true, "parent tree").ToDictionary(kv => ((PdfInteger)kv.Key).Value, kv => kv.Value);
        var array = (PdfArray)d.Resolve(pairs[key])!;
        PdfDictionary firstParent = (PdfDictionary)array[0]!;
        Assert.Equal("H1", firstParent.GetName("S"));
        Assert.Equal("P", ((PdfDictionary)array[1]!).GetName("S"));

        // The link: annotation has StructParent mapping back to the Link element, and the Link element holds an OBJR to the annotation.
        PdfAnnotation a = d.Pages[0].Links.Single();
        long ak = a.Dictionary.GetInt("StructParent", -1);
        PdfDictionary linkElement = (PdfDictionary)d.Resolve(pairs[ak])!;
        Assert.Equal("Link", linkElement.GetName("S"));
        PdfArray kids = linkElement.GetArray("K")!;
        Assert.Equal(2, kids.Count);
        Assert.Equal("OBJR", ((PdfDictionary)kids[1]!).GetName("Type"));
        Assert.Equal("Visit example.com", a.Contents);

        // Figure alternate text and table header scope.
        string raw = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Scope/Column", raw, StringComparison.Ordinal);
        Assert.Contains("/Alt", raw, StringComparison.Ordinal);
        Assert.Equal("Visit example.com", a.Contents);
        Assert.Equal("Third paragraph on page two continued", CreationSupport.Normalize(d.Pages[1].ExtractText().Text));
    }

    [Fact]
    public void TreesWeWroteSurviveImportAndRedaction()
    {
        byte[] pdf = BuildTagged();
        using PdfDocument src = PdfDocument.Open(pdf);
        using PdfDocument dst = PdfDocument.CreateEmpty();
        dst.ImportPages(src, [0, 1]);
        byte[] out1 = CreationSupport.Save(dst);
        using (PdfDocument back = PdfDocument.Open(out1))
        {
            Assert.DoesNotContain(back.Diagnostics, x => x.Severity >= PdfSeverity.Warning);
            PdfPageText t = back.Pages[0].ExtractText();
            Assert.True(t.UsedStructureTree);
            string text = CreationSupport.Normalize(t.Text);
            Assert.True(text.IndexOf("First paragraph", StringComparison.Ordinal) < text.IndexOf("Second paragraph", StringComparison.Ordinal), text);
            Assert.Equal("Third paragraph on page two continued", CreationSupport.Normalize(back.Pages[1].ExtractText().Text));
        }

        // Importing only page two prunes the tree to that page.
        using PdfDocument dst2 = PdfDocument.CreateEmpty();
        dst2.ImportPages(src, [1]);
        using (PdfDocument back2 = PdfDocument.Open(CreationSupport.Save(dst2)))
        {
            Assert.Equal("Third paragraph on page two continued", CreationSupport.Normalize(back2.Pages[0].ExtractText().Text));
        }

        // Redaction of the second paragraph removes its text and leaves the tree readable.
        using PdfDocument red = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
        red.Redact([new PdfRedactionRegion(0, new PdfRectangle(10, 290, 200, 320))]);
        byte[] redacted = CreationSupport.Save(red);
        using PdfDocument rb = PdfDocument.Open(redacted);
        string rt = CreationSupport.Normalize(rb.Pages[0].ExtractText().Text);
        Assert.DoesNotContain("Second paragraph", rt, StringComparison.Ordinal);
        Assert.Contains("First paragraph", rt, StringComparison.Ordinal);
    }

    [Fact]
    public void TaggedOutputPassesVeraPdfStructureRulesInPdfA2b()
    {
        VeraPdf.Require();
        byte[] pdf = BuildTagged();
        using PdfDocument d = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });
        using var ms = new MemoryStream();
        d.Save(ms, new PdfSaveOptions { DocumentId = Id, PdfA = new PdfAOptions { Part = PdfAPart.Part2, Conformance = PdfAConformance.B } });
        List<string> failures = VeraPdf.Validate(ms.ToArray(), "2b");
        TestContext.Current.SendDiagnosticMessage("veraPDF 2b failures: " + (failures.Count == 0 ? "none" : string.Join("; ", failures)));
        Assert.DoesNotContain(failures, f => f.Contains("6.7", StringComparison.Ordinal));
        Assert.Empty(failures);

        // Information only: what the PDF/UA-1 profile says about the same content.
        List<string> ua = VeraPdf.Validate(ms.ToArray(), "ua1");
        TestContext.Current.SendDiagnosticMessage("veraPDF ua1 failures: " + (ua.Count == 0 ? "none" : string.Join("; ", ua)));
    }
}
