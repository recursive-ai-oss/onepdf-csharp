using System.Text;
using OnePdf.Tests.PdfA;
using OnePdf.Text;

namespace OnePdf.Tests.Tagging;

/// <summary>Builds small untagged documents and checks tagged results; shared by the tagging tests.</summary>
internal static class TagDocs
{
    /// <summary>A page: its own content, or a shared content object (number of an object the caller adds), plus extra page entries.</summary>
    public sealed record PageSpec(string? Content, int ContentObject = 0, string Entries = "");

    public static string T(double x, double y, string text, string font = "F1", double size = 12) =>
        $"BT /{font} {size} Tf {x} {y} Td ({text}) Tj ET\n";

    /// <summary>Catalog 1, Pages 2, fonts 3 (Helvetica) and 4 (Times-Roman), pages 10.., their contents 50..; extra objects from 100.</summary>
    public static byte[] Make(PageSpec[] pages, Action<PdfBuilder>? extra = null, string xobjects = "", string catalogExtra = "", string resourcesExtra = "")
    {
        var b = new PdfBuilder();
        b.Obj(1, $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>");
        b.Obj(2, $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, i) => $"{10 + i} 0 R"))}] /Count {pages.Length} >>");
        b.Obj(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        b.Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>");
        for (int i = 0; i < pages.Length; i++)
        {
            PageSpec p = pages[i];
            int content = p.ContentObject != 0 ? p.ContentObject : 50 + i;
            b.Obj(10 + i, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content} 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> /XObject << {xobjects} >> {resourcesExtra} >> {p.Entries} >>");
            if (p.Content is not null)
            {
                b.Stream(content, string.Empty, Encoding.Latin1.GetBytes(p.Content));
            }
        }

        extra?.Invoke(b);
        long x = b.XrefTable(200);
        b.Trailer("/Size 200 /Root 1 0 R", x);
        return b.ToArray();
    }

    public static byte[] Make(params string[] pageContents) => Make([.. pageContents.Select(static c => new PageSpec(c))]);

    /// <summary>A Form XObject with the given content that uses both fonts and covers the page.</summary>
    public static void Form(PdfBuilder b, int number, string content, string extraEntries = "", string xobjects = "") =>
        b.Stream(number, $"/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> /XObject << {xobjects} >> >> {extraEntries}", Encoding.Latin1.GetBytes(content));

    public static int Blk(PdfPageText page, string prefix)
    {
        for (int i = 0; i < page.Blocks.Count; i++)
        {
            if (page.Blocks[i].Lines[0].Text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException("No block starting with '" + prefix + "': " + string.Join(" | ", page.Blocks.Select(static x => x.Lines[0].Text)));
    }

    /// <summary>The roles of the structure tree in document order (depth first), for example <c>H1 P Table TR TD</c>.</summary>
    public static string Roles(PdfDocument doc)
    {
        var sb = new StringBuilder();
        PdfDictionary? root = doc.StructureTreeRoot;
        if (root is null)
        {
            return string.Empty;
        }

        var stack = new Stack<PdfObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            PdfObject o = stack.Pop();
            if (o is PdfDictionary d)
            {
                if (d.GetName("S") is { } s)
                {
                    sb.Append(sb.Length > 0 ? " " : string.Empty).Append(s);
                }

                PdfObject? k = d.Get("K");
                if (k is not null)
                {
                    stack.Push(k);
                }
            }
            else if (o is PdfArray a)
            {
                for (int i = a.Count - 1; i >= 0; i--)
                {
                    if (a[i] is (PdfDictionary or PdfArray) and { } item)
                    {
                        stack.Push(item);
                    }
                }
            }
        }

        string roles = sb.ToString();
        return roles.StartsWith("Document ", StringComparison.Ordinal) ? roles[9..] : roles;
    }

    /// <summary>The structure element dictionaries in document order.</summary>
    public static List<PdfDictionary> Elements(PdfDocument doc)
    {
        var list = new List<PdfDictionary>();
        PdfDictionary? root = doc.StructureTreeRoot;
        if (root is null)
        {
            return list;
        }

        var stack = new Stack<PdfObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            PdfObject o = stack.Pop();
            if (o is PdfDictionary d)
            {
                if (d.GetName("S") is not null)
                {
                    list.Add(d);
                }

                if (d.Get("K") is { } k)
                {
                    stack.Push(k);
                }
            }
            else if (o is PdfArray a)
            {
                for (int i = a.Count - 1; i >= 0; i--)
                {
                    if (a[i] is (PdfDictionary or PdfArray) and { } item)
                    {
                        stack.Push(item);
                    }
                }
            }
        }

        return list;
    }

    /// <summary>The happy path: tags with <see cref="PdfDocument.Tag"/> from a whole-document extraction, saves, and checks that extraction and the display list are unchanged.</summary>
    public static byte[] Happy(byte[] source, Func<PdfTagBlock, PdfBlockRole?> roles, PdfTagOptions? options, out PdfTaggingResult result, bool check = true)
    {
        using PdfDocument doc = TagSupport.Editable(source);
        string extractBefore = TagSupport.ExtractionJson(doc);
        string displayBefore = TagSupport.DisplayText(doc);
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        options ??= new PdfTagOptions();
        options.Title ??= "Test document";
        options.Language ??= "en";
        result = doc.Tag(pages, roles, options);
        byte[] saved = TagSupport.Save(doc);
        if (check)
        {
            using PdfDocument after = PdfDocument.Open(saved);
            TagSupport.AssertSame(extractBefore, TagSupport.ExtractionJson(after));
            TagSupport.AssertSame(displayBefore, TagSupport.DisplayText(after));
        }

        return saved;
    }

    /// <summary>The decoded content of a page, all its streams joined.</summary>
    public static string PageContent(PdfDocument doc, int page)
    {
        PdfObject? c = doc.Pages[page].Dictionary.Get("Contents");
        var sb = new StringBuilder();
        if (c is PdfStream s)
        {
            sb.Append(Encoding.Latin1.GetString(s.GetDecodedBytes().Span));
        }
        else if (c is PdfArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] is PdfStream x)
                {
                    sb.Append(Encoding.Latin1.GetString(x.GetDecodedBytes().Span)).Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tags <paramref name="source"/> with the plan <paramref name="build"/> makes, saves it (full or incremental), and checks that
    /// extraction and the display list are unchanged. Returns the saved file.
    /// </summary>
    public static byte[] Tag(byte[] source, Action<PdfDocument, IReadOnlyList<PdfPageText>, PdfTaggingPlan> build, out PdfTaggingResult result, bool incremental = false, PdfOpenOptions? options = null)
    {
        using PdfDocument doc = PdfDocument.Open(source, new PdfOpenOptions { AllowEditing = true, Limits = options?.Limits ?? PdfLimits.Default });
        string extractBefore = TagSupport.ExtractionJson(doc);
        string displayBefore = TagSupport.DisplayText(doc);
        IReadOnlyList<PdfPageText> pages = TagSupport.Extract(doc);
        PdfTaggingPlan plan = doc.CreateTaggingPlan();
        plan.Title = "Test document";
        plan.Language = "en";
        build(doc, pages, plan);
        result = doc.ApplyTagging(plan);
        byte[] saved = incremental ? TagSupport.SaveIncremental(doc) : TagSupport.Save(doc);
        using PdfDocument after = PdfDocument.Open(saved);
        TagSupport.AssertSame(extractBefore, TagSupport.ExtractionJson(after));
        TagSupport.AssertSame(displayBefore, TagSupport.DisplayText(after));
        if (Environment.GetEnvironmentVariable("ONEPDF_TAGGED_OUT") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "t-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(saved))[..8] + ".pdf"), saved);
        }

        return saved;
    }

    private static bool Font(string clause) => clause.StartsWith("7.21", StringComparison.Ordinal);

    // What a PDF/UA save repairs by itself (identification and title in the metadata, tab order) is not the tagging's business.
    private static bool Ignorable(PdfUAViolation v) => Font(v.Clause) || (v.Clause == "7.1" && v.Checkpoint is "06" or "07") || v.Clause == "7.18.3";

    private static bool IgnorableRule(string f) => f.StartsWith("7.21", StringComparison.Ordinal) || f.StartsWith("7.1-8:", StringComparison.Ordinal) || f.StartsWith("7.1-10:", StringComparison.Ordinal) || f.StartsWith("7.18.3-", StringComparison.Ordinal);

    /// <summary>The PDF/UA check and veraPDF (when installed) find nothing but the unembedded standard fonts the test documents use.</summary>
    public static void AssertConforms(byte[] tagged)
    {
        using PdfDocument d = PdfDocument.Open(tagged);
        IReadOnlyList<PdfUAViolation> check = d.CheckPdfUA();
        Assert.True(check.All(static v => Ignorable(v)), string.Join(" | ", check.Where(static v => !Ignorable(v)).Select(static v => v.ToString())));
        if (VeraPdf.Path is not null)
        {
            List<string> failures = VeraPdf.Validate(tagged, "ua1");
            Assert.True(failures.All(static f => IgnorableRule(f)), "veraPDF ua1: " + string.Join("; ", failures.Where(static f => !IgnorableRule(f)).Take(6)));
        }
    }

    /// <summary>Fails when the PDF/UA check reports anything but the unembedded-font rules.</summary>
    public static void AssertNoTagProblems(PdfDocument d)
    {
        string[] bad = [.. d.CheckPdfUA().Where(static v => !Ignorable(v)).Select(static v => v.ToString())];
        Assert.True(bad.Length == 0, string.Join(" | ", bad));
    }

    public static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
