using System.Text;

namespace OnePdf.Tests.Signatures;

/// <summary>
/// Files built to defeat signature validation, one test per attack class and variant of the published work on PDF signature
/// attacks (universal signature forgery, incremental saving, signature wrapping, shadow attacks and the certification attacks).
/// Every file is made here with the test kit. Each test says whether the result is a detection (<c>IsValid</c> false) or only an
/// advisory entry in <c>Changes</c> for a change the permission level lets through.
/// </summary>
public class AttackTests
{
    private const string PageBody = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 7 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Annots [{0}] >>";

    private static readonly TestPki Pki = TestPki.Shared;

    private static string Latin(byte[] b) => Encoding.Latin1.GetString(b);

    private static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);

    private static byte[] Signed(SignedPdf.Options? options = null, CmsSpec? spec = null) =>
        SignedPdf.SignWith(SignedPdf.BaseFile(options ?? new SignedPdf.Options(), out _), Pki.RsaSigner, spec ?? new CmsSpec());

    private static PdfSignatureVerification Check(byte[] file, PdfSignatureVerifyOptions? options = null)
    {
        using var doc = PdfDocument.Open(file);
        IReadOnlyList<PdfSignature> sigs = doc.GetSignatures();
        Assert.NotEmpty(sigs);
        return sigs[0].Verify(options ?? Pki.Trusting());
    }

    private static void AssertRejected(PdfSignatureVerification v)
    {
        Assert.False(v.IsValid, string.Join("; ", v.Warnings));
    }

    /// <summary>Replaces the text of the signature's <c>/ByteRange</c> array, keeping the length so no offset moves.</summary>
    private static byte[] WithRange(byte[] file, string value)
    {
        string t = Latin(file);
        int at = t.LastIndexOf("/ByteRange ", StringComparison.Ordinal) + "/ByteRange ".Length;
        // The placeholder the kit writes is 36 characters; the real range is padded with spaces after its closing bracket.
        return Splice(file, at, "[0 0000000000 0000000000 0000000000]".Length, value);
    }

    private static byte[] WithContentsValue(byte[] file, string value)
    {
        string t = Latin(file);
        int at = t.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        int end = t.IndexOf('>', at) + 1;
        return Splice(file, at, end - at, value);
    }

    private static byte[] Splice(byte[] file, int at, int length, string value)
    {
        Assert.True(value.Length <= length, "replacement is longer than the original");
        byte[] copy = (byte[])file.Clone();
        Bytes(value.PadRight(length)).CopyTo(copy, at);
        return copy;
    }

    private static (long B, long C, long D) Range(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        IReadOnlyList<long> r = doc.GetSignatures()[0].ByteRange!;
        return (r[1], r[2], r[3]);
    }

    private static byte[] Insert(byte[] file, long at, string text) => [.. file.AsSpan(0, (int)at), .. Bytes(text), .. file.AsSpan((int)at)];

    private static byte[] Certified(int level, SignedPdf.Options? basis = null)
    {
        SignedPdf.Options o = basis ?? new SignedPdf.Options();
        return SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options
        {
            CertifyLevel = level,
            ExtraObjects = o.ExtraObjects,
            FontObject = o.FontObject,
            PreparedContent = o.PreparedContent,
        }, out _), Pki.RsaSigner, new CmsSpec());
    }

    // ---- 1. Universal signature forgery --------------------------------------------------------------------------------

    [Theory]
    [InlineData("[0 0 0 0]")]
    [InlineData("null")]
    [InlineData("(0 0 0 0)")]
    [InlineData("/Name")]
    [InlineData("<< >>")]
    [InlineData("[0.0 100.0 200.0 300.0]")]
    [InlineData("[0 -1 5 5]")]
    [InlineData("[]")]
    [InlineData("[0]")]
    [InlineData("[0 1 2]")]
    [InlineData("[0 1 2 3 4 5]")]
    [InlineData("true")]
    [InlineData("12")]
    [InlineData("7 0 R")]
    [InlineData("99 0 R")]
    public void UsfByteRangeOfWrongValueOrTypeIsNeverValid(string value)
    {
        byte[] file = WithRange(Signed(), value);
        using var doc = PdfDocument.Open(file);
        IReadOnlyList<PdfSignature> sigs = doc.GetSignatures();
        Assert.Single(sigs);
        PdfSignatureVerification v = sigs[0].Verify(Pki.Trusting());
        Assert.NotEqual(PdfByteRangeStatus.Valid, v.ByteRange);
        AssertRejected(v);
        Assert.False(doc.VerifySignatures(Pki.Trusting()).AllValid);
    }

    [Fact]
    public void UsfByteRangeKeyAbsentIsMissing()
    {
        byte[] file = Signed();
        string t = Latin(file);
        int at = t.LastIndexOf("/ByteRange", StringComparison.Ordinal);
        byte[] copy = (byte[])file.Clone();
        Bytes("/ByteRangX").CopyTo(copy, at);
        PdfSignatureVerification v = Check(copy);
        Assert.Equal(PdfByteRangeStatus.Missing, v.ByteRange);
        AssertRejected(v);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("()")]
    [InlineData("(abc)")]
    [InlineData("/Name")]
    [InlineData("<< >>")]
    [InlineData("[]")]
    [InlineData("7 0 R")]
    [InlineData("<>")]
    [InlineData("<00>")]
    public void UsfContentsOfWrongValueOrTypeIsNeverValid(string value)
    {
        PdfSignatureVerification v = Check(WithContentsValue(Signed(), value));
        AssertRejected(v);
        Assert.NotEqual(PdfByteRangeStatus.Valid, v.ByteRange);
    }

    [Fact]
    public void UsfContentsKeyAbsentIsMissing()
    {
        byte[] file = Signed();
        int at = Latin(file).IndexOf("/Contents <", StringComparison.Ordinal);
        byte[] copy = (byte[])file.Clone();
        Bytes("/ContentX").CopyTo(copy, at);
        PdfSignatureVerification v = Check(copy);
        Assert.Equal(PdfByteRangeStatus.Missing, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void UsfContentsEmptyWithMatchingRangeIsMissing()
    {
        // The hex digits are replaced by white space: an empty string whose gap the byte range still matches exactly.
        byte[] file = Signed();
        string t = Latin(file);
        int at = t.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents <".Length;
        int end = t.IndexOf('>', at);
        PdfSignatureVerification v = Check(Splice(file, at, end - at, string.Empty));
        Assert.Equal(PdfByteRangeStatus.Missing, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void UsfContentsAllZerosFailsTheSignatureCheck()
    {
        byte[] file = SignedPdf.Sign(SignedPdf.BaseFile(new SignedPdf.Options(), out _), _ => []);
        PdfSignatureVerification v = Check(file);
        Assert.Equal(PdfByteRangeStatus.Valid, v.ByteRange);
        Assert.NotEqual(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.NotEqual(PdfCheckStatus.Valid, v.Integrity);
        AssertRejected(v);
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(-1)]
    [InlineData(3)]
    public void UsfGapDoesNotMatchTheContentsString(int delta)
    {
        // The second range starts a few bytes early or late, so the excluded gap is not exactly the /Contents value.
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        byte[] changed = WithRange(file, $"[0 {b} {c + delta} {d}]");
        PdfSignatureVerification v = Check(changed);
        Assert.NotEqual(PdfByteRangeStatus.Valid, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void UsfByteRangeAsIndirectObjectIsRefusedEvenWhenItIsCorrect()
    {
        byte[] file = Signed(new SignedPdf.Options { IndirectByteRange = true });
        PdfSignatureVerification v = Check(file);
        Assert.Equal(PdfByteRangeStatus.Malformed, v.ByteRange);
        Assert.Contains(v.Warnings, static w => w.Contains("indirect", StringComparison.Ordinal));
        AssertRejected(v);
    }

    [Fact]
    public void UsfContentsAsIndirectObjectIsRefusedEvenWhenItIsCorrect()
    {
        byte[] file = Signed(new SignedPdf.Options { IndirectContents = true });
        PdfSignatureVerification v = Check(file);
        Assert.Equal(PdfByteRangeStatus.Malformed, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void UsfIndirectNumberInsideTheByteRangeIsRefused()
    {
        // [0 b c d] with the first number held in an object of its own (object 11).
        byte[] file = Signed(new SignedPdf.Options { ExtraObjects = new Dictionary<int, string> { [11] = "0" } });
        (long b, long c, long d) = Range(file);
        PdfSignatureVerification v = Check(WithRange(file, $"[11 0 R {b} {c} {d}]"));
        Assert.Equal(PdfByteRangeStatus.Malformed, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void UsfSignatureThatIsNotFoundIsNeverReportedValid()
    {
        // A field whose value is null, and a value dictionary on a field without /FT: nothing to verify, and the report says so.
        byte[] file = Signed();
        string t = Latin(file);
        int at = t.IndexOf("/V 6 0 R", StringComparison.Ordinal);
        byte[] nulled = Splice(file, at, "/V 6 0 R".Length, "/V null");
        using var doc = PdfDocument.Open(nulled);
        PdfSignatureReport report = doc.VerifySignatures(Pki.Trusting());
        Assert.False(report.HasSignatures);
        Assert.False(report.AllValid);

        int ft = t.IndexOf("/FT /Sig /T (Sig1)", StringComparison.Ordinal);
        byte[] noType = Splice(file, ft, "/FT /Sig".Length, "/FT /Xxx");
        using var doc2 = PdfDocument.Open(noType);
        Assert.False(doc2.VerifySignatures(Pki.Trusting()).AllValid);
    }

    // ---- 2. Incremental saving attack ----------------------------------------------------------------------------------

    private static PdfSignatureVerification AssertModified(byte[] attacked, int level = 0)
    {
        PdfSignatureVerification v = Check(attacked);
        AssertRejected(v);
        Assert.NotEqual(PdfSignatureCoverage.WholeDocument, v.Coverage);
        Assert.True(level == 0 || v.DocMdpPermission == level);
        return v;
    }

    [Fact]
    public void IsaContentStreamRedefined()
    {
        byte[] file = Signed();
        string evil = "BT /F1 12 Tf 20 200 Td (Pay 9999) Tj ET";
        byte[] attacked = SignedPdf.Append(file, new Dictionary<int, string> { [7] = $"<< /Length {evil.Length} >>\nstream\n{evil}\nendstream" }, 11);
        PdfSignatureVerification v = AssertModified(attacked);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 7 && c.Category == PdfChangeCategory.PageContent && !c.Allowed);
        using var doc = PdfDocument.Open(attacked);
        Assert.Contains("Pay 9999", Encoding.Latin1.GetString(((PdfStream)doc.GetObject(7)!).GetDecodedBytes().Span), StringComparison.Ordinal);
    }

    [Fact]
    public void IsaPageObjectRedefined()
    {
        byte[] attacked = SignedPdf.Append(Signed(), new Dictionary<int, string> { [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 600] /Contents 7 0 R >>" }, 11);
        PdfSignatureVerification v = AssertModified(attacked);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 3 && c.Category == PdfChangeCategory.PageContent && !c.Allowed);
    }

    [Fact]
    public void IsaPagesTreeRedefined()
    {
        byte[] attacked = SignedPdf.Append(Signed(), new Dictionary<int, string>
        {
            [2] = "<< /Type /Pages /Kids [3 0 R 12 0 R] /Count 2 >>",
            [12] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>",
        }, 13);
        PdfSignatureVerification v = AssertModified(attacked);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 2 && !c.Allowed);
    }

    [Fact]
    public void IsaCatalogRedefined()
    {
        byte[] attacked = SignedPdf.Append(Signed(), new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 12 0 R /AcroForm 5 0 R >>",
            [12] = "<< /Type /Pages /Kids [13 0 R] /Count 1 >>",
            [13] = "<< /Type /Page /Parent 12 0 R /MediaBox [0 0 300 300] >>",
        }, 14);
        PdfSignatureVerification v = AssertModified(attacked);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 1 && !c.Allowed);
    }

    [Fact]
    public void IsaFontRedefined()
    {
        byte[] file = Signed(new SignedPdf.Options { FontObject = true });
        byte[] attacked = SignedPdf.Append(file, new Dictionary<int, string> { [11] = "<< /Type /Font /Subtype /Type1 /BaseFont /Symbol >>" }, 12);
        PdfSignatureVerification v = AssertModified(attacked);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 11 && c.Category == PdfChangeCategory.PageContent && !c.Allowed);
    }

    [Fact]
    public void IsaFontRedefinedThroughTheWriter()
    {
        byte[] file = Signed(new SignedPdf.Options { FontObject = true });
        using var editable = PdfDocument.Open(file, new PdfOpenOptions { AllowEditing = true });
        ((PdfDictionary)editable.GetObject(11)!).SetEntry("BaseFont", new PdfName("Symbol"));
        using var ms = new MemoryStream();
        editable.SaveIncremental(ms);
        PdfSignatureVerification v = AssertModified(ms.ToArray());
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 11 && !c.Allowed);
    }

    [Fact]
    public void IsaObjectsAppendedWithoutAnyXrefSection()
    {
        // The attack relies on a reader that repairs: objects after %%EOF, no table, no trailer.
        byte[] file = Signed();
        string evil = "BT /F1 12 Tf 20 200 Td (Pay 9999) Tj ET";
        byte[] attacked = [.. file, .. Bytes($"\n7 0 obj\n<< /Length {evil.Length} >>\nstream\n{evil}\nendstream\nendobj\n")];
        PdfSignatureVerification v = AssertModified(attacked);
        Assert.Contains(v.Changes, static c => !c.Allowed && c.Description.Contains("%%EOF", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("trailer\n<< /Size 11 /Root 1 0 R >>\nstartxref\n0\n%%EOF\n")]
    [InlineData("trailer\n<< /Size 11 /Root 1 0 R >>\n")]
    [InlineData("startxref\n99999999\n%%EOF\n")]
    public void IsaObjectsAppendedWithOnlyATrailer(string tail)
    {
        byte[] file = Signed();
        string evil = "BT /F1 12 Tf 20 200 Td (Pay 9999) Tj ET";
        byte[] attacked = [.. file, .. Bytes($"\n7 0 obj\n<< /Length {evil.Length} >>\nstream\n{evil}\nendstream\nendobj\n{tail}")];
        using var doc = PdfDocument.Open(attacked);
        PdfSignatureVerification v = doc.GetSignatures()[0].Verify(Pki.Trusting());
        AssertRejected(v);
        Assert.NotEqual(PdfSignatureCoverage.WholeDocument, v.Coverage);
    }

    [Fact]
    public void IsaTrailerPointsAtANewCatalog()
    {
        // A well-formed update: new catalog and page tree (the old form is kept, so the signature stays findable), nothing
        // existing is rewritten, only /Root changes.
        byte[] attacked = SignedPdf.Append(Signed(), new Dictionary<int, string>
        {
            [12] = "<< /Type /Catalog /Pages 13 0 R /AcroForm 5 0 R >>",
            [13] = "<< /Type /Pages /Kids [14 0 R] /Count 1 >>",
            [14] = "<< /Type /Page /Parent 13 0 R /MediaBox [0 0 300 300] /Contents 15 0 R >>",
            [15] = "<< /Length 30 >>\nstream\nBT /F1 12 Tf (Evil page) Tj ET\nendstream",
        }, 16, root: 12);
        PdfSignatureVerification v = AssertModified(attacked);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 12 && !c.Allowed && c.Description.Contains("catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void IsaTrailerPointsAtACatalogWithoutTheFormReportsNoSignatureAtAll()
    {
        // The signature is no longer part of the current document: nothing is found, and a report with no signatures is never valid.
        byte[] attacked = SignedPdf.Append(Signed(), new Dictionary<int, string>
        {
            [12] = "<< /Type /Catalog /Pages 2 0 R >>",
        }, 13, root: 12);
        using var doc = PdfDocument.Open(attacked);
        PdfSignatureReport report = doc.VerifySignatures(Pki.Trusting());
        Assert.False(report.HasSignatures);
        Assert.False(report.AllValid);
    }

    [Fact]
    public void IsaUpdateInsertedBeforeTheSignedRevisionEnds()
    {
        // An update is placed inside the signed second range and the range is stretched to cover it: the digest no longer fits.
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        string update = "\n12 0 obj\n<< /Evil true >>\nendobj\nxref\n12 1\n0000000009 00000 n \ntrailer\n<< /Size 13 /Root 1 0 R >>\nstartxref\n9\n%%EOF\n";
        byte[] attacked = WithRange(Insert(file, c + 20, update), $"[0 {b} {c} {d + update.Length}]");
        PdfSignatureVerification v = Check(attacked);
        Assert.Equal(PdfCheckStatus.Invalid, v.Integrity);
        AssertRejected(v);
    }

    [Fact]
    public void IsaCertifyingSignatureDictionaryRewrittenToRaiseThePermission()
    {
        // P=1 forbids everything; the update rewrites the signature dictionary with P=3 and adds an annotation.
        byte[] file = Certified(1);
        string t = Latin(file);
        int s = t.IndexOf("6 0 obj", StringComparison.Ordinal);
        string dict = t[(t.IndexOf("<<", s, StringComparison.Ordinal))..t.IndexOf("\nendobj", s, StringComparison.Ordinal)];
        string raised = dict.Replace("/P 1", "/P 3", StringComparison.Ordinal);
        byte[] attacked = SignedPdf.Append(file, new Dictionary<int, string>
        {
            [6] = raised,
            [3] = PageBody.Replace("{0}", "4 0 R 8 0 R 9 0 R 10 0 R 12 0 R", StringComparison.Ordinal),
            [12] = "<< /Type /Annot /Subtype /Text /Rect [10 10 50 50] /Contents (x) >>",
        }, 13);
        // The dictionary the reader now finds lies outside the signed bytes, so the byte range is refused.
        PdfSignatureVerification v = Check(attacked);
        Assert.NotEqual(PdfByteRangeStatus.Valid, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void IsaCatalogRewrittenToDropTheCertification()
    {
        byte[] file = Certified(1);
        byte[] attacked = SignedPdf.Append(file, new Dictionary<int, string> { [1] = "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>" }, 11);
        PdfSignatureVerification v = Check(attacked);
        Assert.Equal(1, v.DocMdpPermission);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 1 && !c.Allowed);
        AssertRejected(v);
    }

    // ---- 3. Signature wrapping -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void SwaSecondRangeStartsInsideTheFirst(int offsetFromB)
    {
        byte[] file = Signed();
        (long b, long _, long d) = Range(file);
        PdfSignatureVerification v = Check(WithRange(file, $"[0 {b} {b + offsetFromB} {d}]"));
        Assert.Equal(PdfByteRangeStatus.OverlapOrGap, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void SwaSecondRangeStartsEarlierThanTheEndOfTheContentsString()
    {
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        PdfSignatureVerification v = Check(WithRange(file, $"[0 {b} {c - 10} {d + 10}]"));
        Assert.Equal(PdfByteRangeStatus.ContentsMismatch, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void SwaFirstRangeShortenedExtraBytesFallInTheGap()
    {
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        PdfSignatureVerification v = Check(WithRange(file, $"[0 {b - 30} {c} {d}]"));
        Assert.Equal(PdfByteRangeStatus.ContentsMismatch, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void SwaExtraObjectsHiddenInTheExcludedArea()
    {
        // The attacker adds objects after the /Contents string and moves the second range past them. The signature stays
        // mathematically valid (the signed bytes are unchanged); the gap now holds more than the signature.
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        const string hidden = "\n7 0 obj\n<< /Length 38 >>\nstream\nBT /F1 12 Tf 20 200 Td (Pay 9999) Tj ET\nendstream\nendobj\n";
        byte[] attacked = WithRange(Insert(file, c, hidden), $"[0 {b} {c + hidden.Length} {d}]");
        PdfSignatureVerification v = Check(attacked);
        Assert.Equal(PdfByteRangeStatus.ContentsMismatch, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void SwaSignedContentMovedToTheEndBehindANewXref()
    {
        // New objects, a new cross-reference section and trailer are put between the signature and the signed rest, and the
        // range is moved to skip them. This is the classic wrapping layout.
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        const string front = "\n7 0 obj\n<< /Length 38 >>\nstream\nBT /F1 12 Tf 20 200 Td (Pay 9999) Tj ET\nendstream\nendobj\nxref\n7 1\n0000000009 00000 n \ntrailer\n<< /Size 11 /Root 1 0 R >>\nstartxref\n9\n%%EOF\n";
        byte[] attacked = WithRange(Insert(file, c, front), $"[0 {b} {c + front.Length} {d}]");
        PdfSignatureVerification v = Check(attacked);
        Assert.Equal(PdfByteRangeStatus.ContentsMismatch, v.ByteRange);
        AssertRejected(v);

        // And the honest-looking variant: the range is left alone, so the inserted bytes are hashed.
        PdfSignatureVerification unmoved = Check(Insert(file, c, front));
        Assert.Equal(PdfCheckStatus.Invalid, unmoved.Integrity);
        AssertRejected(unmoved);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(-3)]
    [InlineData(-2000)]
    public void SwaLengthOfTheSecondRangeDoesNotMatchTheFile(int delta)
    {
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        PdfSignatureVerification v = Check(WithRange(file, $"[0 {b} {c} {d + delta}]"));
        Assert.NotEqual(PdfByteRangeStatus.Valid, v.ByteRange);
        AssertRejected(v);
    }

    [Fact]
    public void SwaTwoSecondaryRangesAreNotAccepted()
    {
        byte[] file = Signed();
        (long b, long c, long d) = Range(file);
        PdfSignatureVerification v = Check(WithRange(file, $"[0 {b} {c} {d / 2} {c + (d / 2)} {d - (d / 2)}]"));
        Assert.Equal(PdfByteRangeStatus.Malformed, v.ByteRange);
        AssertRejected(v);
    }

    // ---- 4. Shadow attacks and certification attacks -------------------------------------------------------------------

    private static string PageWith(string annots) => PageBody.Replace("{0}", annots, StringComparison.Ordinal);

    private const string AppearanceStream = "<< /Type /XObject /Subtype /Form /BBox [0 0 100 40] /Length 38 >>\nstream\nBT /F1 14 Tf 2 10 Td (Pay 9999) Tj ET\nendstream";

    [Fact]
    public void ShadowReplaceByFormFieldValueIsAllowedAtLevel2AndListed()
    {
        // Advisory: the specification lets a certified document's fields be filled, and a changed value changes what is shown.
        byte[] file = Certified(2);
        using var editable = PdfDocument.Open(file, new PdfOpenOptions { AllowEditing = true });
        editable.Form!.FindField("Name")!.SetValue("Pay 9999");
        using var ms = new MemoryStream();
        editable.SaveIncremental(ms);
        PdfSignatureVerification v = Check(ms.ToArray());
        Assert.True(v.IsValid);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, v.Coverage);
        Assert.Contains(v.Changes, static c => c.Category == PdfChangeCategory.FormFieldValue && c.Allowed && c.ObjectNumber == 8);

        using var after = PdfDocument.Open(ms.ToArray());
        Assert.Equal("Pay 9999", after.Form!.FindField("Name")!.Value);
        Assert.Equal("initial", after.Revisions[0].GetObject(8) is PdfDictionary d ? d.GetString("V")?.GetText() : null);
    }

    [Fact]
    public void ShadowReplaceByFormFieldValueIsRefusedAtLevel1()
    {
        byte[] file = Certified(1);
        using var editable = PdfDocument.Open(file, new PdfOpenOptions { AllowEditing = true });
        editable.Form!.FindField("Name")!.SetValue("Pay 9999");
        using var ms = new MemoryStream();
        editable.SaveIncremental(ms);
        PdfSignatureVerification v = Check(ms.ToArray());
        AssertRejected(v);
        Assert.Contains(v.Changes, static c => c.Category == PdfChangeCategory.FormFieldValue && !c.Allowed);
    }

    [Fact]
    public void ShadowReplaceByPreparedAppearanceIsAllowedAtLevel2AndListed()
    {
        // An unused appearance stream was put in the file before signing; the update points a field's /AP at it. Allowed by the
        // level, so only advisory: the change is in the list under the field's object.
        var o = new SignedPdf.Options { ExtraObjects = new Dictionary<int, string> { [14] = AppearanceStream } };
        byte[] file = Certified(2, o);
        byte[] attacked = SignedPdf.Append(file, new Dictionary<int, string>
        {
            [8] = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [10 100 100 120] /P 3 0 R /F 4 /V (initial) /DA (/Helv 10 Tf 0 g) /AP << /N 14 0 R >> >>",
        }, 15);
        PdfSignatureVerification v = Check(attacked);
        Assert.True(v.IsValid);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 8 && c.Category == PdfChangeCategory.FormFieldValue && c.Allowed);
    }

    [Fact]
    public void ShadowHideByPreparedAppearanceOfAnotherKindIsRefusedWhenTheKindIsNotAField()
    {
        // The same trick on a non-field annotation's appearance is not a form fill and is refused at level 2.
        var o = new SignedPdf.Options
        {
            ExtraObjects = new Dictionary<int, string>
            {
                [14] = AppearanceStream,
                [15] = "<< /Type /Annot /Subtype /Stamp /Rect [10 200 110 240] /F 2 /AP << /N 14 0 R >> >>",
            },
        };
        byte[] file = Certified(2, o);
        byte[] attacked = SignedPdf.Append(file, new Dictionary<int, string>
        {
            [15] = "<< /Type /Annot /Subtype /Stamp /Rect [10 200 110 240] /F 4 /AP << /N 14 0 R >> >>",
        }, 16);
        PdfSignatureVerification v = Check(attacked);
        AssertRejected(v);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 15 && !c.Allowed);
    }

    [Fact]
    public void ShadowHideAndReplaceByPageContentUpdateIsRefused()
    {
        byte[] file = Certified(3);
        byte[] attacked = SignedPdf.Append(file, new Dictionary<int, string> { [7] = "<< /Length 30 >>\nstream\nBT /F1 12 Tf (Replaced) Tj ET\nendstream" }, 11);
        PdfSignatureVerification v = Check(attacked);
        AssertRejected(v);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 7 && c.Category == PdfChangeCategory.PageContent && !c.Allowed);
    }

    [Fact]
    public void XrefSwitchingPreparedObjectIsActivatedByANewTable()
    {
        // Object 7 is defined twice in the signed region; only the first is in the signed table. A later table that points
        // object 7 at the second definition contains no new object bodies at all.
        byte[] file = Signed(new SignedPdf.Options { PreparedContent = true });
        string t = Latin(file);
        int first = t.IndexOf("7 0 obj", StringComparison.Ordinal);
        int second = t.IndexOf("7 0 obj", first + 1, StringComparison.Ordinal);
        Assert.True(second > first);
        byte[] attacked = SignedPdf.AppendXrefOnly(file, new Dictionary<int, long> { [7] = second }, 11);

        using var doc = PdfDocument.Open(attacked);
        Assert.Contains("Prepared text", Encoding.Latin1.GetString(((PdfStream)doc.GetObject(7)!).GetDecodedBytes().Span), StringComparison.Ordinal);
        PdfSignatureVerification v = doc.GetSignatures()[0].Verify(Pki.Trusting());
        AssertRejected(v);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionDisallowedChanges, v.Coverage);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 7 && c.Category == PdfChangeCategory.PageContent && !c.Allowed);
    }

    // Evil annotation attack ---------------------------------------------------------------------------------------------

    private static byte[] WithAnnotation(byte[] file, string annotationBody)
    {
        return SignedPdf.Append(file, new Dictionary<int, string>
        {
            [3] = PageWith("4 0 R 8 0 R 9 0 R 10 0 R 12 0 R"),
            [12] = annotationBody,
        }, 14);
    }

    private const string EvilAnnotation = "<< /Type /Annot /Subtype /Stamp /Rect [10 200 110 240] /F 4 /AP << /N 13 0 R >> >>";

    private const string EvilWidget = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Evil) /V (Pay 9999) /Rect [10 200 110 240] /P 3 0 R /F 4 /AP << /N 13 0 R >> >>";

    private const string UnsignedSignatureField = "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sneaky) /Rect [10 200 110 240] /P 3 0 R /F 4 /AP << /N 13 0 R >> >>";

    private static SignedPdf.Options WithAppearanceObject() => new() { ExtraObjects = new Dictionary<int, string> { [13] = AppearanceStream } };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EaaAnnotationAddedBelowLevel3IsDetected(int level)
    {
        PdfSignatureVerification v = Check(WithAnnotation(Certified(level, WithAppearanceObject()), EvilAnnotation));
        AssertRejected(v);
        Assert.Contains(v.Changes, static c => c.Category == PdfChangeCategory.Annotation && !c.Allowed);
    }

    [Fact]
    public void EaaAnnotationAddedAtLevel3IsAllowedAndListed()
    {
        // Advisory: level 3 permits annotations, including one that covers the page. The result lists it as an allowed annotation change.
        PdfSignatureVerification v = Check(WithAnnotation(Certified(3, WithAppearanceObject()), EvilAnnotation));
        Assert.True(v.IsValid);
        Assert.Equal(PdfSignatureCoverage.EarlierRevisionAllowedChanges, v.Coverage);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 12 && c.Category == PdfChangeCategory.Annotation && c.Allowed);
    }

    [Fact]
    public void EaaAnnotationDisguisedAsAFormWidgetIsAllowedAtLevel2AndListedAsAFieldChange()
    {
        // Advisory: a new form field (with an appearance that draws text) is a permitted form change at level 2.
        PdfSignatureVerification v = Check(WithAnnotation(Certified(2, WithAppearanceObject()), EvilWidget));
        Assert.True(v.IsValid);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 12 && c.Category == PdfChangeCategory.FormFieldValue && c.Allowed);
    }

    [Fact]
    public void EaaAnnotationDisguisedAsAFormWidgetIsDetectedAtLevel1()
    {
        PdfSignatureVerification v = Check(WithAnnotation(Certified(1, WithAppearanceObject()), EvilWidget));
        AssertRejected(v);
    }

    [Fact]
    public void SsaUnsignedSignatureFieldWithAnAppearanceIsAllowedAtLevel2AndListed()
    {
        // Advisory: adding a signature field is a permitted form change at level 2; nothing is signed, but the field draws.
        PdfSignatureVerification v = Check(WithAnnotation(Certified(2, WithAppearanceObject()), UnsignedSignatureField));
        Assert.True(v.IsValid);
        Assert.Contains(v.Changes, static c => c.ObjectNumber == 12 && c.Allowed);
        using var doc = PdfDocument.Open(WithAnnotation(Certified(2, WithAppearanceObject()), UnsignedSignatureField));
        Assert.Single(doc.GetSignatures());
    }

    [Fact]
    public void SsaUnsignedSignatureFieldWithAnAppearanceIsDetectedAtLevel1()
    {
        PdfSignatureVerification v = Check(WithAnnotation(Certified(1, WithAppearanceObject()), UnsignedSignatureField));
        AssertRejected(v);
    }

    [Fact]
    public void SsaSignatureAddedAtLevel1IsDetected()
    {
        byte[] file = Certified(1);
        byte[] two = SignedPdf.AppendSecondSignature(file, digest => CmsKit.Build(digest(System.Security.Cryptography.HashAlgorithmName.SHA256), Pki.EcSigner, [Pki.EcSigner, Pki.Intermediate], new CmsSpec { Scheme = SigScheme.Ecdsa }));
        using var doc = PdfDocument.Open(two);
        PdfSignatureVerification v = doc.GetSignatures()[0].Verify(Pki.Trusting());
        AssertRejected(v);
        Assert.Contains(v.Changes, static c => c.Category == PdfChangeCategory.NewSignature && !c.Allowed);
    }

    // ---- 5. Certificate checks -----------------------------------------------------------------------------------------

    private static PdfSignatureVerification SignedBy(System.Security.Cryptography.X509Certificates.X509Certificate2 signer, PdfSignatureVerifyOptions? options = null)
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), signer, new CmsSpec(), [signer, Pki.Intermediate]);
        return Check(file, options);
    }

    [Fact]
    public void CertKeyUsageWithoutDigitalSignatureIsRejectedByDefault()
    {
        PdfSignatureVerification v = SignedBy(Pki.KeyEnciphermentSigner);
        Assert.Equal(PdfCheckStatus.Valid, v.SignatureCheck);
        Assert.Equal(PdfChainStatus.Trusted, v.Chain);
        Assert.Equal(PdfCheckStatus.Invalid, v.KeyUsage);
        Assert.Contains(v.Warnings, static w => w.Contains("key usage", StringComparison.Ordinal));
        AssertRejected(v);
    }

    [Fact]
    public void CertExtendedKeyUsageForTlsOnlyIsRejectedByDefault()
    {
        PdfSignatureVerification v = SignedBy(Pki.ServerAuthSigner);
        Assert.Equal(PdfCheckStatus.Invalid, v.KeyUsage);
        AssertRejected(v);
    }

    [Fact]
    public void CertTimeStampAuthorityCertificateCannotSignDocuments()
    {
        PdfSignatureVerification v = SignedBy(Pki.Tsa);
        Assert.Equal(PdfCheckStatus.Invalid, v.KeyUsage);
        AssertRejected(v);
    }

    [Fact]
    public void CertKeyUsagePolicyCanBeRelaxedByTheCallerAndStaysReported()
    {
        var relaxed = new PdfSignatureVerifyOptions { TrustAnchors = [Pki.RootAnchor], RequireSigningKeyUsage = false };
        PdfSignatureVerification v = SignedBy(Pki.KeyEnciphermentSigner, relaxed);
        Assert.Equal(PdfCheckStatus.Invalid, v.KeyUsage);
        Assert.True(v.IsValid);
        Assert.Contains(v.Warnings, static w => w.Contains("allowed by the options", StringComparison.Ordinal));
    }

    [Fact]
    public void CertKeyUsageAcceptsDigitalSignatureAndTheEmailPurpose()
    {
        Assert.True(SignedBy(Pki.RsaSigner).IsValid);
        PdfSignatureVerification mail = SignedBy(Pki.EmailSigner);
        Assert.Equal(PdfCheckStatus.Valid, mail.KeyUsage);
        Assert.True(mail.IsValid, string.Join("; ", mail.Warnings));
    }

    [Fact]
    public void CertTimestampFromACertificateWithoutTheTimeStampingPurposeIsInvalid()
    {
        // The token's signature is fine, but its signer is an ordinary signing certificate, not a time-stamp authority.
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), Pki.RsaSigner,
            new CmsSpec { Timestamp = true, TimestampAuthority = Pki.EmailSigner });
        PdfSignatureVerification v = Check(file);
        Assert.NotNull(v.Timestamp);
        Assert.False(v.Timestamp.IsValid);
        AssertRejected(v);
    }
}
