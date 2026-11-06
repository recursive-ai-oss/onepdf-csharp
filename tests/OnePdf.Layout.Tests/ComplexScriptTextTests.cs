using System.Text.RegularExpressions;

namespace OnePdf.Layout.Tests;

/// <summary>
/// Shaped complex-script text: every used character code of an embedded font has a /ToUnicode entry (ISO 14289-1 7.21.7), joined
/// words extract without gaps, and the logical text comes back.
/// </summary>
public class ComplexScriptTextTests
{
    private const string Arabic = "مرحبا بكم في هذا المستند التجريبي. هذا نص عربي طويل يلتف على عدة أسطر لاختبار الاتجاه من اليمين إلى اليسار وكسر الأسطر.";
    private const string ArabicMarks = "لا الله لاعب سَلَامٌ كِتَابٌ";
    private const string Mixed = "English text then مرحبا بالعالم and numbers 12345 then שלום back to English text again for a long line that wraps around the page.";
    private const string Devanagari = "कि क्षेत्र हिन्दी";
    private const string Thai = "นี่คือย่อหน้าภาษาไทยสำหรับทดสอบการแบ่งบรรทัด";

    private static byte[] Font(string name) => File.ReadAllBytes(Path.Combine(Support.Root(), "testdata", "fonts", name));

    private static byte[] Make(string text, bool rtl, PdfUAOptions? ua)
    {
        LayoutDocument layout = LayoutDocument.Compose(doc =>
        {
            doc.Title("Complex script").Language(rtl ? "ar" : "en");
            if (rtl)
            {
                doc.RightToLeft();
            }

            doc.Fonts.Add(Font("NotoSansArabic-Subset-Regular.ttf"), "Noto Sans Arabic");
            doc.Fonts.Add(Font("NotoSansHebrew-Subset-Regular.ttf"), "Noto Sans Hebrew");
            doc.Fonts.Add(Font("NotoSansDevanagari-Subset-Regular.ttf"), "Noto Sans Devanagari");
            doc.Fonts.Add(Font("NotoSansThai-Subset-Regular.ttf"), "Noto Sans Thai");
            doc.Fonts.FallbackFamilies.Add("Noto Sans Arabic");
            doc.Fonts.FallbackFamilies.Add("Noto Sans Hebrew");
            doc.Fonts.FallbackFamilies.Add("Noto Sans Devanagari");
            doc.Fonts.FallbackFamilies.Add("Noto Sans Thai");
            doc.Page(page => page.Content().Text(text));
        });
        using var ms = new MemoryStream();
        layout.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], PdfUA = ua });
        return ms.ToArray();
    }

    // Every code shown by a TJ/Tj string must have a bfchar entry in its font's ToUnicode map with non-empty, valid text.
    private static void AssertEveryCodeMapped(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(pdf);
        PdfPage page = doc.Pages[0];
        var fonts = (PdfDictionary)((PdfDictionary)page.Dictionary.Get("Resources")!).Get("Font")!;
        var used = new Dictionary<string, HashSet<int>>();
        string content = ContentOf(page);
        Regex fontOps = new(@"/(\S+) [\d.]+ Tf(.*?)ET", RegexOptions.Singleline);
        foreach (Match m in fontOps.Matches(content))
        {
            if (!used.TryGetValue(m.Groups[1].Value, out HashSet<int>? set))
            {
                used[m.Groups[1].Value] = set = [];
            }

            foreach (Match h in Regex.Matches(m.Groups[2].Value, "<([0-9A-F]{4})>"))
            {
                set.Add(Convert.ToInt32(h.Groups[1].Value, 16));
            }
        }

        Assert.NotEmpty(used);
        foreach ((string name, HashSet<int> codes) in used)
        {
            var font = (PdfDictionary)fonts.Get(name)!;
            var toUni = (PdfStream)font.Get("ToUnicode")!;
            string cmap = System.Text.Encoding.Latin1.GetString(toUni.GetDecodedBytes().Span);
            var map = new Dictionary<int, string>();
            foreach (Match e in Regex.Matches(cmap, "<([0-9A-F]{4})> <([0-9A-F]+)>"))
            {
                string hex = e.Groups[2].Value;
                var chars = new char[hex.Length / 4];
                for (int i = 0; i < chars.Length; i++)
                {
                    chars[i] = (char)Convert.ToInt32(hex.Substring(i * 4, 4), 16);
                }

                map[Convert.ToInt32(e.Groups[1].Value, 16)] = new string(chars);
            }

            foreach (int code in codes)
            {
                Assert.True(map.TryGetValue(code, out string? text), $"font {name}: code {code:X4} has no ToUnicode entry");
                Assert.NotEmpty(text!);
                Assert.True(text!.Any(ch => ch is not ('\0' or '\uFEFF' or '\uFFFE')), $"font {name}: code {code:X4} maps to an invalid value");
            }
        }
    }

    private static string ContentOf(PdfPage page)
    {
        var sb = new System.Text.StringBuilder();
        PdfObject? contents = page.Dictionary.Get("Contents");
        if (contents is PdfArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                sb.Append(System.Text.Encoding.Latin1.GetString(((PdfStream)arr[i]!).GetDecodedBytes().Span)).Append('\n');
            }
        }
        else if (contents is PdfStream s)
        {
            sb.Append(System.Text.Encoding.Latin1.GetString(s.GetDecodedBytes().Span));
        }

        return sb.ToString();
    }

    private static string Extract(byte[] pdf)
    {
        using PdfDocument doc = PdfDocument.Open(pdf);
        return Support.Normalize(string.Join(" ", doc.ExtractText().Select(t => t.Text)));
    }

    public static TheoryData<string, string, bool> Cases => new()
    {
        { "arabic", Arabic, true },
        { "arabic-marks", ArabicMarks, true },
        { "mixed-bidi", Mixed, false },
        { "devanagari", Devanagari, false },
        { "thai", Thai, false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryCodeIsMappedAndTextExtractsInLogicalOrder(string name, string text, bool rtl)
    {
        byte[] pdf = Make(text, rtl, null);
        AssertEveryCodeMapped(pdf);
        string got = Extract(pdf);
        // Direction marks are not part of the text.
        got = new string([.. got.Where(ch => ch is not ((>= '\u202A' and <= '\u202E') or '\u200E' or '\u200F' or (>= '\u2066' and <= '\u2069') or '\u200C'))]);
        Assert.True(Support.Normalize(text) == got, $"{name}: expected \"{text}\" but got \"{got}\"");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ComplexScriptOutputPassesUa1ToUnicodeRule(string name, string text, bool rtl)
    {
        _ = name;
        byte[] pdf = Make(text, rtl, new PdfUAOptions());
        List<string> failures = TagSupport.Vera(pdf, "ua1");
        Assert.DoesNotContain(failures, f => f.StartsWith("7.21.7", StringComparison.Ordinal));
    }
}
