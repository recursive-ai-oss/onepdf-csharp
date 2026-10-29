using System.Text;
using OnePdf.Tests.Creation;

namespace OnePdf.Tests.PdfUA;

/// <summary>
/// Small documents whose embedded font lacks a glyph the text asks for, so the writer shows glyph 0 (<c>.notdef</c>): hidden text in
/// rendering mode 3 (the shape of an OCR layer), visible text, and a form XObject drawn from two places.
/// </summary>
internal static class NotdefDocs
{
    // CJK ideographs the Latin test font has no glyph for.
    public const string Missing1 = "\u4E00";
    public const string Missing2 = "\u4E01";
    public const string Missing3 = "\u4E02";

    /// <summary>
    /// Page 1: visible "Hello", hidden "ab?cd" (mode 3), visible "x?y". With <paramref name="form"/> a form with "m?n" is drawn on page 1
    /// and page 2; its text mode is whatever the page has set (see <see cref="ShareForm"/>).
    /// </summary>
    public static byte[] Build(bool form = false, bool hiddenOnPage = true, bool visibleOnPage = true)
    {
        byte[] font = CreationSupport.RequireFont("LiberationSans-Regular.ttf");
        using var ms = new MemoryStream();
        using (var w = new PdfDocumentWriter(ms, new PdfDocumentWriterOptions { DocumentId = new byte[16], LeaveOpen = true, CompressStreams = false }))
        {
            w.SetInfo("Title", "Notdef test");
            PdfCreatedFont f = w.AddFont(font, new PdfEmbedFontOptions());
            PdfFormXObject? shared = null;
            if (form)
            {
                PdfFormContentBuilder fb = w.BeginForm(new PdfRectangle(0, 0, 200, 40));
                fb.SetFont(f, 12);
                fb.DrawText("m" + Missing3 + "n", 5, 10);
                shared = fb.Commit();
            }

            for (int page = 0; page < (form ? 2 : 1); page++)
            {
                PdfStreamPageBuilder p = w.BeginPage(300, 200);
                p.SetFont(f, 12);
                if (page == 0)
                {
                    p.DrawText("Hello", 20, 180);
                    if (hiddenOnPage)
                    {
                        p.SetTextRenderMode(PdfTextRenderMode.Invisible);
                        p.DrawText("ab" + Missing1 + "cd", 20, 160);
                        p.SetTextRenderMode(PdfTextRenderMode.Fill);
                    }

                    if (visibleOnPage)
                    {
                        p.DrawText("x" + Missing2 + "y", 20, 140);
                    }
                }

                if (shared is not null)
                {
                    p.Translate(20, 80);
                    p.DrawForm(shared);
                }

                p.Commit();
            }

            w.Finish();
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Takes the text mode out of the form of a <c>Build(form: true)</c> document, so it inherits the mode of the page that draws it, and
    /// sets that mode (<c>3 Tr</c>, invisible, or <c>0 Tr</c>) before the form on each page.
    /// </summary>
    public static byte[] ShareForm(byte[] pdf, bool hiddenOnFirstPage, bool hiddenOnSecondPage)
    {
        using PdfDocument doc = Editable(pdf);
        PdfDictionary xobjects = doc.Pages[0].Resources.GetDictionary("XObject")!;
        PdfStream form = (PdfStream)xobjects.Get(xobjects.Keys.First())!;
        string fc = Encoding.Latin1.GetString(form.GetDecodedBytes().Span);
        Assert.Contains("0 Tr\n", fc, StringComparison.Ordinal);
        form.SetData(Encoding.Latin1.GetBytes(fc.Replace("0 Tr\n", string.Empty, StringComparison.Ordinal)));
        for (int i = 0; i < 2; i++)
        {
            PdfPage page = doc.Pages[i];
            string c = Encoding.Latin1.GetString(page.ContentStreams[0].GetDecodedBytes().Span);
            string mode = (i == 0 ? hiddenOnFirstPage : hiddenOnSecondPage) ? "3 Tr\n" : "0 Tr\n";
            c = c.Replace("/OpFm1 Do", mode + "/OpFm1 Do", StringComparison.Ordinal);
            page.Dictionary.SetEntry("Contents", doc.AddObject(doc.CreateStream(Encoding.Latin1.GetBytes(c))));
        }

        return Save(doc);
    }

    public static PdfDocument Editable(byte[] pdf) => PdfDocument.Open(pdf, new PdfOpenOptions { AllowEditing = true });

    public static byte[] Save(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Every glyph the page draws or hides, in order: code, mode, and the rounded position of its origin.</summary>
    public static List<string> Glyphs(PdfDocument doc, bool visibleOnly = false)
    {
        var dev = new GlyphLog(visibleOnly);
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            doc.Pages[i].Interpret(dev, new PdfInterpretOptions { GlyphOutlines = false, GenerateAppearances = false });
        }

        return dev.Items;
    }

    private sealed class GlyphLog(bool visibleOnly) : PdfDevice
    {
        public readonly List<string> Items = [];

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            if (visibleOnly && run.RenderMode == PdfTextRenderMode.Invisible)
            {
                return;
            }

            for (int i = 0; i < run.Glyphs.Length; i++)
            {
                PdfGlyph g = run.Glyphs.Span[i];
                PdfMatrix m = run.GlyphMatrix(i).Then(state.Ctm);
                Items.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{g.Code}/{(int)run.RenderMode}@{m.E:F3},{m.F:F3}"));
            }
        }
    }

    /// <summary>The decoded content of the first page, as text.</summary>
    public static string PageContent(PdfDocument doc)
    {
        var sb = new StringBuilder();
        foreach (PdfStream s in doc.Pages[0].ContentStreams)
        {
            sb.Append(Encoding.Latin1.GetString(s.GetDecodedBytes().Span)).Append('\n');
        }

        return sb.ToString();
    }
}
