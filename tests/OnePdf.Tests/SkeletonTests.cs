using OnePdf.Fonts;

namespace OnePdf.Tests;

public class SkeletonTests
{
    [Fact]
    public void LibraryAndFontsPackageAreReachable()
    {
        Assert.Equal("OnePDF", Library.Name);
        Assert.Equal("OnePdf.Fonts", FontsPackage.Name);
    }
}
