namespace OnePdf.Tests.Signatures;

public class SignatureModificationTests
{
    private static readonly TestPki Pki = TestPki.Shared;

    private static readonly int[] RectValues = [200, 200, 220, 220];

    private static byte[] Certified(int level, string extraField = "", string extraSig = "")
    {
        byte[] file = SignedPdf.BaseFile(new SignedPdf.Options { CertifyLevel = level, ExtraFieldEntries = extraField, ExtraSigEntries = extraSig }, out _);
        return SignedPdf.SignWith(file, Pki.RsaSigner, new CmsSpec { Cades = false });
    }

    private static byte[] Edit(byte[] file, Action<PdfDocument> change)
    {
        using var doc = PdfDocument.Open(file, new PdfOpenOptions { AllowEditing = true });
        change(doc);
        using var ms = new MemoryStream();
        doc.SaveIncremental(ms);
        return ms.ToArray();
    }

    private static string Dump(PdfSignatureVerification v) => string.Join(" | ", v.Changes.Select(static c => $"{c.ObjectNumber} {c.Category} {c.Allowed} {c.Description}"));

    private static PdfSignatureVerification First(byte[] file)
    {
        PdfSignatureVerification v = SignedPdf.VerifyOne(file);
        Assert.True(v.Integrity == PdfCheckStatus.Valid, Dump(v));
        Assert.Equal(PdfCheckStatus.Valid, v.Integrity);
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        return v;
    }

    private static byte[] FillName(byte[] file) => Edit(file, d => d.Form!.FindField("Name")!.SetValue("Alice"));

    private static byte[] ChangeContent(byte[] file) => Edit(file, d =>
    {
        var s = (PdfStream)d.GetObject(7)!;
        s.SetData("BT /F1 12 Tf 20 200 Td (Evil text) Tj ET"u8);
    });

    private static byte[] AddAnnotation(byte[] file) => Edit(file, d =>
    {
        var annot = new PdfDictionary();
        annot.SetEntry("Type", new PdfName("Annot"));
        annot.SetEntry("Subtype", new PdfName("Text"));
        var rect = new PdfArray();
        foreach (int v in RectValues)
        {
            rect.Append(new PdfInteger(v));
        }

        annot.SetEntry("Rect", rect);
        annot.SetEntry("Contents", PdfString.FromText("note"));
        PdfReference r = d.AddObject(annot);
        ((PdfDictionary)d.GetObject(3)!).GetArray("Annots")!.Append(r);
    });

    [Fact]
    public void FormFillAfterCertificationAtLevel2IsAllowed()
    {
        PdfSignatureVerification v = First(FillName(Certified(2)));
        Assert.Equal(2, v.DocMdpPermission);
        Assert.True(v.HasLaterRevisions);
        Assert.True(v.Coverage == PdfSignatureCoverage.EarlierRevisionAllowedChanges, Dump(v));
        Assert.NotEmpty(v.Changes);
        Assert.All(v.Changes, static c => Assert.True(c.Allowed, c.Description));
        Assert.Contains(v.Changes, static c => c.Category == PdfChangeCategory.FormFieldValue && c.ObjectNumber == 8);
        Assert.True(v.IsValid);
    }

    [Fact]
    public void FormFillAtLevel1IsDisallowed()
    {
        PdfSignatureVerification v = First(FillName(Certified(1)));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
        Assert.Contains(v.Changes, static c => !c.Allowed && c.Category == PdfChangeCategory.FormFieldValue);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void PageContentChangeIsDisallowedAtEveryLevel()
    {
        foreach (int level in new[] { 1, 2, 3 })
        {
            PdfSignatureVerification v = First(ChangeContent(Certified(level)));
            Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
            Assert.Contains(v.Changes, static c => c.ObjectNumber == 7 && c.Category == PdfChangeCategory.PageContent && !c.Allowed);
        }
    }

    [Fact]
    public void AnnotationIsAllowedAtLevel3AndNotAtLevel2()
    {
        PdfSignatureVerification three = First(AddAnnotation(Certified(3)));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, three.Coverage);
        Assert.Contains(three.Changes, static c => c.Category == PdfChangeCategory.Annotation && c.Allowed);
        Assert.All(three.Changes, static c => Assert.True(c.Allowed, c.Description));

        PdfSignatureVerification two = First(AddAnnotation(Certified(2)));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, two.Coverage);
        Assert.Contains(two.Changes, static c => c.Category == PdfChangeCategory.Annotation && !c.Allowed);
    }

    [Fact]
    public void SecondSignatureIsAllowedAtLevel2AndNotAtLevel1()
    {
        foreach ((int level, bool allowed) in new[] { (1, false), (2, true), (3, true) })
        {
            byte[] file = Certified(level);
            byte[] two = SignedPdf.AppendSecondSignature(file, digest => CmsKit.Build(digest(System.Security.Cryptography.HashAlgorithmName.SHA256), Pki.EcSigner, [Pki.EcSigner, Pki.Intermediate], new CmsSpec { Scheme = SigScheme.Ecdsa }));
            using var doc = PdfDocument.Open(two);
            IReadOnlyList<PdfSignature> sigs = doc.GetSignatures();
            Assert.Equal(2, sigs.Count);
            PdfSignatureVerification v = sigs[0].Verify(Pki.Trusting());
            Assert.Equal(allowed ? PdfSignatureCoverage.EarlierRevisionAllowedChanges : PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
            Assert.Contains(v.Changes, static c => c.Category == PdfChangeCategory.NewSignature);
            PdfSignatureVerification second = sigs[1].Verify(Pki.Trusting());
            Assert.True(second.IsValid, string.Join("; ", second.Warnings));
            Assert.Equal(PdfSignatureCoverage.WholeDocument, second.Coverage);
            Assert.Equal(1, second.CoveredRevision);
        }
    }

    [Fact]
    public void FieldLockBlocksOnlyTheLockedFields()
    {
        string lockEntry = "/Lock << /Type /SigFieldLock /Action /Include /Fields [(Name)] >>";
        byte[] file = Certified(2, lockEntry);
        using (var doc = PdfDocument.Open(file))
        {
            PdfSignatureLock l = Assert.IsType<PdfSignatureLock>(doc.GetSignatures()[0].FieldLock);
            Assert.Equal(PdfLockAction.Include, l.Action);
            Assert.Equal(["Name"], l.Fields);
        }

        PdfSignatureVerification locked = First(FillName(file));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, locked.Coverage);
        Assert.Contains(locked.Changes, static c => !c.Allowed && c.Description.Contains("locked", StringComparison.Ordinal));

        PdfSignatureVerification open = First(Edit(file, d => d.Form!.FindField("Other")!.SetValue("fine")));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, open.Coverage);
    }

    [Fact]
    public void FieldMdpTransformAndExcludeAction()
    {
        string sigEntries = "/Reference [ << /Type /SigRef /TransformMethod /FieldMDP /TransformParams << /Type /TransformParams /Action /Exclude /Fields [(Other)] /V /1.2 >> >> ]";
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options { ExtraSigEntries = sigEntries }, out _), Pki.RsaSigner, new CmsSpec());
        PdfSignatureVerification nameChange = First(FillName(file));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, nameChange.Coverage);
        PdfSignatureVerification otherChange = First(Edit(file, d => d.Form!.FindField("Other")!.SetValue("fine")));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, otherChange.Coverage);
    }

    [Fact]
    public void LockPermissionLowersTheLevel()
    {
        string lockEntry = "/Lock << /Action /Include /Fields [(Other)] /P 1 >>";
        PdfSignatureVerification v = First(Edit(Certified(3, lockEntry), d => d.Form!.FindField("Name")!.SetValue("x")));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
    }

    [Fact]
    public void ApprovalSignatureWithoutCertificationUsesTheDefaultLevel()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), Pki.RsaSigner, new CmsSpec());
        PdfSignatureVerification fill = First(FillName(file));
        Assert.Null(fill.DocMdpPermission);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, fill.Coverage);
        PdfSignatureVerification content = First(ChangeContent(file));
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, content.Coverage);
    }

    [Fact]
    public void RewritingAnObjectUnchangedIsNotAChange()
    {
        // An update that re-saves a page unchanged (as some tools do) is not reported.
        byte[] file = Certified(1);
        byte[] again = SignedPdf.Append(file, new Dictionary<int, string> { [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" }, 11);
        PdfSignatureVerification v = First(again);
        Assert.Empty(v.Changes);
        Assert.True(v.Coverage == PdfSignatureCoverage.EarlierRevisionAllowedChanges, Dump(v));
    }

    [Fact]
    public void AnalysisCanBeTurnedOff()
    {
        byte[] file = FillName(Certified(2));
        PdfSignatureVerification v = SignedPdf.VerifyOne(file, new PdfSignatureVerifyOptions { TrustAnchors = [Pki.RootAnchor], AnalyzeModifications = false });
        Assert.Empty(v.Changes);
        Assert.True(v.HasLaterRevisions);
    }
}
