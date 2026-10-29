using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Font embedding target: the first byte picks the kind of font dictionary the program is embedded into (simple non-symbolic, symbolic,
/// Type0 with a CIDFontType2 descendant, Type1 with Differences) and option bits; the rest is a font file. The file is opened with
/// <see cref="PdfFontFile"/>, queried over a range of characters and glyphs, and embedded into a document made in memory; the result is
/// saved and reopened. Only the library's typed exceptions may escape. With bit 7 of the first byte set the rest is taken as a CFF program
/// that a document already embeds (a Type1C simple font, or with bit 1 a CIDFontType0C descendant): its widths are matched to the PDF's, text
/// for the codes that have none comes from a callback, and the result is saved and reopened.
/// </summary>
internal static class FontEmbedTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
        {
            return;
        }

        byte mode = data[0];
        byte[] body = data[1..].ToArray();
        if ((mode & 128) != 0)
        {
            RunCffWidths(mode, body);
            return;
        }

        PdfFontFile file;
        try
        {
            file = PdfFontFile.Open(body, 0);
        }
        catch (PdfFormatException)
        {
            return;
        }

        int glyphs = Math.Min(file.GlyphCount, 64);
        for (int g = 0; g < glyphs; g++)
        {
            _ = file.TryGetAdvanceWidth(g, out _);
            try
            {
                _ = file.GetAdvanceWidth(g);
            }
            catch (PdfFormatException)
            {
            }

            _ = file.GetGlyphName(g);
        }

        for (int c = 0; c < 256; c++)
        {
            _ = file.GetGlyphForUnicode(c);
            _ = file.GetGlyphForSymbolicCode(c);
        }

        _ = file.GetGlyphForName("A");
        _ = file.EmbeddingRights;

        string fontObject = (mode & 3) switch
        {
            0 => "<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /WinAnsiEncoding /FirstChar 65 /LastChar 70 /Widths [700 700 700 700 700 700] >>",
            1 => "<< /Type /Font /Subtype /TrueType /BaseFont /Sym /FirstChar 65 /LastChar 70 /Widths [500 500 500 500 500 500] /FontDescriptor << /Type /FontDescriptor /FontName /Sym /Flags 4 >> >>",
            2 => "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /BaseFont /X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 /W [1 [500 600] 3 9 700] >>] >>",
            _ => "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /Euro /bullet /uni0041] >> >>",
        };
        string content = (mode & 3) == 2 ? "BT /F1 12 Tf <00010002000300040005> Tj ET" : "BT /F1 12 Tf (ABCDEF) Tj ET";
        byte[] pdf = Build(content, fontObject);
        try
        {
            var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 16 << 20 };
            using PdfDocument doc = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true, Limits = limits });
            PdfDictionary font = doc.Pages[0].Resources.GetDictionary("Font")!.GetDictionary("F1")!;
            if ((mode & 64) != 0)
            {
                _ = doc.AddMissingToUnicode();
            }

            var map = new Dictionary<int, string> { [1] = "A", [2] = "B", [3] = "C", [65] = "A", [66] = "B" };
            _ = doc.EmbedFontProgram(font, file, new PdfFontEmbedOptions
            {
                Subset = (mode & 4) == 0,
                IgnoreEmbeddingRestrictions = (mode & 8) != 0,
                Replace = (mode & 16) != 0,
                CidsAreGlyphIds = (mode & 32) != 0,
                MissingGlyphs = PdfMissingGlyphPolicy.UseNotdef,
                Widths = (mode & 8) != 0 ? PdfFontWidthPolicy.KeepProgram : PdfFontWidthPolicy.AdjustProgram,
                CodeToUnicode = map,
            });
            _ = doc.AddMissingToUnicode();
            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16] });
            using PdfDocument back = PdfDocument.Open(ms.ToArray());
            _ = back.ExtractText();
        }
        catch (PdfException)
        {
            // Typed refusals and limits are the contract.
        }
    }

    private static void RunCffWidths(byte mode, byte[] program)
    {
        string stream = Encoding.Latin1.GetString(program);
        bool cid = (mode & 2) != 0;
        string[] objects = cid
            ?
            [
                "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+X /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
                "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /ABCDEF+X /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 600 /W [1 [500 600 700] 4 9 650] /FontDescriptor 7 0 R >>",
                "<< /Type /FontDescriptor /FontName /ABCDEF+X /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 8 0 R >>",
                $"<< /Subtype /CIDFontType0C /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            ]
            :
            [
                "<< /Type /Font /Subtype /Type1 /BaseFont /ABCDEF+X /FirstChar 65 /LastChar 70 /Widths [700 650 600 550 500 450] /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding /Differences [65 /gid00001 /gid00002 /A /B] >> /FontDescriptor 6 0 R >>",
                "<< /Type /FontDescriptor /FontName /ABCDEF+X /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 900 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 7 0 R >>",
                $"<< /Subtype /Type1C /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            ];
        string content = cid ? "BT /F1 12 Tf <00010002000300040005> Tj ET" : "BT /F1 12 Tf (ABCDEF) Tj ET";
        byte[] pdf = Build(content, objects[0], objects[1..]);
        try
        {
            var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 16 << 20 };
            using PdfDocument doc = PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true, Limits = limits });
            _ = doc.AddMissingToUnicode(new PdfToUnicodeOptions { CodeToText = static u => u.Code % 3 == 0 ? null : ((char)('a' + (u.Code % 26))).ToString() });
            _ = doc.MatchEmbeddedFontWidths(new PdfWidthMatchOptions { Tolerance = (mode & 4) != 0 ? 0.5 : 0.01 });
            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16] });
            using PdfDocument back = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { AllowEditing = true, Limits = limits });
            _ = back.ExtractText();
            _ = back.MatchEmbeddedFontWidths();
        }
        catch (PdfException)
        {
            // Typed refusals and limits are the contract.
        }
    }

    private static byte[] Build(string content, string fontObject, params string[] more)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        Obj(fontObject);
        foreach (string extra in more)
        {
            Obj(extra);
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
