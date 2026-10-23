using OnePdf.Tests.Writing;

namespace OnePdf.Tests.Forms;

/// <summary>Filling and flattening documents that are encrypted: the new objects are encrypted like the rest.</summary>
public class FormEncryptedTests
{
    public static IEnumerable<object[]> Ciphers() => [[PdfCipher.Aes256], [PdfCipher.Aes128]];

    private static byte[] Encrypt(byte[] plain, PdfCipher cipher)
    {
        using PdfDocument doc = PdfDocument.Open(plain, FormTestSupport.Editable);
        return WriterSupport.Save(doc, new PdfSaveOptions
        {
            Encryption = PdfEncryptionAction.Set,
            NewEncryption = new PdfEncryptionOptions { UserPassword = "pw", OwnerPassword = "owner", Cipher = cipher },
        });
    }

    private static PdfOpenOptions Opts => new() { AllowEditing = true, Password = "pw" };

    [Theory]
    [MemberData(nameof(Ciphers))]
    public void FillThenSaveStaysEncryptedAndReadsBack(PdfCipher cipher)
    {
        byte[] enc = Encrypt(FormTestSupport.Synthetic(needAppearances: false), cipher);
        using PdfDocument doc = PdfDocument.Open(enc, Opts);
        Assert.True(doc.IsEncrypted);
        doc.Form!.SetValue("name", "Ada");
        doc.Form!.SetValue("notes", "secret note");

        byte[] inc = WriterSupport.SaveIncremental(doc);
        byte[] full = WriterSupport.Save(doc);
        foreach (byte[] saved in new[] { inc, full })
        {
            using PdfDocument d = PdfDocument.Open(saved, new PdfOpenOptions { Password = "pw" });
            Assert.True(d.IsEncrypted);
            Assert.Equal("Ada", d.Form!.FindField("name")!.Value);
            Dictionary<int, string> text = FormTestSupport.AnnotationText(d, new PdfInterpretOptions { GenerateAppearances = false });
            Assert.Contains("secret", text[d.Form!.FindField("notes")!.Widgets[0].Reference!.Number]);
            Assert.False(ContainsAscii(saved, "secret note"), "the value must not appear in the clear");
            WriterSupport.AssertNoWorse(enc, saved, "pw", "encrypted fill");
        }
    }

    [Theory]
    [MemberData(nameof(Ciphers))]
    public void FlattenKeepsEncryptionAndExtractsValues(PdfCipher cipher)
    {
        byte[] enc = Encrypt(FormTestSupport.Synthetic(needAppearances: false), cipher);
        using PdfDocument doc = PdfDocument.Open(enc, Opts);
        doc.Form!.SetValue("notes", "flattened words");
        doc.Flatten(new PdfFlattenOptions { IncludeMarkup = true });
        foreach (byte[] saved in new[] { WriterSupport.SaveIncremental(doc), WriterSupport.Save(doc) })
        {
            using PdfDocument d = PdfDocument.Open(saved, new PdfOpenOptions { Password = "pw" });
            Assert.True(d.IsEncrypted);
            Assert.Null(d.Form);
            Assert.Contains("flattened words", string.Join("\n", d.ExtractText().Select(static p => p.Text)));
            Assert.False(ContainsAscii(saved, "flattened words"));
            WriterSupport.AssertNoWorse(enc, saved, "pw", "encrypted flatten");
        }
    }

    private static bool ContainsAscii(byte[] data, string needle) =>
        data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(needle)) >= 0;
}
