using System.Text;
using OnePdf.PdfFonts;

namespace OnePdf.Tests.PdfFonts;

/// <summary>A provider that has nothing: fonts the file does not embed get no outlines.</summary>
internal sealed class NoFonts : IPdfFontProvider
{
    public PdfSubstituteFont? FindFont(PdfFontRequest request) => null;
}

/// <summary>A provider that always answers with one font and records what was asked.</summary>
internal sealed class FixedFont(byte[] data) : IPdfFontProvider
{
    public List<PdfFontRequest> Requests { get; } = [];

    public PdfSubstituteFont? FindFont(PdfFontRequest request)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return new PdfSubstituteFont(data, "Fixed", "fixed-test-font");
    }
}

internal static class FontPdf
{
    /// <summary>Builds a one-page file plus the objects added by <paramref name="add"/> (numbers 10 and up) and opens it.</summary>
    public static PdfDocument Open(Action<PdfBuilder> add, IPdfFontProvider? provider = null)
    {
        var b = new PdfBuilder();
        b.BasicPages();
        add(b);
        long x = b.XrefTable(40);
        b.Trailer("/Size 40 /Root 1 0 R", x);
        PdfDocument doc = PdfDocument.Open(b.ToArray());
        doc.FontProvider = provider ?? new NoFonts();
        return doc;
    }

    public static PdfFont Font(PdfDocument doc, int objectNumber) =>
        doc.GetFont((PdfDictionary?)doc.GetObject(objectNumber));

    public static List<FontGlyph> Decode(PdfFont font, params byte[] text)
    {
        var list = new List<FontGlyph>();
        font.Decode(text, list);
        return list;
    }

    public static string Shown(IEnumerable<FontGlyph> glyphs) => string.Concat(glyphs.Select(static g => g.Unicode));

    public static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);
}
