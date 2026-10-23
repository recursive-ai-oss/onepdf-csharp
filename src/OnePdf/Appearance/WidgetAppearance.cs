using System.Globalization;

namespace OnePdf.Appearance;

/// <summary>
/// Appearances for form field widgets (ISO 32000-2 12.7.4): the border and background from <c>/MK</c> and <c>/BS</c>, then the
/// field's content as variable text (12.7.4.3) or a check mark, circle or caption.
/// </summary>
internal static class WidgetAppearance
{
    private const double Padding = 2;
    private const double MaxExtent = 1e6;

    private enum Kind { None, Text, Combo, List, Check, Radio, Push, Signature }

    private static Kind KindOf(GenContext c)
    {
        PdfDictionary a = c.Annotation;
        long ff = c.Inherited(a, "Ff") is PdfInteger i ? i.Value : 0;
        return c.Inherited(a, "FT") is PdfName n
            ? n.Value switch
            {
                "Tx" => Kind.Text,
                "Btn" => (ff & (1 << 16)) != 0 ? Kind.Push : (ff & (1 << 15)) != 0 ? Kind.Radio : Kind.Check,
                "Ch" => (ff & (1 << 17)) != 0 ? Kind.Combo : Kind.List,
                "Sig" => Kind.Signature,
                _ => Kind.None,
            }
            : Kind.None;
    }

    /// <summary>True for a widget that regenerating from <c>/NeedAppearances</c> should replace: text and choice fields always; buttons only when no stored appearance exists for their state.</summary>
    public static bool RegeneratesUnderNeedAppearances(PdfDocument doc, PdfDictionary annotation, bool hasStoredAppearance)
    {
        var c = new GenContext(doc, annotation, static (_, _) => { });
        Kind k = KindOf(c);
        return k switch
        {
            Kind.Text or Kind.Combo or Kind.List => true,
            Kind.Check or Kind.Radio or Kind.Push => !hasStoredAppearance,
            _ => false,
        };
    }

    public static PdfGeneratedAppearance? Generate(GenContext c, PdfRectangle rect)
    {
        Kind kind = KindOf(c);
        if (kind is Kind.None or Kind.Signature)
        {
            c.Info(kind == Kind.Signature ? "A signature field has no generated appearance" : "A widget without a known field type has no generated appearance");
            return null;
        }

        double w = rect.Width, h = rect.Height;
        if (!(w > 0 && h > 0) || w > MaxExtent || h > MaxExtent)
        {
            c.Warn("The widget rectangle is empty or out of range; no appearance was generated");
            return null;
        }

        PdfDictionary a = c.Annotation;
        PdfDictionary? mk = a.GetDictionary("MK");
        double[]? bg = GenContext.Components(mk?.Get("BG"));
        double[]? bc = GenContext.Components(mk?.Get("BC"));
        int rot = (int)(((mk?.GetInt("R") ?? 0) % 360 + 360) % 360);
        if (rot % 90 != 0)
        {
            rot = 0;
        }

        bool swap = rot is 90 or 270;
        double cw = swap ? h : w, ch = swap ? w : h;
        (double bw0, char style, double[] dash) = c.Border();
        double bw = bc is null ? 0 : bw0;
        if (bw * 2 >= Math.Min(cw, ch))
        {
            bw = Math.Max(0, Math.Min(cw, ch) / 4);
        }

        double inset = style is 'B' or 'I' ? bw * 2 : bw;

        var cb = new ContentBuilder();
        string description;
        FontChain? chain = null;
        var used = new HashSet<int>();
        bool circle = kind == Kind.Radio;

        // The frame comes first so the content sits on top of it.
        Frame(cb, cw, ch, bg, bc, bw, style, dash, circle);

        DefaultAppearance da = DefaultAppearance.Parse(GenContext.TextOf(c.Inherited(a, "DA")) ?? GenContext.TextOf(c.Doc.Catalog.GetDictionary("AcroForm")?.Get("DA")));
        switch (kind)
        {
            case Kind.Text:
            case Kind.Combo:
                description = kind == Kind.Text ? "text field" : "combo box";
                chain = c.Chain(da);
                TextContent(c, cb, chain, used, da, cw, ch, inset, bw, bc, kind == Kind.Combo);
                break;
            case Kind.List:
                description = "list box";
                chain = c.Chain(da);
                ListContent(c, cb, chain, used, da, cw, ch, inset);
                break;
            case Kind.Check:
            case Kind.Radio:
                description = kind == Kind.Check ? "check box" : "radio button";
                if (IsOn(c, kind))
                {
                    MarkContent(c, cb, mk, da, cw, ch, inset, kind, ref chain, used);
                }

                break;
            default:
                description = "push button";
                chain = c.Chain(da);
                CaptionContent(c, cb, chain, used, da, cw, ch, inset, mk);
                break;
        }

        PdfDictionary res = GenContext.Resources(chain, used, null);
        PdfDictionary form = GenContext.Form([0, 0, cw, ch], res);
        switch (rot)
        {
            case 90:
                form.Set("Matrix", Cos.Array(0, 1, -1, 0, w, 0));
                break;
            case 180:
                form.Set("Matrix", Cos.Array(-1, 0, 0, -1, w, h));
                break;
            case 270:
                form.Set("Matrix", Cos.Array(0, -1, 1, 0, 0, h));
                break;
        }

        return new PdfGeneratedAppearance(form, cb.ToBytes(), rect, description);
    }

    // ---- frame ----

    private static void Frame(ContentBuilder cb, double w, double h, double[]? bg, double[]? bc, double bw, char style, double[] dash, bool circle)
    {
        if (bg is not null)
        {
            cb.Raw("q\n");
            if (cb.Color(bg, false))
            {
                if (circle)
                {
                    cb.Ellipse(0, 0, w, h);
                }
                else
                {
                    cb.Rect(0, 0, w, h);
                }

                cb.Raw("f\n");
            }

            cb.Raw("Q\n");
        }

        if (bc is null || bw <= 0)
        {
            return;
        }

        cb.Raw("q\n");
        if (!cb.Color(bc, true))
        {
            cb.Raw("Q\n");
            return;
        }

        cb.Op("w", bw);
        if (circle)
        {
            if (style == 'D')
            {
                cb.Raw(GenContext.DashOp(dash));
            }

            cb.Ellipse(bw / 2, bw / 2, w - bw, h - bw);
            cb.Raw("S\n");
        }
        else
        {
            switch (style)
            {
                case 'B':
                case 'I':
                    Bevel(cb, w, h, bw, style == 'B', bg);
                    cb.Rect(bw / 2, bw / 2, w - bw, h - bw);
                    cb.Raw("S\n");
                    break;
                case 'U':
                    cb.Move(0, bw / 2).Line(w, bw / 2).Raw("S\n");
                    break;
                default:
                    if (style == 'D')
                    {
                        cb.Raw(GenContext.DashOp(dash));
                    }

                    cb.Rect(bw / 2, bw / 2, w - bw, h - bw);
                    cb.Raw("S\n");
                    break;
            }
        }

        cb.Raw("Q\n");
    }

    // ISO 32000-2 12.5.4 Table 168: beveled shows the upper left in white and the lower right in half the background tone;
    // inset shows the upper left in half grey and the lower right in three quarter grey.
    private static void Bevel(ContentBuilder cb, double w, double h, double bw, bool beveled, double[]? bg)
    {
        double b = bw, b2 = bw * 2;
        cb.Raw("q\n");
        if (beveled)
        {
            cb.Gray(1, false);
        }
        else
        {
            cb.Gray(0.5, false);
        }

        cb.Move(b, b).Line(b, h - b).Line(w - b, h - b).Line(w - b2, h - b2).Line(b2, h - b2).Line(b2, b2).Raw("f\n");
        if (beveled)
        {
            if (bg is null)
            {
                cb.Gray(0.5, false);
            }
            else
            {
                (double r, double g, double bl) = GenContext.Rgb(bg);
                cb.Op("rg", r / 2, g / 2, bl / 2);
            }
        }
        else
        {
            cb.Gray(0.75, false);
        }

        cb.Move(w - b, h - b).Line(w - b, b).Line(b, b).Line(b2, b2).Line(w - b2, b2).Line(w - b2, h - b2).Raw("f\n");
        cb.Raw("Q\n");
    }

    // ---- text and choice ----

    private static void TextContent(GenContext c, ContentBuilder cb, FontChain chain, HashSet<int> used, DefaultAppearance da, double cw, double ch, double inset, double bw, double[]? bc, bool combo)
    {
        PdfDictionary a = c.Annotation;
        long ff = c.Inherited(a, "Ff") is PdfInteger fi ? fi.Value : 0;
        bool multiline = !combo && (ff & (1 << 12)) != 0;
        bool password = !combo && (ff & (1 << 13)) != 0;
        bool comb = !combo && !multiline && !password && (ff & (1 << 24)) != 0;
        int maxLen = c.Inherited(a, "MaxLen") is PdfInteger ml ? (int)Math.Clamp(ml.Value, 0, int.MaxValue) : 0;
        int q = c.Inherited(a, "Q") is PdfInteger qi ? (int)qi.Value : (int)(c.Doc.Catalog.GetDictionary("AcroForm")?.GetInt("Q") ?? 0);
        if (q is < 0 or > 2)
        {
            q = 0;
        }

        string text = GenContext.TextOf(c.Inherited(a, "V")) ?? string.Empty;
        if (combo)
        {
            text = DisplayOf(c, text);
        }

        int maxChars = c.Limits.MaxAppearanceText;
        if (text.Length > maxChars)
        {
            c.Warn("The field value is longer than " + maxChars.ToString(CultureInfo.InvariantCulture) + " characters; only the start is drawn");
        }

        if (password)
        {
            text = new string('*', Math.Min(text.Length, maxChars));
        }

        if (text.Length == 0)
        {
            return;
        }

        List<ShapedGlyph> glyphs = chain.Shape(text, maxChars, multiline);
        c.NoteDropped(chain);
        if (glyphs.Count == 0)
        {
            return;
        }

        double availW = cw - (2 * inset) - (2 * Padding);
        double availH = ch - (2 * inset);
        if (!(availW > 0 && availH > 0))
        {
            return;
        }

        string colour = ColourOp(da);
        double fs = da.Size;
        var lines = new List<TextLine>();
        if (multiline)
        {
            int maxLines = c.Limits.MaxAppearanceItems;
            if (fs <= 0)
            {
                fs = 12;
                for (; fs > 4; fs -= 1)
                {
                    List<TextLine> trial = TextLayout.Wrap(glyphs, availW, fs, maxLines);
                    if (trial.Count * fs * TextLayout.LineHeight <= availH)
                    {
                        break;
                    }
                }
            }

            lines = TextLayout.Wrap(glyphs, availW, fs, maxLines);
        }
        else
        {
            if (comb && maxLen > 0)
            {
                int limit = Math.Min(maxLen, c.Limits.MaxAppearanceItems);
                if (glyphs.Count > limit)
                {
                    glyphs.RemoveRange(limit, glyphs.Count - limit);
                }
            }

            double w1 = TextLayout.Width(glyphs, 0, glyphs.Count);
            if (fs <= 0)
            {
                fs = Math.Clamp(availH * 0.75, 4, 100);
                if (!comb && w1 * fs > availW)
                {
                    fs = Math.Max(4, availW / w1);
                }
            }

            lines.Add(new TextLine(0, glyphs.Count, w1));
        }

        cb.Raw("/Tx BMC\nq\n").Rect(inset, inset, cw - (2 * inset), ch - (2 * inset)).Raw("W n\nBT\n").Raw(colour);
        int cur = -1;
        if (comb && maxLen > 0)
        {
            int cells = Math.Min(maxLen, c.Limits.MaxAppearanceItems);
            double cellW = (cw - (2 * inset)) / cells;
            double y = (ch - (TextLayout.CapHeight * fs)) / 2;
            for (int i = 0; i < glyphs.Count; i++)
            {
                double x = inset + (i * cellW) + ((cellW - (glyphs[i].Advance * fs)) / 2);
                TextLayout.EmitLine(cb, chain, glyphs, i, i + 1, x, y, fs, ref cur, used);
            }
        }
        else if (multiline)
        {
            double lh = fs * TextLayout.LineHeight;
            double y = ch - inset - Padding - (TextLayout.Ascent * fs);
            foreach (TextLine ln in lines)
            {
                if (y < -lh)
                {
                    break;
                }

                if (y <= ch + lh && ln.End > ln.Start)
                {
                    TextLayout.EmitLine(cb, chain, glyphs, ln.Start, ln.End, LineX(ln.Width * fs, q, cw, inset, availW), y, fs, ref cur, used);
                }

                y -= lh;
            }
        }
        else
        {
            TextLine ln = lines[0];
            TextLayout.EmitLine(cb, chain, glyphs, ln.Start, ln.End, LineX(ln.Width * fs, q, cw, inset, availW), (ch - (TextLayout.CapHeight * fs)) / 2, fs, ref cur, used);
        }

        cb.Raw("ET\nQ\nEMC\n");

        if (comb && maxLen > 1 && bc is not null && bw > 0)
        {
            int cells = Math.Min(maxLen, c.Limits.MaxAppearanceItems);
            double cellW = (cw - (2 * inset)) / cells;
            cb.Raw("q\n");
            cb.Color(bc, true);
            cb.Op("w", Math.Min(bw, 1));
            for (int i = 1; i < cells; i++)
            {
                double x = inset + (i * cellW);
                cb.Move(x, inset).Line(x, ch - inset);
            }

            cb.Raw("S\nQ\n");
        }
    }

    // ISO 32000-2 12.7.4.3 /Q: 0 left, 1 centred, 2 right-justified; text wider than the box starts at the left edge.
    private static double LineX(double width, int q, double cw, double inset, double availW)
    {
        double left = inset + Padding;
        if (width >= availW || q == 0)
        {
            return left;
        }

        return q == 1 ? left + ((availW - width) / 2) : left + availW - width;
    }

    private static string ColourOp(DefaultAppearance da)
    {
        var cb = new ContentBuilder();
        if (!cb.Color(da.Color, false))
        {
            cb.Gray(0, false);
        }

        return cb.ToString();
    }

    /// <summary>The display string of a choice field's export value (<c>/Opt</c> pairs), or the value itself.</summary>
    private static string DisplayOf(GenContext c, string value)
    {
        if (c.Inherited(c.Annotation, "Opt") is PdfArray opt)
        {
            int n = Math.Min(opt.Count, c.Limits.MaxAppearanceItems);
            for (int i = 0; i < n; i++)
            {
                if (opt[i] is PdfArray pair && pair.Count >= 2 && GenContext.TextOf(pair[0]) == value && GenContext.TextOf(pair[1]) is { } display)
                {
                    return display;
                }
            }
        }

        return value;
    }

    private static void ListContent(GenContext c, ContentBuilder cb, FontChain chain, HashSet<int> used, DefaultAppearance da, double cw, double ch, double inset)
    {
        PdfDictionary a = c.Annotation;
        if (c.Inherited(a, "Opt") is not PdfArray opt || opt.Count == 0)
        {
            return;
        }

        double fs = da.Size > 0 ? da.Size : 12;
        double lh = fs * TextLayout.LineHeight;
        int top = c.Inherited(a, "TI") is PdfInteger ti ? (int)Math.Clamp(ti.Value, 0, int.MaxValue) : 0;

        var selected = new HashSet<string>(StringComparer.Ordinal);
        PdfObject? v = c.Inherited(a, "V");
        if (v is PdfArray va)
        {
            for (int i = 0; i < Math.Min(va.Count, c.Limits.MaxAppearanceItems); i++)
            {
                if (GenContext.TextOf(va[i]) is { } s)
                {
                    selected.Add(s);
                }
            }
        }
        else if (GenContext.TextOf(v) is { } one)
        {
            selected.Add(one);
        }

        var selectedIndex = new HashSet<int>();
        if (c.Inherited(a, "I") is PdfArray ia)
        {
            for (int i = 0; i < Math.Min(ia.Count, c.Limits.MaxAppearanceItems); i++)
            {
                selectedIndex.Add((int)ia.GetInt(i));
            }
        }

        string colour = ColourOp(da);
        int count = Math.Min(opt.Count, c.Limits.MaxAppearanceItems);
        double rowTop = ch - inset;
        int cur = -1;
        cb.Raw("/Tx BMC\nq\n").Rect(inset, inset, cw - (2 * inset), ch - (2 * inset)).Raw("W n\n");
        for (int i = top; i < count && rowTop > inset; i++, rowTop -= lh)
        {
            string export, display;
            if (opt[i] is PdfArray pair && pair.Count >= 2)
            {
                export = GenContext.TextOf(pair[0]) ?? string.Empty;
                display = GenContext.TextOf(pair[1]) ?? string.Empty;
            }
            else
            {
                export = display = GenContext.TextOf(opt[i]) ?? string.Empty;
            }

            bool isSelected = selectedIndex.Contains(i) || selected.Contains(export) || selected.Contains(display);
            if (isSelected)
            {
                cb.Raw("0.6 0.756 0.854 rg\n").Rect(inset, rowTop - lh, cw - (2 * inset), lh).Raw("f\n");
            }

            List<ShapedGlyph> glyphs = chain.Shape(display, Math.Min(c.Limits.MaxAppearanceText, 4096), false);
            c.NoteDropped(chain);
            if (glyphs.Count == 0)
            {
                continue;
            }

            cb.Raw("BT\n").Raw(colour);
            cur = -1;
            TextLayout.EmitLine(cb, chain, glyphs, 0, glyphs.Count, inset + Padding, rowTop - lh + ((lh - (TextLayout.CapHeight * fs)) / 2), fs, ref cur, used);
            cb.Raw("ET\n");
        }

        cb.Raw("Q\nEMC\n");
    }

    // ---- buttons ----

    private static bool IsOn(GenContext c, Kind kind)
    {
        PdfDictionary a = c.Annotation;
        PdfDictionary? n = a.GetDictionary("AP")?.GetDictionary("N");
        string? state = a.GetName("AS");
        if (state is not null)
        {
            return state != "Off";
        }

        string? v = GenContext.TextOf(c.Inherited(a, "V"));
        if (v is null || v == "Off")
        {
            return false;
        }

        if (kind == Kind.Check)
        {
            return true;
        }

        // A radio button is on when the field's value names this widget's on state: the key of its normal appearance
        // dictionary, the export from /Opt, or (ISO 32000-2 12.7.5.2.4) the widget's index among the field's kids.
        if (n is not null)
        {
            foreach (string key in n.Keys)
            {
                if (key != "Off" && key == v)
                {
                    return true;
                }
            }

            return false;
        }

        PdfDictionary? parent = a.GetDictionary("Parent");
        if (parent?.Get("Kids") is PdfArray kids)
        {
            int count = Math.Min(kids.Count, c.Limits.MaxAppearanceItems);
            for (int i = 0; i < count; i++)
            {
                if (ReferenceEquals(kids[i], a) || (kids[i] is PdfDictionary kd && kd.Reference is { } kr && a.Reference is { } ar && kr.Number == ar.Number))
                {
                    string idx = i.ToString(CultureInfo.InvariantCulture);
                    if (c.Inherited(a, "Opt") is PdfArray opt && i < opt.Count && GenContext.TextOf(opt[i]) is { } exp)
                    {
                        return v == exp || v == idx;
                    }

                    return v == idx;
                }
            }
        }

        return false;
    }

    private static void MarkContent(GenContext c, ContentBuilder cb, PdfDictionary? mk, DefaultAppearance da, double cw, double ch, double inset, Kind kind, ref FontChain? chain, HashSet<int> used)
    {
        string ca = GenContext.TextOf(mk?.Get("CA")) is { Length: > 0 } s ? s : kind == Kind.Radio ? "l" : "4";
        char mark = ca[0];
        double size = Math.Min(cw, ch) - (2 * inset);
        if (!(size > 0))
        {
            return;
        }

        double cx = cw / 2, cy = ch / 2;
        cb.Raw("q\n");
        if (!cb.Color(da.Color, false))
        {
            cb.Gray(0, false);
        }

        if (!cb.Color(da.Color, true))
        {
            cb.Gray(0, true);
        }

        double u = size * 0.9;
        double x0 = cx - (u / 2), y0 = cy - (u / 2);
        switch (mark)
        {
            case '4':
                // The ZapfDingbats check mark, drawn as a stroked polyline so it needs no font.
                cb.Op("w", u * 0.13).Raw("1 J 1 j\n").Move(x0 + (u * 0.15), y0 + (u * 0.5)).Line(x0 + (u * 0.4), y0 + (u * 0.22)).Line(x0 + (u * 0.85), y0 + (u * 0.82)).Raw("S\n");
                break;
            case '8':
                cb.Op("w", u * 0.13).Raw("1 J\n").Move(x0 + (u * 0.2), y0 + (u * 0.2)).Line(x0 + (u * 0.8), y0 + (u * 0.8)).Move(x0 + (u * 0.2), y0 + (u * 0.8)).Line(x0 + (u * 0.8), y0 + (u * 0.2)).Raw("S\n");
                break;
            case 'l':
                cb.Ellipse(cx - (u * 0.3), cy - (u * 0.3), u * 0.6, u * 0.6).Raw("f\n");
                break;
            case 'u':
                cb.Move(cx, cy + (u * 0.4)).Line(cx + (u * 0.4), cy).Line(cx, cy - (u * 0.4)).Line(cx - (u * 0.4), cy).Raw("h f\n");
                break;
            case 'n':
                cb.Rect(cx - (u * 0.3), cy - (u * 0.3), u * 0.6, u * 0.6).Raw("f\n");
                break;
            default:
                chain = new FontChain(c.Cache.Get(c.Doc, c.Cache.Standard("ZapfDingbats")), []);
                if (chain[0].TryEncode(mark, out byte code, out double adv) || chain[0].TryEncode(0x2022, out code, out adv))
                {
                    double fs = u;
                    var one = new List<ShapedGlyph> { new(0, code, adv, false, false) };
                    int cur = -1;
                    cb.Raw("BT\n");
                    TextLayout.EmitLine(cb, chain, one, 0, 1, cx - (adv * fs / 2), cy - (0.35 * fs), fs, ref cur, used);
                    cb.Raw("ET\n");
                }
                else
                {
                    c.Info("The check mark character '" + mark + "' is not in the ZapfDingbats font; nothing was drawn for it");
                }

                break;
        }

        cb.Raw("Q\n");
    }

    private static void CaptionContent(GenContext c, ContentBuilder cb, FontChain chain, HashSet<int> used, DefaultAppearance da, double cw, double ch, double inset, PdfDictionary? mk)
    {
        string text = GenContext.TextOf(mk?.Get("CA")) ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        List<ShapedGlyph> glyphs = chain.Shape(text, Math.Min(c.Limits.MaxAppearanceText, 4096), false);
        c.NoteDropped(chain);
        if (glyphs.Count == 0)
        {
            return;
        }

        double availW = cw - (2 * inset) - (2 * Padding), availH = ch - (2 * inset);
        if (!(availW > 0 && availH > 0))
        {
            return;
        }

        double w1 = TextLayout.Width(glyphs, 0, glyphs.Count);
        double fs = da.Size;
        if (fs <= 0)
        {
            fs = Math.Clamp(availH * 0.6, 4, 12);
        }

        if (w1 * fs > availW && da.Size <= 0)
        {
            fs = Math.Max(4, availW / w1);
        }

        double x = Math.Max(inset + Padding, inset + Padding + ((availW - (w1 * fs)) / 2));
        int cur = -1;
        cb.Raw("q\n").Rect(inset, inset, cw - (2 * inset), ch - (2 * inset)).Raw("W n\nBT\n").Raw(ColourOp(da));
        TextLayout.EmitLine(cb, chain, glyphs, 0, glyphs.Count, x, (ch - (TextLayout.CapHeight * fs)) / 2, fs, ref cur, used);
        cb.Raw("ET\nQ\n");
    }
}
