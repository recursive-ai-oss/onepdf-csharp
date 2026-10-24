using System;
using System.Collections.Generic;
using System.IO;
using OnePdf;
using OnePdf.Tests.Images;

namespace OnePdf.Fuzz;

/// <summary>
/// PNG target: the bytes are a PNG file. It is added to a new document as an image, drawn on a page, the document is saved, reopened
/// and interpreted, which decodes the stored image (passed-through IDAT data with PNG predictors, or the decoded samples) and its soft
/// mask. Limits are small so hostile dimensions are refused quickly. Only the library's typed exceptions may escape.
/// </summary>
internal static class PngTarget
{
    private sealed class Sink : PdfDevice
    {
    }

    // The first byte is a mode: bit 0 recomputes every chunk's CRC, so that mutated chunk contents get past the checksum and reach the
    // decoder (a random mutation almost never keeps a CRC valid). The rest is the PNG file.
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        byte[] png = data[1..].ToArray();
        if ((data[0] & 1) != 0)
        {
            FixCrcs(png);
        }

        var limits = PdfLimits.Default with { MaxImagePixels = 1 << 22, MaxDecodedStreamBytes = 16 << 20 };
        try
        {
            using PdfDocument doc = PdfDocument.CreateEmpty(new PdfOpenOptions { Limits = limits });
            PdfPage page = doc.InsertBlankPage(0, 200, 200);
            PdfCreatedImage image = doc.AddPngImage(png);
            using (PdfPageContentBuilder b = page.BeginContent())
            {
                b.DrawImage(image, 10, 10, 100, 100);
                b.Commit();
            }

            using var ms = new MemoryStream();
            doc.Save(ms, new PdfSaveOptions { DocumentId = new byte[16], CompressStreams = true });
            using PdfDocument back = PdfDocument.Open(ms.ToArray(), new PdfOpenOptions { Limits = limits });
            back.Pages[0].Interpret(new Sink(), null);
        }
        catch (PdfException)
        {
            // Typed errors for hostile files are the contract.
        }
    }

    private static void FixCrcs(byte[] png)
    {
        int pos = 8;
        while (pos >= 8 && pos + 12 <= png.Length)
        {
            uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            if (len > (uint)(png.Length - pos - 12))
            {
                return;
            }

            int end = pos + 8 + (int)len;
            uint c = 0xFFFFFFFFu;
            for (int i = pos + 4; i < end; i++)
            {
                c ^= png[i];
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(end), c ^ 0xFFFFFFFFu);
            pos = end + 4;
        }
    }

    public static IEnumerable<byte[]> Seeds()
    {
        (int Type, int Depth)[] kinds = [(0, 1), (0, 4), (0, 8), (0, 16), (2, 8), (2, 16), (3, 1), (3, 2), (3, 8), (4, 8), (4, 16), (6, 8), (6, 16)];
        foreach ((int type, int depth) in kinds)
        {
            foreach (bool interlaced in (bool[])[false, true])
            {
                var spec = new PngKit.Spec(11, 7, type, depth) { Interlaced = interlaced };
                if (type == 3)
                {
                    spec = spec with { Palette = [.. Enumerable3(Math.Min(1 << depth, 16) * 3)], Trns = depth == 8 ? [255, 0, 0, 128] : null };
                }
                else if (type == 0 && depth == 8)
                {
                    spec = spec with { Trns = [0, 77] };
                }
                else if (type == 2 && depth == 8)
                {
                    spec = spec with { Trns = [0, 1, 0, 2, 0, 3], Iccp = PngKit.IccProfileChunk("RGB ") };
                }

                yield return PngKit.Encode(spec, PngKit.RandomRows(spec, type * 31 + depth));
            }
        }
    }

    private static IEnumerable<byte> Enumerable3(int n)
    {
        for (int i = 0; i < n; i++)
        {
            yield return (byte)((i * 53) + 7);
        }
    }
}
