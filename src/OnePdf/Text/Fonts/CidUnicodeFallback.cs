using System.Collections.Concurrent;
using OnePdf.FontData;
using OnePdf.FontPrograms;

namespace OnePdf.PdfFonts;

/// <summary>
/// Unicode for CIDs that the character-collection table has no entry for: the vertical variants (CIDs that only the <c>-V</c>
/// CMaps use) and the half-width Latin forms (CIDs that only the half-width and legacy single-byte CMaps use). Each is found by
/// reading a predefined CMap backwards: a Unicode CMap's two-byte code is the character; a legacy CMap's single-byte codes
/// 0x20 to 0x7E are ASCII.
/// </summary>
internal static class CidUnicodeFallback
{
    private const int MaxSpan = 1 << 16;

    private static readonly (string Ordering, string CMap, bool UnicodeCoded)[] Sources =
    [
        ("Japan1", "UniJIS-UCS2-HW-H", true),
        ("Japan1", "UniJIS-UCS2-V", true),
        ("Japan1", "UniJIS-UTF16-V", true),
        ("Japan1", "UniJISPro-UCS2-V", true),
        ("Japan1", "UniJIS2004-UTF16-V", true),
        ("Japan1", "90ms-RKSJ-H", false),
        ("GB1", "UniGB-UCS2-V", true),
        ("GB1", "UniGB-UTF16-V", true),
        ("GB1", "GBK-EUC-H", false),
        ("GB1", "GBpc-EUC-H", false),
        ("CNS1", "UniCNS-UCS2-V", true),
        ("CNS1", "UniCNS-UTF16-V", true),
        ("CNS1", "ETen-B5-H", false),
        ("CNS1", "B5pc-H", false),
        ("Korea1", "UniKS-UCS2-V", true),
        ("Korea1", "UniKS-UTF16-V", true),
        ("Korea1", "KSCms-UHC-HW-H", false),
        ("Korea1", "KSC-EUC-H", false),
        ("KR", "UniAKR-UTF16-H", true),
    ];

    private static readonly ConcurrentDictionary<string, Dictionary<int, int>> Tables = new(StringComparer.Ordinal);

    /// <summary>The code point for a CID of the ordering (<c>Japan1</c>, <c>GB1</c>, <c>CNS1</c>, <c>Korea1</c>, <c>KR</c>), or 0.</summary>
    public static int Lookup(string? ordering, int cid)
    {
        if (ordering is null)
        {
            return 0;
        }

        Dictionary<int, int> t = Tables.GetOrAdd(ordering, Build);
        return t.GetValueOrDefault(cid);
    }

    private static Dictionary<int, int> Build(string ordering)
    {
        var map = new Dictionary<int, int>();
        foreach ((string o, string name, bool unicodeCoded) in Sources)
        {
            if (o != ordering)
            {
                continue;
            }

            CMap? cmap;
            try
            {
                cmap = PredefinedCMaps.TryGet(name);
            }
            catch (FontProgramException)
            {
                continue;
            }

            if (cmap is null)
            {
                continue;
            }

            foreach ((int length, uint lo, uint hi, int cid) in cmap.EnumerateCidRanges())
            {
                if (unicodeCoded ? length != 2 : length != 1)
                {
                    continue;
                }

                uint from = unicodeCoded ? lo : Math.Max(lo, 0x20u);
                uint to = unicodeCoded ? hi : Math.Min(hi, 0x7Eu);
                if (to < from || to - from > MaxSpan || cid < 0)
                {
                    continue;
                }

                for (uint c = from; c <= to; c++)
                {
                    // The first source wins; surrogate code units are not characters.
                    if (c is >= 0xD800 and <= 0xDFFF)
                    {
                        continue;
                    }

                    map.TryAdd(cid + (int)(c - lo), (int)c);
                }
            }
        }

        return map;
    }
}
