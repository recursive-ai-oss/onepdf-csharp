using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using OneImage;
using OneImage.Pdf;

// Checks that two PDFs look the same. Every page of both files is rendered by two independent renderers, used as black boxes:
// the published OneImage PDF renderer (in process) and poppler's pdftoppm (a separate process, when installed). Pages are compared
// pixel by pixel. A rewrite that only adds marked content, a structure tree or metadata must leave every pixel alone.
//
//   OnePdf.RenderCompare <a.pdf> <b.pdf> [--dpi 150] [--out dir]
//   OnePdf.RenderCompare --self-test [--dpi 150] [--out dir]
//
// Exit codes: 0 every page identical in every renderer that ran, 1 a difference, 2 a usage or rendering error.
const int DefaultDpi = 150;

try
{
    return Run(args);
}
catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 2;
}

static int Run(string[] args)
{
    int dpi = DefaultDpi;
    string? outDir = null;
    bool selfTest = false;
    var files = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--dpi" when i + 1 < args.Length:
                dpi = int.Parse(args[++i], CultureInfo.InvariantCulture);
                break;
            case "--out" when i + 1 < args.Length:
                outDir = args[++i];
                break;
            case "--self-test":
                selfTest = true;
                break;
            default:
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    return Usage();
                }

                files.Add(args[i]);
                break;
        }
    }

    if (dpi is < 20 or > 600)
    {
        Console.Error.WriteLine("--dpi must be between 20 and 600");
        return 2;
    }

    if (selfTest)
    {
        return files.Count == 0 ? SelfTest(dpi, outDir) : Usage();
    }

    if (files.Count != 2)
    {
        return Usage();
    }

    return Compare(files[0], files[1], dpi, outDir) ? 0 : 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: OnePdf.RenderCompare <a.pdf> <b.pdf> [--dpi 150] [--out dir]\n       OnePdf.RenderCompare --self-test [--dpi 150] [--out dir]");
    return 2;
}

// Compares two files in every available renderer and prints one line per renderer plus one per differing page.
static bool Compare(string a, string b, int dpi, string? outDir)
{
    bool allSame = true;
    Console.WriteLine($"a: {a}\nb: {b}\ndpi: {dpi}");
    foreach (IRenderer renderer in Renderers())
    {
        List<Frame> pa = renderer.Render(a, dpi);
        List<Frame> pb = renderer.Render(b, dpi);
        bool countEqual = pa.Count == pb.Count;
        int differing = 0;
        long totalPixels = 0;
        long differingPixels = 0;
        int maxDelta = 0;
        int pages = Math.Min(pa.Count, pb.Count);
        for (int p = 0; p < pages; p++)
        {
            PageDiff d = Diff(pa[p], pb[p]);
            totalPixels += d.Pixels;
            differingPixels += d.Differing;
            maxDelta = Math.Max(maxDelta, d.MaxDelta);
            if (d.Differing > 0 || d.SizeMismatch)
            {
                differing++;
                string size = d.SizeMismatch ? $" (size {pa[p].Width}x{pa[p].Height} vs {pb[p].Width}x{pb[p].Height})" : string.Empty;
                Console.WriteLine($"  {renderer.Name} page {p + 1}: {d.Differing} of {d.Pixels} pixels differ, max channel delta {d.MaxDelta}{size}");
                if (outDir is not null)
                {
                    Directory.CreateDirectory(outDir);
                    string path = Path.Combine(outDir, $"{renderer.Name}-p{p + 1}-diff.png");
                    WriteDiffPng(path, pa[p], pb[p]);
                    Console.WriteLine("    diff image: " + path);
                }
            }
        }

        bool same = countEqual && differing == 0;
        allSame &= same;
        Console.WriteLine($"{renderer.Name}: {(same ? "IDENTICAL" : "DIFFERENT")}; pages {pa.Count} vs {pb.Count} ({(countEqual ? "equal" : "NOT EQUAL")}); pages differing {differing}; pixels differing {differingPixels} of {totalPixels}; max channel delta {maxDelta}");
    }

    return allSame;
}

static IEnumerable<IRenderer> Renderers()
{
    yield return new OneImageRenderer();
    if (PopplerRenderer.Available)
    {
        yield return new PopplerRenderer();
    }
    else
    {
        Console.WriteLine("poppler: pdftoppm not found, skipped");
    }
}

static PageDiff Diff(Frame a, Frame b)
{
    if (a.Width != b.Width || a.Height != b.Height)
    {
        return new PageDiff(Math.Max((long)a.Width * a.Height, (long)b.Width * b.Height), Math.Max((long)a.Width * a.Height, (long)b.Width * b.Height), 255, true);
    }

    long differing = 0;
    int max = 0;
    byte[] x = a.Rgba;
    byte[] y = b.Rgba;
    for (int i = 0; i < x.Length; i += 4)
    {
        int d = 0;
        for (int c = 0; c < 4; c++)
        {
            d = Math.Max(d, Math.Abs(x[i + c] - y[i + c]));
        }

        if (d != 0)
        {
            differing++;
            max = Math.Max(max, d);
        }
    }

    return new PageDiff((long)a.Width * a.Height, differing, max, false);
}

// The diff image shows page a faded, with every differing pixel in red, so a viewer sees where the change is.
static void WriteDiffPng(string path, Frame a, Frame b)
{
    int w = Math.Max(a.Width, b.Width);
    int h = Math.Max(a.Height, b.Height);
    var raw = new byte[h * (w * 3 + 1)];
    for (int y = 0; y < h; y++)
    {
        int row = y * (w * 3 + 1);
        raw[row] = 0;
        for (int x = 0; x < w; x++)
        {
            bool inA = x < a.Width && y < a.Height;
            bool inB = x < b.Width && y < b.Height;
            int o = row + 1 + x * 3;
            bool differs = !(inA && inB);
            if (!differs)
            {
                int ia = (y * a.Width + x) * 4;
                int ib = (y * b.Width + x) * 4;
                for (int c = 0; c < 4 && !differs; c++)
                {
                    differs = a.Rgba[ia + c] != b.Rgba[ib + c];
                }
            }

            if (differs)
            {
                raw[o] = 255;
            }
            else
            {
                int ia = (y * a.Width + x) * 4;
                byte faded = (byte)(255 - ((255 - a.Rgba[ia]) / 3));
                raw[o] = raw[o + 1] = raw[o + 2] = faded;
            }
        }
    }

    using var file = File.Create(path);
    file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    var ihdr = new byte[13];
    WriteBe(ihdr, 0, w);
    WriteBe(ihdr, 4, h);
    ihdr[8] = 8;
    ihdr[9] = 2;
    Chunk(file, "IHDR", ihdr);
    using var z = new MemoryStream();
    using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true))
    {
        zs.Write(raw);
    }

    Chunk(file, "IDAT", z.ToArray());
    Chunk(file, "IEND", []);
}

static void WriteBe(byte[] buffer, int at, int value)
{
    buffer[at] = (byte)(value >> 24);
    buffer[at + 1] = (byte)(value >> 16);
    buffer[at + 2] = (byte)(value >> 8);
    buffer[at + 3] = (byte)value;
}

static void Chunk(Stream s, string type, byte[] data)
{
    var head = new byte[4];
    WriteBe(head, 0, data.Length);
    s.Write(head);
    byte[] t = Encoding.ASCII.GetBytes(type);
    s.Write(t);
    s.Write(data);
    uint crc = 0xFFFFFFFF;
    foreach (byte v in t)
    {
        crc = Crc(crc, v);
    }

    foreach (byte v in data)
    {
        crc = Crc(crc, v);
    }

    WriteBe(head, 0, unchecked((int)~crc));
    s.Write(head);
}

static uint Crc(uint crc, byte v)
{
    crc ^= v;
    for (int k = 0; k < 8; k++)
    {
        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
    }

    return crc;
}

// The self-test proves the comparison can see what it must and ignore what it must: a copy is identical, content wrapped in
// BDC/EMC and /Artifact BMC/EMC is identical, and one changed operand or a one-unit shift is detected in each renderer.
static int SelfTest(int dpi, string? outDir)
{
    string dir = Path.Combine(Path.GetTempPath(), "onepdf-rendercompare-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    Directory.CreateDirectory(dir);
    try
    {
        const string Body = "0.8 0.9 1 rg 50 50 300 120 re f BT /F1 24 Tf 72 700 Td (Heading text) Tj 0 -40 Td /F1 12 Tf (Body text of a paragraph.) Tj ET 0 0 1 rg 72 600 200 50 re f 1 0 0 RG 3 w 72 520 m 300 560 l S";
        string basePath = Path.Combine(dir, "base.pdf");
        File.WriteAllBytes(basePath, MakePdf(Body, 2));
        string copy = Path.Combine(dir, "copy.pdf");
        File.Copy(basePath, copy);
        string wrapped = Path.Combine(dir, "wrapped.pdf");
        File.WriteAllBytes(wrapped, MakePdf("/Artifact BMC 0.8 0.9 1 rg 50 50 300 120 re f EMC /P <</MCID 0>> BDC BT /F1 24 Tf 72 700 Td (Heading text) Tj 0 -40 Td /F1 12 Tf (Body text of a paragraph.) Tj ET EMC /Figure <</MCID 1 /Alt (box)>> BDC 0 0 1 rg 72 600 200 50 re f EMC /Artifact <</Type /Pagination>> BDC 1 0 0 RG 3 w 72 520 m 300 560 l S EMC", 2));
        string color = Path.Combine(dir, "color.pdf");
        File.WriteAllBytes(color, MakePdf(Body.Replace("0 0 1 rg", "0 1 0 rg", StringComparison.Ordinal), 2));
        string shifted = Path.Combine(dir, "shifted.pdf");
        File.WriteAllBytes(shifted, MakePdf(Body.Replace("72 600 200 50 re", "73 600 200 50 re", StringComparison.Ordinal), 2));
        string pages = Path.Combine(dir, "pages.pdf");
        File.WriteAllBytes(pages, MakePdf(Body, 3));

        bool ok = true;
        ok &= Expect("identical copy", true, basePath, copy);
        ok &= Expect("BDC/EMC and artifact wrapping", true, basePath, wrapped);
        ok &= Expect("changed colour operand", false, basePath, color);
        ok &= Expect("one-unit shift of a rectangle", false, basePath, shifted);
        ok &= Expect("extra page", false, basePath, pages);
        Console.WriteLine(ok ? "self-test passed" : "self-test FAILED");
        return ok ? 0 : 1;
    }
    finally
    {
        Directory.Delete(dir, recursive: true);
    }

    bool Expect(string name, bool identical, string x, string y)
    {
        Console.WriteLine($"-- {name}: expecting {(identical ? "identical" : "different")}");
        bool result = Compare(x, y, dpi, identical ? null : outDir is null ? null : Path.Combine(outDir, name.Replace(' ', '-').Replace('/', '_')));
        return result == identical;
    }
}

// A small valid PDF: Helvetica text, a filled box and a line on each of pageCount pages. All offsets are exact.
static byte[] MakePdf(string content, int pageCount)
{
    var objects = new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [" + string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{4 + i * 2} 0 R")) + $"] /Count {pageCount} >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    };
    for (int i = 0; i < pageCount; i++)
    {
        objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 800] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
        objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
    }

    var sb = new StringBuilder("%PDF-1.7\n");
    var offsets = new List<int>();
    for (int i = 0; i < objects.Count; i++)
    {
        offsets.Add(sb.Length);
        sb.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }

    int xref = sb.Length;
    sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
    foreach (int o in offsets)
    {
        sb.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
    }

    sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    return Encoding.Latin1.GetBytes(sb.ToString());
}

internal sealed record Frame(int Width, int Height, byte[] Rgba);

internal readonly record struct PageDiff(long Pixels, long Differing, int MaxDelta, bool SizeMismatch);

internal interface IRenderer
{
    string Name { get; }

    List<Frame> Render(string path, int dpi);
}

// The published renderer, called through its public API only.
internal sealed class OneImageRenderer : IRenderer
{
    public string Name => "oneimage";

    public List<Frame> Render(string path, int dpi)
    {
        var load = new LoadOptions { Configuration = new ImageConfiguration().Add(new PdfFormat(new PdfRenderOptions { Dpi = dpi })) };
        Image pdf = Image.Load(path, load);
        var frames = new List<Frame>(pdf.Frames.Count);
        for (int i = 0; i < pdf.Frames.Count; i++)
        {
            Image page = pdf.Frames[i].ToImage();
            Rgba32[] pixels = page.ToArray<Rgba32>();
            var rgba = new byte[pixels.Length * 4];
            for (int p = 0; p < pixels.Length; p++)
            {
                rgba[p * 4] = pixels[p].R;
                rgba[p * 4 + 1] = pixels[p].G;
                rgba[p * 4 + 2] = pixels[p].B;
                rgba[p * 4 + 3] = pixels[p].A;
            }

            frames.Add(new Frame(page.Width, page.Height, rgba));
        }

        return frames;
    }
}

// poppler's pdftoppm in a child process, one binary PPM per page.
internal sealed class PopplerRenderer : IRenderer
{
    private const string Exe = "/usr/bin/pdftoppm";

    public static bool Available => File.Exists(Exe);

    public string Name => "poppler";

    public List<Frame> Render(string path, int dpi)
    {
        string dir = Path.Combine(Path.GetTempPath(), "onepdf-poppler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var psi = new ProcessStartInfo(Exe) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(dpi.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add(Path.GetFullPath(path));
            psi.ArgumentList.Add(Path.Combine(dir, "p"));
            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("could not start pdftoppm");
            Task<string> err = process.StandardError.ReadToEndAsync();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"pdftoppm failed on {path}: {err.GetAwaiter().GetResult().Trim()}");
            }

            // Names are p-<page>.ppm with a zero-padded page number of varying width, so order by the number.
            return [.. Directory.EnumerateFiles(dir, "p-*.ppm")
                .OrderBy(f => int.Parse(Path.GetFileNameWithoutExtension(f).AsSpan(2), CultureInfo.InvariantCulture))
                .Select(f => ReadPpm(File.ReadAllBytes(f)))];
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Frame ReadPpm(byte[] data)
    {
        int pos = 0;
        string Token()
        {
            while (pos < data.Length && (data[pos] <= ' ' || data[pos] == '#'))
            {
                if (data[pos] == '#')
                {
                    while (pos < data.Length && data[pos] != '\n')
                    {
                        pos++;
                    }
                }
                else
                {
                    pos++;
                }
            }

            int start = pos;
            while (pos < data.Length && data[pos] > ' ')
            {
                pos++;
            }

            return Encoding.ASCII.GetString(data, start, pos - start);
        }

        if (Token() != "P6")
        {
            throw new FormatException("pdftoppm did not write a binary PPM");
        }

        int w = int.Parse(Token(), CultureInfo.InvariantCulture);
        int h = int.Parse(Token(), CultureInfo.InvariantCulture);
        if (Token() != "255")
        {
            throw new FormatException("unexpected PPM depth");
        }

        pos++;
        if (w <= 0 || h <= 0 || (long)w * h * 3 > data.Length - pos)
        {
            throw new FormatException("truncated PPM");
        }

        var rgba = new byte[w * h * 4];
        for (int p = 0; p < w * h; p++)
        {
            rgba[p * 4] = data[pos + p * 3];
            rgba[p * 4 + 1] = data[pos + p * 3 + 1];
            rgba[p * 4 + 2] = data[pos + p * 3 + 2];
            rgba[p * 4 + 3] = 255;
        }

        return new Frame(w, h, rgba);
    }
}
