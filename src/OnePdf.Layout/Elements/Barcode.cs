using System.Globalization;
using OnePdf.Layout.Barcodes;
using OnePdf.Layout.Pagination;

namespace OnePdf.Layout;

/// <summary>
/// A barcode drawn as vector rectangles: QR Code, Code 128 (and GS1-128), EAN-13, EAN-8, UPC-A or Code 39. The encoders are part of this
/// package and follow the standards (ISO/IEC 18004, 15417, 15420, 16388). Never splits; in a tagged document it is a <c>Figure</c> whose
/// alternate text is <see cref="Element.AltText"/>, or else <see cref="Value"/>.
/// </summary>
/// <remarks>
/// <para>Sizes are set by the module, the narrowest bar or the dark square of a QR Code. Every module of a symbol has the same size, which is
/// never rounded: the symbol is exactly a whole number of modules wide. <see cref="ModuleSize"/> picks it (a printer-friendly value such as
/// 0.75 pt keeps edges on the device grid); <see cref="Contain"/> takes the largest size that fits the space offered; otherwise the module is
/// 2 pt for QR Code and 1 pt for the linear symbologies. A symbol wider or taller than the space offered is shrunk to fit and a note is recorded.</para>
/// <para>Linear symbols are <see cref="BarHeight"/> tall (40 modules by default). The quiet zone, the empty margin the symbology requires
/// around the bars, is part of the element's box. With <see cref="HumanReadableText"/> the digits or characters are set under the bars in
/// the document's text pipeline: EAN and UPC in the layout of the standard, other symbologies centred. The text is decoration; the figure's
/// alternate text carries the value.</para>
/// <para>A value the symbology cannot encode raises <see cref="PdfBarcodeException"/> when the element is laid out.</para>
/// </remarks>
public sealed class Barcode : Element
{
    private double? _moduleSize;
    private double? _quietZone;
    private double? _barHeight;
    private int _minVersion = 1;
    private int _maxVersion = 40;
    private string? _cacheKey;
    private Encoded? _cache;

    /// <summary>Which symbology to draw. Default <see cref="BarcodeSymbology.QrCode"/>.</summary>
    public BarcodeSymbology Symbology { get; set; }

    /// <summary>
    /// The data. QR Code takes any text; Code 128 characters 0 to 127; GS1-128 application identifiers in parentheses; EAN and UPC digits
    /// (without the check digit, or with it, which is then verified); Code 39 its 43 characters.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>QR Code only: the error correction level. Default <see cref="QrErrorCorrection.Medium"/>.</summary>
    public QrErrorCorrection ErrorCorrection { get; set; } = QrErrorCorrection.Medium;

    /// <summary>QR Code only: the smallest version to use, 1 to 40. The smallest version that holds the data is chosen from here. Default 1.</summary>
    public int MinVersion
    {
        get => _minVersion;
        set => _minVersion = value is >= 1 and <= 40 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The QR version must be 1 to 40.");
    }

    /// <summary>QR Code only: the largest version allowed, 1 to 40; data that needs more raises <see cref="PdfBarcodeException"/>. Default 40.</summary>
    public int MaxVersion
    {
        get => _maxVersion;
        set => _maxVersion = value is >= 1 and <= 40 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The QR version must be 1 to 40.");
    }

    /// <summary>QR Code only: how text is turned into bytes. Default <see cref="QrCharacterSet.Utf8"/>.</summary>
    public QrCharacterSet CharacterSet { get; set; }

    /// <summary>Code 39 only: add the optional modulo-43 check character. Default false.</summary>
    public bool Code39CheckDigit { get; set; }

    /// <summary>The size of one module in points. Null, the default, is 2 for QR Code and 1 for the other symbologies, or the fitted size with <see cref="Contain"/>.</summary>
    public double? ModuleSize
    {
        get => _moduleSize;
        set => _moduleSize = value is null or > 0 and < double.PositiveInfinity ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The module size must be positive.");
    }

    /// <summary>Takes the largest module size at which the symbol, quiet zone included, fits the width (and, for QR Code, the height) offered. Ignored when <see cref="ModuleSize"/> is set. Default false.</summary>
    public bool Contain { get; set; }

    /// <summary>
    /// The quiet zone in modules, on every side of a QR Code and left and right of a linear symbol (the bars are as tall as set, with no margin
    /// above and below). Null is the standard's: 4 for QR Code, 10 for Code 128 and Code 39, 11 and 7 for EAN-13, 9 for UPC-A, 7 for EAN-8. Zero is
    /// allowed. For EAN-13 and UPC-A with <see cref="HumanReadableText"/> the digits outside the bars sit in the quiet zone, which is then at least 7 modules on that side.
    /// </summary>
    public double? QuietZone
    {
        get => _quietZone;
        set => _quietZone = value is null or >= 0 and < 1000 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The quiet zone must be 0 or more modules.");
    }

    /// <summary>The height of the bars of a linear symbol, in points. Null, the default, is 40 modules, or what is left of the height offered with <see cref="Contain"/>.</summary>
    public double? BarHeight
    {
        get => _barHeight;
        set => _barHeight = value is null or > 0 and < double.PositiveInfinity ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bar height must be positive.");
    }

    /// <summary>The colour of the bars and of the human-readable text. Default black.</summary>
    public LayoutColor Foreground { get; set; } = LayoutColor.Black;

    /// <summary>A colour for the whole box, quiet zone included. Null, the default, paints nothing, so the page shows through.</summary>
    public LayoutColor? Background { get; set; }

    /// <summary>Whether to set the encoded characters under a linear symbol. Null, the default, is true for EAN and UPC, whose standard includes the digits, and false for the others. Has no effect on QR Code.</summary>
    public bool? HumanReadableText { get; set; }

    /// <summary>The font family of the human-readable text, as for <see cref="Text.FontName"/>. Null is the text default.</summary>
    public string? FontName { get; set; }

    /// <summary>The weight of the human-readable text. Default 400.</summary>
    public int Weight { get; set; } = 400;

    /// <summary>The size of the human-readable text in points. Default 9; it shrinks, all groups alike, when the digits would not fit their place under the bars.</summary>
    public double TextSize { get; set; } = 9;

    internal void InheritFrom(SpanFormat d)
    {
        if (FontName is null && d.Families is { Length: > 0 } fam)
        {
            FontName = fam[0];
        }
    }

    internal sealed class Encoded
    {
        public QrSymbol? Qr;
        public LinearSymbol? Linear;
    }

    private Encoded Encode()
    {
        string key = string.Join('|', (int)Symbology, Value, (int)ErrorCorrection, _minVersion, _maxVersion, (int)CharacterSet, Code39CheckDigit, _quietZone?.ToString(CultureInfo.InvariantCulture), HumanReadableText?.ToString(), ShowText());
        if (_cache is not null && key == _cacheKey)
        {
            return _cache;
        }

        string value = Value ?? string.Empty;
        var result = new Encoded();
        switch (Symbology)
        {
            case BarcodeSymbology.QrCode:
                result.Qr = QrEncoder.Encode(value, ErrorCorrection, _minVersion, _maxVersion, CharacterSet);
                break;
            case BarcodeSymbology.Code128:
                result.Linear = Code128.Create(value, false, _quietZone);
                break;
            case BarcodeSymbology.Gs1128:
                result.Linear = Code128.Create(value, true, _quietZone);
                break;
            case BarcodeSymbology.Ean13:
                result.Linear = Retail.Ean13(value, _quietZone, ShowText(), false);
                break;
            case BarcodeSymbology.UpcA:
                result.Linear = Retail.Ean13(value, _quietZone, ShowText(), true);
                break;
            case BarcodeSymbology.Ean8:
                result.Linear = Retail.Ean8(value, _quietZone, ShowText());
                break;
            case BarcodeSymbology.Code39:
                result.Linear = Code39.Create(value, Code39CheckDigit, _quietZone);
                break;
            default:
                throw new PdfBarcodeException(Symbology, "Unknown barcode symbology.");
        }

        _cache = result;
        _cacheKey = key;
        return result;
    }

    private bool ShowText() => Symbology != BarcodeSymbology.QrCode && (HumanReadableText ?? Symbology is BarcodeSymbology.Ean13 or BarcodeSymbology.Ean8 or BarcodeSymbology.UpcA);

    internal sealed record Data(Encoded Encoded, double ModuleSize, double BarHeight, double QuietLeft, double QuietTop);

    internal override bool FigureByDefault => true;

    internal override string? FigureAltText => AltText ?? (string.IsNullOrEmpty(Value) ? null : Value);

    internal override FlowFit LayoutCore(LayoutContext ctx, in Constraint c, object? resume)
    {
        Encoded enc = Encode();
        if (enc.Qr is QrSymbol qr)
        {
            return LayoutQr(ctx, c, enc, qr);
        }

        LinearSymbol lin = enc.Linear!;
        double cols = lin.LeftQuiet + lin.Modules.Length + lin.RightQuiet;
        double m = _moduleSize ?? (Contain && c.WidthBounded ? c.Width / cols : 1);
        if (c.WidthBounded && cols * m > c.Width + 1e-9)
        {
            ctx.NoteOnce(this, "A barcode is wider than the space offered; its module size was reduced to fit.");
            m = c.Width / cols;
        }

        double width = cols * m;
        bool text = ShowText();
        var children = new List<Placed>();
        double textHeight = 0;
        bool retail = lin.Extended is not null;
        if (text)
        {
            BuildText(ctx, lin, m, children, out textHeight);
        }

        double gap = text ? (retail ? m : 2 * m) : 0;
        double barH = _barHeight ?? (Contain && c.HeightBounded ? c.Height - gap - textHeight : 40 * m);
        double extension = retail ? 5 * m : 0;
        double total = barH + gap + textHeight;
        if (retail && !text)
        {
            total = barH + extension;
        }
        else if (retail)
        {
            total = Math.Max(total, barH + extension);
        }

        if (barH <= 0 || total > c.Height + 1e-9)
        {
            return ctx.NoFit(c, width, total, "The barcode is taller than the space offered");
        }

        double textTop = barH + gap;
        foreach (Placed child in children)
        {
            child.Y = textTop;
        }

        var node = new Placed(this, 0, 0, width, total) { Data = new Data(enc, m, barH, lin.LeftQuiet, 0) };
        foreach (Placed child in children)
        {
            node.Children.Add(child);
        }

        return FlowFit.Complete(total, node);
    }

    private FlowFit LayoutQr(LayoutContext ctx, in Constraint c, Encoded enc, QrSymbol qr)
    {
        double q = _quietZone ?? 4;
        double cols = qr.Size + (2 * q);
        double m = _moduleSize ?? 2;
        if (_moduleSize is null && Contain)
        {
            double fit = double.PositiveInfinity;
            if (c.WidthBounded)
            {
                fit = Math.Min(fit, c.Width / cols);
            }

            if (c.HeightBounded)
            {
                fit = Math.Min(fit, c.Height / cols);
            }

            if (!double.IsInfinity(fit))
            {
                m = fit;
            }
        }

        double maxM = Math.Min(c.WidthBounded ? c.Width / cols : double.PositiveInfinity, c.HeightBounded ? c.Height / cols : double.PositiveInfinity);
        if (m > maxM + 1e-12)
        {
            ctx.NoteOnce(this, "A QR Code is larger than the space offered; its module size was reduced to fit.");
            m = maxM;
        }

        double size = cols * m;
        var node = new Placed(this, 0, 0, size, size) { Data = new Data(enc, m, size, q, q) };
        return FlowFit.Complete(size, node);
    }

    // Sets the digits or characters under a linear symbol and reports the height of the line. The text shrinks, all groups by the same factor,
    // until every group fits the room its place under the bars gives it.
    private void BuildText(LayoutContext ctx, LinearSymbol lin, double m, List<Placed> children, out double height)
    {
        TextGroup[] groups = lin.Groups ?? [new TextGroup(lin.Text, 0, lin.Modules.Length)];
        Text Make(string content, double size) => new()
        {
            Content = content,
            FontName = FontName,
            Weight = Weight,
            Size = size,
            Color = Foreground,
            LineSpacing = 1,
            Alignment = HorizontalAlignment.Center,
            Artifact = true,
        };

        double scale = 1;
        foreach (TextGroup g in groups)
        {
            double room = (g.ToModule - g.FromModule) * m;
            double natural = ctx.Text.Prepare(Make(g.Text, TextSize), 1e6, ctx).NaturalWidth;
            if (natural > room && natural > 0)
            {
                scale = Math.Min(scale, room / natural);
            }
        }

        height = 0;
        foreach (TextGroup g in groups)
        {
            Text t = Make(g.Text, TextSize * scale * 0.999);
            double room = (g.ToModule - g.FromModule) * m;
            FlowFit f = t.Layout(ctx, new Constraint(room, double.PositiveInfinity, true), null);
            if (f.Placed is Placed placed)
            {
                placed.X = (lin.LeftQuiet + g.FromModule) * m;
                height = Math.Max(height, placed.Height);
                children.Add(placed);
            }
        }
    }

    internal override void DrawCore(in DrawContext d, Placed p)
    {
        var data = (Data)p.Data!;
        PdfContentBuilder b = d.B;
        double m = data.ModuleSize;
        bool tagged = d.Ctx.Tag.BeginContent();
        b.Save();
        if (Background is LayoutColor bg)
        {
            b.SetFillRgb(bg.R, bg.G, bg.B);
            b.Rectangle(d.X, d.PdfY(p.Height), p.Width, p.Height);
            b.Fill();
        }

        b.SetFillRgb(Foreground.R, Foreground.G, Foreground.B);
        if (data.Encoded.Qr is QrSymbol qr)
        {
            foreach ((int x, int y, int w, int h) in QrRectangles(qr))
            {
                b.Rectangle(d.X + ((data.QuietLeft + x) * m), d.PdfY((data.QuietTop + y + h) * m), w * m, h * m);
            }
        }
        else
        {
            LinearSymbol lin = data.Encoded.Linear!;
            double ext = lin.Extended is null ? 0 : 5 * m;
            bool[] mod = lin.Modules;
            for (int i = 0; i < mod.Length;)
            {
                if (!mod[i])
                {
                    i++;
                    continue;
                }

                bool tall = lin.Extended?[i] ?? false;
                int j = i + 1;
                while (j < mod.Length && mod[j] && (lin.Extended?[j] ?? false) == tall)
                {
                    j++;
                }

                double h = data.BarHeight + (tall ? ext : 0);
                b.Rectangle(d.X + ((lin.LeftQuiet + i) * m), d.PdfY(h), (j - i) * m, h);
                i = j;
            }
        }

        b.Fill();
        b.Restore();
        d.Ctx.Tag.EndContent(tagged);
        DrawChildren(d, p);
    }

    // Runs of dark modules, joined with the identical run of the row above so that no hairline seams show between rows when rasterised.
    internal static List<(int X, int Y, int W, int H)> QrRectangles(QrSymbol qr)
    {
        var result = new List<(int, int, int, int)>();
        var open = new Dictionary<(int, int), int>();
        var next = new Dictionary<(int, int), int>();
        for (int y = 0; y < qr.Size; y++)
        {
            next.Clear();
            for (int x = 0; x < qr.Size;)
            {
                if (!qr[x, y])
                {
                    x++;
                    continue;
                }

                int x2 = x + 1;
                while (x2 < qr.Size && qr[x2, y])
                {
                    x2++;
                }

                (int, int) key = (x, x2 - x);
                if (open.TryGetValue(key, out int index))
                {
                    (int rx, int ry, int rw, int rh) = result[index];
                    result[index] = (rx, ry, rw, rh + 1);
                    next[key] = index;
                }
                else
                {
                    result.Add((x, y, x2 - x, 1));
                    next[key] = result.Count - 1;
                }

                x = x2;
            }

            (open, next) = (next, open);
        }

        return result;
    }
}
