using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Writes starting inputs for a fuzz target: <c>seeds &lt;target&gt; &lt;out dir&gt; [pdf directory] [font directory]</c>.
/// Targets that read a whole file are seeded with real PDFs; the others get small hand-built inputs in the layout the target
/// expects (a few header bytes, then the payload), plus fonts and CMaps cut out of the PDFs. This runs on the plain
/// (uninstrumented) build, before the library is instrumented.
/// </summary>
internal static class Seeds
{
    private const int MaxPdfBytes = 200_000;

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: seeds <target> <out dir> [pdf dir] [font dir]");
            return 2;
        }

        string target = args[1], outDir = args[2];
        string? pdfDir = args.Length > 3 ? args[3] : null;
        string? fontDir = args.Length > 4 ? args[4] : null;
        Directory.CreateDirectory(outDir);
        int written = 0;

        void Put(string name, ReadOnlySpan<byte> bytes)
        {
            string path = Path.Combine(outDir, name);
            if (!File.Exists(path))
            {
                File.WriteAllBytes(path, bytes.ToArray());
                written++;
            }
        }

        void PutData(string kind, ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> header = default)
        {
            byte[] all = [.. header, .. bytes];
            string hash = Convert.ToHexString(SHA256.HashData(all))[..16].ToLowerInvariant();
            Put($"{kind}-{hash}", all);
        }

        IEnumerable<string> pdfs = pdfDir is not null && Directory.Exists(pdfDir)
            ? Directory.EnumerateFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            : [];

        switch (target)
        {
            case "parser" or "document" or "model" or "content" or "pdffont" or "save" or "pdfa":
                foreach (string f in pdfs)
                {
                    if (new FileInfo(f).Length <= MaxPdfBytes)
                    {
                        PutData("pdf", File.ReadAllBytes(f));
                    }
                }

                if (target is "parser" or "content")
                {
                    // Bare object syntax and content streams, which are not files.
                    PutData("obj", "<< /Type /Page /Kids [1 0 R 2 0 R] /Rect [0 0 1.5e2 -3.] /S (a\\)b\\050) /H <48656C6C6F> /N /A#20B >> 12 0 obj [1 2 R true null] endobj"u8);
                    PutData("cs", "q 1 0 0 1 10 10 cm 0 0 100 100 re W n BT /F1 12 Tf 10 20 Td (Hello) Tj [(a) -120 (b)] TJ ET 0.5 g 0 0 50 50 re f Q"u8);
                    PutData("cs", "q 1 0 0 rg 0 0 m 10 0 20 10 30 30 c h B* /GS gs /Fm Do /P1 cs /P1 scn BI /W 2 /H 2 /BPC 8 /CS /G ID abcd EI Q"u8);
                }

                break;

            case "tag":
                {
                    int n = 0;
                    foreach (string f in pdfs)
                    {
                        if (new FileInfo(f).Length <= MaxPdfBytes)
                        {
                            // Rotate through the modes so every mix of block, word, graphics and artifact tagging has seeds.
                            PutData("pdf", File.ReadAllBytes(f), [(byte)(n++ % 16), 0]);
                        }
                    }

                    PutData("cs", "BT /F1 12 Tf 20 100 Td (Hello secret world) Tj [(a) -120 (b)] TJ ET q 1 0 0 rg 0 0 50 50 re f Q /Fm Do BI /W 2 /H 2 /BPC 8 /CS /G ID abcd EI"u8, [2, 0]);
                    PutData("cs", "/Artifact BMC BT /F1 12 Tf 20 100 Td (One two three) Tj ET EMC q 1 0 0 1 0 -20 cm BT /F1 12 Tf 20 100 Td (Four five) Tj ET Q"u8, [3, 0]);
                    break;
                }

            case "redact":
                {
                    byte[] head = [0, 20, 90, 60, 20, 100, 150, 50, 40, 10, 10, 80, 80, 150, 20, 60, 60, 0, 0, 0];
                    foreach (string f in pdfs)
                    {
                        if (new FileInfo(f).Length <= MaxPdfBytes)
                        {
                            PutData("pdf", File.ReadAllBytes(f), head);
                        }
                    }

                    PutData("cs", "BT /F1 12 Tf 20 100 Td (Hello secret world) Tj ET q 1 0 0 rg 0 0 50 50 re f Q"u8, head);

                    // Content drawn by a pattern cell, a soft-mask group and a Type 3 glyph procedure (files without a cross-reference table: repaired on open).
                    byte[] nestedHead = [3, 14, 60, 40, 30, 100, 20, 40, 40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
                    string font = "5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj\n";
                    PutData("pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n" +
                        "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /Pattern << /P1 6 0 R >> >> >> endobj\n" +
                        "4 0 obj << /Length 70 >> stream\n/Pattern cs /P1 scn 20 100 80 60 re f BT /F1 12 Tf 120 20 Td (visible) Tj ET\nendstream endobj\n" + font +
                        "6 0 obj << /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 40 40] /XStep 40 /YStep 40 /Resources << /Font << /F1 5 0 R >> >> /Length 44 >> stream\nBT /F1 10 Tf 2 25 Td (PATTERNSECRET) Tj ET\nendstream endobj\ntrailer << /Root 1 0 R >>\n"), nestedHead);
                    PutData("pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n" +
                        "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> /ExtGState << /GS1 6 0 R >> >> >> endobj\n" +
                        "4 0 obj << /Length 60 >> stream\n/GS1 gs 0 0 1 rg 120 20 60 40 re f BT /F1 12 Tf 20 20 Td (visible) Tj ET\nendstream endobj\n" + font +
                        "6 0 obj << /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 7 0 R >> >> endobj\n" +
                        "7 0 obj << /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Font << /F1 5 0 R >> >> /Length 60 >> stream\n1 g BT /F1 14 Tf 30 120 Td (MASKSECRET) Tj ET 0.5 g 150 30 30 30 re f\nendstream endobj\ntrailer << /Root 1 0 R >>\n"), nestedHead);
                    PutData("pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n" +
                        "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /T3 6 0 R >> >> >> endobj\n" +
                        "4 0 obj << /Length 50 >> stream\nBT /T3 20 Tf 20 100 Td (ab) Tj 130 0 Td (c) Tj ET\nendstream endobj\n" + font +
                        "6 0 obj << /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 7 0 R /b 8 0 R /c 9 0 R >> /Encoding << /Type /Encoding /Differences [97 /a /b /c] >> /FirstChar 97 /LastChar 99 /Widths [1000 1000 1000] /Resources << /Font << /F1 5 0 R >> >> >> endobj\n" +
                        "7 0 obj << /Length 70 >> stream\n1000 0 d0 100 100 700 700 re f BT /F1 300 Tf 100 400 Td (GLYPHSECRET) Tj ET\nendstream endobj\n" +
                        "8 0 obj << /Length 40 >> stream\n1000 0 0 0 1000 1000 d1 100 100 800 800 re f\nendstream endobj\n" +
                        "9 0 obj << /Length 40 >> stream\n1000 0 d0 -6500 100 800 800 re f\nendstream endobj\ntrailer << /Root 1 0 R >>\n"), nestedHead);
                    PutData("cs", "q 1 0 0 1 10 10 cm /Fm Do Q BT /F1 10 Tf [(a) -120 (b)] TJ (c) ' 1 2 (d) \" ET BI /W 2 /H 2 /BPC 8 /CS /G ID abcd EI"u8, [255, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
                    break;
                }

            case "appearance":
                for (byte k = 0; k < 21; k++)
                {
                    PutData("entries", "/Rect [10 10 150 50] /C [1 0 0] /IC [0 1 0] /BS << /W 2 /S /D /D [3 2] >> /MK << /BG [0.9] /BC [0] /CA (4) /R 90 >> /MaxLen 8 /Q 1 /TI 1 /I [0] /LE [/OpenArrow /Circle] /CA 0.5 /RD [1 1 1 1] /CL [5 5 40 40] /LL 10 /DA (/Cour 9 Tf 1 0 0 rg)"u8, [k]);
                }

                break;

            case "filters":
                {
                    byte[] text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("BT /F1 12 Tf 72 700 Td (Hello, world) Tj ET\n", 20)));
                    byte[] z = Deflate(text);
                    PutData("flate", z, [0, 0, 1, 7, 0, 1]);
                    // PNG predictor 15 over rows of 16 bytes (filter byte per row).
                    byte[] rows = new byte[10 * 17];
                    for (int r = 0; r < 10; r++)
                    {
                        rows[r * 17] = (byte)(r % 5);
                        for (int c = 1; c < 17; c++)
                        {
                            rows[(r * 17) + c] = (byte)((r * 7) + (c * 3));
                        }
                    }

                    PutData("flate-png", Deflate(rows), [0, 15, 0, 7, 15, 1]);
                    PutData("flate-tiff", Deflate(rows), [5, 2, 2, 7, 15, 1]);
                    PutData("flate-trunc", z.AsSpan(0, z.Length - 6), [0, 0, 1, 7, 0, 1]);
                    PutData("lzw", new byte[] { 0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01 }, [1, 0, 0, 7, 0, 1]);
                    PutData("hex", "48656C6C6F2C20776F726C64>"u8, [2, 0, 0, 7, 0, 1]);
                    PutData("a85", "87cURD]i,\"Ebo80~>"u8, [3, 0, 0, 7, 0, 1]);
                    PutData("rl", new byte[] { 2, 65, 66, 67, 0xFE, 88, 0x80 }, [4, 0, 0, 7, 0, 1]);
                    break;
                }

            case "ccitt":
                PutData("g4", new byte[] { 0xFF, 0xFF, 0xFF, 0x00, 0x10, 0x01 }, [0, 0, 0, 0, 7, 0]);
                PutData("g3-1d", new byte[] { 0x35, 0x35, 0x35, 0xAC, 0x80, 0x00, 0x10, 0x01 }, [1, 1, 0, 0, 63, 0]);
                PutData("g3-2d", new byte[] { 0x00, 0x18, 0xA5, 0x5A, 0x00, 0x1C, 0x2D, 0x00, 0x10 }, [2, 1, 0, 0, 31, 0]);
                PutData("g4-rows", new byte[] { 0xFF, 0xAA, 0x55, 0x0F, 0xF0 }, [0, 16, 1, 0, 15, 4]);
                break;

            case "png":
                foreach (byte[] seed in PngTarget.Seeds())
                {
                    PutData("png-fixed", seed, [1]);
                    PutData("png-raw", seed, [0]);
                }

                break;

            case "shading":
                foreach (byte[] seed in ShadingTarget.Seeds())
                {
                    PutData("shading", seed);
                }

                break;

            case "type4":
                foreach (string p in new[]
                {
                    "{ dup mul exch dup mul add sqrt 1 exch sub }",
                    "{ 360 mul sin 2 div 0.5 add dup 0.5 gt { 1 exch sub } { } ifelse }",
                    "{ 3 1 roll exch 2 copy lt { pop } { exch pop } ifelse 1 index 0 eq { 1 } { 0 } ifelse add }",
                    "{ 100 { 2 mul 1 add } repeat cvi 7 idiv truncate round ceiling floor abs neg }",
                    "{ 1 0 atan 2 ln exp 3 log div and or xor not bitshift 5 mod }",
                })
                {
                    PutData("ps", Encoding.ASCII.GetBytes(p), [1, 0]);
                    PutData("ps3", Encoding.ASCII.GetBytes(p), [3, 2]);
                }

                break;

            case "signature":
                foreach (byte[] seed in SignatureTarget.Seeds())
                {
                    PutData("sig", seed);
                }

                foreach (string f in pdfs)
                {
                    byte[] bytes = File.ReadAllBytes(f);
                    if (bytes.Length <= MaxPdfBytes && bytes.AsSpan().IndexOf("ByteRange"u8) >= 0)
                    {
                        PutData("pdf", bytes, [1]);
                    }
                }

                break;

            case "cmap":
                PutData("tounicode", Encoding.ASCII.GetBytes(
                    "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Adobe-Identity-UCS def /CMapType 2 def\n" +
                    "1 begincodespacerange <0000> <FFFF> endcodespacerange\n2 beginbfchar <0003> <0020> <0024> <0041> endbfchar\n" +
                    "2 beginbfrange <0030> <0039> <0030> <0050> <0052> [<0061> <0062> <0063>] endbfrange\nendcmap CMapName currentdict /CMap defineresource pop end end"), [0]);
                PutData("cidcmap", Encoding.ASCII.GetBytes(
                    "/Adobe-Japan1-UCS2 usecmap 2 begincodespacerange <00> <80> <8140> <9FFC> endcodespacerange\n" +
                    "1 begincidrange <20> <7e> 231 endcidrange 2 begincidchar <8140> 633 <8141> 634 endcidchar 1 beginnotdefrange <00> <1f> 1 endnotdefrange /WMode 1 def"), [1]);
                foreach (byte[] cmap in ExtractFromPdfs(pdfs, Cmaps))
                {
                    PutData("pdfcmap", cmap, [0]);
                }

                break;

            case "security":
                {
                    var rnd = new Random(1);
                    for (int i = 0; i < 12; i++)
                    {
                        byte[] b = new byte[rnd.Next(64, 400)];
                        rnd.NextBytes(b);
                        b[0] = (byte)(i % 5);
                        b[1] = (byte)(i % 3);
                        PutData("rnd", b);
                    }

                    break;
                }

            case "fontsubset":
                // Option byte, eight picks, then the font: the generated test fonts' shape comes from the cut-out PDF fonts and any system fonts.
                foreach (byte[] font in ExtractFromPdfs(pdfs, FontFiles))
                {
                    PutData("pdffont", font, [0, 1, 2, 3, 4, 5, 6, 7, 8]);
                    PutData("pdffont-whole", font, [1, 9, 8, 7, 6, 5, 4, 3, 2]);
                }

                if (fontDir is not null && Directory.Exists(fontDir))
                {
                    foreach (string f in Directory.EnumerateFiles(fontDir, "*.*", SearchOption.AllDirectories)
                                 .Where(static f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                                 .Order(StringComparer.Ordinal).Take(6))
                    {
                        if (new FileInfo(f).Length <= 260_000)
                        {
                            PutData("sysfont", File.ReadAllBytes(f), [0, 10, 20, 30, 40, 50, 60, 70, 80]);
                        }
                    }
                }

                break;

            case "fontprogram":
                foreach (byte[] font in ExtractFromPdfs(pdfs, FontFiles))
                {
                    PutData("pdffont", font, [0]);
                    PutData("pdffont5", font, [5]);
                }

                if (fontDir is not null && Directory.Exists(fontDir))
                {
                    foreach (string f in Directory.EnumerateFiles(fontDir, "*.*", SearchOption.AllDirectories)
                                 .Where(static f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                                 .Order(StringComparer.Ordinal).Take(6))
                    {
                        if (new FileInfo(f).Length <= 400_000)
                        {
                            PutData("sysfont", File.ReadAllBytes(f), [0]);
                            PutData("sysfont5", File.ReadAllBytes(f), [5]);
                        }
                    }
                }

                break;

            case "extraction":
                // A flag byte, then 10-byte glyph records: text pick, x (2 bytes, 1/80 pt), y (2 bytes), size, direction, advance, flags, extra.
                foreach ((string name, byte flags, int[] picks, byte dir) in new (string, byte, int[], byte)[]
                {
                    ("columns", 0b1001, [0, 1, 2, 3, 4, 0, 1, 2], 0),
                    ("rtl-mixed", 0b1011, [11, 12, 13, 14, 4, 0, 1, 15], 0),
                    ("vertical", 0b1001, [17, 18, 17, 18, 17, 18, 17, 18], 4),
                })
                {
                    var rec = new List<byte> { flags };
                    for (int col = 0; col < 2; col++)
                    {
                        for (int line = 0; line < 3; line++)
                        {
                            for (int i = 0; i < picks.Length; i++)
                            {
                                int x = (60 + col * 250 + i * 5) * 80, y = (700 - line * 14) * 80;
                                rec.AddRange([(byte)picks[i], (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y, 10, dir, 8, 1, 0]);
                            }
                        }
                    }

                    PutData(name, rec.ToArray());
                }

                break;

            default:
                Console.Error.WriteLine($"no seeds for target '{target}'");
                return 2;
        }

        Console.WriteLine($"{target}: {written} seed files in {outDir}");
        return 0;
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    private static IEnumerable<PdfStream> FontFiles(PdfDictionary d)
    {
        foreach (string key in new[] { "FontFile", "FontFile2", "FontFile3" })
        {
            if (d.GetStream(key) is { } s)
            {
                yield return s;
            }
        }
    }

    private static IEnumerable<PdfStream> Cmaps(PdfDictionary d)
    {
        if (d.GetStream("ToUnicode") is { } s)
        {
            yield return s;
        }

        if (d.GetStream("Encoding") is { } e)
        {
            yield return e;
        }
    }

    private static IEnumerable<byte[]> ExtractFromPdfs(IEnumerable<string> pdfs, Func<PdfDictionary, IEnumerable<PdfStream>> pick)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (string f in pdfs)
        {
            if (count >= 60)
            {
                yield break;
            }

            PdfDocument? doc;
            try
            {
                doc = PdfDocument.Open(f);
            }
            catch (PdfException)
            {
                continue;
            }

            using (doc)
            {
                int n = Math.Min(doc.ObjectCount, 20_000);
                for (int i = 0; i < n; i++)
                {
                    byte[]? bytes = null;
                    try
                    {
                        if (doc.GetObject(i) is PdfDictionary d)
                        {
                            foreach (PdfStream s in pick(d))
                            {
                                byte[] b = s.GetDecodedBytes().ToArray();
                                if (b.Length is > 8 and <= 250_000 && seen.Add(Convert.ToHexString(SHA256.HashData(b))))
                                {
                                    bytes = b;
                                }
                            }
                        }
                    }
                    catch (PdfException)
                    {
                    }

                    if (bytes is not null && count++ < 60)
                    {
                        yield return bytes;
                    }
                }
            }
        }
    }
}
