using System.Globalization;
using System.Text;

namespace OnePdf.FontData;

/// <summary>
/// The Adobe Glyph List (names to Unicode) with the AGL specification's name-parsing rules, and the ZapfDingbats list.
/// Tables load on first use; lookups are thread-safe after that.
/// </summary>
internal static class GlyphList
{
    private static readonly Lazy<Dictionary<string, string>> Agl = new(() => Load("glyphlist.bin"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Dictionary<string, string>> Zapf = new(() => Load("zapfdingbats.bin"), LazyThreadSafetyMode.ExecutionAndPublication);

    private static Dictionary<string, string> Load(string resource)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in FontResources.ReadLines(resource))
        {
            int semi = line.IndexOf(';', StringComparison.Ordinal);
            if (semi <= 0)
            {
                continue;
            }

            var sb = new StringBuilder();
            foreach (string hex in line[(semi + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }

            map[line[..semi]] = sb.ToString();
        }

        return map;
    }

    /// <summary>Looks a name up in the AGL itself (no suffix or <c>uniXXXX</c> handling).</summary>
    public static bool TryGetListed(string name, out string text)
    {
        if (Agl.Value.TryGetValue(name, out string? t))
        {
            text = t;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>Looks a name up in the ZapfDingbats list (<c>a1</c> to <c>a206</c>).</summary>
    public static bool TryGetZapfDingbats(string name, out string text)
    {
        if (Zapf.Value.TryGetValue(name, out string? t))
        {
            text = t;
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>
    /// Maps a glyph name to Unicode text by the AGL specification, "Mapping glyph names to Unicode": drop the part from the first
    /// period, split at underscores into components, and map each component by the list, by <c>uniXXXX…</c> (groups of four
    /// uppercase hex digits, no surrogates) or by <c>uXXXX</c> to <c>uXXXXXX</c>. Components that match none of these contribute
    /// nothing. Returns false when the result is empty (".notdef", "foo").
    /// </summary>
    public static bool TryGetUnicode(string glyphName, out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(glyphName))
        {
            return false;
        }

        int dot = glyphName.IndexOf('.', StringComparison.Ordinal);
        ReadOnlySpan<char> name = dot >= 0 ? glyphName.AsSpan(0, dot) : glyphName.AsSpan();
        if (name.IsEmpty)
        {
            return false;
        }

        if (name.IndexOf('_') < 0)
        {
            text = MapComponent(name);
            return text.Length > 0;
        }

        var sb = new StringBuilder();
        while (true)
        {
            int us = name.IndexOf('_');
            ReadOnlySpan<char> part = us >= 0 ? name[..us] : name;
            sb.Append(MapComponent(part));
            if (us < 0)
            {
                break;
            }

            name = name[(us + 1)..];
        }

        text = sb.ToString();
        return text.Length > 0;
    }

    private static string MapComponent(ReadOnlySpan<char> c)
    {
        if (c.IsEmpty)
        {
            return string.Empty;
        }

        string s = c.ToString();
        if (Agl.Value.TryGetValue(s, out string? listed))
        {
            return listed;
        }

        if (c.Length >= 7 && (c.Length - 3) % 4 == 0 && c.StartsWith("uni", StringComparison.Ordinal))
        {
            var sb = new StringBuilder();
            for (int i = 3; i < c.Length; i += 4)
            {
                if (!TryHex4(c.Slice(i, 4), out int cu) || (cu >= 0xD800 && cu <= 0xDFFF))
                {
                    return string.Empty;
                }

                sb.Append((char)cu);
            }

            return sb.ToString();
        }

        if (c.Length is >= 5 and <= 7 && c[0] == 'u')
        {
            int v = 0;
            for (int i = 1; i < c.Length; i++)
            {
                int d = UpperHex(c[i]);
                if (d < 0)
                {
                    return string.Empty;
                }

                v = (v << 4) | d;
            }

            if (v > 0x10FFFF || (v >= 0xD800 && v <= 0xDFFF))
            {
                return string.Empty;
            }

            return char.ConvertFromUtf32(v);
        }

        return string.Empty;
    }

    private static bool TryHex4(ReadOnlySpan<char> s, out int value)
    {
        value = 0;
        foreach (char ch in s)
        {
            int d = UpperHex(ch);
            if (d < 0)
            {
                return false;
            }

            value = (value << 4) | d;
        }

        return true;
    }

    // The specification allows only uppercase hexadecimal digits.
    private static int UpperHex(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => -1,
    };
}
