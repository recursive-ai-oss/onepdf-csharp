using System;
using System.Collections.Generic;
using System.Text.Json;
using OnePdf.Text;

namespace OnePdf.Fuzz;

/// <summary>
/// Layout and bidi target. The first byte selects the options; the rest is read as 10-byte glyph records (text pick, position, size,
/// direction and flags) and, separately, as code points for the bidirectional algorithm. Analysis, all three text modes and the JSON writer must not
/// throw, indices must stay in range, and the reordering must be a permutation.
/// </summary>
internal static class ExtractionTarget
{
    private static readonly string[] Texts =
    [
        "a", "b", "e", "f", " ", "-", "1", "2", ".", "\uFB01", "\uFB03", "\u05D0", "\u05D1", "\u05E9", "\u0627", "\u0644", "\u0661", "\u3042", "\u6F22", "\uD800",
        "\u2F00", "\uFB50", "\uFE70", "\uFDFA", "\U0002F800", "\u202B", "\u202E", "\u202C", "\u2067", "\u2069", "\u200F", "(", ")", "\uD83D\uDE00", "", "ab",
    ];

    /// <summary>Reads the options and glyphs a fuzz input stands for; null when the input is too short.</summary>
    internal static (PdfTextOptions Options, List<PdfTextGlyph> Glyphs)? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return null;
        }

        PdfTextOptions o = new()
        {
            DecomposeLigatures = (data[0] & 1) != 0,
            JoinHyphenatedWords = (data[0] & 2) != 0,
            IncludeClippedAndOffPage = (data[0] & 4) != 0,
            CollapseDuplicates = (data[0] & 8) != 0,
        };

        ReadOnlySpan<byte> rest = data.Slice(1);
        int n = Math.Min(rest.Length / 10, 400);
        List<PdfTextGlyph> glyphs = new(n);
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> r = rest.Slice(i * 10, 10);
            double x = ((r[1] << 8) | r[2]) / 80.0;
            double y = ((r[3] << 8) | r[4]) / 80.0;
            double size = 1 + (r[5] & 31);
            int dir = r[6] & 7; // 0-3 axis directions, 4-7 vertical variants
            double c = dir switch { 0 or 4 => 1, 2 or 6 => -1, _ => 0 };
            double s = dir switch { 1 or 5 => 1, 3 or 7 => -1, _ => 0 };
            glyphs.Add(new PdfTextGlyph
            {
                Text = Texts[r[0] % Texts.Length],
                Matrix = new PdfTextMatrix(c * size, s * size, -s * size, c * size, x, y),
                Advance = (r[7] & 15) / 16.0,
                IsVertical = dir >= 4,
                IsVisible = (r[8] & 1) != 0,
                IsClipped = (r[8] & 2) != 0,
                RenderMode = r[8] >> 5,
                MarkedContentId = (r[8] & 4) != 0 ? r[9] & 7 : -1,
                ActualTextSpan = (r[8] & 8) != 0 ? 1 + (r[9] >> 6) : 0,
                ActualText = (r[8] & 16) != 0 ? "xy" : null,
                FillColor = r[9],
            });
        }

        return (o, glyphs);
    }

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (Decode(data) is not var (o, glyphs))
        {
            return;
        }

        ReadOnlySpan<byte> rest = data.Slice(1);

        // Another option bit, kept out of Decode so the recorded golden output of the seeds stays as it is.
        o.RetainGlyphs = (data[0] & 16) == 0;
        PdfPageText page = PdfTextExtractor.Analyze(new PdfPageGlyphs(0, 600, 800, glyphs), o);
        foreach (PdfTextWord w in page.Words)
        {
            foreach (int gi in w.GlyphIndices)
            {
                if ((uint)gi >= (uint)page.Glyphs.Count)
                {
                    throw new InvalidOperationException("glyph index out of range");
                }
            }
        }

        if (o.RetainGlyphs)
        {
            _ = page.GetText(PdfTextMode.ContentOrder);
            _ = page.GetText(PdfTextMode.Layout);
        }
        else if (page.Glyphs.Count != 0)
        {
            throw new InvalidOperationException("glyphs kept although not requested");
        }

        _ = page.GetText(PdfTextMode.ReadingOrder);
        PdfTextExtractor.FlagHeadersAndFooters([page, page]);
        using JsonDocument d = JsonDocument.Parse(PdfExtractionJson.ToJson([page]));

        // Bidi over the same bytes read as code points from the interesting sets.
        List<int> cps = new(rest.Length);
        foreach (byte b in rest)
        {
            string t = Texts[b % Texts.Length];
            for (int i = 0; i < t.Length; i++)
            {
                int cp = char.IsHighSurrogate(t[i]) && i + 1 < t.Length ? char.ConvertToUtf32(t[i], t[++i]) : t[i];
                cps.Add(cp);
            }

            if (cps.Count > 600)
            {
                break;
            }
        }

        int[] arr = [.. cps];
        byte[] lev = new byte[arr.Length];
        Bidi.ResolveLevels(arr, (data[0] >> 4) % 3 - 1, lev);
        int[] ord = new int[arr.Length];
        int m = Bidi.VisualOrder(lev, ord);
        bool[] seen = new bool[arr.Length];
        for (int i = 0; i < m; i++)
        {
            if ((uint)ord[i] >= (uint)arr.Length || seen[ord[i]] || lev[ord[i]] == Bidi.Removed)
            {
                throw new InvalidOperationException("reordering is not a permutation");
            }

            seen[ord[i]] = true;
        }
    }
}
