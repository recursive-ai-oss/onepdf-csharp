using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OnePdf.Tests.Signatures;

/// <summary>Assembles signed PDF files for tests: a placeholder signature dictionary is written, then its byte range and CMS are patched in.</summary>
internal static class SignedPdf
{
    public const int ContentsBytes = 12000;

    private const string RangePlaceholder = "[0 0000000000 0000000000 0000000000]";

    public sealed class Options
    {
        public string SubFilter { get; init; } = "adbe.pkcs7.detached";

        /// <summary>When set, the first signature certifies the document with this DocMDP level.</summary>
        public int? CertifyLevel { get; init; }

        /// <summary>Extra entries for the signature dictionary of the first signature (e.g. a FieldMDP reference).</summary>
        public string ExtraSigEntries { get; init; } = string.Empty;

        /// <summary>Extra entries for the first signature field (e.g. /Lock).</summary>
        public string ExtraFieldEntries { get; init; } = string.Empty;

        public bool TextFields { get; init; } = true;
    }

    /// <summary>
    /// One-page file: catalog 1, pages 2, page 3, signature field 4 (signature dict 6), AcroForm 5, content 7, text field "Name" 8,
    /// text field "Other" 9 and an unsigned signature field "Sig2" 10. Fields 8 to 10 exist only when requested.
    /// </summary>
    public static byte[] BaseFile(Options o, out int sigObject)
    {
        sigObject = 6;
        var sb = new StringBuilder();
        var offsets = new SortedDictionary<int, int>();
        sb.Append("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n");

        void Obj(int n, string body)
        {
            offsets[n] = sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        string perms = o.CertifyLevel is not null ? " /Perms << /DocMDP 6 0 R >>" : string.Empty;
        Obj(1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R{perms} >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        string annots = o.TextFields ? "4 0 R 8 0 R 9 0 R 10 0 R" : "4 0 R";
        Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 7 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Annots [{annots}] >>");
        Obj(4, $"<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /Rect [10 10 100 50] /P 3 0 R /F 132 /V 6 0 R {o.ExtraFieldEntries} >>");
        string fields = o.TextFields ? "4 0 R 8 0 R 9 0 R 10 0 R" : "4 0 R";
        Obj(5, $"<< /Fields [{fields}] /SigFlags 3 /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
        string reference = o.CertifyLevel is { } lvl
            ? $"/Reference [ << /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P {lvl} /V /1.2 >> >> ]"
            : string.Empty;
        Obj(6, $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /{o.SubFilter} /ByteRange {RangePlaceholder} /Contents <{new string('0', ContentsBytes * 2)}> /Name (Rita RSA) /Reason (Testing) /Location (Lab) /ContactInfo (rita@example.test) /M (D:20260101120000Z) {reference} {o.ExtraSigEntries} >>");
        const string content = "BT /F1 12 Tf 20 200 Td (Hello signed) Tj ET";
        Obj(7, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        if (o.TextFields)
        {
            Obj(8, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Name) /Rect [10 100 100 120] /P 3 0 R /F 4 /V (initial) /DA (/Helv 10 Tf 0 g) >>");
            Obj(9, "<< /Type /Annot /Subtype /Widget /FT /Tx /T (Other) /Rect [10 130 100 150] /P 3 0 R /F 4 /DA (/Helv 10 Tf 0 g) >>");
            Obj(10, "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig2) /Rect [110 10 200 50] /P 3 0 R /F 132 >>");
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

    /// <summary>
    /// Fills in the signature whose placeholder dictionary is the last in <paramref name="file"/> (or the one with object number
    /// <paramref name="sigObject"/>): computes the byte range and calls <paramref name="makeContents"/> with the digest function.
    /// </summary>
    public static byte[] Sign(byte[] file, Func<Func<HashAlgorithmName, byte[]>, byte[]> makeContents, bool tamperRange = false)
    {
        string text = Encoding.Latin1.GetString(file);
        int rangeAt = text.LastIndexOf(RangePlaceholder, StringComparison.Ordinal);
        int contentsAt = text.IndexOf("/Contents <", rangeAt, StringComparison.Ordinal) + "/Contents ".Length;
        int holeStart = contentsAt;
        int holeEnd = text.IndexOf('>', holeStart) + 1;
        long b = holeStart;
        long c = holeEnd;
        long d = file.Length - holeEnd;
        byte[] patched = (byte[])file.Clone();
        string range = $"[0 {b} {c} {d}]".PadRight(RangePlaceholder.Length);
        Encoding.Latin1.GetBytes(range).CopyTo(patched, rangeAt);

        byte[] Digest(HashAlgorithmName name)
        {
            using IncrementalHash h = IncrementalHash.CreateHash(name);
            h.AppendData(patched, 0, holeStart);
            h.AppendData(patched, holeEnd, patched.Length - holeEnd);
            return h.GetHashAndReset();
        }

        byte[] contents = makeContents(Digest);
        if (contents.Length > ContentsBytes)
        {
            throw new InvalidOperationException("The signature does not fit its placeholder.");
        }

        string hex = Convert.ToHexString(contents).PadRight(ContentsBytes * 2, '0');
        Encoding.Latin1.GetBytes(hex).CopyTo(patched, holeStart + 1);
        if (tamperRange)
        {
            string shifted = $"[0 {b} {c + 1} {d - 1}]".PadRight(RangePlaceholder.Length);
            Encoding.Latin1.GetBytes(shifted).CopyTo(patched, rangeAt);
        }

        return patched;
    }

    public static byte[] SignWith(byte[] file, X509Certificate2 signer, CmsSpec spec, IReadOnlyList<X509Certificate2>? chain = null, bool tamperRange = false)
    {
        TestPki pki = TestPki.Shared;
        chain ??= [signer, pki.Intermediate];
        return Sign(file, digest => CmsKit.Build(digest(spec.Hash), signer, chain, spec), tamperRange);
    }

    /// <summary>
    /// Appends an incremental update with these objects (full text of each, "n 0 obj ... endobj" added here). A signature placeholder
    /// among them can be signed afterwards with <see cref="Sign"/>.
    /// </summary>
    public static byte[] Append(byte[] file, IReadOnlyDictionary<int, string> objects, int size)
    {
        int prev = LastStartXref(file);
        var sb = new StringBuilder();
        sb.Append('\n');
        var offsets = new SortedDictionary<int, long>();
        foreach ((int n, string body) in objects.OrderBy(static kv => kv.Key))
        {
            offsets[n] = file.Length + sb.Length;
            sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        long xref = file.Length + sb.Length;
        sb.Append("xref\n");
        foreach ((int n, long off) in offsets)
        {
            sb.Append(n).Append(" 1\n").Append($"{off:D10} 00000 n \n");
        }

        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R /Prev ").Append(prev).Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return [.. file, .. Encoding.Latin1.GetBytes(sb.ToString())];
    }

    /// <summary>A second signature in an incremental update: sig dict object 11, field "Sig2" (object 10) now holds it.</summary>
    public static byte[] AppendSecondSignature(byte[] file, Func<Func<HashAlgorithmName, byte[]>, byte[]> makeContents, string extraSigEntries = "")
    {
        var objs = new Dictionary<int, string>
        {
            [10] = "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig2) /Rect [110 10 200 50] /P 3 0 R /F 132 /V 11 0 R >>",
            [11] = $"<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange {RangePlaceholder} /Contents <{new string('0', ContentsBytes * 2)}> /Name (Second) /M (D:20260102120000Z) {extraSigEntries} >>",
        };
        return Sign(Append(file, objs, 12), makeContents);
    }

    /// <summary>A document time-stamp in an incremental update (object 12, in a field "TS" object 13 added to the page).</summary>
    public static byte[] AppendDocumentTimestamp(byte[] file, DateTimeOffset time)
    {
        var objs = new Dictionary<int, string>
        {
            [12] = $"<< /Type /DocTimeStamp /Filter /Adobe.PPKLite /SubFilter /ETSI.RFC3161 /ByteRange {RangePlaceholder} /Contents <{new string('0', ContentsBytes * 2)}> >>",
            [13] = "<< /Type /Annot /Subtype /Widget /FT /Sig /T (TS) /Rect [0 0 0 0] /P 3 0 R /F 132 /V 12 0 R >>",
            [5] = "<< /Fields [4 0 R 8 0 R 9 0 R 10 0 R 13 0 R] /SigFlags 3 /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>",
        };
        return Sign(Append(file, objs, 14), digest => CmsKit.Timestamp(digest(HashAlgorithmName.SHA256), HashAlgorithmName.SHA256, time));
    }

    public static int LastStartXref(byte[] file)
    {
        string tail = Encoding.Latin1.GetString(file, Math.Max(0, file.Length - 80), Math.Min(80, file.Length));
        int at = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        string rest = tail[(at + 9)..].Trim();
        return int.Parse(rest.Split('\n')[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static PdfSignatureVerification VerifyOne(byte[] file, PdfSignatureVerifyOptions? options = null, int index = 0)
    {
        using var doc = PdfDocument.Open(file);
        IReadOnlyList<PdfSignature> sigs = doc.GetSignatures();
        return sigs[index].Verify(options ?? TestPki.Shared.Trusting());
    }
}
