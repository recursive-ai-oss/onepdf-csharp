using System.Text;
using System.Text.Json;
using OnePdf.Tests.Content;
using OnePdf.Text;

namespace OnePdf.Tests.Tagging;

/// <summary>Helpers shared by the tagging tests: opening, snapshots of what a document shows, and saving.</summary>
internal static class TagSupport
{
    public static PdfDocument Editable(byte[] pdf) => PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });

    public static PdfTextOptions Opts => new() { RetainGlyphs = true, UseStructureTree = false };

    public static IReadOnlyList<PdfPageText> Extract(PdfDocument doc) => doc.ExtractText(Opts);

    /// <summary>The extraction JSON of the whole document, structure tree ignored: the thing that must not change when a file is tagged.</summary>
    public static string ExtractionJson(PdfDocument doc) =>
        System.Text.RegularExpressions.Regex.Replace(PdfExtractionJson.ToJson(doc.ExtractText(new PdfTextOptions { RetainGlyphs = true, UseStructureTree = false })), "\"mcid\":-?[0-9]+", "\"mcid\":0");

    /// <summary>The display lists of all pages as text, without marked-content brackets and with indentation removed.</summary>
    public static string DisplayText(PdfDocument doc)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            var list = new PdfDisplayList();
            doc.Pages[i].Interpret(list, new PdfInterpretOptions { GenerateAppearances = false });
            sb.Append("== page ").Append(i).Append('\n');
            foreach (string raw in list.ToText().Split('\n'))
            {
                string l = raw.TrimStart();
                if (l.StartsWith("begin-mc ", StringComparison.Ordinal) || l == "end-mc")
                {
                    continue;
                }

                l = System.Text.RegularExpressions.Regex.Replace(l, "[0-9]+ 0 R", "N 0 R");
                l = System.Text.RegularExpressions.Regex.Replace(l, "/Length (N 0 R|[0-9]+)", "/Length L");
                if (l.StartsWith("text font=", StringComparison.Ordinal))
                {
                    ExpandRun(sb, l);
                    continue;
                }

                sb.Append(l).Append('\n');
            }
        }

        return sb.ToString();
    }

    public static byte[] Save(PdfDocument doc, PdfSaveOptions? options = null)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, options ?? new PdfSaveOptions());
        return ms.ToArray();
    }

    public static byte[] SaveIncremental(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.SaveIncremental(ms);
        return ms.ToArray();
    }

    private static readonly System.Text.RegularExpressions.Regex RunPattern = new(
        @"^(text .*?) tm=\[(\S+) (\S+) (\S+) (\S+) (\S+) (\S+)\] (vert=\d) adv=\S+ \S+ codes=\w* glyphs=\[(.*)\]$");

    // A tagged page may show one string in several pieces; what must stay equal is where each glyph is, so a run is written glyph by glyph
    // with its origin in device space.
    private static void ExpandRun(StringBuilder sb, string line)
    {
        System.Text.RegularExpressions.Match m = RunPattern.Match(line);
        if (!m.Success)
        {
            sb.Append(line).Append('\n');
            return;
        }

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        double a = double.Parse(m.Groups[2].Value, ci), b = double.Parse(m.Groups[3].Value, ci), c = double.Parse(m.Groups[4].Value, ci);
        double d = double.Parse(m.Groups[5].Value, ci), e = double.Parse(m.Groups[6].Value, ci), f = double.Parse(m.Groups[7].Value, ci);
        foreach (string g in m.Groups[9].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = g.IndexOf('@', StringComparison.Ordinal);
            string[] parts = g[(at + 1)..].Split(',');
            double ox = double.Parse(parts[0], ci);
            double oy = double.Parse(parts[1], ci);
            string rest = parts[2];
            sb.Append(m.Groups[1].Value).Append(" lin=").Append(a.ToString(ci)).Append(' ').Append(b.ToString(ci)).Append(' ').Append(c.ToString(ci)).Append(' ').Append(d.ToString(ci))
                .Append(" at=").Append((e + ox * a + oy * c).ToString("F3", ci)).Append(' ').Append((f + ox * b + oy * d).ToString("F3", ci))
                .Append(' ').Append(m.Groups[8].Value).Append(" glyph=").Append(g[..at]).Append(':').Append(rest).Append('\n');
        }
    }

    private static readonly System.Text.RegularExpressions.Regex Number = new(@"-?[0-9]+(\.[0-9]+)?");

    /// <summary>
    /// Equal, except that numbers may differ by 0.002: a string shown in two pieces reaches its second piece by a different sum of the
    /// same advances, so a coordinate printed to three decimals can differ in the last digit.
    /// </summary>
    public static void AssertSame(string expected, string actual)
    {
        if (expected == actual)
        {
            return;
        }

        string skeletonA = Number.Replace(expected, "#");
        string skeletonB = Number.Replace(actual, "#");
        if (skeletonA != skeletonB)
        {
            int i = 0;
            while (i < Math.Min(skeletonA.Length, skeletonB.Length) && skeletonA[i] == skeletonB[i])
            {
                i++;
            }

            throw new Xunit.Sdk.XunitException($"differs at {i}: ...{skeletonA[Math.Max(0, i - 80)..Math.Min(skeletonA.Length, i + 120)]} | ...{skeletonB[Math.Max(0, i - 80)..Math.Min(skeletonB.Length, i + 120)]}");
        }

        System.Text.RegularExpressions.MatchCollection a = Number.Matches(expected), b = Number.Matches(actual);
        for (int i = 0; i < a.Count; i++)
        {
            double x = double.Parse(a[i].Value, System.Globalization.CultureInfo.InvariantCulture), y = double.Parse(b[i].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(x - y) > 0.002)
            {
                throw new Xunit.Sdk.XunitException($"number {i} differs: {a[i].Value} vs {b[i].Value} near ...{expected[Math.Max(0, a[i].Index - 60)..Math.Min(expected.Length, a[i].Index + 60)]}");
            }
        }
    }

    public static bool Same(string expected, string actual)
    {
        try
        {
            AssertSame(expected, actual);
            return true;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return false;
        }
    }
}
