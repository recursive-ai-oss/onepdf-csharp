using System.Globalization;
using System.Text;
using OnePdf.Content;

namespace OnePdf.Structure;

/// <summary>
/// Takes the tags out of a document: the structure tree and everything that points into it, and the marked-content sequences with an
/// <c>/MCID</c> in every content stream a page can reach (ISO 32000-2 14.7 and 14.6). It reads the operators with the content
/// lexer and edits the bytes in place of the operators it removes, so what the page paints does not change. The edits are all worked
/// out before the first one is applied, so a cancelled or failed call leaves the document as it was.
/// </summary>
internal sealed class RemoveTagsPass
{
    private readonly PdfDocument _doc;
    private readonly PdfRemoveTagsOptions _options;
    private readonly CancellationToken _ct;
    private readonly PdfRemoveTagsResult _result = new();
    private readonly HashSet<PdfObject> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<(PdfObject Item, PdfDictionary? Resources)> _work = new();
    private readonly List<PdfDictionary> _strip = [];
    private readonly List<(PdfStream Stream, byte[] Data, bool Compress)> _streamEdits = [];
    private readonly List<(PdfDictionary Page, byte[] Data)> _pageEdits = [];
    private int _visited;

    private RemoveTagsPass(PdfDocument doc, PdfRemoveTagsOptions options, CancellationToken ct)
    {
        _doc = doc;
        _options = options;
        _ct = ct;
    }

    public static PdfRemoveTagsResult Run(PdfDocument doc, PdfRemoveTagsOptions options, CancellationToken ct)
    {
        doc.EnsureEditable(0);
        return new RemoveTagsPass(doc, options, ct).Execute();
    }

    private PdfRemoveTagsResult Execute()
    {
        int pageCount = _doc.Pages.Count;
        for (int i = 0; i < pageCount; i++)
        {
            _ct.ThrowIfCancellationRequested();
            PdfPage page = _doc.Pages[i];
            PdfDictionary pd = page.Dictionary;
            _strip.Add(pd);
            if (!ReadPageContent(page, out ReadOnlyMemory<byte> data))
            {
                _result.WarningList.Add($"Page {i + 1}: the content could not be read completely, so its marked content was left as it is.");
            }
            else if (Rewrite(data, page.Resources, $"page {i + 1}") is { } bytes)
            {
                _pageEdits.Add((pd, bytes));
            }

            Push(page.Resources, null);
            if (pd.Get("Annots") is PdfArray annots)
            {
                for (int a = 0; a < annots.Count; a++)
                {
                    if (annots[a] is not PdfDictionary ad)
                    {
                        continue;
                    }

                    _strip.Add(ad);
                    if (ad.Get("AP") is PdfDictionary ap)
                    {
                        foreach ((string key, _) in ap.Entries)
                        {
                            if (key is not ("N" or "R" or "D"))
                            {
                                continue;
                            }

                            PdfObject? v = ap.Get(key);
                            if (v is PdfStream s)
                            {
                                _work.Push((s, page.Resources));
                            }
                            else if (v is PdfDictionary states)
                            {
                                foreach ((string sk, _) in states.Entries)
                                {
                                    if (states.Get(sk) is PdfStream ss)
                                    {
                                        _work.Push((ss, page.Resources));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Drain();
        }

        // Nothing has been changed yet; from here on the edits are applied.
        _ct.ThrowIfCancellationRequested();
        foreach ((PdfDictionary page, byte[] data) in _pageEdits)
        {
            page.SetEntry("Contents", _doc.AddObject(_doc.CreateStream(data, compress: true)));
            _result.StreamsChanged++;
        }

        foreach ((PdfStream stream, byte[] data, bool compress) in _streamEdits)
        {
            stream.SetData(data, compress);
            _result.StreamsChanged++;
        }

        foreach (PdfDictionary d in _strip)
        {
            if (d.RemoveEntry("StructParents"))
            {
                _result.StructParentsRemoved++;
            }

            if (d.RemoveEntry("StructParent"))
            {
                _result.StructParentsRemoved++;
            }
        }

        PdfDictionary catalog = _doc.Catalog;
        if (catalog.ContainsKey("StructTreeRoot"))
        {
            catalog.RemoveEntry("StructTreeRoot");
            _result.StructureTreeRemoved = true;
        }

        if (catalog.ContainsKey("MarkInfo"))
        {
            catalog.RemoveEntry("MarkInfo");
            _result.MarkInfoRemoved = true;
        }

        return _result;
    }

    private bool ReadPageContent(PdfPage page, out ReadOnlyMemory<byte> data)
    {
        IReadOnlyList<PdfStream> streams = page.ContentStreams;
        data = ReadOnlyMemory<byte>.Empty;
        try
        {
            if (streams.Count == 1)
            {
                data = streams[0].GetDecodedBytes(_ct);
                return true;
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

            data = ms.ToArray();
            return true;
        }
        catch (PdfFormatException)
        {
            return false;
        }
    }

    // ---- reaching the streams ----

    private void Push(PdfDictionary? resources, PdfDictionary? fallback)
    {
        resources ??= fallback;
        if (resources is null)
        {
            return;
        }

        if (resources.Get("XObject") is PdfDictionary xo)
        {
            foreach ((string k, _) in xo.Entries)
            {
                if (xo.Get(k) is PdfStream s)
                {
                    _work.Push((s, resources));
                }
            }
        }

        if (resources.Get("Pattern") is PdfDictionary pat)
        {
            foreach ((string k, _) in pat.Entries)
            {
                if (pat.Get(k) is PdfStream s)
                {
                    _work.Push((s, resources));
                }
            }
        }

        if (resources.Get("ExtGState") is PdfDictionary egs)
        {
            foreach ((string k, _) in egs.Entries)
            {
                if (egs.Get(k) is PdfDictionary g && g.Get("SMask") is PdfDictionary sm && sm.Get("G") is PdfStream form)
                {
                    _work.Push((form, resources));
                }
            }
        }

        if (resources.Get("Font") is PdfDictionary fonts)
        {
            foreach ((string k, _) in fonts.Entries)
            {
                if (fonts.Get(k) is PdfDictionary f && f.GetName("Subtype") == "Type3" && f.Get("CharProcs") is PdfDictionary procs)
                {
                    PdfDictionary? fr = f.Get("Resources") as PdfDictionary ?? resources;
                    foreach ((string pk, _) in procs.Entries)
                    {
                        if (procs.Get(pk) is PdfStream ps)
                        {
                            _work.Push((ps, fr));
                        }
                    }
                }
            }
        }
    }

    private void Drain()
    {
        while (_work.Count > 0)
        {
            (PdfObject item, PdfDictionary? inherited) = _work.Pop();
            if (item is not PdfStream stream || !_seen.Add(stream))
            {
                continue;
            }

            if (++_visited > _doc.Limits.MaxObjects)
            {
                throw new PdfLimitException("The document reaches more content streams than the object limit allows.");
            }

            if ((_visited & 255) == 0)
            {
                _ct.ThrowIfCancellationRequested();
            }

            _strip.Add(stream.Dictionary);
            string? subtype = stream.Dictionary.GetName("Subtype");
            if (subtype == "Image")
            {
                continue;
            }

            PdfDictionary? own = stream.Dictionary.Get("Resources") as PdfDictionary;
            try
            {
                ReadOnlyMemory<byte> data = stream.GetDecodedBytes(_ct);
                if (Rewrite(data, own ?? inherited, "a form or appearance stream") is { } bytes)
                {
                    _streamEdits.Add((stream, bytes, stream.Dictionary.ContainsKey("Filter")));
                }
            }
            catch (PdfFormatException)
            {
                _result.WarningList.Add("A form or appearance stream could not be decoded, so its marked content was left as it is.");
            }

            Push(own, inherited);
        }
    }

    // ---- one content stream ----

    private enum Close : byte
    {
        Keep,
        Drop,
    }

    private readonly record struct Edit(int Start, int End, byte[] Bytes);

    private static readonly byte[] s_space = [(byte)' '];

    /// <summary>Returns the rewritten bytes, or null when nothing in the stream has to change (or it cannot be read to its end).</summary>
    private byte[]? Rewrite(ReadOnlyMemory<byte> data, PdfDictionary? resources, string where)
    {
        var lx = new ContentLexer(data, _doc.Limits.MaxNestingDepth);
        var edits = new List<Edit>();
        var stack = new List<Close>();
        Operand a = default;
        Operand b = default;
        int operands = 0;
        int segment = 0;
        int removed = 0;
        int artifacts = 0;
        int actualText = 0;
        while (true)
        {
            ContentToken t = lx.Next(out Operand operand, out ContentOp op);
            if (t == ContentToken.End)
            {
                break;
            }

            if (t == ContentToken.Operand)
            {
                a = b;
                b = operand;
                operands++;
                continue;
            }

            int end = lx.Position;
            switch (op)
            {
                case ContentOp.BeginMarked:
                {
                    string? tag = operands >= 1 && b.Kind == OperandKind.Name ? b.Name : null;
                    if (tag == "Artifact" && _options.RemoveArtifactMarks)
                    {
                        edits.Add(new Edit(segment, end, s_space));
                        stack.Add(Close.Drop);
                        artifacts++;
                    }
                    else
                    {
                        stack.Add(Close.Keep);
                    }

                    break;
                }

                case ContentOp.BeginMarkedProps:
                {
                    string? tag = operands >= 2 && a.Kind == OperandKind.Name ? a.Name : null;
                    PdfDictionary? props = null;
                    if (operands >= 2)
                    {
                        if (b.Kind == OperandKind.Name)
                        {
                            props = resources?.Get("Properties") is PdfDictionary pr ? pr.Get(b.Name!) as PdfDictionary : null;
                        }
                        else if (b.Kind == OperandKind.Object)
                        {
                            props = lx.ToObject(in b) as PdfDictionary;
                        }
                    }

                    if (tag == "Artifact" && _options.RemoveArtifactMarks)
                    {
                        edits.Add(new Edit(segment, end, s_space));
                        stack.Add(Close.Drop);
                        artifacts++;
                    }
                    else if (tag is not null && tag != "OC" && props is not null && props.ContainsKey("MCID"))
                    {
                        removed++;
                        PdfString? at = props.Get("ActualText") as PdfString;
                        if (at is not null)
                        {
                            // Text extraction takes /ActualText from the content stream, so it stays, without the identifier.
                            edits.Add(new Edit(segment, end, Encoding.Latin1.GetBytes(" " + NameText(tag) + " <</ActualText " + HexText(at) + ">> BDC")));
                            stack.Add(Close.Keep);
                            actualText++;
                        }
                        else if (_options.MarkedContent == PdfMarkedContentRemoval.ConvertToBmc)
                        {
                            edits.Add(new Edit(segment, end, Encoding.Latin1.GetBytes(" " + NameText(tag) + " BMC")));
                            stack.Add(Close.Keep);
                        }
                        else
                        {
                            edits.Add(new Edit(segment, end, s_space));
                            stack.Add(Close.Drop);
                        }
                    }
                    else
                    {
                        stack.Add(Close.Keep);
                    }

                    break;
                }

                case ContentOp.EndMarked:
                    if (stack.Count > 0)
                    {
                        Close c = stack[^1];
                        stack.RemoveAt(stack.Count - 1);
                        if (c == Close.Drop)
                        {
                            edits.Add(new Edit(segment, end, s_space));
                        }
                    }

                    break;

                case ContentOp.BeginInline:
                    if (lx.ReadInlineImageDictionary() is null)
                    {
                        _result.WarningList.Add($"The inline image dictionary in {where} could not be read, so its marked content was left as it is.");
                        return null;
                    }

                    lx.ReadInlineImageData(-1, out bool found);
                    if (!found)
                    {
                        _result.WarningList.Add($"An inline image in {where} has no end, so its marked content was left as it is.");
                        return null;
                    }

                    end = lx.Position;
                    break;
            }

            segment = end;
            operands = 0;
            a = default;
            b = default;
            lx.ResetItems();
        }

        if (edits.Count == 0)
        {
            return null;
        }

        _result.SequencesRemoved += removed;
        _result.ArtifactSequencesRemoved += artifacts;
        _result.ActualTextKept += actualText;
        ReadOnlySpan<byte> src = data.Span;
        var output = new MemoryStream(src.Length);
        int pos = 0;
        foreach (Edit e in edits)
        {
            output.Write(src[pos..e.Start]);
            output.Write(e.Bytes);
            pos = e.End;
        }

        output.Write(src[pos..]);
        return output.ToArray();
    }

    private static string NameText(string name)
    {
        var sb = new StringBuilder("/");
        foreach (char ch in name)
        {
            if (ch is > ' ' and < '\u007f' and not ('#' or '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%'))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('#').Append(((int)ch & 0xFF).ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static string HexText(PdfString s)
    {
        var sb = new StringBuilder("<");
        foreach (byte x in s.Bytes.Span)
        {
            sb.Append(x.ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.Append('>').ToString();
    }
}
