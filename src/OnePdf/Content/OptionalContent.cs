namespace OnePdf.Content;

/// <summary>
/// Optional content visibility from the document's default configuration (<c>/OCProperties /D</c>), with a per-group
/// override from the caller. Usage-application dictionaries (<c>/AS</c>) are not consulted: the default configuration's
/// <c>/BaseState</c>, <c>/ON</c> and <c>/OFF</c> lists decide (ISO 32000-2 8.11.4.3).
/// </summary>
internal sealed class OptionalContentState
{
    private const int MaxExpressionDepth = 16;
    private readonly Func<PdfDictionary, bool?>? _override;
    private readonly HashSet<int> _on = [];
    private readonly HashSet<int> _off = [];
    private readonly bool _baseOn = true;
    private readonly bool _any;

    public OptionalContentState(PdfDocument doc, Func<PdfDictionary, bool?>? overrideFunc)
    {
        _override = overrideFunc;
        PdfDictionary? props = null;
        try
        {
            props = doc.Catalog.GetDictionary("OCProperties");
        }
        catch (PdfFormatException)
        {
            // A document without a catalog has no optional content.
        }

        PdfDictionary? d = props?.GetDictionary("D");
        _any = props is not null;
        if (d is null)
        {
            return;
        }

        _baseOn = d.GetName("BaseState") != "OFF";
        Collect(d.GetArray("ON"), _on);
        Collect(d.GetArray("OFF"), _off);
    }

    private static void Collect(PdfArray? a, HashSet<int> into)
    {
        if (a is null)
        {
            return;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a.GetRaw(i) is PdfReference r)
            {
                into.Add(r.Number);
            }
        }
    }

    /// <summary>True when content tagged with <paramref name="oc"/> (an OCG or OCMD dictionary) is visible.</summary>
    public bool IsVisible(PdfObject? oc)
    {
        if (oc is not PdfDictionary d)
        {
            return true;
        }

        return Evaluate(d, 0);
    }

    private bool Evaluate(PdfDictionary d, int depth)
    {
        string? type = d.GetName("Type");
        if (type == "OCMD")
        {
            return EvaluateMembership(d, depth);
        }

        return GroupVisible(d);
    }

    private bool GroupVisible(PdfDictionary g)
    {
        if (_override?.Invoke(g) is { } forced)
        {
            return forced;
        }

        if (!_any || g.Reference is not { } r)
        {
            return true;
        }

        if (_off.Contains(r.Number))
        {
            return false;
        }

        if (_on.Contains(r.Number))
        {
            return true;
        }

        return _baseOn;
    }

    private bool EvaluateMembership(PdfDictionary ocmd, int depth)
    {
        if (ocmd.GetArray("VE") is { } ve)
        {
            return EvaluateExpression(ve, depth + 1);
        }

        var groups = new List<PdfDictionary>();
        PdfObject? ocgs = ocmd.Get("OCGs");
        if (ocgs is PdfDictionary single)
        {
            groups.Add(single);
        }
        else if (ocgs is PdfArray arr)
        {
            for (int i = 0; i < arr.Count && groups.Count < 1024; i++)
            {
                if (arr[i] is PdfDictionary g)
                {
                    groups.Add(g);
                }
            }
        }

        if (groups.Count == 0)
        {
            return true;
        }

        string policy = ocmd.GetName("P") ?? "AnyOn";
        int on = 0;
        foreach (PdfDictionary g in groups)
        {
            if (GroupVisible(g))
            {
                on++;
            }
        }

        return policy switch
        {
            "AllOn" => on == groups.Count,
            "AnyOff" => on < groups.Count,
            "AllOff" => on == 0,
            _ => on > 0,
        };
    }

    private bool EvaluateExpression(PdfArray expr, int depth)
    {
        if (depth > MaxExpressionDepth || expr.Count == 0)
        {
            return true;
        }

        string? op = expr.GetName(0);
        if (op is not ("And" or "Or" or "Not"))
        {
            return true;
        }

        bool Operand(int i) => expr[i] switch
        {
            PdfDictionary g => Evaluate(g, depth),
            PdfArray sub => EvaluateExpression(sub, depth + 1),
            _ => true,
        };

        if (op == "Not")
        {
            return expr.Count < 2 || !Operand(1);
        }

        bool and = op == "And";
        bool result = and;
        for (int i = 1; i < expr.Count && i < 256; i++)
        {
            bool v = Operand(i);
            if (and)
            {
                result &= v;
            }
            else
            {
                result |= v;
            }
        }

        return result;
    }
}

/// <summary>Resolves the page box, rotation and unit into the transform from default user space to the output space.</summary>
internal static class PageGeometry
{
    private const int MaxParentChain = 64;

    /// <summary>Looks up an inheritable page attribute along the <c>/Parent</c> chain (ISO 32000-2 7.7.3.4).</summary>
    public static PdfObject? Inherited(PdfDictionary page, string key)
    {
        PdfDictionary? node = page;
        for (int depth = 0; node is not null && depth < MaxParentChain; depth++)
        {
            if (node.Get(key) is { } v)
            {
                return v;
            }

            node = node.GetDictionary("Parent");
        }

        return null;
    }

    private static PdfBounds? ReadBox(PdfObject? o)
    {
        if (o is not PdfArray a || a.Count < 4)
        {
            return null;
        }

        for (int i = 0; i < 4; i++)
        {
            if (a[i] is not (PdfInteger or PdfReal))
            {
                return null;
            }
        }

        var b = PdfBounds.FromCorners(a.GetDouble(0), a.GetDouble(1), a.GetDouble(2), a.GetDouble(3));
        return b.IsFinite && b.Width > 0 && b.Height > 0 ? b : null;
    }

    public static PdfPageInfo Compute(PdfDictionary page, PdfInterpretOptions o, Action<string> warn)
    {
        PdfBounds media;
        if (ReadBox(Inherited(page, "MediaBox")) is { } m)
        {
            media = m;
        }
        else
        {
            warn("The page has no usable /MediaBox; US Letter is assumed.");
            media = new PdfBounds(0, 0, 612, 792);
        }

        PdfBounds? crop = ReadBox(Inherited(page, "CropBox"));
        PdfPageBox name = o.Box;
        PdfBounds? chosen = o.Box switch
        {
            PdfPageBox.MediaBox => media,
            PdfPageBox.BleedBox => ReadBox(page.Get("BleedBox")) ?? crop,
            PdfPageBox.TrimBox => ReadBox(page.Get("TrimBox")) ?? crop,
            PdfPageBox.ArtBox => ReadBox(page.Get("ArtBox")) ?? crop,
            _ => crop,
        };
        if (chosen is null)
        {
            chosen = media;
            name = PdfPageBox.MediaBox;
        }

        // Every box is limited to the media box (ISO 32000-2 14.11.2).
        PdfBounds box = chosen.Value;
        var clipped = new PdfBounds(Math.Max(box.Left, media.Left), Math.Max(box.Bottom, media.Bottom), Math.Min(box.Right, media.Right), Math.Min(box.Top, media.Top));
        if (clipped.Width > 0 && clipped.Height > 0)
        {
            box = clipped;
        }
        else
        {
            warn("A page box lies outside the media box; the media box is used.");
            box = media;
            name = PdfPageBox.MediaBox;
        }

        int rotation = 0;
        if (o.ApplyRotation)
        {
            long r = Inherited(page, "Rotate") switch
            {
                PdfInteger i => i.Value,
                PdfReal re when double.IsFinite(re.Value) && Math.Abs(re.Value) < 1e9 => (long)re.Value,
                _ => 0,
            };
            rotation = (int)(((r % 360) + 360) % 360);
            if (rotation % 90 != 0)
            {
                warn("The page /Rotate is not a multiple of 90; it is ignored.");
                rotation = 0;
            }
        }

        double unit = 1;
        if (o.ApplyUserUnit)
        {
            double u = page.GetDouble("UserUnit", 1);
            if (u > 0 && double.IsFinite(u))
            {
                unit = u;
            }
        }

        double requested = o.Dpi > 0 && double.IsFinite(o.Dpi) ? o.Dpi / 72.0 : o.Scale;
        if (!(requested > 0) || !double.IsFinite(requested))
        {
            requested = 1;
        }

        double s = requested * unit;
        double w = box.Width * s, h = box.Height * s;

        // Default space (y up, origin bottom-left of the box) to unrotated output (y down, origin top-left).
        var m0 = new PdfMatrix(s, 0, 0, -s, -box.Left * s, box.Top * s);
        PdfMatrix baseMatrix;
        double outW = w, outH = h;
        switch (rotation)
        {
            case 90:
                // Clockwise quarter turn: (x, y) -> (h - y, x).
                baseMatrix = m0.Then(new PdfMatrix(0, 1, -1, 0, h, 0));
                outW = h;
                outH = w;
                break;
            case 180:
                baseMatrix = m0.Then(new PdfMatrix(-1, 0, 0, -1, w, h));
                break;
            case 270:
                baseMatrix = m0.Then(new PdfMatrix(0, -1, 1, 0, 0, w));
                outW = h;
                outH = w;
                break;
            default:
                baseMatrix = m0;
                break;
        }

        return new PdfPageInfo(box, name, rotation, unit, s, outW, outH, baseMatrix, page);
    }
}
