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

        // Clusters of a left-to-right run never go down, so each cluster value forms one group and its text ends where the next group's
        // begins. Right-to-left runs arrive in visual order and need the general bookkeeping below.
        bool ascending = true;
        double total = 0;
        for (int i = 0; i < glyphs.Length; i++)
        {
            PdfShapedGlyph g = glyphs[i];
            if ((uint)g.Cluster > (uint)text.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(glyphs), $"A cluster value ({g.Cluster}) is outside the text (length {text.Length}).");
            }

            if (!double.IsFinite(g.Advance) || !double.IsFinite(g.OffsetX) || !double.IsFinite(g.OffsetY) || Math.Abs(g.Advance) > 1e7 || Math.Abs(g.OffsetX) > 1e7 || Math.Abs(g.OffsetY) > 1e7)
            {
                throw new ArgumentOutOfRangeException(nameof(glyphs), "Advances and offsets must be finite and below 1e7.");
            }

            if (i > 0 && g.Cluster < glyphs[i - 1].Cluster)
            {
                ascending = false;
            }

            total += g.Advance;
        }

        List<int>? starts = null;
        HashSet<int>? seen = null;
        if (!ascending)
        {
            starts = new List<int>(glyphs.Length);
            foreach (PdfShapedGlyph g in glyphs)
            {
                starts.Add(g.Cluster);
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
            seen = [];
        }

        string fontName = ResourceName(_fonts, "OpF", font.Reference);
        double size = _fontSize;
        double tcUnits = _charSpacing * 1000.0 / size;
        CheckContentSize();

        // The run is written straight into the page's content. If a glyph turns out to be outside the font, nothing of it stays.
        StringBuilder sb = _sb;
        int mark = sb.Length;
        try
        {
            sb.Append("BT\n/").Append(fontName).Append(' ');
            AppendNum(sb, size);
            sb.Append(" Tf\n");
            AppendNum(sb, _charSpacing);
            sb.Append(" Tc\n0 Tw\n");
            sb.Append((int)_renderMode).Append(" Tr\n1 0 0 1 ");
            AppendNum(sb, x);
            sb.Append(' ');
            AppendNum(sb, y);
            sb.Append(" Tm\n");

            bool arrayOpen = false;
            double adjust = 0; // pending pen correction before the next glyph, in the units of a TJ number (positive moves left)
            double rise = 0;
            int n = glyphs.Length;
            int[]? codes = null;
            int[]? widths = null;
            int gi = 0;
            while (gi < n)
            {
                int cluster = glyphs[gi].Cluster;
                int gj = gi + 1;
                while (gj < n && glyphs[gj].Cluster == cluster)
                {
                    gj++;
                }

                bool first = seen is null || seen.Add(cluster);
                ReadOnlySpan<char> groupText = default;
                if (first)
                {
                    int end;
                    if (starts is null)
                    {
                        end = gj < n ? glyphs[gj].Cluster : text.Length;
                    }
                    else
                    {
                        int at = starts.BinarySearch(cluster);
                        end = at + 1 < starts.Count ? starts[at + 1] : text.Length;
                    }

                    groupText = text[cluster..end];
                }

                int count = gj - gi;
                bool needsActual = count > 1 || !first;
                int code0 = 0;
                int width0 = 0;
                if (count == 1)
                {
                    code0 = font.CodeForGlyph(glyphs[gi].GlyphId, groupText, out bool conflict, out width0);
                    needsActual |= conflict;
                }
                else
                {
                    if (codes is null || codes.Length < count)
                    {
                        codes = new int[Math.Max(count, 16)];
                        widths = new int[codes.Length];
                    }

                    for (int k = 0; k < count; k++)
                    {
                        codes[k] = font.CodeForGlyph(glyphs[gi + k].GlyphId, default, out bool conflict, out widths![k]);
                        needsActual |= conflict;
                    }
                }

                if (needsActual)
                {
                    CloseArray(sb, ref arrayOpen);
                    sb.Append("/Span <</ActualText ");
                    AppendTextString(sb, groupText.ToString());
                    sb.Append(">> BDC\n");
                }

                for (int k = 0; k < count; k++)
                {
                    PdfShapedGlyph g = glyphs[gi + k];
                    if (g.OffsetY != rise)
                    {
                        CloseArray(sb, ref arrayOpen);
                        AppendNum(sb, g.OffsetY * size / 1000.0);
                        sb.Append(" Ts\n");
                        rise = g.OffsetY;
                    }

                    if (!arrayOpen)
                    {
                        sb.Append('[');
                        arrayOpen = true;
                    }

                    adjust -= g.OffsetX;
                    if (!IsZeroNum(adjust))
                    {
                        AppendNum(sb, adjust);
                    }

                    adjust = 0;
                    int code = count == 1 ? code0 : codes![k];
                    int width = count == 1 ? width0 : widths![k];
                    sb.Append('<');
                    AppendHex4(sb, code);
                    sb.Append('>');
                    adjust = g.OffsetX + width + tcUnits - g.Advance;
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
        }
        catch
        {
            sb.Length = mark;
            throw;
        }

        return total * size / 1000.0;
    }

    private static void AppendHex4(StringBuilder sb, int code)
    {
        const string digits = "0123456789ABCDEF";
        sb.Append(digits[(code >> 12) & 0xF]).Append(digits[(code >> 8) & 0xF]).Append(digits[(code >> 4) & 0xF]).Append(digits[code & 0xF]);
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
