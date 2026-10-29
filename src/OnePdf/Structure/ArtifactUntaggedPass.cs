using System.Text;
using OnePdf.Content;

namespace OnePdf.Structure;

/// <summary>
/// Wraps painting operators that lie outside any marked content in <c>/Artifact BMC … EMC</c>, page by page. The scan follows the same
/// rules as the PDF/UA check (ISO 14289-1 7.1): a sequence with an <c>/MCID</c> is tagged, an <c>/Artifact</c> sequence is an artifact,
/// any other sequence is neither; a Form XObject drawn outside marked content is wrapped at the place it is drawn, and only when nothing
/// in it is tagged or text. Only <c>BMC</c> and <c>EMC</c> are inserted, around whole operations, so what a page paints does not change.
/// All edits are worked out before the first one is applied.
/// </summary>
internal sealed class ArtifactUntaggedPass
{
    private const int MaxFormDepth = 32;

    private sealed class FormInfo
    {
        public bool Graphics;
        public bool Text;
        public bool Tagged;
        public bool Unreadable;
    }

    private readonly PdfDocument _doc;
    private readonly CancellationToken _ct;
    private readonly Dictionary<PdfStream, FormInfo> _forms = new(ReferenceEqualityComparer.Instance);
    private readonly PdfArtifactResult _result = new();

    private ArtifactUntaggedPass(PdfDocument doc, CancellationToken ct)
    {
        _doc = doc;
        _ct = ct;
    }

    public static PdfArtifactResult Run(PdfDocument doc, CancellationToken ct)
    {
        doc.EnsureEditable(0);
        return new ArtifactUntaggedPass(doc, ct).Execute();
    }

    private PdfArtifactResult Execute()
    {
        var edits = new List<(PdfDictionary Page, byte[] Data)>();
        for (int i = 0; i < _doc.Pages.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            PdfPage page = _doc.Pages[i];
            var stats = new PdfArtifactPageResult { PageIndex = i };
            ReadOnlyMemory<byte> data;
            try
            {
                data = ReadPageContent(page);
            }
            catch (PdfFormatException)
            {
                _result.WarningList.Add($"Page {i + 1}: the content could not be decoded, so it was left as it is.");
                continue;
            }

            var wraps = new List<(int Start, int End)>();
            var info = new FormInfo();
            Scan(data, page.Resources, 0, info, wraps, stats);
            if (info.Unreadable)
            {
                _result.WarningList.Add($"Page {i + 1}: the content could not be read to its end, so it was left as it is.");
                stats.Wrapped = 0;
                continue;
            }

            if (wraps.Count > 0)
            {
                edits.Add((page.Dictionary, Apply(data.Span, wraps)));
            }

            if (stats.Wrapped > 0 || stats.UntaggedTextOperators > 0 || stats.FormsLeft > 0)
            {
                _result.PageList.Add(stats);
            }

            _result.Wrapped += stats.Wrapped;
            _result.UntaggedTextOperators += stats.UntaggedTextOperators;
            _result.FormsLeft += stats.FormsLeft;
        }

        _ct.ThrowIfCancellationRequested();
        foreach ((PdfDictionary page, byte[] bytes) in edits)
        {
            page.SetEntry("Contents", _doc.AddObject(_doc.CreateStream(bytes, compress: true)));
            _result.StreamsChanged++;
        }

        return _result;
    }

    private ReadOnlyMemory<byte> ReadPageContent(PdfPage page)
    {
        IReadOnlyList<PdfStream> streams = page.ContentStreams;
        if (streams.Count == 1)
        {
            return streams[0].GetDecodedBytes(_ct);
        }

        var ms = new MemoryStream();
        foreach (PdfStream s in streams)
        {
            ms.Write(s.GetDecodedBytes(_ct).Span);
            ms.WriteByte((byte)'\n');
            if (ms.Length > _doc.Limits.MaxDecodedStreamBytes)
            {
                throw new PdfLimitException("The page content is larger than the decoded stream limit.");
            }
        }

        return ms.ToArray();
    }

    private static byte[] Apply(ReadOnlySpan<byte> src, List<(int Start, int End)> wraps)
    {
        byte[] open = Encoding.Latin1.GetBytes(" /Artifact BMC ");
        byte[] close = Encoding.Latin1.GetBytes(" EMC ");
        var output = new MemoryStream(src.Length + wraps.Count * (open.Length + close.Length));
        int pos = 0;
        foreach ((int start, int end) in wraps)
        {
            output.Write(src[pos..start]);
            output.Write(open);
            output.Write(src[start..end]);
            output.Write(close);
            pos = end;
        }

        output.Write(src[pos..]);
        return output.ToArray();
    }

    // What a form paints outside marked content. Memoized per stream, which is also the cycle guard.
    private FormInfo FormOf(PdfStream form, PdfDictionary? parentResources, int depth)
    {
        if (_forms.TryGetValue(form, out FormInfo? known))
        {
            return known;
        }

        var info = new FormInfo();
        _forms[form] = info;
        if (depth >= MaxFormDepth)
        {
            info.Unreadable = true;
            return info;
        }

        ReadOnlyMemory<byte> data;
        try
        {
            data = form.GetDecodedBytes(_ct);
        }
        catch (PdfException)
        {
            info.Unreadable = true;
            return info;
        }

        Scan(data, form.Dictionary.GetDictionary("Resources") ?? parentResources, depth, info, null, null);
        return info;
    }

    // One pass over a content stream. With wraps == null it only describes the stream (a form); otherwise it records where to wrap.
    private void Scan(ReadOnlyMemory<byte> data, PdfDictionary? resources, int depth, FormInfo info, List<(int Start, int End)>? wraps, PdfArtifactPageResult? stats)
    {
        PdfDictionary? properties = resources?.GetDictionary("Properties");
        PdfDictionary? xobjects = resources?.GetDictionary("XObject");
        var lexer = new ContentLexer(data, _doc.Limits.MaxNestingDepth);
        var stack = new List<byte>(); // 0 artifact, 1 tagged, 2 other
        int artifact = 0, tagged = 0, segment = 0, pathStart = -1;
        string? firstName = null;
        Operand last = default;
        long operators = 0;
        while (true)
        {
            ContentToken tok = lexer.Next(out Operand operand, out ContentOp op);
            if (tok == ContentToken.End)
            {
                break;
            }

            if (tok == ContentToken.Operand)
            {
                if (operand.Kind == OperandKind.Name && firstName is null)
                {
                    firstName = operand.Name;
                }

                last = operand;
                continue;
            }

            if (++operators > _doc.Limits.MaxOperatorsPerPage)
            {
                throw new PdfLimitException("A content stream has too many operators.");
            }

            int end = lexer.Position;
            bool painting = true;
            bool isText = false;
            switch (op)
            {
                case ContentOp.BeginMarked:
                    stack.Add(firstName == "Artifact" ? (byte)0 : (byte)2);
                    artifact += firstName == "Artifact" ? 1 : 0;
                    pathStart = -1;
                    painting = false;
                    break;
                case ContentOp.BeginMarkedProps:
                    PdfDictionary? props = last.Kind == OperandKind.Object ? last.Obj as PdfDictionary : last.Kind == OperandKind.Name && last.Name is { } pn ? properties?.GetDictionary(pn) : null;
                    if (firstName == "Artifact")
                    {
                        stack.Add(0);
                        artifact++;
                    }
                    else if (props is not null && props.ContainsKey("MCID"))
                    {
                        stack.Add(1);
                        tagged++;
                        info.Tagged = true;
                    }
                    else
                    {
                        stack.Add(2);
                    }

                    pathStart = -1;
                    painting = false;
                    break;
                case ContentOp.EndMarked:
                    if (stack.Count > 0)
                    {
                        byte k = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        artifact -= k == 0 ? 1 : 0;
                        tagged -= k == 1 ? 1 : 0;
                    }

                    pathStart = -1;
                    painting = false;
                    break;
                case ContentOp.Move or ContentOp.Line or ContentOp.Curve or ContentOp.CurveV or ContentOp.CurveY or ContentOp.Close or ContentOp.Rect:
                    if (pathStart < 0)
                    {
                        pathStart = segment;
                    }

                    painting = false;
                    break;
                case ContentOp.ClipNz or ContentOp.ClipEo:
                    painting = false;
                    break;
                case ContentOp.Stroke or ContentOp.CloseStroke or ContentOp.FillNz or ContentOp.FillNzOld or ContentOp.FillEo
                    or ContentOp.FillStroke or ContentOp.FillStrokeEo or ContentOp.CloseFillStroke or ContentOp.CloseFillStrokeEo:
                    break;
                case ContentOp.Shading or ContentOp.Do or ContentOp.BeginInline:
                    pathStart = -1;
                    break;
                case ContentOp.ShowText or ContentOp.ShowTextArray or ContentOp.NextLineShow or ContentOp.SpacedNextLineShow:
                    isText = true;
                    pathStart = -1;
                    break;
                default:
                    pathStart = -1;
                    painting = false;
                    break;
            }

            if (op == ContentOp.BeginInline)
            {
                // After BI the lexer is at the image dictionary; read it and the data so the next operator is the one after EI.
                if (lexer.ReadInlineImageDictionary() is null)
                {
                    info.Unreadable = true;
                    return;
                }

                lexer.ReadInlineImageData(-1, out bool found);
                if (!found)
                {
                    info.Unreadable = true;
                    return;
                }

                end = lexer.Position;
            }

            if (painting && artifact == 0 && tagged == 0)
            {
                int start = op is ContentOp.Stroke or ContentOp.CloseStroke or ContentOp.FillNz or ContentOp.FillNzOld or ContentOp.FillEo
                    or ContentOp.FillStroke or ContentOp.FillStrokeEo or ContentOp.CloseFillStroke or ContentOp.CloseFillStrokeEo && pathStart >= 0 ? pathStart : segment;
                if (isText)
                {
                    info.Text = true;
                    if (stats is not null)
                    {
                        stats.UntaggedTextOperators++;
                    }
                }
                else if (op == ContentOp.Do && firstName is not null && xobjects?.Get(firstName) is PdfStream form && form.Dictionary.GetName("Subtype") == "Form")
                {
                    FormInfo fi = FormOf(form, resources, depth + 1);
                    info.Graphics |= fi.Graphics;
                    info.Text |= fi.Text;
                    info.Tagged |= fi.Tagged;
                    info.Unreadable |= fi.Unreadable && depth > 0;
                    if (wraps is not null && (fi.Graphics || fi.Text || fi.Unreadable))
                    {
                        if (fi.Unreadable || fi.Text || fi.Tagged)
                        {
                            // Hiding text, or tagged content of the form's own, is not this call's decision.
                            stats!.FormsLeft++;
                        }
                        else
                        {
                            wraps.Add((start, end));
                            stats!.Wrapped++;
                        }
                    }
                }
                else
                {
                    info.Graphics = true;
                    if (wraps is not null)
                    {
                        wraps.Add((start, end));
                        stats!.Wrapped++;
                    }
                }
            }

            if (op is not (ContentOp.Move or ContentOp.Line or ContentOp.Curve or ContentOp.CurveV or ContentOp.CurveY or ContentOp.Close or ContentOp.Rect or ContentOp.ClipNz or ContentOp.ClipEo))
            {
                pathStart = -1;
            }

            segment = end;
            firstName = null;
            last = default;
            lexer.ResetItems();
        }
    }
}
