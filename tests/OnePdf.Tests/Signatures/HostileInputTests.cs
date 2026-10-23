using OnePdf.Asn1;

namespace OnePdf.Tests.Signatures;

public class HostileInputTests
{
    [Fact]
    public void DeeplyNestedIndefiniteLengthsAreRefused()
    {
        byte[] data = new byte[200_000];
        for (int i = 0; i < data.Length; i += 2)
        {
            data[i] = 0x30;
            data[i + 1] = 0x80;
        }

        Assert.False(Asn1Reader.TryRead(data, out _, out _, out string? error));
        Assert.NotNull(error);
        Assert.False(CmsSignedData.TryParse(data, out _, out _));
    }

    [Theory]
    [InlineData(new byte[] { 0x30, 0x84, 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(new byte[] { 0x30, 0x85, 1, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x04, 0x82, 0x7F, 0xFF, 1 })]
    [InlineData(new byte[] { 0x30, 0x80, 0x04, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x04, 0x80, 0, 0 })]
    [InlineData(new byte[] { 0x1F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x30 })]
    [InlineData(new byte[] { })]
    public void MalformedElementsFailWithoutAllocatingOrThrowing(byte[] data)
    {
        Assert.False(Asn1Reader.TryRead(data, out _, out _, out _));
        Assert.False(CmsSignedData.TryParse(data, out _, out _));
    }

    [Fact]
    public void ManyShallowSiblingsInsideAnIndefiniteElementStayLinear()
    {
        var data = new List<byte> { 0x30, 0x80 };
        for (int i = 0; i < 300_000; i++)
        {
            data.AddRange([0x05, 0x00]);
        }

        data.AddRange([0, 0]);
        Assert.True(Asn1Reader.TryRead(data.ToArray(), out Asn1Element e, out int used, out _));
        Assert.Equal(data.Count, used);
        Assert.False(Asn1Reader.TryChildren(e, 1000, [], out _));
    }

    [Fact]
    public void ObjectIdentifiersAndTimesAreValidated()
    {
        Assert.True(Asn1Reader.TryRead(new byte[] { 6, 3, 0x2A, 0x03, 0x04 }, out Asn1Element oid, out _, out _));
        Assert.Equal("1.2.3.4", Asn1Reader.TryOid(oid));
        Assert.True(Asn1Reader.TryRead(new byte[] { 6, 11, 0x2A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, out Asn1Element big, out _, out _));
        Assert.Null(Asn1Reader.TryOid(big));
        Assert.True(Asn1Reader.TryRead(System.Text.Encoding.ASCII.GetBytes("\u0018\u000f20260102030405Z"), out Asn1Element t, out _, out _));
        Assert.True(Asn1Reader.TryTime(t, out DateTimeOffset when));
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), when);
        Assert.True(Asn1Reader.TryRead(System.Text.Encoding.ASCII.GetBytes("\u0018\u000f20261302030405Z"), out Asn1Element badMonth, out _, out _));
        Assert.False(Asn1Reader.TryTime(badMonth, out _));
    }

    [Theory]
    [InlineData("[0 -1 5 5]")]
    [InlineData("[0 9223372036854775807 9223372036854775807 9223372036854775807]")]
    [InlineData("[0 100 50 50]")]
    [InlineData("[5 10 20 30]")]
    [InlineData("[0 10 20]")]
    [InlineData("[0 10 20 30 40 50]")]
    [InlineData("[0 (a) 20 30]")]
    [InlineData("[0 99999999 99999999 99999999]")]
    [InlineData("[0 0 0 0]")]
    public void HostileByteRangesGetTypedResults(string range)
    {
        byte[] file = SignedPdf.BaseFile(new SignedPdf.Options(), out _);
        string text = System.Text.Encoding.Latin1.GetString(file);
        int at = text.IndexOf("[0 0000000000 0000000000 0000000000]", StringComparison.Ordinal);
        Assert.True(at > 0);
        string replaced = text[..at] + range.PadRight("[0 0000000000 0000000000 0000000000]".Length) + text[(at + "[0 0000000000 0000000000 0000000000]".Length)..];
        using var doc = PdfDocument.Open(System.Text.Encoding.Latin1.GetBytes(replaced));
        PdfSignatureVerification v = doc.GetSignatures()[0].Verify(TestPki.Shared.Trusting());
        Assert.NotEqual(PdfByteRangeStatus.Valid, v.ByteRange);
        Assert.False(v.IsValid);
    }

    [Fact]
    public void DamagedSignerCertificateIsReportedNotThrown()
    {
        // The signer certificate keeps its issuer and serial (so it is still matched to the signer) but a byte inside it is wrong.
        // Some platforms parse certificates lazily, so the damage only shows when a property is read.
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options { SubFilter = "ETSI.CAdES.detached" }, out _), TestPki.Shared.RsaSigner, new CmsSpec { Cades = true });
        string text = System.Text.Encoding.Latin1.GetString(file);
        int hole = text.IndexOf("/Contents <", StringComparison.Ordinal) + 11;
        string signerHex = Convert.ToHexString(TestPki.Shared.RsaSigner.RawData);
        int certAt = text.IndexOf(signerHex, hole, StringComparison.OrdinalIgnoreCase);
        Assert.True(certAt > 0);

        // The signature algorithm OID (06 09 2A 86 48 86 F7 0D 01 01 0B) inside the certificate: break its tag.
        const string oidHex = "06092A864886F70D01010B";
        int oidAt = text.IndexOf(oidHex, certAt, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(oidAt, certAt, certAt + signerHex.Length);
        byte[] broken = (byte[])file.Clone();
        broken[oidAt + 1] = (byte)'7';
        PdfSignatureVerification v = SignedPdf.VerifyOne(broken);
        Assert.False(v.IsValid);
        Assert.NotEqual(PdfCheckStatus.Valid, v.SignatureCheck);

        // Every few bytes of the certificate, damaged one at a time: always a result, never an exception.
        for (int i = 8; i < signerHex.Length; i += 14)
        {
            byte[] copy = (byte[])file.Clone();
            int p = certAt + i;
            copy[p] = copy[p] == (byte)'F' ? (byte)'0' : (byte)'F';
            PdfSignatureVerification r = SignedPdf.VerifyOne(copy);
            Assert.NotNull(r.Warnings);
        }
    }

    [Fact]
    public void MutatedSignedFilesNeverThrow()
    {
        byte[] file = SignedPdf.SignWith(SignedPdf.BaseFile(new SignedPdf.Options(), out _), TestPki.Shared.RsaSigner, new CmsSpec { Cades = true, Timestamp = true });
        int cms = System.Text.Encoding.Latin1.GetString(file).IndexOf("/Contents <", StringComparison.Ordinal) + 11;
        var rng = new Random(1234);
        for (int i = 0; i < 400; i++)
        {
            byte[] copy = (byte[])file.Clone();
            int n = 1 + rng.Next(3);
            for (int k = 0; k < n; k++)
            {
                // Mostly hit the CMS hex, sometimes the rest.
                int pos = rng.Next(4) == 0 ? rng.Next(copy.Length) : cms + rng.Next(3000);
                copy[pos] = (byte)rng.Next(256);
            }

            if (PdfDocument.TryOpen(copy, out PdfDocument? doc, out _))
            {
                using (doc)
                {
                    _ = doc.VerifySignatures(TestPki.Shared.Trusting());
                }
            }
        }
    }

    [Fact]
    public void FuzzTargetSurvivesMutatedSeeds()
    {
        var rng = new Random(99);
        foreach (byte[] seed in OnePdf.Fuzz.SignatureTarget.Seeds())
        {
            OnePdf.Fuzz.SignatureTarget.Run(seed);
            for (int i = 0; i < 60; i++)
            {
                byte[] copy = (byte[])seed.Clone();
                for (int k = 0; k < 1 + rng.Next(4); k++)
                {
                    copy[1 + rng.Next(copy.Length - 1)] = (byte)rng.Next(256);
                }

                OnePdf.Fuzz.SignatureTarget.Run(copy);
            }
        }
    }
}
