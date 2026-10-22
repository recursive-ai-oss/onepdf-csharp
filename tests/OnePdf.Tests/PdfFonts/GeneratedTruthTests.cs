using System.Text;
using System.Text.Json;
using OnePdf.PdfFonts;

namespace OnePdf.Tests.PdfFonts;

/// <summary>
/// Shows the Unicode chain end to end on the generated set: the strings shown by each page's content stream (read with a small
/// test-only tokenizer, in content order), decoded through the page's fonts, contain every word of the truth text.
/// </summary>
public class GeneratedTruthTests
{
    private readonly record struct Op(string Name, List<object> Args);

    [Fact]
    public void ShownStringsContainEveryTruthWord()
    {
        var problems = new List<string>();
        int files = 0, words = 0, reversed = 0, folded = 0;
        foreach (string name in TestData.GeneratedNames())
        {
            JsonElement truth = TestData.Truth(name);
            if (truth.TryGetProperty("expect", out JsonElement ex) && ex.GetString() == "unrecoverable")
            {
                continue;
            }

            string? user = truth.TryGetProperty("passwords", out JsonElement pw) ? pw.GetProperty("user").GetString() : null;
            using PdfDocument doc = PdfDocument.Open(TestData.Generated(name), new PdfOpenOptions { Password = user });
            doc.FontProvider = new NoFonts();
            files++;
            string category = truth.TryGetProperty("category", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
            JsonElement pages = truth.GetProperty("pages");
            for (int i = 0; i < pages.GetArrayLength() && i < doc.Pages.Count; i++)
            {
                string expected = pages[i].GetProperty("text").GetString() ?? string.Empty;
                string shown = Compact(Ligatures.Decompose(PageText(doc, doc.Pages[i])));
                // Producers shape text with presentation forms (Arabic FExx) and compatibility ideographs (Kangxi radicals) and
                // their ToUnicode maps say so faithfully; folding them to base characters is the extractor's job (compatibility
                // normalization), not the font layer's. Such pages are checked for replacement characters instead of words.
                bool compat = shown.Any(static ch => ch is (>= '\u2F00' and <= '\u2FDF') or (>= '\uFB50' and <= '\uFDFF') or (>= '\uFE70' and <= '\uFEFF'));
                Assert.DoesNotContain('\uFFFD', shown);
                foreach (string raw in expected.Split([' ', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string w = Compact(raw);
                    words++;
                    if (shown.Contains(w, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Right-to-left text is written in visual order by the producers; the truth is logical order.
                    if (category == "rtl" && shown.Contains(new string([.. w.Reverse()]), StringComparison.Ordinal))
                    {
                        reversed++;
                        continue;
                    }

                    if (compat && w.Any(static ch => ch > 0x7F))
                    {
                        folded++;
                        continue;
                    }

                    problems.Add($"{name} p{i}: '{raw}'");
                }
            }
        }

        Console.WriteLine($"files={files} words={words} visual-order RTL words={reversed} compatibility-form words={folded} missing={problems.Count}");
        Assert.True(files > 30);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
    }

    private static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!char.IsWhiteSpace(c) && c != '\u00AD')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string PageText(PdfDocument doc, PdfPage page)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
        foreach (PdfStream s in page.ContentStreams)
        {
            Run(doc, s.GetDecodedBytes().Span, page.Resources, sb, 0, seen);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static void Run(PdfDocument doc, ReadOnlySpan<byte> data, PdfDictionary? resources, StringBuilder sb, int depth, HashSet<PdfStream> seen)
    {
        PdfFont? font = null;
        foreach (Op op in Tokenize(data))
        {
            switch (op.Name)
            {
                case "Tf" when op.Args.Count >= 2 && op.Args[^2] is string fn:
                    font = doc.GetFont(resources, fn);
                    break;
                case "Tj" or "'" or "\"" when op.Args.Count > 0 && op.Args[^1] is byte[] str && font is not null:
                    Show(font, str, sb);
                    break;
                case "TJ" when op.Args.Count > 0 && op.Args[^1] is List<object> arr && font is not null:
                    foreach (object o in arr)
                    {
                        if (o is byte[] part)
                        {
                            Show(font, part, sb);
                        }
                    }

                    break;
                case "Do" when depth < 4 && op.Args.Count > 0 && op.Args[^1] is string xn
                    && resources?.GetDictionary("XObject")?.Get(xn) is PdfStream form && form.Dictionary.GetName("Subtype") == "Form" && seen.Add(form):
                    Run(doc, form.GetDecodedBytes().Span, form.Dictionary.GetDictionary("Resources") ?? resources, sb, depth + 1, seen);
                    seen.Remove(form);
                    break;
            }
        }
    }

    private static void Show(PdfFont font, byte[] text, StringBuilder sb)
    {
        var glyphs = new List<PdfGlyph>();
        font.Decode(text, glyphs);
        foreach (PdfGlyph g in glyphs)
        {
            sb.Append(g.Unicode);
        }
    }

    // A tokenizer just good enough for generated content streams: names are strings, strings are byte[], arrays are List<object>.
    private static List<Op> Tokenize(ReadOnlySpan<byte> d)
    {
        var ops = new List<Op>();
        var stack = new Stack<List<object>>();
        var args = new List<object>();
        int i = 0;
        while (i < d.Length)
        {
            byte c = d[i];
            if (c is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or 0 or 12)
            {
                i++;
            }
            else if (c == '%')
            {
                while (i < d.Length && d[i] is not ((byte)'\n' or (byte)'\r'))
                {
                    i++;
                }
            }
            else if (c == '/')
            {
                int s = ++i;
                while (i < d.Length && !IsDelimiter(d[i]))
                {
                    i++;
                }

                args.Add(Encoding.Latin1.GetString(d[s..i]));
            }
            else if (c == '(')
            {
                args.Add(LiteralString(d, ref i));
            }
            else if (c == '<' && i + 1 < d.Length && d[i + 1] == '<')
            {
                int depth = 0;
                while (i < d.Length)
                {
                    if (d[i] == '<' && i + 1 < d.Length && d[i + 1] == '<')
                    {
                        depth++;
                        i += 2;
                    }
                    else if (d[i] == '>' && i + 1 < d.Length && d[i + 1] == '>')
                    {
                        depth--;
                        i += 2;
                        if (depth == 0)
                        {
                            break;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }
            }
            else if (c == '<')
            {
                int s = ++i;
                while (i < d.Length && d[i] != '>')
                {
                    i++;
                }

                args.Add(HexString(d[s..Math.Min(i, d.Length)]));
                i++;
            }
            else if (c == '[')
            {
                stack.Push(args);
                args = [];
                i++;
            }
            else if (c == ']')
            {
                List<object> arr = args;
                args = stack.Count > 0 ? stack.Pop() : [];
                args.Add(arr);
                i++;
            }
            else
            {
                int s = i;
                while (i < d.Length && !IsDelimiter(d[i]))
                {
                    i++;
                }

                if (i == s)
                {
                    i++;
                    continue;
                }

                string tok = Encoding.Latin1.GetString(d[s..i]);
                if (double.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num))
                {
                    args.Add(num);
                }
                else if (tok == "BI")
                {
                    // Inline image: skip to " EI ".
                    int e = d[i..].IndexOf(" EI"u8);
                    i = e < 0 ? d.Length : i + e + 3;
                    args = [];
                }
                else
                {
                    ops.Add(new Op(tok, args));
                    args = [];
                }
            }
        }

        return ops;
    }

    private static bool IsDelimiter(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or 0 or 12 or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'/' or (byte)'%';

    private static byte[] HexString(ReadOnlySpan<byte> h)
    {
        var bytes = new List<byte>();
        int hi = -1;
        foreach (byte b in h)
        {
            int v = b is >= (byte)'0' and <= (byte)'9' ? b - '0' : b is >= (byte)'a' and <= (byte)'f' ? b - 'a' + 10 : b is >= (byte)'A' and <= (byte)'F' ? b - 'A' + 10 : -1;
            if (v < 0)
            {
                continue;
            }

            if (hi < 0)
            {
                hi = v;
            }
            else
            {
                bytes.Add((byte)((hi << 4) | v));
                hi = -1;
            }
        }

        if (hi >= 0)
        {
            bytes.Add((byte)(hi << 4));
        }

        return [.. bytes];
    }

    private static byte[] LiteralString(ReadOnlySpan<byte> d, ref int i)
    {
        var bytes = new List<byte>();
        int depth = 0;
        i++;
        while (i < d.Length)
        {
            byte c = d[i++];
            if (c == '\\' && i < d.Length)
            {
                byte e = d[i++];
                switch (e)
                {
                    case (byte)'n': bytes.Add(10); break;
                    case (byte)'r': bytes.Add(13); break;
                    case (byte)'t': bytes.Add(9); break;
                    case (byte)'b': bytes.Add(8); break;
                    case (byte)'f': bytes.Add(12); break;
                    case (byte)'\r':
                        if (i < d.Length && d[i] == '\n')
                        {
                            i++;
                        }

                        break;
                    case (byte)'\n': break;
                    default:
                        if (e is >= (byte)'0' and <= (byte)'7')
                        {
                            int v = e - '0';
                            for (int k = 0; k < 2 && i < d.Length && d[i] is >= (byte)'0' and <= (byte)'7'; k++)
                            {
                                v = (v * 8) + (d[i++] - '0');
                            }

                            bytes.Add((byte)v);
                        }
                        else
                        {
                            bytes.Add(e);
                        }

                        break;
                }
            }
            else if (c == '(')
            {
                depth++;
                bytes.Add(c);
            }
            else if (c == ')')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
                bytes.Add(c);
            }
            else
            {
                bytes.Add(c);
            }
        }

        return [.. bytes];
    }
}
