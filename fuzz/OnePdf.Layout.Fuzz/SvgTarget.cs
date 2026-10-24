using System;
using System.IO;
using System.Linq;
using OnePdf;
using OnePdf.Layout;

namespace OnePdf.Layout.Fuzz;

// Fuzz target `svg`: the input is an SVG document (any bytes). Invariants: only the library's typed exceptions come out; the picture
// laid out in a tagged document saves, saves to the same bytes a second time, and reopens; extraction of the page never throws.
// Limits are tightened so one input stays cheap.
internal static class SvgTarget
{
    private static readonly SvgLimits s_limits = new()
    {
        MaxDocumentBytes = 256 * 1024,
        MaxElements = 20_000,
        MaxInstances = 20_000,
        MaxPathSegments = 100_000,
        MaxCssBytes = 64 * 1024,
        MaxCssRules = 1_000,
        MaxCssMatchSteps = 2_000_000,
        MaxImageBytes = 64 * 1024,
        MaxTotalImageBytes = 128 * 1024,
        MaxTextCharacters = 5_000,
    };

    public static void Run(ReadOnlySpan<byte> data)
    {
        SvgImage svg;
        try
        {
            svg = SvgImage.Parse(data, s_limits);
        }
        catch (PdfException)
        {
            return;
        }

        byte[] first;
        try
        {
            first = Save(svg, tagged: (data.Length & 1) == 0);
        }
        catch (PdfException)
        {
            return;
        }

        byte[] second = Save(svg, tagged: (data.Length & 1) == 0);
        if (!first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidOperationException("The same SVG gave different bytes on a second run.");
        }

        using PdfDocument back = PdfDocument.Open(first);
        _ = back.ExtractText().Select(t => t.Text.Length).Sum();
    }

    private static byte[] Save(SvgImage svg, bool tagged)
    {
        var doc = new LayoutDocument { Title = "Fuzz", Language = "en" };
        doc.Options.Tagged = tagged;
        doc.Options.MaxPages = 4;
        var column = new Column();
        column.Items.Add(new SvgPicture { Source = svg, Fit = ImageFit.Contain, AltText = "fuzz" });
        doc.Pages.Add(new LayoutPage { Size = new PageSize(300, 400), Margin = Insets.All(20), Content = column });
        using var ms = new MemoryStream();
        doc.Save(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true });
        return ms.ToArray();
    }

    /// <summary>Starting inputs: the committed sample SVGs.</summary>
    public static int Seeds(string outDir, string svgDir)
    {
        Directory.CreateDirectory(outDir);
        if (Directory.Exists(svgDir))
        {
            foreach (string f in Directory.EnumerateFiles(svgDir, "*.svg"))
            {
                File.Copy(f, Path.Combine(outDir, Path.GetFileName(f)), overwrite: true);
            }
        }

        return 0;
    }
}
