using System;
using System.Diagnostics;
using System.IO;
using OnePdf;
using OnePdf.Layout;

namespace LayoutBench;

/// <summary>
/// 1,200 short texts drawn from 8 distinct strings, set with and without OpenType features, to show that features do not cost the sharing of
/// shaping between equal texts. Run as <c>featured [iterations]</c>: prints mean time, allocation and an output hash per case.
/// </summary>
internal static class FeaturedText
{
    private static readonly string[] Words = ["22.12.2023.", "678.784.474", "ISK", "12.500", "Total", "office", "Invoice no.", "EUR"];
    private static readonly byte[] Font = File.ReadAllBytes(Content.FontFile("LiberationSans-Regular.ttf"));

    public static readonly string[] Cases = ["none", "on-text", "page-default", "document-default", "default-valued"];

    public static LayoutDocument Compose(string mode, int count = 1200)
    {
        return LayoutDocument.Compose(d =>
        {
            d.Fonts.Add(Font, "Report Sans");
            d.Fonts.DefaultFamily = "Report Sans";
            if (mode == "document-default")
            {
                d.DefaultTextStyle(s => s.Ligatures(false).Kerning(false));
            }

            d.Page(p =>
            {
                p.Size(PageSize.A4);
                p.Margin(36);
                if (mode == "page-default")
                {
                    p.DefaultTextStyle(s => s.Ligatures(false).Kerning(false));
                }

                p.Content().Column(col =>
                {
                    for (int i = 0; i < count; i++)
                    {
                        TextDescriptor t = col.Item().Text(Words[i % Words.Length]).FontSize(8);
                        if (mode == "on-text")
                        {
                            t.Ligatures(false).Kerning(false);
                        }
                        else if (mode == "default-valued")
                        {
                            t.Kerning(true);
                        }
                    }
                });
            });
        });
    }

    public static int Run(int iterations)
    {
        var options = new PdfDocumentWriterOptions { DocumentId = new byte[16] };
        Console.WriteLine($"{"case",-18} {"mean ms",9} {"alloc KiB",10}  output hash");
        foreach (string mode in Cases)
        {
            for (int w = 0; w < 5; w++)
            {
                Compose(mode).ToBytes(options);
            }

            GC.Collect();
            long alloc = 0;
            double ms = 0;
            byte[]? last = null;
            for (int i = 0; i < iterations; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                last = Compose(mode).ToBytes(options);
                ms += clock.Elapsed.TotalMilliseconds;
                alloc += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Console.WriteLine($"{mode,-18} {ms / iterations,9:F2} {alloc / iterations / 1024.0,10:F0}  {ShortTextDocs.Hash(last!)[..16]}");
        }

        return 0;
    }
}
