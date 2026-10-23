using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnePdf;
using OnePdf.FontPrograms;

namespace OnePdf.Fuzz;

/// <summary>
/// Font writing target: the first byte holds option bits (subset, ignore embedding flags, standard font too), the next eight pick
/// characters from the font's own character map, and the rest is a TrueType or OpenType font. The font is added to a document,
/// text is drawn with it, the document is saved, and the subset that was written is parsed again. Only the library's typed
/// exceptions may escape; the subset must stay a parseable font whenever saving succeeds.
/// </summary>
internal static class FontSubsetTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
        {
            return;
        }

        byte flags = data[0];
        byte[] picks = data.Slice(1, 8).ToArray();
        byte[] font = data[9..].ToArray();
        var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 16 << 20 };
        try
        {
            using PdfDocument doc = PdfDocument.CreateEmpty(new PdfOpenOptions { Limits = limits });
            PdfPage page = doc.InsertBlankPage(0, 400, 200);
            PdfCreatedFont f = doc.AddFont(font, new PdfEmbedFontOptions
            {
                Subset = (flags & 1) == 0,
                IgnoreEmbeddingRestrictions = (flags & 2) != 0,
                FaceIndex = (flags >> 4) & 3,
            });
            string text = TextFor(font, picks, flags);
            using (PdfPageContentBuilder b = page.BeginContent())
            {
                b.SetFont(f, 12);
                b.SetWordSpacing(1.5);
                b.DrawText(text, 10, 100);
                b.DrawTextBox(text + " " + text, 10, 20, 150, 60, PdfTextAlignment.Justify);
                b.Commit();
            }

            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], CompressStreams = (flags & 4) != 0 });
            byte[] pdf = ms.ToArray();

            using PdfDocument back = PdfDocument.Open(pdf, new PdfOpenOptions { Limits = limits });
            PdfDictionary? fonts = back.Pages[0].Resources.GetDictionary("Font");
            if (fonts is null)
            {
                throw new InvalidOperationException("A saved font page lost its font resources.");
            }

            foreach (string key in fonts.Keys)
            {
                if (fonts.GetDictionary(key)?.Get("DescendantFonts") is PdfArray { Count: 1 } d && d[0] is PdfDictionary cid && cid.GetDictionary("FontDescriptor") is { } desc)
                {
                    byte[] program = (desc.GetStream("FontFile2") ?? desc.GetStream("FontFile3"))?.GetDecodedBytes().ToArray() ?? throw new InvalidOperationException("The saved font has no font file.");
                    bool isSubset = cid.GetName("BaseFont") is { Length: > 7 } baseName && baseName[6] == '+';
                    try
                    {
                        // What we wrote must read back; a font embedded whole is the input's own bytes and may be damaged.
                        FontProgram parsed = FontProgramLoader.Open(program);
                        for (int g = 0; g < Math.Min(parsed.GlyphCount, 64); g++)
                        {
                            try
                            {
                                parsed.GetOutline(g);
                            }
                            catch (FontProgramException)
                            {
                                // A glyph the input font already had broken (bad point matching) is copied as it was.
                            }
                        }
                    }
                    catch (FontProgramException) when (!isSubset)
                    {
                    }
                }
            }

            _ = back.ExtractText();
        }
        catch (Exception ex) when (ex is PdfException or ArgumentException)
        {
            // Typed errors for hostile fonts are the contract. (ArgumentException: an unusable collection face.)
        }
    }

    private static string TextFor(byte[] font, byte[] picks, byte flags)
    {
        var sb = new StringBuilder("Aa Zz ");
        try
        {
            SfntFont sfnt = SfntFont.Parse(font, (flags >> 4) & 3, GlyphPrograms.Never);
            CmapSubtable? cmap = sfnt.FindUnicodeCmap();
            if (cmap is not null)
            {
                List<(int Code, int Glyph)> all = cmap.Enumerate();
                if (all.Count > 0)
                {
                    for (int i = 0; i < picks.Length; i++)
                    {
                        int index = (picks[i] * 257 + (picks[(i + 1) % picks.Length] * 31)) % all.Count;
                        int cp = all[index].Code;
                        if (cp is >= 0x20 and < 0xD800 or >= 0xE000 and <= 0x10FFFF)
                        {
                            sb.Append(char.ConvertFromUtf32(cp));
                        }
                    }
                }
            }
        }
        catch (FontProgramException)
        {
        }

        return sb.ToString();
    }
}
