using System;
using System.Collections.Generic;
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
            bool damaged = false;
            foreach (PdfDiagnostic d in doc.Diagnostics)
            {
                damaged |= d.Severity >= PdfSeverity.Warning;
            }

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

                // A file that needed repair may read differently with an update behind it (a stream whose end was found by scanning
                // can run on into the update), so only an undamaged original must give a readable update.
                if (!damaged)
                {
                    Reopen(bytes, limits);
                }
            }

            PageOperations(input, sel, limits);
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>
    /// Page operations on a fresh editable copy: rotate, duplicate, move, reorder, delete, insert, import from a second reading of the
    /// same bytes, merge and split. Page counts must add up at every step, and the merged file must open again with that count.
    /// </summary>
    internal static void PageOperations(byte[] input, int sel, PdfLimits limits)
    {
        using PdfDocument doc = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits, AllowEditing = true });
        int count = doc.Pages.Count;
        if (count == 0)
        {
            doc.InsertBlankPage(0, 100, 100);
            count = 1;
        }

        doc.RotatePage(sel % count, 90 * (1 + (sel % 3)));
        if ((sel & 1) != 0)
        {
            doc.DuplicatePage(sel % count);
            count++;
            Expect(doc.Pages.Count == count, "duplicate");
        }

        if ((sel & 2) != 0 && count > 1)
        {
            doc.MovePage(0, count - 1);
        }

        if ((sel & 4) != 0 && count > 1)
        {
            int[] reversed = new int[count];
            for (int i = 0; i < count; i++)
            {
                reversed[i] = count - 1 - i;
            }

            doc.ReorderPages(reversed);
        }

        if ((sel & 8) != 0 && count > 1)
        {
            doc.RemovePage(sel % count);
            count--;
            Expect(doc.Pages.Count == count, "remove");
        }

        doc.InsertBlankPage(sel % (count + 1), 50, 60);
        count++;
        Expect(doc.PageCount == count && doc.Pages.Count == count, "insert");

        using PdfDocument other = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits });
        int m = other.Pages.Count;
        doc.ImportPages(other, null, sel % (count + 1));
        count += m;
        Expect(doc.Pages.Count == count, "import");

        using PdfDocument merged = PdfDocument.Merge([doc, other]);
        Expect(merged.Pages.Count == count + m, "merge");
        using var saved = new MemoryStream();
        merged.Save(saved, new PdfSaveOptions { XrefFormat = (sel & 16) != 0 ? PdfXrefFormat.Table : PdfXrefFormat.Stream });
        using PdfDocument again = PdfDocument.Open(saved.ToArray(), new PdfOpenOptions { Limits = limits });
        Expect(again.Pages.Count == count + m, "merged file reopened with a different page count");

        if (m > 0)
        {
            IReadOnlyList<PdfDocument> parts = PdfDocument.Split(other, 1 + (sel % 3));
            int total = 0;
            foreach (PdfDocument part in parts)
            {
                total += part.Pages.Count;
                part.Dispose();
            }

            Expect(total == m, "split");
        }
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("page operations: " + what);
        }
    }

    internal static void Mutate(PdfDocument doc, int sel)
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
                // Existing stream data is only rewritten, never replaced: object streams and cross-reference streams hold the
                // structure the reopen check relies on, and a mutated file may have lost the keys that say which they are.
                case PdfStream s when (i + sel) % 5 == 0:
                    s.Dictionary.SetEntry("FuzzKey", new PdfInteger(i));
                    break;
            }
        }

        if ((sel & 32) != 0)
        {
            doc.RemoveObject(doc.AddObject(new PdfDictionary()).Number);
        }

        PdfStream fresh = doc.CreateStream(new byte[] { 1, 2, 3, 4, 5 }, (sel & 16) != 0);
        PdfReference extra = doc.AddObject(new PdfDictionary());
        ((PdfDictionary)doc.Resolve(extra)!).SetEntry("Data", fresh);
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
