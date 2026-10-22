using System;
using OnePdf;
using OnePdf.Internal;

namespace OnePdf.Fuzz;

/// <summary>
/// Fuzz targets for the object reader. Each lets only the library's documented exceptions escape; anything else (an
/// index error, a null reference, a stack overflow, a hang) is a finding.
/// </summary>
internal static class ReaderTargets
{
    /// <summary>Tokenizes and parses the bytes as a sequence of PDF objects.</summary>
    public static void Parser(ReadOnlySpan<byte> data)
    {
        try
        {
            using var src = new MemorySource(data.ToArray());
            using var lx = new Lexer(src);
            var limits = PdfLimits.Default;
            var parser = new ObjectParser(lx, null, limits, new DiagnosticBag(false));
            for (int i = 0; i < 100_000; i++)
            {
                long before = lx.Position;
                PdfObject? o = parser.ReadObject(out string? kw);
                if (o is null && kw is null)
                {
                    break;
                }

                if (lx.Position <= before)
                {
                    lx.Position = before + 1;
                }
            }
        }
        catch (PdfException)
        {
        }
    }

    /// <summary>Opens the bytes as a document (xref reading and repair), then touches every object, stream and the page tree.</summary>
    public static void Document(ReadOnlySpan<byte> data)
    {
        var options = new PdfOpenOptions { Limits = PdfLimits.Default with { MaxDecodedStreamBytes = 8 << 20, MaxObjects = 200_000 } };
        try
        {
            using PdfDocument doc = PdfDocument.Open(data.ToArray(), options);
            _ = doc.PageCount;
            int n = Math.Min(doc.ObjectCount, 2000);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    if (doc.GetObject(i) is PdfStream s)
                    {
                        _ = s.GetRawBytes();
                        _ = s.GetDecodedBytes();
                    }
                }
                catch (PdfLimitException)
                {
                }
                catch (PdfFormatException)
                {
                }
            }

            foreach (PdfRevision r in doc.Revisions)
            {
                _ = r.EndOffset;
                _ = r.Catalog;
            }
        }
        catch (PdfException)
        {
        }
    }
}
