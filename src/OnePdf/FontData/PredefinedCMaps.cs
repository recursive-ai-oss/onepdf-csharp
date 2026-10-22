using System.Collections.Concurrent;
using System.Text;
using OnePdf.FontPrograms;

namespace OnePdf.FontData;

/// <summary>
/// The predefined CMaps of ISO 32000-1 Table 118 and the rest of Adobe's cmap-resources for Adobe-Japan1, GB1, CNS1, Korea1 and
/// KR (and Identity-H/V), stored compressed in the assembly. The directory loads on first use; each CMap is inflated and decoded
/// only when asked for, and then cached. Thread-safe.
/// </summary>
internal static class PredefinedCMaps
{
    private sealed record Entry(int Offset, int Length, int RawLength);

    private static readonly Lazy<(byte[] Blob, int Base, Dictionary<string, Entry> Index)> Dir = new(LoadDirectory, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, CMap> Cache = new(StringComparer.Ordinal);

    /// <summary>The names of all stored CMaps, sorted.</summary>
    public static IReadOnlyCollection<string> Names
    {
        get
        {
            var names = new List<string>(Dir.Value.Index.Keys);
            names.Sort(StringComparer.Ordinal);
            return names;
        }
    }

    /// <summary>True when a CMap of this name is stored.</summary>
    public static bool Contains(string name) => Dir.Value.Index.ContainsKey(name);

    /// <summary>Loads a predefined CMap by name (<c>90ms-RKSJ-H</c>, <c>UniJIS-UCS2-V</c>, <c>Identity-H</c>...), or returns null.</summary>
    public static CMap? TryGet(string name)
    {
        if (Cache.TryGetValue(name, out CMap? hit))
        {
            return hit;
        }

        (byte[] blob, int baseOffset, Dictionary<string, Entry> index) = Dir.Value;
        if (!index.ContainsKey(name))
        {
            return null;
        }

        return Load(name, blob, baseOffset, index, 0);
    }

    private static CMap Load(string name, byte[] blob, int baseOffset, Dictionary<string, Entry> index, int depth)
    {
        if (Cache.TryGetValue(name, out CMap? cached))
        {
            return cached;
        }

        if (depth > 8)
        {
            throw new FontProgramException("Predefined CMaps are nested too deeply.");
        }

        Entry e = index[name];
        byte[] raw = FontResources.Inflate(blob.AsSpan(baseOffset + e.Offset, e.Length), e.RawLength);
        CMap map = CMap.DecodeStored(raw, out string? useName);
        map.Name = name;
        if (name.StartsWith("Identity", StringComparison.Ordinal))
        {
            map.Registry = "Adobe";
            map.Ordering = "Identity";
        }
        else
        {
            map.Registry = "Adobe";
            map.Ordering = OrderingOf(name);
        }

        if (useName is not null && index.ContainsKey(useName))
        {
            map.UseCMap = Load(useName, blob, baseOffset, index, depth + 1);
        }

        return Cache.GetOrAdd(name, map);
    }

    // The character collection a CMap belongs to follows from its name (Adobe's naming scheme in cmap-resources).
    private static string? OrderingOf(string name)
    {
        string[] gb = ["GB-", "GBpc-", "GBK-", "GBKp-", "GBK2K-", "UniGB-"];
        string[] cns = ["B5pc-", "HKscs-B5-", "ETen-B5-", "ETenms-B5-", "CNS-EUC-", "UniCNS-", "CNS1-", "Adobe-CNS1-"];
        string[] kr = ["KSC-", "KSCms-", "KSCpc-", "UniKS-", "Adobe-Korea1-"];
        foreach (string p in gb)
        {
            if (name.StartsWith(p, StringComparison.Ordinal))
            {
                return "GB1";
            }
        }

        foreach (string p in cns)
        {
            if (name.StartsWith(p, StringComparison.Ordinal))
            {
                return "CNS1";
            }
        }

        foreach (string p in kr)
        {
            if (name.StartsWith(p, StringComparison.Ordinal))
            {
                return "Korea1";
            }
        }

        if (name.StartsWith("UniAKR-", StringComparison.Ordinal) || name.StartsWith("Adobe-KR-", StringComparison.Ordinal))
        {
            return "KR";
        }

        if (name.StartsWith("Adobe-GB1-", StringComparison.Ordinal))
        {
            return "GB1";
        }

        // Everything else (RKSJ, EUC-H, H, V, UniJIS, Ext, Add, Hankaku...) is Japanese.
        return "Japan1";
    }

    private static (byte[], int, Dictionary<string, Entry>) LoadDirectory()
    {
        byte[] blob = FontResources.ReadStored("cmaps.bin");
        ReadOnlySpan<byte> d = blob;
        uint count = Be.U32(d, 0);
        if (count > 4096)
        {
            throw new FontProgramException("Corrupt CMap directory.");
        }

        var index = new Dictionary<string, Entry>((int)count, StringComparer.Ordinal);
        int p = 4;
        for (uint i = 0; i < count; i++)
        {
            int nl = Be.U8(d, p++);
            string name = Encoding.ASCII.GetString(Be.Slice(d, p, nl));
            p += nl;
            index[name] = new Entry((int)Be.U32(d, p), (int)Be.U32(d, p + 4), (int)Be.U32(d, p + 8));
            p += 12;
        }

        return (blob, p, index);
    }
}

/// <summary>
/// CID to Unicode for the Adobe character collections (Japan1, GB1, CNS1, Korea1, KR), derived from the UTF-32 CMaps of
/// cmap-resources by inverting them: for a CID with several code points the smallest wins, and the Japan1 table also takes
/// CIDs that only the JIS2004 and JIS X 0213 CMaps map. Used to produce text for non-embedded and unmapped CID fonts.
/// </summary>
internal sealed class CidToUnicode
{
    private static readonly ConcurrentDictionary<string, CidToUnicode> Cache = new(StringComparer.Ordinal);

    private readonly byte[] _planes;
    private readonly int _count;

    private CidToUnicode(byte[] planes, int count)
    {
        _planes = planes;
        _count = count;
    }

    /// <summary>The ordering names with a table.</summary>
    public static readonly string[] Orderings = ["Japan1", "GB1", "CNS1", "Korea1", "KR"];

    /// <summary>Number of CIDs covered (the highest CID plus one).</summary>
    public int Count => _count;

    /// <summary>Loads the table of an ordering (<c>Japan1</c>, <c>GB1</c>, <c>CNS1</c>, <c>Korea1</c>, <c>KR</c>), or returns null for others.</summary>
    public static CidToUnicode? Get(string? ordering)
    {
        if (ordering is null || Array.IndexOf(Orderings, ordering) < 0)
        {
            return null;
        }

        return Cache.GetOrAdd(ordering, static o =>
        {
            byte[] raw = FontResources.Read("cid2uni-" + o + ".bin", 1 << 22);
            int n = (int)Be.U32(raw, 0);
            if (n < 0 || raw.Length != 4 + (3 * n))
            {
                throw new FontProgramException("Corrupt CID to Unicode table.");
            }

            return new CidToUnicode(raw, n);
        });
    }

    /// <summary>The code point for a CID, or 0 when the collection has none.</summary>
    public int Lookup(int cid)
    {
        if ((uint)cid >= (uint)_count)
        {
            return 0;
        }

        return (_planes[4 + cid] << 16) | (_planes[4 + _count + cid] << 8) | _planes[4 + (2 * _count) + cid];
    }
}
