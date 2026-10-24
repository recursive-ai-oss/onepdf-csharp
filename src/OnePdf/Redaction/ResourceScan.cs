using OnePdf.Content;

namespace OnePdf.Redaction;

/// <summary>Finds which named resources a content stream refers to, by reading its operators the way the interpreter does.</summary>
internal static class ResourceScan
{
    /// <summary>
    /// Adds the (category, name) pairs that fonts, XObjects, graphics states, shadings and patterns are selected by in
    /// <paramref name="data"/>. Returns false when the stream cannot be read to its end with certainty (an inline image whose end
    /// is not found), in which case the answer must not be used to drop anything.
    /// </summary>
    public static bool Collect(ReadOnlyMemory<byte> data, int maxNesting, HashSet<(string, string)> used)
    {
        var lx = new ContentLexer(data, maxNesting);
        string? name = null;
        while (true)
        {
            ContentToken t = lx.Next(out Operand operand, out ContentOp op);
            if (t == ContentToken.End)
            {
                return true;
            }

            if (t == ContentToken.Operand)
            {
                if (operand.Kind == OperandKind.Name)
                {
                    name = operand.Name;
                }

                continue;
            }

            string? category = op switch
            {
                ContentOp.TextFont => "Font",
                ContentOp.Do => "XObject",
                ContentOp.SetExtGState => "ExtGState",
                ContentOp.Shading => "Shading",
                ContentOp.FillColor or ContentOp.FillColorN or ContentOp.StrokeColor or ContentOp.StrokeColorN => "Pattern",
                _ => null,
            };
            if (category is not null && name is not null)
            {
                used.Add((category, name));
            }

            if (op == ContentOp.BeginInline)
            {
                if (lx.ReadInlineImageDictionary() is null)
                {
                    return false;
                }

                lx.ReadInlineImageData(-1, out bool found);
                if (!found)
                {
                    return false;
                }
            }

            name = null;
            lx.ResetItems();
        }
    }
}
