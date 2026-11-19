namespace OnePdf.Layout.Tests;

/// <summary>Shared helpers for the wrapper, page-default-style and OpenType feature tests.</summary>
internal static class StyleSupport
{
    internal sealed record Run(string Text, double X, double Y, double Size, double R, double G, double B, string Font, int Glyphs, int[] GlyphIds);

    /// <summary>Records what a page draws. The interpreter's output space has y running down from the top; positions here are converted to PDF user space (y up).</summary>
    internal sealed class Capture(double pageHeight) : PdfDevice
    {
        public List<Run> Runs { get; } = [];

        public List<(double X0, double Y0, double X1, double Y1)> Paths { get; } = [];

        public List<PdfMatrix> Images { get; } = [];

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            var sb = new System.Text.StringBuilder();
            ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
            var ids = new int[glyphs.Length];
            for (int i = 0; i < glyphs.Length; i++)
            {
                sb.Append(glyphs[i].Unicode);
                ids[i] = glyphs[i].GlyphId;
            }

            // The glyph matrix puts the origin of the first glyph on the page, with the font size folded in.
            PdfMatrix m = run.GlyphMatrix(0).Then(state.Ctm);
            double size = Math.Sqrt((m.A * m.A) + (m.B * m.B));
            Runs.Add(new Run(sb.ToString(), m.E, pageHeight - m.F, size, state.FillColor.C0, state.FillColor.C1, state.FillColor.C2, run.Font?.GetName("BaseFont") ?? string.Empty, glyphs.Length, ids));
        }

        public override void FillPath(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Add(path, state.Ctm);

        public override void StrokePath(in PdfPath path, in PdfGraphicsState state) => Add(path, state.Ctm);

        public override void DrawImage(in PdfImage image, in PdfGraphicsState state) => Images.Add(state.Ctm);

        private void Add(in PdfPath path, in PdfMatrix ctm)
        {
            ReadOnlySpan<double> p = path.Points.Span;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int i = 0; i + 1 < p.Length; i += 2)
            {
                (double x, double y0n) = ctm.Transform(p[i], p[i + 1]);
                double y = pageHeight - y0n;
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }

            if (x0 <= x1)
            {
                Paths.Add((x0, y0, x1, y1));
            }
        }
    }

    /// <summary>The runs that make up the first occurrence of <paramref name="text"/> in the page's text, in drawing order; empty when it is not there.</summary>
    public static List<Run> Find(Capture c, string text)
    {
        var sb = new System.Text.StringBuilder();
        var owner = new List<int>();
        for (int i = 0; i < c.Runs.Count; i++)
        {
            sb.Append(c.Runs[i].Text);
            owner.AddRange(Enumerable.Repeat(i, c.Runs[i].Text.Length));
        }

        int at = sb.ToString().IndexOf(text, StringComparison.Ordinal);
        if (at < 0)
        {
            return [];
        }

        return [.. owner.Skip(at).Take(text.Length).Distinct().Select(i => c.Runs[i])];
    }

    public static Capture Interpret(byte[] pdf, int page = 0)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        var cap = new Capture(d.Pages[page].CropBox.Height);
        d.Pages[page].Interpret(cap);
        return cap;
    }

    public static string Text(byte[] pdf)
    {
        using PdfDocument d = PdfDocument.Open(pdf);
        return Support.Normalize(string.Join(" ", d.ExtractText().Select(t => t.Text)));
    }

    public static byte[] Save(LayoutDocument doc) => Support.Save(doc);
}
