using System.Text;

namespace OnePdf.Tests;

public class DocumentModelStructureTests
{
    // Catalog 1, root Pages 2, then caller-supplied objects; a classic table and trailer close the file.
    private static byte[] Build(Action<PdfBuilder> objects, int size, string catalogExtra = "", string pagesEntry = "/Pages 2 0 R")
    {
        var b = new PdfBuilder();
        b.Obj(1, $"<< /Type /Catalog {pagesEntry} {catalogExtra} >>");
        objects(b);
        long x = b.XrefTable(size);
        b.Trailer($"/Size {size} /Root 1 0 R", x);
        return b.ToArray();
    }

    // n pages as objects 3..n+2 under a flat root.
    private static byte[] FlatPages(int n, string catalogExtra, Action<PdfBuilder>? more = null, int extraObjects = 0)
    {
        return Build(
            b =>
            {
                b.Obj(2, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(3, n).Select(static i => $"{i} 0 R"))}] /Count {n} /MediaBox [0 0 100 100] >>");
                for (int i = 0; i < n; i++)
                {
                    b.Obj(3 + i, "<< /Type /Page /Parent 2 0 R >>");
                }

                more?.Invoke(b);
            },
            3 + n + extraObjects,
            catalogExtra);
    }

    [Fact]
    public void PageLabelsRomanDecimalPrefixLettersAndStart()
    {
        byte[] data = FlatPages(14, "/PageLabels 100 0 R", b => b.Obj(
            100,
            "<< /Nums [0 << /S /r >> 4 << /S /D /P (A-) /St 5 >> 8 << /P (Appendix) >> 10 << /S /A >> 12 << /S /a /St 27 >>] >>"), 98);
        // Object numbers are sparse: the table lists the free ones as free.
        using PdfDocument doc = PdfDocument.Open(data);
        string[] labels = [.. Enumerable.Range(0, 14).Select(i => doc.Pages[i].Label!)];
        Assert.Equal(["i", "ii", "iii", "iv", "A-5", "A-6", "A-7", "A-8", "Appendix", "Appendix", "A", "B", "aa", "bb"], labels);
        Assert.Equal(PdfPageLabelStyle.UpperLetters, doc.PageLabels!.Ranges[3].Style);
    }

    [Fact]
    public void PageLabelsLargeRomanAndKidsTree()
    {
        byte[] data = FlatPages(3, "/PageLabels 100 0 R", b =>
        {
            b.Obj(100, "<< /Kids [101 0 R 102 0 R] >>");
            b.Obj(101, "<< /Nums [0 << /S /R /St 1994 >>] /Limits [0 0] >>");
            b.Obj(102, "<< /Nums [2 << /S /D /P (p) >>] /Limits [2 2] >>");
        }, 99);
        using PdfDocument doc = PdfDocument.Open(data);
        Assert.Equal(["MCMXCIV", "MCMXCV", "p1"], doc.Pages.Select(static p => p.Label!));
    }

    [Fact]
    public void NoPageLabelsMeansNullLabel()
    {
        using PdfDocument doc = PdfDocument.Open(PdfBuilder.Simple());
        Assert.Null(doc.PageLabels);
        Assert.Null(doc.Pages[0].Label);
    }

    [Fact]
    public void InheritedAttributesIncludingRotateOnIntermediateNode()
    {
        byte[] data = Build(
            b =>
            {
                b.Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 3 /Rotate 90 /MediaBox [0 0 300 400] /Resources << /Font << /F1 9 0 R >> >> >>");
                b.Obj(3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 /Rotate 180 >>");
                b.Obj(4, "<< /Type /Page /Parent 3 0 R >>");
                b.Obj(5, "<< /Type /Page /Parent 3 0 R /Rotate 270 /MediaBox [10 10 50 60] /CropBox [0 0 1000 1000] /Resources << >> >>");
                b.Obj(6, "<< /Type /Page /Parent 2 0 R /CropBox [20 30 120 230] /BleedBox [1 2 3 4] >>");
                b.Obj(9, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            },
            10);
        using PdfDocument doc = PdfDocument.Open(data);
        Assert.Equal(3, doc.Pages.Count);
        PdfPage a = doc.Pages[0], b2 = doc.Pages[1], c = doc.Pages[2];
        Assert.Equal(180, a.Rotate);
        Assert.Equal(270, b2.Rotate);
        Assert.Equal(90, c.Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 300, 400), a.MediaBox);
        Assert.Equal(a.MediaBox, a.CropBox);
        Assert.Equal(new PdfRectangle(10, 10, 50, 60), b2.MediaBox);
        Assert.Equal(b2.MediaBox, b2.CropBox); // the CropBox is clipped to the MediaBox
        Assert.Equal(new PdfRectangle(20, 30, 120, 230), c.CropBox);
        Assert.Equal(new PdfRectangle(1, 2, 3, 4), c.BleedBox);
        Assert.Equal(c.CropBox, c.TrimBox);
        Assert.NotNull(a.Resources.GetDictionary("Font"));
        Assert.Equal(0, b2.Resources.Count); // its own empty resources override the inherited ones
        Assert.NotNull(c.Resources.GetDictionary("Font"));
        Assert.Equal(1, doc.Pages.IndexOfObject(5));
        Assert.Equal(-1, doc.Pages.IndexOfObject(3));
        Assert.Same(doc.Pages[1], doc.Pages[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Pages[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Pages[-1]);
    }

    [Fact]
    public void OddRotateAndMissingMediaBoxAreRepaired()
    {
        byte[] data = Build(
            b =>
            {
                b.Obj(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
                b.Obj(3, "<< /Type /Page /Rotate -90 /MediaBox [200 300 0 0] /UserUnit -2 >>");
                b.Obj(4, "<< /Type /Page /Rotate 45 >>");
            },
            5);
        using PdfDocument doc = PdfDocument.Open(data);
        Assert.Equal(270, doc.Pages[0].Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 200, 300), doc.Pages[0].MediaBox);
        Assert.Equal(1.0, doc.Pages[0].UserUnit);
        Assert.Equal(0, doc.Pages[1].Rotate);
        Assert.Equal(new PdfRectangle(0, 0, 612, 792), doc.Pages[1].MediaBox);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("45", StringComparison.Ordinal));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("MediaBox", StringComparison.Ordinal));
    }

    [Fact]
    public void CyclicPageTreeAndNameTreeTerminate()
    {
        byte[] data = Build(
            b =>
            {
                b.Obj(2, "<< /Type /Pages /Kids [3 0 R 2 0 R] /Count 1 >>");
                b.Obj(3, "<< /Type /Page /MediaBox [0 0 10 10] >>");
                b.Obj(4, "<< /Kids [4 0 R 5 0 R] /Names [(a) 6 0 R] >>");
                b.Obj(5, "<< /Names [(b) 6 0 R 7 0 R (bad)] /Kids [4 0 R] >>");
                b.Obj(6, "<< /F (x.txt) >>");
            },
            8,
            "/Names << /EmbeddedFiles 4 0 R >>");
        using PdfDocument doc = PdfDocument.Open(data);
        Assert.Single(doc.Pages);
        Assert.Equal(2, doc.EmbeddedFiles.Count);
        Assert.Equal("x.txt", doc.EmbeddedFiles[0].Name);
        Assert.False(doc.EmbeddedFiles[0].HasContent);
        Assert.Empty(doc.EmbeddedFiles[0].ReadContent().ToArray());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("embedded files", StringComparison.Ordinal));
    }

    [Fact]
    public void OutlineCyclesAreCutAndNamedDestinationsResolve()
    {
        byte[] data = FlatPages(
            2,
            "/Outlines 10 0 R /Dests 14 0 R /Names << /Dests 15 0 R >>",
            b =>
            {
                b.Obj(10, "<< /Type /Outlines /First 11 0 R /Last 12 0 R /Count 3 >>");
                b.Obj(11, "<< /Title (One) /Parent 10 0 R /Next 12 0 R /First 13 0 R /Last 13 0 R /Count 1 /Dest [4 0 R /Fit] /F 3 /C [1 0 0] >>");
                b.Obj(12, "<< /Title (Two) /Parent 10 0 R /Prev 11 0 R /Next 11 0 R /Dest (nm) >>");
                b.Obj(13, "<< /Title (Child) /Parent 11 0 R /Next 11 0 R /A << /S /GoTo /D /old >> >>");
                b.Obj(14, "<< /old [3 0 R /FitH 700] >>");
                b.Obj(15, "<< /Names [(nm) << /D [4 0 R /XYZ 1 2 null] >>] >>");
            },
            14);
        using PdfDocument doc = PdfDocument.Open(data);
        IReadOnlyList<PdfOutlineItem> o = doc.Outline;
        Assert.Equal(2, o.Count);
        Assert.Equal("One", o[0].Title);
        Assert.True(o[0].IsBold && o[0].IsItalic && o[0].IsOpen);
        Assert.Equal([1.0, 0.0, 0.0], o[0].Color);
        Assert.Equal(1, o[0].Destination!.PageIndex);
        Assert.Equal("Fit", o[0].Destination!.FitType);
        Assert.Single(o[0].Children);
        Assert.Equal(0, o[0].Children[0].Destination!.PageIndex);
        Assert.Equal(700, o[0].Children[0].Destination!.Top);
        Assert.Equal("old", o[0].Children[0].Destination!.Name);
        Assert.Equal("Two", o[1].Title);
        PdfDestination named = o[1].Destination!;
        Assert.Equal((1, "XYZ", 1.0, 2.0, null), (named.PageIndex, named.FitType, named.Left, named.Top, named.Zoom));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("outline", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["nm", "old"], doc.NamedDestinationNames.Order(StringComparer.Ordinal));
        Assert.Null(doc.GetNamedDestination("missing"));
    }

    [Fact]
    public void ActionsAreDataOnly()
    {
        byte[] data = FlatPages(
            1,
            "/OpenAction 10 0 R /Names << /JavaScript 11 0 R >>",
            b =>
            {
                b.Obj(10, "<< /S /JavaScript /JS (app.alert\\(1\\)) /Next [<< /S /URI /URI (http://example.com/x) /Next 10 0 R >> << /S /Named /N /NextPage >>] >>");
                b.Obj(11, "<< /Names [(init) 12 0 R] >>");
                b.Obj(12, "<< /S /JavaScript /JS 13 0 R >>");
                b.Stream(13, "", Encoding.ASCII.GetBytes("var x = 1;"));
            },
            11);
        using PdfDocument doc = PdfDocument.Open(data);
        Assert.Equal([new PdfJavaScript("init", "var x = 1;"), new PdfJavaScript("OpenAction", "app.alert(1)")], doc.JavaScript);
        var action = new PdfAction(doc, (PdfDictionary)doc.Catalog.Get("OpenAction")!);
        Assert.Equal("JavaScript", action.ActionType);
        Assert.Equal(["URI", "Named"], action.Next.Select(static a => a.ActionType));
        Assert.Equal("http://example.com/x", action.Next[0].Uri);
        Assert.Equal("NextPage", action.Next[1].NamedAction);
    }

    [Fact]
    public void AnnotationAppearanceAndAttachment()
    {
        byte[] data = Build(
            b =>
            {
                b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
                b.Obj(3, "<< /Type /Page /MediaBox [0 0 100 100] /Annots [4 0 R 5 0 R (junk) 6 0 R] >>");
                b.Obj(4, "<< /Type /Annot /Subtype /Widget /Rect [10 10 0 0] /F 4 /AP << /N << /On 7 0 R /Off 8 0 R >> >> /AS /On >>");
                b.Obj(5, "<< /Subtype /FileAttachment /Rect [0 0 5 5] /FS << /F (a.txt) /Desc (d) /EF << /F 9 0 R >> >> /M (D:20200102030405Z) >>");
                b.Obj(6, "<< /Subtype /Square /QuadPoints [1 2 3 4 5 6 7 8 9] /C [0.5] >>");
                b.Stream(7, "/Type /XObject /Subtype /Form /BBox [0 0 1 1]", []);
                b.Stream(8, "/Type /XObject /Subtype /Form /BBox [0 0 1 1]", []);
                b.Stream(9, "/Type /EmbeddedFile /Subtype /text#2Fplain /Params << /Size 3 >>", Encoding.ASCII.GetBytes("abc"));
            },
            10);
        using PdfDocument doc = PdfDocument.Open(data);
        IReadOnlyList<PdfAnnotation> a = doc.Pages[0].Annotations;
        Assert.Equal(3, a.Count);
        Assert.Equal(new PdfRectangle(0, 0, 10, 10), a[0].Rect);
        Assert.Equal(PdfAnnotationOptions.Print, a[0].Flags);
        Assert.Same(doc.GetObject(7), a[0].GetAppearanceStream());
        Assert.Null(a[0].GetAppearanceStream(PdfAppearanceKind.Down));
        Assert.Equal("a.txt", a[1].AttachedFile!.Name);
        Assert.Equal("text/plain", a[1].AttachedFile!.MimeType);
        Assert.Equal("abc", Encoding.ASCII.GetString(a[1].AttachedFile!.ReadContent().Span));
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), a[1].ModifiedDate);
        Assert.Equal(8, a[2].QuadPoints.Count);
        Assert.Equal([0.5], a[2].Color);
        Assert.Single(doc.GetAllEmbeddedFiles());
    }

    [Theory]
    [InlineData("D:20260102030405+02'00'", "2026-01-02T03:04:05+02:00")]
    [InlineData("D:20260102030405Z", "2026-01-02T03:04:05+00:00")]
    [InlineData("D:2026", "2026-01-01T00:00:00+00:00")]
    [InlineData("D:202601", "2026-01-01T00:00:00+00:00")]
    [InlineData("2026010203", "2026-01-02T03:00:00+00:00")]
    [InlineData("D:20260102030405-0530", "2026-01-02T03:04:05-05:30")]
    [InlineData("D:20260102030460Z", "2026-01-02T03:04:59+00:00")]
    public void DatesParse(string text, string expected)
    {
        Assert.True(PdfDate.TryParse(text, out DateTimeOffset d));
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), d);
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).Offset, d.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("D:")]
    [InlineData("D:abcd")]
    [InlineData("D:20261301")]
    [InlineData("D:20260230")]
    [InlineData("D:2026010225")]
    public void BadDatesAreRejected(string text) => Assert.False(PdfDate.TryParse(text, out _));

    [Fact]
    public void TextStringsDecodePdfDocEncodingUtf16AndLanguageEscapes()
    {
        using PdfDocument doc = PdfDocument.Open(Build(
            b =>
            {
                b.Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>");
                b.Obj(3, "<< /A (\\200\\226\\030 \\351) /B <FEFF001B656E001B00480069> /C <FFFE48006900> /D (plain) >>");
            },
            4));
        var d = (PdfDictionary)doc.GetObject(3)!;
        Assert.Equal("\u2022\u0152\u02D8 \u00E9", d.GetString("A")!.GetText());
        Assert.Equal("Hi", d.GetString("B")!.GetText());
        Assert.Equal("Hi", d.GetString("C")!.GetText());
        Assert.Equal("plain", d.GetString("D")!.GetText());
    }

    [Fact]
    public void FormFieldTreeInheritsAndNamesAreQualified()
    {
        byte[] data = FlatPages(
            1,
            "/AcroForm 10 0 R",
            b =>
            {
                b.Obj(10, "<< /Fields [11 0 R 14 0 R 16 0 R] /NeedAppearances true /XFA [(preamble) 17 0 R] >>");
                b.Obj(11, "<< /T (form) /FT /Tx /V (inherited default) /Kids [12 0 R 13 0 R] >>");
                b.Obj(12, "<< /T (a) /Parent 11 0 R /Subtype /Widget /Rect [0 0 1 1] >>");
                b.Obj(13, "<< /T (b) /Parent 11 0 R /V (own) /Kids [15 0 R 11 0 R] >>");
                b.Obj(14, "<< /T (sig) /FT /Sig /V << /Type /Sig /Filter /Adobe.PPKLite >> >>");
                b.Obj(15, "<< /Subtype /Widget /Parent 13 0 R /Rect [0 0 2 2] >>");
                b.Obj(16, "<< /T (sig2) /FT /Sig >>");
                b.Stream(17, "", Encoding.ASCII.GetBytes("<xdp/>"));
            },
            14);
        using PdfDocument doc = PdfDocument.Open(data);
        PdfAcroForm f = doc.Form!;
        Assert.True(f.NeedAppearances);
        Assert.Equal(["form", "form.a", "form.b", "sig", "sig2"], f.AllFields.Select(static x => x.FullName));
        Assert.Equal("inherited default", f.FindField("form.a")!.Value);
        Assert.Equal("own", f.FindField("form.b")!.Value);
        Assert.Equal(PdfFieldKind.Text, f.FindField("form.b")!.Kind);
        Assert.Single(f.FindField("form.b")!.Widgets);
        Assert.Single(f.FindField("form.a")!.Widgets);
        Assert.True(f.FindField("sig")!.IsSigned);
        Assert.False(f.FindField("sig2")!.IsSigned);
        Assert.Equal("<xdp/>", Encoding.ASCII.GetString(f.GetXfaXml()!.Value.Span));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("field", StringComparison.Ordinal));
    }

    [Fact]
    public void XmpWithDtdIsRejectedWithoutThrowing()
    {
        byte[] xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><x>&e;</x>");
        using PdfDocument doc = PdfDocument.Open(Build(
            b =>
            {
                b.Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>");
                b.Stream(3, "/Type /Metadata /Subtype /XML", xml);
            },
            4,
            "/Metadata 3 0 R"));
        Assert.False(doc.Xmp!.IsWellFormed);
        Assert.Equal(xml.Length, doc.Xmp.RawBytes.Length);
    }

    [Fact]
    public void XmpOverTheLimitIsIgnoredWithADiagnostic()
    {
        using PdfDocument doc = PdfDocument.Open(
            Build(
                b =>
                {
                    b.Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>");
                    b.Stream(3, "/Type /Metadata /Subtype /XML", new byte[100]);
                },
                4,
                "/Metadata 3 0 R"),
            new PdfOpenOptions { Limits = new PdfLimits { MaxMetadataBytes = 50 } });
        Assert.Null(doc.Xmp);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("metadata limit", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedSecurityHandlerIsAnEncryptionException()
    {
        var b = new PdfBuilder();
        b.BasicPages();
        b.Obj(4, "<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V 4 >>");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R /Encrypt 4 0 R", x);
        Assert.Throws<PdfEncryptionException>(() => PdfDocument.Open(b.ToArray()));
    }

    private static readonly string[] BadObjStmDictionaries = ["/Type /ObjStm /N 99 /First 5", "/Type /ObjStm /N 3 /First 99999", "/Type /ObjStm /N -1 /First -1", "/Type /ObjStm /N 3"];

    [Fact]
    public void ObjectStreamWithInvalidHeaderFieldsIsSalvaged()
    {
        // The catalog, page tree and page live in object stream 4 whose /N and /First lie about the stream.
        string o1 = "<< /Type /Catalog /Pages 2 0 R >>";
        string o2 = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        string o3 = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 50 60] >>";
        // Offsets are relative to /First; the dictionaries below get /First wrong, so salvage derives it from the header.
        string h = $"1 0 2 {o1.Length + 1} 3 {o1.Length + 1 + o2.Length + 1} ";
        byte[] body = Encoding.ASCII.GetBytes(h + o1 + " " + o2 + " " + o3);
        foreach (string dict in BadObjStmDictionaries)
        {
            var b = new PdfBuilder();
            b.Stream(4, dict, body);
            b.Write("trailer\n<< /Root 1 0 R /Size 5 >>\n");
            using PdfDocument doc = PdfDocument.Open(b.ToArray());
            Assert.Equal(1, doc.PageCount);
            Assert.Equal(new PdfRectangle(0, 0, 50, 60), doc.Pages[0].MediaBox);
            Assert.Contains(doc.Diagnostics, d => d.Message.Contains("invalid /N or /First", StringComparison.Ordinal));

            var strict = new PdfBuilder();
            strict.Stream(4, dict, body);
            strict.Write("trailer\n<< /Root 1 0 R /Size 5 >>\n");
            Assert.ThrowsAny<PdfException>(() =>
            {
                using PdfDocument s = PdfDocument.Open(strict.ToArray(), new PdfOpenOptions { Strict = true });
                _ = s.Pages.Count;
            });
        }
    }

    [Fact]
    public void EncryptedObjectStreamRepro()
    {
        string path = Path.Combine(TestData.Root(), "repros", "encrypted-objstm-aes128.pdf");
        using (PdfDocument doc = PdfDocument.Open(path, new PdfOpenOptions { Password = "up" }))
        {
            Assert.Equal(PdfAccessLevel.User, doc.AccessLevel);
            Assert.True(doc.Pages.Count >= 1);
            Assert.DoesNotContain(doc.Diagnostics, static d => d.Severity >= PdfSeverity.Warning);
        }

        using PdfDocument owner = PdfDocument.Open(path, new PdfOpenOptions { Password = "op" });
        Assert.Equal(PdfAccessLevel.Owner, owner.AccessLevel);
        Assert.Throws<PdfPasswordException>(() => PdfDocument.Open(path));
    }
}
