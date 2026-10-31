using System.Globalization;
using System.Linq;
using System.Text;
using OnePdf.FontData;
using OnePdf.FontPrograms;
using OnePdf.PdfFonts;

namespace OnePdf.PdfA;

// Checks of embedded font programs against the pages that show them: widths and glyphs (ISO 19005-2 6.2.11.4.1, 6.2.11.5, 6.2.11.8).
internal sealed partial class ConversionRepairs
{
    private const double WidthTolerance = 0.95;   // a validator allows a whole unit of 1/1000 em
    private const double MaxWidthFix = 12.0;      // larger differences would visibly move text; the file is refused instead

    private readonly List<(string Clause, string Message)> _findings = [];

    /// <summary>Findings the converter makes beyond the checker's: fonts whose program lacks a shown glyph or disagrees with the file's widths.</summary>
    public IReadOnlyList<(string Clause, string Message)> ExtraFindings => _findings;

    /// <summary>
    /// Makes the embedded programs and the font dictionaries agree where that moves nothing. CFF programs get the PDF's widths for the shown glyphs;
    /// other programs, whose advances OnePDF does not rewrite, get small corrections to <c>/Widths</c> or <c>/W</c> (rounding in the producer).
    /// What cannot be reconciled without moving text, and glyphs a font lacks, become findings that refuse the file.
    /// </summary>
    public void AuditFonts()
    {
        // The default tolerance (0.01): a validator reads a CFF program's fractional widths in its own way, so every shown glyph is made to carry the PDF's width exactly.
        _doc.MatchEmbeddedFontWidths(new PdfWidthMatchOptions(), _ct);

        var fonts = new List<(PdfDictionary Font, PdfDictionary Owner, bool Composite)>();
        var targets = new HashSet<int>();
        var composite = new Dictionary<int, PdfFont>();
        var type3 = new List<PdfDictionary>();
        foreach (PdfDictionary font in FontEmbedding.SubstituteEmbedder.CollectFonts(_doc, _ct))
        {
            string? subtype = font.GetName("Subtype");
            if (subtype == "Type3" && font.Reference is { } t3)
            {
                type3.Add(font);
                targets.Add(t3.Number);
                continue;
            }

            if (subtype is not ("Type1" or "MMType1" or "TrueType" or "Type0"))
            {
                continue;
            }

            bool isComposite = subtype == "Type0";
            PdfDictionary owner = font;
            if (isComposite)
            {
                if (font.Get("DescendantFonts") is not PdfArray { Count: > 0 } a || a[0] is not PdfDictionary dsc)
                {
                    continue;
                }

                owner = dsc;
            }

            if (owner.GetDictionary("FontDescriptor") is not { } desc || !FontEmbedding.FontEmbedder.HasFontFile(desc))
            {
                continue;
            }

            fonts.Add((font, owner, isComposite));
            if (font.Reference is { } r)
            {
                targets.Add(r.Number);
                if (isComposite)
                {
                    composite[r.Number] = _doc.GetFont(font);
                }
            }
        }

        if (fonts.Count == 0 && type3.Count == 0)
        {
            return;
        }

        FontUsage usage = FontUsage.Scan(_doc, targets, _ct, composite);
        foreach ((PdfDictionary font, PdfDictionary owner, bool isComposite) in fonts)
        {
            _ct.ThrowIfCancellationRequested();
            AuditFont(font, owner, isComposite, usage);
        }

        foreach (PdfDictionary font in type3)
        {
            _ct.ThrowIfCancellationRequested();
            AuditType3(font, usage);
        }
    }

    // ISO 19005-2 6.2.11.5: the widths of a Type 3 font's /Widths and of the d0/d1 operator of each shown glyph agree. A difference of a few units is a rounding
    // in the producer and /Widths is corrected; a larger one would move text, so the file is refused.
    private void AuditType3(PdfDictionary font, FontUsage usage)
    {
        int number = font.Reference?.Number ?? -1;
        if (!usage.Complete || number < 0 || !usage.Used.TryGetValue(number, out HashSet<int>? shown)
            || font.Get("Widths") is not PdfArray widths || font.Get("CharProcs") is not PdfDictionary procs
            || font.GetDictionary("Encoding")?.Get("Differences") is not PdfArray diff)
        {
            return;
        }

        string name = font.GetName("Name") ?? "(Type 3)";
        int first = (int)font.GetInt("FirstChar");
        double scale = font.Get("FontMatrix") is PdfArray { Count: 6 } fm ? Math.Abs(fm.GetDouble(0)) * 1000.0 : 1.0;
        var names = new Dictionary<int, string>();
        int code = 0;
        for (int i = 0; i < diff.Count; i++)
        {
            if (diff[i] is PdfInteger n)
            {
                code = (int)n.Value;
            }
            else if (diff.GetName(i) is { } glyph)
            {
                names[code++] = glyph;
            }
        }

        var fixes = new SortedDictionary<int, double>();
        foreach (int c in shown)
        {
            if (c < first || c - first >= widths.Count || !names.TryGetValue(c, out string? glyph) || procs.Get(glyph) is not PdfStream proc)
            {
                continue;
            }

            double? wx = FirstGlyphWidth(proc);
            if (wx is null)
            {
                continue;
            }

            double pdf = widths.GetDouble(c - first);
            if (Math.Abs(pdf - wx.Value) * scale <= 0.01)
            {
                continue;
            }

            if (Math.Abs(pdf - wx.Value) * scale > MaxWidthFix)
            {
                _findings.Add(("6.2.11.5", $"The Type 3 font {name} has /Widths that differ from the d0/d1 width of its glyph for code {c} ({pdf:0.##} against {wx.Value:0.##}); correcting them would move text."));
                return;
            }

            fixes[c] = wx.Value;
        }

        if (fixes.Count > 0 && PatchSimpleWidths(font, fixes))
        {
            _log.Add(PdfAChangeKind.FontsRepaired, "Type 3 font widths that disagreed with their glyph procedures by rounding were corrected.");
            _doc.ForgetFont(font);
        }
    }

    // The width operand of the d0 or d1 operator that opens a glyph procedure.
    private double? FirstGlyphWidth(PdfStream proc)
    {
        ReadOnlyMemory<byte> data;
        try
        {
            data = proc.GetDecodedBytes(_ct);
        }
        catch (PdfException)
        {
            return null;
        }

        var lx = new OnePdf.Content.ContentLexer(data, 64);
        double? first = null;
        for (int guard = 0; guard < 16; guard++)
        {
            lx.ResetItems();
            OnePdf.Content.ContentToken t = lx.Next(out OnePdf.Content.Operand operand, out OnePdf.Content.ContentOp op);
            if (t == OnePdf.Content.ContentToken.End)
            {
                return null;
            }

            if (t == OnePdf.Content.ContentToken.Operand)
            {
                if (operand.Kind == OnePdf.Content.OperandKind.Number && first is null)
                {
                    first = operand.Num;
                }

                continue;
            }

            return op is OnePdf.Content.ContentOp.CharProcWidth or OnePdf.Content.ContentOp.CharProcBox ? first : null;
        }

        return null;
    }

    private void AuditFont(PdfDictionary font, PdfDictionary owner, bool isComposite, FontUsage usage)
    {
        int number = font.Reference?.Number ?? -1;
        string name = font.GetName("BaseFont") ?? "(unnamed)";
        bool tracked = usage.Complete && number >= 0;
        if (!tracked)
        {
            return; // without the list of shown codes there is nothing to compare, and a validator only looks at shown glyphs
        }

        var codes = new SortedDictionary<uint, int>();
        if (isComposite)
        {
            if (!usage.UsedComposite.TryGetValue(number, out Dictionary<uint, (int Cid, int Length)>? seen))
            {
                return;
            }

            foreach ((uint code, (int _, int length) v) in seen)
            {
                codes[code] = v.length;
            }
        }
        else if (usage.Used.TryGetValue(number, out HashSet<int>? shown))
        {
            foreach (int c in shown.Where(static c => c is >= 0 and <= 255))
            {
                codes[(uint)c] = 1;
            }
        }
        else
        {
            return;
        }

        PdfDictionary desc = owner.GetDictionary("FontDescriptor")!;
        PdfFont pf = _doc.GetFont(font);
        if (pf.EmbeddedFailed || pf.Program is not { } program)
        {
            _findings.Add(("6.2.11.4.1", $"The embedded font program of {name} cannot be read."));
            return;
        }

        // ISO 19005-2 6.2.11.6: the program of a symbolic TrueType font has one cmap subtable, or a (3,0) one; a validator reads no other.
        if (!isComposite && font.GetName("Subtype") == "TrueType" && (desc.GetInt("Flags") & 4) != 0 && !font.ContainsKey("Encoding")
            && program is SfntFont symbolicProgram && symbolicProgram.CmapSubtables.Count != 1 && symbolicProgram.FindCmap(3, 0) is null)
        {
            _findings.Add(("6.2.11.6", $"The symbolic TrueType font {name} has {symbolicProgram.CmapSubtables.Count} cmap subtables and none for Microsoft Symbol (3,0)."));
            return;
        }

        // Glyph space to 1/1000 em: the unit grid of a TrueType font, the font matrix of the others (not rounded: a matrix of 0.0018 is not 1/556).
        double perUnit = program is SfntFont sfnt
            ? 1000.0 / sfnt.UnitsPerEm
            : Math.Abs(program.FontMatrix[0] == 0 ? 0.001 : program.FontMatrix[0]) * 1000.0;
        var missing = new List<uint>();
        var fixes = new SortedDictionary<int, double>();   // code or cid -> program width
        var unfixable = new List<(int Code, double Pdf, double Program)>();
        var one = new List<FontGlyph>();
        int badUnicode = 0;
        foreach ((uint code, int length) in codes)
        {
            one.Clear();
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)(code >> (8 * (length - 1 - i)));
            }

            pf.Decode(bytes, one);
            if (one.Count != 1)
            {
                continue;
            }

            FontGlyph g = one[0];
            if (_options.Conformance == PdfAConformance.U && g.Unicode.AsSpan().IndexOfAny('\u0000', '\uFEFF', '\uFFFE') >= 0)
            {
                badUnicode++;
            }

                // Validators read code 0 of a simple TrueType font as a reference to .notdef whatever the cmap says (ISO 19005-2 6.2.11.8).
            bool zeroOfTrueType = !isComposite && code == 0 && font.GetName("Subtype") == "TrueType";
            if (zeroOfTrueType || g.Face != 0 || g.Gid < 0 || !g.Exists || (!isComposite && font.GetName("Subtype") == "TrueType" && !TrueTypeHasGlyph(program, desc, font, (int)code, g)))
            {
                missing.Add(code);
                continue;
            }

            if (!program.TryGetAdvance(g.Gid, out float adv))
            {
                continue;
            }

            double programWidth = adv * perUnit;
            double pdfWidth = g.Advance * 1000.0;
            if (Math.Abs(pdfWidth - programWidth) > WidthTolerance)
            {
                int key = isComposite ? g.Cid : unchecked((int)code);
                if (Math.Abs(pdfWidth - programWidth) <= MaxWidthFix)
                {
                    fixes[key] = programWidth;
                }
                else
                {
                    unfixable.Add((key, pdfWidth, programWidth));
                }
            }
        }

        if (_options.Conformance == PdfAConformance.U && font.Get("ToUnicode") is PdfStream toUnicode)
        {
            badUnicode += CountInvalidToUnicode(toUnicode, codes.Keys);
        }

        if (badUnicode > 0)
        {
            _findings.Add(("6.2.11.7.2", $"The font {name} maps {badUnicode} shown code(s) to U+0000, U+FEFF or U+FFFE, which are not valid text (level U)."));
        }

        if (missing.Count > 0)
        {
            _findings.Add(("6.2.11.4.1", $"The embedded font {name} has no glyph for {missing.Count} shown code(s) (first: {missing[0]}); they would draw .notdef."));
        }

        if (unfixable.Count > 0)
        {
            (int c0, double p0, double q0) = unfixable[0];
            _findings.Add(("6.2.11.5", $"The widths of the font {name} differ from its embedded program for {unfixable.Count} shown code(s) by more than {MaxWidthFix:0} units (code {c0}: {p0:0.##} in the file, {q0:0.##} in the program); correcting them would move text."));
        }

        if (fixes.Count > 0 && unfixable.Count == 0)
        {
            if (isComposite ? PatchCidWidths(owner, fixes) : PatchSimpleWidths(font, fixes))
            {
                _log.Add(PdfAChangeKind.FontsRepaired, "Font widths that disagreed with the embedded program by rounding were corrected.", 1);
                _doc.ForgetFont(font);
            }
            else
            {
                _findings.Add(("6.2.11.5", $"The widths of the font {name} disagree with its embedded program and could not be corrected."));
            }
        }
    }

    /// <summary>
    /// Level U: every shown code needs a Unicode value (ISO 19005-2 6.2.11.7.2). Values OnePDF can derive are written into <c>/ToUnicode</c>; a code whose glyph
    /// carries no character (a subset's <c>gid00132</c>) is mapped to a private-use code point, which the clause allows, and the result says so.
    /// </summary>
    public void AddUnicodeMaps()
    {
        TrimToUnicodeTails();
        var options = new PdfToUnicodeOptions
        {
            CodeToText = code => char.ConvertFromUtf32(0xE000 + (int)((uint)code.Code % 0x1800u)),
        };
        PdfToUnicodeReport report = _doc.AddMissingToUnicode(options, _ct);
        if (report.CodesAdded > 0)
        {
            _log.Add(PdfAChangeKind.FontsRepaired, "ToUnicode maps were written for fonts that lacked them.", report.FontsChanged);
        }

        int privateUse = report.Entries.Sum(static e => e.CodesFromCaller);
        if (privateUse > 0)
        {
            _log.Warn($"{privateUse} character code(s) whose glyph names no character were mapped to private-use code points in /ToUnicode.");
        }
    }

    /// <summary>
    /// A packet in the file is never trusted to identify the result: it may claim PDF/A and still use properties outside the predefined schemas without
    /// declaring them (ISO 19005-2 6.6.2.3). The packet is replaced by one built from the Info dictionary; what only the old packet said (title, author,
    /// subject, keywords, creator tool, producer) is copied into Info first, so it is not lost.
    /// </summary>
    public void RebuildMetadata()
    {
        PdfStream? current = _doc.Catalog.GetStream("Metadata");
        if (current is null || current.FindUnreadableReason(_ct) is not null)
        {
            return;
        }

        XmpMetadata? xmp = _doc.Xmp;
        if (xmp is { IsWellFormed: true })
        {
            void fill(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(_doc.Info.Dictionary.GetString(key)?.GetText()))
                {
                    _doc.SetInfo(key, value);
                }
            }

            fill("Title", xmp.Title);
            fill("Author", xmp.Creators.Count > 0 ? string.Join("; ", xmp.Creators) : null);
            fill("Subject", xmp.Description);
            fill("Keywords", xmp.Keywords ?? (xmp.Subjects.Count > 0 ? string.Join(", ", xmp.Subjects) : null));
            fill("Creator", xmp.CreatorTool);
            fill("Producer", xmp.Producer);
        }

        _doc.Catalog.RemoveEntry("Metadata");
        _log.Add(PdfAChangeKind.MetadataWritten, "The XMP packet was written again from the Info dictionary.");
    }

    // The shown codes that a ToUnicode CMap maps to U+0000, U+FEFF or U+FFFE (ISO 19005-2 6.2.11.7.2). The CMap text is read for its bfchar and bfrange
    // entries; the extraction chain treats U+0000 as "no mapping" and falls back to the glyph name, so it cannot be asked.
    private int CountInvalidToUnicode(PdfStream map, IEnumerable<uint> shownCodes)
    {
        ReadOnlyMemory<byte> data;
        try
        {
            data = map.GetDecodedBytes(_ct);
        }
        catch (PdfException)
        {
            return 0;
        }

        var shown = new HashSet<uint>(shownCodes);
        string text = Encoding.Latin1.GetString(data.Span);
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        static bool Hex(string t, out uint value, out int digits)
        {
            value = 0;
            digits = 0;
            if (t.Length < 3 || t[0] != '<' || t[^1] != '>')
            {
                return false;
            }

            string h = t[1..^1];
            digits = h.Length;
            return h.Length is > 0 and <= 8 && uint.TryParse(h, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        static bool Invalid(long v) => v is 0 or 0xFEFF or 0xFFFE;
        int bad = 0;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "beginbfchar")
            {
                for (i++; i + 1 < tokens.Length && tokens[i] != "endbfchar"; i += 2)
                {
                    if (Hex(tokens[i], out uint src, out _) && Hex(tokens[i + 1], out uint dst, out int dd) && dd <= 4 && shown.Contains(src) && Invalid(dst))
                    {
                        bad++;
                    }
                }
            }
            else if (tokens[i] == "beginbfrange")
            {
                for (i++; i + 2 < tokens.Length && tokens[i] != "endbfrange";)
                {
                    if (!Hex(tokens[i], out uint lo, out _) || !Hex(tokens[i + 1], out uint hi, out _))
                    {
                        break;
                    }

                    if (tokens[i + 2].StartsWith('['))
                    {
                        int k = i + 2;
                        uint code = lo;
                        for (; k < tokens.Length; k++)
                        {
                            string t = tokens[k].TrimStart('[');
                            bool last = t.EndsWith(']');
                            t = t.TrimEnd(']');
                            if (Hex(t, out uint d, out int dd) && dd <= 4 && shown.Contains(code) && Invalid(d))
                            {
                                bad++;
                            }

                            code++;
                            if (last)
                            {
                                break;
                            }
                        }

                        i = k + 1;
                    }
                    else
                    {
                        if (Hex(tokens[i + 2], out uint d, out int dd) && dd <= 4)
                        {
                            foreach (uint c in shown)
                            {
                                if (c >= lo && c <= hi && Invalid((long)d + (c - lo)))
                                {
                                    bad++;
                                }
                            }
                        }

                        i += 3;
                    }
                }
            }
        }

        return bad;
    }

    // Some producers pad a ToUnicode stream with bytes after the closing "end end" of the CMap program; a validator's CMap reader then rejects the whole map.
    private void TrimToUnicodeTails()
    {
        foreach (PdfDictionary font in FontEmbedding.SubstituteEmbedder.CollectFonts(_doc, _ct))
        {
            if (font.Get("ToUnicode") is not PdfStream map)
            {
                continue;
            }

            ReadOnlyMemory<byte> data;
            try
            {
                data = map.GetDecodedBytes(_ct);
            }
            catch (PdfException)
            {
                continue;
            }

            string text = Encoding.Latin1.GetString(data.Span);
            int endCmap = text.LastIndexOf("endcmap", StringComparison.Ordinal);
            if (endCmap < 0)
            {
                continue;
            }

            int pop = text.IndexOf("pop", endCmap, StringComparison.Ordinal);
            int end1 = pop < 0 ? -1 : text.IndexOf("end", pop, StringComparison.Ordinal);
            int end2 = end1 < 0 ? -1 : text.IndexOf("end", end1 + 3, StringComparison.Ordinal);
            if (end2 < 0)
            {
                continue;
            }

            int cut = end2 + 3;
            if (text.AsSpan(cut).IsWhiteSpace())
            {
                continue;
            }

            map.SetData(data.Span[..cut], compress: true);
            _log.Add(PdfAChangeKind.FontsRepaired, "Bytes after the end of a ToUnicode map were removed.");
        }
    }

    // ISO 32000-1 9.6.6.4: how a reader finds the glyph of a code in a simple TrueType font. A validator follows these rules and takes a failed
    // lookup for .notdef, even where OnePDF's own decoding finds a glyph some other way.
    private static bool TrueTypeHasGlyph(FontProgram program, PdfDictionary desc, PdfDictionary font, int code, FontGlyph g)
    {
        if (program is not SfntFont sfnt)
        {
            return true;
        }

        bool symbolic = (desc.GetInt("Flags") & 4) != 0 && !font.ContainsKey("Encoding");
        if (!symbolic)
        {
            CmapSubtable? unicode = sfnt.FindCmap(3, 1);
            if (unicode is null || g.Name is not { Length: > 0 } name || !GlyphList.TryGetUnicode(name, out string? text) || text.Length == 0)
            {
                return true;
            }

            return !Rune.TryGetRuneAt(text, 0, out Rune r) || unicode.Lookup(r.Value) > 0;
        }

        CmapSubtable? sym = sfnt.FindCmap(3, 0);
        if (sym is not null)
        {
            if (sym.Lookup(code) > 0 || sym.Lookup(0xF000 + code) > 0 || sym.Lookup(0xF100 + code) > 0 || sym.Lookup(0xF200 + code) > 0)
            {
                return true;
            }
        }

        CmapSubtable? mac = sfnt.FindCmap(1, 0);
        if (mac is not null)
        {
            return mac.Lookup(code) > 0;
        }

        return sym is null;
    }

    private static PdfObject Number(double v)
    {
        double r = Math.Round(v, 3);
        return r == Math.Floor(r) ? new PdfInteger((long)r) : new PdfReal(r);
    }

    private static bool PatchSimpleWidths(PdfDictionary font, SortedDictionary<int, double> fixes)
    {
        if (font.Get("Widths") is not PdfArray widths || !font.ContainsKey("FirstChar"))
        {
            return false;
        }

        int first = (int)font.GetInt("FirstChar");
        int last = first + widths.Count - 1;
        int lo = Math.Min(first, fixes.Keys.Min()), hi = Math.Max(last, fixes.Keys.Max());
        if (lo < 0 || hi > 255)
        {
            return false;
        }

        double missingWidth = font.GetDictionary("FontDescriptor")?.GetDouble("MissingWidth") ?? 0;
        var fresh = new PdfArray();
        for (int c = lo; c <= hi; c++)
        {
            if (fixes.TryGetValue(c, out double w))
            {
                fresh.Append(Number(w));
            }
            else if (c >= first && c <= last)
            {
                fresh.Append(widths.GetRaw(c - first));
            }
            else
            {
                fresh.Append(Number(missingWidth));
            }
        }

        font.SetEntry("Widths", fresh);
        font.SetEntry("FirstChar", new PdfInteger(lo));
        font.SetEntry("LastChar", new PdfInteger(hi));
        return true;
    }

    // /W is a list of "c [w1 w2 ...]" and "cfirst clast w" entries (ISO 32000-1 9.7.4.3). It is read into a map, patched and written back as "c [w ...]" runs.
    private static bool PatchCidWidths(PdfDictionary cidFont, SortedDictionary<int, double> fixes)
    {
        var map = new SortedDictionary<int, double>();
        if (cidFont.Get("W") is PdfArray w)
        {
            int i = 0;
            while (i < w.Count)
            {
                if (w[i] is not (PdfInteger or PdfReal))
                {
                    return false;
                }

                int c = (int)w.GetInt(i);
                if (i + 1 < w.Count && w[i + 1] is PdfArray list)
                {
                    for (int k = 0; k < list.Count; k++)
                    {
                        map[c + k] = list.GetDouble(k);
                    }

                    i += 2;
                }
                else if (i + 2 < w.Count)
                {
                    int c2 = (int)w.GetInt(i + 1);
                    if (c2 < c || c2 - c > 65535)
                    {
                        return false;
                    }

                    double width = w.GetDouble(i + 2);
                    for (int k = c; k <= c2; k++)
                    {
                        map[k] = width;
                    }

                    i += 3;
                }
                else
                {
                    return false;
                }
            }
        }

        foreach ((int cid, double width) in fixes)
        {
            map[cid] = width;
        }

        var fresh = new PdfArray();
        PdfArray? run = null;
        int prev = -2;
        foreach ((int cid, double width) in map)
        {
            if (run is null || cid != prev + 1)
            {
                run = new PdfArray();
                fresh.Append(new PdfInteger(cid));
                fresh.Append(run);
            }

            run.Append(Number(width));
            prev = cid;
        }

        cidFont.SetEntry("W", fresh);
        return true;
    }
}
