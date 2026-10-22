using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Single-threaded sweep: opens every file of a directory tree and interprets up to the first pages of each into a display
/// list, replaying it into a balance checker. Reports typed errors, unexpected exceptions, unbalanced output and slow pages.
/// </summary>
internal static class InterpretSweep
{
    private const int MaxPages = 25;

    private static readonly Dictionary<string, int> ImageKinds = [];

    private sealed class Balance : PdfDevice
    {
        public int Clips;
        public int Others;
        public int Items;
        public bool CountImages = true;

        public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Clips++;

        public override void PushTextClip(in PdfGraphicsState state) => Clips++;

        public override void PopClip() => Clips--;

        public override void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state) => Others++;

        public override void EndGroup() => Others--;

        public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state) => Others++;

        public override void EndSoftMask() => Others--;

        public override void BeginMarkedContent(string tag, PdfDictionary? properties) => Others++;

        public override void EndMarkedContent() => Others--;

        public override void BeginAnnotation(in PdfAnnotationInfo annotation) => Others++;

        public override void EndAnnotation() => Others--;

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
        {
            Items++;
            if (!state.Ctm.IsFinite)
            {
                Others += 1000;
            }
        }

        public override void DrawImage(in PdfImage image, in PdfGraphicsState state)
        {
            // Builds the finished image (cached on the image), so decoding runs under the sweep like a real device's would.
            try
            {
                PdfImageData? d = image.GetData();
                if (CountImages)
                {
                    string kind = d is null ? "none" : !d.IsDecoded ? "encoded" : d.IsStencil ? "stencil" : "decoded";
                    ImageKinds[kind] = ImageKinds.GetValueOrDefault(kind) + 1;
                }
            }
            catch (PdfLimitException)
            {
                ImageKinds["limit"] = ImageKinds.GetValueOrDefault("limit") + 1;
            }
        }

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            Items++;
            if (!run.TextMatrix.IsFinite)
            {
                Others += 1000;
            }
        }
    }

    public static int Run(string[] args)
    {
        int files = 0, opened = 0, typed = 0, crashes = 0, unbalanced = 0, slow = 0, pages = 0, limitStops = 0;
        long items = 0;
        double worstPage = 0;
        var typedKinds = new Dictionary<string, int>();
        foreach (string f in Directory.EnumerateFiles(args[1], "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            files++;
            try
            {
                using PdfDocument doc = PdfDocument.Open(f);
                opened++;
                int n = Math.Min(doc.Pages.Count, MaxPages);
                for (int i = 0; i < n; i++)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var list = new PdfDisplayList();
                        doc.Pages[i].Interpret(list);
                        var b = new Balance();
                        list.Replay(b);
                        b.CountImages = false;
                        list.Replay(b, new PdfBounds(0, 0, 200, 200));
                        items += b.Items;
                        if (b.Clips != 0 || b.Others != 0)
                        {
                            unbalanced++;
                            Console.WriteLine($"UNBALANCED {f} page {i}: clips={b.Clips} others={b.Others}");
                        }

                        pages++;
                    }
                    catch (PdfException ex)
                    {
                        typed++;
                        if (ex is PdfLimitException)
                        {
                            limitStops++;
                        }

                        typedKinds[ex.GetType().Name] = typedKinds.GetValueOrDefault(ex.GetType().Name) + 1;
                    }

                    double t = sw.Elapsed.TotalSeconds;
                    worstPage = Math.Max(worstPage, t);
                    if (t > 10)
                    {
                        slow++;
                        Console.WriteLine($"SLOW {f} page {i}: {t:F1}s");
                    }
                }
            }
            catch (PdfException)
            {
                // Not openable: the open sweep covers those.
            }
#pragma warning disable CA1031 // The sweep must report any other exception as a crash.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                crashes++;
                Console.WriteLine($"CRASH {f}: {ex}");
            }
        }

        using Process p = Process.GetCurrentProcess();
        Console.WriteLine($"files={files} opened={opened} pages={pages} items={items} typed-errors={typed} ({string.Join(", ", typedKinds.Select(kv => kv.Key + "=" + kv.Value))}) crashes={crashes} unbalanced={unbalanced} slow={slow} worst-page={worstPage:F2}s peak-rss={p.PeakWorkingSet64 >> 20}MiB images=[{string.Join(", ", ImageKinds.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))}]");
        return crashes == 0 && unbalanced == 0 ? 0 : 1;
    }
}
