using System.Globalization;
using System.Text;
using OnePdf.Content;

namespace OnePdf.Structure;

/// <summary>What to do with one marked-content sequence.</summary>
internal enum McAction : byte
{
    Keep,

    /// <summary>Drop the <c>BDC</c> and its <c>EMC</c>; the content between them stays.</summary>
    Drop,

    /// <summary>Make the <c>BDC</c> a <c>BMC</c> with the same tag.</summary>
    ToBmc,

    /// <summary>Keep a <c>BDC</c> with only the <c>/ActualText</c> of the property list (text extraction reads it from the content stream).</summary>
    KeepActualText,

    /// <summary>Make the sequence an <c>/Artifact</c> <c>BMC</c>.</summary>
    ToArtifact,
}

/// <summary>
/// Reads a content stream with the content lexer and edits the marked-content operators in place, leaving every other byte as it is.
/// The decision for each sequence comes from a delegate that sees the tag, the resolved property list and the <c>/MCID</c> (or -1).
/// </summary>
internal sealed class MarkedContentRewriter
{
    private static readonly byte[] s_space = [(byte)' '];

    private readonly PdfLimits _limits;

    public MarkedContentRewriter(PdfLimits limits) => _limits = limits;

    private enum Close : byte
    {
        Keep,
        Drop,
    }

    private readonly record struct Edit(int Start, int End, byte[] Bytes);

    /// <summary>
    /// Returns the rewritten bytes, or null when nothing changes. <paramref name="complete"/> is false when the stream could not be read to
    /// its end (an inline image without an end), in which case the result is null.
    /// </summary>
    public byte[]? Rewrite(ReadOnlyMemory<byte> data, PdfDictionary? resources, Func<string, PdfDictionary?, int, McAction> decide, out bool complete)
    {
        complete = true;
        var lx = new ContentLexer(data, _limits.MaxNestingDepth);
        var edits = new List<Edit>();
        var stack = new List<Close>();
        Operand a = default;
        Operand b = default;
        int operands = 0;
        int segment = 0;
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
                    Begin(tag, null, -1, segment, end, edits, stack, decide);
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

                    int mcid = props?.Get("MCID") is PdfInteger { Value: >= 0 and <= int.MaxValue } id ? (int)id.Value : -1;
                    Begin(tag, props, mcid, segment, end, edits, stack, decide);
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
                        complete = false;
                        return null;
                    }

                    lx.ReadInlineImageData(-1, out bool found);
                    if (!found)
                    {
                        complete = false;
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

    private static void Begin(string? tag, PdfDictionary? props, int mcid, int segment, int end, List<Edit> edits, List<Close> stack, Func<string, PdfDictionary?, int, McAction> decide)
    {
        McAction action = tag is null ? McAction.Keep : decide(tag, props, mcid);
        switch (action)
        {
            case McAction.Drop:
                edits.Add(new Edit(segment, end, s_space));
                stack.Add(Close.Drop);
                return;
            case McAction.ToBmc:
                edits.Add(new Edit(segment, end, Encoding.Latin1.GetBytes(" " + NameText(tag!) + " BMC")));
                break;
            case McAction.ToArtifact:
                edits.Add(new Edit(segment, end, Encoding.Latin1.GetBytes(" /Artifact BMC")));
                break;
            case McAction.KeepActualText:
                if (props?.Get("ActualText") is PdfString at)
                {
                    edits.Add(new Edit(segment, end, Encoding.Latin1.GetBytes(" " + NameText(tag!) + " <</ActualText " + HexText(at) + ">> BDC")));
                }

                break;
        }

        stack.Add(Close.Keep);
    }

    internal static string NameText(string name)
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
