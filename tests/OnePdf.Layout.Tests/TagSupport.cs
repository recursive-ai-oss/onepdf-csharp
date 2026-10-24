using System.Diagnostics;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;

namespace OnePdf.Layout.Tests;

/// <summary>One structure element read back from a written file.</summary>
internal sealed class SNode
{
    public required string Type { get; init; }

    public required PdfDictionary Dict { get; init; }

    public int Number { get; init; }

    public SNode? Parent { get; init; }

    public List<SNode> Kids { get; } = [];

    /// <summary>Marked-content items: the page index and MCID.</summary>
    public List<(int Page, int Mcid)> Content { get; } = [];

    /// <summary>Annotations the element holds (OBJR).</summary>
    public List<PdfDictionary> Objects { get; } = [];

    /// <summary>The order of children and content items as written, "e" for an element and "c" for content.</summary>
    public string Order { get; set; } = string.Empty;

    public string? Alt => Dict.GetString("Alt")?.ToString();

    public string? Lang => Dict.GetString("Lang")?.ToString();

    public PdfDictionary? Attributes => Dict.GetDictionary("A");

    public IEnumerable<SNode> Descendants()
    {
        var stack = new Stack<SNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            SNode n = stack.Pop();
            yield return n;
            for (int i = n.Kids.Count - 1; i >= 0; i--)
            {
                stack.Push(n.Kids[i]);
            }
        }
    }

    public int PageSpan() => Descendants().SelectMany(n => n.Content).Select(c => c.Page).Distinct().Count();

    /// <summary>Compact text form: <c>Type(child child)</c>; leaf elements with content show <c>*</c>.</summary>
    public string Shape()
    {
        var sb = new StringBuilder();
        Write(sb);
        return sb.ToString();
    }

    private void Write(StringBuilder sb)
    {
        sb.Append(Type);
        if (Kids.Count == 0 && Content.Count == 0 && Objects.Count == 0)
        {
            return;
        }

        sb.Append('(');
        bool first = true;
        foreach (SNode k in Kids)
        {
            if (!first)
            {
                sb.Append(' ');
            }

            first = false;
            k.Write(sb);
        }

        if (Content.Count > 0 || Objects.Count > 0)
        {
            if (!first)
            {
                sb.Append(' ');
            }

            sb.Append('*');
        }

        sb.Append(')');
    }
}

internal static class TagSupport
{
    public static SNode ReadTree(PdfDocument doc)
    {
        PdfDictionary root = doc.StructureTreeRoot ?? throw new InvalidOperationException("The document has no structure tree.");
        var pages = new Dictionary<int, int>();
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            pages[doc.Pages[i].Dictionary.Reference!.Number] = i;
        }

        PdfObject? k = root.Get("K");
        PdfDictionary top = k as PdfDictionary ?? (PdfDictionary)((PdfArray)k!)[0]!;
        return Read(doc, top, null, pages, -1);
    }

    private static SNode Read(PdfDocument doc, PdfDictionary dict, SNode? parent, Dictionary<int, int> pages, int inheritedPage)
    {
        int pg = dict.GetRaw("Pg") is PdfReference r && pages.TryGetValue(r.Number, out int pi) ? pi : inheritedPage;
        var node = new SNode { Type = dict.GetName("S")!, Dict = dict, Number = dict.Reference!.Number, Parent = parent };
        PdfObject? k = dict.Get("K");
        var items = new List<PdfObject?>();
        if (k is PdfArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                items.Add(a[i]);
            }
        }
        else if (k is not null)
        {
            items.Add(k);
        }

        var order = new StringBuilder();
        foreach (PdfObject? item in items)
        {
            switch (item)
            {
                case PdfInteger n:
                    node.Content.Add((pg, (int)n.Value));
                    order.Append('c');
                    break;
                case PdfDictionary d when d.GetName("Type") == "MCR":
                    node.Content.Add((pages[((PdfReference)d.GetRaw("Pg")!).Number], (int)d.GetInt("MCID")));
                    order.Append('c');
                    break;
                case PdfDictionary d when d.GetName("Type") == "OBJR":
                    node.Objects.Add(d.GetDictionary("Obj")!);
                    order.Append('o');
                    break;
                case PdfDictionary d:
                    node.Kids.Add(Read(doc, d, node, pages, pg));
                    order.Append('e');
                    break;
            }
        }

        node.Order = order.ToString();
        return node;
    }

    /// <summary>Finds every paint that is neither inside tagged content (a marked-content sequence with an MCID) nor an artifact.</summary>
    public static List<string> Untagged(PdfDocument doc) => Scan(doc).Failures;

    /// <summary>Interprets every page: the untagged paints, and the marked-content ids each page's content uses, in order.</summary>
    public static (List<string> Failures, List<int>[] Mcids) Scan(PdfDocument doc)
    {
        var failures = new List<string>();
        var mcids = new List<int>[doc.Pages.Count];
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            var finder = new UntaggedFinder(i);
            doc.Pages[i].Interpret(finder);
            failures.AddRange(finder.Failures);
            mcids[i] = finder.Mcids;
        }

        return (failures, mcids);
    }

    private sealed class UntaggedFinder(int page) : PdfDevice
    {
        private readonly Stack<bool> _marks = new();
        private int _covered;

        public List<string> Failures { get; } = [];

        public List<int> Mcids { get; } = [];

        public override void BeginMarkedContent(string tag, PdfDictionary? properties)
        {
            if (properties is not null && properties.ContainsKey("MCID"))
            {
                Mcids.Add((int)properties.GetInt("MCID"));
            }

            bool covers = tag == "Artifact" || (properties is not null && properties.ContainsKey("MCID"));
            _marks.Push(covers);
            if (covers)
            {
                _covered++;
            }
        }

        public override void EndMarkedContent()
        {
            if (_marks.Count > 0 && _marks.Pop())
            {
                _covered--;
            }
        }

        private void Paint(string what)
        {
            if (_covered == 0 && Failures.Count < 20)
            {
                Failures.Add($"page {page}: {what} outside tagged content and artifacts");
            }
        }

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Paint("fill");

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => Paint("stroke");

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state) => Paint("text");

        public override void DrawImage(in PdfImage image, in PdfGraphicsState state) => Paint("image");

        public override void PaintShading(PdfShading shading, in PdfGraphicsState state) => Paint("shading");
    }

    /// <summary>Runs the veraPDF validator on <paramref name="pdf"/> and returns each failed rule as "clause-test: description (count)". Skips when veraPDF is absent.</summary>
    public static List<string> Vera(byte[] pdf, string flavour)
    {
        string? exe = Environment.GetEnvironmentVariable("ONEPDF_VERAPDF");
        if (exe is null || !File.Exists(exe))
        {
            Assert.Skip("veraPDF is not installed (set ONEPDF_VERAPDF to its launcher script).");
        }

        string dir = Path.Combine(Path.GetTempPath(), "layout-vera-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "doc.pdf");
        File.WriteAllBytes(file, pdf);
        try
        {
            var psi = new ProcessStartInfo(exe!) { RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--flavour");
            psi.ArgumentList.Add(flavour);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("xml");
            psi.ArgumentList.Add(file);
            psi.Environment["JAVA_OPTS"] = "-Xmx1g";
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string xml = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            var failures = new List<string>();
            if (!xml.Contains("isCompliant=\"true\"", StringComparison.Ordinal))
            {
                foreach (Match m in Regex.Matches(xml, "<rule [^>]*clause=\"([^\"]*)\"[^>]*testNumber=\"([^\"]*)\"[^>]*failedChecks=\"([^\"]*)\"[^>]*>\\s*<description>([^<]*)</description>"))
                {
                    failures.Add($"{m.Groups[1].Value}-{m.Groups[2].Value}: {m.Groups[4].Value} ({m.Groups[3].Value})");
                }

                if (failures.Count == 0)
                {
                    failures.Add("not compliant: " + (xml.Length > 1500 ? xml[..1500] : xml));
                }
            }

            return failures;
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
