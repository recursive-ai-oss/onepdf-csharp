using System.Buffers.Binary;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnePdf.FontData;
using OnePdf.FontPrograms;
using OnePdf.FontWriting;
using OnePdf.PdfA;
using OnePdf.PdfFonts;

namespace OnePdf.FontEmbedding;

/// <summary>
/// Embeds a TrueType font program into a font dictionary the document already has: simple Type1 and TrueType fonts, and Type 0 fonts
/// with a CIDFontType2 descendant. The subset keeps the glyph outlines of the program and gives the glyphs the PDF's advance widths;
/// the PDF's own <c>/Widths</c>, positions and text are not changed.
/// </summary>
internal static class FontEmbedder
{
    private const int MaxCid = 0xFFFF;

    public static PdfFontEmbedResult Embed(PdfDocument doc, PdfDictionary font, PdfFontFile program, PdfFontEmbedOptions o, CancellationToken ct)
    {
        doc.EnsureEditable(0);
        string? subtype = font.GetName("Subtype") ?? (font.ContainsKey("DescendantFonts") ? "Type0" : null);
        if (subtype == "Type3")
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.Type3Font, "A Type 3 font draws its glyphs with content streams; there is no font program to embed.");
        }

        if (subtype is not ("Type1" or "MMType1" or "TrueType" or "Type0"))
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.UnsupportedFont, "The dictionary is not a Type1, TrueType or Type0 font.");
        }

        if (program.Kind != PdfFontFileKind.TrueType || program.Sfnt is null)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.UnsupportedProgram, "Only fonts with TrueType (glyf) outlines can be embedded.");
        }

        if (!o.IgnoreEmbeddingRestrictions && (!program.AllowsEmbedding || (o.Subset && !program.AllowsSubsetting)))
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.ProgramForbidsEmbedding,
                $"The font's embedding flags (fsType 0x{program.FsType:X4}) do not allow {(program.AllowsEmbedding ? "subsetting" : "embedding")}.");
        }

        PdfFontEmbedResult result = subtype == "Type0" ? EmbedComposite(doc, font, program, o, ct) : EmbedSimple(doc, font, program, o, ct);
        doc.ForgetFont(font);
        return result;
    }

    // ---- simple fonts -----------------------------------------------------------------------------------------------------------

    private static PdfFontEmbedResult EmbedSimple(PdfDocument doc, PdfDictionary font, PdfFontFile program, PdfFontEmbedOptions o, CancellationToken ct)
    {
        SfntFont sfnt = program.Sfnt!;
        PdfDictionary? desc = font.GetDictionary("FontDescriptor");
        if (desc is not null && HasFontFile(desc) && !o.Replace)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.AlreadyEmbedded, "The font already has an embedded program.");
        }

        string baseName = StripTag(font.GetName("BaseFont") ?? desc?.GetName("FontName") ?? "Font");
        string? stdName = Core14.Resolve(baseName);
        StandardFontMetrics? std = stdName is null ? null : Core14.Get(stdName);
        SimpleEncoding.Read(font, out bool present, out PredefinedEncoding? baseEnc, out PdfArray? diffs);
        int flags = desc is not null && desc.ContainsKey("Flags") ? (int)desc.GetInt("Flags") : stdName is "Symbol" or "ZapfDingbats" ? 4 : 32;
        bool symbolicSource = (flags & 4) != 0 && (flags & 32) == 0 && !present;

        var names = new string?[256];
        Array.Copy(baseEnc is { } be ? Encodings.Get(be) : std?.BuiltInEncoding ?? Encodings.Get(PredefinedEncoding.Standard), names, 256);
        SimpleEncoding.ApplyDifferences(names, diffs);

        // The codes to give glyphs to. A font a form may still be filled in with keeps its whole encoding even if the pages show only a few
        // codes; what the result reports as missing is about the codes the pages do show (null: unknown, so every code counts).
        HashSet<int>? shown = null;
        HashSet<int>? reportShown = null;
        if (o.Codes is not null)
        {
            shown = [.. o.Codes.Where(static c => c is >= 0 and <= 255)];
            reportShown = shown;
        }
        else if (o.Subset && font.Reference is { } fr)
        {
            FontUsage usage = FontUsage.Scan(doc, [fr.Number], ct);
            if (usage.Complete)
            {
                reportShown = usage.Used.TryGetValue(fr.Number, out HashSet<int>? seen) ? seen : [];
                if (!usage.Full.Contains(fr.Number) && seen is { Count: > 0 })
                {
                    shown = seen;
                }
            }
        }

        bool tolerant = shown is null;
        List<int> codes = shown is not null
            ? [.. shown.OrderBy(static c => c)]
            : [.. Enumerable.Range(0, 256).Where(c => symbolicSource || names[c] is not null || (o.CodeToUnicode?.ContainsKey(c) ?? false))];

        var gids = new SortedDictionary<int, int>();
        var texts = new Dictionary<int, string?>();
        var nameTexts = new Dictionary<int, string?>();
        var missing = new List<int>();
        foreach (int code in codes)
        {
            string? name = names[code];
            string? explicitText = o.CodeToUnicode is not null && o.CodeToUnicode.TryGetValue(code, out string? t) && t.Length > 0 ? t : null;
            string? nameText = name is not null && FontSupport.NameToUnicode(name, stdName == "ZapfDingbats", out string? nt) && nt.Length > 0 ? nt : null;
            string? text = explicitText ?? nameText;
            int g = 0;
            if (o.GlyphForCode is { } pick)
            {
                int chosen = pick(code);
                g = chosen > 0 && chosen < program.GlyphCount ? chosen : 0;
            }

            if (g == 0 && symbolicSource)
            {
                g = program.GetGlyphForSymbolicCode(code);
            }

            if (g == 0 && text is not null && SingleRune(text, out int cp))
            {
                int mapped = o.CodePointMap?.Invoke(cp) ?? cp;
                g = program.GetGlyphForUnicode(mapped);
                if (g == 0 && mapped != cp)
                {
                    g = program.GetGlyphForUnicode(cp);
                }
            }

            if (g == 0 && name is not null)
            {
                g = program.GetGlyphForName(name);
            }

            texts[code] = text;
            nameTexts[code] = nameText;
            if (g > 0)
            {
                gids[code] = g;
            }
            else
            {
                missing.Add(code);
            }
        }

        if (missing.Count > 0 && !tolerant && o.MissingGlyphs == PdfMissingGlyphPolicy.Refuse)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.NoGlyphForCode, $"{missing.Count} shown code(s) have no glyph in the font program.", missing);
        }

        // Non-symbolic output reaches glyphs by Unicode through glyph names (ISO 32000-2 9.6.5.4); anything that can't be named that way
        // makes the whole font symbolic, where every used code has its own entry in a (3,0) map.
        bool symbolicOut = symbolicSource || gids.Keys.Any(c => texts[c] is not { } tx || !SingleRune(tx, out int cp) || cp > 0xFFFE);

        // Widths.
        PdfArray? widths = font.GetArray("Widths");
        int firstChar = (int)Math.Clamp(font.GetInt("FirstChar"), 0, 255);
        double missingWidth = desc?.GetDouble("MissingWidth") ?? 0;
        double PdfWidth(int code)
        {
            if (widths is not null)
            {
                int i = code - firstChar;
                return i >= 0 && i < widths.Count ? widths.GetDouble(i) : missingWidth;
            }

            if (std is not null && names[code] is { } n)
            {
                return std.GetWidth(n) ?? missingWidth;
            }

            return program.TryGetAdvanceWidth(gids.TryGetValue(code, out int g) ? g : 0, out double w) ? Math.Round(w) : missingWidth;
        }

        var wanted = new List<(int Code, double Pdf)>();
        foreach (int code in gids.Keys)
        {
            wanted.Add((code, PdfWidth(code)));
        }

        (Dictionary<int, int> advances, List<PdfFontWidthChange> changes, List<int> conflicts) = PlanWidths(program, o, wanted, gids.Values.ToList(), gids.Keys.ToList());

        // The subset.
        List<int> glyphs = o.Subset ? [.. gids.Values.Distinct()] : [.. Enumerable.Range(1, Math.Max(0, program.GlyphCount - 1))];
        if (glyphs.Count > FontLimits.MaxGlyphs)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.TooLarge, "The program has too many glyphs to embed whole.");
        }

        var pairs = new List<(int Unicode, int Glyph)>();
        foreach ((int code, int g) in gids)
        {
            if (symbolicOut)
            {
                pairs.Add((0xF000 + code, g));
            }
            else
            {
                SingleRune(texts[code]!, out int cp);
                pairs.Add((cp, g));
            }
        }

        byte[] data = BuildSubset(sfnt, glyphs, pairs, advances, symbolicOut ? 0 : 1);
        string tag = SubsetTag(program.Data.Span, glyphs, advances, baseName);
        string newName = o.Subset ? tag + "+" + baseName.Replace(" ", string.Empty, StringComparison.Ordinal) : baseName.Replace(" ", string.Empty, StringComparison.Ordinal);

        // Encoding written for non-symbolic output (ISO 32000-2 9.6.5.4: WinAnsi or MacRoman, plus glyph names for what differs).
        PdfObject? newEncoding = null;
        bool differences = false;
        if (!symbolicOut)
        {
            newEncoding = BuildEncoding(font, baseEnc, names, gids.Keys, texts, nameTexts, shown is null, out differences);
        }

        // Writes begin here: nothing above changed the document.
        PdfStream file = doc.CreateStream(data, compress: true);
        file.Dictionary.SetEntry("Length1", new PdfInteger(data.Length));
        PdfReference fileRef = doc.AddObject(file);
        int outFlags = symbolicOut ? (flags | 4) & ~32 : (flags | 32) & ~4;
        if (desc is null && stdName is not null)
        {
            outFlags |= StyleFlags(stdName);
        }

        // The descriptor is rewritten as a new object: other fonts of the file may share the old one and still need it as it was.
        PdfObject fdValue = doc.AddObject(NewDescriptor(desc, program, newName, outFlags, baseName, fileRef));

        font.SetEntry("Subtype", new PdfName("TrueType"));
        font.SetEntry("BaseFont", new PdfName(newName));
        if (symbolicOut)
        {
            font.RemoveEntry("Encoding");
        }
        else if (newEncoding is not null)
        {
            font.SetEntry("Encoding", newEncoding);
        }

        if (widths is null)
        {
            int lo = gids.Count > 0 ? gids.Keys.Min() : 0;
            int hi = gids.Count > 0 ? gids.Keys.Max() : 0;
            var arr = new PdfArray();
            for (int c = lo; c <= hi; c++)
            {
                arr.Append(new PdfInteger((long)Math.Round(gids.ContainsKey(c) ? PdfWidth(c) : 0)));
            }

            font.SetEntry("FirstChar", new PdfInteger(lo));
            font.SetEntry("LastChar", new PdfInteger(hi));
            font.SetEntry("Widths", arr);
        }

        font.SetEntry("FontDescriptor", fdValue);

        // ToUnicode: keep what is there, add what the encoding no longer says by itself.
        bool needTu = o.WriteToUnicode && (font.ContainsKey("ToUnicode") || (o.CodeToUnicode?.Count ?? 0) > 0 || symbolicOut || differences);
        var noUnicode = new List<int>();
        bool tuWritten = false;
        if (needTu)
        {
            var entries = new Dictionary<int, (int, string)>();
            foreach (int code in gids.Keys.Concat(missing))
            {
                if (texts[code] is { } tx)
                {
                    entries[code] = (1, tx);
                }
                else
                {
                    noUnicode.Add(code);
                }
            }

            if (o.OverrideToUnicode && o.CodeToUnicode is not null)
            {
                tuWritten = ToUnicodeWriter.Merge(doc, font, entries.Where(e => o.CodeToUnicode.ContainsKey(e.Key)).ToDictionary(static e => e.Key, static e => e.Value), true);
            }

            tuWritten |= ToUnicodeWriter.Merge(doc, font, entries, false);
        }

        return new PdfFontEmbedResult
        {
            FontFile = fileRef,
            BaseFont = newName,
            EmbeddedCodes = [.. gids.Keys],
            MissingCodes = reportShown is null ? missing : [.. missing.Where(reportShown.Contains)],
            WidthChanges = changes,
            WidthConflicts = conflicts,
            ToUnicodeWritten = tuWritten,
            CodesWithoutUnicode = noUnicode,
            Symbolic = symbolicOut,
        };
    }

    // The /Encoding for a non-symbolic TrueType font whose codes are named by glyph names.
    private static PdfObject BuildEncoding(PdfDictionary font, PredefinedEncoding? baseEnc, string?[] names, IEnumerable<int> used,
        Dictionary<int, string?> texts, Dictionary<int, string?> nameTexts, bool allCodes, out bool differences)
    {
        bool baseKnown = baseEnc is PredefinedEncoding.WinAnsi or PredefinedEncoding.MacRoman;
        PredefinedEncoding target = baseKnown ? baseEnc!.Value : PredefinedEncoding.WinAnsi;
        string?[] table = Encodings.Get(target);
        var outNames = new string?[256];
        var usedSet = used.ToHashSet();
        for (int c = 0; c < 256; c++)
        {
            // A name keeps its code when it still means the text the glyph was chosen by; otherwise the glyph is named by its Unicode value.
            if (usedSet.Contains(c) && texts[c] is { } text && nameTexts[c] != text && SingleRune(text, out int cp))
            {
                outNames[c] = "uni" + cp.ToString("X4", CultureInfo.InvariantCulture);
            }
            else if (usedSet.Contains(c) || baseKnown || allCodes)
            {
                outNames[c] = names[c];
            }
        }

        var d = new PdfArray();
        int prev = -2;
        for (int c = 0; c < 256; c++)
        {
            if (outNames[c] is { } nm && nm != table[c])
            {
                if (c != prev + 1)
                {
                    d.Append(new PdfInteger(c));
                }

                d.Append(new PdfName(nm));
                prev = c;
            }
        }

        differences = d.Count > 0;
        string encName = target == PredefinedEncoding.MacRoman ? "MacRomanEncoding" : "WinAnsiEncoding";
        if (!differences)
        {
            return font.Get("Encoding") is PdfName { Value: "WinAnsiEncoding" or "MacRomanEncoding" } existing ? existing : new PdfName(encName);
        }

        var ed = new PdfDictionary();
        ed.SetEntry("Type", new PdfName("Encoding"));
        ed.SetEntry("BaseEncoding", new PdfName(encName));
        ed.SetEntry("Differences", d);
        return ed;
    }

    // ---- composite fonts --------------------------------------------------------------------------------------------------------

    private static PdfFontEmbedResult EmbedComposite(PdfDocument doc, PdfDictionary font, PdfFontFile program, PdfFontEmbedOptions o, CancellationToken ct)
    {
        SfntFont sfnt = program.Sfnt!;
        PdfObject? d0 = font.Get("DescendantFonts");
        PdfDictionary? cidFont = d0 is PdfArray { Count: > 0 } arr ? arr[0] as PdfDictionary : d0 as PdfDictionary;
        if (cidFont is null || cidFont.GetName("Subtype") != "CIDFontType2")
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.UnsupportedFont, "The font has no CIDFontType2 descendant; only TrueType-based CID fonts take a TrueType program.");
        }

        PdfDictionary? desc = cidFont.GetDictionary("FontDescriptor");
        if (desc is not null && HasFontFile(desc) && !o.Replace)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.AlreadyEmbedded, "The font already has an embedded program.");
        }

        string type0Name = font.GetName("BaseFont") ?? string.Empty;
        string baseName = StripTag(cidFont.GetName("BaseFont") ?? desc?.GetName("FontName") ?? type0Name);
        if (baseName.Length == 0)
        {
            baseName = "Font";
        }

        // Existing ToUnicode, to choose glyphs by Unicode.
        CMap? tu = null;
        if (font.Get("ToUnicode") is PdfStream tus)
        {
            try
            {
                tu = CMapParser.Parse(tus.GetDecodedBytes(ct).Span, static _ => null);
            }
            catch (PdfFormatException)
            {
                tu = null;
            }
        }

        PdfFont pf = doc.GetFont(font);
        var used = new SortedDictionary<uint, (int Cid, int Length)>();
        bool tolerant = true;
        HashSet<int>? reportShown = null; // the codes the pages show, when that is known
        if (o.Codes is not null)
        {
            tolerant = false;
            var one = new List<FontGlyph>();
            foreach (int c in o.Codes.Where(static c => c is >= 0 and <= 0xFFFF).OrderBy(static c => c))
            {
                one.Clear();
                pf.Decode([(byte)(c >> 8), (byte)c], one);
                foreach (FontGlyph g in one)
                {
                    used[g.Code] = (g.Cid, g.CodeLength);
                }
            }
        }
        else if (o.Subset && font.Reference is { } fr)
        {
            FontUsage usage = FontUsage.Scan(doc, [fr.Number], ct, new Dictionary<int, PdfFont> { [fr.Number] = pf });
            if (usage.Complete)
            {
                reportShown = usage.UsedComposite.TryGetValue(fr.Number, out Dictionary<uint, (int Cid, int Length)>? shownNow) ? [.. shownNow.Keys.Select(static c => unchecked((int)c))] : [];
            }

            if (usage.Complete && usage.UsedComposite.TryGetValue(fr.Number, out Dictionary<uint, (int Cid, int Length)>? seen) && seen.Count > 0)
            {
                tolerant = false;
                foreach ((uint code, (int, int) v) in seen)
                {
                    used[code] = v;
                }
            }
        }

        if (tolerant)
        {
            // Usage unknown: every code the text map or the caller knows.
            var one = new List<FontGlyph>();
            var known = new SortedSet<int>(o.CodeToUnicode?.Keys ?? Enumerable.Empty<int>());
            if (tu is not null)
            {
                foreach ((uint code, int length, _) in tu.EnumerateUnicode(1 << 16))
                {
                    if (length == 2)
                    {
                        known.Add((int)code);
                    }
                }
            }

            foreach (int c in known.Where(static c => c is >= 0 and <= 0xFFFF))
            {
                one.Clear();
                pf.Decode([(byte)(c >> 8), (byte)c], one);
                foreach (FontGlyph g in one)
                {
                    used[g.Code] = (g.Cid, g.CodeLength);
                }
            }
        }

        var cidToGid = new SortedDictionary<int, int>();
        var codeGid = new Dictionary<uint, int>();
        var texts = new Dictionary<uint, string?>();
        var missing = new List<int>();
        foreach ((uint code, (int cid, int length)) in used)
        {
            if (cid is < 0 or > MaxCid)
            {
                throw new PdfFontEmbedException(PdfFontEmbedFailure.TooLarge, "A character id is outside 0 to 65535.");
            }

            string? text = o.CodeToUnicode is not null && o.CodeToUnicode.TryGetValue((int)code, out string? ex) && ex.Length > 0 ? ex
                : tu is not null && tu.TryGetUnicode(code, length, out string t) && t.Length > 0 ? t : null;
            texts[code] = text;
            int gid = 0;
            if (o.GlyphForCode is { } pick)
            {
                int chosen = pick((int)code);
                gid = chosen > 0 && chosen < program.GlyphCount ? chosen : 0;
            }

            if (gid == 0)
            {
                gid = o.CidsAreGlyphIds ? (cid < program.GlyphCount ? cid : 0) : text is not null && SingleRune(text, out int cp) ? program.GetGlyphForUnicode(cp) : 0;
            }

            if (gid > 0)
            {
                cidToGid[cid] = gid;
                codeGid[code] = gid;
            }
            else
            {
                missing.Add((int)code);
            }
        }

        if (missing.Count > 0 && !tolerant && o.MissingGlyphs == PdfMissingGlyphPolicy.Refuse)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.NoGlyphForCode, $"{missing.Count} shown code(s) have no glyph in the font program.", missing);
        }

        // Widths: /W and /DW of the descendant, by character id.
        double dw = cidFont.ContainsKey("DW") ? cidFont.GetDouble("DW") : 1000;
        Dictionary<int, double> w = ReadCidWidths(cidFont.GetArray("W"), cidToGid.Keys.ToHashSet());
        var wanted = new List<(int Code, double Pdf)>();
        foreach (int cid in cidToGid.Keys)
        {
            wanted.Add((cid, w.TryGetValue(cid, out double v) ? v : dw));
        }

        (Dictionary<int, int> advances, List<PdfFontWidthChange> changes, List<int> conflicts) =
            PlanWidths(program, o, wanted, cidToGid.Values.ToList(), cidToGid.Keys.ToList());

        // The subset. Glyph i + 1 of the output is the i-th glyph asked for, so the map from character id to output glyph follows from the order.
        List<int> glyphs;
        Dictionary<int, int> newGid = [];
        bool identityMap = false;
        if (o.Subset)
        {
            glyphs = [.. cidToGid.Values.Distinct()];
            for (int i = 0; i < glyphs.Count; i++)
            {
                newGid[glyphs[i]] = i + 1;
            }
        }
        else
        {
            glyphs = [.. Enumerable.Range(1, Math.Max(0, program.GlyphCount - 1))];
            if (glyphs.Count > FontLimits.MaxGlyphs)
            {
                throw new PdfFontEmbedException(PdfFontEmbedFailure.TooLarge, "The program has too many glyphs to embed whole.");
            }

            foreach (int g in glyphs)
            {
                newGid[g] = g;
            }

            identityMap = o.CidsAreGlyphIds;
        }

        byte[] data = BuildSubset(sfnt, glyphs, null, advances, 1);
        string tag = SubsetTag(program.Data.Span, glyphs, advances, baseName);
        string cleanName = baseName.Replace(" ", string.Empty, StringComparison.Ordinal);
        string newName = o.Subset ? tag + "+" + cleanName : cleanName;

        byte[]? map = null;
        if (!identityMap)
        {
            int maxCid = cidToGid.Count == 0 ? 0 : cidToGid.Keys.Max();
            map = new byte[2 * (maxCid + 1)];
            foreach ((int cid, int g) in cidToGid)
            {
                BinaryPrimitives.WriteUInt16BigEndian(map.AsSpan(2 * cid), (ushort)newGid[g]);
            }
        }

        // Writes begin here.
        PdfStream file = doc.CreateStream(data, compress: true);
        file.Dictionary.SetEntry("Length1", new PdfInteger(data.Length));
        PdfReference fileRef = doc.AddObject(file);
        int flags = desc is not null && desc.ContainsKey("Flags") ? (int)desc.GetInt("Flags") : 4;
        cidFont.SetEntry("FontDescriptor", doc.AddObject(NewDescriptor(desc, program, newName, flags, baseName, fileRef)));

        cidFont.SetEntry("BaseFont", new PdfName(newName));
        if (map is null)
        {
            cidFont.SetEntry("CIDToGIDMap", new PdfName("Identity"));
        }
        else
        {
            cidFont.SetEntry("CIDToGIDMap", doc.AddObject(doc.CreateStream(map, compress: true)));
        }

        // The Type 0 name keeps whatever follows the descendant's name (the "-Identity-H" some producers append).
        string oldDescName = StripTag(type0Name);
        string suffix = oldDescName.StartsWith(baseName, StringComparison.Ordinal) ? oldDescName[baseName.Length..] : string.Empty;
        font.SetEntry("BaseFont", new PdfName(newName + suffix));

        bool needTu = o.WriteToUnicode && (tu is not null || (o.CodeToUnicode?.Count ?? 0) > 0);
        var noUnicode = new List<int>();
        bool tuWritten = false;
        if (needTu)
        {
            var entries = new Dictionary<int, (int, string)>();
            foreach ((uint code, (int _, int length)) in used)
            {
                if (texts[code] is { } tx)
                {
                    entries[(int)code] = (length, tx);
                }
                else
                {
                    noUnicode.Add((int)code);
                }
            }

            tuWritten = ToUnicodeWriter.Merge(doc, font, entries, o.OverrideToUnicode);
        }

        return new PdfFontEmbedResult
        {
            FontFile = fileRef,
            BaseFont = newName,
            EmbeddedCodes = [.. codeGid.Keys.Select(static c => (int)c).OrderBy(static c => c)],
            MissingCodes = reportShown is null ? missing : [.. missing.Where(reportShown.Contains)],
            WidthChanges = changes,
            WidthConflicts = conflicts,
            ToUnicodeWritten = tuWritten,
            CodesWithoutUnicode = noUnicode,
            Symbolic = false,
        };
    }

    // /W: "c [w1 w2 ...]" gives consecutive ids their own widths, "c1 c2 w" gives a range one width (ISO 32000-2 9.7.4.3).
    private static Dictionary<int, double> ReadCidWidths(PdfArray? w, HashSet<int> needed)
    {
        var result = new Dictionary<int, double>();
        if (w is null)
        {
            return result;
        }

        int i = 0;
        int steps = 0;
        while (i < w.Count && steps++ < (1 << 20))
        {
            if (w[i] is not (PdfInteger or PdfReal))
            {
                i++;
                continue;
            }

            int first = (int)Math.Clamp(w.GetInt(i), 0, MaxCid);
            if (i + 1 < w.Count && w[i + 1] is PdfArray list)
            {
                for (int k = 0; k < list.Count && first + k <= MaxCid; k++)
                {
                    if (needed.Contains(first + k))
                    {
                        result[first + k] = list.GetDouble(k);
                    }
                }

                i += 2;
            }
            else if (i + 2 < w.Count)
            {
                int last = (int)Math.Clamp(w.GetInt(i + 1), 0, MaxCid);
                double width = w.GetDouble(i + 2);
                foreach (int cid in needed)
                {
                    if (cid >= first && cid <= last)
                    {
                        result[cid] = width;
                    }
                }

                i += 3;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    // ---- shared pieces ----------------------------------------------------------------------------------------------------------

    // Decides the advance each glyph gets in the subset. A glyph has one advance, so when two codes share a glyph the first one decides.
    private static (Dictionary<int, int> Advances, List<PdfFontWidthChange> Changes, List<int> Conflicts) PlanWidths(
        PdfFontFile program, PdfFontEmbedOptions o, List<(int Code, double Pdf)> wanted, List<int> glyphOf, List<int> codeOf)
    {
        var advances = new Dictionary<int, int>();
        var changes = new List<PdfFontWidthChange>();
        var conflicts = new List<int>();
        var bad = new List<int>();
        int upem = program.UnitsPerEm;
        for (int i = 0; i < wanted.Count; i++)
        {
            (int code, double pdf) = wanted[i];
            int g = glyphOf[i];
            program.TryGetAdvanceWidth(g, out double prog);
            int units = (int)Math.Round(pdf * upem / 1000.0);
            if (Math.Abs(pdf - prog) > 0.5)
            {
                changes.Add(new PdfFontWidthChange(code, pdf, prog));
                if (Math.Abs(pdf - prog) > o.MaxWidthDifference)
                {
                    bad.Add(code);
                }
            }

            if (advances.TryGetValue(g, out int have))
            {
                if (Math.Abs(have - units) * 1000.0 / upem > 0.5)
                {
                    conflicts.Add(code);
                }

                continue;
            }

            advances[g] = units;
        }

        if (o.Widths == PdfFontWidthPolicy.Refuse && bad.Count > 0)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.WidthMismatch, $"{bad.Count} width(s) differ from the program's advances by more than {o.MaxWidthDifference.ToString(CultureInfo.InvariantCulture)}/1000 em.", bad);
        }

        if (o.Widths != PdfFontWidthPolicy.AdjustProgram)
        {
            advances.Clear();
        }

        return (advances, changes, conflicts);
    }

    private static byte[] BuildSubset(SfntFont sfnt, List<int> glyphs, List<(int Unicode, int Glyph)>? pairs, Dictionary<int, int> advances, int cmapEncoding)
    {
        try
        {
            return TrueTypeSubsetter.Subset(sfnt, glyphs, pairs, advances.Count > 0 ? advances : null, 3, cmapEncoding);
        }
        catch (FontProgramException ex)
        {
            throw new PdfFontEmbedException(PdfFontEmbedFailure.UnsupportedProgram, "The font program cannot be subset: " + ex.Message);
        }
    }

    // Six capital letters from a hash of the program, the glyphs, the widths and the name, so equal inputs give equal names (ISO 32000-2 9.6.4).
    private static string SubsetTag(ReadOnlySpan<byte> source, List<int> glyphs, Dictionary<int, int> advances, string name)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(SHA256.HashData(source));
        sha.AppendData(Encoding.UTF8.GetBytes(name));
        Span<byte> buf = stackalloc byte[4];
        foreach (int g in glyphs)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buf, g);
            sha.AppendData(buf);
            BinaryPrimitives.WriteInt32LittleEndian(buf, advances.TryGetValue(g, out int a) ? a : -1);
            sha.AppendData(buf);
        }

        byte[] h = sha.GetHashAndReset();
        var chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            chars[i] = (char)('A' + (h[i] % 26));
        }

        return new string(chars);
    }

    internal static bool HasFontFile(PdfDictionary descriptor) =>
        descriptor.ContainsKey("FontFile") || descriptor.ContainsKey("FontFile2") || descriptor.ContainsKey("FontFile3");

    internal static string StripTag(string name)
    {
        if (name.Length > 7 && name[6] == '+')
        {
            for (int i = 0; i < 6; i++)
            {
                if (name[i] is not (>= 'A' and <= 'Z'))
                {
                    return name;
                }
            }

            return name[7..];
        }

        return name;
    }

    internal static bool SingleRune(string text, out int codePoint)
    {
        codePoint = 0;
        if (text.Length == 0 || !Rune.TryGetRuneAt(text, 0, out Rune r) || r.Utf16SequenceLength != text.Length)
        {
            return false;
        }

        codePoint = r.Value;
        return true;
    }

    private static int StyleFlags(string standard)
    {
        int f = 0;
        if (standard.StartsWith("Courier", StringComparison.Ordinal))
        {
            f |= 1;
        }

        if (standard.StartsWith("Times", StringComparison.Ordinal))
        {
            f |= 2;
        }

        if (standard.Contains("Italic", StringComparison.Ordinal) || standard.Contains("Oblique", StringComparison.Ordinal))
        {
            f |= 64;
        }

        return f;
    }

    // A copy of the font's descriptor (or a new one) that names the embedded program. Entries the PDF has stay as they are.
    private static PdfDictionary NewDescriptor(PdfDictionary? old, PdfFontFile program, string fontName, int flags, string baseName, PdfReference fileRef)
    {
        var fd = new PdfDictionary();
        if (old is not null)
        {
            foreach (string key in old.Keys.ToArray())
            {
                if (key is not ("FontFile" or "FontFile2" or "FontFile3" or "CharSet") && old.GetRaw(key) is { } value)
                {
                    fd.SetEntry(key, value);
                }
            }
        }

        FillDescriptor(fd, program, fontName, flags, baseName);
        fd.SetEntry("FontFile2", fileRef);
        return fd;
    }

    // Sets the descriptor entries an embedded font needs and leaves the ones the PDF already has (the PDF's metrics stay the PDF's).
    private static void FillDescriptor(PdfDictionary fd, PdfFontFile program, string fontName, int flags, string baseName)
    {
        SfntFont sfnt = program.Sfnt!;
        double scale = 1000.0 / sfnt.UnitsPerEm;
        fd.SetEntry("Type", new PdfName("FontDescriptor"));
        fd.SetEntry("FontName", new PdfName(fontName));
        fd.SetEntry("Flags", new PdfInteger(flags));
        ReadOnlyMemory<byte> hhea = sfnt.GetTable("hhea");
        ReadOnlyMemory<byte> os2 = sfnt.GetTable("OS/2");
        ReadOnlyMemory<byte> post = sfnt.GetTable("post");
        int ascent = hhea.Length >= 8 ? (int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(hhea.Span[4..]) * scale) : 800;
        int descent = hhea.Length >= 8 ? (int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(hhea.Span[6..]) * scale) : -200;
        if (!fd.ContainsKey("FontBBox"))
        {
            var box = new PdfArray();
            if (sfnt.FontBBox is { Length: 4 } bb)
            {
                foreach (double v in bb)
                {
                    box.Append(new PdfInteger((long)Math.Round(v * scale)));
                }
            }
            else
            {
                foreach (int v in (int[])[0, descent, 1000, ascent])
                {
                    box.Append(new PdfInteger(v));
                }
            }

            fd.SetEntry("FontBBox", box);
        }

        if (!fd.ContainsKey("ItalicAngle"))
        {
            double angle = post.Length >= 8 ? BinaryPrimitives.ReadInt32BigEndian(post.Span[4..]) / 65536.0 : 0;
            fd.SetEntry("ItalicAngle", new PdfReal(Math.Round(angle, 2)));
        }

        if (!fd.ContainsKey("Ascent"))
        {
            fd.SetEntry("Ascent", new PdfInteger(ascent));
        }

        if (!fd.ContainsKey("Descent"))
        {
            fd.SetEntry("Descent", new PdfInteger(descent));
        }

        if (!fd.ContainsKey("CapHeight"))
        {
            int cap = os2.Length >= 90 && BinaryPrimitives.ReadUInt16BigEndian(os2.Span) >= 2 ? (int)Math.Round(BinaryPrimitives.ReadInt16BigEndian(os2.Span[88..]) * scale) : (int)(ascent * 0.7);
            fd.SetEntry("CapHeight", new PdfInteger(cap));
        }

        if (!fd.ContainsKey("StemV"))
        {
            fd.SetEntry("StemV", new PdfInteger(baseName.Contains("Bold", StringComparison.OrdinalIgnoreCase) ? 140 : 80));
        }
    }
}
