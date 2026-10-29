using System.Buffers.Binary;
using System.Text;
using OnePdf.FontPrograms;

namespace OnePdf.FontData;

/// <summary>One range of a CMap's codespace (ISO 32000-1 9.7.6.2): codes of <see cref="Length"/> bytes whose every byte lies in the matching byte of Lo..Hi.</summary>
internal readonly record struct CodespaceRange(int Length, uint Lo, uint Hi)
{
    /// <summary>True when every byte of <paramref name="code"/> (<see cref="Length"/> bytes, most significant first) lies in the matching byte range.</summary>
    public bool Contains(uint code)
    {
        for (int i = 0; i < Length; i++)
        {
            int shift = 8 * (Length - 1 - i);
            uint b = (code >> shift) & 0xFF;
            if (b < ((Lo >> shift) & 0xFF) || b > ((Hi >> shift) & 0xFF))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A run of consecutive codes mapped to consecutive CIDs.</summary>
internal readonly record struct CidRange(uint Lo, uint Hi, int Cid, int Order);

/// <summary>A run of consecutive codes mapped to consecutive Unicode strings (the last UTF-16 unit of the base string counts up) or to an explicit list.</summary>
internal readonly record struct BfRange(uint Lo, uint Hi, string Base, string[]? List, int Order);

/// <summary>
/// A character-code map: codespace ranges, code to CID mappings (<c>cidrange</c>, <c>cidchar</c>) and code to Unicode mappings
/// (<c>bfrange</c>, <c>bfchar</c>), as used by Type 0 fonts' <c>/Encoding</c> and by <c>/ToUnicode</c> streams. Built by
/// <see cref="CMapParser"/> or loaded from the predefined set by <see cref="PredefinedCMaps"/>; immutable afterwards and safe to
/// share between threads.
/// </summary>
/// <remarks>
/// <c>usecmap</c> is modelled as a parent: lookups that find nothing in this map continue in <see cref="UseCMap"/>, and codespace
/// ranges of both apply. Codes are at most four bytes. Lookups with the wrong code length fall back to the same numeric code at
/// another length for Unicode mappings only, because ToUnicode streams in the wild disagree with their font's code length.
/// </remarks>
internal sealed class CMap
{
    private const int MaxParentDepth = 16;

    private readonly CodespaceRange[] _codespace;
    private readonly CidRange[][] _cid; // index = code length - 1
    private readonly Dictionary<long, string> _bfChars;
    private readonly BfRange[][] _bfRanges;

    internal CMap(
        string? name,
        int wmode,
        CodespaceRange[] codespace,
        CidRange[][] cid,
        Dictionary<long, string> bfChars,
        BfRange[][] bfRanges)
    {
        Name = name;
        WMode = wmode;
        _codespace = codespace;
        _cid = cid;
        _bfChars = bfChars;
        _bfRanges = bfRanges;
    }

    /// <summary><c>/CMapName</c>, when the map declares one.</summary>
    public string? Name { get; internal set; }

    /// <summary><c>/CIDSystemInfo</c> registry, e.g. <c>Adobe</c>.</summary>
    public string? Registry { get; internal set; }

    /// <summary><c>/CIDSystemInfo</c> ordering, e.g. <c>Japan1</c>, <c>Identity</c>, <c>UCS</c>.</summary>
    public string? Ordering { get; internal set; }

    /// <summary><c>/CIDSystemInfo</c> supplement.</summary>
    public int Supplement { get; internal set; }

    /// <summary>The writing mode: 0 horizontal, 1 vertical.</summary>
    public int WMode { get; internal set; }

    /// <summary>The map this one extends through <c>usecmap</c>, or null.</summary>
    public CMap? UseCMap { get; internal set; }

    /// <summary>The name given to <c>usecmap</c> when the resolver could not supply it (only set then).</summary>
    public string? UnresolvedUseCMap { get; internal set; }

    /// <summary>Number of code to CID entries in this map (not the parent).</summary>
    public int CidRangeCount
    {
        get
        {
            int n = 0;
            foreach (CidRange[] r in _cid)
            {
                n += r.Length;
            }

            return n;
        }
    }

    /// <summary>Number of Unicode entries in this map (not the parent).</summary>
    public int UnicodeEntryCount
    {
        get
        {
            int n = _bfChars.Count;
            foreach (BfRange[] r in _bfRanges)
            {
                n += r.Length;
            }

            return n;
        }
    }

    /// <summary>True when the map (or a parent) maps codes to CIDs.</summary>
    public bool HasCidMappings => CidRangeCount > 0 || (UseCMap?.HasCidMappings ?? false);

    /// <summary>True when the map (or a parent) maps codes to Unicode.</summary>
    public bool HasUnicodeMappings => UnicodeEntryCount > 0 || (UseCMap?.HasUnicodeMappings ?? false);

    private CodespaceRange[]? _allCodespace;

    /// <summary>All codespace ranges, this map's first, then the parents'. Built on first use and cached (the parent link is set while the map is being built, before any lookup).</summary>
    public CodespaceRange[] CodespaceRanges => _allCodespace ??= BuildAllCodespace();

    private CodespaceRange[] BuildAllCodespace()
    {
        List<CodespaceRange> all = [];
        CMap? m = this;
        for (int depth = 0; m is not null && depth < MaxParentDepth; depth++, m = m.UseCMap)
        {
            all.AddRange(m._codespace);
        }

        return all.ToArray();
    }

    /// <summary>
    /// Splits the next character code off a string shown with a Type 0 font (ISO 32000-1 9.7.6.2). Returns false only for an empty
    /// string. <paramref name="valid"/> is false when the bytes match no codespace range; the code then has the length of the
    /// shortest partially matching range, or of the shortest range at all, so decoding stays in step (and maps to CID 0).
    /// </summary>
    public bool ReadCode(ReadOnlySpan<byte> s, out uint code, out int length, out bool valid)
    {
        code = 0;
        length = 0;
        valid = false;
        if (s.IsEmpty)
        {
            return false;
        }

        int max = Math.Min(4, s.Length);
        bool any = false;
        uint c = 0;
        for (int n = 1; n <= max; n++)
        {
            c = (c << 8) | s[n - 1];
            foreach (CodespaceRange r in CodespaceRanges)
            {
                any = true;
                if (r.Length == n && r.Contains(c))
                {
                    code = c;
                    length = n;
                    valid = true;
                    return true;
                }
            }
        }

        if (!any)
        {
            // No codespace at all: assume two-byte codes, the common case for Type 0 fonts.
            length = Math.Min(2, s.Length);
            for (int i = 0; i < length; i++)
            {
                code = (code << 8) | s[i];
            }

            return true;
        }

        // No range matches. Consume as many bytes as the shortest range that agrees with the leading bytes would (9.7.6.2).
        int best = int.MaxValue;
        int shortest = int.MaxValue;
        foreach (CodespaceRange r in CodespaceRanges)
        {
            shortest = Math.Min(shortest, r.Length);
            if (r.Length <= s.Length && FirstByteMatches(r, s[0]))
            {
                best = Math.Min(best, r.Length);
            }
        }

        length = Math.Min(best != int.MaxValue ? best : shortest, s.Length);
        for (int i = 0; i < length; i++)
        {
            code = (code << 8) | s[i];
        }

        return true;
    }

    private static bool FirstByteMatches(CodespaceRange r, byte first)
    {
        int shift = 8 * (r.Length - 1);
        return first >= ((r.Lo >> shift) & 0xFF) && first <= ((r.Hi >> shift) & 0xFF);
    }

    /// <summary>The CID for a code of <paramref name="length"/> bytes, or -1 when this map and its parents leave it unmapped.</summary>
    public int ToCid(uint code, int length)
    {
        CMap? m = this;
        for (int depth = 0; m is not null && depth < MaxParentDepth; depth++, m = m.UseCMap)
        {
            if ((uint)(length - 1) >= 4)
            {
                return -1;
            }

            CidRange[] ranges = m._cid[length - 1];
            int lo = 0, hi = ranges.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (ranges[mid].Lo <= code)
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            // Overlapping ranges are rare; look back a little so the nearest enclosing one wins.
            for (int k = found; k >= 0 && k > found - 16; k--)
            {
                if (code <= ranges[k].Hi)
                {
                    return ranges[k].Cid + (int)(code - ranges[k].Lo);
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// The Unicode text for a code from <c>bfchar</c>/<c>bfrange</c>, or false. Tries the exact length first, then the same
    /// numeric code at other lengths.
    /// </summary>
    public bool TryGetUnicode(uint code, int length, out string text)
    {
        CMap? m = this;
        for (int depth = 0; m is not null && depth < MaxParentDepth; depth++, m = m.UseCMap)
        {
            if (m.TryGetUnicodeHere(code, length, out text))
            {
                return true;
            }

            for (int other = 1; other <= 4; other++)
            {
                if (other != length && m.TryGetUnicodeHere(code, other, out text))
                {
                    return true;
                }
            }
        }

        text = string.Empty;
        return false;
    }

    private bool TryGetUnicodeHere(uint code, int length, out string text)
    {
        if (_bfChars.TryGetValue(((long)length << 32) | code, out string? s))
        {
            text = s;
            return true;
        }

        BfRange[] ranges = _bfRanges[length - 1];
        int lo = 0, hi = ranges.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (ranges[mid].Lo <= code)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        for (int k = found; k >= 0 && k > found - 16; k--)
        {
            BfRange r = ranges[k];
            if (code > r.Hi)
            {
                continue;
            }

            uint offset = code - r.Lo;
            if (r.List is not null)
            {
                if (offset < r.List.Length)
                {
                    text = r.List[offset];
                    return true;
                }

                continue;
            }

            if (r.Base.Length == 0)
            {
                continue;
            }

            // The last UTF-16 unit of the destination counts up (ISO 32000-1 9.10.3).
            char last = (char)(r.Base[^1] + offset);
            text = string.Concat(r.Base.AsSpan(0, r.Base.Length - 1), new ReadOnlySpan<char>(in last));
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>The code to CID ranges of this map (not the parent) in code order per length, for tools and tests.</summary>
    public IEnumerable<(int Length, uint Lo, uint Hi, int Cid)> EnumerateCidRanges()
    {
        for (int len = 1; len <= 4; len++)
        {
            foreach (CidRange r in _cid[len - 1])
            {
                yield return (len, r.Lo, r.Hi, r.Cid);
            }
        }
    }

    /// <summary>
    /// True when a <c>bfrange</c> of this map (not the parent) runs across a boundary of its last code byte, or counts its destination past the
    /// last byte of the destination string. ISO 32000-2 9.10.3 and Adobe Technical Note #5411 define a range as incrementing the last byte only
    /// and keep a range within one block of 256 codes, so readers that follow them read such a range wrongly. OnePDF reads it as written
    /// (the whole span, carrying into the destination's last UTF-16 unit); a map is rewritten when this is true.
    /// </summary>
    public bool HasWideBfRanges()
    {
        foreach (BfRange[] ranges in _bfRanges)
        {
            foreach (BfRange r in ranges)
            {
                if ((r.Lo >> 8) != (r.Hi >> 8))
                {
                    return true;
                }

                if (r.List is null && r.Base.Length > 0 && (r.Base[^1] & 0xFF) + (r.Hi - r.Lo) > 0xFF)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Enumerates every code to Unicode mapping of this map (not the parent), expanding ranges, up to <paramref name="limit"/> entries.</summary>
    public List<(uint Code, int Length, string Text)> EnumerateUnicode(int limit = 1 << 20)
    {
        var list = new List<(uint, int, string)>();
        foreach (KeyValuePair<long, string> kv in _bfChars)
        {
            if (list.Count >= limit)
            {
                return list;
            }

            list.Add(((uint)(kv.Key & 0xFFFFFFFF), (int)(kv.Key >> 32), kv.Value));
        }

        for (int len = 1; len <= 4; len++)
        {
            foreach (BfRange r in _bfRanges[len - 1])
            {
                for (uint c = r.Lo; c <= r.Hi && list.Count < limit; c++)
                {
                    if (TryGetUnicodeHere(c, len, out string t))
                    {
                        list.Add((c, len, t));
                    }

                    if (c == uint.MaxValue)
                    {
                        break;
                    }
                }
            }
        }

        return list;
    }

    /// <summary>Decodes the binary form stored in the predefined-CMap resource (see tools/fontdata/generate.py).</summary>
    internal static CMap DecodeStored(ReadOnlySpan<byte> d, out string? useName)
    {
        int p = 0;
        if (d.Length < 6 || d[0] != (byte)'C' || d[1] != (byte)'M' || d[2] != 1)
        {
            throw new FontProgramException("Corrupt predefined CMap data.");
        }

        int wmode = d[3];
        int nameLen = d[4];
        p = 5;
        useName = nameLen == 0 ? null : Encoding.ASCII.GetString(Be.Slice(d, p, nameLen));
        p += nameLen;
        int nSpaces = checked((int)ReadVarint(d, ref p));
        if (nSpaces > 4096)
        {
            throw new FontProgramException("Corrupt predefined CMap data.");
        }

        var spaces = new CodespaceRange[nSpaces];
        for (int i = 0; i < nSpaces; i++)
        {
            int len = Be.U8(d, p++);
            uint lo = (uint)ReadVarint(d, ref p);
            uint hi = (uint)ReadVarint(d, ref p);
            spaces[i] = new CodespaceRange(len, lo, hi);
        }

        long nRanges = ReadVarint(d, ref p);
        if (nRanges > d.Length)
        {
            throw new FontProgramException("Corrupt predefined CMap data.");
        }

        var lists = new List<CidRange>[4];
        for (int i = 0; i < 4; i++)
        {
            lists[i] = [];
        }

        int prevLen = -1;
        long prevLo = 0, prevCidEnd = 0;
        for (long i = 0; i < nRanges; i++)
        {
            int len = Be.U8(d, p++);
            if (len is < 1 or > 4)
            {
                throw new FontProgramException("Corrupt predefined CMap data.");
            }

            if (len != prevLen)
            {
                prevLen = len;
                prevLo = 0;
            }

            long lo = prevLo + ReadVarint(d, ref p);
            long hi = lo + ReadVarint(d, ref p);
            long z = ReadVarint(d, ref p);
            long cid = prevCidEnd + ((z & 1) == 0 ? z >> 1 : -((z + 1) >> 1));
            lists[len - 1].Add(new CidRange((uint)lo, (uint)hi, (int)cid, (int)i));
            prevLo = lo;
            prevCidEnd = cid + (hi - lo) + 1;
        }

        var cidRanges = new CidRange[4][];
        for (int i = 0; i < 4; i++)
        {
            cidRanges[i] = [.. lists[i]];
        }

        return new CMap(null, wmode, spaces, cidRanges, [], EmptyBf());
    }

    internal static BfRange[][] EmptyBf() => [[], [], [], []];

    private static long ReadVarint(ReadOnlySpan<byte> d, ref int p)
    {
        long v = 0;
        for (int shift = 0; shift < 63; shift += 7)
        {
            byte b = Be.U8(d, p++);
            v |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }
        }

        throw new FontProgramException("Corrupt predefined CMap data.");
    }

    /// <summary>The predefined Identity-H style map: two-byte codes whose CID equals the code.</summary>
    public static CMap CreateIdentity(int wmode)
    {
        var space = new[] { new CodespaceRange(2, 0x0000, 0xFFFF) };
        var cid = new CidRange[4][] { [], [new CidRange(0, 0xFFFF, 0, 0)], [], [] };
        return new CMap(wmode == 0 ? "Identity-H" : "Identity-V", wmode, space, cid, [], EmptyBf()) { Registry = "Adobe", Ordering = "Identity" };
    }

    internal static uint ReadBigEndian(ReadOnlySpan<byte> bytes)
    {
        uint v = 0;
        foreach (byte b in bytes)
        {
            v = (v << 8) | b;
        }

        return v;
    }

    internal static string Utf16BeToString(ReadOnlySpan<byte> bytes)
    {
        int n = bytes.Length & ~1;
        var chars = new char[n / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2 * i, 2));
        }

        // A one-byte destination (some producers) is a Latin-1 character.
        return chars.Length == 0 && bytes.Length == 1 ? ((char)bytes[0]).ToString() : new string(chars);
    }
}
