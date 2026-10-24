using System.Buffers.Binary;
using OnePdf.FontPrograms;

namespace OnePdf.FontWriting;

/// <summary>
/// Writes a TrueType subset (OpenType spec, "glyf", "loca", "hmtx", "head"): the requested glyphs and the components of any composite
/// among them are renumbered compactly, so glyph <c>i + 1</c> of the output is requested glyph <c>i</c>, glyph 0 is the original
/// <c>.notdef</c>, and components follow. Kept tables are head, hhea, maxp, hmtx, loca, glyf and the hinting tables cvt, fpgm and
/// prep (PDF 32000-2 9.9, "Embedded font programs", lists exactly these for a TrueType subset); the rest is dropped. A caller that
/// embeds the subset in a simple font with a non-symbolic encoding also asks for a (3,1) cmap (9.6.5.4), which maps the Unicode values
/// it names to the renumbered glyphs.
/// </summary>
/// <remarks>
/// The input is untrusted. Every offset is checked against its table, a <c>loca</c> that is not monotonic or runs past <c>glyf</c> is an
/// error (not repaired: a subset must not carry a guess), composite nesting is bounded by <see cref="FontLimits.MaxCompositeDepth"/>
/// and a composite that reaches itself is an error. All failures are <see cref="FontProgramException"/>.
/// </remarks>
internal static class TrueTypeSubsetter
{
    private const int HeadLength = 54;
    private const int HheaLength = 36;

    /// <summary>Writes the subset.</summary>
    /// <param name="font">The source font.</param>
    /// <param name="glyphs">The glyphs to keep, as glyph ids of <paramref name="font"/>.</param>
    /// <param name="unicodeMap">
    /// Optional (Unicode value, source glyph id) pairs for a (3,1) cmap, format 4. Values above U+FFFF are skipped; every glyph named
    /// must be in <paramref name="glyphs"/>. With null the output has no cmap, which is right for a CID-keyed (Type 0) use.
    /// </param>
    public static byte[] Subset(SfntFont font, IReadOnlyList<int> glyphs, IReadOnlyList<(int Unicode, int Glyph)>? unicodeMap = null)
    {
        ReadOnlyMemory<byte> head = font.GetTable("head");
        ReadOnlyMemory<byte> hhea = font.GetTable("hhea");
        ReadOnlyMemory<byte> maxp = font.GetTable("maxp");
        ReadOnlyMemory<byte> hmtx = font.GetTable("hmtx");
        ReadOnlyMemory<byte> loca = font.GetTable("loca");
        ReadOnlyMemory<byte> glyf = font.GetTable("glyf");
        if (head.Length < HeadLength || hhea.Length < HheaLength || maxp.Length < 6 || loca.IsEmpty || glyf.IsEmpty)
        {
            throw new FontProgramException("The font lacks a table needed to subset it (head, hhea, maxp, loca, glyf).");
        }

        int numGlyphs = Be.U16(maxp.Span, 4);
        int locaFormat = Be.I16(head.Span, 50);
        if (locaFormat is not (0 or 1))
        {
            throw new FontProgramException("The font's indexToLocFormat is invalid.");
        }

        long locaNeeded = (numGlyphs + 1L) * (locaFormat == 0 ? 2 : 4);
        if (loca.Length < locaNeeded)
        {
            throw new FontProgramException("The 'loca' table is shorter than the glyph count requires.");
        }

        var offsets = new uint[numGlyphs + 1];
        uint previous = 0;
        for (int i = 0; i <= numGlyphs; i++)
        {
            uint o = locaFormat == 0 ? (uint)Be.U16(loca.Span, 2 * i) * 2 : Be.U32(loca.Span, 4 * i);
            if (o < previous || o > glyf.Length)
            {
                throw new FontProgramException("The 'loca' table is not ascending or points outside 'glyf'.");
            }

            offsets[i] = o;
            previous = o;
        }

        // New numbering: .notdef, the requested glyphs, then their components in discovery order.
        var order = new List<int>(glyphs.Count + 1) { 0 };
        var newOf = new Dictionary<int, int> { [0] = 0 };
        foreach (int g in glyphs)
        {
            if ((uint)g >= (uint)numGlyphs)
            {
                throw new FontProgramException("A requested glyph is outside the font.");
            }

            if (newOf.TryAdd(g, order.Count))
            {
                order.Add(g);
            }
        }

        ReadOnlySpan<byte> glyfSpan = glyf.Span;
        for (int i = 0; i < order.Count; i++)
        {
            int g = order[i];
            ReadOnlySpan<byte> data = GlyphBytes(glyfSpan, offsets, g);
            if (!IsComposite(data))
            {
                continue;
            }

            foreach (int component in Components(data, numGlyphs))
            {
                if (newOf.TryAdd(component, order.Count))
                {
                    order.Add(component);
                    if (order.Count > FontLimits.MaxGlyphs)
                    {
                        throw new FontProgramException("The subset would hold too many glyphs.");
                    }
                }
            }
        }

        CheckCompositeDepth(glyfSpan, offsets, numGlyphs, order);

        // glyf and loca.
        var glyfOut = new MemoryStream();
        var newOffsets = new uint[order.Count + 1];
        for (int n = 0; n < order.Count; n++)
        {
            newOffsets[n] = checked((uint)glyfOut.Length);
            ReadOnlySpan<byte> data = GlyphBytes(glyfSpan, offsets, order[n]);
            if (data.IsEmpty)
            {
                continue;
            }

            byte[] copy = data.ToArray();
            if (IsComposite(copy))
            {
                RemapComponents(copy, newOf);
            }

            glyfOut.Write(copy);
            while (glyfOut.Length % 4 != 0)
            {
                glyfOut.WriteByte(0);
            }
        }

        newOffsets[order.Count] = checked((uint)glyfOut.Length);
        bool shortLoca = glyfOut.Length / 2 <= 0xFFFF;
        byte[] locaOut = new byte[(order.Count + 1) * (shortLoca ? 2 : 4)];
        for (int i = 0; i <= order.Count; i++)
        {
            if (shortLoca)
            {
                BinaryPrimitives.WriteUInt16BigEndian(locaOut.AsSpan(2 * i), (ushort)(newOffsets[i] / 2));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(locaOut.AsSpan(4 * i), newOffsets[i]);
            }
        }

        // hmtx: one long metric per glyph.
        int numHMetrics = Math.Min(Be.U16(hhea.Span, 34), hmtx.Length / 4);
        byte[] hmtxOut = new byte[order.Count * 4];
        ReadOnlySpan<byte> hm = hmtx.Span;
        for (int n = 0; n < order.Count; n++)
        {
            int g = order[n];
            ushort advance = 0;
            short lsb = 0;
            if (numHMetrics > 0)
            {
                int i = Math.Min(g, numHMetrics - 1);
                advance = Be.U16(hm, 4 * i);
                if (g < numHMetrics)
                {
                    lsb = Be.I16(hm, (4 * i) + 2);
                }
                else
                {
                    int lo = (4 * numHMetrics) + (2 * (g - numHMetrics));
                    lsb = lo + 2 <= hm.Length ? Be.I16(hm, lo) : (short)0;
                }
            }

            BinaryPrimitives.WriteUInt16BigEndian(hmtxOut.AsSpan(4 * n), advance);
            BinaryPrimitives.WriteInt16BigEndian(hmtxOut.AsSpan((4 * n) + 2), lsb);
        }

        byte[] headOut = head.Span[..HeadLength].ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(headOut.AsSpan(8), 0); // checkSumAdjustment, set once the file is assembled
        BinaryPrimitives.WriteInt16BigEndian(headOut.AsSpan(50), shortLoca ? (short)0 : (short)1);
        byte[] hheaOut = hhea.Span[..HheaLength].ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(hheaOut.AsSpan(34), (ushort)order.Count);
        byte[] maxpOut = maxp.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(maxpOut.AsSpan(4), (ushort)order.Count);

        var tables = new SfntTables
        {
            [Be.Tag("head")] = headOut,
            [Be.Tag("hhea")] = hheaOut,
            [Be.Tag("maxp")] = maxpOut,
            [Be.Tag("hmtx")] = hmtxOut,
            [Be.Tag("loca")] = locaOut,
            [Be.Tag("glyf")] = glyfOut.ToArray(),
        };
        if (unicodeMap is not null)
        {
            tables[Be.Tag("cmap")] = BuildCmap31(unicodeMap, newOf);
        }

        foreach (string optional in (string[])["cvt ", "fpgm", "prep"])
        {
            ReadOnlyMemory<byte> t = font.GetTable(optional);
            if (!t.IsEmpty)
            {
                tables[Be.Tag(optional)] = t.ToArray();
            }
        }

        return SfntWriter.Assemble(0x00010000, tables, Be.Tag("head"));
    }

    // OpenType "cmap": one encoding record, platform 3 (Windows) encoding 1 (Unicode BMP), format 4 (segment mapping to delta values).
    // Consecutive code points that map to consecutive glyphs share a segment, so idRangeOffset is always 0.
    private static byte[] BuildCmap31(IReadOnlyList<(int Unicode, int Glyph)> map, Dictionary<int, int> newOf)
    {
        // (code point, subset glyph, position in the input): sorted by code point, and for a repeated code point the later entry wins.
        var sorted = new List<(int Unicode, int Glyph, int Order)>(map.Count);
        for (int m = 0; m < map.Count; m++)
        {
            (int unicode, int glyph) = map[m];
            if (unicode is < 0 or > 0xFFFE)
            {
                continue;
            }

            if (!newOf.TryGetValue(glyph, out int mapped))
            {
                throw new FontProgramException("A cmap entry names a glyph that is not in the subset.");
            }

            sorted.Add((unicode, mapped, m));
        }

        sorted.Sort(static (a, b) => a.Unicode != b.Unicode ? a.Unicode.CompareTo(b.Unicode) : a.Order.CompareTo(b.Order));
        var starts = new List<int>();
        var ends = new List<int>();
        var deltas = new List<int>();
        for (int k = 0; k < sorted.Count; k++)
        {
            (int unicode, int glyph, _) = sorted[k];
            if (k + 1 < sorted.Count && sorted[k + 1].Unicode == unicode)
            {
                continue; // a later entry for the same code point replaces this one
            }

            int last = ends.Count - 1;
            if (last >= 0 && ends[last] == unicode - 1 && (((deltas[last] + ends[last]) & 0xFFFF) + 1 & 0xFFFF) == glyph)
            {
                ends[last] = unicode;
                continue;
            }

            starts.Add(unicode);
            ends.Add(unicode);
            deltas.Add((glyph - unicode) & 0xFFFF);
        }

        starts.Add(0xFFFF);
        ends.Add(0xFFFF);
        deltas.Add(1);
        int segCount = starts.Count;
        int subtableLength = 16 + (8 * segCount);
        if (subtableLength > 0xFFFF)
        {
            throw new FontProgramException("The cmap would be too large.");
        }

        int entrySelector = 0;
        while ((2 << entrySelector) <= segCount)
        {
            entrySelector++;
        }

        int searchRange = 2 * (1 << entrySelector);
        var t = new byte[12 + subtableLength];
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), 1); // numTables
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(4), 3); // platformID
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(6), 1); // encodingID
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), 12);
        Span<byte> s = t.AsSpan(12);
        BinaryPrimitives.WriteUInt16BigEndian(s, 4);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)subtableLength);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)(2 * segCount));
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)((2 * segCount) - searchRange));
        for (int i = 0; i < segCount; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[(14 + (2 * i))..], (ushort)ends[i]);
            BinaryPrimitives.WriteUInt16BigEndian(s[(16 + (2 * segCount) + (2 * i))..], (ushort)starts[i]);
            BinaryPrimitives.WriteUInt16BigEndian(s[(16 + (4 * segCount) + (2 * i))..], (ushort)deltas[i]);
        }

        return t;
    }

    private static ReadOnlySpan<byte> GlyphBytes(ReadOnlySpan<byte> glyf, uint[] offsets, int gid) =>
        glyf.Slice((int)offsets[gid], (int)(offsets[gid + 1] - offsets[gid]));

    private static bool IsComposite(ReadOnlySpan<byte> glyph) => glyph.Length >= 10 && Be.I16(glyph, 0) < 0;

    // Walks the component records of a composite (OpenType 'glyf': flags, glyphIndex, arguments, optional transform).
    private static List<int> Components(ReadOnlySpan<byte> glyph, int numGlyphs)
    {
        var list = new List<int>();
        foreach ((int _, int component) in ComponentRecords(glyph))
        {
            if ((uint)component >= (uint)numGlyphs)
            {
                throw new FontProgramException("A composite glyph names a component outside the font.");
            }

            list.Add(component);
        }

        return list;
    }

    private static List<(int Offset, int Glyph)> ComponentRecords(ReadOnlySpan<byte> glyph)
    {
        var list = new List<(int, int)>();
        int p = 10;
        while (true)
        {
            if (list.Count >= FontLimits.MaxCompositeComponents)
            {
                throw new FontProgramException("A composite glyph has too many components.");
            }

            int flags = Be.U16(glyph, p);
            int component = Be.U16(glyph, p + 2);
            list.Add((p + 2, component));
            p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0)
            {
                p += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                p += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                p += 8;
            }

            if (p > glyph.Length)
            {
                throw Be.Truncated();
            }

            if ((flags & 0x0020) == 0)
            {
                return list;
            }
        }
    }

    private static void RemapComponents(byte[] glyph, Dictionary<int, int> newOf)
    {
        foreach ((int offset, int component) in ComponentRecords(glyph))
        {
            BinaryPrimitives.WriteUInt16BigEndian(glyph.AsSpan(offset), (ushort)newOf[component]);
        }
    }

    // Composite nesting is limited (the reader limits it too) and a composite reaching itself is an error: depth-first with an explicit stack.
    private static void CheckCompositeDepth(ReadOnlySpan<byte> glyf, uint[] offsets, int numGlyphs, List<int> order)
    {
        // 1 = on the path, 2 = done; a glyph number outside the font has no children and is not tracked. An array, because a
        // dictionary over these types is compiled by the JIT on first use.
        byte[] state = new byte[numGlyphs];
        var stack = new Stack<(int Glyph, int[] Children, int Next)>();
        foreach (int root in order)
        {
            if ((uint)root >= (uint)numGlyphs || state[root] != 0)
            {
                continue;
            }

            stack.Push((root, ChildrenOf(glyf, offsets, numGlyphs, root), 0));
            state[root] = 1;
            while (stack.Count > 0)
            {
                (int g, int[] children, int next) = stack.Pop();
                if (next < children.Length)
                {
                    stack.Push((g, children, next + 1));
                    int c = children[next];
                    if ((uint)c >= (uint)numGlyphs)
                    {
                        continue;
                    }

                    byte s = state[c];
                    if (s != 0)
                    {
                        if (s == 1)
                        {
                            throw new FontProgramException("A composite glyph contains itself.");
                        }

                        continue;
                    }

                    if (stack.Count >= FontLimits.MaxCompositeDepth)
                    {
                        throw new FontProgramException("Composite glyphs are nested too deeply.");
                    }

                    state[c] = 1;
                    stack.Push((c, ChildrenOf(glyf, offsets, numGlyphs, c), 0));
                }
                else
                {
                    state[g] = 2;
                }
            }
        }
    }

    private static int[] ChildrenOf(ReadOnlySpan<byte> glyf, uint[] offsets, int numGlyphs, int gid)
    {
        ReadOnlySpan<byte> data = GlyphBytes(glyf, offsets, gid);
        return IsComposite(data) ? [.. Components(data, numGlyphs)] : [];
    }
}

/// <summary>
/// The tables of an sfnt file kept in tag order, which the table directory needs. A handful of entries in two parallel arrays: sorted
/// dictionaries over these types are compiled by the JIT on first use, which a first document pays for.
/// </summary>
internal sealed class SfntTables
{
    private uint[] _tags = new uint[16];
    private byte[][] _data = new byte[16][];

    public int Count { get; private set; }

    public uint TagAt(int index) => _tags[index];

    public byte[] DataAt(int index) => _data[index];

    /// <summary>Sets the table with this tag, replacing an earlier one.</summary>
    public byte[] this[uint tag]
    {
        set
        {
            int i = 0;
            while (i < Count && _tags[i] < tag)
            {
                i++;
            }

            if (i < Count && _tags[i] == tag)
            {
                _data[i] = value;
                return;
            }

            if (Count == _tags.Length)
            {
                Array.Resize(ref _tags, Count * 2);
                Array.Resize(ref _data, Count * 2);
            }

            Array.Copy(_tags, i, _tags, i + 1, Count - i);
            Array.Copy(_data, i, _data, i + 1, Count - i);
            _tags[i] = tag;
            _data[i] = value;
            Count++;
        }
    }
}

/// <summary>Assembles an sfnt file from its tables (OpenType spec, "Organization of an OpenType Font").</summary>
internal static class SfntWriter
{
    public static byte[] Assemble(uint version, SfntTables tables, uint headTag)
    {
        int n = tables.Count;
        int entrySelector = 0;
        while ((1 << (entrySelector + 1)) <= n)
        {
            entrySelector++;
        }

        int searchRange = (1 << entrySelector) * 16;
        long total = 12 + (16L * n);
        for (int i = 0; i < n; i++)
        {
            total += (tables.DataAt(i).Length + 3L) & ~3L;
        }

        byte[] file = new byte[checked((int)total)];
        BinaryPrimitives.WriteUInt32BigEndian(file, version);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)((n * 16) - searchRange));
        int dir = 12;
        int pos = 12 + (16 * n);
        int headPos = -1;
        for (int i = 0; i < n; i++)
        {
            uint tag = tables.TagAt(i);
            byte[] t = tables.DataAt(i);
            t.CopyTo(file.AsSpan(pos));
            if (tag == headTag)
            {
                headPos = pos;
            }

            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(dir), tag);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(dir + 4), Checksum(file.AsSpan(pos, (t.Length + 3) & ~3)));
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(dir + 8), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(dir + 12), (uint)t.Length);
            dir += 16;
            pos += (t.Length + 3) & ~3;
        }

        if (headPos >= 0)
        {
            // head.checkSumAdjustment = 0xB1B0AFBA - checksum of the whole file (head's own copy was zero while summing).
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(headPos + 8), unchecked(0xB1B0AFBA - Checksum(file)));
        }

        return file;
    }

    public static uint Checksum(ReadOnlySpan<byte> d)
    {
        uint sum = 0;
        for (int i = 0; i + 4 <= d.Length; i += 4)
        {
            sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(d.Slice(i, 4)));
        }

        return sum;
    }
}
