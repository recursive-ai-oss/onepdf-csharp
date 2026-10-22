namespace OnePdf.Content;

/// <summary>
/// Optional content visibility from the document's default configuration (<c>/OCProperties /D</c>), with a per-group
/// override from the caller. The configuration's <c>/BaseState</c>, <c>/ON</c> and <c>/OFF</c> lists give each group's state
/// (ISO 32000-2 8.11.4.3); then the usage-application dictionaries (<c>/AS</c>) for the <c>View</c> event adjust it, as a
/// viewer does when it opens the document (8.11.4.4, 8.11.4.6). Only the <c>View</c> category has an answer without a viewer
/// context (<c>/View /ViewState</c>); <c>Zoom</c>, <c>User</c>, <c>Language</c>, <c>Print</c> and <c>Export</c> make no
/// recommendation here, so a group they alone name keeps its configured state.
/// </summary>
internal sealed class OptionalContentState
{
    private const int MaxExpressionDepth = 16;
    private const int MaxUsageApplications = 4096;
    private readonly Func<PdfDictionary, bool?>? _override;
    private readonly Dictionary<int, bool> _defaults = [];

    public OptionalContentState(PdfDocument doc, Func<PdfDictionary, bool?>? overrideFunc)
    {
        _override = overrideFunc;
        foreach (PdfOptionalContentGroup g in doc.OptionalContentGroups)
        {
            if (g.Dictionary.Reference is { } r)
            {
                _defaults[r.Number] = g.IsVisibleByDefault;
            }
        }

        if (_defaults.Count > 0 && doc.Catalog.GetDictionary("OCProperties")?.GetDictionary("D")?.GetArray("AS") is { } auto)
        {
            ApplyViewEvent(auto);
        }
    }

    // ISO 32000-2 8.11.4.6: for each group of a View usage application dictionary, the recommendations of its categories
    // are combined; the group is ON only if all of them are ON, and a group in several dictionaries must satisfy all.
    private void ApplyViewEvent(PdfArray auto)
    {
        var recommended = new Dictionary<int, bool>();
        for (int i = 0; i < auto.Count && i < MaxUsageApplications; i++)
        {
            if (auto[i] is not PdfDictionary app || app.GetName("Event") != "View" || app.GetArray("OCGs") is not { } groups || app.GetArray("Category") is not { } categories)
            {
                continue;
            }

            bool usesView = false;
            for (int c = 0; c < categories.Count && c < 16; c++)
            {
                usesView |= categories.GetName(c) == "View";
            }

            if (!usesView)
            {
                continue;
            }

            for (int g = 0; g < groups.Count && g < MaxUsageApplications; g++)
            {
                if (groups[g] is PdfDictionary group && group.Reference is { } r && _defaults.ContainsKey(r.Number)
                    && group.GetDictionary("Usage")?.GetDictionary("View")?.GetName("ViewState") is { } state and ("ON" or "OFF"))
                {
                    bool on = state == "ON";
                    recommended[r.Number] = recommended.TryGetValue(r.Number, out bool before) ? before && on : on;
                }
            }
        }

        foreach (KeyValuePair<int, bool> kv in recommended)
        {
            _defaults[kv.Key] = kv.Value;
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

        return g.Reference is not { } r || !_defaults.TryGetValue(r.Number, out bool visible) || visible;
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
    public static PdfPageInfo Compute(PdfPage page, PdfInterpretOptions o, Action<string> warn)
    {
        PdfRectangle r = o.Box switch
        {
            PdfPageBox.MediaBox => page.MediaBox,
            PdfPageBox.BleedBox => page.BleedBox,
            PdfPageBox.TrimBox => page.TrimBox,
            PdfPageBox.ArtBox => page.ArtBox,
            _ => page.CropBox,
        };
        PdfRectangle media = page.MediaBox;

        // Every box is limited to the media box (ISO 32000-2 14.11.2).
        PdfRectangle box = r.Intersect(media) ?? media;
        PdfPageBox name = o.Box;
        if (box.IsEmpty)
        {
            warn("The page box has no area; US Letter is assumed.");
            box = new PdfRectangle(0, 0, 612, 792);
        }

        int rotation = o.ApplyRotation ? page.Rotate : 0;
        double unit = o.ApplyUserUnit ? page.UserUnit : 1;
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

        return new PdfPageInfo(new PdfBounds(box.Left, box.Bottom, box.Right, box.Top), name, rotation, unit, s, outW, outH, baseMatrix, page.Dictionary);
    }
}
