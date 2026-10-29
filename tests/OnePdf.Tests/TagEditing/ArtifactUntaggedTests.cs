using System.Text;
using OnePdf.Tests.PdfA;
using OnePdf.Tests.Tagging;
using static OnePdf.Tests.Tagging.TagDocs;

namespace OnePdf.Tests.TagEditing;

/// <summary>
/// <c>ArtifactUntaggedGraphics</c>: a tagged page that also paints paths, images, shadings, inline images and forms outside marked content.
/// After the call the PDF/UA check and veraPDF find no untagged non-text content, and the page paints and extracts as before.
/// </summary>
public class ArtifactUntaggedTests
{
    private const string Shading = "/Shading << /Sh1 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >>";

    private static bool Untagged(PdfUAViolation v) => v.Message.Contains("neither tagged", StringComparison.Ordinal);

    /// <summary>A tagged one-page document with the extra content appended after the tagged text.</summary>
    internal static byte[] TaggedWith(string extra)
    {
        byte[] plain = Make([new PageSpec(T(72, 700, "Hello world"))], b =>
        {
            b.Stream(100, "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", [0x80]);
            Form(b, 101, "0 0 50 50 re f");
        }, xobjects: "/Im1 100 0 R /Fm1 101 0 R", resourcesExtra: Shading);
        byte[] tagged;
        using (PdfDocument doc = TagSupport.Editable(plain))
        {
            doc.Tag(TagSupport.Extract(doc), static _ => PdfBlockRole.Paragraph, new PdfTagOptions { Title = "Test document", Language = "en" });
            tagged = TagSupport.Save(doc);
        }

        using PdfDocument again = TagSupport.Editable(tagged);
        PdfStream last = again.Pages[0].ContentStreams[^1];
        byte[] old = last.GetDecodedBytes().ToArray();
        last.SetData([.. old, .. Encoding.Latin1.GetBytes("\n" + extra + "\n")], compress: true);
        return TagSupport.Save(again);
    }

    private static string? VeraUntagged(byte[] pdf) =>
        VeraPdf.Path is null ? null : string.Join("; ", VeraPdf.Validate(pdf, "ua1").Where(static f => f.StartsWith("7.1-3", StringComparison.Ordinal)));

    [Theory]
    [InlineData("path", "50 50 100 100 re f", 1)]
    [InlineData("stroke", "50 50 m 100 100 l S", 1)]
    [InlineData("image", "q 50 0 0 50 100 100 cm /Im1 Do Q", 1)]
    [InlineData("inline", "q 20 0 0 20 100 100 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \u0080\nEI Q", 1)]
    [InlineData("shading", "q 0 0 200 200 re W n /Sh1 sh Q", 1)]
    [InlineData("form", "/Fm1 Do", 1)]
    [InlineData("form-also-tagged", "/Fm1 Do /P << /MCID 5 >> BDC /Fm1 Do EMC", 1)]
    [InlineData("nested", "q 1 0 0 1 5 5 cm BT /F1 12 Tf ET q 10 10 100 100 re S Q Q", 1)]
    [InlineData("clip", "20 20 50 50 re W n 10 10 80 80 re f", 1)]
    [InlineData("other-mc", "/Span BMC 0 0 10 10 re f EMC", 1)]
    [InlineData("in-artifact", "/Artifact BMC 0 0 5 5 re f EMC 1 1 9 9 re f", 1)]
    [InlineData("several", "0 0 5 5 re f 10 10 5 5 re f q /Fm1 Do Q", 3)]
    public void Wraps(string name, string extra, int expected)
    {
        byte[] source = TaggedWith(extra);
        if (Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR") is { Length: > 0 } rd)
        {
            File.WriteAllBytes(Path.Combine(rd, "artifact-source-" + name + ".pdf"), source);
        }

        using PdfDocument before = PdfDocument.Open(source);
        Assert.Contains(before.CheckPdfUA(), Untagged);
        if (VeraPdf.Path is not null)
        {
            Assert.NotEmpty(VeraUntagged(source)!);
        }

        using PdfDocument doc = TagSupport.Editable(source);
        string extract = TagSupport.ExtractionJson(doc);
        string display = TagSupport.DisplayText(doc);
        PdfArtifactResult r = doc.ArtifactUntaggedGraphics();
        Assert.Equal(expected, r.Wrapped);
        Assert.Equal(0, r.UntaggedTextOperators);
        Assert.Equal(0, r.FormsLeft);
        Assert.Equal(expected, r.Pages.Sum(static p => p.Wrapped));
        TagSupport.AssertSame(extract, TagSupport.ExtractionJson(doc));
        TagSupport.AssertSame(display, TagSupport.DisplayText(doc));

        byte[] saved = TagSupport.Save(doc);
        using PdfDocument after = PdfDocument.Open(saved);
        Assert.DoesNotContain(after.CheckPdfUA(), Untagged);
        Assert.True(string.IsNullOrEmpty(VeraUntagged(saved)), VeraUntagged(saved));
        TagSupport.AssertSame(extract, TagSupport.ExtractionJson(after));
        TagSupport.AssertSame(display, TagSupport.DisplayText(after));
        if (name != "form-also-tagged")
        {
            // (that case tags content by hand, which the tree does not list)
            AssertNoTagProblems(after);
        }

        // The PDF/UA save accepts it (the unembedded test fonts aside).
        using PdfDocument ua = TagSupport.Editable(saved);
        try
        {
            TagSupport.Save(ua, new PdfSaveOptions { PdfUA = new PdfUAOptions() });
        }
        catch (PdfUAException ex) when (ex.Violations.All(static v => v.Clause.StartsWith("7.21", StringComparison.Ordinal) || v.Message.StartsWith("Marked content with MCID 5", StringComparison.Ordinal)))
        {
        }

        // A second call has nothing left to do.
        using PdfDocument second = TagSupport.Editable(saved);
        Assert.Equal(0, second.ArtifactUntaggedGraphics().Wrapped);
    }

    [Fact]
    public void TaggedUseOfAFormIsLeftAsItWas()
    {
        using PdfDocument doc = TagSupport.Editable(TaggedWith("/Fm1 Do /P << /MCID 5 >> BDC /Fm1 Do EMC"));
        doc.ArtifactUntaggedGraphics();
        string content = PageContent(doc, 0);
        Assert.Contains("/P << /MCID 5 >> BDC /Fm1 Do EMC", content, StringComparison.Ordinal);
        Assert.Equal(1, Count(content, "/Artifact BMC"));
        PdfStream form = (PdfStream)doc.Pages[0].Resources.GetDictionary("XObject")!.Get("Fm1")!;
        Assert.Equal("0 0 50 50 re f", Encoding.Latin1.GetString(form.GetDecodedBytes().Span));
    }

    [Fact]
    public void TextIsReportedAndLeftAlone()
    {
        byte[] source = TaggedWith("BT /F1 12 Tf 50 50 Td (loose) Tj ET");
        using PdfDocument doc = TagSupport.Editable(source);
        string display = TagSupport.DisplayText(doc);
        PdfArtifactResult r = doc.ArtifactUntaggedGraphics();
        Assert.Equal(0, r.Wrapped);
        Assert.Equal(1, r.UntaggedTextOperators);
        Assert.Equal(0, r.StreamsChanged);
        PdfArtifactPageResult page = Assert.Single(r.Pages);
        Assert.Equal(0, page.PageIndex);
        Assert.Equal(1, page.UntaggedTextOperators);
        TagSupport.AssertSame(display, TagSupport.DisplayText(doc));
        Assert.Contains(doc.CheckPdfUA(), Untagged);
    }

    [Fact]
    public void GraphicsAreWrappedAndTextAroundThemIsReported()
    {
        using PdfDocument doc = TagSupport.Editable(TaggedWith("0 0 5 5 re f BT /F1 12 Tf 50 50 Td (loose) Tj ET"));
        PdfArtifactResult r = doc.ArtifactUntaggedGraphics();
        Assert.Equal(1, r.Wrapped);
        Assert.Equal(1, r.UntaggedTextOperators);
        Assert.Equal(1, r.StreamsChanged);
    }

    [Fact]
    public void FormsWithTextOrTagsOfTheirOwnAreLeft()
    {
        byte[] plain = Make([new PageSpec("/FmT Do /FmM Do")], b =>
        {
            Form(b, 101, "BT /F1 12 Tf 10 10 Td (in form) Tj ET");
            Form(b, 102, "0 0 5 5 re f /P << /MCID 0 >> BDC 0 0 9 9 re f EMC");
        }, xobjects: "/FmT 101 0 R /FmM 102 0 R");
        using PdfDocument doc = TagSupport.Editable(plain);
        PdfArtifactResult r = doc.ArtifactUntaggedGraphics();
        Assert.Equal(0, r.Wrapped);
        Assert.Equal(2, r.FormsLeft);
    }

    [Fact]
    public void ReadOnlyDocumentIsRefused()
    {
        using PdfDocument doc = PdfDocument.Open(TaggedWith("0 0 5 5 re f"));
        Assert.Throws<InvalidOperationException>(() => doc.ArtifactUntaggedGraphics());
    }

    [Fact]
    public void CommittedTaggedFilesKeepTheirLookAndLoseTheirFindings()
    {
        string dir = RealWorldDir();
        int tagged = 0, withGraphics = 0, cleared = 0, findings = 0, remaining = 0;
        var report = new StringBuilder();
        foreach (string file in Directory.EnumerateFiles(dir, "*.pdf").Order(StringComparer.Ordinal))
        {
            byte[] bytes = File.ReadAllBytes(file);
            try
            {
                using PdfDocument doc = TagSupport.Editable(bytes);
                if (doc.StructureTreeRoot is null)
                {
                    continue;
                }

                tagged++;
                int before = doc.CheckPdfUA().Count(Untagged);
                string display = TagSupport.DisplayText(doc);
                string extract = TagSupport.ExtractionJson(doc);
                PdfArtifactResult r = doc.ArtifactUntaggedGraphics();
                int after = doc.CheckPdfUA().Count(Untagged);
                TagSupport.AssertSame(display, TagSupport.DisplayText(doc));
                TagSupport.AssertSame(extract, TagSupport.ExtractionJson(doc));
                Assert.True(after == 0 || r.UntaggedTextOperators > 0 || r.FormsLeft > 0, Path.GetFileName(file) + ": untagged content is left that is not text");
                if (r.Wrapped > 0)
                {
                    withGraphics++;
                }

                findings += before;
                remaining += after;
                if (before > 0 && after == 0)
                {
                    cleared++;
                }

                report.Append(Path.GetFileName(file)).Append(": wrapped ").Append(r.Wrapped).Append(", text ").Append(r.UntaggedTextOperators)
                    .Append(", forms left ").Append(r.FormsLeft).Append(", findings ").Append(before).Append(" -> ").Append(after).Append('\n');
                if (Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR") is { Length: > 0 } rd && r.Wrapped > 0)
                {
                    if (VeraPdf.Path is not null)
                    {
                        report.Append("  veraPDF 7.1-3: ").Append(VeraPdf.Validate(bytes, "ua1").Count(static f => f.StartsWith("7.1-3", StringComparison.Ordinal)))
                            .Append(" -> ").Append(VeraPdf.Validate(TagSupport.Save(doc), "ua1").Count(static f => f.StartsWith("7.1-3", StringComparison.Ordinal))).Append('\n');
                    }

                    File.WriteAllBytes(Path.Combine(rd, "artifacted-" + Path.GetFileName(file)), TagSupport.Save(doc));
                }
            }
            catch (PdfException)
            {
                // a file the library cannot edit is not this test's concern
            }
        }

        string summary = $"tagged {tagged}, with untagged graphics {withGraphics}, files cleared {cleared}, findings {findings} -> {remaining}";
        if (Environment.GetEnvironmentVariable("ONEPDF_REPORT_DIR") is { Length: > 0 } od)
        {
            File.WriteAllText(Path.Combine(od, "artifact-realworld.txt"), summary + "\n" + report);
        }

        Assert.True(tagged > 0, summary);
    }

    private static string RealWorldDir([System.Runtime.CompilerServices.CallerFilePath] string source = "")
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
}
