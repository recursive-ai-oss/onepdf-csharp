using System.Runtime.CompilerServices;
using System.Text;

namespace OnePdf.Tests.Content;

/// <summary>
/// Interprets every page of the committed test files. Snapshots of the generated set and the PDF 2.0 examples live in
/// <c>Snapshots/</c>; set ONEPDF_UPDATE_SNAPSHOTS=1 to rewrite them after an intended change (and bump the format version
/// when the format itself changed).
/// </summary>
public class DataSetTests
{
    private static string? TestData([CallerFilePath] string source = "")
    {
        for (string? d = Path.GetDirectoryName(source); d is not null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "testdata");
            if (Directory.Exists(c))
            {
                return c;
            }
        }

        return null;
    }

    private static string SnapshotDir([CallerFilePath] string source = "") => Path.Combine(Path.GetDirectoryName(source)!, "Snapshots");

    private static IEnumerable<string> Files(string sub) =>
        TestData() is { } root && Directory.Exists(Path.Combine(root, sub))
            ? Directory.EnumerateFiles(Path.Combine(root, sub), "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            : [];

    private static PdfDocument? TryOpen(string file)
    {
        try
        {
            return PdfDocument.Open(file);
        }
        catch (PdfException)
        {
            return null;
        }
    }

    private static string Snapshot(PdfDocument doc, int degree)
    {
        int n = doc.Pages.Count;
        var texts = new string[n];
        if (degree <= 1)
        {
            for (int i = 0; i < n; i++)
            {
                texts[i] = TestPdf.Record(doc, i).ToText();
            }
        }
        else
        {
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = degree }, i => texts[i] = TestPdf.Record(doc, i).ToText());
        }

        var sb = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            sb.Append("# page ").Append(i + 1).Append('\n').Append(texts[i]);
        }

        return sb.ToString();
    }

    public static IEnumerable<TheoryDataRow<string>> SnapshotFiles()
    {
        foreach (string f in Files("generated/pdf").Concat(Files("pdf20examples")))
        {
            yield return new TheoryDataRow<string>(f) { TestDisplayName = Path.GetFileName(f) };
        }
    }

    [Theory]
    [MemberData(nameof(SnapshotFiles))]
    public void SnapshotsAreStableAndMatchTheCommittedOnes(string file)
    {
        using PdfDocument? doc = TryOpen(file);
        Assert.SkipWhen(doc is null, "needs a password");
        string a = Snapshot(doc!, 1);
        string b = Snapshot(doc!, 1);
        Assert.Equal(a, b);
        Assert.Equal(a, Snapshot(doc!, 4));

        string set = file.Contains("pdf20examples", StringComparison.Ordinal) ? "pdf20examples" : "generated";
        string path = Path.Combine(SnapshotDir(), set, Path.GetFileNameWithoutExtension(file) + ".txt");
        if (Environment.GetEnvironmentVariable("ONEPDF_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, a);
            return;
        }

        Assert.True(File.Exists(path), "no committed snapshot: " + path);
        Assert.True(File.ReadAllText(path) == a, "snapshot differs: " + path);
    }

    public static IEnumerable<TheoryDataRow<string>> AllFiles()
    {
        foreach (string f in Files("."))
        {
            yield return new TheoryDataRow<string>(f) { TestDisplayName = Path.GetRelativePath(TestData()!, f) };
        }
    }

    private sealed class PageSizeDevice : PdfDevice
    {
        public double Width { get; private set; }

        public double Height { get; private set; }

        public override void BeginPage(in PdfPageInfo page)
        {
            Width = page.Width;
            Height = page.Height;
        }
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void StructureIsBalancedFiniteAndOnThePage(string file)
    {
        using PdfDocument? doc = TryOpen(file);
        Assert.SkipWhen(doc is null, "needs a password or cannot be opened");
        bool strictPage = file.Contains("generated", StringComparison.Ordinal) || file.Contains("pdf20examples", StringComparison.Ordinal);
        for (int i = 0; i < doc!.Pages.Count; i++)
        {
            var chk = new CheckingDevice();
            doc.Pages[i].Interpret(chk);
            Assert.True(chk.Problems.Count == 0, $"{file} page {i}: {string.Join("; ", chk.Problems.Take(3))}");
            Assert.True(chk.Ended);

            PdfDisplayList list = TestPdf.Record(doc, i);
            var size = new PageSizeDevice();
            list.Replay(size);
            int outside = 0;
            for (int k = 0; k < list.ItemCount; k++)
            {
                string kind = list.GetItemKind(k);
                if (kind is not ("Fill" or "Stroke" or "Glyphs" or "Image"))
                {
                    continue;
                }

                PdfBounds b = list.GetItemBounds(k);
                Assert.True(!double.IsNaN(b.Left + b.Bottom + b.Right + b.Top) && b.Left <= b.Right && b.Bottom <= b.Top, $"{file} page {i} item {k}: bad bounds");
                if (b.Right < 0 || b.Bottom > size.Height || b.Left > size.Width || b.Top < 0)
                {
                    outside++;
                }
            }

            if (strictPage)
            {
                Assert.True(outside == 0, $"{file} page {i}: {outside} items outside the page");
            }
        }
    }
}
