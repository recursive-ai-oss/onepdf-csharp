using OnePdf.FontPrograms;

namespace OnePdf.FontWriting;

/// <summary>
/// Writes a CID-keyed CFF subset (Adobe TN #5176, "The Compact Font Format Specification"; TN #5177 for Type 2 charstrings): the
/// requested glyphs are renumbered so output glyph <c>i + 1</c> is requested glyph <c>i</c> (glyph 0 stays <c>.notdef</c>) and the
/// charset maps every output glyph to the CID equal to its glyph index, which is what an Identity-H font addresses.
/// </summary>
/// <remarks>
/// Charstrings are copied verbatim. Subroutines are kept in place (indexes and biases do not change) and those the kept glyphs never
/// reach are emptied to a lone <c>return</c>, which costs almost nothing once compressed. If a charstring cannot be followed (it
/// computes a subroutine number, loops, or is too long) every subroutine is kept. Name-keyed input becomes a one-FD CID font. The
/// deprecated accented-character form of <c>endchar</c> is refused, as are inputs whose tables point outside the font; all failures
/// are <see cref="FontProgramException"/>.
/// </remarks>
internal static class CffSubsetter
{
    private const int MaxDictEntries = 512;
    private const int MaxFontDicts = FontLimits.MaxFontDicts;
    private const int ScanBudgetPerGlyph = 200_000;
    private const long ScanBudgetTotal = 40_000_000;

    private readonly record struct DictEntry(int Op, int RawStart, int RawLength, double First, double Second);

    public static byte[] Subset(ReadOnlyMemory<byte> cff, IReadOnlyList<int> glyphs, string fontName)
    {
        ReadOnlySpan<byte> d = cff.Span;
        int hdrSize = Be.U8(d, 2);
        int pos = hdrSize;
        _ = CffIndex.Read(d, pos, out pos); // Name INDEX
        CffIndex topIndex = CffIndex.Read(d, pos, out pos);
        CffIndex strings = CffIndex.Read(d, pos, out pos);
        CffIndex gsubrs = CffIndex.Read(d, pos, out _);
        if (topIndex.Count < 1)
        {
            throw new FontProgramException("The CFF font has no Top DICT.");
        }

        ReadOnlySpan<byte> topSpan = topIndex.Get(d, 0);
        List<DictEntry> top = ParseDict(topSpan);
        int charStringsOffset = (int)Operand(top, 17, "CharStrings");
        CffIndex charStrings = CffIndex.Read(d, charStringsOffset, out _);
        int numGlyphs = charStrings.Count;
        if (numGlyphs < 1 || numGlyphs > FontLimits.MaxGlyphs)
        {
            throw new FontProgramException("The CFF font's glyph count is out of range.");
        }

        bool isCid = Find(top, 0x0C1E) >= 0;

        // FontBBox and FontMatrix are copied as they are.
        var topExtra = new List<byte>();
        foreach (int rawOp in (int[])[5, 0x0C07])
        {
            int i = Find(top, rawOp);
            if (i >= 0)
            {
                topExtra.AddRange(topSpan.Slice(top[i].RawStart, top[i].RawLength).ToArray());
            }
        }

        // Font DICT sources: their Private DICT spans (absolute offsets in the source) and the per-glyph FD choice.
        var fdPrivate = new List<(int Size, int Offset)>();
        var fdMatrix = new List<byte[]?>();
        int[] fdOfGlyph = new int[numGlyphs];
        if (isCid)
        {
            CffIndex fdArray = CffIndex.Read(d, (int)Operand(top, 0x0C24, "FDArray"), out _);
            if (fdArray.Count < 1 || fdArray.Count > MaxFontDicts)
            {
                throw new FontProgramException("The CFF FDArray size is out of range.");
            }

            for (int i = 0; i < fdArray.Count; i++)
            {
                ReadOnlySpan<byte> fd = fdArray.Get(d, i);
                List<DictEntry> entries = ParseDict(fd);
                int p = Find(entries, 18);
                fdPrivate.Add(p >= 0 ? ((int)entries[p].First, (int)entries[p].Second) : (0, 0));
                int m = Find(entries, 0x0C07);
                fdMatrix.Add(m >= 0 ? fd.Slice(entries[m].RawStart, entries[m].RawLength).ToArray() : null);
            }

            ReadFdSelect(d, (int)Operand(top, 0x0C25, "FDSelect"), fdOfGlyph, fdArray.Count);
        }
        else
        {
            int p = Find(top, 18);
            fdPrivate.Add(p >= 0 ? ((int)top[p].First, (int)top[p].Second) : (0, 0));
            fdMatrix.Add(null);
        }

        // Kept glyph list: .notdef then the requested ones.
        var order = new List<int>(glyphs.Count + 1) { 0 };
        var seen = new HashSet<int> { 0 };
        foreach (int g in glyphs)
        {
            if ((uint)g >= (uint)numGlyphs)
            {
                throw new FontProgramException("A requested glyph is outside the font.");
            }

            if (seen.Add(g))
            {
                order.Add(g);
            }
        }

        // Private DICTs and local subroutines of every FD.
        var privateRaw = new List<byte[]>();
        var localSubrs = new List<CffIndex?>();
        for (int i = 0; i < fdPrivate.Count; i++)
        {
            (int size, int offset) = fdPrivate[i];
            if (size == 0)
            {
                privateRaw.Add([]);
                localSubrs.Add(null);
                continue;
            }

            ReadOnlySpan<byte> priv = Be.Slice(d, offset, size);
            List<DictEntry> entries = ParseDict(priv);
            var kept = new List<byte>(size);
            CffIndex? subrs = null;
            foreach (DictEntry e in entries)
            {
                if (e.Op == 19)
                {
                    subrs = CffIndex.Read(d, checked(offset + (int)e.First), out _);
                }
                else
                {
                    kept.AddRange(priv.Slice(e.RawStart, e.RawLength).ToArray());
                }
            }

            privateRaw.Add([.. kept]);
            localSubrs.Add(subrs);
        }

        // Which subroutines the kept glyphs reach.
        var usedLocal = new bool[fdPrivate.Count][];
        for (int i = 0; i < usedLocal.Length; i++)
        {
            usedLocal[i] = new bool[localSubrs[i]?.Count ?? 0];
        }

        var usedGlobal = new bool[gsubrs.Count];
        bool keepAll = false;
        long total = ScanBudgetTotal;
        foreach (int g in order)
        {
            ReadOnlySpan<byte> cs = charStrings.Get(d, g);
            int fd = fdOfGlyph[g];
            if (!keepAll && !ScanGlyph(d, cs, gsubrs, localSubrs[fd], usedGlobal, usedLocal[fd], ref total))
            {
                keepAll = true;
            }
        }

        // ---- output pieces ----
        byte[] nameIndex = IndexBytes([System.Text.Encoding.ASCII.GetBytes(fontName)]);
        int stringCount = strings.Count;
        var stringList = new List<byte[]>(stringCount + 2);
        for (int i = 0; i < stringCount; i++)
        {
            stringList.Add(strings.Get(d, i).ToArray());
        }

        stringList.Add("Adobe"u8.ToArray());
        stringList.Add("Identity"u8.ToArray());
        byte[] stringIndex = IndexBytes(stringList);
        byte[] gsubrIndex = SubrIndex(d, gsubrs, usedGlobal, keepAll);

        var csList = new List<byte[]>(order.Count);
        foreach (int g in order)
        {
            csList.Add(charStrings.Get(d, g).ToArray());
        }

        byte[] charStringsIndex = IndexBytes(csList);
        int n = order.Count;
        byte[] charset = new byte[n >= 2 ? 5 : 1];
        charset[0] = 2;
        if (n >= 2)
        {
            charset[1] = 0;
            charset[2] = 1;
            charset[3] = (byte)((n - 2) >> 8);
            charset[4] = (byte)(n - 2);
        }

        byte[] fdSelect = new byte[1 + n];
        for (int i = 0; i < n; i++)
        {
            fdSelect[1 + i] = (byte)fdOfGlyph[order[i]];
        }

        // Top DICT (offsets are fixed-width, so its size is known before the layout).
        byte[] topBytes(int csOff, int charsetOff, int fdSelOff, int fdArrOff)
        {
            var b = new List<byte>(64);
            // ROS comes first (TN #5176 section 9). Its operands use the shortest integer forms: a reader that looks for the
            // leading ROS operator (veraPDF does) rejects the 3- and 5-byte forms there.
            PutCompact(b, stringCount + 391);
            PutCompact(b, stringCount + 392);
            PutCompact(b, 0);
            PutOp(b, 0x0C1E);
            b.AddRange(topExtra);

            PutInt(b, n);
            PutOp(b, 0x0C22);
            PutInt(b, charsetOff);
            PutOp(b, 15);
            PutInt(b, fdSelOff);
            PutOp(b, 0x0C25);
            PutInt(b, csOff);
            PutOp(b, 17);
            PutInt(b, fdArrOff);
            PutOp(b, 0x0C24);
            return [.. b];
        }

        byte[] topIndexBytes = IndexBytes([topBytes(0, 0, 0, 0)]);

        // Font DICTs: [FontMatrix] Private(size, offset); the Private DICTs follow the FDArray.
        int fdCount = fdPrivate.Count;
        byte[] FdDict(int i, int privateSize, int privateOffset)
        {
            var b = new List<byte>(16);
            if (fdMatrix[i] is { } m)
            {
                b.AddRange(m);
            }

            PutInt(b, privateSize);
            PutInt(b, privateOffset);
            PutOp(b, 18);
            return [.. b];
        }

        int privateLength(int i) => privateRaw[i].Length + (localSubrs[i] is null ? 0 : 6);
        var fdSizeList = new List<byte[]>(fdCount);
        for (int i = 0; i < fdCount; i++)
        {
            fdSizeList.Add(FdDict(i, 0, 0));
        }

        int fdArrayLength = IndexBytes(fdSizeList).Length;

        int header = 4;
        int csOffset = header + nameIndex.Length + topIndexBytes.Length + stringIndex.Length + gsubrIndex.Length;
        int charsetOffset = csOffset + charStringsIndex.Length;
        int fdSelectOffset = charsetOffset + charset.Length;
        int fdArrayOffset = fdSelectOffset + fdSelect.Length;
        int privatesOffset = fdArrayOffset + fdArrayLength;

        var privateOffsets = new int[fdCount];
        var subrBytes = new byte[fdCount][];
        long cursor = privatesOffset;
        for (int i = 0; i < fdCount; i++)
        {
            privateOffsets[i] = checked((int)cursor);
            subrBytes[i] = localSubrs[i] is { } ls ? SubrIndex(d, ls, usedLocal[i], keepAll) : [];
            cursor += privateLength(i) + subrBytes[i].Length;
        }

        if (cursor > int.MaxValue / 2)
        {
            throw new FontProgramException("The CFF subset would be too large.");
        }

        var output = new List<byte>((int)cursor + 16) { 1, 0, 4 };
        output.Add(4); // offSize of the header
        output.AddRange(nameIndex);
        output.AddRange(IndexBytes([topBytes(csOffset, charsetOffset, fdSelectOffset, fdArrayOffset)]));
        output.AddRange(stringIndex);
        output.AddRange(gsubrIndex);
        output.AddRange(charStringsIndex);
        output.AddRange(charset);
        output.AddRange(fdSelect);
        var fds = new List<byte[]>(fdCount);
        for (int i = 0; i < fdCount; i++)
        {
            fds.Add(FdDict(i, privateLength(i), privateRaw[i].Length == 0 && localSubrs[i] is null ? 0 : privateOffsets[i]));
        }

        output.AddRange(IndexBytes(fds));
        for (int i = 0; i < fdCount; i++)
        {
            output.AddRange(privateRaw[i]);
            if (localSubrs[i] is not null)
            {
                PutInt(output, privateRaw[i].Length + 6);
                PutOp(output, 19);
                output.AddRange(subrBytes[i]);
            }
        }

        return [.. output];
    }

    private static long Operand(List<DictEntry> dict, int op, string what)
    {
        int i = Find(dict, op);
        if (i < 0)
        {
            throw new FontProgramException($"The CFF Top DICT has no {what}.");
        }

        return (long)dict[i].First;
    }

    private static int Find(List<DictEntry> dict, int op)
    {
        for (int i = 0; i < dict.Count; i++)
        {
            if (dict[i].Op == op)
            {
                return i;
            }
        }

        return -1;
    }

    // DICT data (TN #5176, section 4): operands then an operator. Only the first two operands are kept as numbers (real numbers
    // are skipped as 0): everything the subsetter edits is an integer, and the rest is copied as raw bytes.
    private static List<DictEntry> ParseDict(ReadOnlySpan<byte> d)
    {
        var list = new List<DictEntry>();
        int p = 0;
        int entryStart = 0;
        double first = 0;
        double second = 0;
        int operands = 0;
        while (p < d.Length)
        {
            int b0 = d[p];
            if (b0 <= 21)
            {
                int op = b0;
                p++;
                if (b0 == 12)
                {
                    op = 0x0C00 | Be.U8(d, p);
                    p++;
                }

                if (list.Count >= MaxDictEntries)
                {
                    throw new FontProgramException("A CFF DICT has too many entries.");
                }

                list.Add(new DictEntry(op, entryStart, p - entryStart, first, second));
                entryStart = p;
                first = second = 0;
                operands = 0;
                continue;
            }

            double value = 0;
            if (b0 == 28)
            {
                value = Be.I16(d, p + 1);
                p += 3;
            }
            else if (b0 == 29)
            {
                value = Be.I32(d, p + 1);
                p += 5;
            }
            else if (b0 == 30)
            {
                p++;
                bool done = false;
                while (!done)
                {
                    int b = Be.U8(d, p++);
                    done = (b & 0x0F) == 0x0F || (b >> 4) == 0x0F;
                }
            }
            else if (b0 >= 32 && b0 <= 246)
            {
                value = b0 - 139;
                p++;
            }
            else if (b0 >= 247 && b0 <= 250)
            {
                value = ((b0 - 247) * 256) + Be.U8(d, p + 1) + 108;
                p += 2;
            }
            else if (b0 >= 251 && b0 <= 254)
            {
                value = (-(b0 - 251) * 256) - Be.U8(d, p + 1) - 108;
                p += 2;
            }
            else
            {
                throw new FontProgramException("A CFF DICT holds an invalid byte.");
            }

            if (operands == 0)
            {
                first = value;
            }
            else if (operands == 1)
            {
                second = value;
            }

            if (++operands > 64)
            {
                throw new FontProgramException("A CFF DICT entry has too many operands.");
            }
        }

        return list;
    }

    private static void ReadFdSelect(ReadOnlySpan<byte> d, int offset, int[] fdOfGlyph, int fdCount)
    {
        int format = Be.U8(d, offset);
        if (format == 0)
        {
            for (int g = 0; g < fdOfGlyph.Length; g++)
            {
                fdOfGlyph[g] = CheckFd(Be.U8(d, offset + 1 + g), fdCount);
            }
        }
        else if (format == 3)
        {
            int ranges = Be.U16(d, offset + 1);
            int p = offset + 3;
            int first = Be.U16(d, p);
            for (int r = 0; r < ranges; r++)
            {
                int fd = CheckFd(Be.U8(d, p + 2), fdCount);
                int next = Be.U16(d, p + 3);
                if (next < first || next > fdOfGlyph.Length)
                {
                    throw new FontProgramException("The CFF FDSelect ranges are invalid.");
                }

                for (int g = first; g < next; g++)
                {
                    fdOfGlyph[g] = fd;
                }

                first = next;
                p += 3;
            }
        }
        else
        {
            throw new FontProgramException("The CFF FDSelect format is not supported.");
        }
    }

    private static int CheckFd(int fd, int fdCount) =>
        fd < fdCount ? fd : throw new FontProgramException("A CFF glyph names a Font DICT that does not exist.");

    // ---- charstring scan (TN #5177) ----

    private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    // Follows the glyph's charstring through call(g)subr, marking the subroutines it reaches. Returns false when it cannot be followed.
    private static bool ScanGlyph(ReadOnlySpan<byte> file, ReadOnlySpan<byte> glyph, CffIndex gsubrs, CffIndex? lsubrs, bool[] usedGlobal, bool[] usedLocal, ref long total)
    {
        var stack = new double[96];
        int sp = 0;
        int stems = 0;
        int budget = ScanBudgetPerGlyph;
        int gBias = Bias(gsubrs.Count);
        int lBias = Bias(lsubrs?.Count ?? 0);

        ReadOnlySpan<byte> current = glyph;
        int p = 0;
        var callers = new Stack<(bool Global, int Index, int Pos)>();
        bool curGlobal = false;
        int curIndex = -1;
        while (true)
        {
            if (--budget < 0 || --total < 0)
            {
                return false;
            }

            if (p >= current.Length)
            {
                // Fell off the end of a subroutine or glyph without return/endchar: treat as return.
                if (callers.Count == 0)
                {
                    return true;
                }

                (curGlobal, curIndex, p) = callers.Pop();
                current = curIndex < 0 ? glyph : (curGlobal ? gsubrs : lsubrs!.Value).Get(file, curIndex);
                continue;
            }

            int b0 = current[p++];
            if (b0 >= 32 || b0 == 28)
            {
                double v;
                if (b0 == 28)
                {
                    v = Be.I16(current, p);
                    p += 2;
                }
                else if (b0 <= 246)
                {
                    v = b0 - 139;
                }
                else if (b0 <= 250)
                {
                    v = ((b0 - 247) * 256) + Be.U8(current, p++) + 108;
                }
                else if (b0 <= 254)
                {
                    v = (-(b0 - 251) * 256) - Be.U8(current, p++) - 108;
                }
                else
                {
                    v = Be.I32(current, p) / 65536.0;
                    p += 4;
                }

                if (sp >= stack.Length)
                {
                    return false;
                }

                stack[sp++] = v;
                continue;
            }

            switch (b0)
            {
                case 1:
                case 3:
                case 18:
                case 23:
                    stems += sp / 2;
                    sp = 0;
                    break;

                case 19:
                case 20:
                    stems += sp / 2;
                    sp = 0;
                    p += (stems + 7) / 8;
                    break;

                case 10:
                case 29:
                {
                    if (sp < 1)
                    {
                        return false;
                    }

                    bool global = b0 == 29;
                    CffIndex index = global ? gsubrs : lsubrs ?? default;
                    int number = (int)stack[--sp] + (global ? gBias : lBias);
                    if ((uint)number >= (uint)index.Count || callers.Count >= FontLimits.MaxSubrDepth)
                    {
                        return false;
                    }

                    (global ? usedGlobal : usedLocal)[number] = true;
                    callers.Push((curGlobal, curIndex, p));
                    curGlobal = global;
                    curIndex = number;
                    current = index.Get(file, number);
                    p = 0;
                    break;
                }

                case 11:
                    if (callers.Count == 0)
                    {
                        return true;
                    }

                    (curGlobal, curIndex, p) = callers.Pop();
                    current = curIndex < 0 ? glyph : (curGlobal ? gsubrs : lsubrs!.Value).Get(file, curIndex);
                    break;

                case 14:
                    if (sp >= 4)
                    {
                        throw new FontProgramException("A glyph uses the deprecated accented-character form of endchar.");
                    }

                    return true;

                case 12:
                {
                    int b1 = Be.U8(current, p++);

                    // Arithmetic, storage and stack operators can compute a subroutine number: give up following.
                    if (b1 is 3 or 4 or 5 or 9 or 10 or 11 or 12 or 14 or 15 or 18 or 20 or 21 or 22 or 23 or 24 or 26 or 27 or 28 or 29 or 30)
                    {
                        return false;
                    }

                    sp = 0;
                    break;
                }

                default:
                    sp = 0;
                    break;
            }
        }
    }

    // ---- INDEX output ----

    private static byte[] SubrIndex(ReadOnlySpan<byte> file, CffIndex index, bool[] used, bool keepAll)
    {
        var list = new List<byte[]>(index.Count);
        for (int i = 0; i < index.Count; i++)
        {
            list.Add(keepAll || used[i] ? index.Get(file, i).ToArray() : [11]);
        }

        return IndexBytes(list);
    }

    private static byte[] IndexBytes(List<byte[]> items)
    {
        if (items.Count == 0)
        {
            return [0, 0];
        }

        long total = 1;
        foreach (byte[] item in items)
        {
            total += item.Length;
        }

        int offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;
        var b = new List<byte>(checked((int)total) + ((items.Count + 1) * offSize) + 3);
        b.Add((byte)(items.Count >> 8));
        b.Add((byte)items.Count);
        b.Add((byte)offSize);
        long off = 1;
        PutOffset(b, off, offSize);
        foreach (byte[] item in items)
        {
            off += item.Length;
            PutOffset(b, off, offSize);
        }

        foreach (byte[] item in items)
        {
            b.AddRange(item);
        }

        return [.. b];
    }

    private static void PutOffset(List<byte> b, long v, int size)
    {
        for (int i = size - 1; i >= 0; i--)
        {
            b.Add((byte)(v >> (8 * i)));
        }
    }

    private static void PutInt(List<byte> b, int v)
    {
        b.Add(29);
        PutOffset(b, (uint)v, 4);
    }

    /// <summary>A DICT integer in its shortest form (TN #5176 table 3), as far as the 1- and 2-byte forms reach, else the 3-byte form.</summary>
    private static void PutCompact(List<byte> b, int v)
    {
        if (v >= -107 && v <= 107)
        {
            b.Add((byte)(v + 139));
        }
        else if (v >= 108 && v <= 1131)
        {
            b.Add((byte)(247 + ((v - 108) >> 8)));
            b.Add((byte)(v - 108));
        }
        else if (v >= -32768 && v <= 32767)
        {
            b.Add(28);
            b.Add((byte)(v >> 8));
            b.Add((byte)v);
        }
        else
        {
            PutInt(b, v);
        }
    }

    private static void PutOp(List<byte> b, int op)
    {
        if (op > 0xFF)
        {
            b.Add(12);
        }

        b.Add((byte)op);
    }
}
