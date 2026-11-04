namespace OnePdf.Tests.Signatures;

/// <summary>
/// After an approval signature (no DocMDP), a revision that only appends new text markup annotations and their pop-ups is an allowed
/// change; every other annotation subtype and every other change is judged as at permission level 2.
/// </summary>
public class ApprovalMarkupTests
{
    private static readonly TestPki Pki = TestPki.Shared;

    private static byte[] Approved() =>
        SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), Pki.RsaSigner, new CmsSpec { Cades = false });

    private static byte[] Edit(byte[] file, Action<PdfDocument> change)
    {
        using var doc = PdfDocument.Open(file, new PdfOpenOptions { AllowEditing = true });
        change(doc);
        using var ms = new MemoryStream();
        doc.SaveIncremental(ms);
        return ms.ToArray();
    }

    private static string Dump(PdfSignatureVerification v) => string.Join(" | ", v.Changes.Select(static c => $"{c.ObjectNumber} {c.Category} {c.Allowed} {c.Description}"));

    private static PdfSignatureVerification Verify(byte[] file)
    {
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.Equal(PdfCheckStatus.Valid, v.Integrity);
        return v;
    }

    private static void AddMarkup(PdfDocument d, PdfMarkupKind kind, bool popup)
    {
        d.Pages[0].AddMarkupAnnotation(new PdfMarkupAnnotationOptions
        {
            Kind = kind,
            Quads = kind == PdfMarkupKind.Caret ? [] : [PdfMarkupQuad.FromRectangle(new PdfRectangle(20, 190, 100, 205))],
            Rectangle = kind == PdfMarkupKind.Caret ? new PdfRectangle(50, 50, 60, 62) : null,
            Color = [1, 0, 0],
            Contents = "note",
            Popup = popup ? new PdfMarkupPopup(new PdfRectangle(150, 150, 280, 250)) : null,
        });
    }

    private static PdfReference Append(PdfDocument d, PdfDictionary annot)
    {
        PdfReference r = d.AddObject(annot);
        ((PdfDictionary)d.GetObject(3)!).GetArray("Annots")!.Append(r);
        return r;
    }

    private static PdfDictionary Annot(string subtype)
    {
        var a = new PdfDictionary();
        a.SetEntry("Type", new PdfName("Annot"));
        a.SetEntry("Subtype", new PdfName(subtype));
        var rect = new PdfArray();
        foreach (int v in (int[])[200, 200, 240, 240])
        {
            rect.Append(new PdfInteger(v));
        }

        a.SetEntry("Rect", rect);
        return a;
    }

    private static void AssertFlagged(byte[] file)
    {
        PdfSignatureVerification v = Verify(file);
        Assert.False(v.IsValid, Dump(v));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
        Assert.Contains(v.Changes, static c => !c.Allowed);
    }

    [Theory]
    [InlineData(PdfMarkupKind.Highlight, false)]
    [InlineData(PdfMarkupKind.StrikeOut, true)]
    [InlineData(PdfMarkupKind.Underline, true)]
    [InlineData(PdfMarkupKind.Squiggly, false)]
    [InlineData(PdfMarkupKind.Caret, true)]
    public void AppendedMarkupIsAllowedAfterAnApprovalSignature(PdfMarkupKind kind, bool popup)
    {
        PdfSignatureVerification v = Verify(Edit(Approved(), d => AddMarkup(d, kind, popup)));
        Assert.True(v.IsValid, Dump(v));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, v.Coverage);
        Assert.Contains(v.Changes, static c => c.Category == PdfChangeCategory.Annotation && c.Allowed);
        Assert.All(v.Changes, static c => Assert.True(c.Allowed, c.Description));
    }

    [Fact]
    public void SeveralMarkupAnnotationsInOneUpdateAreAllowed()
    {
        PdfSignatureVerification v = Verify(Edit(Approved(), d =>
        {
            foreach (PdfMarkupKind k in Enum.GetValues<PdfMarkupKind>())
            {
                AddMarkup(d, k, popup: true);
            }
        }));
        Assert.True(v.IsValid, Dump(v));
    }

    [Theory]
    [InlineData("FreeText")]
    [InlineData("Stamp")]
    [InlineData("Ink")]
    [InlineData("Square")]
    [InlineData("Text")]
    public void OtherAnnotationSubtypesAreStillFlagged(string subtype) =>
        AssertFlagged(Edit(Approved(), d => Append(d, Annot(subtype))));

    [Fact]
    public void AddedLinkIsStillFlagged()
    {
        AssertFlagged(Edit(Approved(), d =>
        {
            PdfDictionary link = Annot("Link");
            var action = new PdfDictionary();
            action.SetEntry("S", new PdfName("URI"));
            action.SetEntry("URI", PdfString.FromText("https://example.test/"));
            link.SetEntry("A", action);
            Append(d, link);
        }));
    }

    [Fact]
    public void MarkupPlusAnotherSubtypeIsFlagged() =>
        AssertFlagged(Edit(Approved(), d =>
        {
            AddMarkup(d, PdfMarkupKind.Highlight, popup: false);
            Append(d, Annot("FreeText"));
        }));

    [Fact]
    public void MarkupThatAlsoChangesAnExistingAnnotationIsFlagged() =>
        AssertFlagged(Edit(Approved(), d =>
        {
            AddMarkup(d, PdfMarkupKind.Highlight, popup: true);
            var rect = new PdfArray();
            foreach (int v in (int[])[0, 0, 300, 300])
            {
                rect.Append(new PdfInteger(v));
            }

            ((PdfDictionary)d.GetObject(8)!).SetEntry("Rect", rect);
        }));

    [Fact]
    public void MarkupThatAlsoChangesPageContentIsFlagged() =>
        AssertFlagged(Edit(Approved(), d =>
        {
            AddMarkup(d, PdfMarkupKind.Underline, popup: false);
            ((PdfStream)d.GetObject(7)!).SetData("BT /F1 12 Tf 20 200 Td (Evil text) Tj ET"u8);
        }));

    [Fact]
    public void MarkupThatAlsoChangesAnotherPageKeyIsFlagged() =>
        AssertFlagged(Edit(Approved(), d =>
        {
            AddMarkup(d, PdfMarkupKind.Underline, popup: false);
            ((PdfDictionary)d.GetObject(3)!).SetEntry("Rotate", new PdfInteger(90));
        }));

    [Fact]
    public void PopupWhoseParentIsAnExistingAnnotationIsFlagged() =>
        AssertFlagged(Edit(Approved(), d =>
        {
            PdfDictionary popup = Annot("Popup");
            popup.SetEntry("Parent", new PdfReference(8, 0));
            Append(d, popup);
        }));

    [Fact]
    public void PopupWithAnExistingParentBesideNewMarkupIsFlagged() =>
        AssertFlagged(Edit(Approved(), d =>
        {
            AddMarkup(d, PdfMarkupKind.Highlight, popup: false);
            PdfDictionary popup = Annot("Popup");
            popup.SetEntry("Parent", new PdfReference(8, 0));
            Append(d, popup);
        }));

    [Fact]
    public void PopupWithNoParentIsFlagged() =>
        AssertFlagged(Edit(Approved(), d => Append(d, Annot("Popup"))));

    [Fact]
    public void ExistingAnnotationAppendedToTheListIsFlagged() =>
        AssertFlagged(Edit(Approved(), d =>
        {
            // An annotation that already exists elsewhere is not a new markup annotation, whatever its subtype.
            PdfDictionary existing = (PdfDictionary)d.GetObject(8)!;
            ((PdfDictionary)d.GetObject(3)!).GetArray("Annots")!.Append(existing.Reference!);
        }));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CertifiedLevels1And2StillFlagMarkup(int level)
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options { CertifyLevel = level }, out _), Pki.RsaSigner, new CmsSpec { Cades = false });
        AssertFlagged(Edit(file, d => AddMarkup(d, PdfMarkupKind.Highlight, popup: true)));
    }

    [Fact]
    public void CertifiedLevel3StillAllowsMarkup()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options { CertifyLevel = 3 }, out _), Pki.RsaSigner, new CmsSpec { Cades = false });
        PdfSignatureVerification v = Verify(Edit(file, d => AddMarkup(d, PdfMarkupKind.Highlight, popup: true)));
        Assert.True(v.IsValid, Dump(v));
    }
}
