using System;
using System.IO;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using OnePdf;
using OnePdf.Layout;

namespace LayoutBench;

/// <summary>The variants of the short-text workload: one A4 page flow holding a table of one-word cells (or a column of one-word items).</summary>
public enum ShortTextVariant
{
    /// <summary>Plain <c>Text(v)</c> in a cell.</summary>
    Plain,

    /// <summary>Plus font size, colour and line height.</summary>
    Styled,

    /// <summary>Styled, with 3 pt padding around each cell's text.</summary>
    Padded,

    /// <summary>Styled and padded, with a background colour on each cell's text.</summary>
    Background,

    /// <summary>A column with one item per string instead of a table.</summary>
    Column,
}

/// <summary>Builds the short-text documents. Strings repeat across the document, as in real reports.</summary>
internal static class ShortTextDocs
{
    private static readonly string[] Dates = ["22.12.2023.", "23.12.2023.", "27.12.2023.", "02.01.2024."];
    private static readonly string[] Amounts = ["678.784.474", "12.500", "1.034.220", "99.001.350", "4.020"];
    private static readonly string[] Codes = ["ISK", "EUR", "USD"];

    private static readonly byte[] Font = File.ReadAllBytes(Content.FontFile("LiberationSans-Regular.ttf"));

    public static string Cell(int row, int column) => column switch
    {
        0 => Dates[row % Dates.Length],
        1 => Amounts[row % Amounts.Length],
        _ => Codes[row % Codes.Length],
    };

    public static byte[] Make(ShortTextVariant variant, int rows, bool tagged) =>
        Compose(variant, rows, tagged).ToBytes(new PdfDocumentWriterOptions { DocumentId = new byte[16] });

    public static LayoutDocument Compose(ShortTextVariant variant, int rows, bool tagged)
    {
        var grey = new LayoutColor(0.2, 0.2, 0.2);
        var tint = new LayoutColor(0.95, 0.95, 0.9);
        var doc = LayoutDocument.Compose(d =>
        {
            d.Tagged(tagged);
            d.Fonts.Add(Font, "Report Sans");
            d.Fonts.DefaultFamily = "Report Sans";
            d.Page(p =>
            {
                p.Size(PageSize.A4);
                p.Margin(36);
                if (variant == ShortTextVariant.Column)
                {
                    p.Content().Column(col =>
                    {
                        for (int r = 0; r < rows; r++)
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                col.Item().Text(Cell(r, c)).FontSize(8).FontColor(grey).LineHeight(1.2);
                            }
                        }
                    });
                    return;
                }

                p.Content().Table(t =>
                {
                    t.Columns(c =>
                    {
                        c.Relative();
                        c.Relative();
                        c.Relative();
                    });
                    t.CellPadding(0);
                    for (int r = 0; r < rows; r++)
                    {
                        int row = r;
                        t.Row(tr =>
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                string v = Cell(row, c);
                                TableCellSlot cell = tr.Cell();
                                switch (variant)
                                {
                                    case ShortTextVariant.Plain:
                                        cell.Text(v).FontSize(8);
                                        break;
                                    case ShortTextVariant.Styled:
                                        cell.Text(v).FontSize(8).FontColor(grey).LineHeight(1.2);
                                        break;
                                    case ShortTextVariant.Padded:
                                        cell.Padding(3).Text(v).FontSize(8).FontColor(grey).LineHeight(1.2);
                                        break;
                                    default:
                                        cell.Background(tint).Padding(3).Text(v).FontSize(8).FontColor(grey).LineHeight(1.2);
                                        break;
                                }
                            }
                        });
                    }
                });
            });
        });
        return doc;
    }

    public static string Hash(byte[] pdf) => Convert.ToHexString(SHA256.HashData(pdf));
}

/// <summary>Cost per one-word text element: run with 150 and 600 rows and take the difference.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("layout", "shorttext")]
[SimpleJob(warmupCount: 2, iterationCount: 8)]
public class ShortTextBenchmarks
{
    [Params(150, 600)]
    public int Rows { get; set; }

    [Params(false, true)]
    public bool Tagged { get; set; }

    [Params(ShortTextVariant.Plain, ShortTextVariant.Styled, ShortTextVariant.Padded, ShortTextVariant.Background, ShortTextVariant.Column)]
    public ShortTextVariant Variant { get; set; }

    [Benchmark]
    public int Build() => ShortTextDocs.Make(Variant, Rows, Tagged).Length;
}
