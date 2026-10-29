using OnePdf.FontPrograms;

namespace OnePdf.FontWriting;

/// <summary>
/// Gives chosen glyphs of a CFF program (Adobe TN #5176) other advance widths, and changes nothing else. A Type 2 charstring carries its
/// advance as an optional first operand, relative to the Private DICT's <c>nominalWidthX</c> (TN #5177 section 3.1); this rewrites that
/// operand, so the width a reader takes from the program agrees with the <c>/Widths</c> of the PDF without a single outline point moving.
/// </summary>
/// <remarks>
/// <para>
/// Both width defaults of every Private DICT are made whole numbers (<c>defaultWidthX</c> and <c>nominalWidthX</c> are real numbers in
/// fonts that were instanced from a variable font, and readers differ in how they take them), so every glyph of the program is rewritten
/// against the new defaults and keeps its width unless it is one of the glyphs asked for.
/// </para>
/// <para>
/// Layout: the header, Name, String and Global Subr INDEXes are copied as they are; the Top DICT is written again with every offset
/// shifted by the change in its size; everything that followed the Global Subr INDEX is copied as one block, so the structures in it
/// (charset, Encoding, FDSelect) keep their internal layout; the new CharStrings INDEX, Private DICTs (with copies of their local
/// subroutines) and, for CID-keyed fonts, the FDArray are appended. The old CharStrings INDEX is zeroed when nothing else lies in it, so
/// the program does not carry two copies (zeros cost nothing once compressed).
/// </para>
/// <para>
/// A glyph whose width operand cannot be found at the start of its own charstring (it calls a subroutine before its first drawing or
/// stem operator) makes the rewrite refuse with <see cref="FontProgramException"/>, as do programs with Type 1 charstrings and tables
/// that point outside the file or before the Global Subr INDEX.
/// </para>
/// </remarks>
internal static class CffWidthRewriter
{
    private const int MaxHeadOperands = 96;
    private const int MaxProgramBytes = 1 << 28;

    /// <summary>
    /// Returns a program with the same outlines, names and encodings in which each glyph in <paramref name="widths"/> has the given advance
    /// (in font units, whole or fractional) and every other glyph keeps its advance.
    /// </summary>
    public static byte[] Rewrite(ReadOnlyMemory<byte> cff, IReadOnlyDictionary<int, double> widths)
    {
        ReadOnlySpan<byte> d = cff.Span;
        if (d.Length > MaxProgramBytes)
        {
            throw new FontProgramException("The CFF program is too large.");
        }

        int hdrSize = Be.U8(d, 2);
        if (hdrSize < 4 || hdrSize > d.Length)
        {
            throw new FontProgramException("The CFF header size is invalid.");
        }

        int nameStart = hdrSize;
        _ = CffIndex.Read(d, nameStart, out int topStart);
        CffIndex topIndex = CffIndex.Read(d, topStart, out int stringStart);
        _ = CffIndex.Read(d, stringStart, out int gsubrStart);
        _ = CffIndex.Read(d, gsubrStart, out int tailStart);
        if (topIndex.Count < 1)
        {
            throw new FontProgramException("The CFF font has no Top DICT.");
        }

        ReadOnlySpan<byte> topSpan = topIndex.Get(d, 0);
        List<CffSubsetter.DictEntry> top = CffSubsetter.ParseDict(topSpan);
        int ct = CffSubsetter.Find(top, 0x0C06);
        if (ct >= 0 && top[ct].First != 2)
        {
            throw new FontProgramException("Only Type 2 charstrings are supported.");
        }

        int csi = CffSubsetter.Find(top, 17);
        if (csi < 0)
        {
            throw new FontProgramException("The CFF Top DICT has no CharStrings.");
        }

        int oldCsOffset = Offset(top[csi].First);
        CffIndex charStrings = CffIndex.Read(d, oldCsOffset, out int oldCsEnd);
        int numGlyphs = charStrings.Count;
        if (numGlyphs < 1 || numGlyphs > FontLimits.MaxGlyphs)
        {
            throw new FontProgramException("The CFF font's glyph count is out of range.");
        }

        foreach (int g in widths.Keys)
        {
            if ((uint)g >= (uint)numGlyphs)
            {
                throw new FontProgramException("A glyph to change is outside the program.");
            }
        }

        bool isCid = CffSubsetter.Find(top, 0x0C1E) >= 0;

        // Private DICT sources, one per Font DICT (one for a name-keyed font).
        var fdDicts = new List<List<CffSubsetter.DictEntry>>();
        var fdBytes = new List<byte[]>();
        var privates = new List<PrivateSource>();
        int[] fdOfGlyph = new int[numGlyphs];
        int fdArrayOffset = 0;
        int fdSelectOffset = 0;
        if (isCid)
        {
            int fa = CffSubsetter.Find(top, 0x0C24), fs = CffSubsetter.Find(top, 0x0C25);
            if (fa < 0 || fs < 0)
            {
                throw new FontProgramException("A CID-keyed CFF font needs FDArray and FDSelect.");
            }

            fdArrayOffset = Offset(top[fa].First);
            fdSelectOffset = Offset(top[fs].First);
            CffIndex fdArray = CffIndex.Read(d, fdArrayOffset, out _);
            if (fdArray.Count < 1 || fdArray.Count > FontLimits.MaxFontDicts)
            {
                throw new FontProgramException("The CFF FDArray size is out of range.");
            }

            for (int i = 0; i < fdArray.Count; i++)
            {
                ReadOnlySpan<byte> fd = fdArray.Get(d, i);
                fdBytes.Add(fd.ToArray());
                List<CffSubsetter.DictEntry> entries = CffSubsetter.ParseDict(fd);
                fdDicts.Add(entries);
                privates.Add(ReadPrivate(d, entries));
            }

            CffSubsetter.ReadFdSelect(d, fdSelectOffset, fdOfGlyph, fdArray.Count);
        }
        else
        {
            privates.Add(ReadPrivate(d, top));
        }

        // New charstrings.
        var newCharStrings = new List<byte[]>(numGlyphs);
        for (int g = 0; g < numGlyphs; g++)
        {
            PrivateSource pv = privates[fdOfGlyph[g]];
            double? wanted = widths.TryGetValue(g, out double w) ? w : null;
            newCharStrings.Add(RewriteCharstring(charStrings.Get(d, g), pv, wanted));
        }

        byte[] csIndex = CffSubsetter.IndexBytes(newCharStrings);

        // Offsets that name things in the copied block move by the change in the Top DICT's size, which does not depend on their values
        // because they are written with five bytes.
        int oldTopLength = stringStart - topStart;
        bool rewritePrivate = !isCid && privates[0].Present;
        byte[] trial = CffSubsetter.IndexBytes([TopDict(top, topSpan, 0, 0, 0, tailStart, rewritePrivate, 0, 0)]);
        int delta = trial.Length - oldTopLength;
        long newTail = (long)d.Length + delta;
        CheckSize(newTail);

        int newCsOffset = d.Length + delta;
        long cursor = (long)newCsOffset + csIndex.Length;

        // Private DICTs and their subroutines.
        var privateBlocks = new List<byte[]>();
        var privateInfo = new List<(int Size, int Offset)>();
        foreach (PrivateSource pv in privates)
        {
            if (!pv.Present)
            {
                privateBlocks.Add([]);
                privateInfo.Add((0, 0));
                continue;
            }

            byte[] block = pv.Build(d);
            privateBlocks.Add(block);
            privateInfo.Add((pv.NewDictLength, (int)Math.Min(cursor, int.MaxValue)));
            cursor += block.Length;
            CheckSize(cursor);
        }

        byte[] fdArrayIndex = [];
        int newFdArrayOffset = 0;
        if (isCid)
        {
            var list = new List<byte[]>(fdDicts.Count);
            for (int i = 0; i < fdDicts.Count; i++)
            {
                list.Add(FdDict(fdBytes[i], fdDicts[i], privateInfo[i].Size, privateInfo[i].Offset));
            }

            fdArrayIndex = CffSubsetter.IndexBytes(list);
            newFdArrayOffset = (int)Math.Min(cursor, int.MaxValue);
            cursor += fdArrayIndex.Length;
            CheckSize(cursor);
        }

        byte[] topIndexBytes = CffSubsetter.IndexBytes([
            TopDict(top, topSpan, delta, newCsOffset, newFdArrayOffset, tailStart, rewritePrivate,
                rewritePrivate ? privateInfo[0].Size : 0, rewritePrivate ? privateInfo[0].Offset : 0),
        ]);
        if (topIndexBytes.Length != trial.Length)
        {
            throw new FontProgramException("The CFF Top DICT changed size unexpectedly.");
        }

        byte[] tail = d[tailStart..].ToArray();
        ZeroOldCharStrings(tail, tailStart, oldCsOffset, oldCsEnd, top, privates, fdArrayOffset, fdSelectOffset);

        var output = new List<byte>((int)cursor + 16);
        output.AddRange(d[..topStart].ToArray()); // header, Name INDEX
        output.AddRange(topIndexBytes);
        output.AddRange(d[stringStart..tailStart].ToArray()); // String INDEX, Global Subr INDEX
        output.AddRange(tail);
        output.AddRange(csIndex);
        foreach (byte[] block in privateBlocks)
        {
            output.AddRange(block);
        }

        output.AddRange(fdArrayIndex);
        if (output.Count != cursor)
        {
            throw new FontProgramException("The rewritten CFF program has an unexpected size.");
        }

        return [.. output];
    }

    // ---- Top DICT and Font DICTs ----

    // The Top DICT with its offsets replaced: raw bytes of every entry are kept except the ones that name positions in the file.
    private static byte[] TopDict(List<CffSubsetter.DictEntry> top, ReadOnlySpan<byte> raw, int delta, int csOffset, int fdArrayOffset,
        int firstMovable, bool rewritePrivate, int privateSize, int privateOffset)
    {
        var b = new List<byte>(raw.Length + 32);
        foreach (CffSubsetter.DictEntry e in top)
        {
            switch (e.Op)
            {
                case 15: // charset: 0, 1 and 2 name predefined sets
                case 16: // Encoding: 0 and 1 name predefined encodings
                    {
                        long v = (long)e.First;
                        if (v <= (e.Op == 15 ? 2 : 1))
                        {
                            b.AddRange(raw.Slice(e.RawStart, e.RawLength).ToArray());
                        }
                        else
                        {
                            PutShifted(b, v, delta, firstMovable);
                            CffSubsetter.PutOp(b, e.Op);
                        }

                        break;
                    }

                case 0x0C25: // FDSelect
                    PutShifted(b, (long)e.First, delta, firstMovable);
                    CffSubsetter.PutOp(b, e.Op);
                    break;
                case 17:
                    CffSubsetter.PutInt(b, csOffset);
                    CffSubsetter.PutOp(b, 17);
                    break;
                case 0x0C24:
                    CffSubsetter.PutInt(b, fdArrayOffset);
                    CffSubsetter.PutOp(b, e.Op);
                    break;
                case 18:
                    if (!rewritePrivate)
                    {
                        b.AddRange(raw.Slice(e.RawStart, e.RawLength).ToArray());
                    }
                    else
                    {
                        CffSubsetter.PutInt(b, privateSize);
                        CffSubsetter.PutInt(b, privateOffset);
                        CffSubsetter.PutOp(b, 18);
                    }

                    break;
                default:
                    b.AddRange(raw.Slice(e.RawStart, e.RawLength).ToArray());
                    break;
            }
        }

        return [.. b];
    }

    private static void CheckSize(long size)
    {
        if (size > MaxProgramBytes)
        {
            throw new FontProgramException("The CFF program would be too large.");
        }
    }

    // An offset or size read from a DICT: a whole number within the largest program this rewrite handles.
    private static int Offset(double v) =>
        v >= 0 && v <= MaxProgramBytes && v == Math.Floor(v) ? (int)v : throw new FontProgramException("A CFF offset or size is out of range.");

    private static void PutShifted(List<byte> b, long offset, int delta, int firstMovable)
    {
        if (offset < firstMovable || offset > MaxProgramBytes)
        {
            throw new FontProgramException("A CFF table lies in a place this rewrite cannot move.");
        }

        CffSubsetter.PutInt(b, (int)(offset + delta));
    }

    private static byte[] FdDict(byte[] raw, List<CffSubsetter.DictEntry> entries, int privateSize, int privateOffset)
    {
        var b = new List<byte>(raw.Length + 16);
        foreach (CffSubsetter.DictEntry e in entries)
        {
            if (e.Op == 18 && privateSize > 0)
            {
                CffSubsetter.PutInt(b, privateSize);
                CffSubsetter.PutInt(b, privateOffset);
                CffSubsetter.PutOp(b, 18);
            }
            else
            {
                b.AddRange(raw.AsSpan(e.RawStart, e.RawLength).ToArray());
            }
        }

        return [.. b];
    }

    // ---- Private DICTs ----

    private sealed class PrivateSource
    {
        public bool Present;
        public int Offset;
        public int Size;
        public List<CffSubsetter.DictEntry> Entries = [];
        public double OldDefault;
        public double OldNominal;
        public int NewDefault;
        public int NewNominal;
        public int SubrsStart = -1;
        public int SubrsEnd;
        public int NewDictLength;

        /// <summary>The new Private DICT followed by its local subroutines.</summary>
        public byte[] Build(ReadOnlySpan<byte> d)
        {
            ReadOnlySpan<byte> priv = d.Slice(Offset, Size);
            var kept = new List<byte>(Size + 16);
            foreach (CffSubsetter.DictEntry e in Entries)
            {
                if (e.Op is 19 or 20 or 21)
                {
                    continue;
                }

                kept.AddRange(priv.Slice(e.RawStart, e.RawLength).ToArray());
            }

            CffSubsetter.PutCompact(kept, NewDefault);
            CffSubsetter.PutOp(kept, 20);
            CffSubsetter.PutCompact(kept, NewNominal);
            CffSubsetter.PutOp(kept, 21);
            if (SubrsStart >= 0)
            {
                // The Subrs offset is relative to the start of this DICT and is written with five bytes, so the DICT's length is known.
                NewDictLength = kept.Count + 6;
                CffSubsetter.PutInt(kept, NewDictLength);
                CffSubsetter.PutOp(kept, 19);
                kept.AddRange(d[SubrsStart..SubrsEnd].ToArray());
                return [.. kept];
            }

            NewDictLength = kept.Count;
            return [.. kept];
        }
    }

    private static PrivateSource ReadPrivate(ReadOnlySpan<byte> d, List<CffSubsetter.DictEntry> owner)
    {
        var pv = new PrivateSource();
        int i = CffSubsetter.Find(owner, 18);
        if (i < 0 || owner[i].First == 0)
        {
            return pv;
        }

        pv.Present = true;
        pv.Size = Offset(owner[i].First);
        pv.Offset = Offset(owner[i].Second);
        ReadOnlySpan<byte> priv = Be.Slice(d, pv.Offset, pv.Size);
        pv.Entries = CffSubsetter.ParseDict(priv);
        int dw = CffSubsetter.Find(pv.Entries, 20), nw = CffSubsetter.Find(pv.Entries, 21);
        pv.OldDefault = dw >= 0 ? pv.Entries[dw].First : 0;
        pv.OldNominal = nw >= 0 ? pv.Entries[nw].First : 0;
        pv.NewDefault = RoundWhole(pv.OldDefault);
        pv.NewNominal = RoundWhole(pv.OldNominal);
        int sb = CffSubsetter.Find(pv.Entries, 19);
        if (sb >= 0)
        {
            pv.SubrsStart = pv.Offset + Offset(pv.Entries[sb].First);
            _ = CffIndex.Read(d, pv.SubrsStart, out pv.SubrsEnd);
        }

        return pv;
    }

    private static int RoundWhole(double v) => (int)Math.Clamp(Math.Round(v), -32768, 32767);

    // ---- charstrings ----

    private static byte[] RewriteCharstring(ReadOnlySpan<byte> cs, PrivateSource pv, double? wanted)
    {
        // A font with no Private DICT has both defaults 0 and keeps them.
        double oldDefault = pv.Present ? pv.OldDefault : 0, oldNominal = pv.Present ? pv.OldNominal : 0;
        int newDefault = pv.Present ? pv.NewDefault : 0, newNominal = pv.Present ? pv.NewNominal : 0;
        ScanHead(cs, out bool hasWidth, out double firstOperand, out int firstLength);
        double old = hasWidth ? oldNominal + firstOperand : oldDefault;
        double target = wanted ?? old;
        long targetFixed = CffSubsetter.ToFixed(target);
        ReadOnlySpan<byte> rest = hasWidth ? cs[firstLength..] : cs;
        var b = new List<byte>(cs.Length + 5);
        if (targetFixed != (long)newDefault << 16)
        {
            CffSubsetter.PutType2(b, CffSubsetter.ToFixed(target - newNominal));
        }

        b.AddRange(rest.ToArray());
        return [.. b];
    }

    // Finds out whether the charstring starts with a width operand (TN #5177 section 3.1) and where it ends. The operator that ends the
    // leading operands decides: stems and masks take pairs, so an odd count has a width; moves and endchar take fixed counts.
    private static void ScanHead(ReadOnlySpan<byte> cs, out bool hasWidth, out double first, out int firstLength)
    {
        hasWidth = false;
        first = 0;
        firstLength = 0;
        int p = 0, count = 0;
        while (p < cs.Length)
        {
            int b0 = cs[p];
            if (b0 >= 32 || b0 == 28)
            {
                int len = NumberLength(b0);
                if (p + len > cs.Length)
                {
                    throw new FontProgramException("A charstring ends inside a number.");
                }

                if (count == 0)
                {
                    first = ReadNumber(cs, p);
                    firstLength = len;
                }

                if (++count > MaxHeadOperands)
                {
                    throw new FontProgramException("A charstring starts with too many operands.");
                }

                p += len;
                continue;
            }

            switch (b0)
            {
                case 1 or 3 or 18 or 23 or 19 or 20:
                    hasWidth = (count & 1) == 1;
                    return;
                case 21:
                    hasWidth = count > 2;
                    return;
                case 22 or 4:
                    hasWidth = count > 1;
                    return;
                case 14:
                    hasWidth = count is 1 or 5;
                    return;
                default:
                    throw new FontProgramException("A charstring reaches its width through a subroutine or an unexpected operator.");
            }
        }

        if (count != 0)
        {
            throw new FontProgramException("A charstring has operands but no operator.");
        }
    }

    private static int NumberLength(int b0) => b0 switch
    {
        28 => 3,
        255 => 5,
        >= 247 and <= 254 => 2,
        _ => 1,
    };

    private static double ReadNumber(ReadOnlySpan<byte> cs, int p)
    {
        int b0 = cs[p];
        return b0 switch
        {
            28 => (short)((cs[p + 1] << 8) | cs[p + 2]),
            255 => ((cs[p + 1] << 24) | (cs[p + 2] << 16) | (cs[p + 3] << 8) | cs[p + 4]) / 65536.0,
            >= 247 and <= 250 => ((b0 - 247) * 256) + cs[p + 1] + 108,
            >= 251 and <= 254 => (-(b0 - 251) * 256) - cs[p + 1] - 108,
            _ => b0 - 139,
        };
    }

    // The old CharStrings INDEX sits inside the copied block. Nothing refers to it any more, so it is blanked unless another structure
    // has a known start inside it.
    private static void ZeroOldCharStrings(byte[] tail, int tailStart, int csStart, int csEnd, List<CffSubsetter.DictEntry> top,
        List<PrivateSource> privates, int fdArrayOffset, int fdSelectOffset)
    {
        if (csStart < tailStart || csEnd > tailStart + tail.Length)
        {
            return;
        }

        var starts = new List<long>();
        foreach (CffSubsetter.DictEntry e in top)
        {
            if (e.Op is 15 or 16 && e.First > 2)
            {
                starts.Add((long)e.First);
            }
        }

        starts.Add(fdArrayOffset);
        starts.Add(fdSelectOffset);
        foreach (PrivateSource pv in privates)
        {
            if (pv.Present)
            {
                starts.Add(pv.Offset);
                starts.Add(pv.Offset + pv.Size);
                if (pv.SubrsStart >= 0)
                {
                    starts.Add(pv.SubrsStart);
                }
            }
        }

        foreach (long s in starts)
        {
            if (s > csStart && s < csEnd)
            {
                return;
            }
        }

        Array.Clear(tail, csStart - tailStart, csEnd - csStart);
    }
}
