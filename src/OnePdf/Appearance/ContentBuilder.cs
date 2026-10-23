using System.Globalization;
using System.Text;

namespace OnePdf.Appearance;

/// <summary>Builds the content stream of a generated appearance. Numbers are written with at most four decimals in the invariant culture.</summary>
internal sealed class ContentBuilder
{
    private readonly StringBuilder _sb = new();

    public int Length => _sb.Length;

    public static string Num(double v)
    {
        if (!double.IsFinite(v))
        {
            return "0";
        }

        double r = Math.Round(v, 4, MidpointRounding.AwayFromZero);
        if (r == 0)
        {
            return "0";
        }

        return r.ToString("0.####", CultureInfo.InvariantCulture);
    }

    public ContentBuilder Raw(string s)
    {
        _sb.Append(s);
        return this;
    }

    public ContentBuilder Op(string op, params double[] operands)
    {
        foreach (double d in operands)
        {
            _sb.Append(Num(d)).Append(' ');
        }

        _sb.Append(op).Append('\n');
        return this;
    }

    public ContentBuilder Move(double x, double y) => Op("m", x, y);

    public ContentBuilder Line(double x, double y) => Op("l", x, y);

    public ContentBuilder Curve(double x1, double y1, double x2, double y2, double x3, double y3) => Op("c", x1, y1, x2, y2, x3, y3);

    public ContentBuilder Rect(double x, double y, double w, double h) => Op("re", x, y, w, h);

    /// <summary>An ellipse inscribed in the box as four Bezier curves (kappa 0.5522847498).</summary>
    public ContentBuilder Ellipse(double x, double y, double w, double h)
    {
        const double k = 0.5522847498;
        double rx = w / 2, ry = h / 2, cx = x + rx, cy = y + ry;
        Move(cx + rx, cy);
        Curve(cx + rx, cy + (ry * k), cx + (rx * k), cy + ry, cx, cy + ry);
        Curve(cx - (rx * k), cy + ry, cx - rx, cy + (ry * k), cx - rx, cy);
        Curve(cx - rx, cy - (ry * k), cx - (rx * k), cy - ry, cx, cy - ry);
        Curve(cx + (rx * k), cy - ry, cx + rx, cy - (ry * k), cx + rx, cy);
        return Raw("h\n");
    }

    /// <summary>Sets the fill or stroke colour from 1, 3 or 4 components; other counts leave the colour unchanged and return false.</summary>
    public bool Color(IReadOnlyList<double>? c, bool stroke)
    {
        if (c is null)
        {
            return false;
        }

        string op;
        switch (c.Count)
        {
            case 1:
                op = stroke ? "G" : "g";
                break;
            case 3:
                op = stroke ? "RG" : "rg";
                break;
            case 4:
                op = stroke ? "K" : "k";
                break;
            default:
                return false;
        }

        foreach (double d in c)
        {
            _sb.Append(Num(Math.Clamp(double.IsFinite(d) ? d : 0, 0, 1))).Append(' ');
        }

        _sb.Append(op).Append('\n');
        return true;
    }

    public ContentBuilder Gray(double g, bool stroke) => Op(stroke ? "G" : "g", g);

    /// <summary>Appends a string literal of the given single-byte codes; every byte outside printable ASCII is written as an octal escape.</summary>
    public ContentBuilder Literal(ReadOnlySpan<byte> codes)
    {
        _sb.Append('(');
        foreach (byte b in codes)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                _sb.Append('\\').Append((char)b);
            }
            else if (b is < 32 or > 126)
            {
                _sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
            else
            {
                _sb.Append((char)b);
            }
        }

        _sb.Append(')');
        return this;
    }

    public byte[] ToBytes() => Encoding.Latin1.GetBytes(_sb.ToString());

    public override string ToString() => _sb.ToString();
}

/// <summary>Creates the small in-memory COS objects a generated appearance is made of.</summary>
internal static class Cos
{
    public static PdfDictionary Dict() => new(null);

    public static PdfName Name(string v) => new(v);

    public static PdfReal Real(double v) => new(double.IsFinite(v) ? v : 0);

    public static PdfInteger Int(long v) => new(v);

    public static PdfBoolean Bool(bool v) => new(v);

    public static PdfArray Array(params double[] v)
    {
        var a = new PdfArray(null);
        foreach (double d in v)
        {
            a.Add(Real(d));
        }

        return a;
    }

    public static PdfDictionary With(this PdfDictionary d, string key, PdfObject value)
    {
        d.Set(key, value);
        return d;
    }
}
