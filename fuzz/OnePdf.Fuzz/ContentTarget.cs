using System;
using System.Text;
using OnePdf;

namespace OnePdf.Fuzz;

/// <summary>
/// Fuzz target for the content interpreter. Input starting with <c>%PDF</c> is opened as a file and every page (up to a few)
/// is interpreted; any other input is a content stream placed in a page that has resources of every kind: forms that use
/// each other and themselves, soft masks, optional content, patterns, shadings, fonts and an annotation. Only the library's
/// documented exceptions may escape, and the recorded display list must replay without trouble.
/// </summary>
internal static class ContentTarget
{
    private static readonly PdfLimits Limits = PdfLimits.Default with { MaxOperatorsPerPage = 100_000, MaxDecodedStreamBytes = 8 << 20 };

    public static void Run(ReadOnlySpan<byte> data)
    {
        try
        {
            byte[] file = data.Length > 4 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' ? data.ToArray() : Wrap(data);
            using PdfDocument doc = PdfDocument.Open(file, new PdfOpenOptions { Limits = Limits });
            int pages = Math.Min(doc.Pages.Count, 3);
            for (int i = 0; i < pages; i++)
            {
                var list = new PdfDisplayList();
                doc.Pages[i].Interpret(list);
                var sink = new Sink();
                list.Replay(sink);
                list.Replay(sink, new PdfBounds(0, 0, 100, 100));
                _ = list.ToText().Length;
                if (sink.Depth != 0)
                {
                    throw new InvalidOperationException("unbalanced display list");
                }
            }
        }
        catch (PdfException)
        {
        }
    }

    private sealed class Sink : PdfDevice
    {
        public int Depth { get; private set; }

        public override void BeginPage(in PdfPageInfo page) => Depth++;

        public override void EndPage() => Depth--;

        public override void PushClip(in PdfPath path, bool evenOdd, in PdfGraphicsState state) => Depth++;

        public override void PushTextClip(in PdfGraphicsState state) => Depth++;

        public override void PopClip() => Depth--;

        public override void BeginGroup(in PdfGroupInfo group, in PdfGraphicsState state) => Depth++;

        public override void EndGroup() => Depth--;

        public override void BeginSoftMask(in PdfSoftMaskInfo mask, in PdfGraphicsState state) => Depth++;

        public override void EndSoftMask() => Depth--;

        public override void BeginAnnotation(in PdfAnnotationInfo annotation) => Depth++;

        public override void EndAnnotation() => Depth--;
    }

    /// <summary>The page the fuzzer's content stream is placed in. Also useful as a seed: see <see cref="SeedFile"/>.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> content)
    {
        var bodies = new (string Dict, byte[]? Data)[]
        {
            ("<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [10 0 R 11 0 R] /D << /OFF [11 0 R] >> >> >>", null),
            ("<< /Type /Pages /Kids [3 0 R] /Count 1 >>", null),
            ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [14 0 R] /Resources 5 0 R >>", null),
            ("", content.ToArray()),
            ("<< /XObject << /F 6 0 R /G 7 0 R /Im 8 0 R >> /ExtGState << /GS 9 0 R /GM 15 0 R >> /Font << /F1 12 0 R /F2 13 0 R >>"
                + " /ColorSpace << /Sep [/Separation /X /DeviceCMYK 16 0 R] /Ix [/Indexed /DeviceRGB 1 <00ff00 ff0000>] /Lab [/Lab << /WhitePoint [1 1 1] >>] >>"
                + " /Pattern << /P1 17 0 R >> /Shading << /S1 18 0 R >> /Properties << /On 10 0 R /Off 11 0 R >> >>", null),
            ("<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources 5 0 R >>", Encoding.Latin1.GetBytes("q 1 0 0 rg 0 0 20 20 re f /G Do /F Do Q")),
            ("<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [1 0 0 1 5 5] /Group << /S /Transparency /I true >> /Resources 5 0 R >>", Encoding.Latin1.GetBytes("0 0 9 9 re f /F Do")),
            ("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 >>", new byte[] { 1, 2, 3, 4 }),
            ("<< /SMask << /S /Luminosity /G 7 0 R >> /ca 0.5 /BM /Multiply >>", null),
            ("<< /Type /OCG /Name (a) >>", null),
            ("<< /Type /OCG /Name (b) >>", null),
            ("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 34 /Widths [250 300 400] >>", null),
            ("<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-V /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /DW 1000 /W [1 [500] 2 9 700] >>] >>", null),
            ("<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>", null),
            ("<< /Type /Annot /Subtype /Square /Rect [10 10 60 40] /F 4 /AP << /N 19 0 R >> >>", null),
            ("<< /SMask /None >>", null),
            ("<< /FunctionType 2 /Domain [0 1] /N 1 >>", null),
            ("<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 5 5] /XStep 5 /YStep 5 /Resources << >> >>", Encoding.Latin1.GetBytes("0 0 2 2 re f")),
            ("<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 1 1] >>", null),
            ("<< /Type /XObject /Subtype /Form /BBox [0 0 100 50] /Resources 5 0 R >>", Encoding.Latin1.GetBytes("/F Do 0 0 100 50 re f")),
        };
        var offsets = new int[bodies.Length];
        var ms = new System.IO.MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            int n = i + 1;
            if (bodies[i].Data is { } d)
            {
                string dict = bodies[i].Dict.Length == 0 ? "<< " : bodies[i].Dict[..^2];
                W($"{n} 0 obj\n{dict} /Length {d.Length} >>\nstream\n");
                ms.Write(d);
                W("\nendstream\nendobj\n");
            }
            else
            {
                W($"{n} 0 obj\n{bodies[i].Dict}\nendobj\n");
            }
        }

        int xref = (int)ms.Length;
        W($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets)
        {
            W($"{o:D10} 00000 n \n");
        }

        W($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>A content stream that touches most operators, for seeding.</summary>
    public static byte[] SeedFile() => Encoding.Latin1.GetBytes(
        "q 1 0 0 1 10 10 cm /GS gs 1 0 0 rg 0 0 50 50 re W n 0 0 m 20 20 40 20 50 0 c h B /F Do Q "
        + "BT /F1 12 Tf 14 TL 10 100 Td (Hello) Tj [(A) -200 (B)] TJ T* (x) ' ET /OC /Off BDC 0 0 5 5 re f EMC /Sep cs 0.5 sc 0 0 9 9 re f "
        + "/Pattern cs /P1 scn 0 0 5 5 re f /S1 sh BI /W 2 /H 2 /BPC 8 /CS /G ID abcd EI /Im Do /GM gs 0 0 1 1 re f");
}
