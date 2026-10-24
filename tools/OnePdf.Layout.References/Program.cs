using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneImage;
using OneImage.Pdf;

// Renders the layout reference documents to PNG with the published OneImage PDF renderer, used as a black box, and compares fresh
// renders with the committed references. This is a separate process that knows nothing about the layout package: it only reads PDFs.
//
//   render  <documents dir> <png dir> [dpi]    writes <name>-p<N>.png for every page and manifest.csv
//   compare <documents dir> <png dir> [dpi]    renders again and reports, per page, how far it is from the committed PNG
//
// It is run by hand at milestones and releases, not in CI.
if (args.Length < 3 || args[0] is not ("render" or "compare"))
{
    Console.Error.WriteLine("usage: OnePdf.Layout.References render|compare <documents dir> <png dir> [dpi]");
    return 2;
}

string mode = args[0];
string documents = args[1];
string pngDir = args[2];
int dpi = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 72;
Directory.CreateDirectory(pngDir);

var load = new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = dpi })) };
string[] files = [.. Directory.EnumerateFiles(documents, "*.pdf").Order(StringComparer.Ordinal)];
if (files.Length == 0)
{
    Console.Error.WriteLine("no PDF files in " + documents);
    return 2;
}

Dictionary<string, string> descriptions = ReadDescriptions(Path.Combine(documents, "index.csv"));
var manifest = new StringBuilder("png,document,description,generator,renderer,dpi,png_sha256,document_sha256\n");
int pages = 0;
int differing = 0;
int missing = 0;
double worstFraction = 0;

foreach (string file in files)
{
    string name = Path.GetFileNameWithoutExtension(file);
    string docHash = Hex(SHA256.HashData(File.ReadAllBytes(file)));
    Image pdf = Image.Load(file, load);
    for (int i = 0; i < pdf.Frames.Count; i++)
    {
        pages++;
        string png = $"{name}-p{i + 1}.png";
        string path = Path.Combine(pngDir, png);
        Image page = pdf.Frames[i].ToImage();
        if (mode == "render")
        {
            page.Save(path);
            manifest.Append(CultureInfo.InvariantCulture, $"{png},{name}.pdf,\"{descriptions.GetValueOrDefault(name + ".pdf", string.Empty)}\",tools/OnePdf.Layout.Documents,RecursiveAI.OneImage.Pdf 1.7.0,{dpi},{Hex(SHA256.HashData(File.ReadAllBytes(path)))},{docHash}\n");
            Console.WriteLine($"{png}  {page.Width}x{page.Height}");
            continue;
        }

        if (!File.Exists(path))
        {
            missing++;
            Console.WriteLine($"MISSING   {png}");
            continue;
        }

        Image reference = Image.Load(path);
        if (reference.Width != page.Width || reference.Height != page.Height)
        {
            differing++;
            worstFraction = 1;
            Console.WriteLine($"SIZE      {png}: reference {reference.Width}x{reference.Height}, now {page.Width}x{page.Height}");
            continue;
        }

        Rgba32[] a = reference.ToArray<Rgba32>();
        Rgba32[] b = page.ToArray<Rgba32>();
        long over = 0;
        int max = 0;
        long sum = 0;
        for (int p = 0; p < a.Length; p++)
        {
            int d = Math.Max(Math.Max(Math.Abs(a[p].R - b[p].R), Math.Abs(a[p].G - b[p].G)), Math.Max(Math.Abs(a[p].B - b[p].B), Math.Abs(a[p].A - b[p].A)));
            sum += d;
            max = Math.Max(max, d);
            if (d > 16)
            {
                over++;
            }
        }

        double fraction = a.Length == 0 ? 0 : (double)over / a.Length;
        worstFraction = Math.Max(worstFraction, fraction);
        bool same = max == 0;
        if (!same && fraction > 0.0005)
        {
            differing++;
        }

        Console.WriteLine($"{(same ? "identical" : fraction > 0.0005 ? "DIFFERS  " : "close    ")} {png}: max channel difference {max}, mean {(double)sum / Math.Max(1, a.Length):F4}, pixels beyond 16: {fraction:P3}");
    }
}

if (mode == "render")
{
    File.WriteAllText(Path.Combine(pngDir, "manifest.csv"), manifest.ToString());
    Console.WriteLine($"{pages} page(s) rendered at {dpi} dpi");
    return 0;
}

Console.WriteLine($"{pages} page(s) compared: {differing} differ, {missing} missing; worst fraction of differing pixels {worstFraction:P3}");
return differing == 0 && missing == 0 ? 0 : 1;

static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

static Dictionary<string, string> ReadDescriptions(string index)
{
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!File.Exists(index))
    {
        return map;
    }

    foreach (string line in File.ReadLines(index).Skip(1))
    {
        int c1 = line.IndexOf(',', StringComparison.Ordinal);
        int c2 = line.IndexOf(',', c1 + 1);
        if (c1 > 0 && c2 > c1)
        {
            map[line[..c1]] = line[(c2 + 1)..].Trim('"');
        }
    }

    return map;
}
