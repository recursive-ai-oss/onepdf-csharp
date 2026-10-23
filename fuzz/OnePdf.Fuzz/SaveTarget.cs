using System;
using System.IO;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Writer target: opens the bytes as an editable document, changes a few objects, saves it in full and incrementally in forms chosen
/// from the input, and reopens what was written. Only the library's typed exceptions may escape, and a file the writer produced
/// from an openable document must open again.
/// </summary>
internal static class SaveTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        int sel = 0;
        for (int i = 0; i < Math.Min(data.Length, 64); i++)
        {
            sel += data[i];
        }

        var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 };
        byte[] input = data.ToArray();
        try
        {
            using PdfDocument doc = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits, AllowEditing = true });
            Mutate(doc, sel);

            var options = new PdfSaveOptions
            {
                XrefFormat = (sel & 3) switch { 0 => PdfXrefFormat.Auto, 1 => PdfXrefFormat.Table, _ => PdfXrefFormat.Stream },
                UseObjectStreams = (sel & 4) == 0,
                CompressStreams = (sel & 8) != 0,
            };
            using var full = new MemoryStream();
            doc.Save(full, options);
            Reopen(full.ToArray(), limits);

            if (doc.Revisions.Count > 0 && doc.Revisions[^1].XrefOffset >= 0)
            {
                using var inc = new MemoryStream();
                doc.SaveIncremental(inc, new PdfSaveOptions { CompressStreams = (sel & 8) != 0 });
                byte[] bytes = inc.ToArray();
                if (bytes.Length < input.Length || !bytes.AsSpan(0, input.Length).SequenceEqual(input))
                {
                    throw new InvalidOperationException("the original bytes are not a prefix of the incremental save");
                }

                Reopen(bytes, limits);
            }
        }
        catch (PdfException)
        {
        }
    }

    private static void Mutate(PdfDocument doc, int sel)
    {
        int n = Math.Min(doc.ObjectCount, 300);
        for (int i = 1; i < n; i++)
        {
            PdfObject? o;
            try
            {
                o = doc.GetObject(i);
            }
            catch (PdfException)
            {
                continue;
            }

            switch (o)
            {
                case PdfDictionary d when (i + sel) % 3 == 0:
                    d.SetEntry("FuzzKey", new PdfInteger(i));
                    d.RemoveEntry("Fuzz2");
                    break;
                case PdfArray a when (i + sel) % 3 == 1:
                    a.Append(new PdfName("Fuzz"));
                    if (a.Count > 3)
                    {
                        a.RemoveAt(1);
                    }

                    break;
                case PdfStream s when (i + sel) % 5 == 0:
                    s.SetData(new byte[] { 1, 2, 3, 4, 5 }, (sel & 16) != 0);
                    break;
            }
        }

        if ((sel & 32) != 0 && n > 2)
        {
            doc.RemoveObject(n - 1);
        }

        PdfReference extra = doc.AddObject(new PdfDictionary());
        if (doc.Resolve(extra) is PdfDictionary ed)
        {
            ed.SetEntry("Self", extra);
        }

        try
        {
            doc.SetInfo("Title", "Fuzz \u00e9\u4e2d");
        }
        catch (PdfException)
        {
        }
    }

    private static void Reopen(byte[] bytes, PdfLimits limits)
    {
        try
        {
            using PdfDocument again = PdfDocument.Open(bytes, new PdfOpenOptions { Limits = limits });
            _ = again.PageCount;
        }
        catch (PdfFormatException e)
        {
            throw new InvalidOperationException("a saved file does not open again: " + e.Message, e);
        }
        catch (PdfLimitException)
        {
        }
    }
}
