using OnePdf.Content;

namespace OnePdf;

/// <summary>
/// Counts the graphics states a content stream leaves open (<c>q</c> without <c>Q</c>), so that content added after it can be
/// preceded by enough <c>Q</c> operators to return to the page's initial state. A best-effort lexical scan, not an interpreter:
/// strings, hex strings, names, comments and inline image data are skipped. A <c>Q</c> with nothing to close is ignored, as readers do.
/// </summary>
internal static class ContentDepth
{
    private const int MaxDepth = 4096;

    public static int Unclosed(ReadOnlyMemory<byte> data, int depth)
    {
        var lx = new ContentLexer(data, 64);
        int guard = 0;
        while (true)
        {
            if (++guard > (1 << 24))
            {
                break;
            }

            lx.ResetItems();
            ContentToken t = lx.Next(out _, out ContentOp op);
            if (t == ContentToken.End)
            {
                break;
            }

            if (t != ContentToken.Operator)
            {
                continue;
            }

            switch (op)
            {
                case ContentOp.Save:
                    depth = Math.Min(depth + 1, MaxDepth);
                    break;
                case ContentOp.Restore:
                    // A Q with nothing to close is ignored (ISO 32000-2 8.4.2 forbids it; readers skip it).
                    depth = Math.Max(depth - 1, 0);
                    break;
                case ContentOp.BeginInline:
                    PdfDictionary? d = lx.ReadInlineImageDictionary();
                    if (d is null)
                    {
                        return depth;
                    }

                    _ = lx.ReadInlineImageData(DataLength(d), out _);
                    break;
                default:
                    break;
            }
        }

        return depth;
    }

    // Byte length of unfiltered inline data when the colour space is a device space or Indexed (ISO 32000-2 8.9.7); -1 lets the lexer scan for EI.
    private static long DataLength(PdfDictionary d)
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

        long n, bpc;
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

            PdfObject? cs = d.Get("ColorSpace");
            n = cs switch
            {
                PdfName { Value: "DeviceGray" or "CalGray" } => 1,
                PdfName { Value: "DeviceRGB" or "CalRGB" } => 3,
                PdfName { Value: "DeviceCMYK" } => 4,
                PdfArray { Count: > 0 } a when a[0] is PdfName { Value: "Indexed" } => 1,
                _ => -1,
            };
            if (n < 0)
            {
                return -1;
            }
        }

        return (((w * n * bpc) + 7) / 8) * h;
    }
}
