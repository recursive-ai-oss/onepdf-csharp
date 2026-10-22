using System.Text;
using OnePdf.Content;

namespace OnePdf.Tests.Content;

/// <summary>Builds one-page PDFs around a content stream and interprets pages for assertions.</summary>
internal static class TestPdf
{
    /// <summary>Catalog 1, Pages 2, Page 3 (content 4). Further objects start at 5.</summary>
    public static byte[] Build(string content, string pageEntries = "", Action<PdfBuilder>? extra = null, string mediaBox = "[0 0 200 200]", int objects = 5, string catalogEntries = "")
    {
        var b = new PdfBuilder();
        b.Obj(1, $"<< /Type /Catalog /Pages 2 0 R {catalogEntries} >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Contents 4 0 R {pageEntries} >>");
        b.Stream(4, string.Empty, Encoding.Latin1.GetBytes(content));
        extra?.Invoke(b);
        long x = b.XrefTable(objects);
        b.Trailer($"/Size {objects} /Root 1 0 R", x);
        return b.ToArray();
    }

    public static PdfDocument Open(byte[] data, PdfOpenOptions? options = null) => PdfDocument.Open(data, options);

    public static PdfDictionary Page(PdfDocument doc, int index = 0) =>
        PageAccess.GetPage(doc, index) ?? throw new InvalidOperationException("no such page");

    /// <summary>Interprets page 0 of the file into a display list.</summary>
    public static PdfDisplayList Record(PdfDocument doc, int page = 0, PdfInterpretOptions? options = null, CancellationToken ct = default)
    {
        var list = new PdfDisplayList();
        ContentInterpreter.Run(doc, Page(doc, page), list, options, ct);
        return list;
    }

    public static string Text(byte[] data, PdfInterpretOptions? options = null)
    {
        using PdfDocument doc = Open(data);
        return Record(doc, 0, options).ToText();
    }

    public static string Text(string content, string pageEntries = "", Action<PdfBuilder>? extra = null, PdfInterpretOptions? options = null, int objects = 5, string mediaBox = "[0 0 200 200]") =>
        Text(Build(content, pageEntries, extra, mediaBox, objects), options);

    /// <summary>The lines of a text form that start with the given record keyword (indentation removed).</summary>
    public static string[] Lines(string text, string keyword) =>
        [.. text.Split('\n').Select(l => l.TrimStart()).Where(l => l.StartsWith(keyword + " ", StringComparison.Ordinal) || l == keyword)];
}

/// <summary>A device that checks bracket balance and finite numbers, counting what it sees.</summary>
internal sealed class CheckingDevice : PdfDevice
{
    // Clips and the other brackets are tracked apart: marked content may legitimately straddle q/Q.
    private readonly Stack<string> _open = new();
    private readonly Stack<string> _clips = new();
    public List<string> Problems { get; } = [];

    public int Paints { get; private set; }

    public int Clips { get; private set; }

    public int Runs { get; private set; }

    public int Calls { get; private set; }

    public bool Ended { get; private set; }

    private void Check(in PdfGraphicsState s)
    {
        Calls++;
        if (!s.Ctm.IsFinite)
        {
            Problems.Add("non-finite CTM");
        }

        if (!double.IsFinite(s.LineWidth) || !double.IsFinite(s.FillAlpha) || !double.IsFinite(s.StrokeAlpha))
        {
            Problems.Add("non-finite state value");
        }

        if (!Finite(s.FillColor) || !Finite(s.StrokeColor))
        {
            Problems.Add("non-finite colour");
        }
    }

    private static bool Finite(in PdfColor c) => double.IsFinite(c.C0) && double.IsFinite(c.C1) && double.IsFinite(c.C2) && double.IsFinite(c.C3);

    private void Open(string what) => (what == "clip" ? _clips : _open).Push(what);

    private void Close(string what)
    {
        Stack<string> st = what == "clip" ? _clips : _open;
        if (st.Count == 0 || st.Peek() != what)
        {
            Problems.Add($"unbalanced: closing {what} with {(st.Count == 0 ? "nothing" : st.Peek())} open");
            if (st.Count > 0)
            {
                st.Pop();
            }

            return;
        }

        st.Pop();
    }

    private void CheckPath(in PdfPath p)
    {
        foreach (double d in p.Points.Span)
        {
            if (!double.IsFinite(d))
            {
                Problems.Add("non-finite path coordinate");
                return;
            }
        }
    }

    public override void BeginPage(in PdfPageInfo page) => Open("page");

    public override void EndPage()
    {
        Ended = true;
        Close("page");
        if (_open.Count > 0 || _clips.Count > 0)
        {
            Problems.Add("open at end of page: " + string.Join(",", _open.Concat(_clips)));
        }
    }

    public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        Paints++;
        Check(state);
        CheckPath(path);
    }

    public override void StrokePath(in PdfPath path, in PdfGraphicsState state)
    {
        Paints++;
        Check(state);
        CheckPath(path);
    }

    public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state)
    {
        Clips++;
        Check(state);
        CheckPath(path);
        Open("clip");
    }

    public override void PushTextClip(in PdfGraphicsState state) => Open("clip");

    public override void PopClip() => Close("clip");

    public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
    {
        Runs++;
        Check(state);
        if (!run.TextMatrix.IsFinite || !double.IsFinite(run.AdvanceX) || !double.IsFinite(run.AdvanceY))
        {
            Problems.Add("non-finite glyph run");
        }
    }

    public override void DrawImage(in PdfImage image, in PdfGraphicsState state)
    {
        Paints++;
        Check(state);
    }

    public override void PaintShading(PdfObject shading, string name, in PdfGraphicsState state) => Check(state);

    public override void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state)
    {
        Check(state);
        Open("group");
    }

    public override void EndGroup() => Close("group");

    public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state)
    {
        Check(state);
        Open("mask");
    }

    public override void EndSoftMask() => Close("mask");

    public override void BeginMarkedContent(string tag, PdfDictionary? properties) => Open("mc");

    public override void EndMarkedContent() => Close("mc");

    public override void BeginAnnotation(in PdfAnnotationInfo annotation) => Open("annot");

    public override void EndAnnotation() => Close("annot");
}
