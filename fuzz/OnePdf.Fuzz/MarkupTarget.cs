using System;
using System.Collections.Generic;
using System.IO;
using OnePdf.Tests.Signatures;

namespace OnePdf.Fuzz;

/// <summary>
/// Text markup writing target. A mode byte, three 12-byte annotation records (kind, flags, coordinate scale, quadrilateral, colour,
/// skew), then a PDF. The file is opened for editing, up to three markup annotations are added to a page, and the document is saved
/// in full and incrementally and reopened. Checked: the incremental output starts with the input, the same calls give the same
/// bytes, an undamaged document reopens with the added annotations, and each added annotation keeps its subtype and an appearance.
/// Coordinates sometimes exceed the accepted range or are non-finite; a rejected annotation must be an argument exception and leave the
/// page as it was. Only the library's typed exceptions and that validation outcome may escape.
/// </summary>
internal static class MarkupTarget
{
    private const int RecordLength = 12;
    private const int HeaderLength = 1 + (3 * RecordLength);

    private static readonly double[] Scales = [0.5, 1, 4, 30, 1e4, 1e6, 2e7, double.NaN];

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            return;
        }

        byte[] header = data[..HeaderLength].ToArray();
        byte[] input = data[HeaderLength..].ToArray();
        var limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 };
        try
        {
            (byte[] full, byte[] inc, int added, bool damaged, bool encrypted) = Edit(input, header, limits);
            (byte[] full2, byte[] inc2, _, _, _) = Edit(input, header, limits);
            // Encrypted output uses random IVs, so only plain documents are compared byte for byte.
            if (!encrypted && (!full.AsSpan().SequenceEqual(full2) || !inc.AsSpan().SequenceEqual(inc2)))
            {
                throw new InvalidOperationException("the same calls gave different bytes");
            }

            if (inc.Length < input.Length || !inc.AsSpan(0, input.Length).SequenceEqual(input))
            {
                throw new InvalidOperationException("the original bytes are not a prefix of the incremental save");
            }

            if (damaged || added == 0)
            {
                return;
            }

            foreach (byte[] file in (byte[][])[full, inc])
            {
                using PdfDocument check = PdfDocument.Open(file, new PdfOpenOptions { Limits = limits });
                int pageIndex = header[0] >> 4;
                PdfPage page = check.Pages[pageIndex % check.Pages.Count];
                int found = 0;
                foreach (PdfAnnotation a in page.Annotations)
                {
                    if (a.Subtype is "Highlight" or "StrikeOut" or "Underline" or "Squiggly" or "Caret" && a.Name is not null && a.Name.StartsWith("fz-", StringComparison.Ordinal))
                    {
                        found++;
                        if (a.GetAppearanceStream() is null)
                        {
                            throw new InvalidOperationException("a written markup annotation has no appearance");
                        }
                    }
                }

                if (found != added)
                {
                    throw new InvalidOperationException("the reopened " + (file == full ? "full" : "incremental") + " file has " + found + " of " + added + " added annotations; page " + (pageIndex % check.Pages.Count) + " of " + check.Pages.Count + " lists " + page.Annotations.Count + "; diagnostics: " + string.Join(" | ", System.Linq.Enumerable.Select(check.Diagnostics, static d => d.Message)));
                }
            }
        }
        catch (PdfException)
        {
        }
    }

    private static (byte[] Full, byte[] Incremental, int Added, bool Damaged, bool Encrypted) Edit(byte[] input, byte[] h, PdfLimits limits)
    {
        using PdfDocument doc = PdfDocument.Open(input, new PdfOpenOptions { Limits = limits, AllowEditing = true });
        int added = 0;
        if (doc.Pages.Count > 0)
        {
            PdfPage page = doc.Pages[(h[0] >> 4) % doc.Pages.Count];
            int count = 1 + (h[0] & 3) % 3;
            for (int i = 0; i < count; i++)
            {
                int before = page.Annotations.Count;
                PdfMarkupAnnotationOptions o = Record(h, 1 + (i * RecordLength), i);
                try
                {
                    PdfAnnotation a = page.AddMarkupAnnotation(o);
                    if (a.Subtype != o.Kind.ToString() || page.Annotations.Count != before + (o.Popup is null ? 1 : 2))
                    {
                        throw new InvalidOperationException("the page does not list the added annotation: " + before + " before, " + page.Annotations.Count + " after, popup " + (o.Popup is not null) + ", /Annots is " + page.Dictionary.GetRaw("Annots")?.GetType().Name + " of " + (page.Dictionary.Get("Annots") as PdfArray)?.Count );
                    }

                    added++;
                }
                catch (ArgumentException)
                {
                    if (page.Annotations.Count != before)
                    {
                        throw new InvalidOperationException("a rejected annotation changed the page");
                    }
                }
            }
        }

        using var full = new MemoryStream();
        doc.Save(full);
        using var inc = new MemoryStream();
        doc.SaveIncremental(inc, new PdfSaveOptions { DocumentId = null });
        // Read after the work: objects are parsed lazily, so a file whose damage shows only when its pages are read (offsets that miss,
        // missing page objects) reports it now. Such a file may read differently with an update behind it, as in the save target.
        bool damaged = false;
        foreach (PdfDiagnostic d in doc.Diagnostics)
        {
            damaged |= d.Severity >= PdfSeverity.Warning;
        }

        return (full.ToArray(), inc.ToArray(), added, damaged, doc.Trailer.GetRaw("Encrypt") is not null);
    }

    private static PdfMarkupAnnotationOptions Record(byte[] h, int at, int index)
    {
        byte kind = h[at], flags = h[at + 1], scaleSel = h[at + 2];
        double s = Scales[scaleSel % Scales.Length] * ((scaleSel & 0x80) == 0 ? 1 : 0.01);
        double x = (h[at + 3] - 64) * s, y = (h[at + 4] - 64) * s, w = h[at + 5] * s / 4, ht = h[at + 6] * s / 8;
        double skew = (h[at + 10] - 128) * s / 64, skew2 = (h[at + 11] - 128) * s / 64;
        PdfMarkupKind k = (PdfMarkupKind)(kind % 6); // 5 is not a kind: the rejection path
        var quad = new PdfMarkupQuad(x + skew, y + ht, x + w, y + ht + skew2, x, y, x + w + skew2, y + skew);
        return new PdfMarkupAnnotationOptions
        {
            Kind = k,
            Quads = k == PdfMarkupKind.Caret ? [] : (flags & 1) == 0 ? [quad] : [quad, PdfMarkupQuad.FromRectangle(new PdfRectangle(x, y - 14, x + w, y - 2))],
            Rectangle = k == PdfMarkupKind.Caret || (flags & 2) != 0 ? new PdfRectangle(x, y, x + w, y + ht) : null,
            RectangleDifferences = (flags & 4) == 0 ? null : new PdfRectangle(skew % 3, skew2 % 3, skew % 5, skew2 % 5),
            CaretSymbol = (flags & 8) == 0 ? PdfCaretSymbol.None : PdfCaretSymbol.Paragraph,
            Color = (flags >> 4) switch { 0 => null, 1 => [h[at + 7] / 255.0], 2 => [h[at + 7] / 255.0, h[at + 8] / 255.0, h[at + 9] / 255.0], 3 => [h[at + 7] / 255.0, h[at + 8] / 255.0, h[at + 9] / 255.0, 0.2], _ => [h[at + 7] / 100.0] },
            Opacity = (flags & 0x40) == 0 ? null : h[at + 8] / 255.0,
            Contents = (flags & 0x80) == 0 ? null : "fuzz \u00e9\u4e2d",
            Title = "fz",
            Name = "fz-" + index,
            ModifiedDate = (flags & 2) == 0 ? null : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromMinutes(h[at + 9] - 128)),
            Popup = (flags & 4) == 0 ? null : new PdfMarkupPopup(new PdfRectangle(x + 5, y, x + 5 + w + 20, y + ht + 20), (flags & 1) != 0),
        };
    }

    /// <summary>Starting inputs: the signing tests' base file with one record of each kind.</summary>
    public static IEnumerable<byte[]> Seeds()
    {
        byte[] baseFile = SignedPdf.BaseFile(new SignedPdf.Options(), out _);
        for (byte kind = 0; kind < 5; kind++)
        {
            byte[] head = new byte[HeaderLength];
            head[0] = 0;
            head[1] = kind;
            head[2] = (byte)(kind * 3);
            head[3] = 1;
            head[4] = 84;
            head[5] = 190;
            head[6] = 200;
            head[7] = 100;
            head[8] = 255;
            head[9] = 0;
            head[10] = 0;
            head[11] = 128;
            head[12] = 128;
            yield return [.. head, .. baseFile];
        }
    }
}
