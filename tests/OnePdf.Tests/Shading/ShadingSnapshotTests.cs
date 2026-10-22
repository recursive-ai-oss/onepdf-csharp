using System.Runtime.CompilerServices;
using System.Text;
using OnePdf.Tests.Content;
using static OnePdf.Tests.Shading.ShadingKit;

namespace OnePdf.Tests.Shading;

/// <summary>
/// Display-list text snapshots of pages built here with every shading type and pattern kind. Set ONEPDF_UPDATE_SNAPSHOTS=1
/// to rewrite them after an intended change (bump the format version when the format itself changed).
/// </summary>
public class ShadingSnapshotTests
{
    private static string SnapshotDir([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, "Content", "Snapshots", "shading");

    private static byte[] Coons(bool tensor)
    {
        (int, int)[] order = [(0, 0), (0, 1), (0, 2), (0, 3), (1, 3), (2, 3), (3, 3), (3, 2), (3, 1), (3, 0), (2, 0), (1, 0), (1, 1), (1, 2), (2, 2), (2, 1)];
        BitWriter w = new();
        w.Put(0, 8);
        foreach ((int i, int j) in order.Take(tensor ? 16 : 12))
        {
            w.Put((85 * i) + (i == 1 && j == 1 ? 20 : 0), 8).Put(85 * j, 8);
        }

        foreach (int c in new[] { 0, 85, 255, 170 })
        {
            w.Put(c, 8).Put(255 - c, 8).Put(c / 2, 8);
        }

        w.Put(1, 8);
        foreach ((int i, int j) in order.Skip(4).Take(tensor ? 12 : 8))
        {
            w.Put(85 * j, 8).Put(85 * (3 - i), 8);
        }

        foreach (int c in new[] { 30, 200 })
        {
            w.Put(c, 8).Put(c, 8).Put(255 - c, 8);
        }

        return w.ToArray();
    }

    private static byte[] Triangles()
    {
        BitWriter w = new();
        int[][] v =
        [
            [0, 0, 0, 255, 0, 0], [0, 255, 0, 0, 255, 0], [0, 0, 255, 0, 0, 255], [1, 255, 255, 255, 255, 0], [2, 128, 128, 0, 0, 0],
        ];
        foreach (int[] row in v)
        {
            foreach (int x in row)
            {
                w.Put(x, 8);
            }
        }

        return w.ToArray();
    }

    private static byte[] Lattice()
    {
        BitWriter w = new();
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                w.Put((c * 100) + (r * 10), 8).Put(r * 100, 8).Put(c * 120, 8).Put(r * 120, 8).Put(128, 8);
            }
        }

        return w.ToArray();
    }

    private static byte[] Mesh(int type, byte[] data, string extra = "")
    {
        string res = "/Resources << /Shading << /S 6 0 R >> >>";
        return TestPdf.Build("/S sh", res, b => b.Stream(6, ShadingKit.Mesh(type, extra), data), objects: 8);
    }

    private static byte[] Functional()
    {
        const string fnRgb = "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> ";
        return TestPdf.Build(
            "q 0 0 100 100 re W n /A sh Q q 100 0 100 100 re W n /R sh Q q 0 100 200 100 re W n /F sh Q",
            "/Resources << /Shading << /A 6 0 R /R 7 0 R /F 9 0 R >> >>",
            b =>
            {
                b.Obj(6, "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true false] /BBox [0 0 100 100] " + fnRgb + ">>");
                b.Obj(7, "<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [150 50 5 150 50 40] /Extend [false true] " + fnRgb + ">>");
                b.Stream(8, "/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", Encoding.Latin1.GetBytes("{ 2 copy mul }"));
                b.Obj(9, "<< /ShadingType 1 /ColorSpace /DeviceRGB /Matrix [200 0 0 100 0 100] /Function 8 0 R >>");
            },
            objects: 10);
    }

    private static byte[] Patterns()
    {
        const string cell = "/Type /Pattern /PatternType 1 /TilingType 1 ";
        return TestPdf.Build(
            "/Pattern cs /Col scn 0 0 100 100 re f /Cs cs 0 0.5 1 /Unc scn 100 0 100 100 re f /Pattern cs /Nest scn 0 100 100 100 re f /Sh scn 100 100 100 100 re f /Pattern CS /Sh SCN 4 w 110 110 80 80 re S",
            "/Resources << /Pattern << /Col 6 0 R /Unc 7 0 R /Nest 8 0 R /Sh 10 0 R >> /ColorSpace << /Cs [/Pattern /DeviceRGB] >> >>",
            b =>
            {
                b.Stream(6, cell + "/PaintType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Matrix [1 0.5 -0.5 1 0 0] /Resources << >>", Encoding.Latin1.GetBytes("1 0 0 rg 0 0 5 5 re f 0 0 1 rg 5 5 5 5 re f"));
                b.Stream(7, cell + "/PaintType 2 /BBox [0 0 8 8] /XStep 12 /YStep 12 /Resources << >>", Encoding.Latin1.GetBytes("1 0 0 rg 0 0 6 6 re f"));
                b.Stream(8, cell + "/PaintType 1 /BBox [0 0 20 20] /XStep 20 /YStep 20 /Resources << /Pattern << /In 9 0 R >> >>", Encoding.Latin1.GetBytes("/Pattern cs /In scn 0 0 20 20 re f 0 g 0 0 20 1 re f"));
                b.Stream(9, cell + "/PaintType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>", Encoding.Latin1.GetBytes("0 0.6 0 rg 0 0 2 2 re f"));
                b.Obj(10, "<< /Type /Pattern /PatternType 2 /Matrix [1 0 0 1 100 100] /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /Background [0.5] >> >>");
            },
            objects: 12);
    }

    public static IEnumerable<TheoryDataRow<string>> Cases() =>
        Names.Select(n => new TheoryDataRow<string>(n));

    private static readonly string[] Names = ["functional", "triangles", "lattice", "coons", "tensor", "patterns"];

    private static byte[] Build(string name) => name switch
    {
        "functional" => Functional(),
        "triangles" => Mesh(4, Triangles()),
        "lattice" => Mesh(5, Lattice(), "/VerticesPerRow 3 "),
        "coons" => Mesh(6, Coons(false)),
        "tensor" => Mesh(7, Coons(true)),
        _ => Patterns(),
    };

    private static string Snapshot(PdfDocument doc) => TestPdf.Record(doc).ToText();

    [Theory]
    [MemberData(nameof(Cases))]
    public void SnapshotsAreStableAcrossRunsAndThreadsAndMatchTheCommittedOnes(string name)
    {
        using PdfDocument doc = TestPdf.Open(Build(name));
        string a = Snapshot(doc);
        Assert.Equal(a, Snapshot(doc));
        var parallel = new string[8];
        Parallel.For(0, parallel.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i => parallel[i] = Snapshot(doc));
        Assert.All(parallel, p => Assert.Equal(a, p));
        using PdfDocument fresh = TestPdf.Open(Build(name));
        Assert.Equal(a, Snapshot(fresh));

        // Every device call is balanced and finite, patterns' cells included.
        var chk = new CheckingDevice();
        doc.Pages[0].Interpret(chk);
        Assert.Empty(chk.Problems);

        string path = Path.Combine(SnapshotDir(), name + ".txt");
        if (Environment.GetEnvironmentVariable("ONEPDF_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, a);
            return;
        }

        Assert.True(File.Exists(path), "no committed snapshot: " + path);
        Assert.True(File.ReadAllText(path) == a, "snapshot differs: " + path);
    }
}
