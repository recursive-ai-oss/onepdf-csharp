using OnePdf.Tests.Signatures;
using static OnePdf.Tests.Signatures.SigningFixture;

namespace OnePdf.Tests.Annotations;

/// <summary>Text markup annotations written onto existing pages: what the reader sees afterwards, determinism and signatures.</summary>
public class MarkupAnnotationTests
{
    private static readonly PdfMarkupQuad Quad1 = new(20, 262, 120, 262, 20, 250, 120, 250);
    private static readonly PdfMarkupQuad Quad2 = PdfMarkupQuad.FromRectangle(new PdfRectangle(20, 230, 90, 242));
    private static readonly DateTimeOffset When = new(2026, 11, 4, 9, 30, 15, TimeSpan.FromHours(1));

    public static IEnumerable<object[]> Kinds() =>
        Enum.GetValues<PdfMarkupKind>().SelectMany(static k => new[] { new object[] { k, false }, new object[] { k, true } });

    private static PdfMarkupAnnotationOptions Options(PdfMarkupKind kind, bool popup = true) => new()
    {
        Kind = kind,
        Quads = kind == PdfMarkupKind.Caret ? [] : [Quad1, Quad2],
        Rectangle = kind == PdfMarkupKind.Caret ? new PdfRectangle(100, 100, 112, 112) : null,
        RectangleDifferences = kind == PdfMarkupKind.Caret ? new PdfRectangle(1, 1, 1, 1) : null,
        CaretSymbol = kind == PdfMarkupKind.Caret ? PdfCaretSymbol.Paragraph : PdfCaretSymbol.None,
        Color = kind == PdfMarkupKind.Highlight ? [1, 0.9, 0] : [1, 0, 0],
        Opacity = 0.75,
        Contents = "Check this \u00e9",
        Title = "Reviewer",
        Name = "mk-" + kind,
        ModifiedDate = When,
        Popup = popup ? new PdfMarkupPopup(new PdfRectangle(200, 150, 290, 250), Open: true) : null,
    };

    private static byte[] Annotate(byte[] file, bool incremental, params PdfMarkupKind[] kinds)
    {
        using PdfDocument doc = Editable(file);
        foreach (PdfMarkupKind k in kinds)
        {
            doc.Pages[0].AddMarkupAnnotation(Options(k));
        }

        using var ms = new MemoryStream();
        if (incremental)
        {
            doc.SaveIncremental(ms);
        }
        else
        {
            doc.Save(ms);
        }

        return ms.ToArray();
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void RoundTripsThroughTheReader(PdfMarkupKind kind, bool incremental)
    {
        byte[] saved = Annotate(UnsignedPdf(), incremental, kind);
        using PdfDocument doc = PdfDocument.Open(saved);
        Assert.DoesNotContain(doc.Diagnostics, static d => d.Severity >= PdfSeverity.Warning);
        IReadOnlyList<PdfAnnotation> annots = doc.Pages[0].Annotations;
        Assert.Equal(5, annots.Count);
        PdfAnnotation a = annots[3];
        Assert.Equal(kind.ToString(), a.Subtype);
        Assert.Equal("Check this \u00e9", a.Contents);
        Assert.Equal("Reviewer", a.Title);
        Assert.Equal("mk-" + kind, a.Name);
        Assert.Equal(When, a.ModifiedDate);
        Assert.Equal(PdfAnnotationOptions.Print, a.Flags);
        Assert.Equal(kind == PdfMarkupKind.Highlight ? [1, 0.9, 0] : [1, 0, 0], a.Color);
        Assert.Equal(0.75, a.Dictionary.GetDouble("CA"));
        if (kind == PdfMarkupKind.Caret)
        {
            Assert.Empty(a.QuadPoints);
            Assert.Equal("P", a.Dictionary.GetName("Sy"));
            Assert.Equal(4, a.Dictionary.GetArray("RD")!.Count);
            Assert.Equal(new PdfRectangle(100, 100, 112, 112), a.Rect);
        }
        else
        {
            Assert.Equal([20, 262, 120, 262, 20, 250, 120, 250, 20, 242, 90, 242, 20, 230, 90, 230], a.QuadPoints);
            Assert.True(a.Rect.Left <= 20 && a.Rect.Bottom <= 230 && a.Rect.Right >= 120 && a.Rect.Top >= 262);
        }

        PdfStream n = a.GetAppearanceStream()!;
        Assert.NotNull(n);
        Assert.Equal("Form", n.Dictionary.GetName("Subtype"));
        Assert.True(n.GetDecodedBytes().Length > 10);
        PdfRectangle bbox = PdfRectangle.From(n.Dictionary.Get("BBox"))!.Value;
        Assert.Equal(a.Rect, bbox);

        PdfAnnotation popup = annots[4];
        Assert.Equal("Popup", popup.Subtype);
        Assert.True(a.Dictionary.GetRaw("Popup") is PdfReference);
        Assert.Same(popup.Dictionary, a.Popup);
        Assert.Same(a.Dictionary, popup.Dictionary.GetDictionary("Parent"));
        Assert.True(popup.Dictionary.GetBool("Open"));
        Assert.Equal(new PdfRectangle(200, 150, 290, 250), popup.Rect);
    }

    [Fact]
    public void HighlightMultipliesAndOpacityIsInTheAppearance()
    {
        byte[] saved = Annotate(UnsignedPdf(), true, PdfMarkupKind.Highlight);
        using PdfDocument doc = PdfDocument.Open(saved);
        PdfStream n = doc.Pages[0].Annotations[3].GetAppearanceStream()!;
        PdfDictionary gs = n.Dictionary.GetDictionary("Resources")!.GetDictionary("ExtGState")!.GetDictionary("GS")!;
        Assert.Equal("Multiply", gs.GetName("BM"));
        Assert.Equal(0.75, gs.GetDouble("ca"));
    }

    [Fact]
    public void ThePageListIsRefreshedAndNewAnnotationIsReturned()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        PdfPage page = doc.Pages[0];
        Assert.Equal(3, page.Annotations.Count);
        PdfAnnotation a = page.AddMarkupAnnotation(Options(PdfMarkupKind.Underline, popup: false));
        Assert.Equal(4, page.Annotations.Count);
        Assert.Equal("Underline", a.Subtype);
        Assert.True(a.Dictionary.Reference is not null);
        Assert.Same(a.Dictionary, page.Annotations[3].Dictionary);
        Assert.Equal(page.Dictionary.Reference!.Number, ((PdfReference)a.Dictionary.GetRaw("P")!).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameCallsGiveIdenticalBytes(bool incremental)
    {
        PdfMarkupKind[] all = Enum.GetValues<PdfMarkupKind>();
        byte[] a = Annotate(UnsignedPdf(), incremental, all);
        byte[] b = Annotate(UnsignedPdf(), incremental, all);
        Assert.Equal(a, b);
        if (!incremental)
        {
            return;
        }

        // Numbers follow the call order: annotation, appearance, pop-up, for each call.
        using PdfDocument doc = PdfDocument.Open(a);
        int[] numbers = [.. doc.Pages[0].Annotations.Skip(3).Select(static x => x.Dictionary.Reference!.Number)];
        Assert.Equal([11, 13, 14, 16, 17, 19, 20, 22, 23, 25], numbers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FilesWithoutAnIdSaveTheSameBytesToo(bool incremental)
    {
        byte[] file = IndirectAnnots();
        byte[] a = Annotate(file, incremental, PdfMarkupKind.Underline, PdfMarkupKind.Caret);
        byte[] b = Annotate(file, incremental, PdfMarkupKind.Underline, PdfMarkupKind.Caret);
        Assert.Equal(a, b);
        byte[] c = Annotate(file, incremental, PdfMarkupKind.Underline);
        Assert.NotEqual(a, c);
        using PdfDocument doc = PdfDocument.Open(a);
        Assert.NotNull(doc.Trailer.GetArray("ID"));
    }

    [Fact]
    public void WorksWhenAnnotsIsAbsentDirectOrIndirect()
    {
        foreach (bool fields in new[] { false, true })
        {
            byte[] saved = Annotate(UnsignedPdf(fields), true, PdfMarkupKind.StrikeOut);
            using PdfDocument doc = PdfDocument.Open(saved);
            Assert.Equal(fields ? 5 : 2, doc.Pages[0].Annotations.Count);
        }

        // Page whose /Annots is an indirect array: the array object is the one that changes.
        byte[] indirect = IndirectAnnots();
        using PdfDocument d2 = Editable(indirect);
        d2.Pages[0].AddMarkupAnnotation(Options(PdfMarkupKind.Squiggly));
        using var ms = new MemoryStream();
        d2.SaveIncremental(ms);
        using PdfDocument d3 = PdfDocument.Open(ms.ToArray());
        Assert.Equal(3, d3.Pages[0].Annotations.Count);
        Assert.Equal("Link", d3.Pages[0].Annotations[0].Subtype);
    }

    private static byte[] IndirectAnnots()
    {
        string s =
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R /Resources << >> /Annots 5 0 R >>\nendobj\n" +
            "4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n" +
            "5 0 obj\n[6 0 R]\nendobj\n" +
            "6 0 obj\n<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Border [0 0 0] >>\nendobj\n";
        var sb = new System.Text.StringBuilder(s);
        var offsets = new List<int>();
        for (int n = 1; n <= 6; n++)
        {
            offsets.Add(s.IndexOf($"\n{n} 0 obj", StringComparison.Ordinal) + 1);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            sb.Append($"{o:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void InvalidOptionsThrowAndLeaveTheDocumentUnchanged()
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        PdfPage page = doc.Pages[0];
        int objects = doc.ObjectCount;
        PdfMarkupAnnotationOptions[] bad =
        [
            new() { Kind = PdfMarkupKind.Highlight },
            new() { Kind = PdfMarkupKind.Caret },
            new() { Kind = PdfMarkupKind.Caret, Rectangle = new PdfRectangle(1, 1, 5, 5), Quads = [Quad1] },
            new() { Kind = PdfMarkupKind.Underline, Quads = [new PdfMarkupQuad(double.NaN, 0, 1, 0, 0, 1, 1, 1)] },
            new() { Kind = PdfMarkupKind.Underline, Quads = [Quad1], Color = [2] },
            new() { Kind = PdfMarkupKind.Underline, Quads = [Quad1], Color = [0, 0] },
            new() { Kind = PdfMarkupKind.Underline, Quads = [Quad1], Opacity = 0 },
            new() { Kind = PdfMarkupKind.Underline, Quads = [Quad1], Popup = new PdfMarkupPopup(default) },
            new() { Kind = (PdfMarkupKind)99, Quads = [Quad1] },
        ];
        for (int i = 0; i < bad.Length; i++)
        {
            PdfMarkupAnnotationOptions o = bad[i];
            Assert.True(Throws(() => page.AddMarkupAnnotation(o)), "case " + i);
        }

        Assert.Equal(3, page.Annotations.Count);
        Assert.Equal(objects, doc.ObjectCount);
    }

    private static bool Throws(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public void ReadOnlyDocumentIsRefused()
    {
        using PdfDocument doc = PdfDocument.Open(UnsignedPdf());
        Assert.Throws<InvalidOperationException>(() => doc.Pages[0].AddMarkupAnnotation(Options(PdfMarkupKind.Highlight)));
    }

    // ---- signatures ----

    private static byte[] SignedAt(int? level) => SignToBytes(Editable(UnsignedPdf()), Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions
    {
        FieldName = "Sig2",
        Certify = level is { } l ? (PdfCertificationLevel)l : null,
    });

    [Theory]
    // No certification: the analyzer reads an approval signature as permission level 2 (docs/signatures.md), which flags annotations.
    [InlineData(0, false)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(1, false)]
    public void IncrementalMarkupKeepsEarlierSignaturesValidWhereAllowed(int level, bool valid)
    {
        byte[] signed = SignedAt(level == 0 ? null : level);
        byte[] edited = Annotate(signed, true, Enum.GetValues<PdfMarkupKind>());
        Assert.True(edited.AsSpan(0, signed.Length).SequenceEqual(signed));
        using PdfDocument check = PdfDocument.Open(edited);
        PdfSignatureReport report = check.VerifySignatures(Trust);
        PdfSignatureVerification v = Assert.Single(report.Signatures);
        string why = string.Join(" | ", v.Changes.Select(static c => $"{c.Category} {c.Allowed} {c.Description}"));
        Assert.Equal(PdfCheckStatus.Valid, v.Integrity);
        Assert.True(valid == v.IsValid, why);
        if (valid)
        {
            Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, v.Coverage);
            Assert.All(v.Changes, c => Assert.True(c.Allowed, why));
        }
        else
        {
            Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
            Assert.Contains(v.Changes, static c => !c.Allowed && c.Category == PdfChangeCategory.Annotation);
        }
    }

    // A reference to a number the file never defines must not come to mean a new object. Pointing far beyond the object limit once
    // switched that guard off for every other number.
    [Fact]
    public void NewObjectsNeverTakeANumberTheFileAlreadyPointsAt()
    {
        string s =
            "%PDF-1.7\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Far 9000000 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << >> /Annots [4 0 R 5 0 R 6 0 R] >>\nendobj\n";
        int xref = s.Length;
        s += "xref\n0 4\n0000000000 65535 f \n";
        foreach (int n in new[] { 1, 2, 3 })
        {
            s += $"{s.IndexOf($"\n{n} 0 obj", StringComparison.Ordinal) + 1:D10} 00000 n \n";
        }

        s += $"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n";
        using PdfDocument doc = Editable(System.Text.Encoding.Latin1.GetBytes(s));
        PdfAnnotation a = doc.Pages[0].AddMarkupAnnotation(Options(PdfMarkupKind.Highlight, popup: false));
        Assert.True(a.Dictionary.Reference!.Number is not (4 or 5 or 6), "took number " + a.Dictionary.Reference.Number);
        Assert.Single(doc.Pages[0].Annotations);
    }

    [Fact]
    public void CommittedReproListsTheAddedAnnotationOnce()
    {
        byte[] input = File.ReadAllBytes(Path.Combine(OnePdf.Tests.Writing.WriterSupport.Root(), "testdata", "repros", "markup-dangling-annots-beyond-object-limit.pdf"));
        using PdfDocument doc = Editable(input);
        PdfPage page = doc.Pages[0];
        PdfAnnotation a = page.AddMarkupAnnotation(Options(PdfMarkupKind.Underline, popup: false));
        Assert.True(a.Dictionary.Reference!.Number > 6);
        Assert.Single(page.Annotations);
    }
}
