namespace OnePdf.Content;

/// <summary>
/// Runs a page's content streams (and annotation appearances) against an <see cref="IPdfDevice"/>.
/// </summary>
/// <remarks>
/// Rules applied here, in one place:
/// <list type="bullet">
/// <item><description>Bad operators (too few or wrongly typed operands, unknown names, operands that are not finite) are skipped and recorded once per kind.</description></item>
/// <item><description><c>Q</c> without a matching <c>q</c> in the same content stream is ignored; <c>q</c> left open at the end of a form or of the page content is closed there. Nesting of <c>q</c> is limited to <see cref="MaxSaveDepth"/>; deeper ones are ignored together with their <c>Q</c>.</description></item>
/// <item><description><c>EMC</c> without <c>BMC</c>/<c>BDC</c> is ignored; unclosed sequences are closed at the end of the form or page content.</description></item>
/// <item><description>The operator count of the page (forms and annotations included) is limited by <see cref="PdfLimits.MaxOperatorsPerPage"/>; reaching it stops the page cleanly with an error diagnostic.</description></item>
/// <item><description>Form XObjects nest at most <see cref="PdfLimits.MaxRecursionDepth"/> deep; a form that contains itself is skipped.</description></item>
/// <item><description>Operators run in content order; no operator's effect depends on anything later in the stream.</description></item>
/// </list>
/// </remarks>
internal sealed partial class ContentInterpreter
{
    /// <summary>Deepest <c>q</c> nesting honoured.</summary>
    public const int MaxSaveDepth = 4096;

    private const int OperandCapacity = 128;
    private const int MaxDiagnostics = 200;
    private const int MaxMarkedContentDepth = 4096;

    private readonly PdfDocument _doc;
    private readonly IPdfDevice _device;
    private readonly PdfInterpretOptions _options;
    private readonly CancellationToken _ct;
    private readonly PdfLimits _limits;
    private readonly PathBuilder _path = new();
    private readonly Operand[] _ops = new Operand[OperandCapacity];
    private readonly double[] _a = new double[OperandCapacity];
    private readonly List<GState> _saved = [];
    private readonly List<byte> _mc = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly HashSet<int> _activeForms = [];
    private readonly ColorContext _colorContext;
    private readonly ColorSpaceParser _spaces;
    private readonly OptionalContentState _oc;
    private readonly Dictionary<int, FontInfo> _fontsByRef = [];
    private readonly Dictionary<PdfDictionary, FontInfo> _fontsByDict = new(ReferenceEqualityComparer.Instance);

    private PdfPageInfo _page;
    private GState _g = new();
    private PdfDictionary? _res;
    private int _nOps;
    private long _opCount;
    private int _skippedSaves;
    private int _qBase;
    private int _mcBase;
    private int _hidden;
    private int _compat;
    private int _formDepth;
    private int _reportedCount;
    private int _nextMaskId;
    private bool _stopped;
    private bool _pendingClip;
    private bool _clipEvenOdd;
    private bool _inText;
    private bool _textClipPending;
    private PdfMatrix _tm = PdfMatrix.Identity;
    private PdfMatrix _tlm = PdfMatrix.Identity;
    private PdfGlyph[] _glyphs = new PdfGlyph[256];

    private ContentInterpreter(PdfDocument doc, IPdfDevice device, PdfInterpretOptions options, CancellationToken ct)
    {
        _doc = doc;
        _device = device;
        _options = options;
        _ct = ct;
        _limits = doc.Limits;
        _colorContext = new ColorContext { Note = m => Report(PdfSeverity.Info, m) };
        _spaces = new ColorSpaceParser(doc, m => Report(PdfSeverity.Warning, m));
        _oc = new OptionalContentState(doc, options.OptionalContentOverride);
    }

    /// <summary>
    /// Interprets the page: BeginPage, the page's content streams, the annotation appearances, EndPage. EndPage and every
    /// bracket close even when a limit stops the page or the token is cancelled.
    /// </summary>
    public static void Run(PdfDocument doc, PdfDictionary page, IPdfDevice device, PdfInterpretOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(device);
        cancellationToken.ThrowIfCancellationRequested();
        var interp = new ContentInterpreter(doc, device, options ?? new PdfInterpretOptions(), cancellationToken);
        interp.RunPage(page);
    }

    // ---- diagnostics ----

    private void Report(PdfSeverity severity, string message)
    {
        if (_reportedCount >= MaxDiagnostics || !_reported.Add(message))
        {
            return;
        }

        _reportedCount++;
        _doc.ReportContent(severity, message, _page.Page?.Reference?.Number ?? -1);
    }

    // ---- page ----

    private void RunPage(PdfDictionary page)
    {
        _page = PageGeometry.Compute(page, _options, m => Report(PdfSeverity.Warning, m));
        _res = PageGeometry.Inherited(page, "Resources") as PdfDictionary;
        ResetState(_page.BaseMatrix);
        _device.BeginPage(in _page);
        try
        {
            foreach (PdfStream s in ContentStreams(page))
            {
                if (_stopped)
                {
                    break;
                }

                ReadOnlyMemory<byte> data;
                try
                {
                    data = s.GetDecodedBytes(_ct);
                }
                catch (PdfException e)
                {
                    Report(PdfSeverity.Error, "A content stream could not be decoded and was skipped: " + e.Message);
                    continue;
                }

                Execute(new ContentLexer(data, _limits.MaxNestingDepth));
            }

            UnwindTo(0, 0, "the page content");
            if (!_stopped)
            {
                RunAnnotations(page);
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
                _device.EndPage();
            }
        }
    }

    private IEnumerable<PdfStream> ContentStreams(PdfDictionary page)
    {
        PdfObject? c = page.Get("Contents");
        if (c is PdfStream one)
        {
            yield return one;
        }
        else if (c is PdfArray arr)
        {
            int limit = Math.Min(arr.Count, _limits.MaxObjects);
            for (int i = 0; i < limit; i++)
            {
                if (arr[i] is PdfStream s)
                {
                    yield return s;
                }
                else
                {
                    Report(PdfSeverity.Warning, "A /Contents entry is not a stream and was skipped.");
                }
            }
        }
    }

    private void ResetState(PdfMatrix ctm)
    {
        _g = new GState { Ctm = ctm };
        _saved.Clear();
        _path.Clear();
        _pendingClip = false;
        _inText = false;
        _textClipPending = false;
        _skippedSaves = 0;
        _qBase = 0;
    }

    // ---- unwinding ----

    private void PopClips(GState g)
    {
        for (; g.ClipCount > 0; g.ClipCount--)
        {
            _device.PopClip();
        }
    }

    private void Restore()
    {
        PopClips(_g);
        _g = _saved[^1];
        _saved.RemoveAt(_saved.Count - 1);
    }

    private void CloseMarkedContent()
    {
        byte kind = _mc[^1];
        _mc.RemoveAt(_mc.Count - 1);
        if (kind == 0)
        {
            _device.EndMarkedContent();
        }
        else if (kind == 1)
        {
            _hidden--;
        }
    }

    /// <summary>Closes saved states and marked-content sequences opened since the given depths, then the current state's clips.</summary>
    private void UnwindTo(int savedDepth, int mcDepth, string? where)
    {
        bool unbalanced = false;
        while (_mc.Count > mcDepth)
        {
            CloseMarkedContent();
            unbalanced = true;
        }

        while (_saved.Count > savedDepth)
        {
            Restore();
            unbalanced = true;
        }

        _skippedSaves = 0;
        if (unbalanced && where is not null)
        {
            Report(PdfSeverity.Info, "Unbalanced q or marked content at the end of " + where + "; closed.");
        }
    }

    // ---- main loop ----

    private void Execute(ContentLexer lx)
    {
        _nOps = 0;
        long untilCheck = 0;
        while (!_stopped)
        {
            ContentToken t = lx.Next(out Operand operand, out ContentOp op);
            if (t == ContentToken.End)
            {
                break;
            }

            if (t == ContentToken.Operand)
            {
                if (_nOps < OperandCapacity)
                {
                    _ops[_nOps++] = operand;
                }

                continue;
            }

            if (++_opCount > _limits.MaxOperatorsPerPage)
            {
                Report(PdfSeverity.Error, "The page has more content operators than the limit allows; interpretation stopped.");
                _stopped = true;
                break;
            }

            if ((++untilCheck & 255) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }

            Dispatch(op, lx);
            _nOps = 0;
            lx.ResetItems();
        }

        _nOps = 0;
    }

    private bool Nums(int count)
    {
        if (_nOps < count)
        {
            return false;
        }

        int first = _nOps - count;
        for (int i = 0; i < count; i++)
        {
            ref Operand o = ref _ops[first + i];
            if (o.Kind != OperandKind.Number || !double.IsFinite(o.Num))
            {
                return false;
            }

            _a[i] = o.Num;
        }

        return true;
    }

    private void BadOperands(ContentOp op) =>
        Report(PdfSeverity.Warning, "Operator " + op + " has missing or invalid operands and was skipped.");

    private PdfGraphicsState Snapshot() => new(
        _g.Ctm, _g.Fill, _g.Stroke, _g.LineWidth, _g.LineCap, _g.LineJoin, _g.MiterLimit, _g.Dash, _g.DashPhase, _g.Flatness,
        _g.Intent, _g.FillAlpha, _g.StrokeAlpha, _g.Blend, _g.SoftMask, _g.Ais, _g.FillOverprint, _g.StrokeOverprint, _g.OverprintMode);

    private void Dispatch(ContentOp op, ContentLexer lx)
    {
        switch (op)
        {
            // ---- graphics state ----
            case ContentOp.Save:
                if (_saved.Count >= MaxSaveDepth)
                {
                    _skippedSaves++;
                    Report(PdfSeverity.Warning, "Graphics state nesting is too deep; further q operators were ignored.");
                }
                else
                {
                    _saved.Add(_g);
                    _g = _g.Clone();
                }

                break;
            case ContentOp.Restore:
                if (_skippedSaves > 0)
                {
                    _skippedSaves--;
                }
                else if (_saved.Count > _qBase)
                {
                    Restore();
                }
                else
                {
                    Report(PdfSeverity.Warning, "Q without a matching q was ignored.");
                }

                break;
            case ContentOp.Concat:
                if (!Nums(6))
                {
                    BadOperands(op);
                    break;
                }

                {
                    var m = new PdfMatrix(_a[0], _a[1], _a[2], _a[3], _a[4], _a[5]);
                    PdfMatrix next = m.Then(_g.Ctm);
                    if (next.IsFinite)
                    {
                        _g.Ctm = next;
                    }
                    else
                    {
                        Report(PdfSeverity.Warning, "A cm operator overflowed the transformation matrix and was skipped.");
                    }
                }

                break;
            case ContentOp.LineWidth:
                if (Nums(1))
                {
                    _g.LineWidth = Math.Max(0, _a[0]);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.LineCap:
                if (Nums(1) && _a[0] is >= 0 and <= 2)
                {
                    _g.LineCap = (int)_a[0];
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.LineJoin:
                if (Nums(1) && _a[0] is >= 0 and <= 2)
                {
                    _g.LineJoin = (int)_a[0];
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.MiterLimit:
                if (Nums(1))
                {
                    _g.MiterLimit = Math.Max(1, _a[0]);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.Dash:
                SetDash(lx);
                break;
            case ContentOp.RenderingIntent:
                if (_nOps >= 1 && _ops[_nOps - 1].Kind == OperandKind.Name)
                {
                    _g.Intent = _ops[_nOps - 1].Name!;
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.Flatness:
                if (Nums(1))
                {
                    _g.Flatness = Math.Clamp(_a[0], 0, 100);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.SetExtGState:
                if (_nOps >= 1 && _ops[_nOps - 1].Kind == OperandKind.Name)
                {
                    ApplyExtGState(_ops[_nOps - 1].Name!);
                }
                else
                {
                    BadOperands(op);
                }

                break;

            // ---- path construction ----
            case ContentOp.Move:
                if (Nums(2))
                {
                    _path.MoveTo(_a[0], _a[1]);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.Line:
                if (!Nums(2))
                {
                    BadOperands(op);
                }
                else if (_path.HasCurrentPoint)
                {
                    _path.LineTo(_a[0], _a[1]);
                }
                else
                {
                    Report(PdfSeverity.Warning, "A path segment without a current point was skipped.");
                }

                break;
            case ContentOp.Curve:
                if (!Nums(6))
                {
                    BadOperands(op);
                }
                else if (_path.HasCurrentPoint)
                {
                    _path.CurveTo(_a[0], _a[1], _a[2], _a[3], _a[4], _a[5]);
                }
                else
                {
                    Report(PdfSeverity.Warning, "A path segment without a current point was skipped.");
                }

                break;
            case ContentOp.CurveV:
                if (!Nums(4))
                {
                    BadOperands(op);
                }
                else if (_path.HasCurrentPoint)
                {
                    // v: the first control point is the current point (ISO 32000-2 8.5.2.2).
                    _path.CurveTo(_path.CurX, _path.CurY, _a[0], _a[1], _a[2], _a[3]);
                }
                else
                {
                    Report(PdfSeverity.Warning, "A path segment without a current point was skipped.");
                }

                break;
            case ContentOp.CurveY:
                if (!Nums(4))
                {
                    BadOperands(op);
                }
                else if (_path.HasCurrentPoint)
                {
                    // y: the second control point is the end point.
                    _path.CurveTo(_a[0], _a[1], _a[2], _a[3], _a[2], _a[3]);
                }
                else
                {
                    Report(PdfSeverity.Warning, "A path segment without a current point was skipped.");
                }

                break;
            case ContentOp.Close:
                if (_path.HasCurrentPoint)
                {
                    _path.Close();
                }

                break;
            case ContentOp.Rect:
                if (Nums(4))
                {
                    double x = _a[0], y = _a[1], w = _a[2], h = _a[3];
                    _path.MoveTo(x, y);
                    _path.LineTo(x + w, y);
                    _path.LineTo(x + w, y + h);
                    _path.LineTo(x, y + h);
                    _path.Close();
                }
                else
                {
                    BadOperands(op);
                }

                break;

            // ---- path painting and clipping ----
            case ContentOp.Stroke:
                PaintPath(false, false, true, false);
                break;
            case ContentOp.CloseStroke:
                PaintPath(false, false, true, true);
                break;
            case ContentOp.FillNz:
            case ContentOp.FillNzOld:
                PaintPath(true, false, false, false);
                break;
            case ContentOp.FillEo:
                PaintPath(true, true, false, false);
                break;
            case ContentOp.FillStroke:
                PaintPath(true, false, true, false);
                break;
            case ContentOp.FillStrokeEo:
                PaintPath(true, true, true, false);
                break;
            case ContentOp.CloseFillStroke:
                PaintPath(true, false, true, true);
                break;
            case ContentOp.CloseFillStrokeEo:
                PaintPath(true, true, true, true);
                break;
            case ContentOp.EndPath:
                PaintPath(false, false, false, false);
                break;
            case ContentOp.ClipNz:
                _pendingClip = true;
                _clipEvenOdd = false;
                break;
            case ContentOp.ClipEo:
                _pendingClip = true;
                _clipEvenOdd = true;
                break;

            // ---- colour ----
            case ContentOp.FillGray:
            case ContentOp.StrokeGray:
                SetDeviceColor(op, DeviceColorSpaceDef.Gray, 1, op == ContentOp.FillGray);
                break;
            case ContentOp.FillRgb:
            case ContentOp.StrokeRgb:
                SetDeviceColor(op, DeviceColorSpaceDef.Rgb, 3, op == ContentOp.FillRgb);
                break;
            case ContentOp.FillCmyk:
            case ContentOp.StrokeCmyk:
                SetDeviceColor(op, DeviceColorSpaceDef.Cmyk, 4, op == ContentOp.FillCmyk);
                break;
            case ContentOp.FillSpaceName:
            case ContentOp.StrokeSpaceName:
                SetColorSpace(op, op == ContentOp.FillSpaceName);
                break;
            case ContentOp.FillColor:
            case ContentOp.FillColorN:
                SetColor(op, true);
                break;
            case ContentOp.StrokeColor:
            case ContentOp.StrokeColorN:
                SetColor(op, false);
                break;

            // ---- XObjects, shading, images ----
            case ContentOp.Do:
                if (_nOps >= 1 && _ops[_nOps - 1].Kind == OperandKind.Name)
                {
                    DoXObject(_ops[_nOps - 1].Name!);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.Shading:
                if (_nOps >= 1 && _ops[_nOps - 1].Kind == OperandKind.Name)
                {
                    PaintShadingOperator(_ops[_nOps - 1].Name!);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.BeginInline:
                InlineImage(lx);
                break;
            case ContentOp.InlineData:
            case ContentOp.EndInline:
                Report(PdfSeverity.Warning, "ID or EI outside an inline image was skipped.");
                break;

            // ---- marked content ----
            case ContentOp.BeginMarked:
                if (_nOps >= 1 && _ops[_nOps - 1].Kind == OperandKind.Name)
                {
                    BeginMarked(_ops[_nOps - 1].Name!, null, lx);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.BeginMarkedProps:
                if (_nOps >= 2 && _ops[_nOps - 2].Kind == OperandKind.Name)
                {
                    BeginMarked(_ops[_nOps - 2].Name!, _ops[_nOps - 1], lx);
                }
                else
                {
                    BadOperands(op);
                }

                break;
            case ContentOp.EndMarked:
                if (_mc.Count > _mcBase)
                {
                    CloseMarkedContent();
                }
                else
                {
                    Report(PdfSeverity.Warning, "EMC without a matching BMC or BDC was ignored.");
                }

                break;
            case ContentOp.MarkPoint:
            case ContentOp.MarkPointProps:
                break;

            case ContentOp.ShowTextArray:
                ShowArray(lx);
                break;

            // ---- compatibility and Type 3 glyph operators ----
            case ContentOp.BeginCompat:
                _compat++;
                break;
            case ContentOp.EndCompat:
                if (_compat > 0)
                {
                    _compat--;
                }

                break;
            case ContentOp.CharProcWidth:
            case ContentOp.CharProcBox:
                break;

            default:
                if (!DispatchText(op))
                {
                    if (_compat == 0)
                    {
                        Report(PdfSeverity.Warning, "Unknown operator '" + lx.LastKeyword + "' was skipped.");
                    }
                }

                break;
        }
    }

    // ---- painting ----

    private void PaintPath(bool fill, bool evenOdd, bool stroke, bool close)
    {
        if (_path.Overflowed)
        {
            Report(PdfSeverity.Warning, "A path has more segments than supported; the rest was dropped.");
        }

        if (close && _path.HasCurrentPoint)
        {
            _path.Close();
        }

        if (!_path.IsEmpty)
        {
            PdfPath p = _path.View();
            if (_hidden == 0)
            {
                if (fill)
                {
                    PdfGraphicsState s = Snapshot();
                    _device.FillPath(in p, evenOdd, in s);
                }

                if (stroke)
                {
                    PdfGraphicsState s = Snapshot();
                    _device.StrokePath(in p, in s);
                }
            }

            if (_pendingClip)
            {
                PdfGraphicsState s = Snapshot();
                _device.PushClip(in p, _clipEvenOdd, in s);
                _g.ClipCount++;
            }
        }

        _pendingClip = false;
        _path.Clear();
    }

    private void SetDash(ContentLexer lx)
    {
        if (_nOps < 2 || _ops[_nOps - 1].Kind != OperandKind.Number || _ops[_nOps - 2].Kind != OperandKind.Array)
        {
            BadOperands(ContentOp.Dash);
            return;
        }

        ReadOnlySpan<Operand> items = lx.Items(in _ops[_nOps - 2]);
        var arr = new double[items.Length];
        double sum = 0;
        for (int i = 0; i < items.Length; i++)
        {
            double v = items[i].Kind == OperandKind.Number ? items[i].Num : double.NaN;
            if (!double.IsFinite(v) || v < 0)
            {
                Report(PdfSeverity.Warning, "A dash array with negative or non-numeric entries was skipped.");
                return;
            }

            arr[i] = v;
            sum += v;
        }

        double phase = _ops[_nOps - 1].Num;
        if (!double.IsFinite(phase))
        {
            phase = 0;
        }

        if (arr.Length == 0 || sum <= 0)
        {
            // An empty array, or one of zeros, is a solid line (ISO 32000-2 8.4.3.6).
            _g.Dash = default;
            _g.DashPhase = 0;
            return;
        }

        _g.Dash = arr;
        _g.DashPhase = phase;
    }

    // ---- colour operators ----

    private void SetDeviceColor(ContentOp op, ColorSpaceDef space, int n, bool fill)
    {
        if (!Nums(n))
        {
            BadOperands(op);
            return;
        }

        PdfColor c = space.Convert(_a.AsSpan(0, n), _colorContext);
        if (fill)
        {
            _g.FillCs = space;
            _g.Fill = c;
        }
        else
        {
            _g.StrokeCs = space;
            _g.Stroke = c;
        }
    }

    private void SetColorSpace(ContentOp op, bool fill)
    {
        if (_nOps < 1 || _ops[_nOps - 1].Kind != OperandKind.Name)
        {
            BadOperands(op);
            return;
        }

        string name = _ops[_nOps - 1].Name!;
        ColorSpaceDef? def = _spaces.ParseNamed(name, _res);
        if (def is null)
        {
            Report(PdfSeverity.Warning, "Colour space /" + name + " is not defined or not supported; the operator was skipped.");
            return;
        }

        PdfColor initial = def.Initial(_colorContext);
        if (fill)
        {
            _g.FillCs = def;
            _g.Fill = initial;
        }
        else
        {
            _g.StrokeCs = def;
            _g.Stroke = initial;
        }
    }

    private void SetColor(ContentOp op, bool fill)
    {
        ColorSpaceDef cs = fill ? _g.FillCs : _g.StrokeCs;
        int count = _nOps;
        string? patternName = null;
        if (count > 0 && _ops[count - 1].Kind == OperandKind.Name && op is ContentOp.FillColorN or ContentOp.StrokeColorN)
        {
            patternName = _ops[count - 1].Name;
            count--;
        }

        PdfColor color;
        if (cs is PatternColorSpaceDef pcs)
        {
            if (patternName is null)
            {
                BadOperands(op);
                return;
            }

            PdfObject? pattern = _res?.GetDictionary("Pattern")?.Get(patternName);
            if (pattern is not (PdfDictionary or PdfStream))
            {
                Report(PdfSeverity.Warning, "Pattern /" + patternName + " is not defined; the colour was not changed.");
                return;
            }

            int n = pcs.N;
            if (count < n)
            {
                BadOperands(op);
                return;
            }

            for (int i = 0; i < n; i++)
            {
                ref Operand o = ref _ops[count - n + i];
                if (o.Kind != OperandKind.Number || !double.IsFinite(o.Num))
                {
                    BadOperands(op);
                    return;
                }

                _a[i] = o.Num;
            }

            PdfColor tint = pcs.Convert(_a.AsSpan(0, n), _colorContext);
            color = tint with { Pattern = pattern, PatternName = patternName };
            Report(PdfSeverity.Info, "Pattern colours are passed to the device unevaluated.");
        }
        else
        {
            int n = cs.N;
            if (patternName is not null || count < n || n > 32)
            {
                BadOperands(op);
                return;
            }

            for (int i = 0; i < n; i++)
            {
                ref Operand o = ref _ops[count - n + i];
                if (o.Kind != OperandKind.Number || !double.IsFinite(o.Num))
                {
                    BadOperands(op);
                    return;
                }

                _a[i] = o.Num;
            }

            color = cs.Convert(_a.AsSpan(0, n), _colorContext);
        }

        if (fill)
        {
            _g.Fill = color;
        }
        else
        {
            _g.Stroke = color;
        }
    }

    // ---- marked content ----

    private void BeginMarked(string tag, Operand? props, ContentLexer lx)
    {
        if (_mc.Count - _mcBase >= MaxMarkedContentDepth)
        {
            Report(PdfSeverity.Warning, "Marked-content nesting is too deep; a sequence was ignored.");
            _mc.Add(2);
            return;
        }

        PdfDictionary? dict = null;
        if (props is { } p)
        {
            if (p.Kind == OperandKind.Name)
            {
                dict = _res?.GetDictionary("Properties")?.GetDictionary(p.Name!);
            }
            else if (p.Kind == OperandKind.Object)
            {
                dict = p.Obj as PdfDictionary;
            }
        }

        if (_hidden > 0)
        {
            _mc.Add(2);
            return;
        }

        if (tag == "OC" && dict is not null && !_oc.IsVisible(dict))
        {
            _mc.Add(1);
            _hidden++;
            return;
        }

        _mc.Add(0);
        _device.BeginMarkedContent(tag, dict);
        _ = lx;
    }
}
