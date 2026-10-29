using System.Linq;
using System.Globalization;
using System.Text;
using OnePdf.FontData;

namespace OnePdf.FontEmbedding;

/// <summary>
/// Writes <c>/ToUnicode</c> CMaps (ISO 32000-2 9.10.3, Adobe Technical Note #5411 syntax). A merge keeps every entry and the code space
/// of an existing map and adds or replaces the entries it is given.
/// </summary>
internal static class ToUnicodeWriter
{
    private const int MaxEntries = 1 << 20;

    /// <summary>
    /// Merges <paramref name="entries"/> into the font's <c>/ToUnicode</c> (created when the font has none). Entries are (code, byte length of
    /// the code, text). Returns false when nothing changed; <paramref name="rewrite"/> writes the map again even when no entry changes (to normalise its ranges).
    /// </summary>
    public static bool Merge(PdfDocument doc, PdfDictionary font, IReadOnlyDictionary<int, (int Length, string Text)> entries, bool overrideExisting, bool rewrite = false)
    {
        // code + length -> text. Existing entries first, so they win unless overridden.
        var merged = new SortedDictionary<(int Length, uint Code), string>();
        var space = new List<(int Length, uint Lo, uint Hi)>();
        bool existed = false;
        if (font.Get("ToUnicode") is PdfStream existing)
        {
            existed = true;
            CMap map;
            try
            {
                map = CMapParser.Parse(existing.GetDecodedBytes().Span, static _ => null);
            }
            catch (PdfFormatException)
            {
                map = CMap.CreateIdentity(0);
            }

            foreach ((uint code, int length, string text) in map.EnumerateUnicode(MaxEntries))
            {
                merged[(length, code)] = text;
            }

            foreach (CodespaceRange r in map.CodespaceRanges)
            {
                space.Add((r.Length, r.Lo, r.Hi));
            }
        }

        bool changed = !existed || rewrite;
        foreach ((int code, (int length, string text)) in entries)
        {
            if (code < 0 || text.Length == 0 || merged.Count >= MaxEntries)
            {
                continue;
            }

            var key = (length, (uint)code);
            if (merged.TryGetValue(key, out string? old))
            {
                if (!overrideExisting || old == text)
                {
                    continue;
                }
            }

            merged[key] = text;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        // Every length that has entries needs a code space range that contains them.
        foreach (int length in merged.Keys.Select(static k => k.Length).Distinct())
        {
            uint max = length >= 4 ? uint.MaxValue : (1u << (8 * length)) - 1;
            bool covered = space.Any(s => s.Length == length);
            if (!covered)
            {
                space.Add((length, 0, max));
            }
        }

        if (space.Count == 0)
        {
            space.Add((1, 0, 0xFF));
        }

        byte[] bytes = Encoding.ASCII.GetBytes(Build(merged, space));
        PdfStream stream = doc.CreateStream(bytes, compress: true);
        font.SetEntry("ToUnicode", doc.AddObject(stream));
        return true;
    }

    private static string Build(SortedDictionary<(int Length, uint Code), string> merged, List<(int Length, uint Lo, uint Hi)> space)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append(space.Count.ToString(CultureInfo.InvariantCulture)).Append(" begincodespacerange\n");
        foreach ((int length, uint lo, uint hi) in space)
        {
            sb.Append('<').Append(Hex(lo, length)).Append("> <").Append(Hex(hi, length)).Append(">\n");
        }

        sb.Append("endcodespacerange\n");

        // Consecutive codes whose text is one UTF-16 unit counting up become bfrange lines; the rest are bfchar lines.
        var singles = new List<(int Length, uint Code, string Text)>();
        var ranges = new List<(int Length, uint Lo, uint Hi, string Text)>();
        foreach (((int length, uint code), string text) in merged)
        {
            if (ranges.Count > 0 && ranges[^1] is var r && r.Length == length && r.Hi + 1 == code && r.Hi - r.Lo < 255
                && text.Length == 1 && r.Text.Length == 1 && text[0] == r.Text[0] + (char)(code - r.Lo) && (code & 0xFF) != 0 && (text[0] & 0xFF) != 0 && !char.IsSurrogate(text[0]))
            {
                ranges[^1] = (r.Length, r.Lo, code, r.Text);
            }
            else
            {
                ranges.Add((length, code, code, text));
            }
        }

        foreach ((int length, uint lo, uint hi, string text) in ranges)
        {
            if (hi == lo)
            {
                singles.Add((length, lo, text));
            }
        }

        for (int i = 0; i < singles.Count; i += 100)
        {
            int n = Math.Min(100, singles.Count - i);
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
            for (int j = 0; j < n; j++)
            {
                (int length, uint code, string text) = singles[i + j];
                sb.Append('<').Append(Hex(code, length)).Append("> <").Append(Utf16Hex(text)).Append(">\n");
            }

            sb.Append("endbfchar\n");
        }

        var runs = ranges.Where(static r => r.Hi > r.Lo).ToList();
        for (int i = 0; i < runs.Count; i += 100)
        {
            int n = Math.Min(100, runs.Count - i);
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" beginbfrange\n");
            for (int j = 0; j < n; j++)
            {
                (int length, uint lo, uint hi, string text) = runs[i + j];
                sb.Append('<').Append(Hex(lo, length)).Append("> <").Append(Hex(hi, length)).Append("> <").Append(Utf16Hex(text)).Append(">\n");
            }

            sb.Append("endbfrange\n");
        }

        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    private static string Hex(uint value, int length) => value.ToString("X" + (2 * length).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Utf16Hex(string text)
    {
        var sb = new StringBuilder(text.Length * 4);
        foreach (char c in text)
        {
            sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
