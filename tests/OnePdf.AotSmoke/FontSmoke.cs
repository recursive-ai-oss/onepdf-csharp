using System.Text;
using OnePdf.FontData;
using OnePdf.Fonts;
using OnePdf.FontPrograms;

namespace OnePdf.AotSmoke;

// Touches every embedded font-data resource and the parsers, so trimming or AOT compilation that breaks resource loading shows up here.
internal static class FontSmoke
{
    public static string? Run()
    {
        if (Core14.Get("Helvetica").GetWidth("A") != 667)
        {
            return "core14 metrics";
        }

        if (!GlyphList.TryGetUnicode("uni20AC0308", out string t) || t != "\u20AC\u0308" || Encodings.GetName(PredefinedEncoding.WinAnsi, 0x80) != "Euro")
        {
            return "glyph list or encodings";
        }

        CMap? sj = PredefinedCMaps.TryGet("90ms-RKSJ-H");
        if (sj is null || sj.ToCid(0x82A0, 2) != 843 || CidToUnicode.Get("Japan1")?.Lookup(843) != 0x3042)
        {
            return "predefined cmaps";
        }

        CMap tu = CMapParser.Parse("1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfrange <41> <43> <0061> endbfrange"u8);
        if (!tu.TryGetUnicode(0x42, 1, out string b) || b != "b")
        {
            return "tounicode";
        }

        if (CffStandardData.StandardString(1) != "space" || CffStandardData.MacGlyphName(3) != "space")
        {
            return "cff data";
        }

        if (!SubstituteFonts.TryFind("Arial-BoldMT", out SubstituteFace face) || face.Name != "LiberationSans-Bold"
            || FontProgramLoader.Open(face.GetData()).GlyphCount < 100)
        {
            return "substitute font";
        }

        try
        {
            FontProgramLoader.Open(Encoding.ASCII.GetBytes("not a font at all"));
            return "garbage accepted";
        }
        catch (FontProgramException)
        {
            return null;
        }
    }
}
