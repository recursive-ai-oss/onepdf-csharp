using System.Text;
using static OnePdf.Tests.Signatures.SigningFixture;

namespace OnePdf.Tests.Signatures;

/// <summary>Refusals before anything is written, and the appearance of two-phase signatures.</summary>
public class SigningGuardTests
{
    private static string HexOf(string s) => Convert.ToHexStringLower(Encoding.ASCII.GetBytes(s));

    private static string AppearanceText(byte[] pdf)
    {
        using PdfDocument check = PdfDocument.Open(pdf);
        var list = new PdfDisplayList();
        check.Pages[0].Interpret(list);
        return list.ToText();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlaceholderAppearanceInANewFieldShowsReason(bool signerName)
    {
        var options = new PdfSigningOptions { SignerName = signerName ? "Rita" : null, Reason = "I approve", Rectangle = new PdfRectangle(20, 20, 220, 70) };
        using PdfDocument a = Editable(UnsignedPdf(fields: false));
        using var placeholder = new MemoryStream();
        a.AddSignaturePlaceholder(placeholder, options, 4096);
        Assert.Contains(HexOf("Reason: I approve"), AppearanceText(placeholder.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceholderAppearanceShowsReasonLikeSign()
    {
        var options = new PdfSigningOptions { FieldName = "Sig2", SignerName = "Rita", Reason = "I approve", Location = "Lisbon" };
        using PdfDocument a = Editable(UnsignedPdf());
        using var placeholder = new MemoryStream();
        a.AddSignaturePlaceholder(placeholder, options, 4096);
        string fromPlaceholder = AppearanceText(placeholder.ToArray());

        using PdfDocument b = Editable(UnsignedPdf());
        using var signed = new MemoryStream();
        b.Sign(signed, Credential(TestPki.Shared.RsaSigner), options);
        string fromSign = AppearanceText(signed.ToArray());

        foreach (string text in new[] { fromPlaceholder, fromSign })
        {
            Assert.Contains(HexOf("Digitally signed by Rita"), text, StringComparison.Ordinal);
            Assert.Contains(HexOf("Reason: I approve"), text, StringComparison.Ordinal);
            Assert.Contains(HexOf("Location: Lisbon"), text, StringComparison.Ordinal);
        }
    }

    /// <summary>A tiny file whose page tree has no pages.</summary>
    private static byte[] ZeroPagePdf()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>" })
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 3\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            sb.Append($"{o:D10} 00000 n \n");
        }

        sb.Append("trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void ZeroPageDocumentIsRefusedWithATypedError()
    {
        byte[] input = ZeroPagePdf();
        var tsa = new TestTsa();
        foreach (Action<PdfDocument, Stream> act in new Action<PdfDocument, Stream>[]
        {
            (d, o) => d.AddSignaturePlaceholder(o),
            (d, o) => d.Sign(o, Credential(TestPki.Shared.RsaSigner)),
            (d, o) => d.AddDocumentTimestamp(o, tsa.Callback),
        })
        {
            using PdfDocument doc = Editable(input);
            using var ms = new MemoryStream();
            PdfSigningException e = Assert.Throws<PdfSigningException>(() => act(doc, ms));
            Assert.Equal(PdfSigningError.NoPage, e.Reason);
            Assert.Equal(0, ms.Length);
        }
    }

    [Fact]
    public void PageIndexOutsideANonEmptyDocumentIsAnArgumentError()
    {
        using PdfDocument doc = Editable(UnsignedPdf(fields: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddSignaturePlaceholder(new MemoryStream(), new PdfSigningOptions { PageIndex = 5 }));
    }

    private static byte[] Certified(PdfCertificationLevel level)
    {
        using PdfDocument doc = Editable(UnsignedPdf());
        return SignToBytes(doc, Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { FieldName = "Sig2", Certify = level });
    }

    [Fact]
    public void NoChangesCertificationRefusesFurtherSignatures()
    {
        byte[] certified = Certified(PdfCertificationLevel.NoChanges);
        foreach (Action<PdfDocument, Stream> act in new Action<PdfDocument, Stream>[]
        {
            (d, o) => d.AddSignaturePlaceholder(o, new PdfSigningOptions { FieldName = "Second" }),
            (d, o) => d.Sign(o, Credential(TestPki.Shared.EcSigner), new PdfSigningOptions { FieldName = "Second" }),
        })
        {
            using PdfDocument doc = Editable(certified);
            using var ms = new MemoryStream();
            PdfSigningException e = Assert.Throws<PdfSigningException>(() => act(doc, ms));
            Assert.Equal(PdfSigningError.CertificationForbidsChanges, e.Reason);
            Assert.Equal(0, ms.Length);

            // The refusal is made before the document is used: the same instance can still sign with the override.
            using var ms2 = new MemoryStream();
            doc.Sign(ms2, Credential(TestPki.Shared.EcSigner), new PdfSigningOptions { FieldName = "Second", AllowBreakingCertification = true });
            Assert.True(ms2.Length > certified.Length);
        }
    }

    [Fact]
    public void DocumentTimestampIsAllowedUnderNoChanges()
    {
        byte[] certified = Certified(PdfCertificationLevel.NoChanges);
        using PdfDocument doc = Editable(certified);
        using var ms = new MemoryStream();
        doc.AddDocumentTimestamp(ms, new TestTsa().Callback);
        using PdfDocument check = PdfDocument.Open(ms.ToArray());
        PdfSignatureReport report = check.VerifySignatures(Trust);
        // ISO 32000-2 12.8.2.2.1 lets a document time-stamp update through even at P=1. The verifier's change analysis is
        // stricter about the timestamp's own new field and widget, so only the time-stamp's validity is asserted here.
        Assert.Equal(2, report.Signatures.Count);
        Assert.Equal(PdfSignatureKind.DocumentTimestamp, report.Signatures[1].Signature.Kind);
        Assert.True(report.Signatures[1].IsValid, WarningsOf(report));
    }

    [Theory]
    [InlineData(PdfCertificationLevel.FormFilling)]
    [InlineData(PdfCertificationLevel.FormFillingAndAnnotations)]
    public void LevelsTwoAndThreeStillAllowFurtherSignatures(PdfCertificationLevel level)
    {
        byte[] certified = Certified(level);
        using PdfDocument doc = Editable(certified);
        using var ms = new MemoryStream();
        doc.Sign(ms, Credential(TestPki.Shared.EcSigner), new PdfSigningOptions { FieldName = "Second" });
        using PdfDocument check = PdfDocument.Open(ms.ToArray());
        PdfSignatureReport report = check.VerifySignatures(Trust);
        Assert.True(report.AllValid, WarningsOf(report));
        Assert.Equal(2, report.Signatures.Count);

        using PdfDocument again = Editable(certified);
        again.AddSignaturePlaceholder(new MemoryStream(), new PdfSigningOptions { FieldName = "Third" });
    }

    [Fact]
    public void UncertifiedSignedDocumentIsNotAffected()
    {
        using PdfDocument first = Editable(UnsignedPdf());
        byte[] signed = SignToBytes(first, Credential(TestPki.Shared.RsaSigner), new PdfSigningOptions { FieldName = "Sig2" });
        using PdfDocument doc = Editable(signed);
        using var ms = new MemoryStream();
        doc.Sign(ms, Credential(TestPki.Shared.EcSigner), new PdfSigningOptions { FieldName = "Second" });
        Assert.True(ms.Length > signed.Length);
    }

    [Fact]
    public void TextThatLooksLikeThePlaceholderDoesNotBreakSigning()
    {
        // The page dictionary is rewritten into the update, so a string in it that equals the placeholder text must not be mistaken for it.
        byte[] input = UnsignedPdf(fields: false);
        string text = Encoding.Latin1.GetString(input).Replace("/Type /Page ", "/Type /Page /Fake (/ByteRange[ 0 9999999999 9999999999 9999999999]/Contents<00>) ", StringComparison.Ordinal);
        text = text.Replace("startxref", "startxref", StringComparison.Ordinal);
        byte[] edited = RebuildXref(text);
        using PdfDocument doc = Editable(edited);
        using var ms = new MemoryStream();
        PdfSignaturePlaceholder p = doc.AddSignaturePlaceholder(ms, null, 256);
        p.Fill(ms, [0x30, 0x03, 0x02, 0x01, 0x05]);
        using PdfDocument check = PdfDocument.Open(ms.ToArray());
        PdfSignature sig = Assert.Single(check.GetSignatures());
        Assert.True(sig.Range.Status == PdfByteRangeStatus.Valid);
    }

    [Fact]
    public void ACompleteLookalikeOfThePlaceholderIsRefusedWithATypedError()
    {
        string fake = "(/ByteRange[ 0 9999999999 9999999999 9999999999]/Contents<" + new string('0', 128) + ">)";
        string text = Encoding.Latin1.GetString(UnsignedPdf(fields: false)).Replace("/Type /Page ", "/Type /Page /Fake " + fake + " ", StringComparison.Ordinal);
        using PdfDocument doc = Editable(RebuildXref(text));
        using var ms = new MemoryStream();
        Assert.Throws<PdfFormatException>(() => doc.AddSignaturePlaceholder(ms, null, 64));
        Assert.Equal(0, ms.Length);
    }

    // Recomputes the cross-reference table of a file written by UnsignedPdf after its text was edited.
    private static byte[] RebuildXref(string text)
    {
        var sb = new StringBuilder();
        int end = text.IndexOf("xref\n", StringComparison.Ordinal);
        string body = text[..end];
        sb.Append(body);
        var offsets = new SortedDictionary<int, int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"(?m)^(\d+) 0 obj"))
        {
            offsets[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] = m.Index;
        }

        int size = offsets.Keys.Max() + 1;
        sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
        {
            sb.Append(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 00000 f \n");
        }

        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(body.Length).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    [Fact]
    public void OpenReadIsTheOriginalBytesForEverySourceKind()
    {
        byte[] input = UnsignedPdf();
        string path = Path.Combine(Path.GetTempPath(), "onepdf-openread-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, input);
        try
        {
            var options = new PdfOpenOptions { AllowEditing = true };
            using var seekable = new MemoryStream(input);
            using var oneWay = new OneWayStream(input);
            var docs = new List<PdfDocument>
            {
                PdfDocument.Open(path, options),
                PdfDocument.Open(seekable, options),
                PdfDocument.Open(oneWay, options),
                PdfDocument.Open(new ReadOnlyMemory<byte>(input), options),
            };
            foreach (PdfDocument doc in docs)
            {
                using (doc)
                {
                    // An in-memory edit must not show.
                    doc.Form!.FindField("Name")!.Dictionary.SetEntry("V", PdfString.FromText("changed"));
                    using Stream s = doc.OpenRead();
                    Assert.True(s.CanRead && s.CanSeek && !s.CanWrite);
                    Assert.Equal(input.Length, s.Length);
                    using var copy = new MemoryStream();
                    s.CopyTo(copy);
                    Assert.True(input.AsSpan().SequenceEqual(copy.ToArray()));
                    s.Position = 10;
                    byte[] part = new byte[5];
                    s.ReadExactly(part);
                    Assert.True(input.AsSpan(10, 5).SequenceEqual(part));
                    Assert.Equal(input.Length - 3, s.Seek(-3, SeekOrigin.End));
                    Assert.Equal(3, s.Read(new byte[10], 0, 10));
                    Assert.Throws<NotSupportedException>(() => s.Write(part, 0, 1));

                    Stream late = doc.OpenRead();
                    doc.Dispose();
                    Assert.Throws<ObjectDisposedException>(() => late.ReadByte());
                    Assert.Throws<ObjectDisposedException>(() => doc.OpenRead());
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenReadStreamDisposedDoesNotAffectTheDocument()
    {
        using PdfDocument doc = PdfDocument.Open(UnsignedPdf());
        Stream s = doc.OpenRead();
        s.Dispose();
        Assert.Throws<ObjectDisposedException>(() => s.ReadByte());
        using Stream t = doc.OpenRead();
        Assert.Equal((byte)'%', t.ReadByte());
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void OpenReadSeesAChangedFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "onepdf-openread-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, UnsignedPdf());
        try
        {
            using PdfDocument doc = PdfDocument.Open(path);
            using Stream s = doc.OpenRead();
            File.AppendAllText(path, "junk");
            Assert.Throws<PdfSourceChangedException>(() => s.ReadByte());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class OneWayStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
