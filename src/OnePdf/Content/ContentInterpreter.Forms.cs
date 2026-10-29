using OnePdf.Functions;

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
                case "TK":
                    // ISO 32000-2 9.3.8: the text knockout flag, default true when the key is absent from the whole state.
                    _g.TextKnockout = d.Get("TK") is not PdfBoolean { Value: false };
                    break;
                case "BM":
                    _g.Blend = BlendModeName(d.Get("BM"));
                    break;
                case "SMask":
                    ApplySoftMask(d.Get("SMask"));
                    break;
                default:
                    // SA, SM, TR, TR2, HT, BG, UCR and their variants affect only halftone/transfer or advisory behavior.
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
        var info = new PdfSoftMaskInfo(++_nextMaskId, d.GetName("S") != "Alpha", bounds, d.GetArray("BC"), tr is PdfName ? null : tr)
        {
            TransferTable = BuildTransferTable(tr),
        };
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

    /// <summary>
    /// Evaluates a soft mask's <c>/TR</c> (ISO 32000-2 11.6.5.2) at i/255 for i in 0..255. Null for an absent key or <c>/Identity</c>;
    /// also null, with a warning, when the function is unusable (any other name, a bad program, not one input, no output).
    /// </summary>
    private byte[]? BuildTransferTable(PdfObject? tr)
    {
        if (tr is null || (tr is PdfName n && n.Value == "Identity"))
        {
            return null;
        }

        PdfFunction? f = null;
        string? problem = null;
        if (tr is not PdfName)
        {
            try
            {
                f = PdfFunction.Create(tr, _doc.Limits, out problem);
            }
            catch (PdfLimitException ex)
            {
                problem = ex.Message;
            }
        }

        if (f is null || f.InputCount != 1 || f.OutputCount < 1)
        {
            Report(PdfSeverity.Warning, "A soft mask /TR is not a usable one-input function (" + (problem ?? "wrong shape") + "); identity was used.");
            return null;
        }

        var table = new byte[256];
        var input = new double[1];
        var output = new double[f.OutputCount];
        for (int i = 0; i < 256; i++)
        {
            input[0] = i / 255.0;
            if ((f.Evaluate(input, output) & FunctionStatus.BadProgram) != 0)
            {
                Report(PdfSeverity.Warning, "A soft mask /TR function has a damaged program; identity was used.");
                return null;
            }

            double y = output[0];
            double r = double.IsNaN(y) ? 0 : Math.Floor((y * 255.0) + 0.5);
            table[i] = (byte)Math.Clamp(r, 0, 255);
        }

        return table;
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
                    (double dw, double dh) = DeviceExtent(s.Ctm);
                    var img = new PdfImage(xo.Dictionary, xo, default, name, _doc, _res, dw, dh);
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
            _tap?.CannotVerify("form XObjects are nested deeper than the recursion limit");
            return;
        }

        if (_saved.Count >= MaxSaveDepth)
        {
            Report(PdfSeverity.Error, "Graphics state nesting is too deep to run a form XObject.");
            _tap?.CannotVerify("graphics state nesting is too deep to run a form XObject");
            return;
        }

        int number = form.Reference?.Number ?? -1;
        if (number >= 0 && !_activeForms.Add(number))
        {
            Report(PdfSeverity.Warning, "A form XObject uses itself (directly or through others); the inner use was skipped.");
            // The rewritten copy of a form that uses itself would still point at the original, so it cannot be checked.
            _tap?.CannotVerify("a form XObject uses itself");
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
                _tap?.CannotVerify("a form XObject could not be decoded: " + e.Message);
                return;
            }

            if (_tapSuspended == 0)
            {
                _tap?.StreamOrigin(form);
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
        PdfMatrix savedTm = _tm, savedTlm = _tlm, savedPatternBase = _patternBase;
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

            // Patterns selected inside the form are anchored to the form's space (ISO 32000-2 8.7.3.1).
            _patternBase = _g.Ctm;

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
                _patternBase = savedPatternBase;
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

        if (_hidden != 0)
        {
            return;
        }

        PdfShading? shading = LoadShading(sh);
        if (shading is null)
        {
            return;
        }

        PdfGraphicsState s = Snapshot();
        _device.PaintShading(shading, in s);
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
            (double dw, double dh) = DeviceExtent(s.Ctm);
            var img = new PdfImage(d, null, data, string.Empty, _doc, _res, dw, dh);
            _device.DrawImage(in img, in s);
        }
    }

    /// <summary>
    /// The axis-aligned size of the unit square under the CTM, which already ends in the output space: what an image drawn here
    /// covers in device pixels. Zero when the matrix is not finite.
    /// </summary>
    private static (double Width, double Height) DeviceExtent(in PdfMatrix ctm)
    {
        if (!ctm.IsFinite)
        {
            return (0, 0);
        }

        (double x0, double y0) = ctm.Transform(0, 0);
        (double x1, double y1) = ctm.Transform(1, 0);
        (double x2, double y2) = ctm.Transform(0, 1);
        (double x3, double y3) = ctm.Transform(1, 1);
        return (Math.Max(Math.Max(x0, x1), Math.Max(x2, x3)) - Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
            Math.Max(Math.Max(y0, y1), Math.Max(y2, y3)) - Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)));
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

            PdfColorSpace? cs = d.Get("ColorSpace") is PdfName or PdfArray ? _spaces.Resolve(d.Get("ColorSpace"), _res) : null;
            if (cs is null || cs.ComponentCount < 1)
            {
                return -1;
            }

            n = cs.ComponentCount;
        }

        long row = ((w * n * bpc) + 7) / 8;
        return row * h;
    }

    // ---- annotations ----

    private void RunAnnotations(PdfPage page)
    {
        if (_options.Annotations == PdfAnnotationMode.None)
        {
            return;
        }

        IReadOnlyList<PdfAnnotation> annots = page.Annotations;
        for (int i = 0; i < annots.Count && !_stopped; i++)
        {
            _ct.ThrowIfCancellationRequested();
            RunAnnotation(annots[i]);
        }
    }

    private void RunAnnotation(PdfAnnotation annotation)
    {
        string subtype = annotation.Subtype;
        if (subtype == "Popup")
        {
            return;
        }

        PdfAnnotationOptions f = annotation.Flags;
        bool hiddenFlag = (f & PdfAnnotationOptions.Hidden) != 0;
        bool visible = _options.Annotations == PdfAnnotationMode.Print
            ? !hiddenFlag && (f & PdfAnnotationOptions.Print) != 0
            : !hiddenFlag && (f & PdfAnnotationOptions.NoView) == 0;
        if (!visible || (annotation.Dictionary.Get("OC") is { } oc && !_oc.IsVisible(oc)))
        {
            return;
        }

        PdfStream? ap = annotation.GetAppearanceStream();
        PdfGeneratedAppearance? generated = null;
        if (_options.GenerateAppearances && subtype != "Link" && (ap is null || WantsRegeneration(annotation, subtype)))
        {
            int obj = annotation.Dictionary.Reference?.Number ?? -1;
            generated = annotation.GenerateAppearance(_ct);
            if (generated is not null)
            {
                Report(PdfSeverity.Info, "Drew a generated appearance (" + generated.Description + ") for the " + subtype + " annotation"
                    + (obj >= 0 ? " " + obj.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 0 R" : string.Empty) + ".");
            }
        }

        if (ap is null && generated is null)
        {
            if (subtype != "Link")
            {
                Report(PdfSeverity.Info, "An annotation has no normal appearance stream; nothing was drawn for it.");
            }

            return;
        }

        PdfRectangle r = generated?.Rect ?? annotation.Rect;
        if (r.IsEmpty && annotation.Dictionary.Get("Rect") is not PdfArray)
        {
            Report(PdfSeverity.Warning, "An annotation has no valid /Rect; it was skipped.");
            return;
        }

        var rect = new PdfBounds(r.Left, r.Bottom, r.Right, r.Top);
        int flags = (int)f;
        PdfDictionary a = annotation.Dictionary;
        PdfDictionary apDict = generated?.Dictionary ?? ap!.Dictionary;

        // ISO 32000-2 12.5.5: the transformed appearance box is fitted onto the annotation rectangle.
        PdfMatrix fm = ReadMatrix(apDict.GetArray("Matrix"));
        PdfBounds box = ReadRect(apDict.GetArray("BBox")) ?? new PdfBounds(0, 0, 0, 0);
        PdfBounds tb = fm.TransformBounds(box);
        double sx = tb.Width > 0 ? rect.Width / tb.Width : 1;
        double sy = tb.Height > 0 ? rect.Height / tb.Height : 1;
        var fit = new PdfMatrix(sx, 0, 0, sy, rect.Left - (tb.Left * sx), rect.Bottom - (tb.Bottom * sy));
        PdfMatrix ctm = fit.Then(AnnotationBaseMatrix(f, rect));
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
            if (generated is not null)
            {
                RunFormContent(generated.Dictionary, generated.Content, false);
            }
            else
            {
                RunForm(ap!, string.Empty, false);
            }
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

    // ISO 32000-2 12.7.3: with /NeedAppearances true the viewer rebuilds the appearances of fields from their values.
    private bool WantsRegeneration(PdfAnnotation annotation, string subtype) =>
        subtype == "Widget" && (_needAppearances ??= Appearance.AppearanceGenerator.FormNeedsAppearances(_doc))
        && Appearance.WidgetAppearance.RegeneratesUnderNeedAppearances(_doc, annotation.Dictionary, annotation.GetAppearanceStream() is not null);

    private bool? _needAppearances;

    // ISO 32000-2 12.5.3 Table 167: NoZoom keeps the appearance at its size at 100% magnification and NoRotate keeps it upright,
    // in both cases with the upper-left corner of the annotation rectangle (in default user space) staying where the page
    // matrix puts it. Magnification here is the caller's scale or DPI; /UserUnit is part of the page, not of the zoom.
    private PdfMatrix AnnotationBaseMatrix(PdfAnnotationOptions flags, in PdfBounds rect)
    {
        bool noZoom = (flags & PdfAnnotationOptions.NoZoom) != 0;
        bool noRotate = (flags & PdfAnnotationOptions.NoRotate) != 0;
        double s = noZoom ? _page.UserUnit : _page.Scale;
        int rotation = noRotate ? 0 : _page.Rotation;
        if ((!noZoom || s == _page.Scale) && (!noRotate || _page.Rotation == 0))
        {
            return _page.BaseMatrix;
        }

        PdfMatrix flip = new(s, 0, 0, -s, 0, 0);
        PdfMatrix linear = rotation switch
        {
            90 => flip.Then(new PdfMatrix(0, 1, -1, 0, 0, 0)),
            180 => flip.Then(new PdfMatrix(-1, 0, 0, -1, 0, 0)),
            270 => flip.Then(new PdfMatrix(0, -1, 1, 0, 0, 0)),
            _ => flip,
        };
        (double px, double py) = _page.BaseMatrix.Transform(rect.Left, rect.Top);
        (double lx, double ly) = linear.Transform(rect.Left, rect.Top);
        return linear with { E = px - lx, F = py - ly };
    }
}
