using System;
using OnePdf.FontData;
using OnePdf.FontPrograms;

namespace OnePdf.Fuzz;

/// <summary>
/// Font program target: the first byte picks how the rest is read (any recognized font program, or one parser forced), then the
/// parser and every glyph run. Only <see cref="FontProgramException"/> may escape the parser, and glyph calls must not throw
/// anything else either; nothing may hang or allocate without bound.
/// </summary>
internal static class FontProgramTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        int mode = data[0] % 5;
        byte[] body = data[1..].ToArray();
        FontProgram font;
        try
        {
            font = mode switch
            {
                1 => Type1Font.Parse(body),
                2 => new CffFont(body),
                3 => SfntFont.Parse(body, 0),
                _ => FontProgramLoader.Open(body),
            };
        }
        catch (FontProgramException)
        {
            return;
        }

        // Cap the work per input: the point is parser safety, and big glyph counts only repeat the same paths.
        int n = Math.Min(font.GlyphCount, 200);
        for (int g = 0; g < n; g++)
        {
            try
            {
                font.GetOutline(g);
            }
            catch (FontProgramException)
            {
            }

            try
            {
                font.TryGetAdvance(g, out _);
            }
            catch (FontProgramException)
            {
            }

            font.GetGlyphName(g);
        }

        _ = font.FindGlyph("A");
        _ = font.GlyphForCid(5);
        _ = font.BuiltInEncoding;
        if (font is SfntFont sfnt)
        {
            foreach (CmapSubtable c in sfnt.CmapSubtables)
            {
                c.Lookup(65);
                c.Lookup(0x1F600);
                c.LookupSymbolic(0x41);
                _ = c.Enumerate();
            }
        }
    }
}

/// <summary>CMap target: the first byte selects whether a usecmap parent resolves; the rest is parsed and exercised.</summary>
internal static class CMapTarget
{
    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return;
        }

        CMap m = CMapParser.Parse(data[1..], (data[0] & 1) == 0 ? null : static _ => null);
        for (uint c = 0; c < 0x300; c += 7)
        {
            m.ToCid(c, 1);
            m.ToCid(c, 2);
            m.TryGetUnicode(c, 2, out _);
        }

        ReadOnlySpan<byte> sample = [0x00, 0x41, 0x81, 0x40, 0xFF, 0xD8, 0x00];
        while (m.ReadCode(sample, out _, out int len, out _))
        {
            sample = sample[len..];
        }

        _ = m.EnumerateUnicode(2000);
    }
}
