using OnePdf.FontData;
using OnePdf.PdfA;
using OnePdf.PdfFonts;
using OnePdf.PdfUA;

namespace OnePdf.FontEmbedding;

/// <summary>Writes <c>/ToUnicode</c> entries for the codes a document shows whose Unicode value the font chain already knows.</summary>
internal static class ToUnicodeAdder
{
    public static PdfToUnicodeReport Run(PdfDocument doc, CancellationToken ct)
    {
        var fonts = new List<PdfDictionary>();
        var targets = new HashSet<int>();
        var composite = new Dictionary<int, PdfFont>();
        foreach (PdfDictionary font in SubstituteEmbedder.CollectFonts(doc, ct))
        {
            string? subtype = font.GetName("Subtype");
            if (subtype is not ("Type1" or "MMType1" or "TrueType" or "Type0"))
            {
                continue;
            }

            // The same fonts ISO 14289-1 7.21.7 asks a map for, plus fonts that have a map to complete.
            if (font.Get("ToUnicode") is not PdfStream && PdfUAChecker.EncodingDeterminesUnicode(font, subtype))
            {
                continue;
            }

            fonts.Add(font);
            if (font.Reference is { } r)
            {
                targets.Add(r.Number);
                if (subtype == "Type0")
                {
                    composite[r.Number] = doc.GetFont(font);
                }
            }
        }

        FontUsage? usage = targets.Count > 0 ? FontUsage.Scan(doc, targets, ct, composite) : null;
        var entries = new List<PdfToUnicodeEntry>();
        var one = new List<FontGlyph>();
        foreach (PdfDictionary font in fonts)
        {
            ct.ThrowIfCancellationRequested();
            int number = font.Reference?.Number ?? -1;
            string baseFont = font.GetName("BaseFont") ?? string.Empty;
            bool isComposite = font.GetName("Subtype") == "Type0";
            PdfFont pf = doc.GetFont(font);

            CMap? existing = null;
            if (font.Get("ToUnicode") is PdfStream tus)
            {
                try
                {
                    existing = CMapParser.Parse(tus.GetDecodedBytes(ct).Span, static _ => null);
                }
                catch (PdfFormatException)
                {
                    existing = null;
                }
            }

            // The codes to look at, with their byte length. Usage unknown (a direct font, content that could not be read, a form's default font)
            // means every code of a simple font; a composite font has no code list to try, so it is left alone.
            var codes = new SortedDictionary<uint, int>();
            bool tracked = usage is { Complete: true } && number >= 0 && !usage.Full.Contains(number);
            if (isComposite)
            {
                if (tracked && usage!.UsedComposite.TryGetValue(number, out Dictionary<uint, (int Cid, int Length)>? seen))
                {
                    foreach ((uint code, (int _, int length) v) in seen)
                    {
                        codes[code] = v.length;
                    }
                }
            }
            else if (tracked && usage!.Used.TryGetValue(number, out HashSet<int>? seenSimple))
            {
                foreach (int c in seenSimple)
                {
                    if (c is >= 0 and <= 255)
                    {
                        codes[(uint)c] = 1;
                    }
                }
            }
            else if (!tracked)
            {
                for (uint c = 0; c < 256; c++)
                {
                    codes[c] = 1;
                }
            }

            var add = new Dictionary<int, (int, string)>();
            var unknown = new List<int>();
            foreach ((uint code, int length) in codes)
            {
                if (existing is not null && existing.TryGetUnicode(code, length, out string have) && have.Length > 0)
                {
                    continue;
                }

                one.Clear();
                byte[] bytes = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    bytes[i] = (byte)(code >> (8 * (length - 1 - i)));
                }

                pf.Decode(bytes, one);
                FontGlyph? g = one.Count == 1 ? one[0] : null;
                if (g is { HasUnicode: true } && g.Unicode.Length > 0 && g.Unicode != "\uFFFD")
                {
                    add[unchecked((int)code)] = (length, g.Unicode);
                }
                else if (tracked)
                {
                    unknown.Add(unchecked((int)code));
                }
            }

            bool written = add.Count > 0 && ToUnicodeWriter.Merge(doc, font, add, overrideExisting: false);
            if (written)
            {
                doc.ForgetFont(font);
            }

            if (written || unknown.Count > 0)
            {
                entries.Add(new PdfToUnicodeEntry(number, baseFont, written ? add.Count : 0, unknown));
            }
        }

        return new PdfToUnicodeReport(entries);
    }
}
