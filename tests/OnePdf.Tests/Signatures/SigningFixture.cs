using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OnePdf.Asn1;

namespace OnePdf.Tests.Signatures;

/// <summary>Builds the documents, credentials and an in-process time-stamp authority the signing tests share.</summary>
internal static class SigningFixture
{
    /// <summary>One page with text fields "Name" and "Other" and an unsigned, visible signature field "Sig2". No signatures.</summary>
    public static byte[] UnsignedPdf(bool fields = true)
    {
        var sb = new StringBuilder();
        var offsets = new SortedDictionary<int, int>();
        sb.Append("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n");

        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        string form = fields ? " /AcroForm 5 0 R" : string.Empty;
        string annots = fields ? "/Annots [8 0 R 9 0 R 10 0 R]" : string.Empty;
        Obj(1, $"<< /Type /Catalog /Pages 2 0 R{form} >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 7 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> {annots} >>");
        const string content = "BT /F1 12 Tf 20 250 Td (Hello signing world) Tj ET";
        Obj(7, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        if (fields)
        {
            Obj(5, "<< /Fields [8 0 R 9 0 R 10 0 R] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
            Obj(8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [10 100 100 120] /P 3 0 R /F 4 /V (initial) /DA (/Helv 10 Tf 0 g) >>");
            Obj(9, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Other) /Rect [10 130 100 150] /P 3 0 R /F 4 /DA (/Helv 10 Tf 0 g) >>");
            Obj(10, "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig2) /Rect [110 10 260 70] /P 3 0 R /F 132 >>");
        }

        int size = offsets.Keys.Max() + 1;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(size).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i < size; i++)
        {
            sb.Append(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 00000 f \n");
        }

        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>] >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    public static PdfDocument Editable(byte[] data, string? password = null) =>
        PdfDocument.Open(data, new PdfOpenOptions { AllowEditing = true, Password = password });

    public static (X509Certificate2 Cert, PdfRsaPadding Padding) Key(string id)
    {
        TestPki pki = TestPki.Shared;
        return id switch
        {
            "rsa-pkcs1" => (pki.RsaSigner, PdfRsaPadding.Pkcs1),
            "rsa-pss" => (pki.RsaSigner, PdfRsaPadding.Pss),
            "ec256" => (pki.EcSigner, PdfRsaPadding.Pkcs1),
            "ec384" => (pki.Ec384Signer, PdfRsaPadding.Pkcs1),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    public static PdfSigningCredential Credential(X509Certificate2 cert) => PdfSigningCredential.FromCertificate(cert, [TestPki.Shared.Intermediate]);

    /// <summary>A time-stamp authority in the test process: answers a request with a TimeStampResp signed by the test authority.</summary>
    public sealed class TestTsa
    {
        public int Calls { get; private set; }

        public PdfTimestampRequest? Last { get; private set; }

        public bool WrongImprint { get; init; }

        public bool WrongNonce { get; init; }

        public bool DropNonce { get; init; }

        public bool Refuse { get; init; }

        public bool BareToken { get; init; }

        public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;

        public byte[] Answer(PdfTimestampRequest request)
        {
            Calls++;
            Last = request;
            if (Refuse)
            {
                return Der.Seq(Der.Seq(Der.Int(2)));
            }

            byte[] imprint = request.MessageImprint.ToArray();
            if (WrongImprint)
            {
                imprint[0] ^= 0x55;
            }

            byte[]? nonce = DropNonce ? null : request.Nonce.ToArray();
            if (WrongNonce && nonce is not null)
            {
                nonce[^1] ^= 0x33;
            }

            byte[] token = CmsKit.Timestamp(imprint, request.HashAlgorithm, Time, null, nonce);
            return BareToken ? token : Der.Seq(Der.Seq(Der.Int(0)), token);
        }

        public PdfTimestampCallback Callback => Answer;
    }

    public static PdfSignatureVerifyOptions Trust => TestPki.Shared.Trusting();

    public static byte[] SignToBytes(PdfDocument doc, PdfSigningCredential credential, PdfSigningOptions? options = null)
    {
        using var ms = new MemoryStream();
        doc.Sign(ms, credential, options);
        return ms.ToArray();
    }

    public static string WarningsOf(PdfSignatureReport report) =>
        string.Join(" | ", report.Signatures.Select(static s => $"{s.Signature.FieldName}: " + string.Join("; ", s.Warnings)));

    public static string ContentsOf(byte[] file, int index = 0)
    {
        using PdfDocument doc = PdfDocument.Open(file);
        PdfSignature sig = doc.GetSignatures()[index];
        Assert.True(Asn1Reader.TryRead(sig.Contents, out _, out int used, out _));
        return Convert.ToHexString(sig.Contents.Span[..used]);
    }
}
