using System.Globalization;
using System.Text;
using Image = OneImage.Image;
using Rgba32 = OneImage.Rgba32;
using OneImage;
using OneImage.Svg;
using OnePdf;
using OnePdf.Layout;

// Compares the layout package's SVG output with the published OneImage SVG renderer, used as a black box.
//
//   pdf    <svg dir> <pdf dir>               each SVG becomes a one-page PDF exactly as large as the picture (through Slot.Svg)
//   raster <svg dir> <png dir>               each SVG is rasterised by RecursiveAI.OneImage.Svg at 96 pixels per inch
//   diff   <png dir A> <png dir B> [limit]   per-file difference between two sets of PNG files of the same names
//
// Render the PDFs with tools/OnePdf.Layout.References at 96 dpi to get PNG files the same size as the rasterised SVGs.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: pdf|raster <svg dir> <out dir> | diff <dir A> <dir B> [fraction limit]");
    return 2;
}

string mode = args[0];
string first = args[1];
string second = args[2];

if (mode == "pdf")
{
    Directory.CreateDirectory(second);
    foreach (string path in Directory.EnumerateFiles(first, "*.svg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        string name = FlatName(first, path);
        try
        {
            SvgImage svg = SvgImage.Parse(File.ReadAllBytes(path));
            LayoutDocument doc = LayoutDocument.Compose(d => d.Page(p =>
            {
                p.Size(svg.Width, svg.Height);
                if (svg.Width > svg.Height)
                {
                    p.Landscape();
                }

                p.Margin(0);
                p.Content().Svg(svg).Stretch().AltText(name);
            }));
            using FileStream file = File.Create(Path.Combine(second, name + ".pdf"));
            doc.Save(file, new PdfDocumentWriterOptions { DocumentId = new byte[16] });
            foreach (LayoutDiagnostic note in doc.Diagnostics)
            {
                Console.WriteLine($"{name}: {note.Message}");
            }
        }
        catch (Exception e) when (e is PdfException or PdfLayoutException or ArgumentException)
        {
            Console.WriteLine($"FAILED {name}: {e.GetType().Name}: {e.Message}");
        }
    }

    return 0;
}

if (mode == "raster")
{
    Directory.CreateDirectory(second);
    foreach (string path in Directory.EnumerateFiles(first, "*.svg", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        string name = FlatName(first, path);
        try
        {
            // One pixel per CSS pixel, which is what the 96 dpi render of the PDF page has; a width argument scales both sets alike.
            Image img = Image.Load(path, new LoadOptions { Configuration = new ImageConfiguration().Add(SvgFormat.Instance) });
            if (args.Length > 3)
            {
                img = img.Resize(width: int.Parse(args[3], CultureInfo.InvariantCulture));
            }

            img.Save(Path.Combine(second, name + ".png"));
        }
        catch (Exception e) when (e is ImageException or IOException or ArgumentException)
        {
            Console.WriteLine($"FAILED {name}: {e.GetType().Name}: {e.Message}");
        }
    }

    return 0;
}

if (mode == "diff")
{
    double limit = args.Length > 3 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 0.02;
    int level = args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 48;
    int pass = 0;
    int total = 0;
    var byCategory = new SortedDictionary<string, (int Pass, int Total)>(StringComparer.Ordinal);
    var report = new StringBuilder();
    foreach (string pathA in Directory.EnumerateFiles(first, "*.png").Order(StringComparer.Ordinal))
    {
        string name = Path.GetFileName(pathA);
        // The References tool names PDF renders <name>-p1.png.
        string pathB = Path.Combine(second, name);
        if (!File.Exists(pathB))
        {
            pathB = Path.Combine(second, Path.GetFileNameWithoutExtension(name) + "-p1.png");
        }

        total++;
        string[] parts = Path.GetFileNameWithoutExtension(name).Split("__");
        string category = parts.Length > 2 ? parts[0] + "/" + parts[1] : parts[0];
        byCategory.TryGetValue(category, out (int Pass, int Total) tally);
        byCategory[category] = (tally.Pass, tally.Total + 1);
        if (!File.Exists(pathB))
        {
            Console.WriteLine($"MISSING {name}");
            continue;
        }

        Image a = Image.Load(pathA);
        Image b = Image.Load(pathB);
        if (Math.Abs(a.Width - b.Width) > 1 || Math.Abs(a.Height - b.Height) > 1)
        {
            Console.WriteLine($"SIZE    {name}: {a.Width}x{a.Height} against {b.Width}x{b.Height}");
            continue;
        }

        int w = Math.Min(a.Width, b.Width);
        int h = Math.Min(a.Height, b.Height);
        Rgba32[] pa = a.ToArray<Rgba32>();
        Rgba32[] pb = b.ToArray<Rgba32>();
        long over = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba32 ca = Flatten(pa[(y * a.Width) + x]);
                Rgba32 cb = Flatten(pb[(y * b.Width) + x]);
                int d = Math.Max(Math.Max(Math.Abs(ca.R - cb.R), Math.Abs(ca.G - cb.G)), Math.Abs(ca.B - cb.B));
                if (d > level)
                {
                    over++;
                }
            }
        }

        double fraction = (double)over / (w * h);
        bool ok = fraction <= limit;
        if (ok)
        {
            pass++;
            byCategory[category] = (byCategory[category].Pass + 1, byCategory[category].Total);
        }

        Console.WriteLine($"{(ok ? "ok     " : "DIFFERS")} {name}: {fraction:P2} of {w}x{h} pixels differ by more than {level} levels");
    }

    foreach ((string cat, (int p, int t)) in byCategory)
    {
        Console.WriteLine($"CATEGORY {cat}: {p} of {t} ({(double)p / t:P0})");
    }

    Console.WriteLine($"{pass} of {total} within {limit:P1} (a pixel differs when a channel is off by more than {level} of 255)");
    return pass == total ? 0 : 1;
}

return 2;

static string FlatName(string root, string path) => Path.ChangeExtension(Path.GetRelativePath(root, path), null).Replace(Path.DirectorySeparatorChar.ToString(), "__", StringComparison.Ordinal);

// Both renderers leave the background transparent where nothing is drawn (the PDF page is white): compare on white.
static Rgba32 Flatten(Rgba32 c)
{
    int a = c.A;
    return new Rgba32((byte)(((c.R * a) + (255 * (255 - a))) / 255), (byte)(((c.G * a) + (255 * (255 - a))) / 255), (byte)(((c.B * a) + (255 * (255 - a))) / 255), 255);
}
