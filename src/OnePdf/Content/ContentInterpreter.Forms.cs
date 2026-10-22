namespace OnePdf.Content;

internal sealed partial class ContentInterpreter
{
    private static readonly byte[] RectVerbs = [(byte)PdfPathVerb.Move, (byte)PdfPathVerb.Line, (byte)PdfPathVerb.Line, (byte)PdfPathVerb.Line, (byte)PdfPathVerb.Close];

    // ---- ExtGState ----

    private void ApplyExtGState(string name)
    {
        PdfDictionary? d = _res?.GetDictionary("ExtGState")?.GetDictionary(name);
        if (d is null)
        {
            Report(PdfSeverity.Warning, "Graphics state /" + name + " is not in the resources.");
            return;
        }

        bool hasLowerOp = d.ContainsKey("op");
        foreach (string key in d.Keys)
        {
            if (_stopped)
            {
                return;
            }

            switch (key)
            {
                case "LW":
                    _g.LineWidth = Math.Max(0, Finite(d.GetDouble("LW", _g.LineWidth), _g.LineWidth));
                    break;
                case "LC":
                    _g.LineCap = (int)Math.Clamp(d.GetInt("LC"), 0, 2);
                    break;
                case "LJ":
                    _g.LineJoin = (int)Math.Clamp(d.GetInt("LJ"), 0, 2);
                    break;
                case "ML":
                    _g.MiterLimit = Math.Max(1, Finite(d.GetDouble("ML", _g.MiterLimit), _g.MiterLimit));
                    break;
                case "D":
                    if (d.GetArray("D") is { Count: >= 2 } da && da[0] is PdfArray dashes)
                    {
                        ApplyDash(dashes, da.GetDouble(1));
                    }

                    break;
                case "RI":
                    if (d.GetName("RI") is { } ri)
                    {
                        _g.Intent = ri;
                    }

                    break;
                case "OP":
                    _g.StrokeOverprint = d.GetBool("OP");
                    if (!hasLowerOp)
                    {
                        _g.FillOverprint = _g.StrokeOverprint;
                    }

                    break;
                case "op":
                    _g.FillOverprint = d.GetBool("op");
                    break;
                case "OPM":
                    _g.OverprintMode = d.GetInt("OPM") == 0 ? 0 : 1;
                    break;
                case "Font":
                    if (d.GetArray("Font") is { Count: >= 2 } fa && fa[0] is PdfDictionary fd)
                    {
                        _g.FontSize = Finite(fa.GetDouble(1), _g.FontSize);
                        _g.Font = GetFont(string.Empty, fd, fa.GetRaw(0) as PdfReference);
                    }

                    break;
                case "FL":
                    _g.Flatness = Math.Clamp(Finite(d.GetDouble("FL", _g.Flatness), _g.Flatness), 0, 100);
                    break;
                case "CA":
                    _g.StrokeAlpha = Math.Clamp(Finite(d.GetDouble("CA", 1), 1), 0, 1);
                    break;
                case "ca":
                    _g.FillAlpha = Math.Clamp(Finite(d.GetDouble("ca", 1), 1), 0, 1);
                    break;
                case "AIS":
                    _g.Ais = d.GetBool("AIS");
                    break;
                case "BM":
                    _g.Blend = BlendModeName(d.Get("BM"));
                    break;
                case "SMask":
                    ApplySoftMask(d.Get("SMask"));
                    break;
                default:
                    // TK, SA, SM, TR, TR2, HT, BG, UCR and their variants affect only halftone/transfer or advisory behavior.
                    break;
            }
        }
    }

    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    private void ApplyDash(PdfArray dashes, double phase)
    {
        var arr = new double[Math.Min(dashes.Count, 1024)];
        double sum = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            double v = dashes.GetDouble(i, -1);
            if (!double.IsFinite(v) || v < 0)
            {
                Report(PdfSeverity.Warning, "A dash array with negative or non-numeric entries was skipped.");
                return;
            }

            arr[i] = v;
            sum += v;
        }

        if (arr.Length == 0 || sum <= 0)
        {
            _g.Dash = default;
            _g.DashPhase = 0;
            return;
        }

        _g.Dash = arr;
        _g.DashPhase = Finite(phase, 0);
    }

    /// <summary>The first standard blend mode in a name or array (ISO 32000-2 11.3.5); unknown names fall back to Normal.</summary>
    private string BlendModeName(PdfObject? bm)
    {
        if (bm is PdfArray a)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a.GetName(i) is { } n && IsStandardBlend(n))
                {
                    return n == "Compatible" ? "Normal" : n;
                }
            }

            return "Normal";
        }

        if (bm is PdfName name)
        {
            if (IsStandardBlend(name.Value))
            {
                return name.Value == "Compatible" ? "Normal" : name.Value;
            }

            Report(PdfSeverity.Warning, "Unknown blend mode /" + name.Value + "; Normal is used.");
        }

        return "Normal";
    }

    private static bool IsStandardBlend(string n) => n is "Normal" or "Compatible" or "Multiply" or "Screen" or "Overlay" or "Darken" or "Lighten"
        or "ColorDodge" or "ColorBurn" or "HardLight" or "SoftLight" or "Difference" or "Exclusion" or "Hue" or "Saturation" or "Color" or "Luminosity";

    private void ApplySoftMask(PdfObject? sm)
    {
        if (sm is PdfName)
        {
            _g.SoftMask = 0;
            return;
        }

        if (sm is not PdfDictionary d)
        {
            return;
        }

        if (d.GetStream("G") is not { } g)
        {
            Report(PdfSeverity.Warning, "A soft mask has no /G group; it was ignored.");
            return;
        }

        PdfMatrix fm = ReadMatrix(g.Dictionary.GetArray("Matrix"));
        PdfBounds bounds = ReadRect(g.Dictionary.GetArray("BBox")) is { } bb ? fm.TransformBounds(bb) : PdfBounds.Unbounded;
        PdfObject? tr = d.Get("TR");
        var info = new PdfSoftMaskInfo(++_nextMaskId, d.GetName("S") != "Alpha", bounds, d.GetArray("BC"), tr is PdfName ? null : tr);
        PdfGraphicsState state = Snapshot();
        int savedHidden = _hidden;
        _hidden = 0;
        _device.BeginSoftMask(in info, in state);
        try
        {
            RunForm(g, string.Empty, true);
        }
        finally
        {
            _device.EndSoftMask();
            _hidden = savedHidden;
        }

        _g.SoftMask = info.Id;
    }

    // ---- helpers ----

    internal static PdfMatrix ReadMatrix(PdfArray? a)
    {
        if (a is null || a.Count < 6)
        {
            return PdfMatrix.Identity;
        }

        for (int i = 0; i < 6; i++)
        {
            if (a[i] is not (PdfInteger or PdfReal) || !double.IsFinite(a.GetDouble(i)))
            {
                return PdfMatrix.Identity;
            }
        }

        return new PdfMatrix(a.GetDouble(0), a.GetDouble(1), a.GetDouble(2), a.GetDouble(3), a.GetDouble(4), a.GetDouble(5));
    }

    internal static PdfBounds? ReadRect(PdfArray? a)
    {
        if (a is null || a.Count < 4)
        {
            return null;
        }

        for (int i = 0; i < 4; i++)
        {
            if (a[i] is not (PdfInteger or PdfReal) || !double.IsFinite(a.GetDouble(i)))
            {
                return null;
            }
        }

        return PdfBounds.FromCorners(a.GetDouble(0), a.GetDouble(1), a.GetDouble(2), a.GetDouble(3));
    }

    // ---- XObjects ----

    private void DoXObject(string name)
    {
        if (_res?.GetDictionary("XObject")?.Get(name) is not PdfStream xo)
        {
            Report(PdfSeverity.Warning, "XObject /" + name + " is not in the resources.");
            return;
        }

        if (_hidden > 0)
        {
            return;
        }

        if (xo.Dictionary.Get("OC") is { } oc && !_oc.IsVisible(oc))
        {
            return;
        }

        switch (xo.Dictionary.GetName("Subtype"))
        {
            case "Image":
                {
                    PdfGraphicsState s = Snapshot();
                    var img = new PdfImage(xo.Dictionary, xo, default, name);
                    _device.DrawImage(in img, in s);
                    break;
                }

            case "Form":
                RunForm(xo, name, false);
                break;
            default:
                Report(PdfSeverity.Warning, "XObject /" + name + " has an unsupported /Subtype and was skipped.");
                break;
        }
    }

    /// <summary>
    /// Runs a form XObject: a saved state, the form matrix, a clip to its bounding box and its own resources
    /// (ISO 32000-2 8.10.1). Transparency groups are bracketed when compositing could differ from painting the content
    /// directly. Nesting is limited and a form that is already running is skipped.
    /// </summary>
    private void RunForm(PdfStream form, string name, bool isMask)
    {
        _ = name;
        if (_stopped)
        {
            return;
        }

        if (_formDepth >= _limits.MaxRecursionDepth)
        {
            Report(PdfSeverity.Error, "Form XObjects are nested deeper than the recursion limit; one was skipped.");
            return;
        }

        if (_saved.Count >= MaxSaveDepth)
        {
            Report(PdfSeverity.Error, "Graphics state nesting is too deep to run a form XObject.");
            return;
        }

        int number = form.Reference?.Number ?? -1;
        if (number >= 0 && !_activeForms.Add(number))
        {
            Report(PdfSeverity.Warning, "A form XObject uses itself (directly or through others); the inner use was skipped.");
            return;
        }

        try
        {
            ReadOnlyMemory<byte> data;
            try
            {
                data = form.GetDecodedBytes(_ct);
            }
            catch (PdfException e)
            {
                Report(PdfSeverity.Error, "A form XObject could not be decoded and was skipped: " + e.Message);
                return;
            }

            RunFormContent(form.Dictionary, data, isMask);
        }
        finally
        {
            if (number >= 0)
            {
                _activeForms.Remove(number);
            }
        }
    }

    private void RunFormContent(PdfDictionary fd, ReadOnlyMemory<byte> data, bool isMask)
    {
        PdfMatrix fm = ReadMatrix(fd.GetArray("Matrix"));
        PdfBounds? bbox = ReadRect(fd.GetArray("BBox"));
        PdfDictionary? group = fd.GetDictionary("Group");
        bool transparency = group is not null && group.GetName("S") == "Transparency";
        bool isolated = transparency && group!.GetBool("I");
        bool knockout = transparency && group!.GetBool("K");

        PdfDictionary? savedRes = _res;
        int savedQBase = _qBase, savedMcBase = _mcBase, savedCompat = _compat, savedSkipped = _skippedSaves;
        bool savedInText = _inText, savedTextClip = _textClipPending, savedPending = _pendingClip, savedEo = _clipEvenOdd;
        PdfMatrix savedTm = _tm, savedTlm = _tlm;
        int baseSaved = _saved.Count, baseMc = _mc.Count;
        bool grouped = false;

        _saved.Add(_g);
        _g = _g.Clone();
        _formDepth++;
        _path.Clear();
        _pendingClip = false;
        _inText = false;
        _textClipPending = false;
        _skippedSaves = 0;
        try
        {
            bool needGroup = !isMask && transparency
                && (_g.FillAlpha < 1 || _g.StrokeAlpha < 1 || _g.Blend != "Normal" || _g.SoftMask != 0 || isolated || knockout);
            if (needGroup)
            {
                PdfBounds gb = bbox is { } b0 ? fm.TransformBounds(b0) : PdfBounds.Unbounded;
                var info = new PdfGroupInfo(gb, isolated, knockout, group!.Get("CS"));
                PdfGraphicsState outer = Snapshot();
                _device.BeginGroup(in info, in outer);
                grouped = true;
            }

            if (isMask || grouped)
            {
                // Inside a group the content starts opaque, normal and unmasked; the group carries those effects.
                _g.FillAlpha = 1;
                _g.StrokeAlpha = 1;
                _g.Blend = "Normal";
                _g.SoftMask = 0;
            }

            PdfMatrix next = fm.Then(_g.Ctm);
            if (next.IsFinite)
            {
                _g.Ctm = next;
            }

            if (bbox is { } b)
            {
                double[] pts = [b.Left, b.Bottom, b.Right, b.Bottom, b.Right, b.Top, b.Left, b.Top];
                var rect = new PdfPath(RectVerbs, pts);
                PdfGraphicsState s = Snapshot();
                _device.PushClip(in rect, false, in s);
                _g.ClipCount++;
            }

            _res = fd.GetDictionary("Resources") ?? _res;
            _qBase = _saved.Count;
            _mcBase = _mc.Count;
            _compat = 0;
            Execute(new ContentLexer(data, _limits.MaxNestingDepth));
        }
        finally
        {
            try
            {
                UnwindTo(baseSaved + 1, baseMc, "a form XObject");
                Restore();
                if (grouped)
                {
                    _device.EndGroup();
                }
            }
            finally
            {
                _formDepth--;
                _res = savedRes;
                _qBase = savedQBase;
                _mcBase = savedMcBase;
                _compat = savedCompat;
                _skippedSaves = savedSkipped;
                _inText = savedInText;
                _textClipPending = savedTextClip;
                _pendingClip = savedPending;
                _clipEvenOdd = savedEo;
                _tm = savedTm;
                _tlm = savedTlm;
                _path.Clear();
            }
        }
    }

    // ---- shading ----

    private void PaintShadingOperator(string name)
    {
        PdfObject? sh = _res?.GetDictionary("Shading")?.Get(name);
        if (sh is not (PdfDictionary or PdfStream))
        {
            Report(PdfSeverity.Warning, "Shading /" + name + " is not in the resources.");
            return;
        }

        Report(PdfSeverity.Info, "Shading fills are passed to the device unevaluated.");
        if (_hidden == 0)
        {
            PdfGraphicsState s = Snapshot();
            _device.PaintShading(sh, name, in s);
        }
    }

    // ---- inline images ----

    private void InlineImage(ContentLexer lx)
    {
        PdfDictionary? d = lx.ReadInlineImageDictionary();
        if (d is null)
        {
            Report(PdfSeverity.Warning, "An inline image is cut off before its data.");
            return;
        }

        long expected = InlineDataLength(d);
        ReadOnlyMemory<byte> data = lx.ReadInlineImageData(expected, out bool found);
        if (!found)
        {
            Report(PdfSeverity.Warning, "An inline image has no closing EI; it extends to the end of the stream.");
        }

        if (_hidden == 0)
        {
            PdfGraphicsState s = Snapshot();
            var img = new PdfImage(d, null, data, string.Empty);
            _device.DrawImage(in img, in s);
        }
    }

    /// <summary>The byte length of unfiltered inline image data from its dictionary, or -1 when it cannot be known.</summary>
    private long InlineDataLength(PdfDictionary d)
    {
        if (d.ContainsKey("Filter"))
        {
            return -1;
        }

        long w = d.GetInt("Width"), h = d.GetInt("Height");
        if (w <= 0 || h <= 0 || w > (1 << 24) || h > (1 << 24))
        {
            return -1;
        }

        int n;
        long bpc;
        if (d.GetBool("ImageMask"))
        {
            n = 1;
            bpc = 1;
        }
        else
        {
            bpc = d.GetInt("BitsPerComponent");
            if (bpc is < 1 or > 16)
            {
                return -1;
            }

            ColorSpaceDef? cs = d.Get("ColorSpace") switch
            {
                PdfName nm => _spaces.ParseNamed(nm.Value, _res),
                PdfArray arr => _spaces.Parse(arr, _res, 0),
                _ => null,
            };
            if (cs is null)
            {
                return -1;
            }

            n = cs.N;
        }

        long row = ((w * n * bpc) + 7) / 8;
        return row * h;
    }

    // ---- annotations ----

    private void RunAnnotations(PdfDictionary page)
    {
        if (_options.Annotations == PdfAnnotationMode.None || page.GetArray("Annots") is not { } annots)
        {
            return;
        }

        int n = Math.Min(annots.Count, _limits.MaxObjects);
        for (int i = 0; i < n && !_stopped; i++)
        {
            _ct.ThrowIfCancellationRequested();
            if (annots[i] is PdfDictionary a)
            {
                RunAnnotation(a);
            }
        }
    }

    private void RunAnnotation(PdfDictionary a)
    {
        string subtype = a.GetName("Subtype") ?? string.Empty;
        if (subtype == "Popup")
        {
            return;
        }

        int flags = (int)Math.Clamp(a.GetInt("F"), 0, int.MaxValue);
        bool hiddenFlag = (flags & 2) != 0;
        bool visible = _options.Annotations == PdfAnnotationMode.Print
            ? !hiddenFlag && (flags & 4) != 0
            : !hiddenFlag && (flags & 32) == 0;
        if (!visible || (a.Get("OC") is { } oc && !_oc.IsVisible(oc)))
        {
            return;
        }

        PdfStream? ap = SelectAppearance(a);
        if (ap is null)
        {
            if (subtype != "Link")
            {
                Report(PdfSeverity.Info, "An annotation has no normal appearance stream; nothing was drawn for it.");
            }

            return;
        }

        if (ReadRect(a.GetArray("Rect")) is not { } rect)
        {
            Report(PdfSeverity.Warning, "An annotation has no valid /Rect; it was skipped.");
            return;
        }

        // ISO 32000-2 12.5.5: the transformed appearance box is fitted onto the annotation rectangle.
        PdfMatrix fm = ReadMatrix(ap.Dictionary.GetArray("Matrix"));
        PdfBounds box = ReadRect(ap.Dictionary.GetArray("BBox")) ?? new PdfBounds(0, 0, 0, 0);
        PdfBounds tb = fm.TransformBounds(box);
        double sx = tb.Width > 0 ? rect.Width / tb.Width : 1;
        double sy = tb.Height > 0 ? rect.Height / tb.Height : 1;
        var fit = new PdfMatrix(sx, 0, 0, sy, rect.Left - (tb.Left * sx), rect.Bottom - (tb.Bottom * sy));
        PdfMatrix ctm = fit.Then(_page.BaseMatrix);
        if (!ctm.IsFinite)
        {
            return;
        }

        var info = new PdfAnnotationInfo(a, subtype, rect, flags);
        _device.BeginAnnotation(in info);
        try
        {
            ResetState(_page.BaseMatrix);
            _g.Ctm = ctm;
            RunForm(ap, string.Empty, false);
        }
        finally
        {
            try
            {
                UnwindTo(0, 0, null);
            }
            finally
            {
                _device.EndAnnotation();
            }
        }
    }

    private static PdfStream? SelectAppearance(PdfDictionary a)
    {
        PdfDictionary? ap = a.GetDictionary("AP");
        PdfObject? n = ap?.Get("N");
        if (n is PdfStream s)
        {
            return s;
        }

        if (n is PdfDictionary states && a.GetName("AS") is { } state)
        {
            return states.Get(state) as PdfStream;
        }

        return null;
    }
}
