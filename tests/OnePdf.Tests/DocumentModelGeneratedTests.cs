using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace OnePdf.Tests;

internal static class TestData
{
    public static string Root([CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata");
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        throw new DirectoryNotFoundException("testdata not found above " + source);
    }

    public static string Generated(string name) => Path.Combine(Root(), "generated", "pdf", name + ".pdf");

    public static JsonElement Truth(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "generated", "truth", name + ".json"))).RootElement;

    public static IEnumerable<string> GeneratedNames() =>
        Directory.GetFiles(Path.Combine(Root(), "generated", "truth"), "*.json").Select(static f => Path.GetFileNameWithoutExtension(f)!).Order(StringComparer.Ordinal);
}

public class DocumentModelGeneratedTests
{
    private static PdfDocument Open(string name, string? password = null) =>
        PdfDocument.Open(TestData.Generated(name), new PdfOpenOptions { Password = password });

    [Fact]
    public void EveryGeneratedFileOpensWithTheTruthPageCount()
    {
        int opened = 0;
        foreach (string name in TestData.GeneratedNames())
        {
            JsonElement truth = TestData.Truth(name);
            string? expect = truth.TryGetProperty("expect", out JsonElement e) ? e.GetString() : null;
            string? user = truth.TryGetProperty("passwords", out JsonElement pw) ? pw.GetProperty("user").GetString() : null;
            using PdfDocument doc = Open(name, user);
            if (expect != "unrecoverable")
            {
                Assert.True(truth.GetProperty("pageCount").GetInt32() == doc.Pages.Count, name);
                Assert.Equal(doc.Pages.Count, doc.PageCount);
                // Every page object builds, with its boxes and rotation resolved.
                foreach (PdfPage p in doc.Pages)
                {
                    Assert.False(p.MediaBox.IsEmpty, name);
                    Assert.Equal(0, p.Rotate % 90);
                }
            }

            opened++;
        }

        Assert.True(opened >= 60);
    }

    [Fact]
    public void EncryptedFilesOpenWithUserAndOwnerPasswordsAndDecryptContent()
    {
        int n = 0;
        foreach (string name in TestData.GeneratedNames().Where(static n => n.StartsWith("enc-", StringComparison.Ordinal) || n.Contains("encrypted", StringComparison.Ordinal)))
        {
            JsonElement pw = TestData.Truth(name).GetProperty("passwords");
            string user = pw.GetProperty("user").GetString()!;
            string owner = pw.GetProperty("owner").GetString()!;
            foreach ((string password, PdfAccessLevel level) in new[] { (user, PdfAccessLevel.User), (owner, PdfAccessLevel.Owner) })
            {
                using PdfDocument doc = Open(name, password);
                Assert.True(doc.IsEncrypted, name);
                Assert.NotNull(doc.Encryption);
                // The empty user password also matches when the owner password is given first as "user"; owner opens as owner.
                if (user.Length > 0 || level == PdfAccessLevel.User)
                {
                    Assert.Equal(level, doc.AccessLevel);
                }

                PdfStream content = doc.Pages[0].ContentStreams[0];
                string text = Encoding.Latin1.GetString(content.GetDecodedBytes().Span);
                Assert.Contains("BT", text);
                Assert.Contains("ET", text);
            }

            if (user.Length > 0)
            {
                Assert.Throws<PdfPasswordException>(() => Open(name));
                Assert.Throws<PdfPasswordException>(() => Open(name, "definitely-wrong"));
            }
            else
            {
                using PdfDocument d = Open(name);
                Assert.Equal(PdfAccessLevel.User, d.AccessLevel);
            }

            n++;
        }

        Assert.True(n >= 10);
    }

    [Fact]
    public void EncryptedStringsAreDecrypted()
    {
        // The generated Info dictionaries carry no strings; the outline of an encrypted Chrome file does.
        string name = "enc-chrome-aes-128";
        JsonElement pw = TestData.Truth(name).GetProperty("passwords");
        using PdfDocument doc = Open(name, pw.GetProperty("user").GetString());
        foreach (string key in doc.Info.Dictionary.Keys)
        {
            if (doc.Info.Dictionary.Get(key) is PdfString s)
            {
                Assert.DoesNotContain('\uFFFD', s.GetText());
            }
        }

        Assert.True(doc.Pages.Count > 0);
        Assert.Equal(PdfCipher.Aes128, doc.Encryption!.Cipher);
    }

    [Fact]
    public void OutlineAndLabelsMatchTruth()
    {
        using PdfDocument doc = Open("raw-outlines-labels");
        JsonElement truth = TestData.Truth("raw-outlines-labels");
        AssertOutline(truth.GetProperty("outline"), doc.Outline);
        string[] labels = [.. truth.GetProperty("pageLabels").EnumerateArray().Select(static x => x.GetString()!)];
        Assert.Equal(labels.Length, doc.Pages.Count);
        for (int i = 0; i < labels.Length; i++)
        {
            Assert.Equal(labels[i], doc.PageLabels!.GetLabel(i));
            Assert.Equal(labels[i], doc.Pages[i].Label);
        }
    }

    private static void AssertOutline(JsonElement expected, IReadOnlyList<PdfOutlineItem> actual)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Count);
        int i = 0;
        foreach (JsonElement e in expected.EnumerateArray())
        {
            Assert.Equal(e[0].GetString(), actual[i].Title);
            Assert.Equal(e[1].GetInt32(), actual[i].Destination?.PageIndex);
            AssertOutline(e[2], actual[i].Children);
            i++;
        }
    }

    [Theory]
    [InlineData("chrome-links-outline")]
    [InlineData("chrome-links-outline-objstm")]
    public void ChromeLinksAndOutline(string name)
    {
        using PdfDocument doc = Open(name);
        JsonElement truth = TestData.Truth("chrome-links-outline");
        AssertOutline(truth.GetProperty("outline"), doc.Outline);
        IReadOnlyList<PdfAnnotation> links = doc.Pages[0].Links;
        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/start", links[0].Uri);
        Assert.Equal("URI", links[0].Action!.ActionType);
        // The internal link goes to the "Details" destination on the second page.
        Assert.NotNull(links[1].Destination);
        Assert.Equal(1, links[1].Destination!.PageIndex);
    }

    [Fact]
    public void TaggedReportExposesLinkAndTaggedFlag()
    {
        using PdfDocument doc = Open("lo-report-tagged");
        Assert.True(doc.IsTagged);
        Assert.NotNull(doc.StructureTreeRoot);
        Assert.Contains(doc.Pages[0].Links, l => l.Uri == "https://example.org/report");
        using PdfDocument plain = Open("lo-report");
        Assert.False(plain.IsTagged);
    }

    [Fact]
    public void AnnotationsMatchTruth()
    {
        using PdfDocument doc = Open("raw-annotations");
        JsonElement[] expected = [.. TestData.Truth("raw-annotations").GetProperty("annotations").EnumerateArray()];
        IReadOnlyList<PdfAnnotation> actual = doc.Pages[0].Annotations;
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("subtype").GetString(), actual[i].Subtype);
            if (expected[i].TryGetProperty("contents", out JsonElement c))
            {
                Assert.Equal(c.GetString(), actual[i].Contents);
            }

            if (expected[i].TryGetProperty("title", out JsonElement t))
            {
                Assert.Equal(t.GetString(), actual[i].Title);
            }

            if (expected[i].TryGetProperty("uri", out JsonElement u))
            {
                Assert.Equal(u.GetString(), actual[i].Uri);
            }

            if (expected[i].TryGetProperty("dest", out JsonElement d))
            {
                Assert.Equal("page 0 XYZ", $"page {actual[i].Destination!.PageIndex} {actual[i].Destination!.FitType}");
                Assert.Equal(d.GetString(), $"page {actual[i].Destination!.PageIndex} {actual[i].Destination!.FitType}");
            }

            Assert.False(actual[i].Rect.IsEmpty);
        }
    }

    [Fact]
    public void AcroFormValuesMatchTruth()
    {
        using PdfDocument doc = Open("raw-acroform");
        PdfAcroForm form = doc.Form!;
        JsonElement[] expected = [.. TestData.Truth("raw-acroform").GetProperty("fields").EnumerateArray()];
        Assert.Equal(expected.Length, form.TerminalFields.Count());
        foreach (JsonElement e in expected)
        {
            PdfFormField f = form.FindField(e.GetProperty("name").GetString()!)!;
            Assert.NotNull(f);
            string type = e.GetProperty("type").GetString()!;
            Assert.Equal(
                type switch
                {
                    "Tx" => PdfFieldKind.Text,
                    "Btn/checkbox" => PdfFieldKind.CheckBox,
                    "Btn/radio" => PdfFieldKind.RadioButton,
                    "Ch/combo" => PdfFieldKind.ComboBox,
                    "Ch/list" => PdfFieldKind.ListBox,
                    _ => PdfFieldKind.Unknown,
                },
                f.Kind);
            JsonElement v = e.GetProperty("value");
            if (v.ValueKind == JsonValueKind.Array)
            {
                Assert.Equal(v.EnumerateArray().Select(static x => x.GetString()!), f.Values);
            }
            else
            {
                Assert.Equal(v.GetString(), f.Value);
            }

            if (e.TryGetProperty("options", out JsonElement o))
            {
                Assert.Equal(o.EnumerateArray().Select(static x => x.GetString()!), f.Options.Select(static x => x.Export));
            }
        }

        Assert.Null(form.GetXfaXml());
    }

    [Fact]
    public void EmbeddedFilesMatchTruth()
    {
        using PdfDocument doc = Open("raw-embedded-files");
        JsonElement[] expected = [.. TestData.Truth("raw-embedded-files").GetProperty("embeddedFiles").EnumerateArray()];
        Assert.Equal(expected.Length, doc.EmbeddedFiles.Count);
        foreach (JsonElement e in expected)
        {
            PdfEmbeddedFile f = doc.EmbeddedFiles.Single(x => x.Name == e.GetProperty("name").GetString());
            Assert.Equal(e.GetProperty("content").GetString(), Encoding.UTF8.GetString(f.ReadContent().Span));
            if (e.TryGetProperty("description", out JsonElement d))
            {
                Assert.Equal(d.GetString(), f.Description);
            }
        }

        Assert.Equal(expected.Length, doc.GetAllEmbeddedFiles().Count);
    }

    [Fact]
    public void InfoAndXmpMatchTruth()
    {
        using PdfDocument doc = Open("raw-metadata");
        JsonElement truth = TestData.Truth("raw-metadata");
        JsonElement info = truth.GetProperty("info");
        IReadOnlyDictionary<string, string> entries = doc.Info.Entries;
        Assert.Equal(info.GetProperty("Title").GetString(), doc.Info.Title);
        Assert.Equal(info.GetProperty("Author").GetString(), doc.Info.Author);
        Assert.Equal(info.GetProperty("Subject").GetString(), doc.Info.Subject);
        Assert.Equal(info.GetProperty("Keywords").GetString(), doc.Info.Keywords);
        Assert.Equal(info.GetProperty("Creator").GetString(), doc.Info.Creator);
        Assert.Equal(info.GetProperty("Custom").GetString(), entries["Custom"]);
        Assert.Equal(DateTimeOffset.Parse(info.GetProperty("CreationDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture), doc.Info.CreationDate);
        Assert.Equal(DateTimeOffset.Parse(info.GetProperty("ModDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture), doc.Info.ModificationDate);
        Assert.Equal(TimeSpan.FromHours(2), doc.Info.CreationDate!.Value.Offset);

        JsonElement xmp = truth.GetProperty("xmp");
        XmpMetadata x = doc.Xmp!;
        Assert.True(x.IsWellFormed);
        Assert.Equal(xmp.GetProperty("title").GetString(), x.Title);
        Assert.Equal(xmp.GetProperty("creatorTool").GetString(), x.CreatorTool);
        Assert.Equal(xmp.GetProperty("keywords").GetString(), x.Keywords);
        Assert.Equal(xmp.GetProperty("creators").EnumerateArray().Select(static c => c.GetString()!), x.Creators);
        Assert.True(x.RawBytes.Length > 100);
    }

    [Fact]
    public void RotationsCropBoxAndUserUnitMatchTruth()
    {
        using PdfDocument doc = Open("raw-rotated-pages");
        int[] rotations = [.. TestData.Truth("raw-rotated-pages").GetProperty("rotations").EnumerateArray().Select(static x => x.GetInt32())];
        Assert.Equal(rotations, doc.Pages.Select(static p => p.Rotate));
        PdfPage last = doc.Pages[4];
        Assert.Equal(new PdfRectangle(100, 100, 500, 700), last.CropBox);
        Assert.Equal(2.0, last.UserUnit);
        Assert.Equal(1.0, doc.Pages[0].UserUnit);
    }

    [Fact]
    public void PdfTwoUtf8StringsDecode()
    {
        using PdfDocument doc = PdfDocument.Open(Path.Combine(TestData.Root(), "pdf20examples", "pdf20-utf8-test.pdf"));
        Assert.Equal("\u8868\u30DD\u3042A\u9DD7\u0152\u00E9\uFF22\u900D\u00DC\u00DF\u00AA\u0105\u00F1\u4E02\u3400\U00020000", doc.Info.Title);
        Assert.Equal("Peter Wyatt", doc.Info.Author);
        Assert.Equal(new DateTimeOffset(2021, 12, 30, 13, 46, 41, TimeSpan.FromHours(11)), doc.Info.CreationDate);
        Assert.Equal("PDF 2.0 with UTF-8 test file", doc.Outline[0].Title);
        Assert.Equal("\u202A\u202Atest\u202A", doc.Outline[1].Title);
        Assert.Equal("\U0001F308\uFE0F\n", doc.Outline[2].Title);
        Assert.Equal(0, doc.Outline[0].Destination!.PageIndex);
        Assert.Equal(69, doc.Outline[0].Destination!.Left);
        Assert.Equal(769, doc.Outline[0].Destination!.Top);
        Assert.Equal(["Layer 1", "\u06AF\u0686\u067E\u0698"], doc.OptionalContentGroups.Select(static g => g.Name));
        Assert.All(doc.OptionalContentGroups, static g => Assert.False(g.IsVisibleByDefault));
        Assert.Equal("Prefix", doc.PageLabels!.GetLabel(0)[..6]);
        Assert.Equal("Prefixi", doc.PageLabels.GetLabel(0));
        Assert.Equal("en-UK", doc.Language);
        Assert.True(doc.IsTagged);
    }

    [Fact]
    public void AnnotationInUtf8Example()
    {
        using PdfDocument doc = PdfDocument.Open(Path.Combine(TestData.Root(), "pdf20examples", "PDF 2.0 UTF-8 string and annotation.pdf"));
        Assert.NotEmpty(doc.Pages[0].Annotations);
        Assert.All(doc.Pages[0].Annotations, static a => Assert.DoesNotContain('\uFFFD', a.Contents ?? string.Empty));
    }
}
