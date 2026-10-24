using System.Globalization;
using System.Text;

namespace OnePdf;

/// <summary>
/// One glyph of a run that a shaping engine has already laid out, for <see cref="PdfContentBuilder.DrawGlyphRun"/>. All distances are
/// in thousandths of the font size (the unit of PDF glyph widths), so a run does not depend on the size it is drawn at.
/// </summary>
/// <param name="GlyphId">The glyph index in the embedded font.</param>
/// <param name="Cluster">
/// The UTF-16 index in the run's text of the first character this glyph stands for. Glyphs that share a cluster value (next to each
/// other) form one cluster; a cluster's text runs from its value to the next higher cluster value present in the run, or to the end
/// of the text.
/// </param>
/// <param name="Advance">How far the pen moves after the glyph, to the right, in thousandths of the font size. Not negative for ordinary text.</param>
/// <param name="OffsetX">Shift of the glyph to the right of the pen position, without moving the pen. Default 0.</param>
/// <param name="OffsetY">Shift of the glyph upward from the baseline, without moving the pen. Default 0.</param>
public readonly record struct PdfShapedGlyph(int GlyphId, int Cluster, double Advance, double OffsetX = 0, double OffsetY = 0);

public abstract partial class PdfContentBuilder
{
    private const int MaxGlyphsPerRun = 1 << 20;

    /// <summary>
    /// Draws glyphs that a shaping engine has chosen and placed, by glyph index, with the font selected by <see cref="SetFont"/>
    /// (which must come from <see cref="PdfDocument.AddFont(ReadOnlyMemory{byte}, PdfEmbedFontOptions?)"/>). The run is one line, left
    /// to right in <em>drawing</em> order: for right-to-left text the glyphs are given in visual order, as shaping engines return them.
    /// </summary>
    /// <remarks>
    /// <para>The text each glyph stands for is kept so that extraction returns the logical text (ISO 32000-2 9.10, 14.9.4):</para>
    /// <list type="bullet">
    /// <item>a cluster of one glyph (a letter, or a ligature standing for several characters) adds the cluster's text to the font's
    /// <c>/ToUnicode</c> map, one glyph to one or more characters;</item>
    /// <item>a cluster of several glyphs (a reordered or split Indic syllable, a base with several marks), a glyph that the
    /// map already ties to different text, or a cluster value that returns after other clusters, is wrapped in a
    /// <c>/Span</c> with <c>/ActualText</c> holding the cluster's text.</item>
    /// </list>
    /// <para>Each glyph is placed from the given advance and offsets, whatever the font's own widths are; the font's <c>/W</c> array
    /// still records its design widths. Character spacing set with <see cref="SetCharacterSpacing"/> is compensated, so the pen moves
    /// by exactly <see cref="PdfShapedGlyph.Advance"/>; word spacing is not applied. Glyph 0 (the missing-glyph box) is allowed.
    /// The text render mode and fill colour apply as for <see cref="DrawText(string, double, double)"/>. The call writes one text object.</para>
    /// <para>The API takes no dependency on a shaping library: fill the array from whatever produced the glyphs.</para>
    /// </remarks>
    /// <param name="x">Baseline start of the run in the current coordinates.</param>
    /// <param name="y">Baseline of the run.</param>
    /// <param name="glyphs">The glyphs in drawing order. At most 2^20.</param>
    /// <param name="text">The characters the run was shaped from, in logical order; clusters index into it.</param>
    /// <returns>The distance the pen moved, in points (the sum of the advances at the current font size).</returns>
    /// <exception cref="InvalidOperationException">The builder is closed, or the current font is not an embedded TrueType/OpenType font.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A glyph index is not in the font, a cluster is outside the text, or a number is not finite.</exception>
    /// <exception cref="ArgumentException">The run has too many glyphs.</exception>
    public double DrawGlyphRun(double x, double y, ReadOnlySpan<PdfShapedGlyph> glyphs, ReadOnlySpan<char> text)
    {
        CheckOpen();
        if (_font is not EmbeddedCreatedFont font)
        {
            throw new InvalidOperationException("DrawGlyphRun needs an embedded font: call SetFont with a font from PdfDocument.AddFont.");
        }

        Check(x);
        Check(y);
        if (glyphs.Length == 0)
        {
            return 0;
        }

        if (glyphs.Length > MaxGlyphsPerRun)
        {
            throw new ArgumentException($"A glyph run holds at most {MaxGlyphsPerRun} glyphs.", nameof(glyphs));
        }

        var starts = new List<int>();
        double total = 0;
        foreach (PdfShapedGlyph g in glyphs)
        {
            if ((uint)g.Cluster > (uint)text.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(glyphs), $"A cluster value ({g.Cluster}) is outside the text (length {text.Length}).");
            }

            if (!double.IsFinite(g.Advance) || !double.IsFinite(g.OffsetX) || !double.IsFinite(g.OffsetY) || Math.Abs(g.Advance) > 1e7 || Math.Abs(g.OffsetX) > 1e7 || Math.Abs(g.OffsetY) > 1e7)
            {
                throw new ArgumentOutOfRangeException(nameof(glyphs), "Advances and offsets must be finite and below 1e7.");
            }

            starts.Add(g.Cluster);
            total += g.Advance;
        }

        starts.Sort();
        int distinct = 0;
        for (int i = 0; i < starts.Count; i++)
        {
            if (i == 0 || starts[i] != starts[i - 1])
            {
                starts[distinct++] = starts[i];
            }
        }

        starts.RemoveRange(distinct, starts.Count - distinct);

        string fontName = ResourceName(_fonts, "OpF", font.Reference);
        double size = _fontSize;
        double tcUnits = _charSpacing * 1000.0 / size;
        var sb = new StringBuilder();
        sb.Append("BT\n/").Append(fontName).Append(' ').Append(Num(size)).Append(" Tf\n");
        sb.Append(Num(_charSpacing)).Append(" Tc\n0 Tw\n");
        sb.Append((int)_renderMode).Append(" Tr\n");
        sb.Append("1 0 0 1 ").Append(Num(x)).Append(' ').Append(Num(y)).Append(" Tm\n");

        bool arrayOpen = false;
        double adjust = 0; // pending pen correction before the next glyph, in the units of a TJ number (positive moves left)
        double rise = 0;
        var seen = new HashSet<int>();
        int n = glyphs.Length;
        int[] codes = new int[n];
        int[] widths = new int[n];
        int gi = 0;
        while (gi < n)
        {
            int cluster = glyphs[gi].Cluster;
            int gj = gi + 1;
            while (gj < n && glyphs[gj].Cluster == cluster)
            {
                gj++;
            }

            bool first = seen.Add(cluster);
            string groupText = string.Empty;
            if (first)
            {
                int at = starts.BinarySearch(cluster);
                int end = at + 1 < starts.Count ? starts[at + 1] : text.Length;
                groupText = text[cluster..end].ToString();
            }

            int count = gj - gi;
            bool needsActual = count > 1 || !first;
            for (int k = 0; k < count; k++)
            {
                string? glyphText = count == 1 && groupText.Length > 0 ? groupText : null;
                codes[gi + k] = font.CodeForGlyph(glyphs[gi + k].GlyphId, glyphText, out bool conflict, out widths[gi + k]);
                needsActual |= conflict;
            }

            if (needsActual)
            {
                CloseArray(sb, ref arrayOpen);
                sb.Append("/Span <</ActualText ");
                AppendTextString(sb, groupText);
                sb.Append(">> BDC\n");
            }

            for (int k = 0; k < count; k++)
            {
                PdfShapedGlyph g = glyphs[gi + k];
                if (g.OffsetY != rise)
                {
                    CloseArray(sb, ref arrayOpen);
                    sb.Append(Num(g.OffsetY * size / 1000.0)).Append(" Ts\n");
                    rise = g.OffsetY;
                }

                if (!arrayOpen)
                {
                    sb.Append('[');
                    arrayOpen = true;
                }

                adjust -= g.OffsetX;
                if (Num(adjust) != "0")
                {
                    sb.Append(Num(adjust));
                }

                adjust = 0;
                sb.Append('<').Append(codes[gi + k].ToString("X4", CultureInfo.InvariantCulture)).Append('>');
                adjust = g.OffsetX + widths[gi + k] + tcUnits - g.Advance;
            }

            if (needsActual)
            {
                CloseArray(sb, ref arrayOpen);
                sb.Append("EMC\n");
            }

            gi = gj;
        }

        CloseArray(sb, ref arrayOpen);
        if (rise != 0)
        {
            sb.Append("0 Ts\n");
        }

        sb.Append("ET\n");
        Emit(sb.ToString());
        return total * size / 1000.0;
    }

    private static void CloseArray(StringBuilder sb, ref bool open)
    {
        if (open)
        {
            sb.Append("] TJ\n");
            open = false;
        }
    }
}
