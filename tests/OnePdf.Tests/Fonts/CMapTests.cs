using System.Text;
using OnePdf.FontData;

namespace OnePdf.Tests.Fonts;

public class CMapTests
{
    private static CMap Parse(string text, CMapParser.UseCMapResolver? r = null) => CMapParser.Parse(Encoding.Latin1.GetBytes(text), r);

    private const string ToUnicode = """
        /CIDInit /ProcSet findresource begin
        12 dict begin
        begincmap
        /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
        /CMapName /Adobe-Identity-UCS def
        /CMapType 2 def
        1 begincodespacerange
        <0000> <FFFF>
        endcodespacerange
        3 beginbfchar
        <0003> <0020>
        <0005> <D835DC00>
        <0007> <00660069>
        endbfchar
        4 beginbfrange
        <0010> <0012> <0041>
        <0020> <0022> [<0061> <00620063> <D835DC01>]
        <0030> <0030> <0035>
        <0040> <0042> <D835DC00>
        endbfrange
        endcmap
        CMapName currentdict /CMap defineresource pop
        end
        end
        """;

    [Fact]
    public void ToUnicodeCharsRangesArraysAndSurrogates()
    {
        CMap m = Parse(ToUnicode);
        Assert.Equal("Adobe-Identity-UCS", m.Name);
        Assert.Equal("Adobe", m.Registry);
        Assert.Equal("UCS", m.Ordering);
        Assert.Equal(0, m.WMode);

        string T(uint code)
        {
            Assert.True(m.TryGetUnicode(code, 2, out string s), "code " + code.ToString("X4"));
            return s;
        }

        Assert.Equal(" ", T(3));
        Assert.Equal("\U0001D400", T(5)); // surrogate pair kept as one character
        Assert.Equal("fi", T(7)); // several characters from one code
        Assert.Equal("A", T(0x10));
        Assert.Equal("B", T(0x11));
        Assert.Equal("C", T(0x12));
        Assert.Equal("a", T(0x20));
        Assert.Equal("bc", T(0x21));
        Assert.Equal("\U0001D401", T(0x22));
        Assert.Equal("5", T(0x30));
        // The last UTF-16 unit counts up, so a range over a surrogate pair walks the astral plane.
        Assert.Equal("\U0001D400", T(0x40));
        Assert.Equal("\U0001D401", T(0x41));
        Assert.Equal("\U0001D402", T(0x42));
        Assert.False(m.TryGetUnicode(0x99, 2, out _));
        Assert.False(m.TryGetUnicode(0x23, 2, out _));
        // A 1-byte lookup of a 2-byte entry still resolves: ToUnicode streams often disagree with the font about code length.
        Assert.True(m.TryGetUnicode(0x10, 1, out string one));
        Assert.Equal("A", one);
        Assert.Equal(3 + 4, m.UnicodeEntryCount);
        Assert.Equal(13, m.EnumerateUnicode().Count);
    }

    [Fact]
    public void BfCharWithGlyphNameDestination()
    {
        CMap m = Parse("1 begincodespacerange <00> <FF> endcodespacerange 2 beginbfchar <41> /A <42> /uni20AC endbfchar");
        Assert.True(m.TryGetUnicode(0x41, 1, out string a));
        Assert.Equal("A", a);
        Assert.True(m.TryGetUnicode(0x42, 1, out string e));
        Assert.Equal("\u20AC", e);
    }

    [Fact]
    public void UseCMapFallsBackToTheParent()
    {
        string text = """
            /90ms-RKSJ-H usecmap
            /CMapName /Child def
            /WMode 1 def
            1 begincidchar
            <8140> 5000
            endcidchar
            """;
        CMap m = Parse(text);
        Assert.Equal("Child", m.Name);
        Assert.Equal(1, m.WMode);
        Assert.NotNull(m.UseCMap);
        Assert.Equal("90ms-RKSJ-H", m.UseCMap!.Name);
        Assert.Null(m.UnresolvedUseCMap);
        Assert.Equal(5000, m.ToCid(0x8140, 2)); // overridden by the child
        Assert.Equal(843, m.ToCid(0x82A0, 2)); // inherited
        Assert.True(m.HasCidMappings);
        // Codespace ranges of the parent apply.
        Assert.True(m.ReadCode([0x82, 0xA0], out uint code, out int len, out bool valid));
        Assert.Equal((0x82A0u, 2, true), (code, len, valid));
    }

    [Fact]
    public void UseCMapWithCustomResolverAndUnknownName()
    {
        CMap parent = Parse("1 begincodespacerange <00> <FF> endcodespacerange 1 begincidrange <00> <FF> 100 endcidrange");
        CMap child = Parse("/Mine usecmap 1 begincidchar <41> 7 endcidchar", n => n == "Mine" ? parent : null);
        Assert.Same(parent, child.UseCMap);
        Assert.Equal(7, child.ToCid(0x41, 1));
        Assert.Equal(100 + 0x42, child.ToCid(0x42, 1));
        CMap orphan = Parse("/Nope usecmap", _ => null);
        Assert.Null(orphan.UseCMap);
        Assert.Equal("Nope", orphan.UnresolvedUseCMap);
    }

    [Fact]
    public void CodespaceRulesSplitStrings()
    {
        CMap m = PredefinedCMaps.TryGet("90ms-RKSJ-H")!;
        byte[] s = [0x41, 0x82, 0xA0, 0xB1, 0x81, 0x20, 0x82];
        var codes = new List<(uint, int, bool)>();
        ReadOnlySpan<byte> rest = s;
        while (m.ReadCode(rest, out uint code, out int len, out bool valid))
        {
            codes.Add((code, len, valid));
            rest = rest[len..];
        }

        Assert.Equal(
            [(0x41u, 1, true), (0x82A0u, 2, true), (0xB1u, 1, true), (0x8120u, 2, false), (0x82u, 1, false)],
            codes);
        Assert.False(m.ReadCode([], out _, out _, out _));
    }

    [Fact]
    public void EveryPredefinedCMapLoads()
    {
        IReadOnlyCollection<string> names = PredefinedCMaps.Names;
        Assert.True(names.Count >= 150, "count " + names.Count);
        foreach (string required in new[]
        {
            "Identity-H", "Identity-V", "GB-EUC-H", "GB-EUC-V", "GBpc-EUC-H", "GBK-EUC-H", "GBKp-EUC-H", "GBK2K-H", "UniGB-UCS2-H", "UniGB-UTF16-H",
            "B5pc-H", "HKscs-B5-H", "ETen-B5-H", "ETenms-B5-H", "CNS-EUC-H", "UniCNS-UCS2-H", "UniCNS-UTF16-H",
            "83pv-RKSJ-H", "90ms-RKSJ-H", "90ms-RKSJ-V", "90msp-RKSJ-H", "90pv-RKSJ-H", "Add-RKSJ-H", "EUC-H", "Ext-RKSJ-H", "H", "V",
            "UniJIS-UCS2-H", "UniJIS-UCS2-HW-H", "UniJIS-UTF16-H", "KSC-EUC-H", "KSCms-UHC-H", "KSCms-UHC-HW-H", "KSCpc-EUC-H",
            "UniKS-UCS2-H", "UniKS-UTF16-H", "UniAKR-UTF16-H",
        })
        {
            Assert.Contains(required, names);
        }

        foreach (string name in names)
        {
            CMap m = PredefinedCMaps.TryGet(name) ?? throw new InvalidOperationException(name);
            Assert.Equal(name, m.Name);
            Assert.NotEmpty(m.CodespaceRanges);
            Assert.True(m.HasCidMappings, name);
            Assert.Equal(name.EndsWith("-V", StringComparison.Ordinal) || name == "V" ? 1 : 0, m.WMode);
            Assert.Null(m.UnresolvedUseCMap);
        }

        Assert.Null(PredefinedCMaps.TryGet("Not-A-CMap"));
        Assert.Same(PredefinedCMaps.TryGet("H"), PredefinedCMaps.TryGet("H"));
    }

    [Fact]
    public void SpotCheckedCodesPerOrdering()
    {
        // Japan1
        CMap sj = PredefinedCMaps.TryGet("90ms-RKSJ-H")!;
        Assert.Equal(633, sj.ToCid(0x8140, 2));
        Assert.Equal(843, sj.ToCid(0x82A0, 2));
        CMap ujis = PredefinedCMaps.TryGet("UniJIS-UCS2-H")!;
        Assert.Equal(843, ujis.ToCid(0x3042, 2));
        Assert.Equal("Japan1", ujis.Ordering);
        CMap u16 = PredefinedCMaps.TryGet("UniJIS-UTF16-H")!;
        Assert.Equal(843, u16.ToCid(0x3042, 2));
        Assert.True(u16.ToCid(0xD840DC0B, 4) > 0); // U+2000B, a supplementary-plane code
        CMap vert = PredefinedCMaps.TryGet("UniJIS-UCS2-V")!;
        Assert.Equal(1, vert.WMode);
        Assert.Same(ujis, vert.UseCMap);
        Assert.NotEqual(ujis.ToCid(0x3001, 2), vert.ToCid(0x3001, 2)); // vertical punctuation has its own CID

        // Identity
        CMap id = PredefinedCMaps.TryGet("Identity-H")!;
        Assert.Equal(0x1234, id.ToCid(0x1234, 2));
        Assert.Equal("Identity", id.Ordering);

        // GB1, CNS1, Korea1, KR: a Han or Hangul code through the Unicode CMap, then CID back to Unicode.
        AssertRoundTrip("UniGB-UCS2-H", "GB1", 2, 0x4E00, 0x4E2D, 0x6587, 0x0041);
        AssertRoundTrip("GBK-EUC-H", "GB1", 2, 0xB0A1);
        AssertRoundTrip("UniCNS-UCS2-H", "CNS1", 2, 0x4E00, 0x4E2D, 0x6587, 0x0041);
        AssertRoundTrip("UniKS-UCS2-H", "Korea1", 2, 0xAC00, 0xD55C, 0x4E00, 0x0041);
        AssertRoundTrip("UniAKR-UTF16-H", "KR", 2, 0xAC00, 0xD55C, 0x0041);
        AssertRoundTrip("UniJIS-UCS2-H", "Japan1", 2, 0x3042, 0x4E00, 0x65E5, 0x672C, 0x0041);

        // The proportional Latin block starts at CID 1 = space in every collection.
        foreach (string o in CidToUnicode.Orderings)
        {
            CidToUnicode t = CidToUnicode.Get(o)!;
            Assert.Equal(0x20, t.Lookup(1));
            Assert.Equal(0x41, t.Lookup(34));
            Assert.Equal(0, t.Lookup(0));
            Assert.Equal(0, t.Lookup(-1));
            Assert.Equal(0, t.Lookup(t.Count));
        }

        Assert.Equal(0x3042, CidToUnicode.Get("Japan1")!.Lookup(843));
        Assert.Null(CidToUnicode.Get("Identity"));
        Assert.Null(CidToUnicode.Get(null));
    }

    private static void AssertRoundTrip(string cmapName, string ordering, int length, params int[] codes)
    {
        CMap m = PredefinedCMaps.TryGet(cmapName)!;
        Assert.Equal(ordering, m.Ordering);
        CidToUnicode t = CidToUnicode.Get(ordering)!;
        bool unicodeCMap = cmapName.StartsWith("Uni", StringComparison.Ordinal);
        foreach (int code in codes)
        {
            int cid = m.ToCid((uint)code, length);
            Assert.True(cid > 0, cmapName + " " + code.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(cid < t.Count);
            if (unicodeCMap)
            {
                Assert.Equal(code, t.Lookup(cid));
            }
            else
            {
                Assert.True(t.Lookup(cid) > 0);
            }
        }
    }

    [Fact]
    public void PredefinedMapsEqualTheirUpstreamSource()
    {
        string? up = FontEnv.UpstreamDir;
        if (up is null || !Directory.Exists(Path.Combine(up, "cmr")))
        {
            Assert.Skip("Set ONEPDF_UPSTREAM to the unpacked upstream data (tools/fontdata/fetch.sh).");
        }

        int checkedMaps = 0;
        foreach (string dir in Directory.EnumerateDirectories(Path.Combine(up, "cmr"), "Adobe-*").Where(d => !d.Contains("Manga1", StringComparison.Ordinal)))
        {
            string cm = Path.Combine(dir, "CMap");
            if (!Directory.Exists(cm))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(cm))
            {
                string name = Path.GetFileName(file);
                CMap stored = PredefinedCMaps.TryGet(name) ?? throw new InvalidOperationException("missing " + name);
                CMap text = CMapParser.Parse(File.ReadAllBytes(file), n => n == name ? null : PredefinedCMaps.TryGet(n));
                Assert.Equal(text.WMode, stored.WMode);
                Assert.Equal(text.CodespaceRanges.OrderBy(r => r.Length).ThenBy(r => r.Lo), stored.CodespaceRanges.OrderBy(r => r.Length).ThenBy(r => r.Lo));
                Assert.Equal(text.EnumerateCidRanges().Select(r => (r.Length, r.Lo, r.Hi, r.Cid)).OrderBy(r => r.Length).ThenBy(r => r.Lo), stored.EnumerateCidRanges().Select(r => (r.Length, r.Lo, r.Hi, r.Cid)).OrderBy(r => r.Length).ThenBy(r => r.Lo));
                Assert.Equal(text.UseCMap?.Name, stored.UseCMap?.Name);
                checkedMaps++;
            }
        }

        Assert.Equal(PredefinedCMaps.Names.Count, checkedMaps);
    }

    [Fact]
    public void GarbageNeverThrows()
    {
        var rnd = new Random(99);
        string[] words = ["begincodespacerange", "endcodespacerange", "begincidrange", "endcidrange", "begincidchar", "beginbfrange", "endbfrange", "beginbfchar", "endbfchar", "usecmap", "/WMode", "def", "[", "]", "<", ">", "<<", ">>", "(", ")", "1", "99999999999", "-5", "<41>", "<4142434445>", "<>", "/X", "<0000>", "<FFFF>", "<D835>", "%c\n"];
        for (int i = 0; i < 3000; i++)
        {
            var sb = new StringBuilder();
            int n = rnd.Next(1, 40);
            for (int k = 0; k < n; k++)
            {
                sb.Append(words[rnd.Next(words.Length)]).Append(' ');
            }

            CMap m = Parse(sb.ToString());
            m.TryGetUnicode((uint)rnd.Next(), rnd.Next(0, 6) is var l && l is >= 1 and <= 4 ? l : 2, out _);
            m.ToCid((uint)rnd.Next(), 2);
            m.ReadCode([1, 2, 3], out _, out _, out _);
        }
    }

    [Fact]
    public void EnormousClaimsAreNotTrusted()
    {
        // A count of two billion before a short section allocates nothing; entries are capped.
        CMap m = Parse("2000000000 begincidrange <00> <FF> 1 endcidrange 2000000000 beginbfrange <00> <FF> <0041> endbfrange");
        Assert.Equal(1, m.CidRangeCount);
        var sb = new StringBuilder("1 begincodespacerange <0000> <FFFF> endcodespacerange 1 beginbfchar\n");
        for (int i = 0; i < 20000; i++)
        {
            sb.Append('<').Append(i.ToString("X4", System.Globalization.CultureInfo.InvariantCulture)).Append("> <0041>\n");
        }

        CMap big = Parse(sb.ToString());
        Assert.Equal(20000, big.UnicodeEntryCount);
    }
}
