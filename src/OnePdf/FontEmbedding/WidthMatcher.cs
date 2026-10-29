using System.Linq;
using OnePdf.FontPrograms;
using OnePdf.FontWriting;
using OnePdf.PdfA;
using OnePdf.PdfFonts;

namespace OnePdf.FontEmbedding;

/// <summary>
/// Brings the advance widths of embedded CFF programs to the PDF's <c>/Widths</c> or <c>/W</c> (ISO 14289-1 7.21.5 asks for them to agree)
/// by rewriting the width operand of the charstrings of the glyphs the document shows. Text positions come from the PDF's widths and
/// stay as they are; outlines are not touched.
/// </summary>
internal static class WidthMatcher
{
    private const int VerifyGlyphLimit = 4096;

    public static PdfWidthMatchReport Run(PdfDocument doc, PdfWidthMatchOptions options, CancellationToken ct)
    {
        var fonts = new List<(PdfDictionary Font, PdfDictionary Descriptor, PdfDictionary Owner, bool Composite)>();
        var targets = new HashSet<int>();
        var composite = new Dictionary<int, PdfFont>();
        foreach (PdfDictionary font in SubstituteEmbedder.CollectFonts(doc, ct))
        {
            string? subtype = font.GetName("Subtype");
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

            if (owner.GetDictionary("FontDescriptor") is not { } desc || !FontEmbedder.HasFontFile(desc))
            {
                continue;
            }

            fonts.Add((font, desc, owner, isComposite));
            if (font.Reference is { } r)
            {
                targets.Add(r.Number);
                if (isComposite)
                {
                    composite[r.Number] = doc.GetFont(font);
                }
            }
        }

        FontUsage? usage = targets.Count > 0 ? FontUsage.Scan(doc, targets, ct, composite) : null;
        var entries = new List<PdfWidthMatchEntry>();
        foreach ((PdfDictionary font, PdfDictionary desc, PdfDictionary owner, bool isComposite) in fonts)
        {
            ct.ThrowIfCancellationRequested();
            entries.Add(Process(doc, font, desc, owner, isComposite, usage, options, ct));
        }

        return new PdfWidthMatchReport(entries);
    }

    private static PdfWidthMatchEntry Process(PdfDocument doc, PdfDictionary font, PdfDictionary desc, PdfDictionary owner, bool isComposite,
        FontUsage? usage, PdfWidthMatchOptions options, CancellationToken ct)
    {
        int number = font.Reference?.Number ?? -1;
        string baseFont = font.GetName("BaseFont") ?? string.Empty;
        PdfWidthMatchEntry Refuse(string note) => new(number, baseFont, PdfWidthMatchOutcome.Refused, note);

        string fileKey = desc.ContainsKey("FontFile3") ? "FontFile3" : desc.ContainsKey("FontFile2") ? "FontFile2" : "FontFile";
        if (desc.Get(fileKey) is not PdfStream fileStream)
        {
            return Refuse("The font program is not a readable stream.");
        }

        PdfFontFile program;
        try
        {
            program = PdfFontFile.Open(fileStream.GetDecodedBytes(ct));
        }
        catch (PdfFormatException ex)
        {
            return Refuse("The font program cannot be read: " + ex.Message);
        }

        // The codes the document shows. A font whose use cannot be traced has no list of codes to compare.
        bool tracked = usage is { Complete: true } && number >= 0 && !usage.Full.Contains(number);
        var codes = new SortedDictionary<uint, int>();
        if (isComposite)
        {
            if (!tracked || !usage!.UsedComposite.TryGetValue(number, out Dictionary<uint, (int Cid, int Length)>? seen))
            {
                return tracked ? new PdfWidthMatchEntry(number, baseFont, PdfWidthMatchOutcome.AlreadyMatching, "The font is never shown.")
                    : Refuse("Which characters the document shows with this font cannot be traced.");
            }

            foreach ((uint code, (int _, int length) v) in seen)
            {
                codes[code] = v.length;
            }
        }
        else if (tracked)
        {
            if (usage!.Used.TryGetValue(number, out HashSet<int>? seenSimple))
            {
                foreach (int c in seenSimple.Where(static c => c is >= 0 and <= 255))
                {
                    codes[(uint)c] = 1;
                }
            }
            else
            {
                return new PdfWidthMatchEntry(number, baseFont, PdfWidthMatchOutcome.AlreadyMatching, "The font is never shown.");
            }
        }
        else
        {
            for (uint c = 0; c < 256; c++)
            {
                codes[c] = 1;
            }
        }

        PdfFont pf = doc.GetFont(font);
        var changes = new List<PdfFontWidthChange>();
        var conflicts = new List<int>();
        var target = new Dictionary<int, double>(); // glyph -> PDF width, 1/1000 em
        var one = new List<FontGlyph>();
        foreach ((uint code, int length) in codes)
        {
            one.Clear();
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++)
            {
                bytes[i] = (byte)(code >> (8 * (length - 1 - i)));
            }

            pf.Decode(bytes, one);
            if (one.Count != 1 || one[0] is not { Gid: > 0, Face: 0 } g || (!tracked && !g.Exists))
            {
                continue;
            }

            double pdf = g.Advance * 1000.0;
            program.TryGetAdvanceWidth(g.Gid, out double prog);
            int reported = isComposite ? g.Cid : unchecked((int)code);
            if (Math.Abs(pdf - prog) > options.Tolerance)
            {
                changes.Add(new PdfFontWidthChange(reported, pdf, prog));
            }

            if (target.TryGetValue(g.Gid, out double have))
            {
                if (Math.Abs(have - pdf) > 0.5)
                {
                    conflicts.Add(reported);
                }
            }
            else
            {
                target[g.Gid] = pdf;
            }
        }

        // Only CFF programs are rewritten, and a validator's tolerance is a whole unit of 1/1000 em: for other programs a difference of
        // rounding size (hmtx advances are whole units of a 2048 em) is not worth a mention.
        if (program.Kind != PdfFontFileKind.Cff)
        {
            changes.RemoveAll(static c => Math.Abs(c.PdfWidth - c.ProgramWidth) <= 1.0);
        }

        if (changes.Count == 0)
        {
            return new PdfWidthMatchEntry(number, baseFont, PdfWidthMatchOutcome.AlreadyMatching, "Every shown glyph has the PDF's width.") { WidthConflicts = conflicts };
        }

        if (program.Kind != PdfFontFileKind.Cff)
        {
            return new PdfWidthMatchEntry(number, baseFont, PdfWidthMatchOutcome.UnsupportedProgram,
                $"{changes.Count} width(s) differ, but only CFF programs (FontFile3 Type1C or CIDFontType0C) are rewritten here.")
            { WidthChanges = changes, WidthConflicts = conflicts };
        }

        byte[] rewritten;
        try
        {
            double unitsPerMille = program.UnitsPerEm / 1000.0;
            var units = new Dictionary<int, double>();
            foreach ((int gid, double pdf) in target)
            {
                units[gid] = pdf * unitsPerMille;
            }

            rewritten = CffWidthRewriter.Rewrite(program.Data, units);
            Verify(program.Program, new CffFont(rewritten), units, program.UnitsPerEm);
        }
        catch (FontProgramException ex)
        {
            return Refuse("The CFF program cannot be rewritten safely: " + ex.Message);
        }

        // Writes begin here. The descriptor is replaced by a copy, because other fonts of the file may share the old one.
        PdfStream file = doc.CreateStream(rewritten, compress: true);
        foreach (KeyValuePair<string, PdfObject> e in fileStream.Dictionary.Entries)
        {
            if (e.Key is "Subtype" or "Type")
            {
                file.Dictionary.SetEntry(e.Key, e.Value);
            }
        }

        var fd = new PdfDictionary();
        foreach (KeyValuePair<string, PdfObject> e in desc.Entries)
        {
            fd.SetEntry(e.Key, e.Key == fileKey ? doc.AddObject(file) : e.Value);
        }

        owner.SetEntry("FontDescriptor", doc.AddObject(fd));
        doc.ForgetFont(font);
        return new PdfWidthMatchEntry(number, baseFont, PdfWidthMatchOutcome.Matched,
            $"Set the advance of {target.Count} glyph(s) to the PDF's widths; {changes.Count} code(s) differed.")
        { WidthChanges = changes, WidthConflicts = conflicts };
    }

    // The new program has the same glyphs, names and outlines, the asked-for widths, and every other width as before.
    private static void Verify(FontProgram old, CffFont fresh, Dictionary<int, double> wanted, int unitsPerEm)
    {
        if (old.GlyphCount != fresh.GlyphCount)
        {
            throw new FontProgramException("The glyph count changed.");
        }

        double scale = unitsPerEm / 1000.0;
        int n = Math.Min(old.GlyphCount, VerifyGlyphLimit);
        foreach (int g in wanted.Keys.Concat(Enumerable.Range(0, n)).Distinct())
        {
            GlyphOutline a = old.GetOutline(g), b = fresh.GetOutline(g);
            if (!a.Verbs.AsSpan().SequenceEqual(b.Verbs) || !a.Coords.AsSpan().SequenceEqual(b.Coords) || old.GetGlyphName(g) != fresh.GetGlyphName(g))
            {
                throw new FontProgramException($"Glyph {g} changed.");
            }

            bool hadOld = old.TryGetAdvance(g, out float wo);
            bool hasNew = fresh.TryGetAdvance(g, out float wn);
            double expect = wanted.TryGetValue(g, out double w) ? w : wo;
            if (hasNew != (hadOld || wanted.ContainsKey(g)) || (hasNew && Math.Abs(wn - expect) > 0.01 * scale))
            {
                throw new FontProgramException($"Glyph {g} has the wrong width.");
            }
        }
    }
}
