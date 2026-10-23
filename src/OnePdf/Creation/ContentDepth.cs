namespace OnePdf;

/// <summary>
/// Counts the graphics states a content stream leaves open (<c>q</c> without <c>Q</c>), so that content added after it can be
/// preceded by enough <c>Q</c> operators to return to the page's initial state. A best-effort lexical scan, not an interpreter:
/// strings, hex strings, names, comments and inline image data are skipped. A <c>Q</c> with nothing to close is ignored, as readers do.
/// </summary>
internal static class ContentDepth
{
    private const int MaxDepth = 4096;

    public static int Unclosed(ReadOnlySpan<byte> d, int depth)
    {
        int i = 0;
        while (i < d.Length)
        {
            byte b = d[i];
            if (b is 0 or 9 or 10 or 12 or 13 or 32)
            {
                i++;
            }
            else if (b == '%')
            {
                while (i < d.Length && d[i] is not (10 or 13))
                {
                    i++;
                }
            }
            else if (b == '(')
            {
                int nest = 0;
                for (; i < d.Length; i++)
                {
                    if (d[i] == '\\')
                    {
                        i++;
                    }
                    else if (d[i] == '(')
                    {
                        nest++;
                    }
                    else if (d[i] == ')' && --nest == 0)
                    {
                        i++;
                        break;
                    }
                }
            }
            else if (b == '<')
            {
                if (i + 1 < d.Length && d[i + 1] == '<')
                {
                    i += 2;
                }
                else
                {
                    while (i < d.Length && d[i] != '>')
                    {
                        i++;
                    }

                    i++;
                }
            }
            else if (b is (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)')')
            {
                i++;
            }
            else
            {
                int start = i;
                i++;
                while (i < d.Length && !IsDelimiterOrWhite(d[i]))
                {
                    i++;
                }

                ReadOnlySpan<byte> word = d[start..i];
                if (b == '/')
                {
                    continue;
                }

                if (word.SequenceEqual("q"u8))
                {
                    depth = Math.Min(depth + 1, MaxDepth);
                }
                else if (word.SequenceEqual("Q"u8))
                {
                    depth = Math.Max(depth - 1, 0);
                }
                else if (word.SequenceEqual("BI"u8))
                {
                    i = SkipInlineImage(d, i);
                }
            }
        }

        return depth;
    }

    private static bool IsDelimiterOrWhite(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32 or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    // After BI: find the ID keyword, then the next EI that stands alone between white space.
    private static int SkipInlineImage(ReadOnlySpan<byte> d, int i)
    {
        int id = -1;
        for (int p = i; p + 2 < d.Length; p++)
        {
            if (d[p] == 'I' && d[p + 1] == 'D' && (p == 0 || d[p - 1] is 0 or 9 or 10 or 12 or 13 or 32) && d[p + 2] is 0 or 9 or 10 or 12 or 13 or 32)
            {
                id = p + 3;
                break;
            }
        }

        if (id < 0)
        {
            return d.Length;
        }

        for (int p = id; p + 2 <= d.Length; p++)
        {
            if (d[p] == 'E' && d[p + 1] == 'I' && (p == 0 || d[p - 1] is 0 or 9 or 10 or 12 or 13 or 32) && (p + 2 == d.Length || d[p + 2] is 0 or 9 or 10 or 12 or 13 or 32))
            {
                return p + 2;
            }
        }

        return d.Length;
    }
}
