using System.Linq;
using OnePdf.Content;

namespace OnePdf.PdfA;

/// <summary>
/// Finds which character codes of some simple fonts are actually shown, by reading the content of every page, the form XObjects,
/// patterns, soft-mask groups and Type 3 glyph procedures they reach, and the appearance streams of annotations. Used to subset
/// the library fonts that a PDF/A save embeds to the glyphs the document draws.
/// </summary>
/// <remarks>
/// The result is deliberately conservative. A font that is listed in the form's default resources (a viewer may type into a field
/// with it), that is never shown, or that is used where the scan cannot follow (a stream that cannot be decoded, more than
/// <see cref="MaxOperators"/> operators) is reported as needing its whole encoding. Text is attributed to a font through the font
/// resource of the content it appears in, with the graphics-state stack respected (ISO 32000-2 8.4.2, 9.3.1).
/// </remarks>
internal sealed class FontUsage
{
    private const int MaxOperators = 1 << 24;
    private const int MaxStack = 4096;

    private readonly PdfDocument _doc;
    private readonly HashSet<int> _targets;
    private readonly Dictionary<int, HashSet<int>> _used = [];
    private readonly Dictionary<PdfStream, List<PdfDictionary>> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<(ReadOnlyMemory<byte> Data, PdfDictionary Resources)> _work = new();
    private long _operators;
    private Operand[] _pendingItems = [];

    private FontUsage(PdfDocument doc, HashSet<int> targets)
    {
        _doc = doc;
        _targets = targets;
    }

    /// <summary>Font object numbers that must keep their whole encoding.</summary>
    public HashSet<int> Full { get; } = [];

    /// <summary>The codes shown with a font, by font object number; a font that is never shown has no entry.</summary>
    public IReadOnlyDictionary<int, HashSet<int>> Used => _used;

    /// <summary>True when every content stream was read; false means every font must keep its whole encoding.</summary>
    public bool Complete { get; private set; } = true;

    /// <summary>Reads the document's content for the fonts with the object numbers in <paramref name="targets"/>.</summary>
    public static FontUsage Scan(PdfDocument doc, HashSet<int> targets, CancellationToken ct)
    {
        var usage = new FontUsage(doc, targets);
        try
        {
            usage.Run(ct);
        }
        catch (Exception ex) when (ex is PdfFormatException or PdfLimitException)
        {
            usage.Complete = false;
        }

        return usage;
    }

    private void Run(CancellationToken ct)
    {
        // Fonts a form may be filled in with.
        if (_doc.Form?.Dictionary.GetDictionary("DR")?.GetDictionary("Font") is { } dr)
        {
            foreach (string key in dr.Keys.ToArray())
            {
                if (dr.GetRaw(key) is PdfReference r && _targets.Contains(r.Number))
                {
                    Full.Add(r.Number);
                }
            }
        }

        PdfPageCollection pages = _doc.Pages;
        for (int i = 0; i < pages.Count && Complete; i++)
        {
            ct.ThrowIfCancellationRequested();
            PdfPage page = pages[i];
            PushPage(page);
            foreach (PdfAnnotation a in page.Annotations)
            {
                PushAppearances(a.Dictionary, page.Resources);
            }

            Drain(ct);
        }
    }

    private void PushPage(PdfPage page)
    {
        PdfObject? contents = page.Dictionary.Get("Contents");
        var all = new List<ReadOnlyMemory<byte>>();
        if (contents is PdfStream one)
        {
            all.Add(one.GetDecodedBytes());
        }
        else if (contents is PdfArray list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is PdfStream s)
                {
                    all.Add(s.GetDecodedBytes());
                }
            }
        }

        long total = 0;
        foreach (ReadOnlyMemory<byte> m in all)
        {
            total += m.Length + 1;
        }

        if (total > _doc.Limits.MaxDecodedStreamBytes)
        {
            Complete = false;
            return;
        }

        var joined = new byte[total];
        int at = 0;
        foreach (ReadOnlyMemory<byte> m in all)
        {
            m.CopyTo(joined.AsMemory(at));
            at += m.Length;
            joined[at++] = (byte)'\n';
        }

        _work.Push((joined, page.Resources));
    }

    // Normal, rollover and down appearances: each is a stream, or a dictionary of states to streams (ISO 32000-2 12.5.5).
    private void PushAppearances(PdfDictionary annotation, PdfDictionary pageResources)
    {
        if (annotation.GetDictionary("AP") is not { } ap)
        {
            return;
        }

        foreach (string key in ap.Keys.ToArray())
        {
            switch (ap.Get(key))
            {
                case PdfStream s:
                    PushForm(s, pageResources);
                    break;
                case PdfDictionary states:
                    foreach (string state in states.Keys.ToArray())
                    {
                        if (states.Get(state) is PdfStream ss)
                        {
                            PushForm(ss, pageResources);
                        }
                    }

                    break;
                default:
                    break;
            }
        }
    }

    // A form-like stream (XObject, pattern, glyph procedure, soft-mask group) runs with its own resources, else with the caller's.
    private void PushForm(PdfStream stream, PdfDictionary inherited)
    {
        PdfDictionary resources = stream.Dictionary.GetDictionary("Resources") ?? inherited;
        if (!_seen.TryGetValue(stream, out List<PdfDictionary>? with))
        {
            _seen[stream] = with = [];
        }

        foreach (PdfDictionary r in with)
        {
            if (ReferenceEquals(r, resources))
            {
                return;
            }
        }

        with.Add(resources);
        _work.Push((stream.GetDecodedBytes(), resources));
    }

    private void Drain(CancellationToken ct)
    {
        while (_work.Count > 0 && Complete)
        {
            ct.ThrowIfCancellationRequested();
            (ReadOnlyMemory<byte> data, PdfDictionary resources) = _work.Pop();
            Read(data, resources);
        }

        _work.Clear();
    }

    private void Read(ReadOnlyMemory<byte> data, PdfDictionary resources)
    {
        var lx = new ContentLexer(data, 64);
        var fonts = new Stack<int>();
        int font = -1;
        string? lastName = null;
        Operand lastString = default, lastArray = default;
        bool hasString = false, hasArray = false;
        while (true)
        {
            lx.ResetItems();
            ContentToken t = lx.Next(out Operand operand, out ContentOp op);
            if (t == ContentToken.End)
            {
                return;
            }

            if (t == ContentToken.Operand)
            {
                switch (operand.Kind)
                {
                    case OperandKind.Name:
                        lastName = operand.Name;
                        break;
                    case OperandKind.String:
                        lastString = operand;
                        hasString = true;
                        break;
                    case OperandKind.Array:
                        lastArray = operand;
                        hasArray = true;
                        break;
                    default:
                        break;
                }

                // The lexer keeps array items in a buffer that the next call resets; copy what a TJ needs right away.
                if (operand.Kind == OperandKind.Array)
                {
                    _pendingItems = lx.Items(operand).ToArray();
                }

                continue;
            }

            if (++_operators > MaxOperators)
            {
                Complete = false;
                return;
            }

            switch (op)
            {
                case ContentOp.Save:
                    if (fonts.Count >= MaxStack)
                    {
                        Complete = false;
                        return;
                    }

                    fonts.Push(font);
                    break;
                case ContentOp.Restore:
                    if (fonts.Count > 0)
                    {
                        font = fonts.Pop();
                    }

                    break;
                case ContentOp.TextFont:
                    font = FontNumber(resources, lastName, out PdfDictionary? fontDict);
                    if (fontDict is not null && fontDict.GetName("Subtype") == "Type3")
                    {
                        PushType3(fontDict, resources);
                    }

                    break;
                case ContentOp.ShowText:
                case ContentOp.NextLineShow:
                case ContentOp.SpacedNextLineShow:
                    if (hasString)
                    {
                        Record(font, lastString.Str.Span);
                    }

                    break;
                case ContentOp.ShowTextArray:
                    if (hasArray)
                    {
                        foreach (Operand item in _pendingItems)
                        {
                            if (item.Kind == OperandKind.String)
                            {
                                Record(font, item.Str.Span);
                            }
                        }
                    }

                    break;
                case ContentOp.Do:
                    if (lastName is not null && resources.GetDictionary("XObject")?.Get(lastName) is PdfStream xo && xo.Dictionary.GetName("Subtype") == "Form")
                    {
                        PushForm(xo, resources);
                    }

                    break;
                case ContentOp.FillColorN:
                case ContentOp.StrokeColorN:
                    if (lastName is not null && resources.GetDictionary("Pattern")?.Get(lastName) is PdfStream pattern)
                    {
                        PushForm(pattern, resources);
                    }

                    break;
                case ContentOp.SetExtGState:
                    if (lastName is not null && resources.GetDictionary("ExtGState")?.GetDictionary(lastName) is { } gs)
                    {
                        if (gs.GetDictionary("SMask")?.Get("G") is PdfStream group)
                        {
                            PushForm(group, resources);
                        }

                        if (gs.GetArray("Font") is { Count: >= 1 } fa && fa.GetRaw(0) is PdfReference fr)
                        {
                            font = fr.Number;
                            if (fa[0] is PdfDictionary fd && fd.GetName("Subtype") == "Type3")
                            {
                                PushType3(fd, resources);
                            }
                        }
                    }

                    break;
                default:
                    break;
            }

            lastName = null;
            hasString = hasArray = false;
        }
    }

        private static int FontNumber(PdfDictionary resources, string? name, out PdfDictionary? dict)
    {
        dict = null;
        if (name is null || resources.GetDictionary("Font") is not { } fonts)
        {
            return -1;
        }

        dict = fonts.GetDictionary(name);
        return fonts.GetRaw(name) is PdfReference r ? r.Number : -1;
    }

    private void PushType3(PdfDictionary font, PdfDictionary inherited)
    {
        PdfDictionary resources = font.GetDictionary("Resources") ?? inherited;
        if (font.GetDictionary("CharProcs") is not { } procs)
        {
            return;
        }

        foreach (string key in procs.Keys.ToArray())
        {
            if (procs.Get(key) is PdfStream s)
            {
                PushForm(s, resources);
            }
        }
    }

    private void Record(int font, ReadOnlySpan<byte> codes)
    {
        if (font < 0 || !_targets.Contains(font))
        {
            return;
        }

        if (!_used.TryGetValue(font, out HashSet<int>? set))
        {
            _used[font] = set = [];
        }

        foreach (byte c in codes)
        {
            set.Add(c);
        }
    }
}
