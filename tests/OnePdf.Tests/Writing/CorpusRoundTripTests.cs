using System.Text;
using System.Text.RegularExpressions;
using OnePdf.Text;
using UglyToad.PdfPig;

namespace OnePdf.Tests.Writing;

/// <summary>
/// Opens every committed test file, rewrites it in both cross-reference forms and checks that what comes back reads the same
/// (page count, plain text, extraction JSON, display lists of the first pages), that qpdf finds nothing wrong with it and that a
/// second reader still opens it. Files the reader itself cannot open are not the writer's concern.
/// </summary>
public class CorpusRoundTripTests
{
    [Theory]
    [MemberData(nameof(WriterSupport.CorpusCases), MemberType = typeof(WriterSupport))]
    public void FullRewriteReadsTheSame(string relative)
    {
        string file = Path.Combine(WriterSupport.Root(), relative);
        string? pw = WriterSupport.PasswordFor(file);
        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(file, new PdfOpenOptions { Password = pw });
        }
        catch (PdfException)
        {
            return;
        }

        using (doc)
        {
            string before = Snapshot(doc);
            byte[] original = File.ReadAllBytes(file);
            bool pigOpened = PigOpens(original, pw);
            foreach (PdfXrefFormat form in new[] { PdfXrefFormat.Table, PdfXrefFormat.Stream })
            {
                byte[] bytes = WriterSupport.Save(doc, new PdfSaveOptions { XrefFormat = form });
                using (var again = PdfDocument.Open(bytes, new PdfOpenOptions { Password = pw }))
                {
                    AssertSame(before, Snapshot(again), $"{relative} ({form})");
                    Assert.Equal(doc.IsEncrypted, again.IsEncrypted);
                }

                WriterSupport.AssertNoWorse(original, bytes, pw, $"{relative} ({form})");
                if (pigOpened)
                {
                    Assert.True(PigOpens(bytes, pw), $"{relative} ({form}): PdfPig opened the input but not the output");
                }
            }

            if (doc.IsEncrypted)
            {
                byte[] plain = WriterSupport.Save(doc, new PdfSaveOptions { Encryption = PdfEncryptionAction.Remove });
                using var again = PdfDocument.Open(plain);
                Assert.False(again.IsEncrypted);
                AssertSame(before, Snapshot(again), relative + " (decrypted)");
                WriterSupport.AssertNoWorse(original, plain, pw, relative + " (decrypted)");
            }
        }
    }

    private static void AssertSame(string expected, string actual, string what)
    {
        if (expected == actual)
        {
            return;
        }

        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }

        int from = Math.Max(0, i - 60);
        Assert.Fail($"{what}: reads differently from offset {i}\n  before: {expected.AsSpan(from, Math.Min(160, expected.Length - from)).ToString()}\n  after:  {actual.AsSpan(from, Math.Min(160, actual.Length - from)).ToString()}");
    }

    internal static string Snapshot(PdfDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("pages=").Append(doc.PageCount).Append('\n');
        IReadOnlyList<PdfPageText> pages = doc.ExtractText();
        foreach (PdfPageText p in pages)
        {
            sb.Append(p.GetText(PdfTextMode.ReadingOrder)).Append('\f');
        }

        sb.Append('\n').Append(PdfExtractionJson.ToJson(pages)).Append('\n');
        for (int i = 0; i < Math.Min(3, doc.Pages.Count); i++)
        {
            var list = new PdfDisplayList();
            doc.Pages[i].Interpret(list);
            // Image dictionaries are printed with their references, and the number of a referenced object changes when the file is renumbered.
            sb.Append(Regex.Replace(list.ToText(), @"\d+ \d+ R|fnv=[0-9a-f]+|(?<=/Length )\d+", "#")).Append('\n');
        }

        return sb.ToString();
    }

    internal static bool PigOpens(byte[] pdf, string? password)
    {
        try
        {
            using var d = UglyToad.PdfPig.PdfDocument.Open(pdf, new ParsingOptions { Password = password ?? string.Empty, UseLenientParsing = true });
            return d.NumberOfPages >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
