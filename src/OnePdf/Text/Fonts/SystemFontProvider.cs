using System.Linq;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OnePdf.FontPrograms;

namespace OnePdf;

/// <summary>
/// Finds substitutes among the fonts installed on this machine. The font directories of the operating system are listed once,
/// lazily, and each font file's <c>name</c> table is read to build a small index (family, style, PostScript and full name); the
/// fonts themselves are read only when a PDF needs one. Nothing is fetched from a network and no other files are opened.
/// </summary>
/// <remarks>
/// Matching, in order: the PostScript or full name; the family (for the standard families Helvetica, Times and Courier the
/// metric-compatible and common clones such as Arial, Liberation, Nimbus and Free fonts); for CJK collections a list of common
/// CJK families by character collection. A final generic stage (<see cref="FindGeneric"/>) picks any sans, serif or monospace
/// family by the descriptor's flags. The environment variable <c>ONEPDF_FONT_DIRS</c> (path-separator list) adds directories.
/// </remarks>
internal sealed class SystemFontProvider : IPdfFontProvider
{
    private const long MaxFontFileBytes = 128L << 20;
    private const int MaxFiles = 20000;
    private const int MaxNameTableBytes = 1 << 20;

    private static readonly string[] SansFamilies = ["arial", "helvetica", "liberationsans", "arimo", "nimbussans", "nimbussansl", "freesans", "texgyreheros"];
    private static readonly string[] SerifFamilies = ["timesnewroman", "times", "liberationserif", "tinos", "nimbusroman", "nimbusromno9l", "freeserif", "texgyretermes"];
    private static readonly string[] MonoFamilies = ["couriernew", "courier", "liberationmono", "cousine", "nimbusmono", "nimbusmonops", "nimbusmonol", "freemono", "texgyrecursor"];
    private static readonly string[] SymbolFamilies = ["symbol", "standardsymbolsps", "standardsymbolsl", "texgyrechorus"];
    private static readonly string[] DingbatFamilies = ["zapfdingbats", "dingbats", "d050000l"];
    private static readonly string[] GenericSans = ["dejavusans", "notosans", "opensans", "verdana", "tahoma", "segoeui", "roboto", "ubuntu", "cantarell"];
    private static readonly string[] GenericSerif = ["dejavuserif", "notoserif", "georgia", "cambria"];
    private static readonly string[] GenericMono = ["dejavusansmono", "notosansmono", "consolas", "menlo", "monaco"];
    private static readonly string[] GenericSymbols = ["notosanssymbols2", "notosanssymbols", "notosansmath", "dejavusans", "segoeuisymbol"];

    private static readonly Dictionary<string, string[]> CjkFamilies = new(StringComparer.Ordinal)
    {
        ["Japan1"] = ["notosanscjkjp", "notoserifcjkjp", "notosansjp", "notoserifjp", "sourcehansansjp", "sourcehanserifjp", "ipaexgothic", "ipagothic", "ipaexmincho", "ipamincho", "takaogothic", "takaomincho", "vlgothic", "msgothic", "msmincho", "yugothic", "yumincho", "meiryo", "hiraginosans", "hiraginokaku", "hiraginomin", "kozuka", "droidsansfallback", "unifont"],
        ["GB1"] = ["notosanscjksc", "notoserifcjksc", "notosanssc", "notoserifsc", "sourcehansanssc", "sourcehanserifsc", "simsun", "simhei", "nsimsun", "microsoftyahei", "pingfangsc", "stsong", "stheiti", "wenquanyi", "arplumingcn", "arplukaicn", "droidsansfallback", "unifont"],
        ["CNS1"] = ["notosanscjktc", "notoserifcjktc", "notosanstc", "notoseriftc", "sourcehansanstc", "sourcehanseriftc", "pingfangtc", "mingliu", "pmingliu", "microsoftjhenghei", "arplumingtw", "arplukaitw", "wenquanyi", "droidsansfallback", "unifont"],
        ["Korea1"] = ["notosanscjkkr", "notoserifcjkkr", "notosanskr", "notoserifkr", "sourcehansanskr", "sourcehanserifkr", "nanumgothic", "nanummyeongjo", "malgungothic", "batang", "gulim", "applegothic", "baekmuk", "unbatang", "undotum", "droidsansfallback", "unifont"],
    };

    private readonly Lazy<Entry[]> _index = new(BuildIndex, LazyThreadSafetyMode.ExecutionAndPublication);

    public static SystemFontProvider Instance { get; } = new();

    private sealed record Entry(string Path, int Face, string PostScript, string Full, string Family, string Style, bool Bold, bool Italic)
    {
        public string NormPostScript { get; } = Normalize(PostScript);

        public string NormFull { get; } = Normalize(Full);

        public string NormFamily { get; } = Normalize(Family);
    }

    /// <inheritdoc />
    public PdfSubstituteFont? FindFont(PdfFontRequest request) => Find(request, generic: false);

    /// <summary>The last-resort stage: any sans, serif or monospace family chosen by the request's flags, or a font with symbols.</summary>
    public PdfSubstituteFont? FindGeneric(PdfFontRequest request) => Find(request, generic: true);

    private PdfSubstituteFont? Find(PdfFontRequest request, bool generic)
    {
        Entry[] index = _index.Value;
        if (index.Length == 0)
        {
            return null;
        }

        // Documents repeat the same few faces under many font objects; answer repeats from a small table.
        string key = string.Join('|', generic ? 'g' : 's', request.FontName, request.FamilyName, request.StandardName, request.IsBold, request.IsItalic, request.IsSerif, request.IsFixedPitch, request.IsSymbolic, request.CidOrdering);
        if (_answers.TryGetValue(key, out Entry? known))
        {
            return known is null ? null : Wrap(known);
        }

        Entry? best = generic ? MatchGeneric(index, request) : MatchSpecific(index, request);
        if (_answers.Count < 4096)
        {
            _answers.TryAdd(key, best);
        }

        return best is null ? null : Wrap(best);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry?> _answers = new(StringComparer.Ordinal);

    private static Entry? MatchSpecific(Entry[] index, PdfFontRequest r)
    {
        if (r.CidOrdering is not null)
        {
            if (!CjkFamilies.TryGetValue(r.CidOrdering, out string[]? cjk))
            {
                cjk = r.CidOrdering == "KR" ? CjkFamilies["Korea1"] : null;
            }

            // A CJK font named in the PDF still wins when installed.
            return Exact(index, r) ?? (cjk is null ? null : FromFamilies(index, cjk, r, prefix: true));
        }

        Entry? e = Exact(index, r);
        if (e is not null)
        {
            return e;
        }

        string std = r.StandardName ?? string.Empty;
        string[]? group = std.StartsWith("Helvetica", StringComparison.Ordinal) ? SansFamilies
            : std.StartsWith("Times", StringComparison.Ordinal) ? SerifFamilies
            : std.StartsWith("Courier", StringComparison.Ordinal) ? MonoFamilies
            : std == "Symbol" ? SymbolFamilies
            : std == "ZapfDingbats" ? DingbatFamilies
            : null;
        if (group is not null)
        {
            return FromFamilies(index, group, r, prefix: false);
        }

        string fam = Normalize(r.FamilyName);
        return fam.Length == 0 ? null : FromFamilies(index, [fam], r, prefix: false);
    }

    private static Entry? MatchGeneric(Entry[] index, PdfFontRequest r)
    {
        if (r.CidOrdering is not null)
        {
            return FromFamilies(index, GenericSans, r, prefix: false);
        }

        string std = r.StandardName ?? string.Empty;
        if (std is "Symbol" or "ZapfDingbats" || r.IsSymbolic)
        {
            return FromFamilies(index, GenericSymbols, r, prefix: false);
        }

        string[] first = r.IsFixedPitch ? GenericMono : r.IsSerif ? GenericSerif : GenericSans;
        return FromFamilies(index, first, r, prefix: false)
            ?? FromFamilies(index, r.IsFixedPitch ? MonoFamilies : r.IsSerif ? SerifFamilies : SansFamilies, r, prefix: false);
    }

    private static Entry? Exact(Entry[] index, PdfFontRequest r)
    {
        string n = Normalize(r.FontName);
        if (n.Length == 0)
        {
            return null;
        }

        foreach (Entry e in index)
        {
            if (e.NormPostScript == n || e.NormFull == n)
            {
                return e;
            }
        }

        return null;
    }

    private static Entry? FromFamilies(Entry[] index, string[] families, PdfFontRequest r, bool prefix)
    {
        Entry? fallback = null;
        foreach (string fam in families)
        {
            Entry? bestHere = null;
            int bestScore = -1;
            foreach (Entry e in index)
            {
                string ef = e.NormFamily;
                if (prefix ? !ef.StartsWith(fam, StringComparison.Ordinal) : ef != fam)
                {
                    continue;
                }

                int score = (e.Bold == r.IsBold ? 2 : 0) + (e.Italic == r.IsItalic ? 2 : 0) + (IsRegularStyle(e.Style) ? 1 : 0);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHere = e;
                }
            }

            if (bestHere is null)
            {
                continue;
            }

            if (bestScore >= 4)
            {
                return bestHere;
            }

            fallback ??= bestHere;
        }

        return fallback;
    }

    private static bool IsRegularStyle(string style) => style.Length == 0 || style.Equals("Regular", StringComparison.OrdinalIgnoreCase) || style.Equals("Roman", StringComparison.OrdinalIgnoreCase) || style.Equals("Book", StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    private static PdfSubstituteFont Wrap(Entry e) =>
        new(() => ReadFile(e.Path), e.PostScript.Length > 0 ? e.PostScript : e.Full, e.Path + "#" + e.Face, e.Face);

    private static byte[] ReadFile(string path)
    {
        try
        {
            using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = RandomAccess.GetLength(h);
            if (len < 12 || len > MaxFontFileBytes)
            {
                return [];
            }

            byte[] data = new byte[(int)len];
            int got = 0;
            while (got < data.Length)
            {
                int n = RandomAccess.Read(h, data.AsSpan(got), got);
                if (n <= 0)
                {
                    return [];
                }

                got += n;
            }

            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Entry[] BuildIndex()
    {
        var dirs = new List<string>();
        string? extra = Environment.GetEnvironmentVariable("ONEPDF_FONT_DIRS");
        if (!string.IsNullOrEmpty(extra))
        {
            dirs.AddRange(extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            dirs.AddRange(["/System/Library/Fonts", "/Library/Fonts", Path.Combine(home, "Library", "Fonts")]);
        }
        else
        {
            dirs.AddRange(["/usr/share/fonts", "/usr/local/share/fonts", Path.Combine(home, ".fonts"), Path.Combine(home, ".local", "share", "fonts")]);
        }

        var result = new List<Entry>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8, AttributesToSkip = FileAttributes.Hidden };
        int files = 0;
        foreach (string dir in dirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(dir, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in paths.OrderBy(static p => p, StringComparer.Ordinal))
            {
                string ext = Path.GetExtension(path);
                if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".otc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (++files > MaxFiles)
                {
                    return [.. result];
                }

                try
                {
                    ReadNames(path, result);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FontProgramException)
                {
                    // An unreadable or damaged file is simply not in the index.
                }
            }
        }

        return [.. result];
    }

    private static void ReadNames(string path, List<Entry> into)
    {
        using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = RandomAccess.GetLength(h);
        if (len < 16)
        {
            return;
        }

        Span<byte> head = stackalloc byte[12];
        if (RandomAccess.Read(h, head, 0) < 12)
        {
            return;
        }

        var offsets = new List<uint>();
        if (Be.U32(head, 0) == 0x74746366)
        {
            uint n = Math.Min(Be.U32(head, 8), 32u);
            Span<byte> o = stackalloc byte[4];
            for (int i = 0; i < n; i++)
            {
                if (RandomAccess.Read(h, o, 12 + (4L * i)) < 4)
                {
                    break;
                }

                offsets.Add(Be.U32(o, 0));
            }
        }
        else
        {
            offsets.Add(0);
        }

        for (int face = 0; face < offsets.Count; face++)
        {
            long at = offsets[face];
            byte[] dir = new byte[12];
            if (at + 12 > len || RandomAccess.Read(h, dir, at) < 12)
            {
                continue;
            }

            uint sig = Be.U32(dir, 0);
            if (sig is not (0x00010000 or 0x4F54544F or 0x74727565))
            {
                continue;
            }

            int tables = Be.U16(dir, 4);
            byte[] recs = new byte[tables * 16];
            if (RandomAccess.Read(h, recs, at + 12) < recs.Length)
            {
                continue;
            }

            for (int t = 0; t < tables; t++)
            {
                if (Be.U32(recs, t * 16) != 0x6E616D65)
                {
                    continue;
                }

                uint off = Be.U32(recs, (t * 16) + 8);
                uint size = Be.U32(recs, (t * 16) + 12);
                if (size < 6 || size > MaxNameTableBytes || off + (long)size > len)
                {
                    break;
                }

                byte[] table = new byte[size];
                if (RandomAccess.Read(h, table, off) < table.Length)
                {
                    break;
                }

                string family = SfntName.Get(table, 16) ?? SfntName.Get(table, 1) ?? string.Empty;
                string style = SfntName.Get(table, 17) ?? SfntName.Get(table, 2) ?? string.Empty;
                string ps = SfntName.Get(table, 6) ?? string.Empty;
                string full = SfntName.Get(table, 4) ?? string.Empty;
                bool bold = style.Contains("Bold", StringComparison.OrdinalIgnoreCase) || style.Contains("Black", StringComparison.OrdinalIgnoreCase) || style.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
                bool italic = style.Contains("Italic", StringComparison.OrdinalIgnoreCase) || style.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                if (family.Length > 0)
                {
                    into.Add(new Entry(path, face, ps, full, family, style, bold, italic));
                }

                break;
            }
        }
    }
}
