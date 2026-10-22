using System.Globalization;
using OnePdf.FontPrograms;

namespace OnePdf.FontData;

/// <summary>Predefined CFF data from Adobe Technical Note #5176 appendices A to C, and the Macintosh standard glyph names of the <c>post</c> table.</summary>
internal static class CffStandardData
{
    private sealed class Data
    {
        public string[] Strings = [];
        public int[] ExpertEncodingSids = new int[256];
        public int[] ExpertCharset = [];
        public int[] ExpertSubsetCharset = [];
    }

    private static readonly Lazy<Data> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string[]> MacNames = new(() => FontResources.ReadLines("macglyphs.bin"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<Dictionary<string, int>> StringSids = new(
        () =>
        {
            string[] s = Loaded.Value.Strings;
            var d = new Dictionary<string, int>(s.Length, StringComparer.Ordinal);
            for (int i = 0; i < s.Length; i++)
            {
                d.TryAdd(s[i], i);
            }

            return d;
        },
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Number of standard strings (SIDs 0 to 390).</summary>
    public const int StandardStringCount = 391;

    /// <summary>The standard string for a SID below 391.</summary>
    public static string StandardString(int sid) => Loaded.Value.Strings[sid];

    /// <summary>The SID of a standard string, or -1.</summary>
    public static int StandardSid(string name) => StringSids.Value.TryGetValue(name, out int sid) ? sid : -1;

    /// <summary>SIDs of the Expert encoding by code (0 = .notdef).</summary>
    public static int[] ExpertEncoding => Loaded.Value.ExpertEncodingSids;

    /// <summary>Glyph SIDs in GID order after .notdef: 0 = ISOAdobe (SIDs 1 to 228), 1 = Expert, 2 = ExpertSubset.</summary>
    public static int[] PredefinedCharset(int id)
    {
        switch (id)
        {
            case 0:
                var iso = new int[228];
                for (int i = 0; i < iso.Length; i++)
                {
                    iso[i] = i + 1;
                }

                return iso;
            case 1:
                return Loaded.Value.ExpertCharset;
            case 2:
                return Loaded.Value.ExpertSubsetCharset;
            default:
                throw new FontProgramException("Unknown predefined CFF charset.");
        }
    }

    /// <summary>The name of glyph number <paramref name="index"/> in the Macintosh standard ordering (0 to 257), or null.</summary>
    public static string? MacGlyphName(int index) => (uint)index < (uint)MacNames.Value.Length ? MacNames.Value[index] : null;

    private static Data Load()
    {
        var d = new Data();
        var strings = new string[StandardStringCount];
        foreach (string line in FontResources.ReadLines("cffdata.bin"))
        {
            string[] f = line.Split(' ');
            switch (f[0])
            {
                case "S":
                    strings[int.Parse(f[1], CultureInfo.InvariantCulture)] = f[2];
                    break;
                case "E":
                    d.ExpertEncodingSids[int.Parse(f[1], CultureInfo.InvariantCulture)] = int.Parse(f[2], CultureInfo.InvariantCulture);
                    break;
                case "X":
                    d.ExpertCharset = ParseList(f);
                    break;
                case "Y":
                    d.ExpertSubsetCharset = ParseList(f);
                    break;
            }
        }

        d.Strings = strings;
        return d;
    }

    private static int[] ParseList(string[] f)
    {
        var r = new int[f.Length - 1];
        for (int i = 1; i < f.Length; i++)
        {
            r[i - 1] = int.Parse(f[i], CultureInfo.InvariantCulture);
        }

        return r;
    }
}
