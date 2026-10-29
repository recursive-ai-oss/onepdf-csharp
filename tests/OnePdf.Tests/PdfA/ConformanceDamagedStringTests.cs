using System.Text;

namespace OnePdf.Tests.PdfA;

/// <summary>
/// A string whose ciphertext is damaged (bad padding, a partial last block) reads as far as it decrypted. A normal save writes those bytes
/// again; a PDF/A or PDF/UA check and save name the object and refuse, as they do for a damaged stream.
/// </summary>
public class ConformanceDamagedStringTests
{
    private const string Title = "A reasonably long document title for blocks";

    private static byte[] Encrypted(PdfEncryptionOptions options)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        b.Obj(4, $"<< /Title ({Title}) >>");
        long x = b.XrefTable(5);
        b.Trailer("/Size 5 /Root 1 0 R /Info 4 0 R", x);
        using PdfDocument d = PdfDocument.Open(b.ToArray(), new PdfOpenOptions { AllowEditing = true });
        using var ms = new MemoryStream();
        d.Save(ms, new PdfSaveOptions { UseObjectStreams = false, Encryption = PdfEncryptionAction.Set, NewEncryption = options });
        return ms.ToArray();
    }

    // Finds the hex string after /Title and returns its position and length (without the angle brackets).
    private static (int At, int Length) TitleHex(byte[] pdf)
    {
        int at = pdf.AsSpan().IndexOf("/Title"u8) + 6;
        Assert.True(at >= 6);
        at += pdf.AsSpan(at).IndexOf((byte)'<') + 1;
        int end = pdf.AsSpan(at).IndexOf((byte)'>');
        return (at, end);
    }

    public enum Break
    {
        BadPadding,
        PartialBlock,
    }

    private static byte[] Damage(byte[] enc, Break how)
    {
        (int at, int length) = TitleHex(enc);
        var text = new StringBuilder(Encoding.ASCII.GetString(enc, at, length));
        if (how == Break.BadPadding)
        {
            // In CBC the block before the last (the IV for a one-block string) is XORed into the last plaintext block, so flipping
            // bit 0x10 of its last byte flips the same bit of the padding byte: a valid count of 1 to 16 becomes 0 or 17 to 31,
            // which never checks out. Changing the last cipher block itself would scramble the padding, and about one save in
            // a few hundred would still decrypt to valid padding by chance, since every save uses a fresh IV.
            int p = text.Length - 34;
            int nibble = Convert.ToInt32(text[p].ToString(), 16) ^ 1;
            text[p] = char.IsUpper(text[p]) ? char.ToUpperInvariant(nibble.ToString("x")[0]) : nibble.ToString("x")[0];
            return Replace(enc, at, length, text.ToString());
        }

        // Drop one byte (two hex digits), so the ciphertext is no longer a whole number of blocks; the string gets shorter, so the object is rewritten as a tail.
        string shorter = text.ToString(0, text.Length - 2);
        return Replace(enc, at, length, shorter + new string(' ', 2));
    }

    private static byte[] Replace(byte[] enc, int at, int length, string hex)
    {
        byte[] copy = (byte[])enc.Clone();
        Encoding.ASCII.GetBytes(hex, 0, hex.Length, copy, at);
        Assert.Equal(length, hex.Length);
        return copy;
    }

    public static IEnumerable<object[]> Cases()
    {
        foreach (Break how in Enum.GetValues<Break>())
        {
            yield return [how, new PdfEncryptionOptions { UserPassword = "pw" }];
            yield return [how, new PdfEncryptionOptions { UserPassword = "pw", Cipher = PdfCipher.Aes128 }];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChecksNameTheDamagedString(Break how, PdfEncryptionOptions options)
    {
        byte[] bad = Damage(Encrypted(options), how);
        using PdfDocument d = PdfDocument.Open(bad, new PdfOpenOptions { Password = "pw", AllowEditing = true });
        Assert.Contains(d.CheckPdfA(), v => v.Message.Contains("/Title", StringComparison.Ordinal) && v.ObjectNumber == 2 && v.Message.Contains("decrypted", StringComparison.Ordinal));
        Assert.Contains(d.CheckPdfUA(), v => v.Message.Contains("/Title", StringComparison.Ordinal) && v.ObjectNumber == 2 && v.Message.Contains("decrypted", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ConformanceSavesRefuseAndPlainSavesDoNot(Break how, PdfEncryptionOptions options)
    {
        byte[] bad = Damage(Encrypted(options), how);
        using PdfDocument d = PdfDocument.Open(bad, new PdfOpenOptions { Password = "pw", AllowEditing = true });
        using var ms = new MemoryStream();
        PdfAException a = Assert.Throws<PdfAException>(() => d.Save(ms, new PdfSaveOptions { PdfA = new PdfAOptions() }));
        Assert.Contains(a.Violations, v => v.Message.Contains("string under /Title", StringComparison.Ordinal) && v.ObjectNumber == 2);
        Assert.Throws<PdfUAException>(() => d.Save(ms, new PdfSaveOptions { PdfUA = new PdfUAOptions() }));

        using var plain = new MemoryStream();
        d.Save(plain);
        Assert.True(plain.Length > 0);
    }

    [Fact]
    public void CleanEncryptedStringsAreNotReported()
    {
        using PdfDocument d = PdfDocument.Open(Encrypted(new PdfEncryptionOptions { UserPassword = "pw" }), new PdfOpenOptions { Password = "pw", AllowEditing = true });
        Assert.Equal(Title, d.Info.Title);
        Assert.DoesNotContain(d.CheckPdfA(), v => v.Message.Contains("without loss", StringComparison.Ordinal));
    }
}
