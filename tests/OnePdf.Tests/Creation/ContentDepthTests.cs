using System.Text;

namespace OnePdf.Tests.Creation;

public class ContentDepthTests
{
    private static int Open(string content) => ContentDepth.Unclosed(Encoding.Latin1.GetBytes(content), 0);

    [Fact]
    public void PlainOperatorsAreCounted()
    {
        Assert.Equal(1, Open("q q 1 0 0 1 0 0 cm Q"));
        Assert.Equal(0, Open("q Q"));
    }

    [Fact]
    public void OperatorsInStringsCommentsNamesAndDictionariesAreNotCounted()
    {
        Assert.Equal(1, Open("q (Q Q Q) Tj <51 51> Tj % Q Q\n/Q gs /P <</K (Q) /N /q>> BDC EMC"));
    }

    [Fact]
    public void RestoreWithNothingToCloseIsIgnored()
    {
        Assert.Equal(1, Open("Q Q q"));
    }

    [Fact]
    public void InlineImageDataThatLooksLikeOperatorsIsSkippedByItsLength()
    {
        // 4x1 RGB at 8 bits is 12 bytes of data; they contain " EI " and "q" words. The data length from the dictionary decides.
        string data = " EI q q q q "; // 12 bytes
        Assert.Equal(12, data.Length);
        Assert.Equal(1, Open("q BI /W 4 /H 1 /BPC 8 /CS /RGB ID " + data + "\nEI\n"));
    }

    [Fact]
    public void TruncatedInlineImageAndUnterminatedStringDoNotThrow()
    {
        Assert.Equal(1, Open("q BI /W 4 /H"));
        Assert.Equal(1, Open("q (abc q q"));
        Assert.Equal(1, Open("q BI /W 1 /H 1 /BPC 8 /CS /G ID \u0001"));
    }
}
