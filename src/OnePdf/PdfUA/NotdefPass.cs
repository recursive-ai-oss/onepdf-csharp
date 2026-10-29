using System.Globalization;
using System.Text;
using OnePdf.Content;
using OnePdf.PdfFonts;

namespace OnePdf.PdfUA;

/// <summary>
/// Finds the text operators that show the <c>.notdef</c> glyph of an embedded font (ISO 14289-1 7.21.8) and takes those character codes
/// out of their strings. The interpreter reads every page, so fonts, text state and form XObjects are resolved as a viewer resolves
/// them. Each removed code is replaced by a <c>TJ</c> adjustment of the same advance, so the text around it keeps its place. Edits are
/// worked out for the whole document before the first one is applied, and a form XObject is only edited when every use of it agrees.
/// </summary>
internal sealed class NotdefPass
{
    private const int MaxHits = 1 << 20;

    private readonly PdfDocument _doc;
    private readonly CancellationToken _ct;
    private readonly OptionalContentState _oc;
    private readonly Dictionary<object, Origin> _origins = new(ReferenceEqualityComparer.Instance);
    private readonly List<Hit> _hits = [];
    private readonly List<Op> _orphans = [];
    private readonly List<string> _warnings = [];
    private bool _unverified;

    // A content stream that has at least one hit: the page's content or a form XObject.
    private sealed class Origin
    {
        public PdfStream? Form;
        public PdfDictionary? Page;
        public int Segments;
        public byte[]? Data;
        public readonly Dictionary<int, Op> Ops = [];
        public readonly HashSet<int> Clean = []; // starts of text operators that showed no .notdef in some use
    }

    // One text operator with hits, shared by every use of its stream.
    private sealed class Op
    {
        public int Start;
        public int End;
        public byte[]? Replacement;
        public string? Cannot;
        public bool Disagrees;
        public int Glyphs;
        public readonly List<Hit> Hits = [];
    }

    private sealed class Hit
    {
        public Origin Origin = null!;
        public Op Op = null!;
        public PdfNotdefRun Run = null!;
    }

    private NotdefPass(PdfDocument doc, CancellationToken ct)
    {
        _doc = doc;
        _ct = ct;
        _oc = new OptionalContentState(doc, null);
    }

    /// <summary>Every text operator that shows <c>.notdef</c>, in page order.</summary>
    public static IReadOnlyList<PdfNotdefRun> Find(PdfDocument doc, CancellationToken ct)
    {
        var pass = new NotdefPass(doc, ct);
        pass.ScanAll();
        var runs = new List<PdfNotdefRun>(pass._hits.Count);
        foreach (Hit h in pass._hits)
        {
            runs.Add(h.Run);
        }

        return runs;
    }

    public static PdfNotdefRemoval Remove(PdfDocument doc, PdfNotdefRemoveOptions options, CancellationToken ct)
    {
        if (!options.DryRun)
        {
            doc.EnsureEditable(0);
        }

        var pass = new NotdefPass(doc, ct);
        pass.ScanAll();
        return pass.Decide(options);
    }

    private void ScanAll()
    {
        var io = new PdfInterpretOptions
        {
            Box = PdfPageBox.MediaBox,
            Scale = 1,
            ApplyRotation = false,
            ApplyUserUnit = false,
            Annotations = PdfAnnotationMode.View,
            GenerateAppearances = false,
            OptionalContentOverride = static _ => true,
            GlyphOutlines = false,
        };
        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            PdfPage page;
            try
            {
                page = _doc.Pages[i];
            }
            catch (PdfFormatException)
            {
                _warnings.Add($"Page {i + 1} could not be read and was not examined.");
                _unverified = true;
                continue;
            }

            var scanner = new Scanner(this, page, i);
            try
            {
                ContentInterpreter.Run(page, scanner, io, scanner, _ct);
            }
            catch (PdfFormatException e)
            {
                _warnings.Add($"Page {i + 1} could not be examined to its end: {e.Message}");
                _unverified = true;
            }

            if (scanner.Unverified is { } why)
            {
                _warnings.Add($"Page {i + 1}: {why}.");
                _unverified = true;
            }
        }
    }

    private Origin OriginOf(PdfStream? form, PdfDictionary? page)
    {
        object key = (object?)form ?? page!;
        if (!_origins.TryGetValue(key, out Origin? o))
        {
            _origins[key] = o = new Origin { Form = form, Page = page };
        }

        return o;
    }

    // ---- deciding and applying ----

    private PdfNotdefRemoval Decide(PdfNotdefRemoveOptions options)
    {
        var result = new PdfNotdefRemoval();
        result.WarningList.AddRange(_warnings);
        var edits = new Dictionary<Origin, List<Op>>(ReferenceEqualityComparer.Instance);
        var work = new List<(Origin? Origin, Op Op)>();
        foreach (Origin origin in _origins.Values)
        {
            foreach (Op op in origin.Ops.Values)
            {
                work.Add((origin, op));
            }
        }

        foreach (Op orphan in _orphans)
        {
            work.Add((null, orphan));
        }

        foreach ((Origin? origin, Op op) in work)
        {
            {
                PdfNotdefKeepReason? block = null;
                if (op.Cannot is not null || origin is null || (origin.Form is not null && _unverified) || (origin.Page is not null && origin.Segments != 1))
                {
                    block = PdfNotdefKeepReason.CannotRewrite;
                }
                else if (op.Disagrees || origin!.Clean.Contains(op.Start))
                {
                    block = PdfNotdefKeepReason.SharedFormDisagrees;
                }

                if (block is not null)
                {
                    foreach (Hit h in op.Hits)
                    {
                        result.KeptList.Add(new PdfNotdefKept(h.Run, block.Value));
                    }

                    continue;
                }

                var reasons = new PdfNotdefKeepReason?[op.Hits.Count];
                bool all = true;
                for (int k = 0; k < op.Hits.Count; k++)
                {
                    PdfNotdefRun run = op.Hits[k].Run;
                    if (options.Scope == PdfNotdefScope.Invisible && run.IsVisible)
                    {
                        reasons[k] = PdfNotdefKeepReason.Visible;
                        all = false;
                    }
                    else if (options.Include is { } inc && !inc(run))
                    {
                        reasons[k] = PdfNotdefKeepReason.ExcludedByCaller;
                        all = false;
                    }
                }

                if (!all)
                {
                    for (int k = 0; k < op.Hits.Count; k++)
                    {
                        result.KeptList.Add(new PdfNotdefKept(op.Hits[k].Run, reasons[k] ?? PdfNotdefKeepReason.SharedFormDisagrees));
                    }

                    continue;
                }

                foreach (Hit h in op.Hits)
                {
                    result.RemovedList.Add(h.Run);
                }

                result.GlyphsRemoved += op.Glyphs;
                result.OperatorsChanged++;
                if (!edits.TryGetValue(origin!, out List<Op>? list))
                {
                    edits[origin!] = list = [];
                }

                list.Add(op);
            }
        }

        // Report in the order the runs were found.
        var order = new Dictionary<PdfNotdefRun, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _hits.Count; i++)
        {
            order[_hits[i].Run] = i;
        }

        result.RemovedList.Sort((a, b) => order[a].CompareTo(order[b]));
        result.KeptList.Sort((a, b) => order[a.Run].CompareTo(order[b.Run]));

        _ct.ThrowIfCancellationRequested();
        if (options.DryRun)
        {
            result.StreamsChanged = edits.Count;
            return result;
        }

        foreach ((Origin origin, List<Op> ops) in edits)
        {
            ops.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            byte[] source = origin.Data!;
            using var ms = new MemoryStream(source.Length);
            int at = 0;
            foreach (Op op in ops)
            {
                ms.Write(source, at, op.Start - at);
                ms.Write(op.Replacement!);
                at = op.End;
            }

            ms.Write(source, at, source.Length - at);
            if (origin.Form is { } form)
            {
                form.SetData(ms.ToArray(), compress: true);
            }
            else
            {
                origin.Page!.SetEntry("Contents", _doc.AddObject(_doc.CreateStream(ms.ToArray(), compress: true)));
            }

            result.StreamsChanged++;
        }

        return result;
    }

    // ---- reading the pages ----

    private struct Mark
    {
        public int Code;
        public int Length;
        public bool Notdef;
        public double Adjust;
        public string Text;
    }

    private sealed class Scanner(NotdefPass owner, PdfPage page, int pageIndex) : PdfDevice, IContentTap
    {
        private readonly List<(Origin? Origin, ReadOnlyMemory<byte> Data)> _streams = [];
        private readonly List<Mark> _marks = [];
        private readonly List<PdfDictionary?> _oc = [];
        private PdfStream? _pendingForm;
        private int _annotation;
        private PdfGlyphRun? _first;
        private bool _anyNotdef;
        private bool _sawRun;
        private readonly List<FontGlyph> _decoded = [];
        private string[] _groups = [];
        private bool _groupsVisible = true;

        public string? Unverified { get; private set; }

        public void CannotVerify(string reason) => Unverified ??= reason;

        public void StreamOrigin(PdfStream form) => _pendingForm = form;

        public void StreamBegin(ReadOnlyMemory<byte> data, PdfDictionary? resources)
        {
            Origin? origin = null;
            if (_pendingForm is { } form)
            {
                origin = owner.OriginOf(form, null);
                _pendingForm = null;
            }
            else if (_streams.Count == 0 && _annotation == 0)
            {
                origin = owner.OriginOf(null, page.Dictionary);
                origin.Segments++;
            }

            _streams.Add((origin, data));
            _marks.Clear();
            _first = null;
            _anyNotdef = false;
            _sawRun = false;
        }

        public void StreamEnd()
        {
            if (_streams.Count > 0)
            {
                _streams.RemoveAt(_streams.Count - 1);
            }

            _marks.Clear();
            _first = null;
            _anyNotdef = false;
        }

        public override void BeginMarkedContent(string tag, PdfDictionary? properties) => _oc.Add(tag == "OC" ? properties : null);

        public override void EndMarkedContent()
        {
            if (_oc.Count > 0)
            {
                _oc.RemoveAt(_oc.Count - 1);
            }
        }

        public override void BeginAnnotation(in PdfAnnotationInfo annotation) => _annotation++;

        public override void EndAnnotation() => _annotation--;

        public override void ShowGlyphRun(in PdfGlyphRun run, in PdfGraphicsState state)
        {
            if (_streams.Count == 0)
            {
                return;
            }

            _first ??= run;
            _sawRun = true;
            PdfFont? font = run.Font is null ? null : owner._doc.GetFont(run.Font);
            bool program = font is { IsEmbedded: true, IsSubstituted: false, IsType3: false, Program: not null };
            if (!program)
            {
                // No glyph program, so no .notdef to find; the operator still counts as one that showed none.
                return;
            }

            _decoded.Clear();
            font!.Decode(run.Codes.Span, _decoded);
            List<FontGlyph> decoded = _decoded;
            ReadOnlySpan<PdfGlyph> glyphs = run.Glyphs.Span;
            bool aligned = decoded.Count == glyphs.Length;
            double fs = run.FontSize;
            for (int i = 0; i < glyphs.Length; i++)
            {
                PdfGlyph g = glyphs[i];
                bool notdef = program && aligned && !decoded[i].Exists;
                double adjust = 0;
                if (notdef && fs != 0 && double.IsFinite(fs))
                {
                    // The glyph's advance as a TJ number: a number n moves the text by -n/1000 of the font size (ISO 32000-2 9.4.3).
                    bool word = g.ByteLength == 1 && g.Code == 32;
                    double spacing = run.CharSpacing + (word ? run.WordSpacing : 0);
                    double w = run.IsVertical ? g.VerticalAdvance : g.Width;
                    adjust = double.IsFinite(w) ? -1000.0 * ((w * fs) + spacing) / fs : 0;
                    if (!double.IsFinite(adjust))
                    {
                        adjust = 0;
                    }
                }

                _marks.Add(new Mark { Code = g.Code, Length = g.ByteLength, Notdef = notdef, Adjust = adjust, Text = g.Unicode ?? "\uFFFD" });
                if (notdef)
                {
                    _anyNotdef = true;
                    if (fs == 0 || !double.IsFinite(fs))
                    {
                        _zeroSize = true;
                    }
                }
            }
        }

        private bool _zeroSize;

        public void Operator(ContentOp op, int start, int end)
        {
            try
            {
                if (op is ContentOp.ShowText or ContentOp.ShowTextArray or ContentOp.NextLineShow or ContentOp.SpacedNextLineShow
                    && _sawRun && _first is { } run && _streams.Count > 0)
                {
                    Handle(op, start, end, run);
                }
            }
            finally
            {
                _marks.Clear();
                _first = null;
                _anyNotdef = false;
                _sawRun = false;
                _zeroSize = false;
            }
        }

        private void Handle(ContentOp op, int start, int end, PdfGlyphRun run)
        {
            (Origin? origin, ReadOnlyMemory<byte> data) = _streams[^1];

            // The operator's range starts where the previous operator ended, so it may begin with white space.
            ReadOnlySpan<byte> bytes = data.Span;
            while (start < end && bytes[start] is 0 or 9 or 10 or 12 or 13 or 32)
            {
                start++;
            }

            if (!_anyNotdef)
            {
                // A form's operator that showed no .notdef in this use: remember it, because another use may differ.
                if (origin?.Form is not null)
                {
                    origin.Clean.Add(start);
                }

                return;
            }

            if (owner._hits.Count >= MaxHits)
            {
                throw new PdfLimitException("The document shows more .notdef text operators than the limit allows.");
            }

            ReadGroups();
            bool drawn = run.RenderMode != PdfTextRenderMode.Invisible && _groupsVisible;
            var shown = new List<int>(_marks.Count);
            var codes = new List<int>();
            var text = new StringBuilder();
            var lost = new StringBuilder();
            foreach (Mark m in _marks)
            {
                shown.Add(m.Code);
                text.Append(m.Text);
                if (m.Notdef)
                {
                    codes.Add(m.Code);
                    lost.Append(m.Text);
                }
            }

            PdfNotdefSource source = _annotation > 0 ? PdfNotdefSource.AnnotationAppearance : origin?.Form is not null ? PdfNotdefSource.Form : PdfNotdefSource.PageContent;
            var report = new PdfNotdefRun(pageIndex, source, origin?.Form?.Reference?.Number ?? -1, start, run.FontName, run.Font?.Reference?.Number ?? -1,
                run.Font?.GetName("BaseFont") ?? string.Empty, [.. codes], [.. shown], run.RenderMode, drawn, _groups, _groupsVisible, text.ToString(), lost.ToString());
            if (origin is null)
            {
                // A pattern, or a stream the interpreter made up: it has no object of its own to edit.
                var orphan = new Op { Start = start, End = end, Cannot = "the content has no stream of its own to edit", Glyphs = codes.Count };
                var h = new Hit { Origin = null!, Op = orphan, Run = report };
                orphan.Hits.Add(h);
                owner._orphans.Add(orphan);
                owner._hits.Add(h);
                return;
            }

            if (!origin.Ops.TryGetValue(start, out Op? known))
            {
                known = new Op { Start = start, End = end, Glyphs = codes.Count };
                origin.Ops[start] = known;
                origin.Data ??= data.ToArray();
            }

            byte[]? replacement = null;
            if (known.Cannot is null)
            {
                try
                {
                    replacement = Rewrite(op, data.Span.Slice(start, end - start), _marks, _zeroSize, out string? cannot);
                    known.Cannot ??= cannot;
                }
                catch (PdfFormatException e)
                {
                    known.Cannot = e.Message;
                }
            }

            if (known.Replacement is null)
            {
                known.Replacement = replacement;
            }
            else if (replacement is null || !known.Replacement.AsSpan().SequenceEqual(replacement))
            {
                known.Disagrees = true;
            }

            // A form used twice on one page in the same way is one finding.
            foreach (Hit h in known.Hits)
            {
                if (h.Run.PageIndex == pageIndex)
                {
                    return;
                }
            }

            var hit = new Hit { Origin = origin, Op = known, Run = report };
            known.Hits.Add(hit);
            owner._hits.Add(hit);
        }

        private void ReadGroups()
        {
            var names = new List<string>();
            bool visible = true;
            foreach (PdfDictionary? d in _oc)
            {
                if (d is null)
                {
                    continue;
                }

                visible &= owner._oc.IsVisible(d);
                if (d.GetName("Type") == "OCMD")
                {
                    PdfObject? g = d.Get("OCGs");
                    if (g is PdfDictionary one)
                    {
                        names.Add(one.GetString("Name")?.GetText() ?? string.Empty);
                    }
                    else if (g is PdfArray arr)
                    {
                        for (int i = 0; i < arr.Count && i < 16; i++)
                        {
                            if (arr[i] is PdfDictionary gd)
                            {
                                names.Add(gd.GetString("Name")?.GetText() ?? string.Empty);
                            }
                        }
                    }
                }
                else
                {
                    names.Add(d.GetString("Name")?.GetText() ?? string.Empty);
                }
            }

            _groups = [.. names];
            _groupsVisible = visible;
        }
    }

    // ---- the rewrite of one operator ----

    private static byte[]? Rewrite(ContentOp op, ReadOnlySpan<byte> slice, List<Mark> marks, bool zeroSize, out string? cannot)
    {
        cannot = null;
        if (zeroSize)
        {
            // With a font size of 0 a TJ number moves nothing, so the advance of a removed glyph could not be kept.
            cannot = "the font size is 0";
            return null;
        }

        var lx = new ContentLexer(slice.ToArray(), 8);
        var operands = new List<Operand>();
        var items = new List<Operand>();
        int arrayAt = -1;
        while (true)
        {
            ContentToken t = lx.Next(out Operand o, out _);
            if (t != ContentToken.Operand)
            {
                break;
            }

            if (o.Kind == OperandKind.Array)
            {
                arrayAt = operands.Count;
                items.Clear();
                foreach (Operand it in lx.Items(in o))
                {
                    items.Add(it);
                }
            }

            operands.Add(o);
        }

        var elements = new List<Operand>();
        double aw = 0;
        double ac = 0;
        switch (op)
        {
            case ContentOp.ShowTextArray:
                if (arrayAt < 0)
                {
                    throw new PdfFormatException("A TJ operator has no array.");
                }

                elements = items;
                break;
            case ContentOp.ShowText:
            case ContentOp.NextLineShow:
                if (operands.Count == 0)
                {
                    throw new PdfFormatException("A text operator has no string.");
                }

                elements.Add(operands[^1]);
                break;
            default:
                if (operands.Count < 3)
                {
                    throw new PdfFormatException("A \" operator lacks operands.");
                }

                aw = operands[^3].Num;
                ac = operands[^2].Num;
                elements.Add(operands[^1]);
                break;
        }

        // Pieces in order: a string run (bytes) or a number (a TJ adjustment).
        var pieces = new List<(bool IsNumber, double Number, List<byte>? Bytes)>();
        int gi = 0;
        foreach (Operand it in elements)
        {
            if (it.Kind == OperandKind.Number)
            {
                AddNumber(pieces, it.Num);
            }
            else if (it.Kind == OperandKind.String)
            {
                ReadOnlySpan<byte> s = it.Str.Span;
                var cur = new List<byte>();
                int pos = 0;
                while (pos < s.Length)
                {
                    if (gi >= marks.Count || marks[gi].Length <= 0 || pos + marks[gi].Length > s.Length)
                    {
                        throw new PdfFormatException("A text operator's glyphs do not match its string.");
                    }

                    Mark mk = marks[gi++];
                    if (mk.Notdef)
                    {
                        if (cur.Count > 0)
                        {
                            pieces.Add((false, 0, cur));
                            cur = [];
                        }

                        AddNumber(pieces, mk.Adjust);
                    }
                    else
                    {
                        for (int k = 0; k < mk.Length; k++)
                        {
                            cur.Add(s[pos + k]);
                        }
                    }

                    pos += mk.Length;
                }

                if (cur.Count > 0)
                {
                    pieces.Add((false, 0, cur));
                }
            }
        }

        if (gi != marks.Count)
        {
            throw new PdfFormatException("A text operator's glyphs do not match its string.");
        }

        var o2 = new List<byte>();
        void Text(string s) => o2.AddRange(Encoding.ASCII.GetBytes(s));
        Text(" "); // the previous operator may end right where this one starts
        if (op == ContentOp.NextLineShow)
        {
            Text("T* ");
        }
        else if (op == ContentOp.SpacedNextLineShow)
        {
            Text(Format(aw) + " Tw " + Format(ac) + " Tc T* ");
        }

        o2.Add((byte)'[');
        foreach ((bool isNumber, double number, List<byte>? bytes) in pieces)
        {
            if (isNumber)
            {
                Text(Format(number) + " ");
            }
            else
            {
                o2.Add((byte)'(');
                foreach (byte b in bytes!)
                {
                    if (b is (byte)'(' or (byte)')' or (byte)'\\')
                    {
                        o2.Add((byte)'\\');
                        o2.Add(b);
                    }
                    else if (b is < 32 or > 126)
                    {
                        o2.Add((byte)'\\');
                        o2.Add((byte)('0' + ((b >> 6) & 7)));
                        o2.Add((byte)('0' + ((b >> 3) & 7)));
                        o2.Add((byte)('0' + (b & 7)));
                    }
                    else
                    {
                        o2.Add(b);
                    }
                }

                o2.Add((byte)')');
            }
        }

        Text("] TJ");
        return [.. o2];
    }

    private static void AddNumber(List<(bool IsNumber, double Number, List<byte>? Bytes)> pieces, double v)
    {
        if (!double.IsFinite(v))
        {
            return;
        }

        if (pieces.Count > 0 && pieces[^1].IsNumber)
        {
            pieces[^1] = (true, pieces[^1].Number + v, null);
        }
        else
        {
            pieces.Add((true, v, null));
        }
    }

    // Fixed notation, no exponent, six decimals at most (content streams do not take exponents).
    private static string Format(double v)
    {
        if (!double.IsFinite(v) || Math.Abs(v) < 5e-7)
        {
            return "0";
        }

        string t = v.ToString("0.######", CultureInfo.InvariantCulture);
        return t == "-0" ? "0" : t;
    }
}
