using System.Text;
using OnePdf.Content;

namespace OnePdf.Tests.Content;

public class ContentLexerTests
{
    private static List<string> Tokens(string s)
    {
        var lx = new ContentLexer(Encoding.Latin1.GetBytes(s), 256);
        var r = new List<string>();
        while (true)
        {
            ContentToken t = lx.Next(out Operand o, out ContentOp op);
            if (t == ContentToken.End)
            {
                return r;
            }

            if (t == ContentToken.Operator)
            {
                r.Add(op == ContentOp.Unknown ? "?" + lx.LastKeyword : op.ToString());
                lx.ResetItems();
                continue;
            }

            r.Add(o.Kind switch
            {
                OperandKind.Number => o.IsInteger ? "i" + o.Num.ToString(System.Globalization.CultureInfo.InvariantCulture) : "r" + o.Num.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OperandKind.Name => "/" + o.Name,
                OperandKind.String => "(" + Encoding.Latin1.GetString(o.Str.Span) + ")",
                OperandKind.Array => "[" + o.ArrayCount + "]",
                OperandKind.Bool => o.Bool ? "true" : "false",
                OperandKind.Object => "obj",
                _ => "null",
            });
        }
    }

    [Theory]
    [InlineData("12", "i12")]
    [InlineData("-3.5", "r-3.5")]
    [InlineData("+.5", "r0.5")]
    [InlineData("--5", "i-5")]
    [InlineData("4-5", "i4")]
    [InlineData("1.2.3", "r1.2")]
    [InlineData("-", "i0")]
    [InlineData(".", "r0")]
    public void NumbersAreReadLeniently(string text, string expected) => Assert.Equal([expected], Tokens(text));

    [Fact]
    public void StringsNamesAndOperators()
    {
        Assert.Equal(["/F#1", "i12", "TextFont"], Tokens("/F#231 12 Tf"));
        Assert.Equal(["(a(b)c)", "ShowText"], Tokens("(a(b)c) Tj"));
        Assert.Equal(["(a\nb\u0001 )", "ShowText"], Tokens("(a\\nb\\001\\\r\n )Tj"));
        Assert.Equal(["(Ab\u0010)", "ShowText"], Tokens("<41 62 1>Tj"));
        Assert.Equal(["CloseFillStrokeEo", "FillStrokeEo", "ClipEo", "NextLine"], Tokens("b* B* W* T*"));
        Assert.Equal(["?foo", "q"], Tokens("foo q"));
        Assert.Equal(["i1", "NextLineShow"], Tokens("1 '"));
    }

    [Fact]
    public void CommentsAndStrayDelimitersAreSkipped() =>
        Assert.Equal(["i1", "i2", "Move"], Tokens("1 % ignore this ) ]\n ] 2 m"));

    [Fact]
    public void ArraysKeepSimpleItemsWithoutObjects()
    {
        var lx = new ContentLexer("[(A) -120 (B) /N true]TJ"u8.ToArray(), 256);
        Assert.Equal(ContentToken.Operand, lx.Next(out Operand arr, out _));
        Assert.Equal(OperandKind.Array, arr.Kind);
        Assert.Equal(5, arr.ArrayCount);
        Assert.Equal(-120, lx.Items(in arr)[1].Num);
        Assert.Equal(ContentToken.Operator, lx.Next(out _, out ContentOp op));
        Assert.Equal(ContentOp.ShowTextArray, op);
    }

    [Fact]
    public void NestedStructuresAndUnterminatedOnesAreBounded()
    {
        // Deep nesting beyond the limit is dropped, not followed.
        string deep = new string('[', 100_000) + "1" + new string(']', 100_000) + " m";
        var lx = new ContentLexer(Encoding.ASCII.GetBytes(deep), 32);
        Assert.Equal(ContentToken.Operand, lx.Next(out Operand o, out _));
        Assert.Equal(OperandKind.Object, o.Kind);
        Assert.Equal(ContentToken.Operator, lx.Next(out _, out ContentOp op));
        Assert.Equal(ContentOp.Move, op);

        // An unterminated dictionary closes at the next operator.
        var lx2 = new ContentLexer("<< /A 1 /B [2 3 q"u8.ToArray(), 32);
        Assert.Equal(ContentToken.Operand, lx2.Next(out Operand d, out _));
        Assert.IsType<PdfDictionary>(d.Obj);
        Assert.Equal(ContentToken.Operator, lx2.Next(out _, out op));
        Assert.Equal(ContentOp.Save, op);
    }

    [Fact]
    public void InlineImageEndIsFoundByLengthAndByScanning()
    {
        // Known length: the data contains "EI" bytes that must not end the image.
        byte[] data = Encoding.Latin1.GetBytes("BI /W 2 /H 2 /BPC 8 /CS /G ID \u0001EI  EI Q");
        var lx = new ContentLexer(data, 32);
        Assert.Equal(ContentToken.Operator, lx.Next(out _, out ContentOp bi));
        Assert.Equal(ContentOp.BeginInline, bi);
        PdfDictionary d = lx.ReadInlineImageDictionary()!;
        Assert.Equal("DeviceGray", d.GetName("ColorSpace"));
        Assert.Equal(2, d.GetInt("Width"));
        ReadOnlyMemory<byte> img = lx.ReadInlineImageData(4, out bool found);
        Assert.True(found);
        Assert.Equal(4, img.Length);
        Assert.Equal(ContentToken.Operator, lx.Next(out _, out ContentOp next));
        Assert.Equal(ContentOp.Save, next);

        // Unknown length (filtered): scan for a standalone EI followed by content.
        byte[] filtered = Encoding.Latin1.GetBytes("BI /W 1 /H 1 /F /AHx ID 00>EIx\nEI Q");
        var lx2 = new ContentLexer(filtered, 32);
        lx2.Next(out _, out _);
        PdfDictionary d2 = lx2.ReadInlineImageDictionary()!;
        Assert.Equal("ASCIIHexDecode", d2.GetName("Filter"));
        ReadOnlyMemory<byte> img2 = lx2.ReadInlineImageData(-1, out found);
        Assert.True(found);
        Assert.Equal("00>EIx", Encoding.Latin1.GetString(img2.Span));
    }

    [Fact]
    public void InlineImageWithoutEiRunsToTheEnd()
    {
        var lx = new ContentLexer("BI /W 1 /H 1 /F /Fl ID \u0001\u0002\u0003"u8.ToArray(), 32);
        lx.Next(out _, out _);
        Assert.NotNull(lx.ReadInlineImageDictionary());
        ReadOnlyMemory<byte> img = lx.ReadInlineImageData(-1, out bool found);
        Assert.False(found);
        Assert.Equal(3, img.Length);
        Assert.True(lx.AtEnd);
    }
}
