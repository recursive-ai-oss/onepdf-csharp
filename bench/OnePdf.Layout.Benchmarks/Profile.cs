using System;
using System.Diagnostics;
using System.IO;
using OnePdf;
using OnePdf.Layout;

namespace LayoutBench;

/// <summary>
/// <c>profile &lt;table10k|book500&gt;</c>: where OnePDF's time and allocation go for a row, split into building the element tree, shaping and
/// breaking every piece of text on its own (the public paragraph API, no pages), and the whole save. Single thread, so the allocation
/// counter is exact. The parts overlap by design: the full save includes the text work.
/// </summary>
internal static class Profile
{
    private static void Step(string name, Action work)
    {
        work(); // warm
        GC.Collect();
        long a = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        work();
        sw.Stop();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - a;
        Console.WriteLine($"{name,-34} {sw.Elapsed.TotalMilliseconds,9:F1} ms {bytes / 1048576.0,9:F1} MB");
    }

    private static System.Collections.Generic.IEnumerable<string[]> Rows()
    {
        for (int i = 0; i < Content.TableRows; i++)
        {
            yield return Content.TableRow(i);
        }
    }

    private static void SaveColumn(int count, Func<int, Element> make)
    {
        var col = new Column();
        for (int i = 0; i < count; i++)
        {
            col.Items.Add(make(i));
        }

        var doc = new LayoutDocument();
        doc.Options.Fonts = OnePdfEngine.Fonts;
        doc.Pages.Add(new LayoutPage { Margin = Insets.All(40), Content = col });
        doc.Save(new CountingStream());
    }

    public static int Run(string row)
    {
        TextFontRegistry fonts = OnePdfEngine.Fonts;
        switch (row)
        {
            case "table10k":
            {
                var cells = new string[Content.TableRows * 4];
                for (int i = 0; i < Content.TableRows; i++)
                {
                    Content.TableRow(i).CopyTo(cells, i * 4);
                }

                Step("generate the cell strings", () => { for (int i = 0; i < Content.TableRows; i++) { _ = Content.TableRow(i); } });
                Step("shape + break 40k one-line cells", () =>
                {
                    foreach (string c in cells)
                    {
                        _ = new TextParagraph(c, new TextStyle { Size = 10 }).Shape(fonts).Layout(100);
                    }
                });
                Step("full save (tree, layout, write)", () => new OnePdfEngine().Render(Workload.Table10k, new CountingStream()));
                Step("table, MeasuredRows = 20", () =>
                {
                    var t = (Table)OnePdfSeams.Table(["Id", "Item", "Amount", "State"], Rows(), [1, 4, 1.5, 1.5], [false, false, true, false], 10, true);
                    t.MeasuredRows = 20;
                    var doc = new LayoutDocument();
                    doc.Options.Fonts = OnePdfEngine.Fonts;
                    doc.Pages.Add(new LayoutPage { Margin = Insets.All(40), Content = t });
                    doc.Save(new CountingStream());
                });
                Step("save: 40k bare Text in one Column", () => SaveColumn(cells.Length, i => new Text { Content = cells[i], Size = 10 }));
                Step("save: 10k Rows of 4 bare Text", () => SaveColumn(Content.TableRows, i =>
                {
                    var r = new Row();
                    for (int c = 0; c < 4; c++)
                    {
                        r.Items.Add(RowItem.Relative(1, new Text { Content = cells[(i * 4) + c], Size = 10 }));
                    }

                    return r;
                }));
                Step("save: 10k Rows of 4 Box(Text)", () => SaveColumn(Content.TableRows, i =>
                {
                    var r = new Row();
                    for (int c = 0; c < 4; c++)
                    {
                        r.Items.Add(RowItem.Relative(1, new Box { Padding = Insets.Symmetric(4, 3), BorderBottom = new Border(0.5, LayoutColor.Black), Child = new Text { Content = cells[(i * 4) + c], Size = 10 } }));
                    }

                    return r;
                }));
                break;
            }

            case "invoice":
            {
                Step("one page, one word", () => SaveColumn(1, _ => new Text { Content = "x", Size = 10 }));
                Step("one page, one word, standard font", () => SaveColumn(1, _ => new Text { Content = "x", Size = 10, FontName = "Helvetica" }));
                Step("the invoice", () => new OnePdfEngine().Render(Workload.Invoice, new CountingStream()));
                break;
            }

            case "book500":
            {
                var rng = new Content.Rng(21);
                var paras = new string[Content.BookParagraphs];
                for (int i = 0; i < paras.Length; i++)
                {
                    paras[i] = Content.Paragraph(rng, 90 + rng.Next(60));
                }

                Step("shape 1900 paragraphs", () =>
                {
                    foreach (string p in paras)
                    {
                        _ = new TextParagraph(p, new TextStyle { Size = 11, Families = ["Liberation Serif"] }).Shape(fonts);
                    }
                });
                Step("shape + justify-break at 300 pt", () =>
                {
                    foreach (string p in paras)
                    {
                        _ = new TextParagraph(p, new TextStyle { Size = 11, Families = ["Liberation Serif"] }) { Alignment = TextAlignment.Justify }.Shape(fonts).Layout(300);
                    }
                });
                Step("full save (tree, layout, write)", () => new OnePdfEngine().Render(Workload.Book500, new CountingStream()));
                break;
            }

            case "invoice-phases":
            {
                // Allocation of the writer's own setup, an empty page, and the invoice's content, per document (averaged).
                const int n = 200;
                long Measure(Action a)
                {
                    for (int i = 0; i < 20; i++)
                    {
                        a();
                    }

                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < n; i++)
                    {
                        a();
                    }

                    return (GC.GetAllocatedBytesForCurrentThread() - before) / n;
                }

                Console.WriteLine("writer create+dispose        " + Measure(() => { using var w = new PdfDocumentWriter(new CountingStream()); }) + " B");
                Console.WriteLine("writer + finish, no pages    " + Measure(() => { using var w = new PdfDocumentWriter(new CountingStream()); w.Finish(); }) + " B");
                Console.WriteLine("one empty page               " + Measure(() => { using var w = new PdfDocumentWriter(new CountingStream()); w.BeginPage(595, 842).Commit(); w.Finish(); }) + " B");
                Console.WriteLine("invoice                      " + Measure(() => new OnePdfEngine().Render(Workload.Invoice, new CountingStream())) + " B");
                Console.WriteLine("invoice, untagged            " + Measure(() => { LayoutDocument d = OnePdfEngine.InvoiceDocument(); d.Options.Fonts = OnePdfEngine.Fonts; d.Options.Tagged = false; d.Save(new CountingStream()); }) + " B");
                Console.WriteLine("invoice, build tree only     " + Measure(() => { _ = OnePdfEngine.InvoiceDocument(); }) + " B");
                break;
            }

            case "invoice-loop":
            {
                // For a sampling profiler: many invoices in a row, so the per-document cost dominates.
                var e = new OnePdfEngine();
                for (int i = 0; i < 300; i++)
                {
                    e.Render(Workload.Invoice, new CountingStream());
                }

                long a0 = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 3000; i++)
                {
                    e.Render(Workload.Invoice, new CountingStream());
                }

                Console.WriteLine($"invoice: {sw.Elapsed.TotalMilliseconds / 3000:F3} ms, {(GC.GetAllocatedBytesForCurrentThread() - a0) / 3000 / 1024.0:F0} KB per document");

                break;
            }

            case "alloc-table10k":
            case "alloc-book500":
            case "alloc-invoice":
            {
                Workload w = row switch { "alloc-table10k" => Workload.Table10k, "alloc-book500" => Workload.Book500, _ => Workload.Invoice };
                int reps = w == Workload.Invoice ? 300 : 1;
                using var listener = new AllocListener();
                for (int i = 0; i < reps; i++)
                {
                    new OnePdfEngine().Render(w, new CountingStream());
                }

                listener.Print(45);
                break;
            }

            default:
                Console.Error.WriteLine("usage: profile <invoice|invoice-loop|table10k|book500>");
                return 2;
        }

        return 0;
    }
}
